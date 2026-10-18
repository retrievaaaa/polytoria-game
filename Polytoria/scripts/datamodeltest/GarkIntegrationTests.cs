// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

#if DEBUG && CREATOR
using Godot;
using Polytoria.Creator.Gark;
using Polytoria.Creator.Managers;
using Polytoria.Creator.UI;
using Polytoria.Creator.UI.Popups;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Shared;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using File = System.IO.File;

namespace Polytoria.DatamodelTest;

/// <summary>
/// Run with --creator --garktest. Add --garkplaytest for the real server/client playtest checks,
/// --garkassetlive to hit the public Toolbox.
/// </summary>
public partial class GarkIntegrationTests : Node
{
	private int _checks;
	private string _folder = "";
	private void Check(bool condition, string message)
	{
		if (!condition) throw new Exception("FAIL: " + message);
		_checks++;
		GD.Print("GARK PASS: " + message);
	}
	private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();
	public override void _Ready() => Callable.From(Run).CallDeferred();

	private async void Run()
	{
		try
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			foreach (var popup in GetTree().Root.FindChildren("*", "", true, false).OfType<WelcomePopup>()) popup.QueueFree();
			UnitChecks();
			await OfflineProtocolChecks();
			_folder = ProjectSettings.GlobalizePath("res://target/gark-qa/project-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
			File.Copy(ProjectSettings.GlobalizePath("res://modules/creator/world-templates/baseplate/main.poly"), Path.Combine(_folder, "main.poly"));
			await ProjectManager.NewProject(_folder, new() { MainWorld = "main.poly", ProjectName = "Gark integration checks" }, true);
			var world = World.Current!;
			var tools = new GarkTools(CreatorService.CurrentSession, world);
			if (Globals.ReadCmdArgs().ContainsKey("garkinterop"))
			{
				// Leave Creator running on a disposable project so real agents (claude, codex...) can connect.
				await RemoveFixtureFromRecents();
				await tools.ExecuteAsync("create_script", new() { ["path"] = "scripts/score.server.luau", ["source"] = "local score = 0\nprint('score ready')\n" }, default);
				GD.Print($"GARK INTEROP READY {GarkMcpHost.Instance?.Url} {_folder}");
				return;
			}
			var context = (await tools.ExecuteAsync("get_context", new(), default)).Data;
			Check(context["ready"]!.GetValue<bool>() && Path.GetFullPath(context["project"]!["folder"]!.ToString()) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder)), "active project context with absolute folder: " + context.ToJsonString());
			Check(context["project_worlds"]!.AsArray().Any(w => w!.ToString() == "main.poly"), "context lists project worlds");
			Check((await tools.ExecuteAsync("class_schema", Args("""{"class_name":"Part"}"""), default)).Data["editable_properties"]!["Position"] != null, "real property schema");

