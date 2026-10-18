// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

#if DEBUG && CREATOR
using Godot;
using Polytoria.Creator.Gark;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Datamodel.Resources;
using Polytoria.Formats;
using Polytoria.Shared;
using Polytoria.Shared.AssetLoaders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;
using File = System.IO.File;

namespace Polytoria.DatamodelTest;

public partial class GarkIntegrationTests
{
	private async Task ConstructionChecks(GarkTools tools, World world)
	{
		int before = world.Environment.GetChildren().Length;
		var result = await tools.ExecuteAsync("run_scene_code", new() { ["source"] = """
			assert(game == nil and Instance == nil and io == nil and os == nil and require == nil)
			local m = scene.create('Model', {Name='Procedural café', Position={1000,0,0}})
			local function step(i)
				return scene.create('Part', {Name='Step '..i, LocalPosition={i*3,math.sin(i),0}, Size={3,1,6}, Color='#336699', Anchored=true}, m)
			end
			for i=1,125 do step(i) end
			print('Built', 125, 'café 🌲')
			return {root=m, count=125}
			""" }, default);
		var root = (Model)world.GetObjectFromID(result.Data["result"]!["root"]!.GetValue<string>())!;
		Check(root.GetChildren().Length == 125 && result.Data["operations"]!.GetValue<int>() == 126, "Luau loops/functions build beyond 100 objects in one batch");
		Check(result.Data["output"]!.ToString().Contains("café 🌲") && root.Name == "Procedural café", "construction Unicode round-trip and output");
		var first = (Part)root.GetChildren()[0];
		var position = first.Position;
		Check(Math.Abs(position.X - 1003) < 0.01 && first.Color == Color.FromHtml("#336699"), "procedural parent handles and local properties");
		await tools.ExecuteAsync("undo", new(), default);
		Check(world.Environment.GetChildren().Length == before && root.Parent == world.TemporaryContainer, "entire procedural construction undoes in one action");
		await tools.ExecuteAsync("redo", new(), default);
		Check(root.GetChildren().Length == 125 && first.Position.IsEqualApprox(position), "procedural redo preserves hierarchy and transforms");
		string id = root.ObjectID;
		await tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"local i=scene.get('{id}'); assert(i.properties.Name == 'Procedural café'); scene.set('{id}', {{Name='Edited'}}); return #scene.children('{id}')" }, default);
		Check(root.Name == "Edited", "procedural inspection and existing-object edits");
		await tools.ExecuteAsync("undo", new(), default);
		Check(root.Name == "Procedural café", "procedural property undo");
		await ExpectFailure(() => tools.ExecuteAsync("run_scene_code", new() { ["source"] = $"scene.set('{id}', {{Name='BAD'}}); scene.create('Part', {{Name='Rollback probe'}}); scene.create('UnsupportedCustomMesh', {{}})" }, default), "unsupported classes rejected during construction");
		Check(root.Name == "Procedural café" && world.Environment.GetChildren().Length == before + 1, "failed construction rolls back prior edits and creations");
		await ExpectFailure(() => tools.ExecuteAsync("run_scene_code", new() { ["source"] = "scene.create('Part', {}); error('deliberate')" }, default), "Luau errors reject staged edits");
		await ExpectFailure(() => tools.ExecuteAsync("run_scene_code", new() { ["source"] = "local t={}; t.self=t; return t" }, default), "cyclic return data fails without a native crash");
		using (var stop = new CancellationTokenSource(150))
		{
			bool cancelled = false;
			try { await tools.ExecuteAsync("run_scene_code", new() { ["source"] = "scene.create('Part', {Name='Cancelled'}); while true do end" }, stop.Token); }
			catch (OperationCanceledException) { cancelled = true; }
			Check(cancelled && world.Environment.GetChildren().Length == before + 1, "Stop interrupts an infinite Luau loop without applying staged edits");
		}
		using (var stop = new CancellationTokenSource(100))
		{
			bool cancelled = false;
			try { await tools.ExecuteAsync("run_scene_code", new() { ["source"] = "table.sort({3,2,1}, function(a,b) while true do end end)" }, stop.Token); }
			catch (OperationCanceledException) { cancelled = true; }
			Check(cancelled, "Stop also interrupts a custom sorting callback");
		}
		using (var stop = new CancellationTokenSource())
		{
			var operation = tools.ExecuteAsync("run_scene_code", new() { ["source"] = "local m=scene.create('Model',{Name='CancelDuringApply'}); for i=1,1500 do scene.create('Part',{Position={i,500,0}},m) end" }, stop.Token);
			while (!operation.IsCompleted && world.Environment.FindChild("CancelDuringApply") == null)
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			stop.Cancel();
			bool cancelled = false;
			try { await operation; } catch (OperationCanceledException) { cancelled = true; }
			Check(cancelled && world.Environment.GetChildren().Length == before + 1, "Stop during scene application rolls back already-created objects");
		}
		await tools.ExecuteAsync("run_scene_code", new() { ["source"] = "scene.create('Part',{Name='Keyed vector',Position={x=1,y=2,z=3}})" }, default);
		Check(world.Environment.FindChild("Keyed vector") is Part { Position: var keyed } && keyed == new Vector3(1, 2, 3), "construction accepts {x=,y=,z=} vectors");
		await tools.ExecuteAsync("undo", new(), default);
		try
		{
			await tools.ExecuteAsync("run_scene_code", new() { ["source"] = "scene.create('Part',{Position={1,2}})" }, default);
			Check(false, "invalid vectors must fail");
		}
		catch (ArgumentException ex) { Check(ex.Message.Contains("Part.Position") && ex.Message.Contains("[x,y,z]") && ex.Message.Contains("array of 2"), "construction errors identify the property and expected representation: " + ex.Message); }
		// Existing serialization, no new object/resource types or gameplay source are required.
		var data = PolyFormat.SaveModel(root);
		Check(data.Objects[0].ClassName == "Model" && data.Objects[0].Children.All(c => c.ClassName == "Part"), "procedural output uses only standard serialized client classes");
		var loaded = PolyFormat.LoadModelFromRootData(world, data, world.Environment)!;
		Check(loaded.GetChildren().Length == 125 && ((Part)loaded.GetChildren()[0]).Position.IsEqualApprox(position), "procedural model round-trips through existing format");
		loaded.Delete(); root.Delete();
	}
	private async Task SpatialChecks(GarkTools tools, World world)
	{
		var created = await tools.ExecuteAsync("create_instances", Args("""
			{"objects":[
			{"class_name":"Part","properties":{"Name":"Spatial floor","Position":[2000,0,0],"Size":[40,2,40],"Anchored":true}},
			{"class_name":"Part","properties":{"Name":"Spatial block","Position":[2000,15,0],"Size":[4,4,4],"Anchored":true}},
			{"class_name":"Part","properties":{"Name":"Spatial rotated","Position":[2010,15,0],"Size":[4,2,2],"Rotation":[0,45,0],"Anchored":true}}]}
			"""), default);
		string[] ids = created.Data["created"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToArray();
		var block = (Part)world.GetObjectFromID(ids[1])!;
		var rotated = (Part)world.GetObjectFromID(ids[2])!;
		var bounds = await tools.ExecuteAsync("get_bounds", new() { ["ids"] = new JsonArray(ids[2]) }, default);
		Check(Math.Abs(bounds.Data["combined"]!["size"]![0]!.GetValue<float>() - 6 / MathF.Sqrt(2)) < 0.01f, "bounds include rotation and scale");
		var ray = await tools.ExecuteAsync("raycast", new() { ["origin"] = new JsonArray(2000, 30, 0), ["direction"] = new JsonArray(0, -2, 0), ["ignore_ids"] = new JsonArray(ids[1]) }, default);
		Check(ray.Data["hit"]!.GetValue<bool>() && ray.Data["instance"]!["id"]!.GetValue<string>() == ids[0] && Math.Abs(ray.Data["position"]![1]!.GetValue<float>() - 1) < .01, "physics raycast finds floor and ignores selected object");
		var placed = await tools.ExecuteAsync("place_on_ground", new() { ["ids"] = new JsonArray(ids[1], ids[2]), ["clearance"] = 0.5f }, default);
		Check(Math.Abs(block.Position.Y - 3.5f) < .01 && Math.Abs(rotated.Position.Y - 2.5f) < .01 && placed.Data["moved"]!.AsArray().Count == 2, "ground placement respects bounds and clearance");
		await tools.ExecuteAsync("undo", new(), default);
		Check(block.Position.Y == 15 && rotated.Position.Y == 15, "ground placement is one undo action");
		await tools.ExecuteAsync("align_instances", new() { ["ids"] = new JsonArray(ids[1], ids[2]), ["axis"] = "y", ["edge"] = "min", ["coordinate"] = 5 }, default);
		Check(Math.Abs(block.Position.Y - 7) < .01 && Math.Abs(rotated.Position.Y - 6) < .01, "alignment uses bounding edges for differently sized objects");
		await tools.ExecuteAsync("undo", new(), default);
		var overlap = await tools.ExecuteAsync("overlap_box", new() { ["center"] = new JsonArray(2000, 15, 0), ["size"] = new JsonArray(5, 5, 5) }, default);
		Check(overlap.Data["items"]!.AsArray().Any(n => n!["id"]!.GetValue<string>() == ids[1]), "box overlap finds expected scene geometry");
		var distance = await tools.ExecuteAsync("measure_distance", new() { ["from"] = ids[1], ["to"] = ids[2] }, default);
		Check(Math.Abs(distance.Data["center_distance"]!.GetValue<float>() - 10) < .01 && distance.Data["bounds_gap"]!.GetValue<float>() > 5, "distance reports center and bounds separation");
		await ExpectFailure(() => tools.ExecuteAsync("place_on_ground", new() { ["ids"] = new JsonArray(ids[1], ids[2]), ["max_distance"] = .01f }, default), "missing ground fails before moving any objects");
		Check(block.Position.Y == 15 && rotated.Position.Y == 15, "failed placement preserves both objects");
		foreach (string id in ids) world.GetObjectFromID(id)!.Delete();
	}
	private async Task AssetChecks(World world)
	{
		// Model bytes come from the real packed serializer, served by a fake public Toolbox endpoint.
		var fixture = world.New<Model>(); fixture.Name = "Toolbox fixture"; fixture.Parent = world.TemporaryContainer;
		var part = world.New<Part>(); part.Name = "Imported part"; part.Parent = fixture;
		const string linkedSource = "return { text = 'café 🌲' }";
		world.IO.WriteBytesToPath("scripts/asset-fixture.module.luau", Encoding.UTF8.GetBytes(linkedSource));
		var script = world.New<ModuleScript>(); script.Parent = fixture;
		script.LinkedScript = world.Assets.GetFileLinkByPath("scripts/asset-fixture.module.luau");
		var sound = world.New<Sound>(); sound.Parent = fixture; sound.Audio = world.New<PTAudioAsset>();
		byte[] packed = await PackedFormat.PackModel(fixture);
		fixture.Delete();
		using var thumbnail = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8); thumbnail.Fill(Colors.CornflowerBlue);
		var handler = new AssetHandler(packed, thumbnail.SavePngToBuffer());
		var tools = new GarkTools(CreatorService.CurrentSession, world) { AssetLibrary = new GarkAssetLibrary(new HttpClient(handler)) };
		var search = await tools.ExecuteAsync("search_assets", Args("""{"query":"tree & bush","type":"model","page":2}"""), default);
		Check(search.Data["items"]![0]!["name"]!.ToString() == "Toolbox café" && search.Data["has_more"]!.GetValue<bool>() && handler.LastUri!.Contains("tree%20%26%20bush"), "Toolbox search encodes query and preserves pagination/Unicode");
		var preview = await tools.ExecuteAsync("inspect_asset", Args("""{"type":"model","id":701}"""), default);
		Check(preview.Image is { Length: > 10 } && preview.Data["creator"]!.ToString() == "Fixture author", "Toolbox metadata and thumbnail reach the tool result");
		var imported = await tools.ExecuteAsync("import_asset", Args("""{"type":"model","id":701,"name":"Imported café","properties":{"Position":[3000,10,0]}}"""), default);
		var item = (Model)world.GetObjectFromID(imported.Data["id"]!.GetValue<string>())!;
		Check(item.Name == "Imported café" && item.GetChildren().OfType<Part>().Count() == 1 && item.Position == new Vector3(3000, 10, 0), "Toolbox model imports through the existing packed format");
		var importedScript = item.GetChildren().OfType<ModuleScript>().Single();
		Check(importedScript.LinkedScript != null && Encoding.UTF8.GetString(importedScript.LinkedScript.ReadFile()!) == linkedSource && !importedScript.Ran,
			"packed import preserves linked script content without executing it");
		Check(item.GetChildren().OfType<Sound>().Single().Audio is PTAudioAsset, "imported resource references survive object-ID remapping");
		await tools.ExecuteAsync("undo", new(), default);
		Check(item.Parent == world.TemporaryContainer, "Toolbox import undo");
		await tools.ExecuteAsync("redo", new(), default);
		Check(item.Parent == world.Environment && item.Position == new Vector3(3000, 10, 0), "Toolbox import redo preserves placement");
		var repeated = await tools.ExecuteAsync("import_asset", Args("""{"type":"model","id":701}"""), default);
		string repeatedId = repeated.Data["id"]!.GetValue<string>();
		Check(repeatedId != item.ObjectID && world.GetObjectFromID(item.ObjectID) == item, "repeated imports keep distinct IDs and preserve existing lookups");
		world.GetObjectFromID(repeatedId)!.Delete();
		int beforeFailure = world.Environment.GetChildren().Length;
		await ExpectFailure(() => tools.ExecuteAsync("import_asset", Args("""{"type":"model","id":701,"properties":{"UnsupportedProperty":true}}"""), default), "invalid import properties reject the insertion");
		Check(world.Environment.GetChildren().Length == beforeFailure && world.GetObjectFromID(item.ObjectID) == item
			&& Encoding.UTF8.GetString(importedScript.LinkedScript!.ReadFile()!) == linkedSource, "failed import preserves previous objects and linked files");
		bool useLoader = AssetLoader.Singleton.UseAssetLoader;
		AssetLoader.Singleton.UseAssetLoader = false;
		try
		{
			foreach (string type in new[] { "mesh", "image", "audio" })
			{
				await tools.ExecuteAsync("search_assets", new() { ["query"] = "fixture", ["type"] = type }, default);
				var result = await tools.ExecuteAsync("import_asset", new() { ["type"] = type, ["id"] = 701 }, default);
				var obj = world.GetObjectFromID(result.Data["id"]!.GetValue<string>())!;
				Check(obj is Datamodel.Mesh { Asset: PTMeshAsset { AssetID: 701 } } || obj is Image3D { Image: PTImageAsset { ImageID: 701 } } || obj is Sound { Audio: PTAudioAsset { AudioID: 701 } }, type + " import uses the existing client asset reference");
				obj.Delete();
			}
		}
		finally { AssetLoader.Singleton.UseAssetLoader = useLoader; }
		await ExpectFailure(() => tools.ExecuteAsync("inspect_asset", Args("""{"type":"model","id":999}"""), default), "inspect_asset asks for a search first");
		var direct = await tools.ExecuteAsync("import_asset", Args("""{"type":"model","id":999}"""), default);
		var directItem = (Instance)world.GetObjectFromID(direct.Data["id"]!.GetValue<string>())!;
		Check(directItem.Name == "Model 999" && directItem.GetChildren().OfType<Part>().Any(), "Toolbox models import by ID without a prior search");
		directItem.Delete();
		using (var stop = new CancellationTokenSource())
		{
			stop.Cancel();
			await ExpectFailure(() => tools.ExecuteAsync("search_assets", Args("""{"type":"model","query":"tree"}"""), stop.Token), "Toolbox requests obey cancellation");
		}
		item.Delete();
	}
	private async Task LiveAssetChecks(GarkTools tools, World world)
	{
		var search = await tools.ExecuteAsync("search_assets", Args("""{"query":"tree","type":"model"}"""), default);
		var assets = search.Data["items"]!.AsArray();
		var asset = assets.FirstOrDefault(a => a!["name"]!.GetValue<string>().Equals("Tree", StringComparison.OrdinalIgnoreCase))
			?? assets.FirstOrDefault() ?? throw new Exception("Live Toolbox search returned no tree models.");
		uint id = asset["id"]!.GetValue<uint>();
		Check(search.Data["last_page"]!.GetValue<int>() >= 1, "live Toolbox search and pagination");
		var preview = await tools.ExecuteAsync("inspect_asset", new() { ["type"] = "model", ["id"] = id }, default);
		Check(preview.Image is { Length: > 100 } || preview.Data["preview_error"]?.ToString().Contains("404") == true,
			"live Toolbox preview provides an image or reports the CDN's missing thumbnail");
		var imported = await tools.ExecuteAsync("import_asset", new() { ["type"] = "model", ["id"] = id }, default);
		var item = (Instance)world.GetObjectFromID(imported.Data["id"]!.GetValue<string>())!;
		Check(item.GetDescendants().Length > 0, "live Toolbox model imports into the disposable world");
		var data = PolyFormat.SaveModel(item);
		var restored = PolyFormat.LoadModelFromRootData(world, data, world.Environment)!;
		Check(restored.GetDescendants().Length == item.GetDescendants().Length, "live Toolbox model round-trips through existing client serialization");
		restored.Delete(); item.Delete();
	}
	private sealed class AssetHandler(byte[] model, byte[] thumbnail) : HttpMessageHandler
	{
		internal string? LastUri;
		internal byte[] ModelBytes = model;
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastUri = request.RequestUri!.AbsoluteUri;
			byte[] data = LastUri.Contains("get-model") ? ModelBytes : LastUri.Contains("thumbnail") ? thumbnail : Encoding.UTF8.GetBytes("""
				{"meta":{"total":8,"currentPage":2,"lastPage":3},"data":[{"id":701,"name":"Toolbox café","creatorID":1,"creatorName":"Fixture author","thumbnailUrl":"https://cdn.polytoria.com/thumbnail.png"}]}
				""");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
		}
	}
}
#endif
