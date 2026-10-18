// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Datamodel;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
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
			+ "Edits: scene.create(class_name, properties, parent?) returns an ID usable as a parent or target later in the program; scene.set(id, properties); scene.reparent(id, parent); scene.delete(id). "
			+ "Queries: scene.get(id) -> {id,name,class_name,path,properties}, scene.children(id, include_properties?), scene.descendants(id, class_name?, include_properties?) (include_properties: true for every editable property or a list such as {'Position','Size'}), scene.find(path) -> summary or nil, scene.selection(), scene.bounds(id), "
			+ "scene.raycast({origin={x,y,z}, direction={x,y,z}, max_distance=1000, ignore_ids={...}}), scene.overlap({center={x,y,z}, size={x,y,z}}). "
			+ "By default edits are staged and applied together at the end (fastest), so queries see the world as it was before the program. With apply_as_you_go=true each edit lands immediately and later queries see it (raycasts only hit new parts after the next physics step). "
			+ "Values use the same formats as set_properties: vectors {x,y,z}, colors '#RRGGBB', enums as strings. print(...) output and the returned value come back; created IDs inside the returned value are already real instance IDs, and created_roots lists the top-level objects made. "
			+ "Polytoria keeps sibling names unique (a second 'Wall' becomes 'Wall2'); renamed lists every object whose requested Name changed. "
			+ "Example: local m=scene.create('Model',{Name='Steps'}); for i=1,20 do scene.create('Part',{Position={i*3,i,0},Size={3,1,6},Anchored=true},m) end; return m. "
			+ "No gameplay globals, files or network. Errors or cancellation roll back the whole program.",
			"""{"source":{"type":"string"},"title":{"type":"string","description":"Name for the undo history entry."},"apply_as_you_go":{"type":"boolean","description":"Apply each edit immediately so later queries in the same program see it. Default false. Still one undo action that rolls back on error."},"return_ids":{"type":"boolean","description":"Also return the full map of every created object's temporary ID to its real ID. Default false."}}""", "source");
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
		add("scale_instances", "Scale instances", GarkToolFlags.Edit,
			"Scale objects uniformly about a pivot as one undo action: positions move toward/away from the pivot and sizes change by factor. Models, Folders and parts with children scale everything inside them. Light ranges and Text3D font sizes and outlines scale too (they do not follow Size on their own). "
			+ "pivot: \"center\" (default, combined bounds center), \"bottom\" (bounds bottom center, so the result stays on its ground) or a world point [x,y,z].",
			"""{"ids":""" + IdArray + ""","factor":{"type":"number","description":"Uniform factor: 2 doubles, 0.5 halves."},"pivot":{"anyOf":[{"type":"string","enum":["center","bottom"]},{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3}],"description":"center (default), bottom or a world point [x,y,z]."}}""", "ids", "factor");
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
		bool live = Flag(args, "apply_as_you_go", false), returnIds = Flag(args, "return_ids", false);
		var commands = new List<(string Op, JsonObject Args)>();
		var handles = new Dictionary<string, Instance>();
		var renamed = new Renames();
		using var edit = new SceneEdit(this);
		int nextId = 0, operations = 0;
		Instance Find(string id)
		{
			var item = handles.TryGetValue(id, out var found) ? found : Resolve(id);
			if (!Visible(item)) throw new ArgumentException("Cannot reference an object already deleted by this batch.");
			return item;
		}
		Instance Query(JsonObject a, string? fallback = null)
		{
			string id = Text(a, "id", fallback);
			if (!live && id.StartsWith('@')) throw new ArgumentException($"{id} is created by this program and does not exist until it finishes. Query it in a later call, or pass apply_as_you_go=true.");
			return Resolve(id);
		}
		JsonArray Listing(IEnumerable<Instance> items, JsonObject a)
		{
			string[]? only = a["include_properties"] switch
			{
				null => null,
				JsonValue v when v.GetValueKind() == JsonValueKind.False => null,
				JsonValue v when v.GetValueKind() == JsonValueKind.True => ["*"],
				_ => Strings(a, "include_properties")
			};
			return new(items.Select(i => { var s = Summary(i); if (only != null) s["properties"] = Properties(i, only); return (JsonNode)s; }).ToArray());
		}
		void Apply(string op, JsonObject a)
		{
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
				try
				{
					var changes = ConvertProperties(item, a["properties"] as JsonObject ?? new());
					foreach (var change in changes) change.Property.SetValue(item, change.Value);
					renamed.Check(item, changes);
				}
				catch (ArgumentException ex) { throw new ArgumentException($"scene.create('{name}'): {ex.Message}", ex); }
				return;
			}
			var target = Find(Text(a, "id"));
			if (op == "set")
			{
				var changes = ConvertProperties(target, a["properties"] as JsonObject ?? throw new ArgumentException("scene.set needs a properties table."));
				foreach (var change in changes)
					edit.Change(() => change.Property.SetValue(target, change.Value), () => change.Property.SetValue(target, change.Old));
				renamed.Check(target, changes);
				return;
			}
			RequireMovable(target);
			var before = target.Parent;
			var after = op == "delete" ? world!.TemporaryContainer : Find(Text(a, "parent"));
			if (after == target || after.IsDescendantOf(target)) throw new ArgumentException("Reparenting would create a cycle.");
			string oldName = target.Name;
			edit.Change(() => { world!.CreatorContext.Selections.Deselect(target); target.Parent = after; }, () => target.Parent = before);
			if (op == "reparent") renamed.Check(target, oldName);
		}
		JsonNode? Bridge(string op, JsonObject a)
		{
			CheckContext(token);
			switch (op)
			{
				case "create":
					operations++;
					string id = "@" + ++nextId;
					a["handle"] = id;
					if (!live) { commands.Add((op, a)); return JsonValue.Create(id); }
					Apply(op, a);
					return JsonValue.Create(handles[id].ObjectID);
				case "set": case "reparent": case "delete":
					operations++;
					if (live) Apply(op, a); else commands.Add((op, a));
					return null;
				case "get":
					var item = Query(a);
					var result = Summary(item); result["properties"] = Properties(item); return result;
				case "children": return Listing(Query(a, "/Environment").GetChildren().Where(Visible), a);
				case "descendants":
					Type? filter = a["class_name"] != null ? InstanceType(Text(a, "class_name")) : null;
					return Listing(Query(a, "/Environment").GetDescendants().Where(i => Visible(i) && (filter == null || filter.IsInstanceOfType(i))), a);
				case "find":
					try { return Summary(Resolve(Text(a, "path"))); }
					catch (ArgumentException) { return null; }
				case "selection": return SummaryArray(world!.CreatorContext.Selections.SelectedInstances);
				case "bounds": return BoundsJson(Bounds(Query(a)));
				case "raycast": return Raycast(a, token);
				case "overlap": return OverlapBox(a);
				default: throw new ArgumentException("Unknown scene function: " + op);
			}
		}
		var execution = await GarkLuau.RunAsync(Text(args, "source"), Bridge, () => CheckContext(token), token);
		CheckContext(token);
		var elapsed = Stopwatch.StartNew();
		int applied = 0;
		foreach (var (op, a) in commands)
		{
			CheckContext(token);
			if (++applied % 500 == 0) Progress?.Invoke($"Applying {applied}/{commands.Count} edits");
			Apply(op, a);
			if (elapsed.ElapsedMilliseconds >= 8) { await Task.Delay(1, token); elapsed.Restart(); }
		}
		CheckContext(token);
		edit.Finish("Gark: " + Text(args, "title", "procedural scene edit"));
		// Temporary handles in the returned value become real IDs, so no lookup table is needed.
		JsonNode? Real(JsonNode? node) => node switch
		{
			JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Real(p.Value)))),
			JsonArray array => new JsonArray(array.Select(Real).ToArray()),
			JsonValue v when v.TryGetValue<string>(out var s) && handles.TryGetValue(s, out var made) => JsonValue.Create(made.ObjectID),
			_ => node?.DeepClone()
		};
		var created = handles.Values.Where(Visible).ToHashSet();
		var roots = created.Where(i => i.Parent == null || !created.Contains(i.Parent)).ToArray();
		var response = new JsonObject { ["operations"] = operations, ["created"] = created.Count,
			["created_roots"] = SummaryArray(roots.Take(50)), ["result"] = Real(execution.Result), ["output"] = execution.Output };
		if (roots.Length > 50) response["created_roots_total"] = roots.Length;
		renamed.AddTo(response);
		if (returnIds && !live)
		{
			var mapping = new JsonObject();
			foreach (var (handle, item) in handles) mapping[handle] = item.ObjectID;
			response["ids"] = mapping;
		}
		return response;
	}

	/// <summary>Sibling names are unique in Polytoria: a clashing Name gets a number appended. Agents must hear about it.</summary>
	private sealed class Renames
	{
		private const int Limit = 50;
		private readonly JsonArray _items = [];
		private int _count;
		internal void Check(Instance item, List<(System.Reflection.PropertyInfo Property, object? Old, object? Value)> changes)
		{
			if (changes.FirstOrDefault(c => c.Property.Name == "Name").Value is string requested) Check(item, requested);
		}
		internal void Check(Instance item, string? requested)
		{
			if (requested == null || item.Name == requested) return;
			if (++_count <= Limit) _items.Add((JsonNode)new JsonObject { ["id"] = item.ObjectID, ["requested"] = requested, ["actual"] = item.Name, ["path"] = GarkValues.PathOf(item) });
		}
		internal void AddTo(JsonObject result)
		{
			if (_count == 0) return;
			result["renamed"] = _items;
			if (_count > Limit) result["renamed_total"] = _count;
			result["renamed_note"] = "A sibling already used the requested name, so Polytoria appended a number. Refer to these objects by ID or actual name.";
		}
	}
}
