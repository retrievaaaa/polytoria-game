// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Creator.Settings;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Shared.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using File = System.IO.File;

namespace Polytoria.Creator.Gark;

/// <summary>Recent tool calls from connected agents, shown on the Gark page. Main thread only.</summary>
internal sealed class GarkMcpActivity
{
	internal sealed class Entry
	{
		internal int Id;
		internal string Client = "", Tool = "", Title = "", Arguments = "";
		internal DateTime Started;
		internal TimeSpan? Duration;
		internal bool Failed;
		internal string? Result;
		internal IReadOnlyList<GarkImage> Images = [];
		internal bool Done => Duration != null;
	}

	private const int Capacity = 300;
	private readonly List<Entry> _entries = [];
	private int _next;
	internal IReadOnlyList<Entry> Entries => _entries;
	internal int TotalCalls { get; private set; }
	internal event Action<Entry>? Added;
	internal event Action<Entry>? Updated;
	internal event Action? Cleared;

	internal Entry Begin(string client, GarkToolDefinition tool, JsonObject args)
	{
		var entry = new Entry
		{
			Id = ++_next, Client = client, Tool = tool.Name, Title = tool.Title, Started = DateTime.Now,
			Arguments = args.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
		};
		_entries.Add(entry);
		if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
		TotalCalls++;
		Added?.Invoke(entry);
		return entry;
	}

	internal void Complete(Entry? entry, GarkToolResult? result, string? error)
	{
		if (entry == null) return;
		entry.Duration = DateTime.Now - entry.Started;
		entry.Failed = error != null;
		entry.Result = error ?? result?.Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
		if (entry.Result?.Length > 20000) entry.Result = entry.Result[..20000] + "\n…";
		entry.Images = result?.Images ?? [];
		Updated?.Invoke(entry);
	}

	internal void Clear()
	{
		_entries.Clear();
		Cleared?.Invoke();
	}
}

/// <summary>
/// Owns the Gark MCP server inside Creator: starts it from settings, runs tool calls on Godot's main
/// thread (edits one at a time), and publishes a discovery file so the stdio bridge can find it.
/// </summary>
internal sealed partial class GarkMcpHost : Node, IGarkMcpHost
{
	internal const int DefaultPort = 47830;
	internal static string DiscoveryPath => ProjectSettings.GlobalizePath("user://creator/gark/mcp.json");
	internal static GarkMcpHost? Instance { get; private set; }

	private readonly GarkMcpProtocol _protocol;
	private readonly ConcurrentQueue<Action> _mainThread = new();
	private readonly SemaphoreSlim _exclusive = new(1, 1);
	private GarkMcpHttpServer? _server;
	private volatile bool _allowEdits = true;
	private volatile bool _sessionsDirty;
	private double _pruneTimer;
	private int? _commandLinePort;

	internal GarkMcpActivity Activity { get; } = new();
	internal string? Error { get; private set; }
	internal bool Running => _server != null;
	/// <summary>Started with --gark-mcp-serve [port]: the server runs for this session whatever the saved setting says.</summary>
	internal bool StartedFromCommandLine { get; private set; }
	private int TargetPort => _commandLinePort ?? ConfiguredPort;
	internal int Port => _server?.Port ?? TargetPort;
	internal string Url => $"http://127.0.0.1:{Port}/mcp";
	internal IReadOnlyCollection<GarkMcpSession> Sessions => _server?.Sessions ?? [];
	/// <summary>Server state, sessions or settings changed. Raised on the main thread.</summary>
	internal event Action? Changed;

	public bool Online => true;
	public bool AllowEdits => _allowEdits;

	internal static int ConfiguredPort => Math.Clamp(CreatorSettingsService.Instance.Get<int>(CreatorSettingKeys.Gark.McpPort), 1024, 65535);

	public GarkMcpHost()
	{
		_protocol = new GarkMcpProtocol(this);
		Name = "GarkMcpHost";
		ProcessMode = ProcessModeEnum.Always;
	}

	public override void _Ready()
	{
		Instance = this;
		if (Polytoria.Shared.Globals.ReadCmdArgs().TryGetValue("gark-mcp-serve", out string? port))
		{
			StartedFromCommandLine = true;
			if (int.TryParse(port, out int value) && value is >= 1024 and <= 65535) _commandLinePort = value;
		}
		CreatorSettingsService.Instance.Changed += OnSettingChanged;
		Apply();
	}