			var created = await tools.ExecuteAsync("create_instances", Args("""
				{"objects":[{"class_name":"Part","properties":{"Name":"Gark test cube","Position":[0,2,0],"Size":{"x":4,"y":4,"z":4},"Color":"#ffff00"}}]}
				"""), default);
			string id = created.Data["created"]![0]!["id"]!.GetValue<string>();
			var part = (Part)world.GetObjectFromID(id)!;
			Check(part.Name == "Gark test cube" && part.Size == new Vector3(4, 4, 4) && part.Position == new Vector3(0, 2, 0), "world object creation, coordinates and {x,y,z} vectors");
			await tools.ExecuteAsync("set_properties", new() { ["id"] = id, ["properties"] = Args("""{"Color":"#ff4400"}""") }, default);
			Check(part.Color == Color.FromHtml("#ff4400"), "world property editing");
			var undone = await tools.ExecuteAsync("undo", new(), default);
			Check(part.Color == Color.FromHtml("#ffff00") && undone.Data["undone"]![0]!.ToString().Contains("Gark test cube"), "world edit undo reports what was undone");
			await tools.ExecuteAsync("redo", new(), default);
			Check(part.Color == Color.FromHtml("#ff4400"), "world edit redo");
			var selection = new JsonObject { ["ids"] = new JsonArray(id) };
			await tools.ExecuteAsync("select_instances", selection, default);
			Check(world.CreatorContext.Selections.SelectedInstances.Contains(part), "selection tool");
			await tools.ExecuteAsync("delete_instances", (JsonObject)selection.DeepClone(), default);
			Check(part.Parent == world.TemporaryContainer, "delete uses editor history");
			await tools.ExecuteAsync("undo", new(), default);
			Check(part.Parent == world.Environment, "delete undo restores object");
			var beforeCount = world.Environment.GetChildren().Length;
			var duplicated = await tools.ExecuteAsync("duplicate_instances", (JsonObject)selection.DeepClone(), default);
			Check(world.Environment.GetChildren().Length == beforeCount + 1 && duplicated.Data["duplicates"]![0]!["id"]!.ToString() != id, "duplicate tool returns the copy");
			await tools.ExecuteAsync("undo", new(), default);
			Check(world.Environment.GetChildren().Length == beforeCount, "duplicate undo");
			var listing = await tools.ExecuteAsync("list_instances", Args("""{"parent":"/Environment","query":"Gark test","include_properties":["Size"]}"""), default);
			Check(listing.Data["items"]!.AsArray().Count == 1 && listing.Data["items"]![0]!["properties"]!["Size"]![0]!.GetValue<float>() == 4, "scene search with selected properties");
			await HierarchyChecks(tools, world, part);
			await ConstructionChecks(tools, world);
			await SpatialChecks(tools, world);
			await AssetChecks(world);
			if (Globals.ReadCmdArgs().ContainsKey("garkassetlive")) await LiveAssetChecks(tools, world);

