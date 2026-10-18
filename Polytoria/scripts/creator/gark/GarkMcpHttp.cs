// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Polytoria.Creator.Gark;

/// <summary>
/// Loopback-only HTTP/1.1 server for MCP: Streamable HTTP on /mcp, plus the older HTTP+SSE
/// transport on /sse and /messages for clients that still use it. Requests from web pages are
/// refused (Origin, Host and Content-Type checks) so only local programs can connect.
/// </summary>
internal sealed class GarkMcpHttpServer : IDisposable
{
	private readonly GarkMcpProtocol _protocol;
	private readonly List<TcpListener> _listeners = [];
	private readonly CancellationTokenSource _stop = new();
	private readonly ConcurrentDictionary<string, GarkMcpSession> _sessions = new();
	private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
	private const int MaxBody = 64 * 1024 * 1024;

	internal int Port { get; }
	internal string Url => $"http://127.0.0.1:{Port}/mcp";
	internal IReadOnlyCollection<GarkMcpSession> Sessions => _sessions.Values.ToArray();
	/// <summary>Raised on a background thread whenever a session starts, ends or makes a request.</summary>
	internal event Action? SessionsChanged;

	internal GarkMcpHttpServer(GarkMcpProtocol protocol, int port)
	{
		_protocol = protocol;
		var ipv4 = new TcpListener(IPAddress.Loopback, port);
		ipv4.Server.ExclusiveAddressUse = OperatingSystem.IsWindows();
		ipv4.Start();
		_listeners.Add(ipv4);
		Port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
		try
		{
			// "localhost" may resolve to ::1 first; listen there too when available.
			var ipv6 = new TcpListener(IPAddress.IPv6Loopback, Port);
			ipv6.Start();
			_listeners.Add(ipv6);
		}
		catch (SocketException) { }
		foreach (var listener in _listeners) _ = AcceptLoop(listener);
	}

	internal void Prune(TimeSpan idle)
	{
		foreach (var session in _sessions.Values)
			if (session.Streams.IsEmpty && session.Running.IsEmpty && DateTime.UtcNow - session.LastSeen > idle && _sessions.TryRemove(session.Id, out _))
				SessionsChanged?.Invoke();
	}

	internal void NotifyToolsChanged()
	{
		foreach (var session in _sessions.Values) session.Push(GarkMcpProtocol.Notification("notifications/tools/list_changed"));
	}

	public void Dispose()
	{
		_stop.Cancel();
		foreach (var listener in _listeners) listener.Stop();
		foreach (var session in _sessions.Values) { session.CancelAll(); foreach (var stream in session.Streams.Values) stream.Writer.TryComplete(); }
		foreach (var client in _clients.Keys) client.Dispose();
		_sessions.Clear();
	}

	private async Task AcceptLoop(TcpListener listener)
	{
		while (!_stop.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
			catch (Exception) { if (_stop.IsCancellationRequested) return; continue; }
			_ = HandleConnection(client);
		}
	}

