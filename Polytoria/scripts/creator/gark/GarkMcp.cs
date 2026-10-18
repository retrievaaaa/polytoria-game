// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Polytoria.Creator.Gark;

/// <summary>One connected MCP client (an agent session).</summary>
internal sealed class GarkMcpSession(string id, string transport)
{
	internal string Id { get; } = id;
	internal string Transport { get; } = transport;
	internal string ProtocolVersion { get; set; } = GarkMcpProtocol.LatestVersion;
	internal string ClientName { get; set; } = "MCP client";
	internal string? ClientVersion { get; set; }
	internal DateTime Started { get; } = DateTime.UtcNow;
	internal DateTime LastSeen { get; set; } = DateTime.UtcNow;
	internal int Calls;
	internal readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new();
	/// <summary>Open server-to-client streams (legacy SSE, or GET on the streamable endpoint).</summary>
	internal readonly ConcurrentDictionary<Guid, Channel<JsonObject>> Streams = new();
	internal string Label => ClientVersion == null ? ClientName : $"{ClientName} {ClientVersion}";

	internal void Push(JsonObject message)
	{
		foreach (var stream in Streams.Values) stream.Writer.TryWrite(message);
	}

	internal void CancelAll()
	{
		foreach (var running in Running.Values)
		{
			try { running.Cancel(); }
			catch (ObjectDisposedException) { }
		}
	}
}

internal interface IGarkMcpHost
{
	bool Online { get; }
	bool AllowEdits { get; }
	/// <summary>Runs a tool in Creator. <paramref name="record"/> is false for calls made to serve resources.</summary>
	Task<GarkToolResult> CallToolAsync(GarkMcpSession session, GarkToolDefinition tool, JsonObject args, Action<string> progress, bool record, CancellationToken token);
}

internal sealed class GarkMcpException(int code, string message) : Exception(message)
{
	internal int Code { get; } = code;
}

/// <summary>Transport-independent MCP (JSON-RPC 2.0) message handling.</summary>
internal sealed class GarkMcpProtocol(IGarkMcpHost host)
{
	internal const string LatestVersion = "2025-11-25";
	internal static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
	internal const string ServerName = "polytoria-creator";
	internal static string Version => Globals.AppVersion;

	private static readonly (string Uri, string Name, string Title, string Description, string MimeType)[] StaticResources =
	[
		("polytoria://guide/workflow", "workflow", "Gark workflow", "How to use the Polytoria Creator tools well.", "text/markdown"),
		("polytoria://guide/coordinates", "coordinates", "Polytoria coordinates and rotations", "Y-up axes, +Z forward, degrees, world vs local transforms, primitive axes.", "text/markdown"),
		("polytoria://guide/ui", "ui", "Polytoria UI layout", "PlayerGUI hierarchy and the Offset/Relative/Pivot layout model.", "text/markdown"),
		("polytoria://api/polytoria.d.luau", "luau-api", "Polytoria Luau API definitions", "Complete Luau type definitions for Polytoria scripting: globals, enums and every class with its properties, methods and events.", "text/x-luau"),
		("polytoria://context", "context", "Creator context", "Live project, world, selection, camera and playtest state.", "application/json"),
	];

	private static readonly (string Name, string Title, string Description, (string Name, string Description, bool Required)[] Arguments)[] Prompts =
	[
		("gark", "Build with Gark", "Work on the open Polytoria project: build, script, test and verify.", [("task", "What to make or change.", true)]),
		("explain_project", "Explain this project", "Survey the open project's worlds, objects and scripts and summarize how it works.", []),
		("fix_errors", "Find and fix errors", "Run diagnostics and a playtest, then fix script and runtime errors.", [("focus", "Optional area or script to concentrate on.", false)]),
	];