			const string scriptPath = "scripts/modules/gark.module.luau";
			var createdScript = await tools.ExecuteAsync("create_script", new() { ["path"] = scriptPath, ["source"] = "return { value = 1 }\n" }, default);
			Check(File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 1") && createdScript.Data["class_name"]!.ToString() == "ModuleScript", "persistent script creation with linked instance");
			var read = await tools.ExecuteAsync("read_script", new() { ["path"] = scriptPath }, default);
			Check(read.Data["text"]!.ToString().StartsWith("1: return") && read.Data["linked_instances"]!.AsArray().Count == 1, "read_script numbers lines and lists linked instances");
			await tools.ExecuteAsync("patch_script", new() { ["path"] = scriptPath, ["old_text"] = "value = 1", ["new_text"] = "value = 2", ["expected_sha256"] = read.Data["sha256"]!.ToString() }, default);
			Check(File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 2"), "exact script patch");
			await ExpectFailure(() => tools.ExecuteAsync("write_script", new() { ["path"] = scriptPath, ["source"] = "stale", ["expected_sha256"] = read.Data["sha256"]!.ToString() }, default), "stale script edit rejected");
			await tools.ExecuteAsync("undo", new(), default);
			Check(File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 1"), "script undo");
			await tools.ExecuteAsync("redo", new(), default);
			Check(File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 2"), "script redo");
			var multi = await tools.ExecuteAsync("patch_script", Args("""{"path":"scripts/modules/gark.module.luau","edits":[{"old_text":"value = 2","new_text":"value = 5"},{"old_text":"return {","new_text":"return { name = 'x',"}]}"""), default);
			Check(File.ReadAllText(Path.Combine(_folder, scriptPath)) == "return { name = 'x', value = 5 }\n" && multi.Data["replacements"]!.GetValue<int>() == 2, "multi-edit patch without a prior read");
			await ExpectFailure(() => tools.ExecuteAsync("patch_script", Args("""{"path":"scripts/modules/gark.module.luau","edits":[{"old_text":"value = 5","new_text":"value = 6"},{"old_text":"missing","new_text":"x"}]}"""), default), "failed multi-edit patch rejected");
			Check(File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 5"), "failed multi-edit patch changes nothing");
			await tools.ExecuteAsync("undo", new(), default);
			var grep = await tools.ExecuteAsync("grep_scripts", Args("""{"pattern":"value = 2","context_lines":1}"""), default);
			Check(grep.Data["matches"]!.AsArray().Count == 1 && grep.Data["matches"]![0]!["line"]!.GetValue<int>() == 1, "script grep finds file and line");
			Tabs.Singleton.Insert(new Tabs.TextEditorTab { Session = CreatorService.CurrentSession!, TargetPath = scriptPath, Title = "Gark test script" });
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var editor = Tabs.Singleton.FindTextEditor(Path.Combine(_folder, scriptPath))!.EditorRoot.CodeEditor;
			editor.Text = "return { value = 3 }\n";
			var unsaved = await tools.ExecuteAsync("read_script", new() { ["path"] = scriptPath }, default);
			Check(unsaved.Data["text"]!.ToString().Contains("value = 3") && unsaved.Data["unsaved"]!.GetValue<bool>(), "script tools read unsaved editor buffer");
			await tools.ExecuteAsync("patch_script", new() { ["path"] = scriptPath, ["old_text"] = "value = 3", ["new_text"] = "value = 4", ["expected_sha256"] = unsaved.Data["sha256"]!.ToString() }, default);
			Check(editor.Text.Contains("value = 4") && File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 4"), "script patch updates editor and disk");
			await tools.ExecuteAsync("undo", new(), default);
			Check(editor.Text.Contains("value = 3") && File.ReadAllText(Path.Combine(_folder, scriptPath)).Contains("value = 2"), "script undo preserves unsaved buffer and prior disk content");
			editor.Text = File.ReadAllText(Path.Combine(_folder, scriptPath));
			const string crlfPath = "scripts/crlf.module.luau";
			await tools.ExecuteAsync("create_script", new() { ["path"] = crlfPath, ["source"] = "local a = 1\r\nlocal b = 2\r\nreturn a + b\r\n" }, default);
			await tools.ExecuteAsync("patch_script", new() { ["path"] = crlfPath, ["old_text"] = "local a = 1\nlocal b = 2", ["new_text"] = "local a = 10\nlocal b = 20" }, default);
			Check(File.ReadAllText(Path.Combine(_folder, crlfPath)) == "local a = 10\r\nlocal b = 20\r\nreturn a + b\r\n", "patch matches LF text in CRLF files and keeps CRLF");
			var deleted = await tools.ExecuteAsync("delete_script", new() { ["path"] = crlfPath }, default);
			Check(!File.Exists(Path.Combine(_folder, crlfPath)) && deleted.Data["deleted_instances"]!.AsArray().Count == 1, "delete_script removes file and linked instance");
			await tools.ExecuteAsync("undo", new(), default);
			Check(File.Exists(Path.Combine(_folder, crlfPath)) && world.ScriptService.GetChildren().Any(c => c.Name == "crlf"), "delete_script undo restores file and instance");
			await tools.ExecuteAsync("open_script", new() { ["path"] = crlfPath, ["line"] = 2 }, default);
			Check(Tabs.Singleton.FindTextEditor(Path.Combine(_folder, crlfPath))?.EditorRoot.CodeEditor.GetCaretLine() == 1, "open_script opens the editor at a line");
			await ExpectFailure(() => tools.ExecuteAsync("read_script", Args("""{"path":"../escape.luau"}"""), default), "project traversal rejected");
			await ExpectFailure(() => tools.ExecuteAsync("read_script", Args("""{"path":"project.ptproj"}"""), default), "non-script files inaccessible");
			await ExpectFailure(() => tools.ExecuteAsync("reparent_instances", Args("""{"ids":["/Environment"],"parent":"/ScriptService"}"""), default), "static services protected");
			await ExpectFailure(() => tools.ExecuteAsync("list_instances", Args("""{"parnt":"/Environment"}"""), default), "misspelled arguments are reported instead of ignored");
			Check((await tools.ExecuteAsync("search_api", Args("""{"query":"Vector3"}"""), default)).Data["matches"]!.AsArray().Count > 0, "bundled API lookup");

			if (DisplayServer.GetName() != "headless")
			{
				var screenshot = await tools.ExecuteAsync("capture_viewport", Args("""{"position":[10,8,10],"look_at":[0,1,0],"width":640,"height":360}"""), default);
				Check(screenshot.Image is { Length: > 1000 }, "camera screenshot produces PNG");
				File.WriteAllBytes(ProjectSettings.GlobalizePath("res://target/gark-qa/viewport.png"), screenshot.Image!);
				var views = await tools.ExecuteAsync("capture_viewport", new() { ["focus_ids"] = new JsonArray(id), ["views"] = new JsonArray("iso", "top"), ["format"] = "jpeg", ["width"] = 320, ["height"] = 180 }, default);
				Check(views.Images!.Count == 2 && views.Images.All(i => i.MimeType == "image/jpeg" && i.Data[0] == 0xFF && i.Data[1] == 0xD8), "framed multi-view JPEG capture");
				var camera = world.CreatorContext.Freelook.Camera3D.GlobalPosition;
				Check(camera.IsEqualApprox(world.CreatorContext.Freelook.Camera3D.GlobalPosition), "capture leaves the editor camera alone");
				var focused = await tools.ExecuteAsync("focus_camera", new() { ["ids"] = new JsonArray(id), ["view"] = "front" }, default);
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Check(focused.Data["camera_forward"]![2]!.GetValue<float>() < -0.5f, "focus_camera frames the object from its front");
			}

			await McpChecks(world);
			await DebugToolsChecks(tools, world);
			var saved = await tools.ExecuteAsync("save_world", new(), default);
			Check(new FileInfo(Path.Combine(_folder, "main.poly")).Length > 0 && saved.Data["saved"]![0]!.ToString() == "main.poly", "world saves to project");
			World.Current = null;
			await ExpectFailure(() => tools.ExecuteAsync("set_properties", new() { ["id"] = id, ["properties"] = new JsonObject() }, default), "world switch stops stale tools");
			World.Current = world;

			GD.Print($"GARK TESTS PASSED: {_checks}");
			await RemoveFixtureFromRecents();
			GetTree().Quit();
		}
		catch (Exception ex)
		{
			GD.PrintErr("GARK TEST FAILURE: " + ex);
			try { if (_folder != "") await RemoveFixtureFromRecents(); }
			finally { GetTree().Quit(1); }
		}
	}
	private async Task RemoveFixtureFromRecents()
	{
		foreach (var recent in await ProjectManager.GetRecents(false))
			if (Path.GetFullPath(recent.FolderPath).TrimEnd('/', '\\') == Path.GetFullPath(_folder).TrimEnd('/', '\\'))
				await ProjectManager.RemoveFromRecents(recent.FolderPath);
	}
	private async Task ExpectFailure(Func<Task> action, string label)
	{
		try { await action(); }
		catch { Check(true, label); return; }
		Check(false, label);
	}

	private void UnitChecks()
	{
		Check(GarkProjectFiles.ReplaceOnce("hello world", "world", "Gark") == "hello Gark", "exact replacement helper");
		GarkProjectFiles.Replace("a a a", "a", "b", true, out int count);
		Check(count == 3, "replace_all counts every match");
		try { GarkProjectFiles.ReplaceOnce("a a", "a", "b"); Check(false, "ambiguous replacement rejected"); }
		catch (ArgumentException ex) { Check(ex.Message.Contains("2 places"), "ambiguous replacement explains how many matches"); }
		Check(GarkValues.Vector3Value(JsonNode.Parse("""{"x":1,"y":"2","z":3}""")!) == new Vector3(1, 2, 3), "vectors accept objects and numeric strings");
		Check(GarkTools.Definitions.Select(d => d.Name).Distinct().Count() == GarkTools.Definitions.Count, "tool names are unique");
		Check(GarkTools.Definitions.All(d => d.Description.Length > 20 && d.InputSchema["type"]!.ToString() == "object"), "every tool has a description and object schema");
		Check(GarkTools.Definitions.Count(d => d.Name.Contains("playtest")) >= 7 && GarkTools.Find("playtest_eval") != null, "playtest tools include playtest_eval");
	}

	private async Task HierarchyChecks(GarkTools tools, World world, Part part)
	{
		var built = await tools.ExecuteAsync("create_instances", Args("""
			{"objects":[
			{"class_name":"Model","name":"Gark house"},
			{"class_name":"Part","parent":"@0","name":"Wall","properties":{"Position":[50,2,0],"Size":[8,4,1],"Color":"red"}},
			{"class_name":"Part","parent":"@0","name":"Roof","properties":{"Position":[50,4.5,0],"Size":[8,1,8],"Material":"Brick"}}]}
			"""), default);
		var ids = built.Data["created"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToArray();
		var house = (Model)world.GetObjectFromID(ids[0])!;
		Check(house.GetChildren().Length == 2 && ((Part)house.GetChildren()[0]).Color == Colors.Red, "@N parents build a hierarchy in one call; named colors decode");
		await tools.ExecuteAsync("set_properties", Args($$$"""{"edits":[{"id":"{{{ids[1]}}}","properties":{"Size":[9,4,1]}},{"id":"{{{ids[2]}}}","properties":{"Size":[9,1,9]}}]}"""), default);
		Check(((Part)house.GetChildren()[0]).Size.X == 9 && ((Part)house.GetChildren()[1]).Size.Z == 9, "batched per-object property edits");
		await tools.ExecuteAsync("undo", new(), default);
		Check(((Part)house.GetChildren()[0]).Size.X == 8 && ((Part)house.GetChildren()[1]).Size.Z == 8, "batched edits undo together");
		await tools.ExecuteAsync("set_properties", new() { ["ids"] = new JsonArray(ids[1], ids[2]), ["properties"] = Args("""{"Anchored":true}""") }, default);
		Check(house.GetChildren().OfType<Part>().All(p => p.Anchored), "same properties applied to many objects");
		var inspected = await tools.ExecuteAsync("inspect_instance", new() { ["id"] = "/Environment/Gark house" }, default);
		Check(inspected.Data["children"]!.AsArray().Count == 2 && inspected.Data["bounds"] != null, "inspect by path with children and bounds");
		var moved = await tools.ExecuteAsync("reparent_instances", new() { ["ids"] = new JsonArray(part.ObjectID), ["parent"] = ids[0] }, default);
		Check(part.Parent == house && moved.Data["moved"]![0]!["path"]!.ToString() == "/Environment/Gark house/Gark test cube", "reparent many with paths in result");
		await ExpectFailure(() => tools.ExecuteAsync("reparent_instances", new() { ["ids"] = new JsonArray(ids[0]), ["parent"] = ids[1] }, default), "reparent cycle rejected");
		await tools.ExecuteAsync("undo", new(), default);
		Check(part.Parent == world.Environment, "reparent undo");
		var grouped = await tools.ExecuteAsync("group_instances", new() { ["ids"] = new JsonArray(ids[1], ids[2]), ["as"] = "Folder", ["name"] = "Pieces" }, default);
		var folder = world.GetObjectFromID(grouped.Data["group"]!["id"]!.ToString()) as Instance;
		Check(folder is Folder { Name: "Pieces" } && folder.GetChildren().Length == 2, "group into a named folder");
		await tools.ExecuteAsync("ungroup_instances", new() { ["ids"] = new JsonArray(folder!.ObjectID) }, default);
		Check(house.GetChildren().Length == 2 && house.GetChildren().All(c => c is Part), "ungroup keeps children");
		await tools.ExecuteAsync("delete_instances", new() { ["ids"] = new JsonArray(ids[0]) }, default);
		Check(house.Parent == world.TemporaryContainer, "delete a whole hierarchy");
	}
}
#endif
