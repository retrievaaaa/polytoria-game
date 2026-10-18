// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

#if DEBUG && CREATOR
using Godot;
using Polytoria.Creator.Gark;
using Polytoria.Creator.UI;
using Polytoria.Creator.UI.Popups;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Shared;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using File = System.IO.File;

namespace Polytoria.DatamodelTest;

public partial class GarkIntegrationTests
{
	private async Task DebugToolsChecks(GarkTools tools, World world)
	{
		var rotated = world.New<Part>(); rotated.Parent = world.Environment; rotated.Rotation = new(0, 90, 0);
		Check(rotated.Forward.DistanceTo(Vector3.Right) < 0.001f, "guidance +Z object forward and +90 yaw match runtime");
		rotated.Rotation = new(90, 0, 0);
		Check(rotated.Forward.DistanceTo(Vector3.Down) < 0.001f, "guidance positive X rotation matches runtime"); rotated.Delete();
		var cylinder = Globals.LoadShape("Cylinder").Item1;
		Check(cylinder is CylinderMesh { Height: 1 }, "guide cylinder Y axis verified against shipped mesh");
		Check(new Transform3D(Basis.Identity, new Vector3(0, 0, 10)).LookingAt(Vector3.Zero, Vector3.Up).Basis.X.DistanceTo(Vector3.Right) < 0.001f, "guide: a viewer in front of an object sees its Right (+X) on their right");
		Check((await tools.ExecuteAsync("get_creator_guide", Args("""{"topic":"building"}"""), default)).Data["guide"]!.ToString().Contains("bitmask"), "building guide covers collision layers");
		Check((await tools.ExecuteAsync("get_creator_guide", Args("""{"topic":"ui"}"""), default)).Data["guide"]!.ToString().Contains("PivotPoint=[0,0]"), "UI guide contains explicit full-screen and HUD positioning");
		Check((await tools.ExecuteAsync("get_creator_guide", Args("""{"topic":"workflow"}"""), default)).Data["guide"]!.ToString().Contains("capture_viewport"), "workflow guide explains verification");

		const string diagnosticPath = "scripts/gark-diagnostics.module.luau";
		await tools.ExecuteAsync("create_script", new() { ["path"] = diagnosticPath, ["source"] = "return {}\n" }, default);
		Tabs.Singleton.Insert(new Tabs.TextEditorTab { Session = CreatorService.CurrentSession!, TargetPath = diagnosticPath, Title = "Gark diagnostics" });
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var editor = Tabs.Singleton.FindTextEditor(Path.Combine(_folder, diagnosticPath))!.EditorRoot.CodeEditor;
		editor.Text = "--!strict\n-- café 🌙\nlocal numberValue: number = \"wrong\"\nreturn definitelyMissingSymbol\n";
		var diagnosis = await tools.ExecuteAsync("script_diagnostics", new() { ["path"] = diagnosticPath }, default);
		var file = diagnosis.Data["files"]![0]!;
		Check(file["diagnostics"]!.AsArray().Any(d => d?["severity"]?.GetValue<int>() == 1) && file["sha256"]!.ToString() == GarkProjectFiles.Hash(editor.Text), "real language server diagnoses unsaved strict script");
		Check(file["diagnostics"]!.ToJsonString().Contains("definitelyMissingSymbol") && file["diagnostics"]!.ToJsonString().Contains("number"), "diagnostics include type mismatches and unresolved references");
		Check(File.ReadAllText(Path.Combine(_folder, diagnosticPath)) == "return {}\n", "diagnostics do not save unsaved buffers");
		editor.Text = "--!strict\nlocal numberValue: number = 2\nreturn numberValue\n";
		var fixedResult = await tools.ExecuteAsync("script_diagnostics", new() { ["path"] = diagnosticPath }, default);
		Check(!fixedResult.Data["files"]![0]!["diagnostics"]!.AsArray().Any(d => d?["severity"]?.GetValue<int>() == 1), "diagnostics refresh after an unsaved correction");
		editor.Text = "local broken =\n";
		Check((await tools.ExecuteAsync("script_diagnostics", new() { ["path"] = diagnosticPath }, default)).Data["files"]![0]!["diagnostics"]!.AsArray().Any(d => d?["severity"]?.GetValue<int>() == 1), "language server reports syntax errors in unsaved code");
		editor.Text = "return {}\n";
		var all = await tools.ExecuteAsync("script_diagnostics", new(), default);
		Check(all.Data["files"]!.AsArray().Count >= 2, "diagnostics cover every project script");
		if (!Globals.ReadCmdArgs().ContainsKey("garkplaytest")) return;

		var gui = world.New<GUI>(); gui.Name = "GarkTestGUI"; gui.Parent = world.PlayerGUI;
		var button = world.New<UIButton>(); button.Name = "GarkTestButton"; button.Parent = gui;
		button.PositionRelative = Vector2.Zero; button.PositionOffset = new(32, 80); button.PivotPoint = Vector2.Zero;
		button.SizeRelative = Vector2.Zero; button.SizeOffset = new(200, 64); button.Text = "Before click";
		button.Color = new Color(0.1f, 0.4f, 0.8f);
		const string clientPath = "scripts/gark-playtest.client.luau";
		await tools.ExecuteAsync("create_script", new() { ["path"] = clientPath, ["source"] = "print(\"DISK VERSION\")\n" }, default);
		Tabs.Singleton.Insert(new Tabs.TextEditorTab { Session = CreatorService.CurrentSession!, TargetPath = clientPath, Title = "Gark playtest" });
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var clientEditor = Tabs.Singleton.FindTextEditor(Path.Combine(_folder, clientPath))!.EditorRoot.CodeEditor;
		clientEditor.Text = "local button = PlayerGUI:FindChild(\"GarkTestGUI\"):FindChild(\"GarkTestButton\")\nbutton.Clicked:Connect(function() button.Text = \"Clicked ✓\" print(\"GARK_UI_CLICK ✓\") end)\nprint(\"GARK_UNSAVED_CLIENT ✓\")\n";
		await tools.ExecuteAsync("create_script", new() { ["path"] = "scripts/gark-playtest.server.luau", ["source"] = "print(\"GARK_SERVER_STARTED\")\nerror(\"GARK_EXPECTED_RUNTIME_ERROR\")\n" }, default);
		string savedWorldHash = GarkProjectFiles.Hash(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_folder, "main.poly"))));
		try
		{
			var started = await tools.ExecuteAsync("start_playtest", Args("""{"width":800,"height":600}"""), default);
			Check(started.Data["status"]!["running"]!.GetValue<bool>(), "owned server and real client start with a player");
			Check(started.Data["clients"]![0]!["player"]!["collision"] != null && !started.Data.ToJsonString().Contains("\"instances\""), "start_playtest returns player state and size, not a hierarchy dump");
			var spawnedPlayer = started.Data["clients"]![0]!["player"]!;
			GD.Print("GARK MEASURE player " + spawnedPlayer.ToJsonString());
			Check(spawnedPlayer["collision"]!["size"]!.ToJsonString() == "[2,5.8,1]" && Math.Abs(spawnedPlayer["position"]![1]!.GetValue<float>() - spawnedPlayer["collision"]!["min"]![1]!.GetValue<float>() - 3) < 0.05,
				"guide: player collider is 2 x 5.8 x 1 with feet 3 studs below Position");
			var before = await tools.ExecuteAsync("inspect_playtest", Args("""{"wait_seconds":1.5,"query":"GarkTestButton"}"""), default);
			var runtimeButton = before.Data["instances"]!.AsArray().Single();
			Check(runtimeButton!["text"]!.ToString() == "Before click" && runtimeButton["position_pixels"]![0]!.GetValue<float>() == 32, "unsaved world UI is present and positioned in the actual client");
			var moved = await tools.ExecuteAsync("playtest_input", Args("""{"actions":["rightward"],"duration_seconds":0.75}"""), default);
			Check(moved.Data["player"]!["position"]!.ToJsonString() != before.Data["player"]!["position"]!.ToJsonString(), "simulated movement changes the live player's position");
			Check(moved.Data["instances"] == null, "playtest_input returns state without a hierarchy dump");
			var shallow = await tools.ExecuteAsync("inspect_playtest", Args("""{"depth":1}"""), default);
			Check(shallow.Data["instances"]!.AsArray().All(i => i!["path"]!.ToString().Count(c => c == '/') == 1), "inspect_playtest depth limits the hierarchy");
			await tools.ExecuteAsync("playtest_input", new() { ["mouse_position"] = runtimeButton["center_normalized"]!.DeepClone(), ["mouse_button"] = "left" }, default);
			var clicked = await tools.ExecuteAsync("inspect_playtest", Args("""{"query":"GarkTestButton","wait_seconds":0.2}"""), default);
			Check(clicked.Data["instances"]![0]!["text"]!.ToString() == "Clicked ✓", "simulated client click triggers the real Luau UI handler");
			var turned = await tools.ExecuteAsync("playtest_input", Args("""{"mouse_position":[0.5,0.5],"mouse_button":"right","mouse_delta":[100,-20],"duration_seconds":0.2}"""), default);
			Check(turned.Data["camera"]!["rotation"]!.ToJsonString() != clicked.Data["camera"]!["rotation"]!.ToJsonString(), "simulated mouse drag turns the actual test camera");
			var sum = await tools.ExecuteAsync("playtest_eval", Args("""{"code":"return 1 + 2, 'café ✓', {a = true}"}"""), default);
			Check(sum.Data["ok"]!.GetValue<bool>() && sum.Data["target"]!.ToString() == "server" && sum.Data["returned"]![0]!.GetValue<double>() == 3
				&& sum.Data["returned"]![1]!.ToString() == "café ✓" && sum.Data["returned"]![2]!["a"]!.GetValue<bool>(), "playtest_eval returns JSON values from the server");
			var expression = await tools.ExecuteAsync("playtest_eval", Args("""{"code":"PlayerGUI:FindChild('GarkTestGUI'):FindChild('GarkTestButton').Text","target":"client"}"""), default);
			Check(expression.Data["returned"]!.ToString() == "Clicked ✓", "playtest_eval reads live client state from a bare expression");
			var printed = await tools.ExecuteAsync("playtest_eval", Args("""{"code":"print('GARK_EVAL_PRINT') error('GARK_EVAL_BOOM')"}"""), default);
			Check(!printed.Data["ok"]!.GetValue<bool>() && printed.Data["error"]!.ToString().Contains("GARK_EVAL_BOOM")
				&& printed.Data["output"]!.ToJsonString().Contains("GARK_EVAL_PRINT"), "playtest_eval reports output and errors");
			var waiting = await tools.ExecuteAsync("playtest_eval", Args("""{"code":"wait(5) return 1","timeout_seconds":0.5}"""), default);
			Check(!waiting.Data["completed"]!.GetValue<bool>(), "playtest_eval stops waiting at its timeout");
			var capture = await tools.ExecuteAsync("capture_playtest", new(), default);
			Check(capture.Image is { Length: > 1000 }, "actual gameplay and screen UI captured as PNG");
			File.WriteAllBytes(ProjectSettings.GlobalizePath("res://target/gark-qa/playtest.png"), capture.Image!);
			var logs = await tools.ExecuteAsync("playtest_logs", Args("""{"kind":"script","limit":500}"""), default);
			string logText = logs.Data.ToJsonString();
			Check(logText.Contains("GARK_UNSAVED_CLIENT") && logText.Contains("GARK_EXPECTED_RUNTIME_ERROR"), "test exposes unsaved script output and server runtime errors");
			var errors = await tools.ExecuteAsync("playtest_logs", Args("""{"kind":"script","severity":"error"}"""), default);
			Check(errors.Data["logs"]!.AsArray().Count > 0 && errors.Data["logs"]!.AsArray().All(l => l!["kind"]!.ToString() == "script" && l["severity"]!.ToString() == "error")
				&& errors.Data["logs"]!.ToJsonString().Contains("GARK_EXPECTED_RUNTIME_ERROR") && errors.Data["counts"]!["error"] != null, "playtest_logs filters script errors and counts severities");
			Check((await tools.ExecuteAsync("playtest_logs", Args("""{"contains":"__GARK_EVAL__"}"""), default)).Data["logs"]!.AsArray().Count == 0, "eval results stay out of playtest logs");
			var everything = await tools.ExecuteAsync("playtest_logs", Args("""{"limit":5000}"""), default);
			Check(everything.Data.ToJsonString().Length < 60_000, "playtest_logs pages are size-capped");
			var aimed = await tools.ExecuteAsync("inspect_playtest", new(), default);
			var shot = await tools.ExecuteAsync("capture_playtest", Args("""{"position":[0,40,40],"look_at":[0,0,0],"fov":60}"""), default);
			var afterShot = await tools.ExecuteAsync("inspect_playtest", new(), default);
			Check(shot.Image is { Length: > 1000 } && shot.Data["camera"]!["temporary"]!.GetValue<bool>() && Math.Abs(shot.Data["camera"]!["forward"]![1]!.GetValue<float>() + 0.707f) < 0.01f, "capture_playtest aims a temporary camera at a point");
			Check(afterShot.Data["camera"]!["fov"]!.GetValue<float>() == aimed.Data["camera"]!["fov"]!.GetValue<float>(), "the game camera is untouched by an aimed capture");
			File.WriteAllBytes(ProjectSettings.GlobalizePath("res://target/gark-qa/playtest-aimed.png"), shot.Image!);
			Check(button.Text == "Before click" && File.ReadAllText(Path.Combine(_folder, clientPath)).Contains("DISK VERSION") && savedWorldHash == GarkProjectFiles.Hash(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_folder, "main.poly")))), "testing leaves authoring world and disk scripts unchanged");
			using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
			await ExpectFailure(() => tools.ExecuteAsync("playtest_input", Args("""{"keys":["W"],"duration_seconds":30}"""), stop.Token), "Stop cancels held test input");
			Check(!(await tools.ExecuteAsync("stop_playtest", new(), default)).Data["running"]!.GetValue<bool>(), "owned test processes stop after cancellation");
			Check((await tools.ExecuteAsync("playtest_logs", new(), default)).Data["logs"]!.AsArray().Count > 0, "test logs survive stopping");

			var pair = await tools.ExecuteAsync("start_playtest", Args("""{"players":2,"clock":"2030-06-15T12:00:00Z","width":480,"height":320}"""), default);
			Check(pair.Data["clients"]!.AsArray().Count == 2, "start_playtest spawns several players");
			string first = (await tools.ExecuteAsync("playtest_eval", Args("""{"code":"Players.LocalPlayer.Name","target":"client"}"""), default)).Data["returned"]!.ToString();
			string second = (await tools.ExecuteAsync("playtest_eval", Args("""{"code":"Players.LocalPlayer.Name","target":"client","client":2}"""), default)).Data["returned"]!.ToString();
			Check(first != second && second != "", $"each test client is its own player ({first}, {second})");
			var seen = await tools.ExecuteAsync("inspect_playtest", Args("""{"client":2,"depth":1,"wait_seconds":0.5}"""), default);
			Check(seen.Data["other_players"]!.AsArray().Count == 1, "clients see the other test players");
			await ExpectFailure(() => tools.ExecuteAsync("inspect_playtest", Args("""{"client":3}"""), default), "unknown client numbers are rejected");
			var clock = await tools.ExecuteAsync("playtest_eval", Args("""{"code":"return os.time(), os.date('!%Y-%m-%d'), tick()"}"""), default);
			var clockValues = clock.Data["returned"]!.AsArray();
			Check(Math.Abs(clockValues[0]!.GetValue<double>() - 1907755200) < 600 && clockValues[1]!.ToString() == "2030-06-15" && Math.Abs(clockValues[2]!.GetValue<double>() - 1907755200) < 600,
				"clock shifts server os.time, os.date and tick: " + clockValues.ToJsonString());
			Check((await tools.ExecuteAsync("playtest_eval", Args("""{"code":"os.date('!%Y', os.time())","target":"client","client":2}"""), default)).Data["returned"]!.ToString() == "2030", "clock shifts every test client");
			Check((await tools.ExecuteAsync("playtest_eval", Args("""{"code":"os.date('!%Y', 0)"}"""), default)).Data["returned"]!.ToString() == "1970", "explicit times are not shifted");
			Check((await tools.ExecuteAsync("playtest_logs", Args("""{"source":"client2"}"""), default)).Data["logs"]!.AsArray().Count > 0, "logs can be filtered to one client");
		}
		finally { await tools.ExecuteAsync("stop_playtest", new(), default); }
	}
}
#endif
