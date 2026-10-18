// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

#if DEBUG && CREATOR
using Godot;
using Polytoria.Creator.Gark;
using Polytoria.Datamodel;
using Polytoria.Enums;
using Polytoria.Providers.Datastore;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Polytoria.DatamodelTest;

/// <summary>Regression checks for issues agents reported while building with Gark.</summary>
public partial class GarkIntegrationTests
{
	private static float F(JsonNode? node, int index) => node![index]!.GetValue<float>();

	private async Task FeedbackChecks(GarkTools tools, World world)
	{
		// Sibling names are unique; the requested name must not silently change.
		var walls = await tools.ExecuteAsync("create_instances", Args("""{"objects":[{"class_name":"Folder","name":"Gark rename probe"},{"class_name":"Part","parent":"@0","name":"Wall"},{"class_name":"Part","parent":"@0","properties":{"Name":"Wall"}}]}"""), default);
		var renamed = walls.Data["renamed"]!.AsArray();
		Check(renamed.Count == 1 && renamed[0]!["requested"]!.ToString() == "Wall" && renamed[0]!["actual"]!.ToString() != "Wall", "create_instances reports sibling auto-renames: " + renamed.ToJsonString());
		string probe = walls.Data["created"]![0]!["id"]!.ToString();
		var scripted = await tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"local p = scene.create('Part', {{Name='Wall'}}, '{probe}') return {{part = p, list = {{p}}}}" }, default);
		string realId = scripted.Data["result"]!["part"]!.ToString();
		Check(world.GetObjectFromID(realId) is Part && scripted.Data["result"]!["list"]![0]!.ToString() == realId && scripted.Data["ids"] == null,
			"run_scene_code returns real IDs instead of a handle map");
		Check(scripted.Data["renamed"]!.AsArray().Count == 1 && scripted.Data["created_roots"]!.AsArray().Count == 1, "run_scene_code reports renames and created roots");
		var mapped = await tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"scene.create('Part', {{}}, '{probe}')", ["return_ids"] = true }, default);
		Check(mapped.Data["ids"]!.AsObject().Count == 1, "return_ids opts into the full handle map");
		var props = await tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"local c = scene.children('{probe}', {{'Name','Size'}}) local d = scene.descendants('{probe}', 'Part', true) return {{c[1].properties.Size, d[1].properties.Anchored ~= nil, c[1].properties.Color == nil}}" }, default);
		Check(props.Data["result"]![0]!.AsArray().Count == 3 && props.Data["result"]![1]!.GetValue<bool>() && props.Data["result"]![2]!.GetValue<bool>(), "scene.children/descendants include chosen properties");
		await ExpectFailure(() => tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"local p = scene.create('Part', {{}}, '{probe}') return scene.get(p)" }, default), "staged handles explain why they cannot be queried yet");
		var live = await tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"local before = #scene.children('{probe}') local p = scene.create('Part', {{Name='Live', Size={{2,3,4}}}}, '{probe}') scene.set(p, {{Size={{5,5,5}}}}) return {{before, #scene.children('{probe}'), scene.get(p).properties.Size[1], scene.bounds(p).size[2]}}", ["apply_as_you_go"] = true }, default);
		var counts = live.Data["result"]!.AsArray();
		Check(counts[1]!.GetValue<int>() == counts[0]!.GetValue<int>() + 1 && counts[2]!.GetValue<float>() == 5 && Math.Abs(counts[3]!.GetValue<float>() - 5) < 0.01, "apply_as_you_go queries see earlier edits");
		await ExpectFailure(() => tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"scene.create('Part', {{Name='Rolled back'}}, '{probe}') error('stop')", ["apply_as_you_go"] = true }, default), "apply_as_you_go errors fail");
		Check(!((Instance)world.GetObjectFromID(probe)!).GetChildren().Any(c => c.Name == "Rolled back"), "apply_as_you_go errors roll back applied edits");

		// Model transforms are a pivot and a scale factor; bounds show the contents.
		var model = world.New<Model>(); model.Name = "Gark scale probe"; model.Parent = world.Environment; model.Position = new(200, 0, 0);
		var left = world.New<Part>(); left.Parent = model; left.Position = new(198, 1, 0); left.Size = new(2, 2, 2);
		var right = world.New<Part>(); right.Parent = model; right.Position = new(206, 1, 0); right.Size = new(2, 2, 2);
		var lamp = world.New<PointLight>(); lamp.Parent = model; lamp.Position = new(202, 1, 0); lamp.Range = 10;
		var label = world.New<Text3D>(); label.Parent = model; label.FontSize = 10; label.Position = new(202, 4, 0);
		var inspected = await tools.ExecuteAsync("inspect_instance", new() { ["id"] = model.ObjectID }, default);
		Check(inspected.Data["bounds"] != null && inspected.Data["transform_note"] != null, "Model inspection labels pivot/scale and includes bounds");
		var scaled = await tools.ExecuteAsync("scale_instances", new() { ["ids"] = new JsonArray(model.ObjectID), ["factor"] = 2, ["pivot"] = "bottom" }, default);
		var pivot = scaled.Data["pivot"];
		Check(Math.Abs(left.Size.X - 4) < 0.01 && Math.Abs(right.Position.X - left.Position.X - 16) < 0.01
			&& left.Position.DistanceTo(new Vector3(2 * 198 - F(pivot, 0), 2 * 1 - F(pivot, 1), -F(pivot, 2))) < 0.01
			&& Math.Abs(F(scaled.Data["bounds_after"]!["min"], 1) - F(scaled.Data["bounds_before"]!["min"], 1)) < 0.05,
			$"scale_instances scales sizes and spacing about the bottom pivot: {scaled.Data.ToJsonString()}");
		Check(Math.Abs(lamp.Range - 20) < 0.01 && Math.Abs(label.FontSize - 20) < 0.01 && scaled.Data["lights_adjusted"]!.GetValue<int>() == 1, "scale_instances scales light range and text size");
		await tools.ExecuteAsync("undo", new(), default);
		Check(Math.Abs(left.Size.X - 2) < 0.01 && Math.Abs(lamp.Range - 10) < 0.01 && Math.Abs(label.FontSize - 10) < 0.01 && Math.Abs(right.Position.X - 206) < 0.01, "scale_instances undoes in one step");

		// Replication and collision facts an agent cannot infer from types.
		var lighting = (await tools.ExecuteAsync("class_schema", Args("""{"class_name":"Lighting"}"""), default)).Data;
		var part = (await tools.ExecuteAsync("class_schema", Args("""{"class_name":"Part"}"""), default)).Data;
		Check(lighting["replication"]!["initial_only"]!.AsArray().Any(n => n!.ToString() == "Skybox"), "class_schema flags Lighting.Skybox as not replicating later changes");
		Check(part["replication"]!["replicated"]!.AsArray().Any(n => n!.ToString() == "Color") && part["replication"]!["replicated"]!.AsArray().Any(n => n!.ToString() == "Position"), "class_schema lists replicated Part properties: " + part["replication"]!.ToJsonString());
		Check(part["notes"]!.ToJsonString().Contains("bitmask"), "class_schema explains collision bitmasks");
		Check((await tools.ExecuteAsync("class_schema", Args("""{"class_name":"Model"}"""), default)).Data["notes"]!.ToJsonString().Contains("pivot"), "class_schema explains Model pivot/scale");

		// Text3D orientation and alignment, measured on the real nodes the guide describes.
		var text = world.New<Text3D>(); text.Parent = world.Environment; text.Text = "Gark"; text.FontSize = 20; text.Position = new(0, 60, 0);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var label3D = text.GDNode3D.FindChildren("*", "Label3D", true, false).OfType<Label3D>().First();
		Vector3 readableFrom = label3D.GlobalBasis.Z, flow = label3D.GlobalBasis.X;
		GD.Print($"GARK MEASURE text readable_from={readableFrom} flow={flow} forward={text.Forward} right={text.Right}");
		Check(readableFrom.DistanceTo(-text.Forward) < 0.01f && flow.DistanceTo(-text.Right) < 0.01f, "guide: Text3D is read from its -Forward side and flows toward -Right");
		var extents = new Dictionary<string, Aabb>();
		foreach (var vertical in new[] { TextVerticalAlignmentEnum.Top, TextVerticalAlignmentEnum.Middle, TextVerticalAlignmentEnum.Bottom })
		{
			text.VerticalAlignment = vertical;
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			extents[vertical.ToString()] = text.GetBounds();
		}
		foreach (var horizontal in new[] { TextHorizontalAlignmentEnum.Left, TextHorizontalAlignmentEnum.Right })
		{
			text.HorizontalAlignment = horizontal;
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			extents[horizontal.ToString()] = text.GetBounds();
		}
		foreach (var (key, box) in extents) GD.Print($"GARK MEASURE text {key}: min={box.Position} max={box.End}");
		Check(extents["Top"].End.Y <= 60.01f && extents["Bottom"].Position.Y >= 59.99f, "guide: VerticalAlignment Top hangs below Position and Bottom sits above it");
		Check(extents["Left"].End.X <= 0.01f && extents["Right"].Position.X >= -0.01f, "guide: HorizontalAlignment Left starts at Position and runs toward -X at zero rotation");
		GD.Print($"GARK MEASURE text height_at_fontsize_20={extents["Top"].Size.Y}");
		Check(Math.Abs(extents["Top"].Size.Y / 20 - 0.13f) < 0.01f, "guide: a Text3D line is about 0.13 x FontSize studs tall");
		text.Delete();

		// Framing ignores invisible giants such as boundary walls.
		var wall = world.New<Part>(); wall.Name = "Gark invisible wall"; wall.Parent = world.Environment;
		wall.Position = new(0, 0, 25000); wall.Size = new(50000, 500, 2); wall.Color = new Color(1, 1, 1, 0);
		var far = world.New<Part>(); far.Name = "Gark far giant"; far.Parent = world.Environment; far.Position = new(-20000, 0, 0); far.Size = new(4000, 4000, 4000);
		if (DisplayServer.GetName() != "headless")
		{
			var framed = await tools.ExecuteAsync("capture_viewport", Args("""{"view":"iso","width":320,"height":180}"""), default);
			var size = framed.Data["focus_bounds"]!["size"]!;
			Check(F(size, 0) < 5000 && F(size, 2) < 5000 && framed.Data["framing"]!["ignored_parts"]!.GetValue<int>() >= 2, "whole-world framing skips invisible walls and far giants: " + framed.Data.ToJsonString());
			var pinned = await tools.ExecuteAsync("capture_viewport", new() { ["focus_ids"] = new JsonArray(wall.ObjectID), ["width"] = 160, ["height"] = 90 }, default);
			Check(F(pinned.Data["focus_bounds"]!["size"], 0) > 40000, "explicitly focused invisible parts are still framed");
		}
		wall.Delete(); far.Delete();

		// Large listings are cut by size with a paging hint.
		await tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"for i = 1, 600 do scene.create('Part', {{Name='Row'..i}}, '{probe}') end" }, default);
		var listing = await tools.ExecuteAsync("list_instances", new() { ["parent"] = probe, ["include_properties"] = new JsonArray("*"), ["limit"] = 5000 }, default);
		Check(listing.Data["items"]!.AsArray().Count < 600 && listing.Data["next_offset"] != null && listing.Data["hint"] != null && listing.Data.ToJsonString().Length < 60_000,
			"large listings stop near 48 KB with next_offset");
		await tools.ExecuteAsync("delete_instances", new() { ["ids"] = new JsonArray(probe, model.ObjectID) }, default);

		// Local datastores enforce the live rules.
		var store = new LocalDatastoreProvider();
		await ExpectFailure(() => store.WriteData("table", new Dictionary<string, object>()), "local datastore rejects tables like the live server");
		await store.WriteData("whole", 3);
		Check(await store.ReadData("whole") is double d && d == 3, "local datastore returns numbers as doubles like the live server");
		int writes = 0;
		try { while (writes < 200) { await store.WriteData("spam", writes); writes++; } }
		catch (PTDatastoreProvider.PTDatastoreQuotaException) { }
		Check(writes < 200, $"local datastore enforces the live write quota (stopped after {writes} writes)");
	}
}
#endif
