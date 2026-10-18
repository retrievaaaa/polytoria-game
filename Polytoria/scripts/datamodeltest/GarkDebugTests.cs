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
			var before = await tools.ExecuteAsync("inspect_playtest", Args("""{"wait_seconds":1.5,"query":"GarkTestButton"}"""), default);
			var runtimeButton = before.Data["instances"]!.AsArray().Single();
			Check(runtimeButton!["text"]!.ToString() == "Before click" && runtimeButton["position_pixels"]![0]!.GetValue<float>() == 32, "unsaved world UI is present and positioned in the actual client");
			var moved = await tools.ExecuteAsync("playtest_input", Args("""{"actions":["rightward"],"duration_seconds":0.75}"""), default);
			Check(moved.Data["player"]!["position"]!.ToJsonString() != before.Data["player"]!["position"]!.ToJsonString(), "simulated movement changes the live player's position");
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
			var logs = await tools.ExecuteAsync("playtest_logs", Args("""{"limit":500}"""), default);
			string logText = logs.Data.ToJsonString();
			Check(logText.Contains("GARK_UNSAVED_CLIENT") && logText.Contains("GARK_EXPECTED_RUNTIME_ERROR"), "test exposes unsaved script output and server runtime errors");
			Check(!logText.Contains("__GARK_EVAL__"), "eval results stay out of playtest logs");
			Check(button.Text == "Before click" && File.ReadAllText(Path.Combine(_folder, clientPath)).Contains("DISK VERSION") && savedWorldHash == GarkProjectFiles.Hash(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_folder, "main.poly")))), "testing leaves authoring world and disk scripts unchanged");
			using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
			await ExpectFailure(() => tools.ExecuteAsync("playtest_input", Args("""{"keys":["W"],"duration_seconds":30}"""), stop.Token), "Stop cancels held test input");
			Check(!(await tools.ExecuteAsync("stop_playtest", new(), default)).Data["running"]!.GetValue<bool>(), "owned test processes stop after cancellation");
			Check((await tools.ExecuteAsync("playtest_logs", new(), default)).Data["logs"]!.AsArray().Count > 0, "test logs survive stopping");
		}
		finally { await tools.ExecuteAsync("stop_playtest", new(), default); }
	}
}
#endif
