// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Attributes;
using Polytoria.Client;
using Polytoria.Datamodel;
using Polytoria.Scripting;
using Polytoria.Scripting.Luau;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Instance = Polytoria.Datamodel.Instance;

namespace Polytoria.Creator.Gark;

/// <summary>Included only in Creator. Opt-in instrumentation in Gark's own local-test child process.</summary>
internal sealed partial class GarkPlaytestAgent : Node
{
	private ClientEntry _entry = null!;
	private string _role = "", _secret = "";
	private int _port;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly ConcurrentQueue<JsonObject> _commands = new();
	private readonly SemaphoreSlim _write = new(1);
	private readonly HashSet<Key> _keys = [];
	private readonly HashSet<string> _actions = [];
	private MouseButton? _mouse;
	private Vector2 _mousePosition;
	private TcpClient? _connection;
	private StreamWriter? _writer;
	private LogDispatcher? _logger;
	private bool _busy, _driving;
	internal const string EvalMarker = "__GARK_EVAL__:";
	private readonly object _evalLock = new();
	private string? _evalId;
	private TaskCompletionSource<string>? _evalResult;
	private JsonArray? _evalOutput;
	internal static void Attach(ClientEntry entry, Dictionary<string, string> args)
	{
		if (!args.TryGetValue("gark-test-port", out string? value) || !int.TryParse(value, out int port) || port is < 1 or > 65535) return;
		if (!args.TryGetValue("gark-test-secret", out string? secret) || !Guid.TryParseExact(secret, "N", out _)) return;
		if (!args.TryGetValue("gark-test-role", out string? role) || role is not ("server" or "client")) return;
		if (!args.ContainsKey("solo") && !args.ContainsKey("ltchild")) return;
		entry.AddChild(new GarkPlaytestAgent { _entry = entry, _port = port, _secret = secret, _role = role, ProcessPriority = -1000 });
	}
	public override void _Ready() => _ = Connect();
	private async Task Connect()
	{
		try
		{
			_connection = new(); await _connection.ConnectAsync("127.0.0.1", _port, _lifetime.Token);
			var stream = _connection.GetStream();
			_writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };
			await Send(new() { ["secret"] = _secret, ["role"] = _role });
			using var reader = new StreamReader(stream, new UTF8Encoding(false));
			while (await reader.ReadLineAsync(_lifetime.Token) is { } line) _commands.Enqueue(JsonNode.Parse(line)!.AsObject());
		}
		catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Text.Json.JsonException) { }
		finally { _lifetime.Cancel(); Callable.From(() => { if (IsInsideTree()) { ReleaseInput(); GetTree().Quit(); } }).CallDeferred(); }
	}
	private async Task Send(JsonObject packet)
	{
		await _write.WaitAsync(_lifetime.Token);
		try { if (_writer != null) await _writer.WriteLineAsync(packet.ToJsonString().AsMemory(), _lifetime.Token); }
		finally { _write.Release(); }
	}
	public override void _Process(double delta)
	{
		if (_logger == null && _entry.Root.ScriptService?.Logger is { } logger)
		{
			_logger = logger; logger.NewLog += Log;
			foreach (var log in logger.Logs.ToArray()) Log(log);
		}
		if (_driving) FocusGame();
		if (!_busy && !_lifetime.IsCancellationRequested && _commands.TryDequeue(out var command)) _ = Run(command);
	}
	private async void Log(LogDispatcher.LogData log)
	{
		if (_role == "client" && log.LogFrom == LogDispatcher.LogFromEnum.Server) return;
		lock (_evalLock)
		{
			// Results of playtest_eval travel through the normal log channel; keep them out of playtest_logs.
			if (log.Content.StartsWith(EvalMarker, StringComparison.Ordinal))
			{
				string rest = log.Content[EvalMarker.Length..];
				int split = rest.IndexOf(':');
				if (split > 0 && rest[..split] == _evalId) _evalResult?.TrySetResult(rest[(split + 1)..]);
				return;
			}
			_evalOutput?.Add((JsonNode)new JsonObject { ["severity"] = log.LogType.ToString().ToLowerInvariant(), ["content"] = log.Content });
		}
		try { await Send(new() { ["log"] = new JsonObject { ["kind"] = "runtime", ["severity"] = log.LogType.ToString().ToLowerInvariant(), ["content"] = log.Content, ["time"] = log.LoggedAt.ToString("O"), ["origin"] = log.LogFrom.ToString() } }); }
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
	}
	private async Task Run(JsonObject command)
	{
		_busy = true;
		var reply = new JsonObject { ["id"] = command["id"]?.ToString() };
		try
		{
			string method = command["method"]!.ToString();
			var args = command["args"]!.AsObject();
			if (method == "ready")
			{
				while (_entry.Root.Network?.NetInstance == null || (_role == "client" && (!_entry.Root.IsLoaded || _entry.Root.Players.LocalPlayer == null))) await Frame();
				await Frame(); reply["data"] = State(new());
			}
			else if (method == "inspect_playtest") { await Wait(args, "wait_seconds", 0); reply["data"] = State(args); }
			else if (method == "playtest_eval") reply["data"] = await Eval(args);
			else if (method == "playtest_input") { await Drive(args); reply["data"] = State(new()); }
			else if (method == "capture_playtest")
			{
				await Wait(args, "wait_seconds", 0);
				if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("The test client has no renderer.");
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				using var image = GetViewport().GetTexture().GetImage();
				bool jpeg = args["format"]?.ToString() == "jpeg";
				reply["image"] = Convert.ToBase64String(jpeg ? image.SaveJpgToBuffer(0.85f) : image.SavePngToBuffer());
				reply["mime_type"] = jpeg ? "image/jpeg" : "image/png";
				reply["data"] = new JsonObject { ["width"] = image.GetWidth(), ["height"] = image.GetHeight(), ["includes"] = "Actual test gameplay and screen UI" };
			}
			else throw new ArgumentException("Unknown test command.");
		}
		catch (Exception ex) { reply["error"] = ex.Message; }
		finally { _busy = false; }
		try { await Send(reply); }
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
	}
	private async Task Frame() { _lifetime.Token.ThrowIfCancellationRequested(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); _lifetime.Token.ThrowIfCancellationRequested(); }
	private async Task Wait(JsonObject args, string property, double fallback)
	{
		double seconds = args[property] != null ? GarkValues.Number(args[property]!) : fallback;
		if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentException(property + " must be finite and nonnegative.");
		ulong start = Time.GetTicksMsec();
		do { await Frame(); } while ((Time.GetTicksMsec() - start) / 1000.0 < seconds);
	}
	private void FocusGame()
	{
		// Emulate focus within this owned process only; never raise/focus a desktop window.
		Globals.Singleton.Notification((int)Node.NotificationWMWindowFocusIn);
		_entry.IsFocused = true;
	}
	private async Task Drive(JsonObject args)
	{
		if (_role != "client" || _entry.Root.Players.LocalPlayer == null) throw new InvalidOperationException("The test player is not ready.");
		var keys = (args["keys"]?.AsArray() ?? []).Select(k => OS.FindKeycodeFromString(k!.ToString())).ToArray();
		if (keys.Any(k => k == Key.None)) throw new ArgumentException("Unknown key name; use W, A, S, D, Space, E, Enter etc.");
		string[] actions = (args["actions"]?.AsArray() ?? []).Select(a => a!.ToString()).ToArray();
		if (actions.Any(a => a is not ("forward" or "backward" or "leftward" or "rightward" or "jump" or "sprint" or "activate"))) throw new ArgumentException("Unknown movement action.");
		try
		{
			_driving = true; FocusGame();
			if ((keys.Length > 0 || actions.Length > 0) && args["text"] == null) _entry.Root.PlayerGUI.GrabFocus();
			await Frame();
			if (args["mouse_position"] is JsonArray position)
			{
				float x = position[0]!.GetValue<float>(), y = position[1]!.GetValue<float>();
				if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x > 1 || y > 1) throw new ArgumentException("mouse_position must be within [0,1].");
				_mousePosition = new Vector2(x, y) * GetViewport().GetVisibleRect().Size;
				Input.ParseInputEvent(new InputEventMouseMotion { Position = _mousePosition, GlobalPosition = _mousePosition });
				await Frame();
			}
			foreach (Key key in keys) { _keys.Add(key); Input.ParseInputEvent(KeyEvent(key, true)); }
			foreach (string action in actions) { _actions.Add(action); Input.ActionPress(action); GetViewport().PushInput(new InputEventAction { Action = action, Pressed = true }); }
			if (args["mouse_button"] is { } mouse)
			{
				_mouse = mouse.ToString() switch { "left" => MouseButton.Left, "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => throw new ArgumentException("Unknown mouse button.") };
				Input.ParseInputEvent(MouseEvent(_mouse.Value, true));
			}
			if (args["text"] is { } text)
				foreach (var rune in text.ToString().EnumerateRunes())
				{
					_lifetime.Token.ThrowIfCancellationRequested();
					Input.ParseInputEvent(new InputEventKey { Pressed = true, Unicode = (uint)rune.Value });
					Input.ParseInputEvent(new InputEventKey { Pressed = false, Unicode = (uint)rune.Value });
				}
			if (args["mouse_delta"] is JsonArray delta)
			{
				var relative = new Vector2(delta[0]!.GetValue<float>(), delta[1]!.GetValue<float>());
				if (!relative.IsFinite()) throw new ArgumentException("mouse_delta must be finite.");
				await Frame();
				_mousePosition += relative;
				Input.ParseInputEvent(new InputEventMouseMotion { Position = _mousePosition, GlobalPosition = _mousePosition, Relative = relative, ScreenRelative = relative,
					ButtonMask = _mouse switch { MouseButton.Left => MouseButtonMask.Left, MouseButton.Right => MouseButtonMask.Right, MouseButton.Middle => MouseButtonMask.Middle, _ => 0 } });
			}
			await Wait(args, "duration_seconds", 0.15);
		}
		finally { ReleaseInput(); _driving = false; }
		await Frame();
	}
	/// <summary>Runs a chunk as a normal game script in this process and reports its return values and output.</summary>
	private async Task<JsonObject> Eval(JsonObject args)
	{
		string code = args["code"]!.ToString();
		double timeout = args["timeout_seconds"] != null ? GarkValues.Number(args["timeout_seconds"]!) : 30;
		// A bare expression ("Players.LocalPlayer.Position") is returned, REPL style.
		byte[] expression = LuaState.Compile("return " + code);
		if (expression.Length > 0 && expression[0] != 0) code = "return " + code;
		string id = Guid.NewGuid().ToString("N");
		// The first line of the chunk stays line 1 in error messages.
		string source = "local __gark_r = table.pack(pcall(function(...) " + code + "\nend))\n"
			+ "local __gark_v = {}\n"
			+ "for __gark_i = 2, __gark_r.n do local ok, encoded = pcall(json.serialize, __gark_r[__gark_i]); __gark_v[__gark_i - 1] = ok and encoded or json.serialize(tostring(__gark_r[__gark_i])) end\n"
			+ $"print(\"{EvalMarker}{id}:\" .. (__gark_r[1] and (\"ok:[\" .. table.concat(__gark_v, \",\") .. \"]\") or (\"error:\" .. json.serialize(tostring(__gark_r[2])))))";
		var world = _entry.Root;
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var output = new JsonArray();
		lock (_evalLock) { _evalId = id; _evalResult = completion; _evalOutput = output; }
		try
		{
			Datamodel.Script script = _role == "server" ? world.New<ServerScript>(world.ScriptService) : world.New<ClientScript>(world.ScriptService);
			script.Name = "GarkEval";
			script.Source = source;
			if (!script.Ran) script.Run();
			ulong start = Time.GetTicksMsec();
			while (!completion.Task.IsCompleted)
			{
				if ((Time.GetTicksMsec() - start) / 1000.0 > timeout)
					return new JsonObject { ["completed"] = false, ["target"] = _role, ["output"] = Snapshot(output),
						["note"] = $"Still running after {timeout:0.#} s (it may be waiting or looping). Later output appears in playtest_logs." };
				await Frame();
			}
			string result = completion.Task.Result;
			bool ok = result.StartsWith("ok:", StringComparison.Ordinal);
			JsonNode? values;
			try { values = JsonNode.Parse(result[(ok ? 3 : 6)..]); }
			catch (System.Text.Json.JsonException) { values = JsonValue.Create(result); }
			var reply = new JsonObject { ["completed"] = true, ["target"] = _role, ["ok"] = ok, ["output"] = Snapshot(output) };
			if (ok) reply["returned"] = values is JsonArray { Count: 1 } single ? single[0]?.DeepClone() : values;
			else reply["error"] = values;
			return reply;
		}
		finally { lock (_evalLock) { _evalId = null; _evalResult = null; _evalOutput = null; } }
	}
	private JsonArray Snapshot(JsonArray output) { lock (_evalLock) return (JsonArray)output.DeepClone(); }
	private static InputEventKey KeyEvent(Key key, bool pressed) => new() { Keycode = key, PhysicalKeycode = key, Pressed = pressed };
	private InputEventMouseButton MouseEvent(MouseButton button, bool pressed) => new() { Position = _mousePosition, GlobalPosition = _mousePosition, ButtonIndex = button, Pressed = pressed };
	private void ReleaseInput()
	{
		foreach (Key key in _keys) Input.ParseInputEvent(KeyEvent(key, false)); _keys.Clear();
		foreach (string action in _actions) { Input.ActionRelease(action); GetViewport().PushInput(new InputEventAction { Action = action, Pressed = false }); } _actions.Clear();
		if (_mouse is { } button) Input.ParseInputEvent(MouseEvent(button, false)); _mouse = null;
	}
	private JsonObject State(JsonObject args)
	{
		var world = _entry.Root;
		var all = Descendants(world).ToArray();
		string parentId = args["parent"]?.ToString() ?? "/";
		var parent = parentId is "" or "/" ? world : all.FirstOrDefault(i => i.ObjectID == parentId || PathOf(i) == parentId)
			?? throw new ArgumentException("No test instance at that ID/path.");
		string query = args["query"]?.ToString() ?? "";
		var matching = Descendants(parent).Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
		int offset = (int)Math.Max(0, args["offset"] != null ? GarkValues.Number(args["offset"]!) : 0);
		int limit = (int)Math.Clamp(args["limit"] != null ? GarkValues.Number(args["limit"]!) : 50, 1, 2000);
		var player = world.Players.LocalPlayer;
		var camera = world.Environment.CurrentCamera;
		return new() { ["loaded"] = world.IsLoaded, ["viewport_size"] = V(GetViewport().GetVisibleRect().Size),
			["player"] = player == null ? null : new JsonObject { ["id"] = player.ObjectID, ["position"] = V(player.Position), ["rotation"] = V(player.Rotation), ["velocity"] = V(player.Velocity), ["health"] = player.Health, ["can_move"] = player.CanMove },
			["camera"] = camera == null ? null : new JsonObject { ["position"] = V(camera.Position), ["rotation"] = V(camera.Rotation), ["forward"] = V(camera.Forward) },
			["instances"] = new JsonArray(matching.Skip(offset).Take(limit).Select(i => (JsonNode)Describe(i)).ToArray()), ["total"] = matching.Length,
			["next_offset"] = offset + limit < matching.Length ? offset + limit : null };
	}
	private static IEnumerable<Instance> Descendants(Instance root)
	{
		foreach (var child in root.GetChildren())
		{
			if (child.IsDeleted || child.GetType().IsDefined(typeof(ExplorerExcludeAttribute), false)) continue;
			yield return child;
			foreach (var descendant in Descendants(child)) yield return descendant;
		}
	}
	private static string PathOf(Instance item) => item is World ? "" : PathOf(item.Parent!) + "/" + item.Name;
	private JsonObject Describe(Instance item)
	{
		var data = new JsonObject { ["id"] = item.ObjectID, ["path"] = PathOf(item), ["class_name"] = item.ClassName, ["name"] = item.Name };
		if (item is Dynamic d) { data["position"] = V(d.Position); data["rotation"] = V(d.Rotation); data["size"] = V(d.Size); }
		if (item is UIField ui)
		{
			data["visible"] = ui.NodeControl.IsVisibleInTree(); data["position_pixels"] = V(ui.AbsolutePosition); data["size_pixels"] = V(ui.AbsoluteSize);
			data["center_normalized"] = V((ui.AbsolutePosition + ui.AbsoluteSize / 2) / GetViewport().GetVisibleRect().Size);
			data["ignore_mouse"] = ui.IgnoreMouse;
		}
		if (item is UILabel label) data["text"] = label.Text;
		if (item is UITextInput input) data["text"] = input.Text;
		return data;
	}
	private static JsonArray V(Vector3 v) => new(v.X, v.Y, v.Z);
	private static JsonArray V(Vector2 v) => new(v.X, v.Y);
	public override void _ExitTree()
	{
		_lifetime.Cancel(); ReleaseInput(); if (_logger != null) _logger.NewLog -= Log;
		_connection?.Dispose();
	}
}
