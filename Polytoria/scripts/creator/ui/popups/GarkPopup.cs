// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Creator.Gark;
using Polytoria.Creator.Settings;
using Polytoria.Datamodel.Creator;
using Polytoria.Shared;
using Polytoria.Shared.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using File = System.IO.File;

namespace Polytoria.Creator.UI.Popups;

/// <summary>The Gark page: runs the MCP server, explains how to connect agents and shows what they do.</summary>
public sealed partial class GarkPopup : Window
{
	private sealed record Agent(string Name, string Instructions, Func<string> Snippet, string? ProjectFile = null, Func<JsonObject, JsonObject>? Merge = null);

	private static readonly Color Muted = new(0.62f, 0.66f, 0.71f);
	private static readonly Color Good = new(0.42f, 0.83f, 0.52f);
	private static readonly Color Warn = new(0.96f, 0.66f, 0.3f);

	private readonly Dictionary<int, (Button Toggle, RichTextLabel Detail, VBoxContainer Card)> _cards = [];
	private GarkMcpHost? _host;
	private Label _statusDot = null!, _statusText = null!, _footer = null!;
	private Button _copyUrl = null!, _stopTest = null!, _disconnect = null!, _projectButton = null!;
	private CheckButton _enabled = null!, _allowEdits = null!;
	private LineEdit _port = null!;
	private OptionButton _agentPicker = null!;
	private Label _agentInstructions = null!, _projectNote = null!;
	private TextEdit _snippet = null!;
	private VBoxContainer _agents = null!, _feed = null!;
	private ScrollContainer _feedScroll = null!;
	private Agent[] _agentsList = [];
	private double _refresh;
	private bool _syncing;

	public GarkPopup()
	{
		Title = "Gark";
		Size = new Vector2I(780, 760);
		MinSize = new Vector2I(520, 480);
		Transient = true;
		Exclusive = false;
	}

	public override void _Ready()
	{
		_host = GarkMcpHost.Instance;
		_agentsList = BuildAgents();
		BuildUI();
		CloseRequested += Hide;
		CreatorSettingsService.Instance.Changed += OnSettingChanged;
		if (_host != null)
		{
			_host.Changed += Refresh;
			_host.Activity.Added += AddCard;
			_host.Activity.Updated += UpdateCard;
			_host.Activity.Cleared += ClearCards;
			foreach (var entry in _host.Activity.Entries.TakeLast(100)) AddCard(entry);
		}
		Refresh();
	}

	public override void _ExitTree()
	{
		CreatorSettingsService.Instance.Changed -= OnSettingChanged;
		if (_host != null)
		{
			_host.Changed -= Refresh;
			_host.Activity.Added -= AddCard;
			_host.Activity.Updated -= UpdateCard;
			_host.Activity.Cleared -= ClearCards;
		}
	}

	public override void _Input(InputEvent input)
	{
		if (input.IsActionPressed("ui_cancel")) { SetInputAsHandled(); Hide(); }
	}

	public override void _Process(double delta)
	{
		_refresh += delta;
		if (_refresh < 1) return;
		_refresh = 0;
		_stopTest.Visible = GarkPlaytest.IsRunning;
		RefreshAgents();
	}

	private string Url => _host?.Url ?? $"http://127.0.0.1:{GarkMcpHost.DefaultPort}/mcp";
	private int Port => _host?.Port ?? GarkMcpHost.DefaultPort;

