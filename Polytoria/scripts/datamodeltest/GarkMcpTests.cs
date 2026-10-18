// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

#if DEBUG && CREATOR
using Godot;
using Polytoria.Creator.Gark;
using Polytoria.Creator.UI.Popups;
using Polytoria.Datamodel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;

namespace Polytoria.DatamodelTest;

/// <summary>MCP protocol, HTTP transport and stdio bridge checks against the live test project.</summary>
public partial class GarkIntegrationTests
{
	/// <summary>Runs tools through the real Creator host, with switches the tests control.</summary>
	private sealed class TestHost(IGarkMcpHost? inner) : IGarkMcpHost
	{
		public bool Online { get; set; } = inner != null;
		public bool AllowEdits { get; set; } = true;
		internal volatile bool SawCancellation;
		public async Task<GarkToolResult> CallToolAsync(GarkMcpSession session, GarkToolDefinition tool, JsonObject args, Action<string> progress, bool record, CancellationToken token)
		{
			// "_test_delay_ms" simulates a long tool so transport behaviour can be observed.
			if (args["_test_delay_ms"] is { } delay)
			{
				args.Remove("_test_delay_ms");
				progress("Simulated work started");
				try { await Task.Delay(delay.GetValue<int>(), token); }
				catch (OperationCanceledException) { SawCancellation = true; throw; }
			}
			return await inner!.CallToolAsync(session, tool, args, progress, record, token);
		}
	}

