// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Attributes;
using Polytoria.Creator.UI;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Datamodel.Interfaces;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using File = System.IO.File;
using Instance = Polytoria.Datamodel.Instance;

namespace Polytoria.Creator.Gark;

internal sealed record GarkImage(byte[] Data, string MimeType = "image/png");

internal sealed record GarkToolResult(JsonNode Data, IReadOnlyList<GarkImage>? Images = null)
{
	internal GarkToolResult(JsonNode data, byte[] png) : this(data, [new GarkImage(png)]) { }
	internal byte[]? Image => Images is { Count: > 0 } images ? images[0].Data : null;
}

[Flags]
internal enum GarkToolFlags
{
	/// <summary>Reads the project or test without changing anything.</summary>
	Read = 0,
	/// <summary>Changes the project (world, scripts, saved files). Hidden when edits are disabled.</summary>
	Edit = 1,
	/// <summary>Changes editor or playtest state, not project data.</summary>
	Control = 2,
	Destructive = 4,
	Idempotent = 8,
	/// <summary>Talks to the public Polytoria Toolbox.</summary>
	Network = 16,
}

internal sealed record GarkToolDefinition(string Name, string Title, string Description, JsonObject InputSchema, GarkToolFlags Flags)
{
	internal bool ReadOnly => (Flags & (GarkToolFlags.Edit | GarkToolFlags.Control)) == 0;
	internal bool EditsProject => Flags.HasFlag(GarkToolFlags.Edit);
}

internal delegate void GarkToolAdder(string name, string title, GarkToolFlags flags, string description, string properties = "{}", params string[] required);

/// <summary>
/// The Polytoria Creator tool surface. Runs on Godot's main thread; each call is pinned to the
/// project and world that were active when it started.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
internal sealed partial class GarkTools(CreatorSession? session, World? world)
{
	private static readonly Type[] Classes = typeof(Instance).Assembly.GetTypes()
		.Where(t => typeof(Instance).IsAssignableFrom(t) && t.IsDefined(typeof(InstantiableAttribute), false)
			&& !t.IsAbstract && !t.IsDefined(typeof(ExplorerExcludeAttribute), false)).OrderBy(t => t.Name).ToArray();
	private static readonly Dictionary<string, Type> TypesByName = typeof(Instance).Assembly.GetTypes()
		.Where(t => typeof(NetworkedObject).IsAssignableFrom(t) || (t.IsValueType && typeof(IData).IsAssignableFrom(t)))
		.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

	internal static GarkTools Current() => new(CreatorService.CurrentSession, World.Current);

	/// <summary>Optional stage updates for long calls, forwarded to MCP progress notifications.</summary>
	internal Action<string>? Progress { get; init; }

	internal static IReadOnlyList<GarkToolDefinition> Definitions { get; } = BuildDefinitions();
	private static readonly Dictionary<string, GarkToolDefinition> DefinitionsByName = Definitions.ToDictionary(d => d.Name);
	internal static GarkToolDefinition? Find(string name) => DefinitionsByName.GetValueOrDefault(name);

	private const string Vec3 = """{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3}""";
	private const string IdArray = """{"type":"array","items":{"type":"string"},"description":"Instance IDs (or /paths)."}""";