	private static StyleBoxFlat Style(Color color, int padding) => new()
	{
		BgColor = color, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
		ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
	};
	private static Label Text(string text, int size = 14, Color? color = null, bool wrap = false)
	{
		var label = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
		label.AddThemeFontSizeOverride("font_size", size);
		if (color != null) label.AddThemeColorOverride("font_color", color.Value);
		return label;
	}
	private static Font Monospace() => new SystemFont { FontNames = ["Cascadia Mono", "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "monospace"] };

	private void BuildUI()
	{
		var background = new PanelContainer();
		background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		background.AddThemeStyleboxOverride("panel", Style(new Color(0.12f, 0.12f, 0.12f), 18));
		AddChild(background);
		var layout = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		layout.AddThemeConstantOverride("separation", 12);
		background.AddChild(layout);

		// Header
		var header = new HBoxContainer();
		header.AddThemeConstantOverride("separation", 12);
		layout.AddChild(header);
		header.AddChild(new TextureRect
		{
			Texture = GD.Load<Texture2D>("res://assets/textures/creator/gark.png"), CustomMinimumSize = new Vector2(44, 44),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
		});
		var heading = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		heading.AddThemeConstantOverride("separation", 0);
		heading.AddChild(Text("Gark", 23));
		heading.AddChild(Text("Let your AI agent build in Creator, over MCP", 13, Muted));
		header.AddChild(heading);
		var settings = new Button { Text = "Settings", TooltipText = "Open Gark settings" };
		settings.Pressed += () => CreatorService.Interface.OpenGarkSettings();
		header.AddChild(settings);

		// Server status and controls
		var statusPanel = new PanelContainer();
		statusPanel.AddThemeStyleboxOverride("panel", Style(new Color(0.16f, 0.16f, 0.17f), 14));
		layout.AddChild(statusPanel);
		var status = new VBoxContainer();
		status.AddThemeConstantOverride("separation", 10);
		statusPanel.AddChild(status);
		var statusRow = new HBoxContainer();
		statusRow.AddThemeConstantOverride("separation", 8);
		status.AddChild(statusRow);
		_statusDot = Text("●", 16);
		statusRow.AddChild(_statusDot);
		_statusText = Text("", 14, null, true);
		_statusText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		statusRow.AddChild(_statusText);
		_copyUrl = new Button { Text = "Copy URL", TooltipText = "Copy the server address" };
		_copyUrl.Pressed += () => { DisplayServer.ClipboardSet(Url); _footer.Text = "Copied " + Url; };
		statusRow.AddChild(_copyUrl);

		var controls = new HBoxContainer();
		controls.AddThemeConstantOverride("separation", 18);
		status.AddChild(controls);
		_enabled = new CheckButton { Text = "Server on", TooltipText = "Run the MCP server whenever Creator is open" };
		_enabled.Toggled += on => { if (!_syncing) CreatorSettingsService.Instance.Set(CreatorSettingKeys.Gark.McpEnabled, on); };
		controls.AddChild(_enabled);
		_allowEdits = new CheckButton { Text = "Allow edits", TooltipText = "When off, agents can read, capture and playtest, but not change worlds or scripts" };
		_allowEdits.Toggled += on => { if (!_syncing) CreatorSettingsService.Instance.Set(CreatorSettingKeys.Gark.McpAllowEdits, on); };
		controls.AddChild(_allowEdits);
		var portRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.End };
		portRow.AddChild(Text("Port", 13, Muted));
		_port = new LineEdit { CustomMinimumSize = new Vector2(80, 0), TooltipText = "Local port (1024-65535). Press Enter to apply." };
		_port.TextSubmitted += _ => ApplyPort();
		_port.FocusExited += ApplyPort;
		portRow.AddChild(_port);
		controls.AddChild(portRow);

		// Tabs
		var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		layout.AddChild(tabs);
		tabs.AddChild(BuildConnectTab());
		tabs.AddChild(BuildActivityTab());

		// Footer
		var footer = new HBoxContainer();
		footer.AddThemeConstantOverride("separation", 10);
		_footer = Text("", 12, Muted);
		_footer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_footer.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		footer.AddChild(_footer);
		_stopTest = new Button { Text = "Stop test", TooltipText = "Stop the playtest an agent started", Visible = false };
		_stopTest.Pressed += () => { GarkPlaytest.StopCurrent(); _footer.Text = "Playtest stopped"; };
		footer.AddChild(_stopTest);
		_disconnect = new Button { Text = "Disconnect agents", TooltipText = "Restart the server, ending every connection and cancelling running tools" };
		_disconnect.Pressed += () => { _host?.DisconnectAll(); _footer.Text = "All agents disconnected"; };
		footer.AddChild(_disconnect);
		layout.AddChild(footer);
	}

	private Control BuildConnectTab()
	{
		var scroll = new ScrollContainer { Name = "Connect an agent", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		body.AddThemeConstantOverride("separation", 10);
		scroll.AddChild(body);
		body.AddChild(Text("Use the AI agent you already have. It connects to this Creator window and gets tools to inspect, build, script, capture and playtest your game. No sign-in or API key is needed in Creator.", 13, Muted, true));
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 10);
		row.AddChild(Text("Agent", 14));
		_agentPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		foreach (var agent in _agentsList) _agentPicker.AddItem(agent.Name);
		_agentPicker.ItemSelected += _ => ShowAgent();
		row.AddChild(_agentPicker);
		body.AddChild(row);
		_agentInstructions = Text("", 14, null, true);
		body.AddChild(_agentInstructions);
		_snippet = new TextEdit
		{
			Editable = false, CustomMinimumSize = new Vector2(0, 180), WrapMode = TextEdit.LineWrappingMode.Boundary,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ContextMenuEnabled = true
		};
		_snippet.AddThemeFontOverride("font", Monospace());
		_snippet.AddThemeFontSizeOverride("font_size", 13);
		body.AddChild(_snippet);
		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 10);
		var copy = new Button { Text = "Copy", CustomMinimumSize = new Vector2(90, 0) };
		copy.Pressed += () => { DisplayServer.ClipboardSet(_snippet.Text); _footer.Text = "Copied to the clipboard"; };
		actions.AddChild(copy);
		_projectButton = new Button { Text = "Add to this project" };
		_projectButton.Pressed += AddToProject;
		actions.AddChild(_projectButton);
		body.AddChild(actions);
		_projectNote = Text("", 12, Muted, true);
		body.AddChild(_projectNote);
		body.AddChild(new HSeparator());
		body.AddChild(Text("Tips", 14));
		body.AddChild(Text("• Start your agent in the project folder so it can also read other project files.\n"
			+ "• Ask for something concrete, like \"build a lobby with a spawn area and a scoreboard UI, then playtest it\".\n"
			+ "• Every change an agent makes is one step in Creator's undo history. Watch it work in the Activity tab.\n"
			+ "• Agents see everything you send them, including scripts and captures; your agent's provider policies apply.", 13, Muted, true));
		ShowAgent();
		return scroll;
	}