	public override void _ExitTree()
	{
		CreatorSettingsService.Instance.Changed -= OnSettingChanged;
		Stop();
		if (Instance == this) Instance = null;
	}

	public override void _Process(double delta)
	{
		while (_mainThread.TryDequeue(out var work)) work();
		if (_sessionsDirty)
		{
			_sessionsDirty = false;
			Changed?.Invoke();
		}
		_pruneTimer += delta;
		if (_pruneTimer > 60)
		{
			_pruneTimer = 0;
			_server?.Prune(TimeSpan.FromHours(6));
		}
	}

	private void OnSettingChanged(SettingChangedEvent change)
	{
		if (!change.Key.StartsWith("gark.", StringComparison.Ordinal)) return;
		bool allow = CreatorSettingsService.Instance.Get<bool>(CreatorSettingKeys.Gark.McpAllowEdits);
		if (allow != _allowEdits)
		{
			_allowEdits = allow;
			_server?.NotifyToolsChanged();
		}
		Apply();
	}

	private void Apply()
	{
		var settings = CreatorSettingsService.Instance;
		_allowEdits = settings.Get<bool>(CreatorSettingKeys.Gark.McpAllowEdits);
		if (!StartedFromCommandLine && !settings.Get<bool>(CreatorSettingKeys.Gark.McpEnabled))
		{
			Stop();
			Error = null;
		}
		else if (_server == null || _server.Port != TargetPort)
		{
			Stop();
			try
			{
				_server = new GarkMcpHttpServer(_protocol, TargetPort);
				_server.SessionsChanged += () => _sessionsDirty = true;
				Error = null;
				WriteDiscovery();
			}
			catch (SocketException ex)
			{
				_server = null;
				Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
					? $"Port {TargetPort} is already in use (another Creator window, or another program). Choose a different port."
					: $"Could not start the server on port {TargetPort}: {ex.Message}";
			}
		}
		Changed?.Invoke();
	}

	private void Stop()
	{
		if (_server == null) return;
		_server.Dispose();
		_server = null;
		try
		{
			if (File.Exists(DiscoveryPath) && JsonNode.Parse(File.ReadAllText(DiscoveryPath))?["pid"]?.GetValue<int>() == OS.GetProcessId())
				File.Delete(DiscoveryPath);
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
	}

	private void WriteDiscovery()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(DiscoveryPath)!);
			File.WriteAllText(DiscoveryPath, new JsonObject
			{
				["url"] = Url, ["port"] = Port, ["pid"] = OS.GetProcessId(), ["version"] = GarkMcpProtocol.Version
			}.ToJsonString());
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GD.PushWarning("Gark MCP: could not write discovery file: " + ex.Message); }
	}

	/// <summary>Restarts the server, dropping every connection. Agents reconnect on their next call.</summary>
	internal void DisconnectAll()
	{
		Stop();
		Apply();
	}

	public Task<GarkToolResult> CallToolAsync(GarkMcpSession session, GarkToolDefinition tool, JsonObject args, Action<string> progress, bool record, CancellationToken token)
	{
		var completion = new TaskCompletionSource<GarkToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_mainThread.Enqueue(async () =>
		{
			var entry = record ? Activity.Begin(session.Label, tool, args) : null;
			bool locked = false;
			try
			{
				token.ThrowIfCancellationRequested();
				// Reads run immediately; anything that changes the project or test runs one call at a time.
				if (!tool.ReadOnly) { await _exclusive.WaitAsync(token); locked = true; }
				var tools = new GarkTools(CreatorService.CurrentSession, World.Current) { Progress = progress };
				var result = await tools.ExecuteAsync(tool.Name, args, token);
				Activity.Complete(entry, result, null);
				completion.TrySetResult(result);
			}
			catch (Exception ex)
			{
				var error = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
				Activity.Complete(entry, null, error is OperationCanceledException ? "Cancelled" : error.Message);
				completion.TrySetException(error);
			}
			finally { if (locked) _exclusive.Release(); }
		});
		return completion.Task;
	}
}