	private static List<GarkToolDefinition> BuildDefinitions()
	{
		var tools = new List<GarkToolDefinition>();
		void Add(string name, string title, GarkToolFlags flags, string description, string properties = "{}", params string[] required)
		{
			var schema = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(properties) };
			if (required.Length > 0) schema["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
			tools.Add(new(name, title, description, schema, flags));
		}

		// Context and discovery
		Add("get_context", "Get Creator context", GarkToolFlags.Read,
			"Active project (name, absolute folder), current world, open and project worlds, selection, editor camera, undo/redo state and playtest status. Call first, and again if the user may have switched worlds.");
		Add("get_creator_guide", "Read Polytoria guide", GarkToolFlags.Read,
			"Verified Polytoria conventions: Y-up coordinates, +Z object forward vs -Z camera forward, degrees, world vs local transforms, primitive axes, and the UI layout model. Same text as the server instructions.",
			"""{"topic":{"type":"string","enum":["workflow","coordinates","ui","all"]}}""");
		Add("list_instances", "List instances", GarkToolFlags.Read,
			"Browse or search the scene tree under a parent. Filters: query (name/class substring), class_name (includes subclasses). include_properties adds chosen property values (or [\"*\"] for all) to each row. Paginated; returns total.",
			"""{"parent":{"type":"string","description":"ID or /path. Default: world root."},"depth":{"type":"integer","description":"Levels below parent. Default 2."},"query":{"type":"string"},"class_name":{"type":"string"},"include_properties":{"type":"array","items":{"type":"string"}},"offset":{"type":"integer"},"limit":{"type":"integer","description":"Default 100, max 5000."}}""");
		Add("inspect_instance", "Inspect instances", GarkToolFlags.Read,
			"Read every editable property (including references, assets, ranges and series), children, bounds and linked script path of one instance (id) or many (ids).",
			"""{"id":{"type":"string"},"ids":""" + IdArray + ""","child_limit":{"type":"integer","description":"Children listed per instance. Default 100."}}""");
		Add("class_schema", "Class schema", GarkToolFlags.Read,
			"Without class_name: every creatable class. With class_name: base classes, each editable property's type, JSON format, enum values and allowed asset classes, plus the Luau scripting API (properties, methods, events). Works for instance, asset and data classes.",
			"""{"class_name":{"type":"string"}}""");
		Add("search_api", "Search Luau API", GarkToolFlags.Read,
			"Search the bundled Polytoria Luau type definitions (globals, classes, methods, events, enums) with surrounding lines. Use before writing scripts; this is not Roblox's API.",
			"""{"query":{"type":"string"},"regex":{"type":"boolean"},"limit":{"type":"integer","description":"Default 12, max 200."},"context_lines":{"type":"integer","description":"Lines before/after each match. Default 3."}}""", "query");

		// World editing
		Add("create_instances", "Create instances", GarkToolFlags.Edit,
			"Create objects as one undo action. Each object: class_name, parent (ID, /path or \"@N\" = the Nth object created earlier in this call, so whole hierarchies fit in one call), optional name and properties. Default parent: /PlayerGUI for GUI, else /Environment. Use create_script for scripts.",
			"""{"objects":{"type":"array","items":{"type":"object","properties":{"class_name":{"type":"string"},"parent":{"type":"string"},"name":{"type":"string"},"properties":{"type":"object"}},"required":["class_name"]}}}""", "objects");
		Add("set_properties", "Set properties", GarkToolFlags.Edit | GarkToolFlags.Idempotent,
			"Edit properties as one undo action. Use {id, properties}, {ids, properties} to apply the same values to many objects, or {edits:[{id, properties}, ...]} for different values. Values: vectors [x,y,z], colors \"#RRGGBB\", enums as names, references as IDs, assets as {class_name, properties}; class_schema lists formats.",
			"""{"id":{"type":"string"},"ids":""" + IdArray + ""","properties":{"type":"object"},"edits":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"properties":{"type":"object"}},"required":["id","properties"]}}}""");
		Add("delete_instances", "Delete instances", GarkToolFlags.Edit | GarkToolFlags.Destructive,
			"Delete objects and their descendants as one undo action. The world root and services cannot be deleted.",
			"""{"ids":""" + IdArray + "}", "ids");
		Add("duplicate_instances", "Duplicate instances", GarkToolFlags.Edit,
			"Duplicate objects with their descendants (one undo action). Returns the copies, which become the selection.",
			"""{"ids":""" + IdArray + "}", "ids");
		Add("reparent_instances", "Reparent instances", GarkToolFlags.Edit | GarkToolFlags.Idempotent,
			"Move objects under a new parent as one undo action. Cycles are rejected.",
			"""{"ids":""" + IdArray + ""","parent":{"type":"string"}}""", "ids", "parent");
		Add("group_instances", "Group instances", GarkToolFlags.Edit,
			"Group objects into a new Model (default) or Folder, optionally named. One undo action.",
			"""{"ids":""" + IdArray + ""","as":{"type":"string","enum":["Model","Folder"]},"name":{"type":"string"}}""", "ids");
		Add("ungroup_instances", "Ungroup instances", GarkToolFlags.Edit,
			"Dissolve Models/Folders, keeping their children in the parent. One undo action.",
			"""{"ids":""" + IdArray + "}", "ids");
		Add("select_instances", "Select instances", GarkToolFlags.Control | GarkToolFlags.Idempotent,
			"Set the user's Explorer/viewport selection. An empty list clears it.",
			"""{"ids":""" + IdArray + "}", "ids");
		AddConstructionDefinitions(Add);

		// Visual inspection
		Add("capture_viewport", "Capture viewport", GarkToolFlags.Read,
			"Render the world and return images you can see, without moving the user's camera. Frame objects automatically with focus_ids and/or view presets (views returns one image per preset), or place the camera with position + look_at. Defaults to the editor camera. Screen UI is not drawn; use capture_playtest for that.",
			"""{"focus_ids":""" + IdArray + ""","view":{"type":"string","enum":["current","iso","front","back","left","right","top","bottom"]},"views":{"type":"array","items":{"type":"string","enum":["current","iso","front","back","left","right","top","bottom"]}},"position":""" + Vec3 + ""","look_at":""" + Vec3 + ""","fov":{"type":"number","description":"Vertical field of view in degrees."},"width":{"type":"integer","description":"Default 1024, max 3840."},"height":{"type":"integer","description":"Default 576, max 2160."},"format":{"type":"string","enum":["png","jpeg"]},"quality":{"type":"integer","description":"JPEG quality 1-100. Default 85."}}""");
		Add("focus_camera", "Focus editor camera", GarkToolFlags.Control,
			"Move the user's editor camera to frame objects (ids, optional view preset) or to position + look_at, so they can see a result.",
			"""{"ids":""" + IdArray + ""","view":{"type":"string","enum":["iso","front","back","left","right","top","bottom"]},"position":""" + Vec3 + ""","look_at":""" + Vec3 + "}");

		// Scripts
		Add("list_scripts", "List scripts", GarkToolFlags.Read,
			"List project Luau/Lua scripts (paths relative to the project folder). details adds editor state (open, unsaved) and the IDs of Script instances linked to each file.",
			"""{"query":{"type":"string","description":"Case-insensitive path filter."},"details":{"type":"boolean"},"offset":{"type":"integer"},"limit":{"type":"integer","description":"Default 500, max 10000."}}""");
		Add("read_script", "Read script", GarkToolFlags.Read,
			"Read a script with line numbers, including unsaved editor changes. Returns sha256 of the whole current text (needed by write_script), total_lines, unsaved flag and linked Script instance IDs.",
			"""{"path":{"type":"string"},"start_line":{"type":"integer","description":"1-based. Default 1."},"line_count":{"type":"integer","description":"Default 2000."},"numbered":{"type":"boolean","description":"Prefix lines with 'N: '. Default true."}}""", "path");
		Add("grep_scripts", "Search scripts", GarkToolFlags.Read,
			"Search all scripts, including unsaved editor buffers. Literal case-insensitive by default; regex optional. Returns path, line, text and optional context lines.",
			"""{"pattern":{"type":"string"},"regex":{"type":"boolean"},"case_sensitive":{"type":"boolean"},"path_filter":{"type":"string"},"context_lines":{"type":"integer"},"limit":{"type":"integer","description":"Default 200, max 5000."}}""", "pattern");
		Add("patch_script", "Patch script", GarkToolFlags.Edit,
			"Exact text replacement in a script: old_text/new_text, or edits:[{old_text,new_text,replace_all}] applied in order. Each old_text must match exactly once unless replace_all. Atomic, updates editor and disk, one undo action. expected_sha256 is optional extra protection.",
			"""{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"},"replace_all":{"type":"boolean"},"edits":{"type":"array","items":{"type":"object","properties":{"old_text":{"type":"string"},"new_text":{"type":"string"},"replace_all":{"type":"boolean"}},"required":["old_text","new_text"]}},"expected_sha256":{"type":"string"}}""", "path");
		Add("write_script", "Write script", GarkToolFlags.Edit | GarkToolFlags.Destructive,
			"Replace a whole existing script. expected_sha256 from read_script is required so newer edits are never overwritten. Updates editor and disk, one undo action.",
			"""{"path":{"type":"string"},"source":{"type":"string"},"expected_sha256":{"type":"string"}}""", "path", "source", "expected_sha256");
		Add("create_script", "Create script", GarkToolFlags.Edit,
			"Create a script file and a linked ServerScript/ClientScript/ModuleScript instance (type from the .server.luau/.client.luau/.module.luau suffix, or script_type). Omit source to link an existing file instead. Default parent /ScriptService.",
			"""{"path":{"type":"string"},"source":{"type":"string"},"parent":{"type":"string"},"name":{"type":"string"},"script_type":{"type":"string","enum":["server","client","module"]}}""", "path");
		Add("delete_script", "Delete script", GarkToolFlags.Edit | GarkToolFlags.Destructive,
			"Delete a script file and (by default) the Script instances linked to it. One undo action restores both.",
			"""{"path":{"type":"string"},"delete_instances":{"type":"boolean"}}""", "path");
		Add("script_diagnostics", "Script diagnostics", GarkToolFlags.Read,
			"Fresh Luau language-server errors and warnings, including unsaved buffers and required modules. One script (path) or all scripts paginated. Respects --!strict/--!nonstrict/--!nocheck and .luaurc. Clean diagnostics are not a runtime test.",
			"""{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer","description":"Scripts per page. Default 30."}}""");
		Add("open_script", "Open script in editor", GarkToolFlags.Control,
			"Open a script in the user's code editor, optionally at a line.",
			"""{"path":{"type":"string"},"line":{"type":"integer"}}""", "path");

		AddPlaytestDefinitions(Add);

		// History, saving and worlds
		Add("undo", "Undo", GarkToolFlags.Edit,
			"Undo the latest Creator history action(s), world or script. Returns what was undone. Only undo your own work unless the user asks.",
			"""{"steps":{"type":"integer","description":"Default 1."}}""");
		Add("redo", "Redo", GarkToolFlags.Edit,
			"Redo previously undone action(s). Returns what was redone.",
			"""{"steps":{"type":"integer","description":"Default 1."}}""");
		Add("save_world", "Save world", GarkToolFlags.Edit | GarkToolFlags.Idempotent,
			"Save the current world (or all open worlds) to the project. Does not publish.",
			"""{"all":{"type":"boolean"}}""");
		Add("switch_world", "Switch world", GarkToolFlags.Control,
			"Make another project world current, opening it if needed (paths from get_context.project_worlds). Instance IDs from the previous world no longer apply.",
			"""{"path":{"type":"string"}}""", "path");
		return tools;
	}

	internal async Task<GarkToolResult> ExecuteAsync(string name, JsonObject args, CancellationToken token)
	{
		var definition = Find(name) ?? throw new ArgumentException("Unknown tool: " + name);
		ValidateArguments(definition, args);
		CheckContext(token);
		switch (name)
		{
			case "get_context": return new(Context());
			case "get_creator_guide": return new(new JsonObject { ["guide"] = GarkGuidance.Topic(Text(args, "topic", "all")) });
			case "class_schema": return new(Schema(Text(args, "class_name", "")));
			case "search_api": return new(SearchApi(args));
		}
		RequireWorld();
		switch (name)
		{
			case "list_instances": return new(ListInstances(args));
			case "inspect_instance": return new(InspectInstances(args));
			case "create_instances": return new(CreateInstances(args));
			case "set_properties": return new(SetProperties(args));
			case "delete_instances":
				var deleting = Targets(args);
				var deletedIds = new JsonArray(deleting.Select(i => (JsonNode?)JsonValue.Create(i.ObjectID)).ToArray());
				world!.CreatorContext.History.DeleteInstances(deleting);
				return new(new JsonObject { ["deleted"] = deleting.Length, ["ids"] = deletedIds });
			case "duplicate_instances":
				world!.CreatorContext.History.DuplicateInstances(Targets(args));
				return new(new JsonObject { ["duplicates"] = SummaryArray(world.CreatorContext.Selections.SelectedInstances) });
			case "reparent_instances": return new(Reparent(args));
			case "group_instances": return new(Group(args));
			case "ungroup_instances":
				world!.CreatorContext.History.UngroupInstances(Targets(args));
				return new(new JsonObject { ["children"] = SummaryArray(world.CreatorContext.Selections.SelectedInstances) });
			case "select_instances":
				var selected = IdList(args, allowEmpty: true).Select(Resolve).ToArray();
				world!.CreatorContext.Selections.DeselectAll();
				foreach (var instance in selected) world.CreatorContext.Selections.Select(instance);
				return new(new JsonObject { ["selected"] = SummaryArray(selected) });
			case "list_scripts": return new(ListScripts(args));
			case "read_script": return new(ReadScript(args));
			case "grep_scripts": return new(GrepScripts(args));
			case "write_script": return new(WriteScript(args));
			case "patch_script": return new(PatchScript(args));
			case "create_script": return new(CreateScript(args));
			case "delete_script": return new(DeleteScript(args));
			case "open_script": return new(await OpenScript(args, token));
			case "script_diagnostics": return new(await ScriptDiagnostics(args, token));
			case "start_playtest": case "stop_playtest": case "inspect_playtest": case "playtest_input": case "capture_playtest": case "playtest_logs": case "playtest_eval":
				return await Playtest(name, args, token);
			case "capture_viewport": return await Capture(args, token);
			case "focus_camera": return new(FocusCamera(args));
			case "run_scene_code": return new(await RunSceneCode(args, token));
			case "get_bounds": return new(GetBounds(args));
			case "measure_distance": return new(MeasureDistance(args));
			case "overlap_box": return new(OverlapBox(args));
			case "raycast":
				await PhysicsReady(token);
				return new(Raycast(args, token));
			case "place_on_ground":
				await PhysicsReady(token);
				return new(new JsonObject { ["moved"] = PlaceOnGround(args, token) });
			case "align_instances": return new(new JsonObject { ["moved"] = AlignInstances(args) });
			case "search_assets": return new(await SearchAssets(args, token));
			case "inspect_asset": return await InspectAsset(args, token);
			case "import_asset": return new(await ImportAsset(args, token));
			case "undo": case "redo": return new(History(name == "undo", Int(args, "steps", 1, 1, 1000)));
			case "save_world": return new(Save(Flag(args, "all", false)));
			case "switch_world": return new(await SwitchWorld(args, token));
			default: throw new ArgumentException("Unknown tool: " + name);
		}
	}

