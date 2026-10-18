// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Creator.UI;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Formats;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using File = System.IO.File;

namespace Polytoria.Creator.Gark;

internal sealed partial class GarkTools
{
	private static void AddPlaytestDefinitions(GarkToolAdder add)
	{
		const string client = "\"client\":{\"type\":\"integer\",\"description\":\"Which test client (1..players). Default 1.\"}";
		add("start_playtest", "Start playtest", GarkToolFlags.Control,
			"Launch a real local server and rendered client(s) from a snapshot of the current worlds and unsaved scripts (authoring files are not saved or changed). Returns each player's spawn state once all have spawned. Restarting replaces only this tool's previous test. Runs until stop_playtest, a world switch or Creator exit.",
			"""{"players":{"type":"integer","description":"Number of rendered clients, each its own player (1-8, default 1). Address them with client in the other playtest tools."},"clock":{"type":"string","description":"Start the in-game clock at this ISO 8601 date-time (e.g. 2026-12-25T09:00:00Z) to test daily rewards, events or seasons. os.time, os.date and tick() advance from there on the server and every client. Default: real time. Only this local test is affected."},"width":{"type":"integer","description":"Client window width. Default 960."},"height":{"type":"integer","description":"Default 640."}}""");
		add("stop_playtest", "Stop playtest", GarkToolFlags.Control | GarkToolFlags.Idempotent,
			"Stop the test started by start_playtest and release simulated input. Logs stay readable.");
		add("inspect_playtest", "Inspect playtest", GarkToolFlags.Read,
			"Live player and camera state plus a paginated runtime hierarchy (as one client sees it) with positions, UI text, visibility and on-screen rectangles (center_normalized is ready for playtest_input). IDs belong to the test, not the editor. Narrow large worlds with parent, depth and query. wait_seconds lets physics and scripts run first.",
			"{" + client + ""","parent":{"type":"string","description":"Test instance ID or path such as /Environment/Lobby. Default: the world root."},"depth":{"type":"integer","description":"1 = direct children only. Default: all descendants."},"query":{"type":"string","description":"Case-insensitive name or class filter."},"offset":{"type":"integer"},"limit":{"type":"integer","description":"Default 50, max 2000. Long pages are cut at about 40 KB; follow next_offset."},"wait_seconds":{"type":"number"}}""");
		add("playtest_input", "Send playtest input", GarkToolFlags.Control,
			"Drive a test client. Hold keys (Godot names: W, A, S, D, Space, E, Enter, Shift...) and/or movement actions for duration_seconds, then release. mouse_position is normalized [0..1, 0..1] in the client view; mouse_button left/right/middle clicks there; mouse_delta [x,y] pixels moves while the button is held (right-drag turns the camera). text types into the focused text box. Returns the resulting player and camera state.",
			"{" + client + ""","keys":{"type":"array","items":{"type":"string"}},"actions":{"type":"array","items":{"type":"string","enum":["forward","backward","leftward","rightward","jump","sprint","activate"]}},"duration_seconds":{"type":"number"},"mouse_position":{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2},"mouse_delta":{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2},"mouse_button":{"type":"string","enum":["left","right","middle"]},"text":{"type":"string"}}""");
		add("capture_playtest", "Capture playtest", GarkToolFlags.Read,
			"Screenshot of a running test client: actual gameplay and screen UI. By default it shows the player's own camera; pass position and/or look_at (world coordinates) to frame any spot with a temporary camera for this one shot (the game camera is untouched). wait_seconds lets animations and layout settle.",
			"{" + client + ""","position":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3,"description":"Temporary camera position [x,y,z]."},"look_at":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3,"description":"World point to aim the temporary camera at."},"fov":{"type":"number","description":"Vertical field of view in degrees for the temporary camera."},"wait_seconds":{"type":"number"},"format":{"type":"string","enum":["png","jpeg"]}}""");
		add("playtest_logs", "Read playtest logs", GarkToolFlags.Read,
			"Server and client output in order: script prints, warnings and errors (kind=script) and engine messages (kind=engine, multi-line traces grouped into one entry). For script errors only: kind=script, severity=error. Pass next_after from the previous call as after to read only new entries. counts summarizes the whole log. Pages are cut at about 48 KB (long entries are shortened); follow has_more. Available after the test stops.",
			"""{"after":{"type":"integer"},"limit":{"type":"integer","description":"Default 100, max 5000."},"severity":{"type":"string","enum":["all","error","warning","info"]},"kind":{"type":"string","enum":["all","script","engine"]},"source":{"type":"string","description":"all (default), server, client (every client) or client1, client2..."},"contains":{"type":"string","description":"Case-insensitive text filter."}}""");
		add("playtest_eval", "Run Luau in playtest", GarkToolFlags.Control,
			"Run Luau inside the running test, on the server (default) or a client, with the normal game API and globals. The chunk can return values (JSON-encoded back to you) and its prints and errors are returned. Use it to inspect live state, call functions, fire events or set up scenarios. Event connections it makes stay active until the test stops. Waits up to timeout_seconds (default 30).",
			"""{"code":{"type":"string"},"target":{"type":"string","enum":["server","client"]},""" + client + ""","timeout_seconds":{"type":"number"}}""", "code");
	}
	private async Task<GarkToolResult> Playtest(string name, JsonObject args, CancellationToken token)
	{
		var test = GarkPlaytest.Get(session!, world!);
		if (name == "stop_playtest") { test.Stop(); return new(test.Status()); }
		if (name == "playtest_logs") return new(test.Logs(args));
		if (name == "start_playtest") return new(await test.Start(args, token, Progress));
		string target = name == "playtest_eval" ? Text(args, "target", "server") : "client";
		if (target is not ("server" or "client")) throw new ArgumentException("target must be server or client.");
		if (args["client"] != null && target == "server") throw new ArgumentException("client selects a test client; omit it for target=server.");
		string role = target == "server" ? "server" : "client" + Int(args, "client", 1, 1, GarkPlaytest.MaxPlayers);
		if (name == "playtest_eval" && Text(args, "code").Trim() == "") throw new ArgumentException("code is empty.");
		GarkToolResult result = await test.Request(role, name, args, token);
		CheckContext(token);
		return result;
	}
}

