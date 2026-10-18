// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;

namespace Polytoria.Creator.Gark;

/// <summary>
/// <c>--headless --quiet --gark-mcp</c>: speaks MCP over stdio for clients that only launch
/// commands, and forwards to the running Creator's HTTP server. It answers initialize and
/// list requests itself while Creator is closed, so agents can start before Creator does.
/// </summary>
internal static class GarkMcpBridge
{
	internal static int Run(Dictionary<string, string> args)
	{
		string? url = args.TryGetValue("gark-mcp-url", out var value) && value != "" ? value : null;
		string discovery = GarkMcpHost.DiscoveryPath;
		// Run off Godot's synchronization context; the main thread just waits.
		return Task.Run(() => new Connection(url, discovery).RunAsync()).GetAwaiter().GetResult();
	}

	private sealed class OfflineHost : IGarkMcpHost
	{
		public bool Online => false;
		public bool AllowEdits => true;
		public Task<GarkToolResult> CallToolAsync(GarkMcpSession session, GarkToolDefinition tool, JsonObject args, Action<string> progress, bool record, CancellationToken token) =>
			throw new InvalidOperationException("Polytoria Creator is not running.");
	}

	private sealed class Connection(string? fixedUrl, string discoveryPath)
	{
		private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };
		private readonly SemaphoreSlim _write = new(1, 1), _connect = new(1, 1);
		private readonly GarkMcpProtocol _offline = new(new OfflineHost());
		private readonly GarkMcpSession _offlineSession = new("stdio", "stdio");
		private readonly CancellationTokenSource _lifetime = new();
		private Stream _stdout = null!;
		private string? _url, _sessionId, _protocolVersion;
		private JsonObject? _initialize;
		private CancellationTokenSource? _listener;

