// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Datamodel;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Creator.Gark;

internal sealed partial class GarkTools
{
	private static void AddConstructionDefinitions(GarkToolAdder add)
	{
		add("run_scene_code", "Run construction code", GarkToolFlags.Edit | GarkToolFlags.Destructive,
			"Run a Creator-only Luau program that edits the world as ONE undo action: loops, functions, math/string/table libraries. "
			+ "Edits: scene.create(class_name, properties, parent?) returns a temporary string ID usable as a parent or target later in the program; scene.set(id, properties); scene.reparent(id, parent); scene.delete(id). "
			+ "Queries (see the world as it was before this program): scene.get(id) -> {id,name,class_name,path,properties}, scene.children(id), scene.descendants(id, class_name?), scene.find(path) -> summary or nil, scene.selection(), scene.bounds(id), "
			+ "scene.raycast({origin={x,y,z}, direction={x,y,z}, max_distance=1000, ignore_ids={...}}), scene.overlap({center={x,y,z}, size={x,y,z}}). "
			+ "Values use the same formats as set_properties: vectors {x,y,z}, colors '#RRGGBB', enums as strings. print(...) output and the returned value come back with a map of temporary IDs to real IDs. "
			+ "Example: local m=scene.create('Model',{Name='Steps'}); for i=1,20 do scene.create('Part',{Position={i*3,i,0},Size={3,1,6},Anchored=true},m) end; return m. "
			+ "No gameplay globals, files or network. Errors or cancellation roll back the whole program.",
			"""{"source":{"type":"string"},"title":{"type":"string","description":"Name for the undo history entry."}}""", "source");
		add("get_bounds", "Get bounds", GarkToolFlags.Read,
			"World-space axis-aligned bounding boxes (min, max, center, size) of objects including descendants, plus their combined box. Rotation and scale are included.",
			"""{"ids":""" + IdArray + "}", "ids");
		add("measure_distance", "Measure distance", GarkToolFlags.Read,
			"Distance between two objects' bounding-box centers, per-axis center delta, and the gap between their boxes (zero when touching or overlapping).",
			"""{"from":{"type":"string"},"to":{"type":"string"}}""", "from", "to");
		add("raycast", "Raycast", GarkToolFlags.Read,
			"Cast a ray against the world's collision shapes. Returns the nearest instance, hit position, normal and distance. Ignores editor helpers and ignore_ids (with descendants). Collision geometry can differ from rendered meshes.",
			"""{"origin":""" + Vec3 + ""","direction":""" + Vec3 + ""","max_distance":{"type":"number","description":"Default 1000."},"ignore_ids":""" + IdArray + "}", "origin", "direction");
		add("overlap_box", "Find objects in box", GarkToolFlags.Read,
			"Find visible objects whose world-space bounding boxes overlap an axis-aligned box (conservative bounds test). Paginated.",
			"""{"center":""" + Vec3 + ""","size":""" + Vec3 + ""","ignore_ids":""" + IdArray + ""","offset":{"type":"integer"},"limit":{"type":"integer","description":"Default 100, max 5000."}}""", "center", "size");
		add("place_on_ground", "Place on ground", GarkToolFlags.Edit | GarkToolFlags.Idempotent,
			"Drop objects straight down onto the first collision surface below their bounding-box top center, so the box bottom rests at the hit height plus clearance. Keeps rotation. One undo action; fails without moving anything if any object has no surface within max_distance.",
			"""{"ids":""" + IdArray + ""","clearance":{"type":"number"},"max_distance":{"type":"number","description":"Default 1000."}}""", "ids");
		add("align_instances", "Align instances", GarkToolFlags.Edit | GarkToolFlags.Idempotent,
			"Align objects' bounding-box min, center or max along x, y or z to a reference object, a coordinate, or (default) their combined bounds. Moves without rotating or resizing. One undo action.",
			"""{"ids":""" + IdArray + ""","axis":{"type":"string","enum":["x","y","z"]},"edge":{"type":"string","enum":["min","center","max"]},"reference":{"type":"string"},"coordinate":{"type":"number"}}""", "ids", "axis");
		add("search_assets", "Search Toolbox", GarkToolFlags.Read | GarkToolFlags.Network,
			"Search the public Polytoria Toolbox. Returns asset IDs, names, creators and thumbnail URLs with paging. Types: model, mesh, image, audio.",
			"""{"query":{"type":"string"},"type":{"type":"string","enum":["model","mesh","image","audio"]},"page":{"type":"integer"}}""", "query", "type");
		add("inspect_asset", "Inspect Toolbox asset", GarkToolFlags.Read | GarkToolFlags.Network,
			"Show a Toolbox search result's metadata and thumbnail image.",
			"""{"id":{"type":"integer"},"type":{"type":"string","enum":["model","mesh","image","audio"]},"preview":{"type":"boolean","description":"Include the thumbnail. Default true."}}""", "id", "type");
		add("import_asset", "Import Toolbox asset", GarkToolFlags.Edit | GarkToolFlags.Network,
			"Insert a Toolbox asset by ID using the normal Polytoria model/mesh/image/audio types, with optional parent, name and properties (Position, Size...). One undo action. Model files and dependencies go into a unique Toolbox folder in the project; model scripts are imported but never run in Creator.",
			"""{"id":{"type":"integer"},"type":{"type":"string","enum":["model","mesh","image","audio"]},"parent":{"type":"string"},"name":{"type":"string"},"properties":{"type":"object"}}""", "id", "type");
	}