	private async Task HandleConnection(TcpClient client)
	{
		_clients[client] = 0;
		try
		{
			client.NoDelay = true;
			var stream = client.GetStream();
			var reader = new HttpReader(stream);
			while (!_stop.IsCancellationRequested)
			{
				HttpRequest? request;
				try { request = await reader.ReadRequest(_stop.Token); }
				catch (HttpError error) { await WriteResponse(stream, error.Status, error.Message, "text/plain", close: true); break; }
				if (request == null) break;
				if (request.Header("expect")?.Equals("100-continue", StringComparison.OrdinalIgnoreCase) == true && !request.BodyRead)
					await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), _stop.Token);
				try { await reader.ReadBody(request, MaxBody, _stop.Token); }
				catch (HttpError error) { await WriteResponse(stream, error.Status, error.Message, "text/plain", close: true); break; }
				bool keepAlive = await Route(request, stream, reader);
				if (!keepAlive || request.Header("connection")?.Contains("close", StringComparison.OrdinalIgnoreCase) == true) break;
			}
		}
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
		finally
		{
			_clients.TryRemove(client, out _);
			client.Dispose();
		}
	}

	private static bool IsLocalOrigin(string origin)
	{
		if (origin == "null") return false;
		if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
		return uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
	}
	private bool IsLocalHost(string? host)
	{
		if (host == null) return true; // HTTP/1.0 clients may omit Host; browsers never do.
		string name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
		return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "[::1]";
	}

	/// <summary>Returns whether the connection can be reused.</summary>
	private async Task<bool> Route(HttpRequest request, NetworkStream stream, HttpReader reader)
	{
		// DNS rebinding and cross-site requests from browsers are refused outright.
		if (!IsLocalHost(request.Header("host")))
			return await WriteResponse(stream, 403, "Forbidden: unexpected Host header.", "text/plain");
		if (request.Header("origin") is { } origin && !IsLocalOrigin(origin))
			return await WriteResponse(stream, 403, "Forbidden: browser requests are not allowed.", "text/plain");

		switch (request.Method, request.Path)
		{
			case ("POST", "/mcp"): return await StreamablePost(request, stream, reader);
			case ("GET", "/mcp"): return await StreamableGet(request, stream, reader);
			case ("DELETE", "/mcp"):
				if (request.Header("mcp-session-id") is { } id && _sessions.TryRemove(id, out var ended))
				{
					ended.CancelAll();
					foreach (var s in ended.Streams.Values) s.Writer.TryComplete();
					SessionsChanged?.Invoke();
				}
				return await WriteResponse(stream, 200, "", "text/plain");
			case ("GET", "/sse"): return await LegacyStream(request, stream, reader);
			case ("POST", "/messages"): return await LegacyPost(request, stream);
			case ("GET", "/"): case ("GET", "/health"):
				return await WriteJson(stream, 200, new JsonObject
				{
					["name"] = "Polytoria Creator MCP server (Gark)", ["version"] = GarkMcpProtocol.Version,
					["endpoint"] = "/mcp", ["legacy_sse_endpoint"] = "/sse", ["sessions"] = _sessions.Count
				});
			case ("OPTIONS", _):
				return await WriteResponse(stream, 204, "", "text/plain");
			case (_, "/mcp"):
				return await WriteResponse(stream, 405, "Method not allowed.", "text/plain", extra: "Allow: GET, POST, DELETE\r\n");
			default:
				return await WriteResponse(stream, 404, "Not found. The MCP endpoint is /mcp.", "text/plain");
		}
	}

	private GarkMcpSession SessionFor(HttpRequest request)
	{
		string? id = request.Header("mcp-session-id");
		if (id != null && _sessions.TryGetValue(id, out var existing)) return existing;
		// Unknown IDs usually mean Creator restarted. Adopt them instead of failing, so agents keep working.
		var session = new GarkMcpSession(id is { Length: > 0 and <= 128 } && id.All(c => c is >= '!' and <= '~') ? id : Guid.NewGuid().ToString("N"), "streamable-http");
		if (request.Header("mcp-protocol-version") is { } version) session.ProtocolVersion = GarkMcpProtocol.Negotiate(version);
		session = _sessions.GetOrAdd(session.Id, session);
		SessionsChanged?.Invoke();
		return session;
	}

	private async Task<bool> StreamablePost(HttpRequest request, NetworkStream stream, HttpReader reader)
	{
		if (request.Header("content-type") is not { } type || !type.Contains("json", StringComparison.OrdinalIgnoreCase))
			return await WriteResponse(stream, 415, "Content-Type must be application/json.", "text/plain");
		JsonNode? body;
		try { body = JsonNode.Parse(request.Body); }
		catch (JsonException) { return await WriteJson(stream, 400, GarkMcpProtocol.Error(null, -32700, "Parse error")); }
		var messages = body switch
		{
			JsonObject single => [single],
			JsonArray batch => batch.OfType<JsonObject>().ToList(),
			_ => null
		};
		if (messages == null || messages.Count == 0) return await WriteJson(stream, 400, GarkMcpProtocol.Error(null, -32600, "Invalid request"));

		GarkMcpSession session;
		bool initialize = messages.Count == 1 && messages[0]["method"]?.ToString() == "initialize";
		if (initialize)
		{
			session = new GarkMcpSession(Guid.NewGuid().ToString("N"), "streamable-http");
			_sessions[session.Id] = session;
		}
		else session = SessionFor(request);
		string sessionHeader = $"Mcp-Session-Id: {session.Id}\r\n";

		using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
		_ = reader.WatchForDisconnect().ContinueWith(t => { if (t.IsFaulted || t.Result == 0) TryCancel(connection); }, TaskScheduler.Default);
		var notifications = Channel.CreateUnbounded<JsonObject>();
		var work = Task.WhenAll(messages.Select(m => _protocol.HandleAsync(session, m, n => notifications.Writer.TryWrite(n), connection.Token)));
		_ = work.ContinueWith(_ => notifications.Writer.TryComplete(), TaskScheduler.Default);
		SessionsChanged?.Invoke();

		if (!messages.Any(GarkMcpProtocol.IsRequest))
		{
			await work;
			return await WriteResponse(stream, 202, "", "text/plain", extra: sessionHeader);
		}

		// Quick calls get a plain JSON reply. Slow ones switch to an event stream when the client accepts it,
		// which carries progress notifications and keeps the connection alive.
		bool sse = messages.Count == 1 && request.Header("accept")?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
		if (sse && await Task.WhenAny(work, Task.Delay(1500, connection.Token).ContinueWith(_ => { }, TaskScheduler.Default)) != work)
		{
			var writer = new ChunkedWriter(stream);
			try
			{
				await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache, no-transform\r\n"
					+ sessionHeader + "Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n"), connection.Token);
				while (!work.IsCompleted)
				{
					var next = notifications.Reader.WaitToReadAsync(connection.Token).AsTask();
					if (await Task.WhenAny(next, Task.Delay(15000, connection.Token).ContinueWith(_ => { }, TaskScheduler.Default)) != next)
					{
						await writer.Write(": keep-alive\n\n", connection.Token);
						continue;
					}
					while (notifications.Reader.TryRead(out var notification)) await writer.Write(Event(notification), connection.Token);
					if (!await next) break;
				}
				var responses = await work;
				while (notifications.Reader.TryRead(out var notification)) await writer.Write(Event(notification), connection.Token);
				foreach (var response in responses.Where(r => r != null)) await writer.Write(Event(response!), connection.Token);
				await writer.End(connection.Token);
				return true;
			}
			catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
			{
				connection.Cancel(); // The client went away: stop the tool.
				return false;
			}
		}

		JsonObject?[] results;
		try { results = await work; }
		catch (OperationCanceledException) { return false; }
		var replies = results.Where(r => r != null).Select(r => (JsonNode)r!).ToArray();
		JsonNode payload = body is JsonArray ? new JsonArray(replies) : replies.Length > 0 ? replies[0] : new JsonObject();
		return await WriteJson(stream, 200, payload, sessionHeader);
	}

	/// <summary>Server-to-client stream for list-changed notifications.</summary>
	private async Task<bool> StreamableGet(HttpRequest request, NetworkStream stream, HttpReader reader)
	{
		if (request.Header("accept")?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) != true)
			return await WriteResponse(stream, 406, "Accept must include text/event-stream.", "text/plain");
		var session = SessionFor(request);
		await RunStream(session, stream, reader, $"Mcp-Session-Id: {session.Id}\r\n", null);
		return false;
	}

	private async Task<bool> LegacyStream(HttpRequest request, NetworkStream stream, HttpReader reader)
	{
		var session = new GarkMcpSession(Guid.NewGuid().ToString("N"), "sse");
		_sessions[session.Id] = session;
		SessionsChanged?.Invoke();
		try { await RunStream(session, stream, reader, "", $"event: endpoint\ndata: /messages?sessionId={session.Id}\n\n"); }
		finally
		{
			// The legacy transport ends the session with its stream.
			if (_sessions.TryRemove(session.Id, out _)) session.CancelAll();
			SessionsChanged?.Invoke();
		}
		return false;
	}

	private async Task RunStream(GarkMcpSession session, NetworkStream stream, HttpReader reader, string headers, string? first)
	{
		var channel = Channel.CreateUnbounded<JsonObject>();
		var key = Guid.NewGuid();
		session.Streams[key] = channel;
		using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
		_ = reader.WatchForDisconnect().ContinueWith(_ => TryCancel(connection), TaskScheduler.Default);
		var writer = new ChunkedWriter(stream);
		try
		{
			await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache, no-transform\r\n"
				+ headers + "Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n"), connection.Token);
			if (first != null) await writer.Write(first, connection.Token);
			while (!connection.IsCancellationRequested)
			{
				var next = channel.Reader.WaitToReadAsync(connection.Token).AsTask();
				if (await Task.WhenAny(next, Task.Delay(15000, connection.Token).ContinueWith(_ => { }, TaskScheduler.Default)) != next)
				{
					await writer.Write(": keep-alive\n\n", connection.Token);
					continue;
				}
				if (!await next) break;
				while (channel.Reader.TryRead(out var message)) await writer.Write(Event(message), connection.Token);
			}
		}
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
		finally { session.Streams.TryRemove(key, out _); }
	}

	private async Task<bool> LegacyPost(HttpRequest request, NetworkStream stream)
	{
		string? id = request.Query.GetValueOrDefault("sessionId") ?? request.Query.GetValueOrDefault("session_id");
		if (id == null || !_sessions.TryGetValue(id, out var session) || session.Transport != "sse")
			return await WriteResponse(stream, 404, "Unknown session. Reconnect to /sse.", "text/plain");
		JsonNode? body;
		try { body = JsonNode.Parse(request.Body); }
		catch (JsonException) { return await WriteResponse(stream, 400, "Invalid JSON.", "text/plain"); }
		var messages = body switch { JsonObject single => [single], JsonArray batch => batch.OfType<JsonObject>().ToList(), _ => [] };
		foreach (var message in messages)
		{
			_ = Task.Run(async () =>
			{
				var response = await _protocol.HandleAsync(session, message, session.Push, _stop.Token);
				if (response != null) session.Push(response);
			});
		}
		SessionsChanged?.Invoke();
		return await WriteResponse(stream, 202, "Accepted", "text/plain");
	}

	private static void TryCancel(CancellationTokenSource source)
	{
		try { source.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private static string Event(JsonObject message) => "event: message\ndata: " + message.ToJsonString() + "\n\n";

	private static Task<bool> WriteJson(NetworkStream stream, int status, JsonNode payload, string extra = "") =>
		WriteResponse(stream, status, payload.ToJsonString(), "application/json", extra: extra);

	private static async Task<bool> WriteResponse(NetworkStream stream, int status, string body, string contentType, string extra = "", bool close = false)
	{
		byte[] content = Encoding.UTF8.GetBytes(body);
		string reason = status switch
		{
			200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
			405 => "Method Not Allowed", 406 => "Not Acceptable", 413 => "Payload Too Large", 415 => "Unsupported Media Type",
			431 => "Request Header Fields Too Large", _ => "Error"
		};
		string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\n"
			+ extra + (close ? "Connection: close\r\n" : "Connection: keep-alive\r\n") + "\r\n";
		try
		{
			await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
			if (content.Length > 0) await stream.WriteAsync(content);
			return !close;
		}
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { return false; }
	}

	private sealed class ChunkedWriter(NetworkStream stream)
	{
		internal async Task Write(string text, CancellationToken token)
		{
			byte[] data = Encoding.UTF8.GetBytes(text);
			await stream.WriteAsync(Encoding.ASCII.GetBytes(data.Length.ToString("X") + "\r\n"), token);
			await stream.WriteAsync(data, token);
			await stream.WriteAsync("\r\n"u8.ToArray(), token);
			await stream.FlushAsync(token);
		}
		internal async Task End(CancellationToken token) => await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), token);
	}

	private sealed class HttpError(int status, string message) : Exception(message)
	{
		internal int Status { get; } = status;
	}

	private sealed class HttpRequest
	{
		internal string Method = "", Path = "";
		internal Dictionary<string, string> Query = [];
		internal Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
		internal string Body = "";
		internal bool BodyRead;
		internal string? Header(string name) => Headers.GetValueOrDefault(name);
	}

	/// <summary>Buffered request parser. A read can stay pending between requests to notice when the client disconnects.</summary>
	private sealed class HttpReader(NetworkStream stream)
	{
		private byte[] _buffer = new byte[16384];
		private int _start, _end;
		private Task<int>? _pending;

		private async Task<bool> Fill(CancellationToken token)
		{
			if (_pending == null)
			{
				if (_start > 0 && _start == _end) _start = _end = 0;
				if (_end == _buffer.Length)
				{
					if (_start > 0) { Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start); _end -= _start; _start = 0; }
					else Array.Resize(ref _buffer, _buffer.Length * 2);
				}
				_pending = stream.ReadAsync(_buffer, _end, _buffer.Length - _end, token);
			}
			int read;
			try { read = await _pending.WaitAsync(token); }
			finally { if (_pending.IsCompleted) _pending = null; }
			if (read == 0) return false;
			_end += read;
			return true;
		}

		/// <summary>Completes with 0 when the client closes the connection while a response is pending.</summary>
		internal Task<int> WatchForDisconnect()
		{
			if (_pending == null)
			{
				if (_end == _buffer.Length)
				{
					if (_start > 0) { Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start); _end -= _start; _start = 0; }
					else Array.Resize(ref _buffer, _buffer.Length * 2);
				}
				_pending = stream.ReadAsync(_buffer, _end, _buffer.Length - _end);
			}
			return _pending;
		}

		internal async Task<HttpRequest?> ReadRequest(CancellationToken token)
		{
			int headerEnd;
			while ((headerEnd = IndexOf("\r\n\r\n"u8)) < 0)
			{
				if (_end - _start > 65536) throw new HttpError(431, "Headers too large.");
				if (!await Fill(token)) return null;
			}
			string head = Encoding.ASCII.GetString(_buffer, _start, headerEnd - _start);
			_start = headerEnd + 4;
			var lines = head.Split("\r\n");
			var parts = lines[0].Split(' ');
			if (parts.Length < 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new HttpError(400, "Malformed request line.");
			var request = new HttpRequest { Method = parts[0].ToUpperInvariant() };
			string target = parts[1];
			int question = target.IndexOf('?');
			request.Path = (question >= 0 ? target[..question] : target).TrimEnd('/');
			if (request.Path == "") request.Path = "/";
			if (question >= 0)
				foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
				{
					int equals = pair.IndexOf('=');
					request.Query[Uri.UnescapeDataString(equals >= 0 ? pair[..equals] : pair)] = equals >= 0 ? Uri.UnescapeDataString(pair[(equals + 1)..]) : "";
				}
			foreach (var line in lines.Skip(1))
			{
				int colon = line.IndexOf(':');
				if (colon <= 0) continue;
				string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
				request.Headers[name] = request.Headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
			}
			return request;
		}

		internal async Task ReadBody(HttpRequest request, int max, CancellationToken token)
		{
			if (request.BodyRead) return;
			request.BodyRead = true;
			using var body = new MemoryStream();
			if (request.Header("transfer-encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
			{
				while (true)
				{
					int lineEnd;
					while ((lineEnd = IndexOf("\r\n"u8)) < 0) if (!await Fill(token)) throw new IOException("Connection closed.");
					string sizeText = Encoding.ASCII.GetString(_buffer, _start, lineEnd - _start).Split(';')[0].Trim();
					_start = lineEnd + 2;
					int size = Convert.ToInt32(sizeText, 16);
					if (size == 0)
					{
						// Skip trailers up to the final blank line.
						while (true)
						{
							while ((lineEnd = IndexOf("\r\n"u8)) < 0) if (!await Fill(token)) throw new IOException("Connection closed.");
							bool blank = lineEnd == _start;
							_start = lineEnd + 2;
							if (blank) break;
						}
						break;
					}
					if (body.Length + size > max) throw new HttpError(413, "Request body too large.");
					await Copy(body, size + 2, token);
					body.SetLength(body.Length - 2);
				}
			}
			else if (request.Header("content-length") is { } lengthText)
			{
				if (!long.TryParse(lengthText, out long length) || length < 0) throw new HttpError(400, "Invalid Content-Length.");
				if (length > max) throw new HttpError(413, "Request body too large.");
				await Copy(body, (int)length, token);
			}
			request.Body = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
		}

		private async Task Copy(MemoryStream target, int count, CancellationToken token)
		{
			while (count > 0)
			{
				if (_start == _end && !await Fill(token)) throw new IOException("Connection closed.");
				int take = Math.Min(count, _end - _start);
				target.Write(_buffer, _start, take);
				_start += take;
				count -= take;
			}
		}

		private int IndexOf(ReadOnlySpan<byte> pattern)
		{
			int index = _buffer.AsSpan(_start, _end - _start).IndexOf(pattern);
			return index < 0 ? -1 : _start + index;
		}
	}
}