	private static void ValidateArguments(GarkToolDefinition definition, JsonObject args)
	{
		var known = definition.InputSchema["properties"]!.AsObject();
		foreach (var (key, _) in args)
			if (!known.ContainsKey(key) && !key.StartsWith('_'))
				throw new ArgumentException($"Unknown argument '{key}' for {definition.Name}. Arguments: {string.Join(", ", known.Select(p => p.Key))}.");
		if (definition.InputSchema["required"] is JsonArray required)
			foreach (var key in required)
				if (args[key!.GetValue<string>()] == null) throw new ArgumentException($"Missing required argument '{key}' for {definition.Name}.");
	}

	private bool _contextMayChange;
	private void CheckContext(CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (_contextMayChange) return;
		if (CreatorService.CurrentSession != session || World.Current != world)
			throw new InvalidOperationException("The active project or world changed during this call (the user may have switched tabs). Call get_context and continue in the new context.");
		if (world?.IsDeleted == true) throw new InvalidOperationException("The world was closed.");
	}

	private void RequireWorld()
	{
		if (world == null || session == null || world.SessionType != World.SessionTypeEnum.Creator)
			throw new InvalidOperationException("No project world is open in Polytoria Creator. Ask the user to open or create a project.");
	}

	// Lenient argument readers: models sometimes send numbers or booleans as strings, or a single ID instead of a list.
	private static string Text(JsonObject a, string key, string? fallback = null)
	{
		if (a[key] == null) return fallback ?? throw new ArgumentException("Missing " + key);
		if (a[key] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
		if (a[key] is JsonObject { } obj && obj["id"]?.GetValue<string>() is { } id) return id;
		throw new ArgumentException(key + " must be a string.");
	}
	private static int Int(JsonObject a, string key, int fallback, int min, int max)
	{
		if (a[key] == null) return fallback;
		double value = GarkValues.Number(a[key]!);
		return (int)Math.Clamp(Math.Round(value), min, max);
	}
	private static float? Float(JsonObject a, string key) => a[key] == null ? null : (float)GarkValues.Number(a[key]!);
	private static bool Flag(JsonObject a, string key, bool fallback) => a[key] switch
	{
		null => fallback,
		JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
		JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
		JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out bool b) => b,
		_ => throw new ArgumentException(key + " must be true or false.")
	};
	private static string[] Strings(JsonObject a, string key) => a[key] switch
	{
		null => [],
		JsonArray array => array.Select(n => n is JsonObject o && o["id"] != null ? o["id"]!.GetValue<string>() : n?.GetValue<string>() ?? throw new ArgumentException(key + " cannot contain null.")).ToArray(),
		JsonValue v when v.TryGetValue<string>(out var s) => [s],
		JsonObject { Count: 0 } => [], // An empty Luau table.
		_ => throw new ArgumentException(key + " must be an array of strings.")
	};
	private static string[] IdList(JsonObject a, bool allowEmpty = false)
	{
		var ids = Strings(a, "ids").Concat(a["id"] != null ? [Text(a, "id")] : []).Distinct().ToArray();
		if (ids.Length == 0 && !allowEmpty) throw new ArgumentException("Supply ids.");
		if (ids.Length > 10000) throw new ArgumentException("Supply at most 10,000 IDs per call.");
		return ids;
	}
	private static JsonObject Object(JsonObject a, string key) => a[key] switch
	{
		JsonObject obj => obj,
		JsonArray { Count: 0 } => new JsonObject(),
		null => throw new ArgumentException("Missing " + key),
		_ => throw new ArgumentException(key + " must be an object.")
	};

	private Instance[] Targets(JsonObject args)
	{
		var items = IdList(args).Select(Resolve).ToArray();
		foreach (var item in items) RequireMovable(item);
		return items.Where(item => !items.Any(other => other != item && item.IsDescendantOf(other))).ToArray();
	}
	private void RequireMovable(Instance item)
	{
		if (item == world || item.GetType().IsDefined(typeof(StaticAttribute), false))
			throw new InvalidOperationException($"{item.Name} is the world root or a service and cannot be moved or removed.");
	}
	private bool Visible(Instance item)
	{
		for (Instance? current = item; current != null && current != world; current = current.Parent)
			if (current.IsDeleted || current == world!.TemporaryContainer || current.GetType().IsDefined(typeof(ExplorerExcludeAttribute), false)) return false;
		return !item.IsDeleted;
	}
	private Instance Resolve(string id)
	{
		RequireWorld();
		id = id.Trim();
		if (id is "/" or "" || id == world!.ObjectID) return world!;
		if (world!.GetObjectFromID(id) is Instance found)
			return Visible(found) ? found : throw new ArgumentException($"Instance {id} ({found.Name}) was deleted.");
		Instance current = world;
		foreach (string segment in id.Trim('/').Split('/'))
		{
			var matches = current.GetChildren().Where(c => c.Name == segment && Visible(c)).ToArray();
			if (matches.Length == 0) throw new ArgumentException($"No instance at {id}: '{segment}' not found under {GarkValues.PathOf(current)}. Use an ID from list_instances.");
			if (matches.Length > 1) throw new ArgumentException($"Path {id} is ambiguous: {matches.Length} children of {GarkValues.PathOf(current)} are named '{segment}'. Use an ID.");
			current = matches[0];
		}
		return current;
	}
	private GarkValues.Context ValueContext => new(world!, Resolve);
	private static JsonObject Summary(Instance i) => new()
	{
		["id"] = i.ObjectID, ["name"] = i.Name, ["class_name"] = i.ClassName, ["path"] = GarkValues.PathOf(i),
		["parent_id"] = i.Parent?.ObjectID, ["child_count"] = i.GetChildren().Length
	};
	private static JsonArray SummaryArray(IEnumerable<Instance> items) => new(items.Select(i => (JsonNode)Summary(i)).ToArray());
	private static Type InstanceType(string name) => TypesByName.TryGetValue(name, out var type) && typeof(Instance).IsAssignableFrom(type)
		? type : throw new ArgumentException("Unknown class: " + name + ". Call class_schema without arguments for the list.");