	private static JsonObject Rpc(int id, string method, JsonObject? parameters = null) => GarkMcpProtocol.Request(JsonValue.Create(id), method, parameters);
	private static JsonObject Call(int id, string tool, JsonObject? args = null, JsonNode? progressToken = null)
	{
		var parameters = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() };
		if (progressToken != null) parameters["_meta"] = new JsonObject { ["progressToken"] = progressToken };
		return Rpc(id, "tools/call", parameters);
	}
	private static string ToolText(JsonNode response) => response["result"]!["content"]![0]!["text"]!.ToString();
	private static bool ToolFailed(JsonNode response) => response["result"]!["isError"]!.GetValue<bool>();

	private async Task OfflineProtocolChecks()
	{
		var host = new TestHost(null);
		var protocol = new GarkMcpProtocol(host);
		var session = new GarkMcpSession("unit", "test");
		var init = (await protocol.HandleAsync(session, Rpc(1, "initialize", Args("""{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"unit","title":"Unit client","version":"9"}}""")), _ => { }, default))!;
		Check(init["result"]!["protocolVersion"]!.ToString() == "2025-06-18" && session.Label == "Unit client 9", "initialize negotiates a supported version and records the client");
		Check(init["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>() && init["result"]!["instructions"]!.ToString().Contains("get_context"), "initialize advertises tools and sends workflow instructions");
		var future = (await protocol.HandleAsync(session, Rpc(2, "initialize", Args("""{"protocolVersion":"2099-01-01"}""")), _ => { }, default))!;
		Check(future["result"]!["protocolVersion"]!.ToString() == GarkMcpProtocol.LatestVersion, "unknown protocol versions fall back to the latest");
		var tools = (await protocol.HandleAsync(session, Rpc(3, "tools/list"), _ => { }, default))!["result"]!["tools"]!.AsArray();
		Check(tools.Count == GarkTools.Definitions.Count, "tools/list exposes every tool");
		JsonNode Tool(string name) => tools.Single(t => t!["name"]!.ToString() == name)!;
		Check(Tool("get_context")["annotations"]!["readOnlyHint"]!.GetValue<bool>() && Tool("delete_instances")["annotations"]!["destructiveHint"]!.GetValue<bool>()
			&& Tool("search_assets")["annotations"]!["openWorldHint"]!.GetValue<bool>() && !Tool("create_instances")["annotations"]!["readOnlyHint"]!.GetValue<bool>(), "tool annotations describe effects");
		host.AllowEdits = false;
		var readOnly = (await protocol.HandleAsync(session, Rpc(4, "tools/list"), _ => { }, default))!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.ToString()).ToArray();
		Check(!readOnly.Contains("create_instances") && !readOnly.Contains("patch_script") && readOnly.Contains("capture_viewport") && readOnly.Contains("start_playtest"), "read-only mode hides editing tools only");
		host.AllowEdits = true;
		var offline = (await protocol.HandleAsync(session, Call(5, "get_context"), _ => { }, default))!;
		Check(ToolFailed(offline) && ToolText(offline).Contains("not running"), "tools explain when Creator is unavailable");
		var missing = (await protocol.HandleAsync(session, Rpc(6, "no/such/method"), _ => { }, default))!;
		Check(missing["error"]!["code"]!.GetValue<int>() == -32601, "unknown methods return JSON-RPC errors");
		var unknownTool = (await protocol.HandleAsync(session, Call(7, "make_game"), _ => { }, default))!;
		Check(unknownTool["error"]!["code"]!.GetValue<int>() == -32602, "unknown tools are invalid params");
		var prompt = (await protocol.HandleAsync(session, Rpc(8, "prompts/get", Args("""{"name":"gark","arguments":{"task":"a racing track"}}""")), _ => { }, default))!;
		Check(prompt["result"]!["messages"]![0]!["content"]!["text"]!.ToString().Contains("a racing track"), "prompts include their arguments");
		var guide = (await protocol.HandleAsync(session, Rpc(9, "resources/read", Args("""{"uri":"polytoria://guide/coordinates"}""")), _ => { }, default))!;
		Check(guide["result"]!["contents"]![0]!["text"]!.ToString().Contains("Y-up"), "guide resources are readable offline");
		var api = (await protocol.HandleAsync(session, Rpc(10, "resources/read", Args("""{"uri":"polytoria://api/polytoria.d.luau"}""")), _ => { }, default))!;
		Check(api["result"]!["contents"]![0]!["text"]!.ToString().Length > 10000, "the full Luau API is a resource");
		Check(await protocol.HandleAsync(session, GarkMcpProtocol.Notification("notifications/initialized"), _ => { }, default) == null, "notifications get no reply");
	}

	private async Task McpChecks(World world)
	{
		var creator = GarkMcpHost.Instance;
		Check(creator != null, "Creator hosts the MCP server node");
		var host = new TestHost(creator);
		using var server = new GarkMcpHttpServer(new GarkMcpProtocol(host), 0);
		string url = server.Url;
		using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };

		async Task<(HttpResponseMessage Response, string Body)> Send(HttpMethod method, string? json, string? session = null, Action<HttpRequestMessage>? configure = null, string accept = "application/json, text/event-stream", CancellationToken token = default)
		{
			using var request = new HttpRequestMessage(method, url);
			if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
			request.Headers.TryAddWithoutValidation("Accept", accept);
			if (session != null) request.Headers.Add("Mcp-Session-Id", session);
			configure?.Invoke(request);
			var response = await http.SendAsync(request, token);
			return (response, await response.Content.ReadAsStringAsync(token));
		}
		static List<JsonObject> Events(string body) => body.Split("\n\n").Select(e => e.Split('\n').FirstOrDefault(l => l.StartsWith("data:"))).Where(l => l != null)
			.Select(l => JsonNode.Parse(l![5..].Trim())!.AsObject()).ToList();

		var health = await http.GetStringAsync(url.Replace("/mcp", "/health"));
		Check(JsonNode.Parse(health)!["endpoint"]!.ToString() == "/mcp", "health endpoint describes the server");
		var (initResponse, initBody) = await Send(HttpMethod.Post, Rpc(1, "initialize", Args("""{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"gark-test","title":"Gark test client","version":"1.0"}}""")).ToJsonString());
		string session = initResponse.Headers.GetValues("Mcp-Session-Id").Single();
		Check(initResponse.StatusCode == HttpStatusCode.OK && JsonNode.Parse(initBody)!["result"]!["serverInfo"]!["name"]!.ToString() == "polytoria-creator", "HTTP initialize returns a session");
		var (initialized, _) = await Send(HttpMethod.Post, GarkMcpProtocol.Notification("notifications/initialized").ToJsonString(), session);
		Check(initialized.StatusCode == HttpStatusCode.Accepted, "notifications are accepted with 202");
		var (_, listBody) = await Send(HttpMethod.Post, Rpc(2, "tools/list").ToJsonString(), session);
		Check(JsonNode.Parse(listBody)!["result"]!["tools"]!.AsArray().Count == GarkTools.Definitions.Count, "tools/list over HTTP");

		var (_, createBody) = await Send(HttpMethod.Post, Call(3, "create_instances", Args("""{"objects":[{"class_name":"Part","name":"MCP part","properties":{"Position":[0,40,0]}}]}""")).ToJsonString(), session);
		var create = JsonNode.Parse(createBody)!;
		string partId = JsonNode.Parse(ToolText(create))!["created"]![0]!["id"]!.ToString();
		Check(!ToolFailed(create) && world.GetObjectFromID(partId) is Part { Name: "MCP part" }, "tools/call edits the live world");
		Check(creator!.Activity.Entries.Any(e => e.Tool == "create_instances" && e.Client == "Gark test client 1.0" && e.Done && !e.Failed), "tool calls appear in Creator's activity feed");
		var stringArgs = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "inspect_instance", ["arguments"] = $$"""{"id":"{{partId}}"}""" } };
		var (_, stringArgsBody) = await Send(HttpMethod.Post, stringArgs.ToJsonString(), session);
		Check(ToolText(JsonNode.Parse(stringArgsBody)!).Contains("MCP part"), "arguments sent as a JSON string are accepted");
		var (_, badBody) = await Send(HttpMethod.Post, Call(5, "set_properties", Args($$$"""{"id":"{{{partId}}}","properties":{"Positon":[1,2,3]}}""")).ToJsonString(), session);
		var bad = JsonNode.Parse(badBody)!;
		Check(ToolFailed(bad) && ToolText(bad).Contains("Positon") && ToolText(bad).Contains("class_schema"), "tool errors come back as isError results the agent can act on");
		var (_, batchBody) = await Send(HttpMethod.Post, new JsonArray(Rpc(6, "ping"), Rpc(7, "prompts/list")).ToJsonString(), session);
		var batch = JsonNode.Parse(batchBody)!.AsArray();
		Check(batch.Count == 2 && batch.Any(r => r!["id"]!.GetValue<int>() == 7 && r["result"]!["prompts"]!.AsArray().Count >= 3), "JSON-RPC batches");
		var (_, resourcesBody) = await Send(HttpMethod.Post, Rpc(8, "resources/list").ToJsonString(), session);
		Check(JsonNode.Parse(resourcesBody)!["result"]!["resources"]!.AsArray().Any(r => r!["uri"]!.ToString() == "polytoria://scripts/scripts/modules/gark.module.luau"), "project scripts are listed as resources");
		var (_, contextBody) = await Send(HttpMethod.Post, Rpc(9, "resources/read", Args("""{"uri":"polytoria://context"}""")).ToJsonString(), session);
		Check(JsonNode.Parse(JsonNode.Parse(contextBody)!["result"]!["contents"]![0]!["text"]!.ToString())!["ready"]!.GetValue<bool>(), "live context resource");
		var (_, scriptBody) = await Send(HttpMethod.Post, Rpc(10, "resources/read", Args("""{"uri":"polytoria://scripts/scripts/modules/gark.module.luau"}""")).ToJsonString(), session);
		Check(JsonNode.Parse(scriptBody)!["result"]!["contents"]![0]!["text"]!.ToString().StartsWith("return {"), "script resources return the raw source");
		if (DisplayServer.GetName() != "headless")
		{
			var (_, captureBody) = await Send(HttpMethod.Post, Call(11, "capture_viewport", new JsonObject { ["focus_ids"] = new JsonArray(partId), ["width"] = 320, ["height"] = 180 }).ToJsonString(), session);
			var image = JsonNode.Parse(captureBody)!["result"]!["content"]!.AsArray().Single(c => c!["type"]!.ToString() == "image")!;
			byte[] png = Convert.FromBase64String(image["data"]!.ToString());
			Check(image["mimeType"]!.ToString() == "image/png" && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', "captures arrive as MCP image content");
		}

		// Browser protections
		var (crossSite, _) = await Send(HttpMethod.Post, Rpc(12, "ping").ToJsonString(), session, r => r.Headers.Add("Origin", "https://evil.example"));
		Check(crossSite.StatusCode == HttpStatusCode.Forbidden, "cross-site browser requests are refused");
		var (localOrigin, _) = await Send(HttpMethod.Post, Rpc(13, "ping").ToJsonString(), session, r => r.Headers.Add("Origin", "http://localhost:6274"));
		Check(localOrigin.StatusCode == HttpStatusCode.OK, "local tools such as the MCP Inspector are allowed");
		var (rebinding, _) = await Send(HttpMethod.Post, Rpc(14, "ping").ToJsonString(), session, r => r.Headers.Host = "attacker.example");
		Check(rebinding.StatusCode == HttpStatusCode.Forbidden, "DNS-rebinding Host headers are refused");
		using (var form = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(Rpc(15, "ping").ToJsonString(), Encoding.UTF8, "text/plain") })
			Check((await http.SendAsync(form)).StatusCode == HttpStatusCode.UnsupportedMediaType, "non-JSON bodies (simple form posts) are refused");
		var (noAccept, _) = await Send(HttpMethod.Get, null, session, accept: "application/json");
		Check(noAccept.StatusCode == HttpStatusCode.NotAcceptable, "GET stream requires text/event-stream");

		// Sessions survive a Creator restart: unknown IDs are adopted.
		var (adopted, adoptedBody) = await Send(HttpMethod.Post, Call(16, "get_context").ToJsonString(), "session-from-before-restart");
		Check(adopted.StatusCode == HttpStatusCode.OK && !ToolFailed(JsonNode.Parse(adoptedBody)!), "unknown session IDs keep working after a restart");

		// Long calls switch to an event stream with progress.
		var watch = Stopwatch.StartNew();
		var (slow, slowBody) = await Send(HttpMethod.Post, Call(17, "get_context", Args("""{"_test_delay_ms":4500}"""), "progress-1").ToJsonString(), session);
		var events = Events(slowBody);
		Check(slow.Content.Headers.ContentType?.MediaType == "text/event-stream" && watch.Elapsed.TotalSeconds >= 4, "long calls stream their response");
		Check(events.Any(e => e["method"]?.ToString() == "notifications/progress" && e["params"]!["progressToken"]!.ToString() == "progress-1")
			&& events.Last()["id"]!.GetValue<int>() == 17 && !ToolFailed(events.Last()), "progress notifications precede the final result");
		var (jsonOnly, jsonOnlyBody) = await Send(HttpMethod.Post, Call(18, "get_context", Args("""{"_test_delay_ms":1800}""")).ToJsonString(), session, accept: "application/json");
		Check(jsonOnly.Content.Headers.ContentType?.MediaType == "application/json" && !ToolFailed(JsonNode.Parse(jsonOnlyBody)!), "JSON-only clients still get a JSON reply for long calls");

		// Cancellation by notification and by disconnect.
		var pending = Send(HttpMethod.Post, Call(19, "get_context", Args("""{"_test_delay_ms":30000}""")).ToJsonString(), session, accept: "application/json");
		await Task.Delay(500);
		await Send(HttpMethod.Post, GarkMcpProtocol.Notification("notifications/cancelled", new JsonObject { ["requestId"] = 19, ["reason"] = "test" }).ToJsonString(), session);
		var cancelled = JsonNode.Parse((await pending.WaitAsync(TimeSpan.FromSeconds(10))).Body)!;
		Check(ToolFailed(cancelled) && ToolText(cancelled).Contains("Cancelled"), "notifications/cancelled stops a running tool");
		host.SawCancellation = false;
		using (var abort = new CancellationTokenSource(700))
		{
			try { await Send(HttpMethod.Post, Call(20, "get_context", Args("""{"_test_delay_ms":30000}""")).ToJsonString(), session, token: abort.Token); }
			catch (OperationCanceledException) { }
		}
		for (int i = 0; i < 300 && !host.SawCancellation; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Check(host.SawCancellation, "a client disconnect cancels its running tool");

		// Read-only mode and list_changed on the GET stream.
		using (var stream = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
			request.Headers.Add("Mcp-Session-Id", session);
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stream.Token);
			using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stream.Token));
			for (int i = 0; i < 50 && !server.Sessions.Single(s => s.Id == session).Streams.Any(); i++) await Task.Delay(20);
			host.AllowEdits = false;
			server.NotifyToolsChanged();
			string? line;
			bool changed = false;
			while (!changed && (line = await reader.ReadLineAsync(stream.Token)) != null) changed = line.Contains("notifications/tools/list_changed");
			Check(changed, "tool list changes are pushed on the event stream");
		}
		var (_, readOnlyCall) = await Send(HttpMethod.Post, Call(21, "create_instances", Args("""{"objects":[{"class_name":"Part"}]}""")).ToJsonString(), session);
		Check(ToolFailed(JsonNode.Parse(readOnlyCall)!) && ToolText(JsonNode.Parse(readOnlyCall)!).Contains("Allow edits"), "edits are refused while agents are read-only");
		host.AllowEdits = true;

		// Older HTTP+SSE transport.
		using (var legacy = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url.Replace("/mcp", "/sse"));
			request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, legacy.Token);
			using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(legacy.Token));
			string? endpoint = null, line;
			while (endpoint == null && (line = await reader.ReadLineAsync(legacy.Token)) != null)
				if (line.StartsWith("data: /messages")) endpoint = line[6..];
			var post = await http.PostAsync(url.Replace("/mcp", endpoint!), new StringContent(Call(22, "list_instances", Args("""{"query":"MCP part"}""")).ToJsonString(), Encoding.UTF8, "application/json"), legacy.Token);
			Check(post.StatusCode == HttpStatusCode.Accepted && endpoint!.Contains("sessionId="), "legacy SSE announces its message endpoint");
			JsonNode? reply = null;
			while (reply == null && (line = await reader.ReadLineAsync(legacy.Token)) != null)
				if (line.StartsWith("data: {") && JsonNode.Parse(line[6..]) is { } message && message["id"]?.GetValue<int>() == 22) reply = message;
			Check(reply != null && ToolText(reply).Contains(partId), "legacy SSE delivers tool results on the stream");
		}

		var (ended, _) = await Send(HttpMethod.Delete, null, session);
		Check(ended.StatusCode == HttpStatusCode.OK && server.Sessions.All(s => s.Id != session), "DELETE ends the session");
		await BridgeChecks(url, partId);
		await PageChecks();
		world.GetObjectFromID(partId)!.Delete();
	}

	/// <summary>Launches this executable as the stdio bridge and talks MCP to it like Claude Desktop would.</summary>
	private async Task BridgeChecks(string url, string partId)
	{
		var start = new ProcessStartInfo(OS.GetExecutablePath())
		{
			RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
			StandardOutputEncoding = new UTF8Encoding(false)
		};
		if (OS.HasFeature("editor")) { start.ArgumentList.Add("--path"); start.ArgumentList.Add(ProjectSettings.GlobalizePath("res://").TrimEnd('/')); }
		foreach (string arg in new[] { "--headless", "--quiet", "--gark-mcp", "--gark-mcp-url", url }) start.ArgumentList.Add(arg);
		using var bridge = Process.Start(start)!;
		var stderr = bridge.StandardError.ReadToEndAsync();
		bridge.StandardInput.AutoFlush = true;
		bridge.StandardInput.NewLine = "\n";
		var lines = new List<string>();
		async Task<JsonNode> Reply(int id)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			while (true)
			{
				string line = await bridge.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception("Bridge closed its output. stderr: " + await stderr);
				lines.Add(line);
				var message = JsonNode.Parse(line)!; // Throws if anything but JSON reaches stdout.
				if (message["id"]?.GetValue<int>() == id) return message;
			}
		}
		try
		{
			await bridge.StandardInput.WriteLineAsync(Rpc(1, "initialize", Args("""{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdio-test","version":"1"}}""")).ToJsonString());
			var init = await Reply(1);
			Check(init["result"]!["protocolVersion"]!.ToString() == "2025-06-18", "stdio bridge initializes");
			await bridge.StandardInput.WriteLineAsync(GarkMcpProtocol.Notification("notifications/initialized").ToJsonString());
			await bridge.StandardInput.WriteLineAsync(Rpc(2, "tools/list").ToJsonString());
			Check((await Reply(2))["result"]!["tools"]!.AsArray().Count == GarkTools.Definitions.Count, "stdio bridge lists tools");
			await bridge.StandardInput.WriteLineAsync(Call(3, "inspect_instance", new JsonObject { ["id"] = partId }).ToJsonString());
			Check(ToolText(await Reply(3)).Contains("MCP part"), "stdio bridge forwards tool calls to Creator");
			Check(lines.All(l => l.TrimStart().StartsWith('{')), "stdio bridge keeps stdout clean JSON");
		}
		finally
		{
			bridge.StandardInput.Close();
			if (!bridge.WaitForExit(15000)) bridge.Kill(true);
		}
		Check(bridge.ExitCode == 0, "stdio bridge exits cleanly when its client closes stdin");
	}

	/// <summary>The Gark toolbar page renders, lists agents and writes project configuration safely.</summary>
	private async Task PageChecks()
	{
		var page = new GarkPopup();
		GetTree().Root.AddChild(page);
		page.PopupCentered();
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var picker = page.FindChildren("*", "", true, false).OfType<OptionButton>().Single();
		Check(picker.ItemCount >= 7 && Enumerable.Range(0, picker.ItemCount).Any(i => picker.GetItemText(i) == "Codex CLI / Codex app"), "page offers setup for many agents");
		var snippet = page.FindChildren("*", "", true, false).OfType<TextEdit>().Single();
		Check(snippet.Text.StartsWith("claude mcp add --transport http polytoria http://127.0.0.1:"), "Claude Code command uses the server URL");
		string mcpJson = Path.Combine(_folder, ".mcp.json");
		File.WriteAllText(mcpJson, """{"mcpServers":{"other":{"command":"x"}}}""");
		page.FindChildren("*", "", true, false).OfType<Button>().Single(b => b.Text.StartsWith("Add to this project")).EmitSignal(BaseButton.SignalName.Pressed);
		var written = JsonNode.Parse(File.ReadAllText(mcpJson))!;
		Check(written["mcpServers"]!["other"] != null && written["mcpServers"]!["polytoria"]!["type"]!.ToString() == "http", "adding to a project keeps existing servers");
		int cursor = Enumerable.Range(0, picker.ItemCount).Single(i => picker.GetItemText(i) == "Cursor");
		picker.Select(cursor);
		picker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)cursor);
		Check(JsonNode.Parse(snippet.Text)!["mcpServers"]!["polytoria"]!["url"]!.ToString().EndsWith("/mcp"), "Cursor configuration is valid JSON");
		int desktop = Enumerable.Range(0, picker.ItemCount).Single(i => picker.GetItemText(i) == "Claude Desktop");
		picker.Select(desktop);
		picker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)desktop);
		var command = JsonNode.Parse(snippet.Text)!["mcpServers"]!["polytoria"]!;
		Check(command["args"]!.AsArray().Any(a => a!.ToString() == "--gark-mcp") && File.Exists(command["command"]!.ToString()), "Claude Desktop gets a runnable stdio command");
		async Task Screenshot(string name)
		{
			if (DisplayServer.GetName() == "headless") return;
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var image = page.GetTexture().GetImage();
			File.WriteAllBytes(ProjectSettings.GlobalizePath($"res://target/gark-qa/{name}.png"), image.SavePngToBuffer());
		}
		await Screenshot("gark-page");
		page.FindChildren("*", "", true, false).OfType<TabContainer>().Single().CurrentTab = 1;
		var cards = page.FindChildren("*", "", true, false).OfType<Button>().Where(b => b.ToggleMode && b.Text.Contains("Gark test client")).ToArray();
		Check(cards.Any(c => c.Text.Contains("Create instances") && c.Text.StartsWith('✓')), "activity tab lists agent tool calls with their outcome");
		cards.First(c => c.Text.Contains("Create instances")).ButtonPressed = true;
		Check(page.FindChildren("*", "", true, false).OfType<RichTextLabel>().Any(l => l.Visible && l.Text.Contains("MCP part")), "expanding a call shows its arguments and result");
		await Screenshot("gark-activity");
		page.QueueFree();
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}
}
#endif