	internal static JsonObject Request(JsonNode? id, string method, JsonObject? parameters = null)
	{
		var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["method"] = method };
		if (parameters != null) message["params"] = parameters;
		return message;
	}
	internal static JsonObject Notification(string method, JsonObject? parameters = null)
	{
		var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
		if (parameters != null) message["params"] = parameters;
		return message;
	}
	internal static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
	internal static JsonObject Error(JsonNode? id, int code, string message) => new()
	{
		["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
	};

	internal static bool IsRequest(JsonObject message) => message["method"] != null && message.ContainsKey("id") && message["id"] != null;
	internal static string RequestKey(JsonNode? id) => id?.ToJsonString() ?? "null";

	internal static string Negotiate(string? requested) => requested != null && SupportedVersions.Contains(requested) ? requested : LatestVersion;

	internal static JsonObject InitializeResult(string version) => new()
	{
		["protocolVersion"] = version,
		["capabilities"] = new JsonObject
		{
			["tools"] = new JsonObject { ["listChanged"] = true },
			["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false },
			["prompts"] = new JsonObject { ["listChanged"] = false },
			["logging"] = new JsonObject(),
		},
		["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Polytoria Creator (Gark)", ["version"] = Version },
		["instructions"] = GarkGuidance.Instructions
	};

	/// <summary>Handles one message. Returns the response for a request, or null for notifications and client responses.</summary>
	internal async Task<JsonObject?> HandleAsync(GarkMcpSession session, JsonObject message, Action<JsonObject> notify, CancellationToken cancel)
	{
		session.LastSeen = DateTime.UtcNow;
		if (message["method"]?.GetValueKind() != JsonValueKind.String) return message.ContainsKey("id") && message["result"] == null && message["error"] == null
			? Error(message["id"], -32600, "Invalid request: method is required.") : null;
		string method = message["method"]!.GetValue<string>();
		JsonNode? id = message["id"];
		bool isRequest = IsRequest(message);
		var parameters = message["params"] as JsonObject ?? new JsonObject();
		try
		{
			JsonNode? result = method switch
			{
				"initialize" => Initialize(session, parameters),
				"ping" => new JsonObject(),
				"tools/list" => ToolsList(session),
				"tools/call" => await CallTool(session, id, parameters, notify, cancel),
				"resources/list" => await ResourcesList(session, cancel),
				"resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray((JsonNode)new JsonObject
				{
					["uriTemplate"] = "polytoria://scripts/{+path}", ["name"] = "script", ["title"] = "Project script",
					["description"] = "A project Luau script, including unsaved editor changes.", ["mimeType"] = "text/x-luau"
				}) },
				"resources/read" => await ReadResource(session, parameters, cancel),
				"prompts/list" => PromptsList(),
				"prompts/get" => GetPrompt(parameters),
				"logging/setLevel" => new JsonObject(),
				"completion/complete" => new JsonObject { ["completion"] = new JsonObject { ["values"] = new JsonArray(), ["hasMore"] = false } },
				"notifications/cancelled" => Cancel(session, parameters),
				_ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
				_ => throw new GarkMcpException(-32601, "Method not found: " + method)
			};
			return isRequest ? Result(id, result ?? new JsonObject()) : null;
		}
		catch (GarkMcpException ex) { return isRequest ? Error(id, ex.Code, ex.Message) : null; }
		catch (OperationCanceledException) { return isRequest ? Error(id, -32800, "Request cancelled.") : null; }
		catch (Exception ex) { return isRequest ? Error(id, -32603, ex.GetBaseException().Message) : null; }
	}

	private static JsonObject Initialize(GarkMcpSession session, JsonObject parameters)
	{
		session.ProtocolVersion = Negotiate(parameters["protocolVersion"]?.GetValue<string>());
		if (parameters["clientInfo"] is JsonObject client)
		{
			session.ClientName = client["title"]?.GetValue<string>() ?? client["name"]?.GetValue<string>() ?? session.ClientName;
			session.ClientVersion = client["version"]?.GetValue<string>();
		}
		return InitializeResult(session.ProtocolVersion);
	}

	private static JsonNode? Cancel(GarkMcpSession session, JsonObject parameters)
	{
		if (session.Running.TryGetValue(RequestKey(parameters["requestId"]), out var running))
		{
			try { running.Cancel(); }
			catch (ObjectDisposedException) { }
		}
		return null;
	}

	internal static JsonObject ToolJson(GarkToolDefinition tool) => new()
	{
		["name"] = tool.Name,
		["title"] = tool.Title,
		["description"] = tool.Description,
		["inputSchema"] = tool.InputSchema.DeepClone(),
		["annotations"] = new JsonObject
		{
			["title"] = tool.Title,
			["readOnlyHint"] = tool.ReadOnly,
			["destructiveHint"] = tool.Flags.HasFlag(GarkToolFlags.Destructive),
			["idempotentHint"] = tool.ReadOnly || tool.Flags.HasFlag(GarkToolFlags.Idempotent),
			["openWorldHint"] = tool.Flags.HasFlag(GarkToolFlags.Network)
		}
	};

	private JsonObject ToolsList(GarkMcpSession session) => new()
	{
		["tools"] = new JsonArray(GarkTools.Definitions.Where(t => host.AllowEdits || !t.EditsProject).Select(t => (JsonNode)ToolJson(t)).ToArray())
	};

	private async Task<JsonObject> CallTool(GarkMcpSession session, JsonNode? id, JsonObject parameters, Action<JsonObject> notify, CancellationToken cancel)
	{
		string name = parameters["name"]?.GetValueKind() == JsonValueKind.String ? parameters["name"]!.GetValue<string>() : throw new GarkMcpException(-32602, "tools/call needs a tool name.");
		var tool = GarkTools.Find(name) ?? throw new GarkMcpException(-32602, "Unknown tool: " + name);
		JsonObject args;
		try
		{
			args = parameters["arguments"] switch
			{
				null => new JsonObject(),
				JsonObject obj => (JsonObject)obj.DeepClone(),
				// Some clients send arguments as a JSON string.
				JsonValue value when value.TryGetValue<string>(out var text) => text.Trim() == "" ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("arguments must be an object."),
				_ => throw new ArgumentException("arguments must be an object.")
			};
		}
		catch (Exception ex) when (ex is ArgumentException or JsonException) { return ErrorContent(ex.Message); }
		if (!host.Online) return ErrorContent("Polytoria Creator is not running, or its Gark MCP server is turned off. Open Creator, click Gark in the toolbar and turn the server on, then retry.");
		if (tool.EditsProject && !host.AllowEdits)
			return ErrorContent("Editing is turned off for agents in Creator (Gark → Allow edits). Read-only tools still work; ask the user to allow edits.");

		Interlocked.Increment(ref session.Calls);
		using var request = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		string key = RequestKey(id);
		session.Running[key] = request;
		var progressToken = parameters["_meta"]?["progressToken"]?.DeepClone();
		string stage = "Running " + tool.Title;
		int beat = 0;
		void SendProgress()
		{
			if (progressToken == null) return;
			notify(Notification("notifications/progress", new JsonObject
			{
				["progressToken"] = progressToken.DeepClone(), ["progress"] = ++beat, ["message"] = stage
			}));
		}
		using var heartbeat = new Timer(_ => SendProgress(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
		try
		{
			var result = await host.CallToolAsync(session, tool, args, message => { stage = message; SendProgress(); }, true, request.Token);
			return ToolContent(result);
		}
		catch (OperationCanceledException)
		{
			return ErrorContent("Cancelled before the tool finished. Edits that completed are in Creator's undo history; anything in progress was rolled back.");
		}
		catch (Exception ex)
		{
			var error = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
			return ErrorContent(error.Message);
		}
		finally { session.Running.TryRemove(key, out _); }
	}

	internal static JsonObject ToolContent(GarkToolResult result)
	{
		var content = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = result.Data.ToJsonString() });
		foreach (var image in result.Images ?? [])
			content.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Data), ["mimeType"] = image.MimeType });
		return new JsonObject { ["content"] = content, ["isError"] = false };
	}

	internal static JsonObject ErrorContent(string message) => new()
	{
		["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = message }),
		["isError"] = true
	};

	private async Task<JsonObject> ResourcesList(GarkMcpSession session, CancellationToken cancel)
	{
		var resources = new JsonArray(StaticResources.Select(r => (JsonNode)new JsonObject
		{
			["uri"] = r.Uri, ["name"] = r.Name, ["title"] = r.Title, ["description"] = r.Description, ["mimeType"] = r.MimeType
		}).ToArray());
		if (host.Online)
		{
			try
			{
				var scripts = await host.CallToolAsync(session, GarkTools.Find("list_scripts")!, new JsonObject { ["limit"] = 2000 }, _ => { }, false, cancel);
				foreach (var path in scripts.Data["scripts"]?.AsArray() ?? [])
				{
					string text = path!.GetValue<string>();
					resources.Add((JsonNode)new JsonObject { ["uri"] = "polytoria://scripts/" + text, ["name"] = text, ["title"] = text, ["mimeType"] = "text/x-luau" });
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException) { /* No project open: only the static resources apply. */ }
		}
		return new JsonObject { ["resources"] = resources };
	}

	private async Task<JsonObject> ReadResource(GarkMcpSession session, JsonObject parameters, CancellationToken cancel)
	{
		string uri = parameters["uri"]?.GetValue<string>() ?? throw new GarkMcpException(-32602, "resources/read needs a uri.");
		string text, mime = "text/markdown";
		switch (uri)
		{
			case "polytoria://guide/workflow": text = GarkGuidance.Workflow; break;
			case "polytoria://guide/coordinates": text = GarkGuidance.Coordinates; break;
			case "polytoria://guide/ui": text = GarkGuidance.UI; break;
			case "polytoria://api/polytoria.d.luau":
				text = GarkTools.LuauDefinitions;
				mime = "text/x-luau";
				break;
			case "polytoria://context":
				text = (await LiveTool(session, "get_context", new JsonObject(), cancel)).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
				mime = "application/json";
				break;
			default:
				if (!uri.StartsWith("polytoria://scripts/", StringComparison.Ordinal)) throw new GarkMcpException(-32002, "Resource not found: " + uri);
				var script = await LiveTool(session, "read_script", new JsonObject
				{
					["path"] = Uri.UnescapeDataString(uri["polytoria://scripts/".Length..]), ["numbered"] = false, ["line_count"] = 1_000_000
				}, cancel);
				text = script["text"]!.GetValue<string>();
				mime = "text/x-luau";
				break;
		}
		return new JsonObject { ["contents"] = new JsonArray((JsonNode)new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }) };
	}

	private async Task<JsonNode> LiveTool(GarkMcpSession session, string name, JsonObject args, CancellationToken cancel)
	{
		if (!host.Online) throw new GarkMcpException(-32002, "Polytoria Creator is not running; this resource needs an open project.");
		try { return (await host.CallToolAsync(session, GarkTools.Find(name)!, args, _ => { }, false, cancel)).Data; }
		catch (Exception ex) when (ex is not OperationCanceledException and not GarkMcpException) { throw new GarkMcpException(-32002, ex.Message); }
	}

	private static JsonObject PromptsList() => new()
	{
		["prompts"] = new JsonArray(Prompts.Select(p => (JsonNode)new JsonObject
		{
			["name"] = p.Name, ["title"] = p.Title, ["description"] = p.Description,
			["arguments"] = new JsonArray(p.Arguments.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["description"] = a.Description, ["required"] = a.Required }).ToArray())
		}).ToArray())
	};

	private static JsonObject GetPrompt(JsonObject parameters)
	{
		string name = parameters["name"]?.GetValue<string>() ?? throw new GarkMcpException(-32602, "prompts/get needs a name.");
		var args = parameters["arguments"] as JsonObject ?? new JsonObject();
		string Arg(string key) => args[key]?.ToString() ?? "";
		string text = name switch
		{
			"gark" => Arg("task").Trim() == "" ? throw new GarkMcpException(-32602, "The gark prompt needs a task.")
				: "Use the Polytoria Creator tools to do this in the open project:\n\n" + Arg("task") + "\n\n"
				+ "Work like an experienced Polytoria developer. Start with get_context, inspect what exists, then build. "
				+ "Verify visually with capture_viewport and behaviourally with a playtest (start_playtest, playtest_input, playtest_eval, capture_playtest, playtest_logs), fix what you find, and finish with a short summary of the changes.",
			"explain_project" => "Survey the open Polytoria project with the Creator tools: get_context, list_instances for each top-level service, list_scripts and read the important scripts. "
				+ "Capture the main areas with capture_viewport. Then explain how the game is put together, what each script does, and anything that looks broken or unfinished. Do not change anything.",
			"fix_errors" => "Find and fix errors in the open Polytoria project" + (Arg("focus").Trim() == "" ? "" : " (focus: " + Arg("focus") + ")") + ". "
				+ "Run script_diagnostics on all scripts, start a playtest and read playtest_logs for runtime errors, and use playtest_eval to probe live state. "
				+ "Fix each problem with patch_script, re-run diagnostics and restart the playtest until it is clean, then summarize what you fixed.",
			_ => throw new GarkMcpException(-32602, "Unknown prompt: " + name)
		};
		return new JsonObject
		{
			["description"] = Prompts.First(p => p.Name == name).Description,
			["messages"] = new JsonArray((JsonNode)new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } })
		};
	}
}