/// <summary>Editor-owned processes and authenticated loopback IPC; no new datamodel or gameplay protocol.</summary>
internal sealed partial class GarkPlaytest : Node
{
	private static GarkPlaytest? _current;
	private CreatorSession _session = null!;
	private World _world = null!;
	private readonly List<Process> _processes = [];
	private readonly ConcurrentDictionary<string, Peer> _peers = new();
	private readonly ConcurrentQueue<JsonObject> _incomingLogs = new();
	private readonly List<JsonObject> _logs = [];
	private CancellationTokenSource? _lifetime;
	private TcpListener? _listener;
	private string _secret = "", _snapshot = "";
	private long _sequence, _clockOffset;
	private int _players;
	private bool _running;
	private string _state = "Not started";
	internal static bool IsRunning => _current != null && IsInstanceValid(_current) && _current._running;
	internal static JsonObject? CurrentStatus() => _current != null && IsInstanceValid(_current) ? _current.Status() : null;
	internal static void StopCurrent() { if (_current != null && IsInstanceValid(_current)) _current.Stop(); }
	internal static GarkPlaytest Get(CreatorSession session, World world)
	{
		if (_current != null && IsInstanceValid(_current) && _current._session == session && _current._world == world) return _current;
		if (_current != null && IsInstanceValid(_current)) { _current.Stop(); _current.QueueFree(); }
		_current = new() { _session = session, _world = world };
		Globals.Singleton.AddChild(_current);
		return _current;
	}
	public override void _Ready() => Globals.BeforeQuit += Stop;
	public override void _Process(double delta)
	{
		DrainLogs();
		if (_running && (_world.IsDeleted || !IsInstanceValid(_session) || CreatorService.CurrentSession != _session || World.Current != _world)) Stop();
		if (_running && _processes.Any(p => p.HasExited)) { Stop(); _state = "Test process exited; read playtest_logs"; }
	}
	internal const int MaxPlayers = 8;
	[GeneratedRegex(@"\u001B\[[0-9;]*[A-Za-z]")] private static partial Regex AnsiCodes();
	private const int MaxTraceLines = 80, MaxEntryChars = 4000, MaxPageChars = 48_000;
	private void DrainLogs()
	{
		while (_incomingLogs.TryDequeue(out var log))
		{
			// Engine traces arrive one stdout line at a time; fold continuation lines into the message they belong to.
			string content = log["content"]?.ToString() ?? "";
			if (log["kind"]?.ToString() == "engine" && IsContinuation(content)
				&& _logs.LastOrDefault(l => l["source"]?.ToString() == log["source"]?.ToString()) is { } last && last["kind"]?.ToString() == "engine")
			{
				int lines = (int)(last["lines"]?.GetValue<int>() ?? 1);
				if (lines < MaxTraceLines) last["content"] = last["content"] + "\n" + content;
				else if (lines == MaxTraceLines) last["content"] = last["content"] + "\n…";
				last["lines"] = lines + 1;
				if (log["severity"]?.ToString() == "error") last["severity"] = "error";
				continue;
			}
			log["sequence"] = ++_sequence; _logs.Add(log);
		}
	}
	private static bool IsContinuation(string line) => line.Length > 0 && (char.IsWhiteSpace(line[0]) || line.StartsWith("at ", StringComparison.Ordinal)
		|| line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal) && line.Contains("] at ", StringComparison.Ordinal));
	internal JsonObject Status() => new() { ["running"] = _running, ["status"] = _state, ["world"] = _world.WorldFilePath, ["players"] = _players, ["log_count"] = _logs.Count };
	internal JsonObject Logs(JsonObject args)
	{
		DrainLogs();
		long after = args["after"] != null ? (long)GarkValues.Number(args["after"]!) : 0;
		int limit = (int)Math.Clamp(args["limit"] != null ? GarkValues.Number(args["limit"]!) : 100, 1, 5000);
		string severity = args["severity"]?.ToString() ?? "all", source = args["source"]?.ToString() ?? "all", kind = args["kind"]?.ToString() ?? "all";
		string contains = args["contains"]?.ToString() ?? "";
		if (severity is not ("all" or "error" or "warning" or "info")) throw new ArgumentException("severity must be all, error, warning or info.");
		if (kind is not ("all" or "script" or "engine")) throw new ArgumentException("kind must be all, script or engine.");
		if (source is not ("all" or "server" or "client" or "control") && !IsClientRole(source)) throw new ArgumentException("source must be all, server, client or client1..client" + MaxPlayers + ".");
		bool Matches(JsonObject l)
		{
			string from = l["source"]?.ToString() ?? "";
			return (severity == "all" || l["severity"]?.ToString() == severity) && (kind == "all" || l["kind"]?.ToString() == kind)
				&& (source == "all" || from == source || source == "client" && IsClientRole(from))
				&& (contains == "" || (l["content"]?.ToString() ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase));
		}
		var page = new JsonArray();
		long next = after; int size = 0; bool more = false;
		foreach (var log in _logs)
		{
			if (log["sequence"]!.GetValue<long>() <= after || !Matches(log)) continue;
			var copy = (JsonObject)log.DeepClone();
			string content = copy["content"]?.ToString() ?? "";
			if (content.Length > MaxEntryChars) { copy["content"] = content[..MaxEntryChars] + $"… ({content.Length - MaxEntryChars} more characters)"; copy["truncated"] = true; }
			int cost = copy.ToJsonString().Length;
			if (page.Count >= limit || page.Count > 0 && size + cost > MaxPageChars) { more = true; break; }
			page.Add((JsonNode)copy); size += cost; next = log["sequence"]!.GetValue<long>();
		}
		var counts = new JsonObject();
		foreach (var group in _logs.GroupBy(l => l["severity"]?.ToString() ?? "info")) counts[group.Key] = group.Count();
		return new() { ["status"] = Status(), ["logs"] = page, ["next_after"] = next, ["has_more"] = more, ["counts"] = counts };
	}
	internal static bool IsClientRole(string role) => role.StartsWith("client", StringComparison.Ordinal) && int.TryParse(role.AsSpan(6), out int n) && n is >= 1 and <= MaxPlayers && role == "client" + n;
	internal async Task<JsonObject> Start(JsonObject args, CancellationToken token, Action<string>? progress = null)
	{
		void Stage(string state) { _state = state; progress?.Invoke(state); }
		int players = (int)Math.Clamp(args["players"] != null ? Math.Round(GarkValues.Number(args["players"]!)) : 1, 1, MaxPlayers);
		int width = (int)Math.Clamp(args["width"] != null ? GarkValues.Number(args["width"]!) : 960, 320, 3840);
		int height = (int)Math.Clamp(args["height"] != null ? GarkValues.Number(args["height"]!) : 640, 240, 2160);
		long clockOffset = 0;
		if (args["clock"]?.ToString() is { } clock && clock.Trim() != "")
		{
			if (!DateTimeOffset.TryParse(clock, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
				throw new ArgumentException("clock must be an ISO 8601 date-time such as 2026-12-25T09:00:00Z.");
			if (at.Year < 1971) throw new ArgumentException("clock must be after 1970.");
			clockOffset = (long)Math.Round((at - DateTimeOffset.UtcNow).TotalSeconds);
		}
		Stop();
		_logs.Clear(); _sequence = 0;
		while (_incomingLogs.TryDequeue(out _)) { }
		_lifetime = new();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
		_running = true; Stage("Preparing snapshot");
		try
		{
			string folder = ProjectSettings.GlobalizePath("user://creator/gark/playtests/" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(folder);
			_snapshot = Path.Combine(folder, "snapshot.zip");
			string entry = await Snapshot(_session, _world, _snapshot, cancel.Token);
			cancel.Token.ThrowIfCancellationRequested();
			_secret = Guid.NewGuid().ToString("N");
			_listener = new(IPAddress.Loopback, 0); _listener.Start();
			int controlPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
			// ENet uses UDP, independent of the control listener.
			int gamePort;
			using (var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) gamePort = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
			_ = Accept(_lifetime.Token);
			_clockOffset = clockOffset;
			Stage("Starting server");
			Spawn("server", folder, controlPort, ["--headless", "-solo", _snapshot, "-entry", entry, "-subworld", "-nplr", "0", "-port", gamePort.ToString()]);
			await WaitPeer("server", cancel.Token);
			await Request("server", "ready", new(), cancel.Token);
			_players = players;
			for (int i = 1; i <= players; i++)
			{
				Stage(players == 1 ? "Starting client" : $"Starting client {i} of {players}");
				Spawn("client" + i, folder, controlPort, ["--windowed", "--resolution", $"{width}x{height}", "-network", "client", "-ltchild", "-id", i.ToString(), "-port", gamePort.ToString()]);
			}
			var spawned = new JsonArray();
			for (int i = 1; i <= players; i++)
			{
				await WaitPeer("client" + i, cancel.Token);
				var ready = await Request("client" + i, "ready", new(), cancel.Token);
				var data = (JsonObject)ready.Data.DeepClone(); data["client"] = i; spawned.Add((JsonNode)data);
			}
			_state = "Running";
			var result = new JsonObject { ["status"] = Status(), ["clients"] = spawned, ["snapshot"] = "Current open worlds and unsaved scripts. Restart the test after further edits." };
			if (clockOffset != 0) result["clock"] = new JsonObject { ["starts_at"] = DateTimeOffset.UtcNow.AddSeconds(clockOffset).ToString("O"), ["offset_seconds"] = clockOffset };
			return result;
		}
		catch { Stop(); throw; }
	}
	internal static async Task<string> Snapshot(CreatorSession session, World world, string destination, CancellationToken token)
	{
		// Capture before the asynchronous disk read so the result represents one editor state.
		var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		string entry = string.IsNullOrEmpty(world.WorldFilePath) ? "gark-preview.poly" : world.WorldFilePath.Replace('\\', '/');
		foreach (var open in session.OpenedWorlds.Where(w => !w.IsDeleted))
		{
			string path = open == world ? entry : (open.WorldFilePath ?? "").Replace('\\', '/');
			if (path != "") replacements[path] = PolyFormat.SaveCompressedPlaceAsByte(open);
		}
		replacements[entry] = PolyFormat.SaveCompressedPlaceAsByte(world);
		foreach (string relative in session.IndexToFile.Values.Distinct().Where(p => p.EndsWith(".luau") || p.EndsWith(".lua")))
		{
			string full = GarkProjectFiles.Resolve(session.ProjectFolderPath, relative);
			if (Tabs.Singleton?.FindTextEditor(full)?.EditorRoot.CodeEditor is { } buffer)
				replacements[relative.Replace('\\', '/')] = Encoding.UTF8.GetBytes(buffer.Text);
		}
		replacements["input.json"] = Encoding.UTF8.GetBytes(session.InputMap.SaveToString());
		byte[] packed = await PackedFormat.PackProject(session.ProjectFolderPath);
		token.ThrowIfCancellationRequested();
		using var memory = new MemoryStream();
		memory.Write(packed);
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Update, true))
		{
			JsonObject index;
			using (var reader = new StreamReader(archive.GetEntry("index.json")!.Open())) index = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
			index["world_" + entry] = entry;
			replacements["index.json"] = Encoding.UTF8.GetBytes(index.ToJsonString());
			foreach (var (path, bytes) in replacements)
			{
				archive.GetEntry(path)?.Delete();
				using var stream = archive.CreateEntry(path).Open(); stream.Write(bytes);
			}
		}
		await File.WriteAllBytesAsync(destination, memory.ToArray(), token);
		return entry;
	}
	private void Spawn(string role, string folder, int port, string[] arguments)
	{
		var start = new ProcessStartInfo(OS.GetExecutablePath()) { UseShellExecute = false, CreateNoWindow = role == "server",
			WorkingDirectory = ProjectSettings.GlobalizePath("res://"), RedirectStandardOutput = true, RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
		// Values starting with '-' would read as flags, so the offset travels as two Unix times.
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		string[] clock = _clockOffset != 0 ? ["-gark-test-clock", (now + _clockOffset).ToString(CultureInfo.InvariantCulture) + "/" + now.ToString(CultureInfo.InvariantCulture)] : [];
		if (Globals.IsInGDEditor) { start.ArgumentList.Add("--path"); start.ArgumentList.Add(ProjectSettings.GlobalizePath("res://")); }
		foreach (string argument in new[] { "--log-file", Path.Combine(folder, role + ".log"), "--rendering-method", RenderingDeviceSwitcher.GetCurrentDriverName(), "-rmswignore", "-gark-test-port", port.ToString(), "-gark-test-secret", _secret, "-gark-test-role", role }.Concat(clock).Concat(arguments)) start.ArgumentList.Add(argument);
		var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start test " + role);
		_processes.Add(process);
		var lifetime = _lifetime!.Token;
		bool skipOut = false, skipError = false;
		void Log(string? line, bool error, ref bool skipping)
		{
			if (lifetime.IsCancellationRequested || string.IsNullOrWhiteSpace(line)) return;
			line = AnsiCodes().Replace(line, "");
			// Script output arrives structured from the in-process agent (kind=script); drop its console echo and the echo's trace lines.
			if (IsContinuation(line) ? skipping : skipping = line.Contains("[Lua] ", StringComparison.Ordinal) || line.Contains(GarkPlaytestAgent.EvalMarker, StringComparison.Ordinal)) return;
			_incomingLogs.Enqueue(new() { ["source"] = role, ["kind"] = "engine", ["severity"] = line.StartsWith("WARNING:") ? "warning" : error || line.Contains("ERROR:") || line.Contains("[ERROR]") ? "error" : "info", ["content"] = line, ["time"] = DateTime.UtcNow.ToString("O") });
		}
		process.OutputDataReceived += (_, e) => Log(e.Data, false, ref skipOut); process.ErrorDataReceived += (_, e) => Log(e.Data, true, ref skipError);
		process.BeginOutputReadLine(); process.BeginErrorReadLine();
	}
	private async Task Accept(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				var client = await _listener!.AcceptTcpClientAsync(token);
				_ = Connect(client, token);
			}
		}
		catch (Exception ex) { if (!token.IsCancellationRequested) _incomingLogs.Enqueue(new() { ["source"] = "control", ["severity"] = "error", ["content"] = ex.Message }); }
	}
	private async Task Connect(TcpClient client, CancellationToken token)
	{
		Peer? peer = null;
		try
		{
			var stream = client.GetStream();
			var reader = new StreamReader(stream, new UTF8Encoding(false));
			using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(10));
			var hello = JsonNode.Parse(await reader.ReadLineAsync(handshake.Token) ?? "{}")!;
			string role = hello["role"]?.ToString() ?? "";
			if (hello["secret"]?.ToString() != _secret || role != "server" && !IsClientRole(role)) return;
			peer = new(client, new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
			if (!_peers.TryAdd(role, peer)) return;
			while (await reader.ReadLineAsync(token) is { } line)
			{
				token.ThrowIfCancellationRequested();
				var packet = JsonNode.Parse(line)!.AsObject();
				if (packet["log"] is JsonObject log) { log["source"] = role; _incomingLogs.Enqueue((JsonObject)log.DeepClone()); }
				else if (packet["id"]?.ToString() is { } id && peer.Pending.TryRemove(id, out var completion)) completion.TrySetResult(packet);
			}
		}
		catch (Exception ex) { peer?.Fail(ex); }
		finally { peer?.Fail(new IOException("Test connection closed.")); client.Dispose(); }
	}
	private async Task WaitPeer(string role, CancellationToken token)
	{
		while (!_peers.ContainsKey(role))
		{
			if (_processes.Any(p => p.HasExited)) throw new InvalidOperationException("The test exited during startup. Read playtest_logs for the error.");
			await Task.Delay(50, token);
		}
	}
	internal async Task<GarkToolResult> Request(string role, string method, JsonObject args, CancellationToken token)
	{
		if (!_running) throw new InvalidOperationException("Start a Gark playtest first.");
		if (!_peers.TryGetValue(role, out var peer))
			throw new ArgumentException(IsClientRole(role) ? $"This test has {_players} client(s); pass client 1..{_players} or restart with more players." : "Unknown test process " + role + ".");
		// Input/wait durations are chosen by the caller; no overall playtest duration cutoff.
		double duration = (args["duration_seconds"] ?? args["wait_seconds"] ?? args["timeout_seconds"]) is { } value ? GarkValues.Number(value) : 0;
		if (!double.IsFinite(duration) || duration < 0 || duration > TimeSpan.MaxValue.TotalSeconds - 60) throw new ArgumentException("Duration must be a finite nonnegative number.");
		string id = Guid.NewGuid().ToString("N");
		var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		peer.Pending[id] = completion;
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime!.Token);
		try
		{
			if (peer.Error != null) throw new IOException("Test connection closed; read playtest_logs.", peer.Error);
			await peer.Send(new() { ["id"] = id, ["method"] = method, ["args"] = args.DeepClone() }, cancel.Token);
			var packet = await completion.Task.WaitAsync(cancel.Token);
			if (packet["error"] != null) throw new InvalidOperationException(packet["error"]!.ToString());
			var data = packet["data"]?.DeepClone() ?? new JsonObject();
			return packet["image"] is { } image
				? new(data, [new GarkImage(Convert.FromBase64String(image.ToString()), packet["mime_type"]?.ToString() ?? "image/png")])
				: new GarkToolResult(data);
		}
		catch (OperationCanceledException)
		{
			Stop(); // A cancelled command must never leave held input or an unattended child behind.
			if (token.IsCancellationRequested) throw;
			throw new InvalidOperationException("The test stopped or did not respond. Read playtest_logs for details.");
		}
		finally { peer.Pending.TryRemove(id, out _); }
	}
	internal void Stop()
	{
		_running = false; _state = "Stopped"; _clockOffset = 0;
		_lifetime?.Cancel(); _listener?.Stop(); _listener = null;
		foreach (var peer in _peers.Values) peer.Dispose(); _peers.Clear();
		foreach (var process in _processes)
		{
			try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
			process.Dispose();
		}
		_processes.Clear(); _lifetime?.Dispose(); _lifetime = null;
		if (_snapshot != "") { try { File.Delete(_snapshot); } catch (IOException) { } _snapshot = ""; }
		DrainLogs();
	}
	public override void _ExitTree() { Stop(); Globals.BeforeQuit -= Stop; if (_current == this) _current = null; }
	private sealed class Peer(TcpClient client, StreamWriter writer) : IDisposable
	{
		internal readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Pending = new();
		internal Exception? Error;
		private readonly SemaphoreSlim _write = new(1);
		internal async Task Send(JsonObject packet, CancellationToken token)
		{
			await _write.WaitAsync(token);
			try { await writer.WriteLineAsync(packet.ToJsonString().AsMemory(), token); }
			finally { _write.Release(); }
		}
		internal void Fail(Exception ex) { Error = ex; foreach (var pending in Pending.Values) pending.TrySetException(ex); }
		public void Dispose() { Fail(new IOException("The test stopped.")); client.Dispose(); }
	}
}