		internal async Task<int> RunAsync()
		{
			_stdout = Console.OpenStandardOutput();
			using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
			var inflight = new List<Task>();
			while (await stdin.ReadLineAsync() is { } line)
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				JsonNode? node;
				try { node = JsonNode.Parse(line); }
				catch (JsonException) { await Send(GarkMcpProtocol.Error(null, -32700, "Parse error")); continue; }
				var messages = node switch { JsonObject single => [single], JsonArray batch => batch.OfType<JsonObject>().ToArray(), _ => Array.Empty<JsonObject>() };
				foreach (var message in messages)
				{
					// Initialize must finish before anything else; everything else may run concurrently (cancellation included).
					var task = Handle(message);
					if (message["method"]?.ToString() == "initialize") await task;
					else inflight.Add(task);
				}
				inflight.RemoveAll(t => t.IsCompleted);
			}
			_lifetime.Cancel();
			try { await Task.WhenAll(inflight).WaitAsync(TimeSpan.FromSeconds(2)); }
			catch (Exception) { }
			if (_sessionId != null && _url != null)
			{
				try
				{
					using var end = new HttpRequestMessage(HttpMethod.Delete, _url);
					end.Headers.Add("Mcp-Session-Id", _sessionId);
					using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
					await _http.SendAsync(end, timeout.Token);
				}
				catch (Exception) { }
			}
			return 0;
		}

		private async Task Send(JsonObject message)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
			await _write.WaitAsync();
			try
			{
				await _stdout.WriteAsync(bytes);
				await _stdout.FlushAsync();
			}
			finally { _write.Release(); }
		}

		private async Task Handle(JsonObject message)
		{
			try
			{
				string? method = message["method"]?.ToString();
				if (method == "initialize")
				{
					_initialize = message["params"]?.DeepClone() as JsonObject ?? new JsonObject();
					_protocolVersion = GarkMcpProtocol.Negotiate(_initialize["protocolVersion"]?.ToString());
				}
				if (await Forward(message)) return;
				// Creator is not reachable: answer from the built-in definitions.
				var reply = await _offline.HandleAsync(_offlineSession, message, _ => { }, _lifetime.Token);
				if (reply != null) await Send(reply);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				if (GarkMcpProtocol.IsRequest(message)) await Send(GarkMcpProtocol.Error(message["id"], -32603, ex.Message));
			}
			catch (OperationCanceledException) { }
		}

		private string? DiscoverUrl()
		{
			if (fixedUrl != null) return fixedUrl;
			try
			{
				var info = JsonNode.Parse(File.ReadAllText(discoveryPath));
				if (info?["url"]?.GetValue<string>() is { } url) return url;
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
			return $"http://127.0.0.1:{GarkMcpHost.DefaultPort}/mcp";
		}

		/// <summary>Sends the message to Creator and relays every reply. False when Creator is unreachable.</summary>
		private async Task<bool> Forward(JsonObject message, bool retried = false)
		{
			bool initialize = message["method"]?.ToString() == "initialize";
			if (!initialize && _sessionId == null && !await Connect()) return false;
			string? sessionAtSend = _sessionId;
			HttpResponseMessage response;
			try { response = await Post(message, initialize ? null : _sessionId); }
			catch (HttpRequestException)
			{
				// Creator closed or moved to another port. Reconnect once on the next call.
				_sessionId = null;
				if (!retried && !initialize && await Connect()) return await Forward(message, true);
				return false;
			}
			using (response)
			{
				if (response.StatusCode == HttpStatusCode.NotFound && !initialize && !retried)
				{
					if (_sessionId == sessionAtSend) _sessionId = null;
					return await Connect() && await Forward(message, true);
				}
				if (initialize && response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) SetSession(ids.First());
				if (response.StatusCode == HttpStatusCode.Accepted) return true;
				if (!response.IsSuccessStatusCode)
				{
					if (GarkMcpProtocol.IsRequest(message))
						await Send(GarkMcpProtocol.Error(message["id"], -32603, $"Creator returned HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}"));
					return true;
				}
				await Relay(response, Send);
				return true;
			}
		}

		private async Task<HttpResponseMessage> Post(JsonObject message, string? session)
		{
			_url ??= DiscoverUrl();
			var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
			request.Headers.Accept.ParseAdd("application/json");
			request.Headers.Accept.ParseAdd("text/event-stream");
			if (session != null) request.Headers.Add("Mcp-Session-Id", session);
			if (_protocolVersion != null) request.Headers.Add("MCP-Protocol-Version", _protocolVersion);
			try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token); }
			catch (HttpRequestException)
			{
				_url = null; // Re-read the discovery file next time.
				throw;
			}
		}

		private static async Task Relay(HttpResponseMessage response, Func<JsonObject, Task> send)
		{
			if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
			{
				using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
				var data = new StringBuilder();
				while (await reader.ReadLineAsync() is { } line)
				{
					if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line.AsSpan(5).TrimStart()); }
					else if (line.Length == 0 && data.Length > 0)
					{
						if (JsonNode.Parse(data.ToString()) is JsonObject message) await send(message);
						data.Clear();
					}
				}
				return;
			}
			string body = await response.Content.ReadAsStringAsync();
			if (string.IsNullOrWhiteSpace(body)) return;
			switch (JsonNode.Parse(body))
			{
				case JsonObject single: await send(single); break;
				case JsonArray batch: foreach (var item in batch.OfType<JsonObject>()) await send(item); break;
			}
		}

		/// <summary>Opens a Creator session on behalf of the stdio client, replaying its initialize.</summary>
		private async Task<bool> Connect()
		{
			await _connect.WaitAsync();
			try
			{
				if (_sessionId != null) return true;
				var init = GarkMcpProtocol.Request(JsonValue.Create("bridge-init"), "initialize", (JsonObject?)_initialize?.DeepClone() ?? new JsonObject
				{
					["protocolVersion"] = GarkMcpProtocol.LatestVersion, ["capabilities"] = new JsonObject(),
					["clientInfo"] = new JsonObject { ["name"] = "stdio bridge", ["version"] = GarkMcpProtocol.Version }
				});
				using (var response = await Post(init, null))
				{
					if (!response.IsSuccessStatusCode) return false;
					if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) SetSession(ids.First());
					else return false;
					await Relay(response, _ => Task.CompletedTask);
				}
				using (await Post(GarkMcpProtocol.Notification("notifications/initialized"), _sessionId)) { }
				return true;
			}
			catch (HttpRequestException) { return false; }
			finally { _connect.Release(); }
		}

		private void SetSession(string id)
		{
			_sessionId = id;
			_listener?.Cancel();
			_listener = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
			_ = Listen(id, _listener.Token);
		}

		/// <summary>Relays server notifications (such as a changed tool list) from Creator's event stream.</summary>
		private async Task Listen(string session, CancellationToken token)
		{
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, _url);
				request.Headers.Accept.ParseAdd("text/event-stream");
				request.Headers.Add("Mcp-Session-Id", session);
				using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
				if (response.IsSuccessStatusCode) await Relay(response, m => m["id"] == null ? Send(m) : Task.CompletedTask);
			}
			catch (Exception) { /* Optional; requests still work without it. */ }
		}
	}
}