	private Control BuildActivityTab()
	{
		var body = new VBoxContainer { Name = "Activity", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		body.AddThemeConstantOverride("separation", 10);
		body.AddChild(Text("Connected agents", 14));
		_agents = new VBoxContainer();
		_agents.AddThemeConstantOverride("separation", 2);
		body.AddChild(_agents);
		var feedHeader = new HBoxContainer();
		var feedTitle = Text("Tool calls", 14);
		feedTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		feedHeader.AddChild(feedTitle);
		var clear = new Button { Text = "Clear", Flat = true };
		clear.Pressed += () => _host?.Activity.Clear();
		feedHeader.AddChild(clear);
		body.AddChild(feedHeader);
		_feedScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		_feed = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_feed.AddThemeConstantOverride("separation", 6);
		_feedScroll.AddChild(_feed);
		body.AddChild(_feedScroll);
		return body;
	}

	private void Refresh()
	{
		if (!IsInsideTree()) return;
		var settings = CreatorSettingsService.Instance;
		_syncing = true;
		bool fromCommandLine = _host?.StartedFromCommandLine == true;
		_enabled.ButtonPressed = fromCommandLine || settings.Get<bool>(CreatorSettingKeys.Gark.McpEnabled);
		_enabled.Disabled = fromCommandLine;
		_enabled.TooltipText = fromCommandLine ? "Creator was started with --gark-mcp-serve, so the server stays on for this session" : "Run the MCP server whenever Creator is open";
		_port.Editable = !fromCommandLine;
		_allowEdits.ButtonPressed = settings.Get<bool>(CreatorSettingKeys.Gark.McpAllowEdits);
		if (!_port.HasFocus()) _port.Text = (fromCommandLine ? Port : GarkMcpHost.ConfiguredPort).ToString();
		_syncing = false;
		if (_host == null)
		{
			SetStatus(Warn, "The MCP server is unavailable in this window.");
		}
		else if (_host.Error != null) SetStatus(Warn, _host.Error);
		else if (_host.Running)
		{
			int count = ActiveSessions().Count();
			SetStatus(Good, $"Running at {_host.Url}" + (count > 0 ? $" · {count} agent{(count == 1 ? "" : "s")} connected" : " · waiting for an agent"));
		}
		else SetStatus(Muted, "Off. Turn the server on to let AI agents on this computer work in Creator.");
		_copyUrl.Disabled = _host?.Running != true;
		_disconnect.Disabled = _host?.Running != true;
		ShowAgent();
		RefreshAgents();
	}

	private void SetStatus(Color color, string text)
	{
		_statusDot.AddThemeColorOverride("font_color", color);
		_statusText.Text = text;
	}

	private void ApplyPort()
	{
		if (_syncing) return;
		if (!int.TryParse(_port.Text.Trim(), out int port) || port is < 1024 or > 65535)
		{
			_footer.Text = "Choose a port from 1024 to 65535.";
			_port.Text = GarkMcpHost.ConfiguredPort.ToString();
			return;
		}
		if (port != GarkMcpHost.ConfiguredPort)
		{
			CreatorSettingsService.Instance.Set(CreatorSettingKeys.Gark.McpPort, port);
			_footer.Text = "Port changed. Update the address in your agent's settings.";
		}
	}

	private void OnSettingChanged(SettingChangedEvent change)
	{
		if (change.Key.StartsWith("gark.", StringComparison.Ordinal)) Refresh();
	}

	private IEnumerable<GarkMcpSession> ActiveSessions() => (_host?.Sessions ?? [])
		.Where(s => !s.Streams.IsEmpty || DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(30))
		.OrderByDescending(s => s.LastSeen);

	private void RefreshAgents()
	{
		if (_agents == null) return;
		foreach (Node child in _agents.GetChildren()) { _agents.RemoveChild(child); child.QueueFree(); }
		var sessions = ActiveSessions().Take(12).ToArray();
		if (sessions.Length == 0)
		{
			_agents.AddChild(Text(_host?.Running == true ? "No agent has connected yet. Pick your agent on the Connect tab." : "The server is off.", 13, Muted, true));
			return;
		}
		foreach (var session in sessions)
		{
			var ago = DateTime.UtcNow - session.LastSeen;
			string seen = !session.Running.IsEmpty ? "working now" : ago.TotalSeconds < 60 ? "active now" : $"{(int)ago.TotalMinutes} min ago";
			_agents.AddChild(Text($"● {session.Label}  ·  {session.Calls} tool call{(session.Calls == 1 ? "" : "s")}  ·  {seen}", 13, !session.Running.IsEmpty || ago.TotalSeconds < 60 ? Good : Muted));
		}
	}

	private void AddCard(GarkMcpActivity.Entry entry)
	{
		if (!IsInsideTree()) return;
		bool atBottom = _feedScroll.ScrollVertical >= _feedScroll.GetVScrollBar().MaxValue - _feedScroll.GetVScrollBar().Page - 40;
		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", Style(new Color(0.15f, 0.16f, 0.18f), 8));
		var card = new VBoxContainer();
		panel.AddChild(card);
		var toggle = new Button { Flat = true, Alignment = HorizontalAlignment.Left, ToggleMode = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
		var detail = new RichTextLabel { FitContent = true, ScrollActive = false, SelectionEnabled = true, Visible = false, BbcodeEnabled = false };
		detail.AddThemeFontOverride("normal_font", Monospace());
		detail.AddThemeFontSizeOverride("normal_font_size", 12);
		toggle.Toggled += expanded => detail.Visible = expanded;
		card.AddChild(toggle);
		card.AddChild(detail);
		_feed.AddChild(panel);
		_cards[entry.Id] = (toggle, detail, card);
		while (_feed.GetChildCount() > 100)
		{
			var oldest = _feed.GetChild(0);
			_feed.RemoveChild(oldest);
			oldest.QueueFree();
			foreach (var key in _cards.Where(c => c.Value.Card.GetParent() == oldest).Select(c => c.Key).ToArray()) _cards.Remove(key);
		}
		UpdateCard(entry);
		if (atBottom) Callable.From(() => { if (IsInstanceValid(_feedScroll)) _feedScroll.ScrollVertical = (int)_feedScroll.GetVScrollBar().MaxValue; }).CallDeferred();
	}

	private void UpdateCard(GarkMcpActivity.Entry entry)
	{
		if (!_cards.TryGetValue(entry.Id, out var card)) return;
		string state = !entry.Done ? "…" : entry.Failed ? "✗" : "✓";
		string time = entry.Duration is { } d ? (d.TotalSeconds < 1 ? $"{d.TotalMilliseconds:0} ms" : $"{d.TotalSeconds:0.0} s") : "running";
		card.Toggle.Text = $"{state}  {entry.Title}   ·   {entry.Client}   ·   {entry.Started:HH:mm:ss}   ·   {time}";
		card.Toggle.AddThemeColorOverride("font_color", !entry.Done ? Muted : entry.Failed ? Warn : new Color(0.88f, 0.9f, 0.93f));
		card.Detail.Text = "Arguments\n" + entry.Arguments + (entry.Result != null ? "\n\n" + (entry.Failed ? "Error\n" : "Result\n") + entry.Result : "");
		if (entry.Done && entry.Images.Count > 0 && card.Card.GetChildCount() == 2)
		{
			var images = new HFlowContainer();
			foreach (var picture in entry.Images.Take(6))
			{
				using var image = new Image();
				var loaded = picture.MimeType == "image/jpeg" ? image.LoadJpgFromBuffer(picture.Data) : image.LoadPngFromBuffer(picture.Data);
				if (loaded != Error.Ok) continue;
				images.AddChild(new TextureRect
				{
					Texture = ImageTexture.CreateFromImage(image), CustomMinimumSize = new Vector2(entry.Images.Count == 1 ? 360 : 220, entry.Images.Count == 1 ? 200 : 124),
					ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
				});
			}
			card.Card.AddChild(images);
		}
	}

	private void ClearCards()
	{
		foreach (Node child in _feed.GetChildren()) { _feed.RemoveChild(child); child.QueueFree(); }
		_cards.Clear();
	}

	// Connection instructions

	private Agent[] BuildAgents()
	{
		static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		JsonObject Servers(string key, JsonObject entry) => new() { [key] = new JsonObject { ["polytoria"] = entry } };
		JsonObject Merge(JsonObject existing, string key, JsonObject entry)
		{
			if (existing[key] is not JsonObject servers) existing[key] = servers = new JsonObject();
			servers["polytoria"] = entry;
			return existing;
		}
		return
		[
			new("Claude Code",
				"Run this in a terminal. Add --scope user to make it available in every project, or use \"Add to this project\" to write the project's .mcp.json.",
				() => $"claude mcp add --transport http polytoria {Url}",
				".mcp.json", existing => Merge(existing, "mcpServers", new JsonObject { ["type"] = "http", ["url"] = Url })),
			new("Codex CLI / Codex app",
				"Run the first line in a terminal, or add the TOML block to ~/.codex/config.toml. A longer tool timeout gives playtests room to run.",
				() => $"codex mcp add polytoria --url {Url}\n\n[mcp_servers.polytoria]\nurl = \"{Url}\"\ntool_timeout_sec = 600"),
			new("Cursor",
				"Add this to .cursor/mcp.json in the project (or ~/.cursor/mcp.json for every project), then enable \"polytoria\" under Settings → MCP.",
				() => Json(Servers("mcpServers", new JsonObject { ["url"] = Url })),
				".cursor/mcp.json", existing => Merge(existing, "mcpServers", new JsonObject { ["url"] = Url })),
			new("VS Code (Copilot agent mode)",
				"Add this to .vscode/mcp.json in the project, or run \"MCP: Add Server\" and choose HTTP.",
				() => Json(Servers("servers", new JsonObject { ["type"] = "http", ["url"] = Url })),
				".vscode/mcp.json", existing => Merge(existing, "servers", new JsonObject { ["type"] = "http", ["url"] = Url })),
			new("Claude Desktop",
				"Claude Desktop starts local servers as commands. Open Settings → Developer → Edit Config and add this to claude_desktop_config.json, then restart Claude Desktop. Creator must be open with the server on.",
				() => Json(Servers("mcpServers", BridgeCommand()))),
			new("Gemini CLI",
				"Add this to ~/.gemini/settings.json, or to .gemini/settings.json in the project.",
				() => Json(Servers("mcpServers", new JsonObject { ["httpUrl"] = Url, ["timeout"] = 600000 })),
				".gemini/settings.json", existing => Merge(existing, "mcpServers", new JsonObject { ["httpUrl"] = Url, ["timeout"] = 600000 })),
			new("Windsurf",
				"Add this to ~/.codeium/windsurf/mcp_config.json (Windsurf Settings → Cascade → MCP servers → View raw config).",
				() => Json(Servers("mcpServers", new JsonObject { ["serverUrl"] = Url }))),
			new("Other agents",
				"Any MCP client works. Use Streamable HTTP if it is supported, the older SSE endpoint for clients that need it, or the stdio command for clients that only launch programs.",
				() => $"Streamable HTTP:  {Url}\nSSE (older clients):  http://127.0.0.1:{Port}/sse\nstdio command:  {string.Join(" ", new[] { BridgeCommand()["command"]!.ToString() }.Concat(BridgeCommand()["args"]!.AsArray().Select(a => a!.ToString())).Select(Quote))}"),
		];
	}

	/// <summary>Launches this Creator executable as a stdio bridge to the running server.</summary>
	private static JsonObject BridgeCommand()
	{
		var args = new JsonArray();
		if (Globals.IsInGDEditor) { args.Add((JsonNode?)"--path"); args.Add((JsonNode?)ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\')); }
		foreach (string arg in new[] { "--headless", "--quiet", "--gark-mcp" }) args.Add((JsonNode?)arg);
		return new JsonObject { ["command"] = OS.GetExecutablePath(), ["args"] = args };
	}
	private static string Quote(string arg) => arg.Contains(' ') ? "\"" + arg + "\"" : arg;

	private void ShowAgent()
	{
		if (_agentPicker == null || _agentsList.Length == 0) return;
		var agent = _agentsList[Math.Clamp(_agentPicker.Selected, 0, _agentsList.Length - 1)];
		_agentInstructions.Text = agent.Instructions;
		_snippet.Text = agent.Snippet();
		var session = CreatorService.CurrentSession;
		_projectButton.Visible = agent.ProjectFile != null;
		_projectButton.Disabled = session == null;
		_projectButton.Text = agent.ProjectFile != null ? $"Add to this project ({agent.ProjectFile})" : "";
		_projectNote.Text = agent.ProjectFile == null ? "" : session == null ? "Open a project to add the configuration file to it."
			: $"Writes {agent.ProjectFile} in {session.ProjectFolderPath}, keeping any other servers already listed there.";
	}

	private void AddToProject()
	{
		var agent = _agentsList[Math.Clamp(_agentPicker.Selected, 0, _agentsList.Length - 1)];
		var session = CreatorService.CurrentSession;
		if (agent.ProjectFile == null || agent.Merge == null || session == null) return;
		string path = Path.Combine(session.ProjectFolderPath, agent.ProjectFile.Replace('/', Path.DirectorySeparatorChar));
		try
		{
			JsonObject existing = new();
			if (File.Exists(path))
			{
				string text = File.ReadAllText(path);
				if (text.Trim() != "")
					existing = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
						?? throw new JsonException("The file does not contain a JSON object.");
			}
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, agent.Merge(existing).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
			_footer.Text = $"Saved {agent.ProjectFile}. Start (or restart) {agent.Name} in the project folder.";
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			_footer.Text = $"Could not update {agent.ProjectFile}: {ex.Message}";
		}
	}
}