	private sealed class SceneEdit(GarkTools tools) : IDisposable
	{
		private readonly List<(Action Apply, Action Undo)> _changes = [];
		private readonly List<Instance> _created = [];
		private bool _committed;
		internal void Track(Instance item) => _created.Add(item);
		internal void Change(Action apply, Action undo)
		{
			_changes.Add((apply, undo)); // Record before applying so partially failing setters also roll back.
			apply();
		}
		internal void Finish(string title)
		{
			if (_changes.Count != 0)
			{
				bool first = true;
				tools.Commit(title, () => {
					if (first) { first = false; return; } // The transaction is already applied.
					foreach (var change in _changes) change.Apply();
				}, () => { foreach (var change in _changes.AsEnumerable().Reverse()) change.Undo(); });
			}
			_committed = true;
		}
		public void Dispose()
		{
			if (_committed) return;
			foreach (var change in _changes.AsEnumerable().Reverse()) change.Undo();
			foreach (var item in _created) if (!item.IsDeleted) item.Delete();
		}
	}

	private async Task<JsonObject> RunSceneCode(JsonObject args, CancellationToken token)
	{
		await PhysicsReady(token);
		var commands = new List<(string Op, JsonObject Args)>();
		int nextId = 0;
		JsonNode? Bridge(string op, JsonObject a)
		{
			CheckContext(token);
			switch (op)
			{
				case "create":
					string id = "@" + ++nextId;
					a["handle"] = id;
					commands.Add((op, a));
					return JsonValue.Create(id);
				case "set": case "reparent": case "delete": commands.Add((op, a)); return null;
				case "get":
					var item = Resolve(Text(a, "id"));
					var result = Summary(item); result["properties"] = Properties(item); return result;
				case "children": return SummaryArray(Resolve(Text(a, "id", "/Environment")).GetChildren().Where(Visible));
				case "descendants":
					Type? filter = a["class_name"] != null ? InstanceType(Text(a, "class_name")) : null;
					return SummaryArray(Resolve(Text(a, "id", "/Environment")).GetDescendants().Where(i => Visible(i) && (filter == null || filter.IsInstanceOfType(i))));
				case "find":
					try { return Summary(Resolve(Text(a, "path"))); }
					catch (ArgumentException) { return null; }
				case "selection": return SummaryArray(world!.CreatorContext.Selections.SelectedInstances);
				case "bounds": return BoundsJson(Bounds(Resolve(Text(a, "id"))));
				case "raycast": return Raycast(a, token);
				case "overlap": return OverlapBox(a);
				default: throw new ArgumentException("Unknown scene function: " + op);
			}
		}
		var execution = await GarkLuau.RunAsync(Text(args, "source"), Bridge, () => CheckContext(token), token);
		CheckContext(token);
		using var edit = new SceneEdit(this);
		var handles = new Dictionary<string, Instance>();
		Instance Find(string id)
		{
			var item = handles.TryGetValue(id, out var found) ? found : Resolve(id);
			if (!Visible(item)) throw new ArgumentException("Cannot reference an object already deleted by this batch.");
			return item;
		}
		var elapsed = Stopwatch.StartNew();
		int applied = 0;
		foreach (var (op, a) in commands)
		{
			CheckContext(token);
			if (++applied % 500 == 0) Progress?.Invoke($"Applying {applied}/{commands.Count} edits");
			if (op == "create")
			{
				string name = Text(a, "class_name");
				if (!Classes.Any(t => t.Name == name) || name.EndsWith("Script"))
					throw new ArgumentException("Use an existing creatable class; use create_script for persistent scripts.");
				var parent = Find(Text(a, "parent", "/Environment"));
				var item = Globals.LoadInstance<Instance>(name, world)!;
				edit.Track(item);
				handles.Add(Text(a, "handle"), item);
				edit.Change(() => item.Parent = parent, () => { world!.CreatorContext.Selections.Deselect(item); item.Parent = world.TemporaryContainer; });
				item.Name = name;
				item.CreatorInserted();
				try { foreach (var change in ConvertProperties(item, a["properties"] as JsonObject ?? new())) change.Property.SetValue(item, change.Value); }
				catch (ArgumentException ex) { throw new ArgumentException($"scene.create('{name}'): {ex.Message}", ex); }
			}
			else
			{
				var item = Find(Text(a, "id"));
				if (!Visible(item)) throw new ArgumentException("Cannot edit an object already deleted by this batch.");
				if (op == "set")
				{
					foreach (var change in ConvertProperties(item, a["properties"] as JsonObject ?? throw new ArgumentException("scene.set needs a properties table.")))
						edit.Change(() => change.Property.SetValue(item, change.Value), () => change.Property.SetValue(item, change.Old));
				}
				else
				{
					RequireMovable(item);
					var before = item.Parent;
					var after = op == "delete" ? world!.TemporaryContainer : Find(Text(a, "parent"));
					if (after == item || after.IsDescendantOf(item)) throw new ArgumentException("Reparenting would create a cycle.");
					edit.Change(() => { world!.CreatorContext.Selections.Deselect(item); item.Parent = after; }, () => item.Parent = before);
				}
			}
			if (elapsed.ElapsedMilliseconds >= 8) { await Task.Delay(1, token); elapsed.Restart(); }
		}
		CheckContext(token);
		edit.Finish("Gark: " + Text(args, "title", "procedural scene edit"));
		var mapping = new JsonObject();
		foreach (var (handle, item) in handles) mapping[handle] = item.ObjectID;
		return new() { ["operations"] = commands.Count, ["created"] = handles.Count, ["ids"] = mapping,
			["result"] = execution.Result, ["output"] = execution.Output };
	}
}