	internal JsonObject Context()
	{
		var history = world?.CreatorContext?.History;
		var camera = world?.CreatorContext?.Freelook?.Camera3D;
		bool ready = world != null && session != null && world.SessionType == World.SessionTypeEnum.Creator;
		return new()
		{
			["ready"] = ready,
			["project"] = session == null ? null : new JsonObject
			{
				["name"] = session.Metadata.ProjectName, ["folder"] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.ProjectFolderPath)), ["main_world"] = session.Metadata.MainWorld
			},
			["world"] = world == null ? null : new JsonObject { ["name"] = world.Name, ["path"] = world.WorldFilePath, ["id"] = world.ObjectID },
			["open_worlds"] = session == null ? null : new JsonArray(session.OpenedWorlds.Where(w => !w.IsDeleted)
				.Select(w => (JsonNode)new JsonObject { ["path"] = w.WorldFilePath, ["current"] = w == world }).ToArray()),
			["project_worlds"] = session == null ? null : new JsonArray(ProjectWorlds(session).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
			["selection"] = ready ? SummaryArray(world!.CreatorContext.Selections.SelectedInstances) : new JsonArray(),
			["camera"] = camera == null ? null : new JsonObject
			{
				["position"] = GarkValues.Encode(camera.GlobalPosition), ["forward"] = GarkValues.Encode(-camera.GlobalBasis.Z), ["fov"] = camera.Fov
			},
			["history"] = history == null ? null : new JsonObject { ["next_undo"] = history.NextUndoTitle, ["next_redo"] = history.NextRedoTitle },
			["playtest"] = GarkPlaytest.CurrentStatus(),
			["hint"] = ready ? null : "Ask the user to open or create a project in Polytoria Creator."
		};
	}

	/// <summary>World files in the project, relative and with forward slashes; hidden folders (such as .poly) are skipped.</summary>
	private static IEnumerable<string> ProjectWorlds(CreatorSession session)
	{
		string root = Path.GetFullPath(session.ProjectFolderPath);
		var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.ReparsePoint };
		return Directory.EnumerateFiles(root, "*.poly", options)
			.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
			.Where(p => !p.Split('/').Any(part => part.StartsWith('.')))
			.Take(1000).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
	}

	private JsonObject ListInstances(JsonObject a)
	{
		int depth = Int(a, "depth", 2, 1, 1000), limit = Int(a, "limit", 100, 1, 5000), offset = Int(a, "offset", 0, 0, int.MaxValue);
		string query = Text(a, "query", "");
		Type? classFilter = a["class_name"] != null ? InstanceType(Text(a, "class_name")) : null;
		string[] include = Strings(a, "include_properties");
		var page = new JsonArray();
		int total = 0;
		void Visit(Instance parent, int level)
		{
			foreach (var child in parent.GetChildren())
			{
				if (!Visible(child)) continue;
				bool match = (query == "" || child.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || child.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase))
					&& (classFilter == null || classFilter.IsInstanceOfType(child));
				if (match)
				{
					if (total >= offset && page.Count < limit)
					{
						var entry = Summary(child);
						entry["depth"] = level;
						if (include.Length > 0) entry["properties"] = Properties(child, include);
						page.Add((JsonNode)entry);
					}
					total++;
				}
				if (level < depth) Visit(child, level + 1);
			}
		}
		Visit(Resolve(Text(a, "parent", "/")), 1);
		return new JsonObject { ["items"] = page, ["total"] = total, ["has_more"] = offset + page.Count < total };
	}

	private static JsonObject Properties(Instance i, string[]? only = null)
	{
		var result = new JsonObject();
		bool all = only == null || only.Contains("*");
		foreach (var property in GarkValues.Editable(i.GetType()))
		{
			if (!all && !only!.Contains(property.Name)) continue;
			try { result[property.Name] = GarkValues.Encode(property.GetValue(i)); }
			catch (Exception ex) { result[property.Name] = "<unreadable: " + ex.GetBaseException().Message + ">"; }
		}
		return result;
	}

	private JsonObject Inspect(Instance item, int childLimit)
	{
		var result = Summary(item);
		result["properties"] = Properties(item);
		var children = item.GetChildren().Where(Visible).ToArray();
		result["children"] = new JsonArray(children.Take(childLimit).Select(c => (JsonNode)new JsonObject { ["id"] = c.ObjectID, ["name"] = c.Name, ["class_name"] = c.ClassName }).ToArray());
		if (children.Length > childLimit) result["children_truncated"] = children.Length - childLimit;
		if (item is Dynamic && TryBounds(item) is { } bounds) result["bounds"] = BoundsJson(bounds);
		if (item is Datamodel.Script { LinkedScript.LinkedPath: { } script }) result["script_path"] = script;
		return result;
	}
	private JsonNode InspectInstances(JsonObject a)
	{
		int childLimit = Int(a, "child_limit", 100, 0, 10000);
		if (a["ids"] == null) return Inspect(Resolve(Text(a, "id")), childLimit);
		return new JsonObject { ["items"] = new JsonArray(IdList(a).Select(id => (JsonNode)Inspect(Resolve(id), childLimit)).ToArray()) };
	}

	private static JsonNode Schema(string name)
	{
		if (name == "") return new JsonObject
		{
			["creatable"] = new JsonArray(Classes.Select(t => (JsonNode?)JsonValue.Create(t.Name)).ToArray()),
			["note"] = "Pass class_name for properties and scripting API. Script classes are created with create_script."
		};
		var type = TypesByName.GetValueOrDefault(name) ?? TypesByName.Values.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException("Unknown class: " + name);
		var bases = new JsonArray();
		for (var b = type.BaseType; b != null && b != typeof(object) && b != typeof(ValueType) && b.Namespace?.StartsWith("Polytoria") == true; b = b.BaseType)
			bases.Add((JsonNode?)JsonValue.Create(b.Name));
		var properties = new JsonObject();
		foreach (var property in GarkValues.Editable(type)) properties[property.Name] = GarkValues.Describe(property);
		var scriptProperties = new JsonObject();
		var events = new JsonArray();
		foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(p => p.IsDefined(typeof(ScriptPropertyAttribute), true)).OrderBy(p => p.Name))
		{
			if (property.PropertyType.Name.StartsWith("PTSignal")) events.Add((JsonNode?)JsonValue.Create(property.Name + SignalArguments(property.PropertyType)));
			else if (!properties.ContainsKey(property.Name))
				scriptProperties[property.Name] = TypeName(property.PropertyType) + (property.SetMethod?.IsPublic == true ? "" : " (read-only)");
		}
		var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
			.Select(m => (Method: m, Attribute: m.GetCustomAttribute<ScriptMethodAttribute>()))
			.Where(m => m.Attribute != null)
			.Select(m => $"{(m.Method.IsStatic ? "static " : "")}{m.Attribute!.MethodName ?? m.Method.Name}({string.Join(", ", m.Method.GetParameters().Where(p => !p.IsDefined(typeof(ScriptingCallerAttribute))).Select(p => TypeName(p.ParameterType) + " " + p.Name))}) -> {TypeName(m.Method.ReturnType)}")
			.Distinct().OrderBy(s => s, StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray();
		return new JsonObject
		{
			["class_name"] = type.Name, ["base_classes"] = bases, ["creatable"] = Classes.Contains(type),
			["editable_properties"] = properties,
			["script_properties"] = scriptProperties, ["script_methods"] = new JsonArray(methods), ["script_events"] = events
		};
	}
	private static string SignalArguments(Type signal) => signal.IsGenericType ? "(" + string.Join(", ", signal.GetGenericArguments().Select(TypeName)) + ")" : "()";
	private static string TypeName(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (type == typeof(void)) return "nil";
		if (type == typeof(string)) return "string";
		if (type == typeof(bool)) return "boolean";
		if (type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(uint) || type == typeof(long)) return "number";
		if (type.IsArray) return "{" + TypeName(type.GetElementType()!) + "}";
		if (type.IsGenericType && type.Name.StartsWith("Task")) return TypeName(type.GetGenericArguments()[0]);
		if (type == typeof(Task)) return "nil";
		return type.Name;
	}

	private JsonObject CreateInstances(JsonObject a)
	{
		var objects = a["objects"] as JsonArray ?? throw new ArgumentException("objects must be an array.");
		if (objects.Count is < 1 or > 10000) throw new ArgumentException("Create 1 to 10,000 objects per call; use run_scene_code for larger procedural builds.");
		var created = new List<(Instance Item, Instance Parent)>();
		try
		{
			for (int index = 0; index < objects.Count; index++)
			{
				var spec = objects[index] as JsonObject ?? throw new ArgumentException($"objects[{index}] must be an object.");
				string className = Text(spec, "class_name");
				var type = Classes.FirstOrDefault(t => t.Name == className)
					?? throw new ArgumentException($"objects[{index}]: {className} is not a creatable class. Call class_schema for the list.");
				if (typeof(Datamodel.Script).IsAssignableFrom(type)) throw new ArgumentException($"objects[{index}]: use create_script for {className}.");
				string parentRef = Text(spec, "parent", type == typeof(GUI) ? "/PlayerGUI" : "/Environment");
				Instance parent = parentRef.StartsWith('@')
					? int.TryParse(parentRef[1..], out int n) && n >= 0 && n < created.Count ? created[n].Item
						: throw new ArgumentException($"objects[{index}]: {parentRef} must refer to an earlier object in this call (@0 is the first).")
					: Resolve(parentRef);
				var item = Globals.LoadInstance<Instance>(className, world)!;
				created.Add((item, parent));
				item.Parent = parent;
				item.Name = Text(spec, "name", className);
				item.CreatorInserted();
				try { foreach (var change in ConvertProperties(item, spec["properties"] is JsonObject p ? p : new())) change.Property.SetValue(item, change.Value); }
				catch (Exception ex) when (ex is not OperationCanceledException) { throw new ArgumentException($"objects[{index}] ({className}): {ex.GetBaseException().Message}", ex); }
			}
			Commit("Gark: create " + (created.Count == 1 ? created[0].Item.Name : created.Count + " objects"),
				() => { foreach (var (item, parent) in created) item.Parent = parent; },
				() => { foreach (var (item, _) in created.AsEnumerable().Reverse()) { world!.CreatorContext.Selections.Deselect(item); item.Parent = world.TemporaryContainer; } });
			return new JsonObject { ["created"] = SummaryArray(created.Select(x => x.Item)) };
		}
		catch { foreach (var item in created.AsEnumerable().Reverse()) if (!item.Item.IsDeleted) item.Item.Delete(); throw; }
	}

	private JsonObject SetProperties(JsonObject a)
	{
		var targets = new List<(Instance Item, JsonObject Values)>();
		if (a["edits"] is JsonArray edits)
		{
			if (a["properties"] != null || a["id"] != null || a["ids"] != null) throw new ArgumentException("Use either edits or id/ids + properties, not both.");
			foreach (var edit in edits)
			{
				var entry = edit as JsonObject ?? throw new ArgumentException("Each edit must be {id, properties}.");
				targets.Add((Resolve(Text(entry, "id")), Object(entry, "properties")));
			}
		}
		else
		{
			var values = Object(a, "properties");
			targets.AddRange(IdList(a).Select(id => (Resolve(id), values)));
		}
		if (targets.Count == 0) throw new ArgumentException("Nothing to edit.");
		var changes = targets.Select(t =>
		{
			try { return (t.Item, Changes: ConvertProperties(t.Item, t.Values)); }
			catch (ArgumentException ex) { throw new ArgumentException($"{t.Item.Name} ({t.Item.ObjectID}): {ex.Message}", ex); }
		}).ToArray();
		void Revert() { foreach (var (item, list) in changes.Reverse()) foreach (var change in list.AsEnumerable().Reverse()) change.Property.SetValue(item, change.Old); }
		Commit("Gark: edit " + (changes.Length == 1 ? changes[0].Item.Name : changes.Length + " objects"), () =>
		{
			try { foreach (var (item, list) in changes) foreach (var change in list) change.Property.SetValue(item, change.Value); }
			catch { Revert(); throw; }
		}, Revert);
		return new JsonObject
		{
			["updated"] = new JsonArray(changes.Select(c =>
			{
				var summary = Summary(c.Item);
				summary["properties"] = Properties(c.Item, c.Changes.Select(x => x.Property.Name).ToArray());
				return (JsonNode)summary;
			}).ToArray())
		};
	}

	private List<(PropertyInfo Property, object? Old, object? Value)> ConvertProperties(Instance instance, JsonObject values)
	{
		var properties = GarkValues.Editable(instance.GetType()).ToDictionary(p => p.Name, StringComparer.Ordinal);
		var changes = new List<(PropertyInfo, object?, object?)>();
		foreach (var (key, value) in values)
		{
			if (key == "Parent") throw new ArgumentException("Parent cannot be set as a property; use reparent_instances or the parent field.");
			if (!properties.TryGetValue(key, out var property))
			{
				var match = properties.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
				if (match == null) throw new ArgumentException($"{instance.ClassName} has no editable property '{key}'. Call class_schema(\"{instance.ClassName}\").");
				property = properties[match];
			}
			object? old = property.GetValue(instance);
			try { changes.Add((property, old, GarkValues.Decode(value, property.PropertyType, ValueContext, old))); }
			catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
			{
				throw new ArgumentException(ex.Message.StartsWith("Expected ", StringComparison.Ordinal)
					? $"{instance.ClassName}.{property.Name}: {ex.Message}"
					: $"{instance.ClassName}.{property.Name} expects {GarkValues.Format(property.PropertyType)}. {ex.Message}", ex);
			}
		}
		return changes;
	}

	private void Commit(string title, Action redo, Action undo)
	{
		var history = world!.CreatorContext.History;
		history.NewAction(title);
		history.AddDoCallback(new(_ => redo()));
		history.AddUndoCallback(new(_ => undo()));
		history.CommitAction();
	}

	private JsonObject Reparent(JsonObject a)
	{
		var items = Targets(a);
		var parent = Resolve(Text(a, "parent"));
		foreach (var child in items)
			if (parent == child || parent.IsDescendantOf(child)) throw new InvalidOperationException($"Moving {child.Name} under {parent.Name} would create a cycle.");
		var old = items.Select(i => i.Parent).ToArray();
		Commit("Gark: reparent " + (items.Length == 1 ? items[0].Name : items.Length + " objects"),
			() => { foreach (var item in items) item.Parent = parent; },
			() => { for (int i = items.Length - 1; i >= 0; i--) items[i].Parent = old[i]; });
		return new JsonObject { ["moved"] = SummaryArray(items) };
	}

	private JsonObject Group(JsonObject a)
	{
		var items = Targets(a);
		var mode = Text(a, "as", "Model") switch
		{
			"Model" => CreatorHistory.GroupAsEnum.Model, "Folder" => CreatorHistory.GroupAsEnum.Folder,
			_ => throw new ArgumentException("as must be Model or Folder.")
		};
		string? name = a["name"] != null ? Text(a, "name") : null;
		Instance? group = null;
		var selections = world!.CreatorContext.Selections;
		Commit("Gark: group " + items.Length + " objects", () =>
		{
			group = selections.GroupInstances(items, mode);
			if (name != null) group.Name = name;
		}, () => { if (group != null) items = selections.UngroupModel(group); });
		return new JsonObject { ["group"] = group == null ? null : Summary(group) };
	}

	private JsonObject History(bool undo, int steps)
	{
		var history = world!.CreatorContext.History;
		var titles = new JsonArray();
		for (int i = 0; i < steps; i++)
		{
			string? title = undo ? history.NextUndoTitle : history.NextRedoTitle;
			if (title == null) break;
			if (undo) history.Undo(); else history.Redo();
			titles.Add((JsonNode?)JsonValue.Create(title));
		}
		if (titles.Count == 0) throw new InvalidOperationException(undo ? "Nothing to undo." : "Nothing to redo.");
		return new JsonObject { [undo ? "undone" : "redone"] = titles, ["next_undo"] = history.NextUndoTitle, ["next_redo"] = history.NextRedoTitle };
	}

	private JsonObject Save(bool all)
	{
		var saved = new JsonArray();
		foreach (var target in all ? session!.OpenedWorlds.Where(w => !w.IsDeleted).ToArray() : [world!])
		{
			if (string.IsNullOrEmpty(target.WorldFilePath)) throw new InvalidOperationException(target.Name + " has no project file yet; save it from Creator first.");
			Polytoria.Formats.PolyFormat.SaveWorldToFile(target, session!.GlobalizePath(target.WorldFilePath));
			saved.Add((JsonNode?)JsonValue.Create(target.WorldFilePath));
		}
		session!.Save();
		return new JsonObject { ["saved"] = saved };
	}

	private async Task<JsonObject> SwitchWorld(JsonObject a, CancellationToken token)
	{
		string path = Text(a, "path").Replace('\\', '/').TrimStart('/');
		_contextMayChange = true;
		var target = session!.OpenedWorlds.FirstOrDefault(w => !w.IsDeleted && string.Equals(w.WorldFilePath?.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
		if (target == null)
		{
			if (!path.EndsWith(".poly", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("World paths end in .poly; see get_context.project_worlds.");
			GarkProjectFiles.RequireInside(session.ProjectFolderPath, path);
			target = session.OpenWorld(path);
		}
		else Tabs.Singleton?.FocusWorld(target);
		await Globals.Singleton.ToSignal(Globals.Singleton.GetTree(), SceneTree.SignalName.ProcessFrame);
		token.ThrowIfCancellationRequested();
		if (World.Current != target) Tabs.Singleton?.FocusWorld(target);
		return new GarkTools(CreatorService.CurrentSession, World.Current).Context();
	}

	// Scripts

	private string ScriptPath(string relative)
	{
		RequireWorld();
		return GarkProjectFiles.Resolve(session!.ProjectFolderPath, relative);
	}
	private static string Normalize(string relative) => relative.Replace('\\', '/').TrimStart('/');
	private IEnumerable<string> ScriptFiles() => session!.IndexToFile.Values.Distinct()
		.Where(p => p.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
		.Select(Normalize).Distinct().OrderBy(p => p, StringComparer.Ordinal);
	private static CodeEdit? OpenEditor(string absolute) => Tabs.Singleton?.FindTextEditor(absolute)?.EditorRoot.CodeEditor;
	private static string ReadBuffer(string absolute) => OpenEditor(absolute)?.Text ?? File.ReadAllText(absolute);
	private ILookup<string, Datamodel.Script> LinkedScripts() => world!.GetDescendants().OfType<Datamodel.Script>()
		.Where(s => Visible(s) && s.LinkedScript?.LinkedPath != null)
		.ToLookup(s => Normalize(s.LinkedScript!.LinkedPath!), StringComparer.OrdinalIgnoreCase);
	private static JsonArray Ids(IEnumerable<Instance> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i.ObjectID)).ToArray());

	private JsonObject ListScripts(JsonObject a)
	{
		string[] scripts = ScriptFiles().Where(p => p.Contains(Text(a, "query", ""), StringComparison.OrdinalIgnoreCase)).ToArray();
		int offset = Int(a, "offset", 0, 0, int.MaxValue), limit = Int(a, "limit", 500, 1, 10000);
		var page = scripts.Skip(offset).Take(limit).ToArray();
		JsonArray items;
		if (Flag(a, "details", false))
		{
			var linked = LinkedScripts();
			items = new JsonArray(page.Select(p =>
			{
				string absolute = ScriptPath(p);
				var editor = OpenEditor(absolute);
				return (JsonNode)new JsonObject
				{
					["path"] = p, ["open_in_editor"] = editor != null,
					["unsaved"] = editor != null && File.Exists(absolute) && editor.Text != File.ReadAllText(absolute),
					["linked_instances"] = Ids(linked[p])
				};
			}).ToArray());
		}
		else items = new JsonArray(page.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
		return new JsonObject { ["total"] = scripts.Length, ["scripts"] = items, ["has_more"] = offset + page.Length < scripts.Length };
	}

	private JsonObject ReadScript(JsonObject a)
	{
		string relative = Normalize(Text(a, "path")), path = ScriptPath(relative);
		if (!File.Exists(path) && OpenEditor(path) == null) throw new ArgumentException($"No script at {relative}. Use list_scripts.");
		string source = ReadBuffer(path);
		string[] lines = source.Split('\n');
		int start = Int(a, "start_line", 1, 1, Math.Max(1, lines.Length)), count = Int(a, "line_count", 2000, 1, 1_000_000);
		bool numbered = Flag(a, "numbered", true);
		var selected = lines.Skip(start - 1).Take(count).Select((line, i) => numbered ? $"{start + i}: {line.TrimEnd('\r')}" : line.TrimEnd('\r')).ToArray();
		return new JsonObject
		{
			["path"] = relative, ["sha256"] = GarkProjectFiles.Hash(source), ["total_lines"] = lines.Length,
			["start_line"] = start, ["end_line"] = start + selected.Length - 1,
			["unsaved"] = OpenEditor(path) != null && File.Exists(path) && source != File.ReadAllText(path),
			["linked_instances"] = Ids(LinkedScripts()[relative]),
			["text"] = string.Join("\n", selected)
		};
	}

	private JsonObject GrepScripts(JsonObject a)
	{
		string pattern = Text(a, "pattern");
		if (pattern.Length > 2000) throw new ArgumentException("Search patterns may contain up to 2,000 characters.");
		var options = Flag(a, "case_sensitive", false) ? RegexOptions.None : RegexOptions.IgnoreCase;
		Regex regex;
		try { regex = new Regex(Flag(a, "regex", false) ? pattern : Regex.Escape(pattern), options, TimeSpan.FromMilliseconds(250)); }
		catch (ArgumentException ex) { throw new ArgumentException("Invalid regex: " + ex.Message); }
		int limit = Int(a, "limit", 200, 1, 5000), context = Int(a, "context_lines", 0, 0, 20);
		var matches = new JsonArray();
		var elapsed = System.Diagnostics.Stopwatch.StartNew();
		int files = 0;
		foreach (string relative in ScriptFiles().Where(p => p.Contains(Text(a, "path_filter", ""), StringComparison.OrdinalIgnoreCase)))
		{
			files++;
			var lines = ReadBuffer(ScriptPath(relative)).Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				if (elapsed.ElapsedMilliseconds > 15000)
					return new JsonObject { ["matches"] = matches, ["truncated"] = true, ["hint"] = "Search took too long; narrow path_filter." };
				bool hit;
				try { hit = regex.IsMatch(lines[i]); }
				catch (RegexMatchTimeoutException) { throw new ArgumentException("The regex is too slow on " + relative + ":" + (i + 1) + "; simplify it."); }
				if (!hit) continue;
				var match = new JsonObject { ["path"] = relative, ["line"] = i + 1, ["text"] = Clip(lines[i].TrimEnd('\r'), 1000) };
				if (context > 0)
				{
					match["before"] = new JsonArray(lines[Math.Max(0, i - context)..i].Select(l => (JsonNode?)JsonValue.Create(Clip(l.TrimEnd('\r'), 1000))).ToArray());
					match["after"] = new JsonArray(lines[(i + 1)..Math.Min(lines.Length, i + 1 + context)].Select(l => (JsonNode?)JsonValue.Create(Clip(l.TrimEnd('\r'), 1000))).ToArray());
				}
				matches.Add((JsonNode)match);
				if (matches.Count >= limit) return new JsonObject { ["matches"] = matches, ["truncated"] = true, ["files_searched"] = files };
			}
		}
		return new JsonObject { ["matches"] = matches, ["truncated"] = false, ["files_searched"] = files };
	}
	private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";

	private JsonObject CommitScript(string relative, string path, string before, string after)
	{
		if (after.Length > 5_000_000) throw new ArgumentException("Scripts are limited to 5,000,000 characters.");
		string diskBefore = File.Exists(path) ? File.ReadAllText(path) : before;
		void Write(string expected, string value, string disk)
		{
			if (ReadBuffer(path) != expected) throw new InvalidOperationException("The script changed after this action; refusing to overwrite newer edits.");
			File.WriteAllText(path, disk, new UTF8Encoding(false));
			var editor = OpenEditor(path);
			if (editor != null)
			{
				// Keep the caret and scroll where the user had them.
				int line = editor.GetCaretLine(), column = editor.GetCaretColumn();
				double scroll = editor.ScrollVertical;
				editor.BeginComplexOperation();
				editor.SelectAll();
				editor.InsertTextAtCaret(value);
				editor.EndComplexOperation();
				editor.SetCaretLine(Math.Min(line, editor.GetLineCount() - 1));
				editor.SetCaretColumn(column);
				editor.ScrollVertical = scroll;
			}
		}
		Commit("Gark: edit " + Path.GetFileName(path), () => Write(before, after, after), () => Write(after, before, diskBefore));
		return new JsonObject { ["path"] = relative, ["sha256"] = GarkProjectFiles.Hash(after), ["total_lines"] = after.Split('\n').Length, ["saved"] = true };
	}

	private JsonObject WriteScript(JsonObject a)
	{
		string relative = Normalize(Text(a, "path")), path = ScriptPath(relative);
		if (!File.Exists(path)) throw new ArgumentException($"No script at {relative}. Use create_script for new files.");
		string before = ReadBuffer(path);
		if (GarkProjectFiles.Hash(before) != Text(a, "expected_sha256")) throw new InvalidOperationException("The script changed since you read it (sha256 mismatch). Read it again before writing.");
		return CommitScript(relative, path, before, Text(a, "source"));
	}

	private JsonObject PatchScript(JsonObject a)
	{
		string relative = Normalize(Text(a, "path")), path = ScriptPath(relative);
		if (!File.Exists(path)) throw new ArgumentException($"No script at {relative}.");
		string before = ReadBuffer(path);
		if (a["expected_sha256"] != null && GarkProjectFiles.Hash(before) != Text(a, "expected_sha256"))
			throw new InvalidOperationException("The script changed since you read it (sha256 mismatch). Read it again.");
		JsonArray edits;
		if (a["edits"] is JsonArray list)
		{
			if (a["old_text"] != null || a["new_text"] != null) throw new ArgumentException("Use either edits or old_text/new_text.");
			edits = list;
		}
		else edits = [new JsonObject { ["old_text"] = Text(a, "old_text"), ["new_text"] = Text(a, "new_text"), ["replace_all"] = Flag(a, "replace_all", false) }];
		if (edits.Count == 0) throw new ArgumentException("No edits supplied.");
		string after = before;
		int replacements = 0;
		for (int i = 0; i < edits.Count; i++)
		{
			var edit = edits[i] as JsonObject ?? throw new ArgumentException($"edits[{i}] must be an object.");
			string oldText = Text(edit, "old_text"), newText = Text(edit, "new_text");
			// Models usually send \n; match files saved with CRLF too.
			if (after.Contains("\r\n") && !oldText.Contains('\r') && !after.Contains(oldText, StringComparison.Ordinal))
			{
				oldText = oldText.Replace("\n", "\r\n");
				newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
			}
			try { after = GarkProjectFiles.Replace(after, oldText, newText, Flag(edit, "replace_all", false), out int count); replacements += count; }
			catch (ArgumentException ex) { throw new ArgumentException((edits.Count > 1 ? $"edits[{i}]: " : "") + ex.Message + " No changes were made."); }
		}
		var result = CommitScript(relative, path, before, after);
		result["replacements"] = replacements;
		return result;
	}

	private JsonObject CreateScript(JsonObject a)
	{
		string relative = Normalize(Text(a, "path")), path = ScriptPath(relative);
		string? source = a["source"] != null ? Text(a, "source") : null;
		bool exists = File.Exists(path);
		if (exists && source != null) throw new InvalidOperationException($"{relative} already exists. Omit source to link it, or edit it with patch_script/write_script.");
		if (!exists && source == null) source = "";
		if (source?.Length > 5_000_000) throw new ArgumentException("Scripts are limited to 5,000,000 characters.");
		string kind = a["script_type"] != null ? Text(a, "script_type")
			: relative.EndsWith(".server.luau") || relative.EndsWith(".server.lua") ? "server"
			: relative.EndsWith(".client.luau") || relative.EndsWith(".client.lua") ? "client"
			: relative.EndsWith(".module.luau") || relative.EndsWith(".module.lua") ? "module"
			: throw new ArgumentException("Name the file *.server.luau, *.client.luau or *.module.luau, or pass script_type.");
		string className = kind switch { "server" => "ServerScript", "client" => "ClientScript", "module" => "ModuleScript", _ => throw new ArgumentException("script_type must be server, client or module.") };
		Instance parent = Resolve(Text(a, "parent", "/ScriptService"));
		var script = Globals.LoadInstance<Datamodel.Script>(className, world)!;
		script.Name = Text(a, "name", CreatorService.GetScriptNameFromPath(relative));
		bool created = false;
		try
		{
			Commit("Gark: create " + script.Name, () =>
			{
				if (source != null)
				{
					if (File.Exists(path) && !created) throw new InvalidOperationException("File now exists; refusing to overwrite it.");
					Directory.CreateDirectory(Path.GetDirectoryName(path)!);
					File.WriteAllText(path, source, new UTF8Encoding(false));
					created = true;
				}
				session!.RescanFolder();
				script.LinkedScript = world!.Assets.GetFileLinkByPath(relative);
				script.Parent = parent;
			}, () =>
			{
				world!.CreatorContext.Selections.Deselect(script);
				script.Parent = world.TemporaryContainer;
				if (source != null)
				{
					if (ReadBuffer(path) != source) throw new InvalidOperationException("The script has newer edits; refusing to remove it.");
					File.Delete(path);
					session!.RescanFolder();
				}
			});
		}
		catch { script.Delete(); throw; }
		var result = Summary(script);
		result["script_path"] = relative;
		result["sha256"] = GarkProjectFiles.Hash(source ?? ReadBuffer(path));
		result["linked_existing_file"] = source == null;
		return result;
	}

	private JsonObject DeleteScript(JsonObject a)
	{
		string relative = Normalize(Text(a, "path")), path = ScriptPath(relative);
		if (!File.Exists(path)) throw new ArgumentException($"No script at {relative}.");
		byte[] contents = File.ReadAllBytes(path);
		var linked = Flag(a, "delete_instances", true) ? LinkedScripts()[relative].ToArray() : [];
		var parents = linked.Select(s => s.Parent).ToArray();
		bool openInEditor = OpenEditor(path) != null;
		Commit("Gark: delete " + Path.GetFileName(relative), () =>
		{
			foreach (var script in linked) { world!.CreatorContext.Selections.Deselect(script); script.Parent = world.TemporaryContainer; }
			File.Delete(path);
			session!.RescanFolder();
		}, () =>
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, contents);
			session!.RescanFolder();
			for (int i = 0; i < linked.Length; i++)
			{
				linked[i].LinkedScript = world!.Assets.GetFileLinkByPath(relative);
				linked[i].Parent = parents[i];
			}
		});
		return new JsonObject
		{
			["deleted"] = relative, ["deleted_instances"] = Ids(linked),
			["note"] = openInEditor ? "The script is still open in an editor tab; saving that tab would recreate the file." : null
		};
	}

	private async Task<JsonObject> OpenScript(JsonObject a, CancellationToken token)
	{
		string relative = Normalize(Text(a, "path")), path = ScriptPath(relative);
		if (!File.Exists(path)) throw new ArgumentException($"No script at {relative}.");
		if (Tabs.Singleton == null) throw new InvalidOperationException("The code editor is not available.");
		Tabs.Singleton.Insert(new Tabs.TextEditorTab { Session = session!, TargetPath = relative, Title = Path.GetFileName(relative) });
		await Globals.Singleton.ToSignal(Globals.Singleton.GetTree(), SceneTree.SignalName.ProcessFrame);
		token.ThrowIfCancellationRequested();
		if (a["line"] != null && OpenEditor(path) is { } editor)
		{
			editor.SetCaretLine(Math.Clamp(Int(a, "line", 1, 1, int.MaxValue) - 1, 0, Math.Max(0, editor.GetLineCount() - 1)));
			editor.CenterViewportToCaret();
			editor.GrabFocus();
		}
		return new JsonObject { ["opened"] = relative };
	}

	private static readonly Lazy<string> LuauApi = new(() => DocsGen.LuaDefinitionGenerator.GenerateDefinitions(DocsGen.APIReferenceGenerator.GenerateReferences()));
	/// <summary>The complete Polytoria Luau type definitions (the same file the script editor's language server uses).</summary>
	internal static string LuauDefinitions => LuauApi.Value;

	private static JsonObject SearchApi(JsonObject a)
	{
		string[] lines = LuauDefinitions.Split('\n');
		string query = Text(a, "query");
		Regex? regex = null;
		if (Flag(a, "regex", false))
		{
			try { regex = new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)); }
			catch (ArgumentException ex) { throw new ArgumentException("Invalid regex: " + ex.Message); }
		}
		int limit = Int(a, "limit", 12, 1, 200), context = Int(a, "context_lines", 3, 0, 40);
		var matches = new JsonArray();
		int total = 0, lastEnd = -1;
		for (int i = 0; i < lines.Length; i++)
		{
			if (!(regex?.IsMatch(lines[i]) ?? lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
			total++;
			if (matches.Count >= limit || i <= lastEnd) continue; // Skip matches already shown in the previous excerpt.
			int from = Math.Max(0, i - context);
			lastEnd = Math.Min(lines.Length - 1, i + context);
			matches.Add((JsonNode)new JsonObject { ["line"] = i + 1, ["excerpt"] = string.Join("\n", lines[from..(lastEnd + 1)].Select(l => l.TrimEnd('\r'))) });
		}
		return new JsonObject { ["matches"] = matches, ["total_matching_lines"] = total };
	}

	// Camera framing shared by capture_viewport and focus_camera.

	private static Vector3 ViewDirection(string view) => view switch
	{
		"iso" => new Vector3(1, 0.8f, 1).Normalized(),
		"front" => new Vector3(0, 0.2f, 1).Normalized(), // Objects face +Z, so their front is seen from +Z.
		"back" => new Vector3(0, 0.2f, -1).Normalized(),
		"right" => new Vector3(1, 0.2f, 0).Normalized(),
		"left" => new Vector3(-1, 0.2f, 0).Normalized(),
		"top" => Vector3.Up,
		"bottom" => Vector3.Down,
		_ => throw new ArgumentException("view must be current, iso, front, back, left, right, top or bottom.")
	};
	private Aabb? FocusBounds(string[] ids)
	{
		var items = ids.Length > 0 ? ids.Select(Resolve).ToArray() : [world!.Environment];
		var boxes = items.Select(TryBounds).Where(b => b != null).Select(b => b!.Value).ToArray();
		if (boxes.Length == 0)
		{
			if (ids.Length > 0) throw new ArgumentException("The focused objects have no spatial bounds.");
			return null;
		}
		return boxes.Aggregate((x, y) => x.Merge(y));
	}
	private static (Vector3 Position, Vector3 Target) Frame(Aabb bounds, Vector3 direction, float fov, float aspect)
	{
		Vector3 center = bounds.GetCenter();
		float radius = Math.Max(bounds.Size.Length() * 0.5f, 0.5f);
		float vertical = Mathf.DegToRad(fov) * 0.5f;
		float horizontal = Mathf.Atan(Mathf.Tan(vertical) * aspect);
		float distance = radius / Mathf.Sin(Math.Min(vertical, horizontal)) * 1.05f;
		return (center + direction * distance, center);
	}
	private static Transform3D Looking(Vector3 position, Vector3 target)
	{
		if (position.DistanceTo(target) < 0.001f) throw new ArgumentException("Camera position and look_at must differ.");
		Vector3 direction = (target - position).Normalized();
		return new Transform3D(Basis.Identity, position).LookingAt(target, Math.Abs(direction.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up);
	}

	private async Task<GarkToolResult> Capture(JsonObject a, CancellationToken token)
	{
		var original = world!.CreatorContext.Freelook.Camera3D;
		int width = Int(a, "width", 1024, 64, 3840), height = Int(a, "height", 576, 64, 2160);
		float fov = Math.Clamp(Float(a, "fov") ?? original.Fov, 5, 150);
		string format = Text(a, "format", "png");
		if (format is not ("png" or "jpeg")) throw new ArgumentException("format must be png or jpeg.");
		int quality = Int(a, "quality", 85, 1, 100);
		string[] focus = Strings(a, "focus_ids");
		var views = Strings(a, "views").ToList();
		if (a["view"] != null) views.Insert(0, Text(a, "view"));
		if (views.Count == 0) views.Add(focus.Length > 0 && a["position"] == null ? "iso" : "current");
		if (views.Count > 12) throw new ArgumentException("Request at most 12 views per call.");
		Aabb? bounds = views.Any(v => v != "current") || focus.Length > 0 ? FocusBounds(focus) : null;

		var shots = new List<Transform3D>();
		foreach (string view in views)
		{
			Transform3D transform = original.GlobalTransform;
			if (view != "current")
			{
				if (bounds == null) throw new ArgumentException("There is nothing in the world to frame; pass position and look_at.");
				var (position, target) = Frame(bounds.Value, ViewDirection(view), fov, width / (float)height);
				transform = Looking(position, target);
			}
			else if (focus.Length > 0 && a["position"] == null && bounds != null)
			{
				var (position, target) = Frame(bounds.Value, (original.GlobalPosition - bounds.Value.GetCenter()).Normalized() is { } d && d.IsFinite() && !d.IsZeroApprox() ? d : ViewDirection("iso"), fov, width / (float)height);
				transform = Looking(position, target);
			}
			if (a["position"] != null)
			{
				transform.Origin = GarkValues.Vector3Value(a["position"]!);
				if (a["look_at"] == null && bounds != null && focus.Length > 0) transform = Looking(transform.Origin, bounds.Value.GetCenter());
			}
			if (a["look_at"] != null) transform = Looking(transform.Origin, GarkValues.Vector3Value(a["look_at"]!));
			shots.Add(transform);
		}

		var viewport = new SubViewport
		{
			Size = new Vector2I(width, height), World3D = world.World3D, OwnWorld3D = false,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always
		};
		CreatorService.Interface.AddChild(viewport);
		try
		{
			var camera = new Camera3D
			{
				Fov = fov, Near = original.Near, Far = Math.Max(original.Far, bounds is { } b ? b.Size.Length() * 4 + 100 : 0),
				CullMask = original.CullMask, Environment = original.Environment
			};
			viewport.AddChild(camera);
			camera.MakeCurrent();
			var images = new List<GarkImage>();
			var metadata = new JsonArray();
			for (int i = 0; i < shots.Count; i++)
			{
				camera.GlobalTransform = shots[i];
				await RenderFrames(2, token);
				CheckContext(token);
				using var image = viewport.GetTexture().GetImage();
				images.Add(format == "png" ? new(image.SavePngToBuffer()) : new(image.SaveJpgToBuffer(quality / 100f), "image/jpeg"));
				metadata.Add((JsonNode)new JsonObject
				{
					["view"] = views[i], ["camera_position"] = GarkValues.Encode(camera.GlobalPosition),
					["camera_forward"] = GarkValues.Encode(-camera.GlobalBasis.Z), ["fov"] = fov
				});
			}
			return new(new JsonObject
			{
				["width"] = width, ["height"] = height, ["images"] = metadata,
				["focus_bounds"] = bounds is { } box && focus.Length > 0 ? BoundsJson(box) : null,
				["note"] = "World render without screen UI or editor gizmos; images follow in order."
			}, images);
		}
		finally { viewport.QueueFree(); }
	}

	/// <summary>Waits for rendered frames, failing clearly when Creator is minimized and not drawing.</summary>
	internal static async Task RenderFrames(int count, CancellationToken token)
	{
		for (int i = 0; i < count; i++)
		{
			var frame = Globals.Singleton.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			var started = DateTime.UtcNow;
			while (!frame.IsCompleted)
			{
				token.ThrowIfCancellationRequested();
				if (DateTime.UtcNow - started > TimeSpan.FromSeconds(15))
					throw new InvalidOperationException("Creator is not rendering frames (its window may be minimized). Ask the user to restore the Creator window, then retry.");
				await Globals.Singleton.ToSignal(Globals.Singleton.GetTree(), SceneTree.SignalName.ProcessFrame);
			}
		}
	}

	private JsonObject FocusCamera(JsonObject a)
	{
		var freelook = world!.CreatorContext.Freelook;
		var camera = freelook.Camera3D;
		Transform3D transform;
		string[] ids = Strings(a, "ids");
		if (ids.Length > 0)
		{
			var bounds = FocusBounds(ids)!.Value;
			Vector3 direction = a["view"] != null ? ViewDirection(Text(a, "view")) : (camera.GlobalPosition - bounds.GetCenter()).Normalized();
			if (!direction.IsFinite() || direction.IsZeroApprox()) direction = ViewDirection("iso");
			var viewportSize = camera.GetViewport()?.GetVisibleRect().Size ?? new Vector2(16, 9);
			var (position, target) = Frame(bounds, direction, camera.Fov, viewportSize.X / Math.Max(1, viewportSize.Y));
			transform = Looking(position, target);
		}
		else if (a["position"] != null || a["look_at"] != null)
		{
			Vector3 position = a["position"] != null ? GarkValues.Vector3Value(a["position"]!) : camera.GlobalPosition;
			transform = a["look_at"] != null ? Looking(position, GarkValues.Vector3Value(a["look_at"]!)) : new Transform3D(camera.GlobalBasis, position);
		}
		else throw new ArgumentException("Supply ids, or position and/or look_at.");
		freelook.GDNode3D.GlobalTransform = transform;
		return new JsonObject { ["camera_position"] = GarkValues.Encode(transform.Origin), ["camera_forward"] = GarkValues.Encode(-transform.Basis.Z) };
	}
}

internal static class GarkProjectFiles
{
	internal static string RequireInside(string projectRoot, string relative)
	{
		if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
			throw new ArgumentException("Use a path relative to the project folder.");
		string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
			throw new ArgumentException("Path must stay inside the current project.");
		for (string? check = full; check != null; check = Path.GetDirectoryName(check))
		{
			if ((File.Exists(check) || Directory.Exists(check)) && (File.GetAttributes(check) & System.IO.FileAttributes.ReparsePoint) != 0)
				throw new ArgumentException("Symbolic links and junctions are not allowed in project paths.");
			if (string.Equals(check, root, comparison)) break;
		}
		return full;
	}

	/// <summary>Script tools only touch Luau/Lua files inside the project.</summary>
	internal static string Resolve(string projectRoot, string relative)
	{
		string full = RequireInside(projectRoot, relative);
		if (Path.GetExtension(full).ToLowerInvariant() is not (".luau" or ".lua"))
			throw new ArgumentException("Script tools work on .luau/.lua files. Other project files are in the project folder from get_context.");
		if (File.Exists(full) && new FileInfo(full).Length > 10_000_000) throw new ArgumentException("Script exceeds the 10 MB limit.");
		return full;
	}
	internal static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
	internal static string ReplaceOnce(string source, string oldText, string newText) => Replace(source, oldText, newText, false, out _);
	internal static string Replace(string source, string oldText, string newText, bool all, out int count)
	{
		if (oldText.Length == 0) throw new ArgumentException("old_text cannot be empty.");
		count = 0;
		for (int at = source.IndexOf(oldText, StringComparison.Ordinal); at >= 0; at = source.IndexOf(oldText, at + oldText.Length, StringComparison.Ordinal)) count++;
		if (count == 0) throw new ArgumentException("old_text was not found. Copy it exactly from read_script (without the 'N: ' line prefixes).");
		if (count > 1 && !all) throw new ArgumentException($"old_text matches {count} places. Include more surrounding text, or set replace_all.");
		if (all) return source.Replace(oldText, newText, StringComparison.Ordinal);
		int first = source.IndexOf(oldText, StringComparison.Ordinal);
		return source[..first] + newText + source[(first + oldText.Length)..];
	}
}
