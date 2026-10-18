// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Creator.LSP;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Creator.Gark;

internal sealed partial class GarkTools
{
	private async Task<JsonObject> ScriptDiagnostics(JsonObject args, CancellationToken token)
	{
		string[] paths = args["path"] != null ? [Normalize(Text(args, "path"))] : ScriptFiles().ToArray();
		int offset = Int(args, "offset", 0, 0, int.MaxValue), limit = Int(args, "limit", 30, 1, 500);
		string[] selected = paths.Skip(offset).Take(limit).ToArray();
		// A dedicated LSP view avoids disturbing the editor's didOpen/didChange versions.
		// Open all project buffers before querying so require() also sees unsaved modules.
		var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string relative in ScriptFiles().Concat(selected).Distinct())
			snapshots[relative] = ReadBuffer(ScriptPath(relative));
		Progress?.Invoke($"Checking {selected.Length} script(s) with the Luau language server");
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
		timeout.CancelAfter(TimeSpan.FromSeconds(45)); // A failed LSP request is not a clean result.
		var start = new ProcessStartInfo(NativeBinHelper.ResolveLuauLspBinPath()) {
			WorkingDirectory = session!.ProjectFolderPath, UseShellExecute = false, CreateNoWindow = true,
			RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
		};
		start.ArgumentList.Add("lsp"); start.ArgumentList.Add("--stdio");
		start.ArgumentList.Add("--definitions=@poly=.poly/luau/def.d.luau");
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the bundled Luau language server.");
		process.ErrorDataReceived += (_, _) => { }; // Drain stderr without mixing it into protocol output.
		process.BeginErrorReadLine();
		using var terminate = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
		using var lsp = new LspClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
		try
		{
			await lsp.InitializeAsync(session.ProjectFolderPath, timeout.Token, pullDiagnostics: true);
			foreach (var (relative, source) in snapshots)
			{
				CheckContext(timeout.Token);
				await lsp.DidOpenAsync(ScriptPath(relative), "luau", source);
			}
			var files = new JsonArray();
			foreach (string relative in selected)
			{
				CheckContext(timeout.Token);
				var raw = await lsp.RequestDiagnosticsAsync(ScriptPath(relative), timeout.Token);
				var report = JsonNode.Parse(raw.GetRawText())!.AsObject();
				if (report["kind"]?.ToString() != "full" || report["items"] is not JsonArray items)
					throw new InvalidOperationException("Language server returned no complete diagnostics for " + relative);
				foreach (JsonNode? diagnostic in items)
				{
					// Match read_script's one-based lines. UTF-8 columns are explicit (LSP encoding).
					foreach (string end in new[] { "start", "end" })
					{
						var position = diagnostic!["range"]![end]!;
						position["line"] = position["line"]!.GetValue<int>() + 1;
						position["character"] = position["character"]!.GetValue<int>() + 1;
					}
				}
				string source = snapshots[relative];
				files.Add((JsonNode)new JsonObject { ["path"] = relative, ["sha256"] = GarkProjectFiles.Hash(source),
					["stale"] = ReadBuffer(ScriptPath(relative)) != source, ["diagnostics"] = items.DeepClone(),
					["mode"] = source.TrimStart().StartsWith("--!strict") ? "strict" : source.TrimStart().StartsWith("--!nonstrict") ? "nonstrict" : "project configuration / script directives" });
			}
			CheckContext(token);
			var changed = new JsonArray();
			foreach (var (relative, source) in snapshots)
			{
				bool stale;
				try { stale = ReadBuffer(ScriptPath(relative)) != source; }
				catch (System.IO.IOException) { stale = true; }
				if (stale) changed.Add((JsonNode?)JsonValue.Create(relative));
			}
			if (changed.Count > 0) foreach (var file in files) file!["stale"] = true;
			return new() { ["files"] = files, ["total_files"] = paths.Length, ["next_offset"] = offset + selected.Length < paths.Length ? offset + selected.Length : null,
				["buffers_changed_during_check"] = changed,
				["positions"] = "One-based lines and one-based UTF-8 byte columns, end exclusive.",
				["note"] = "Fresh language-server results for the captured buffers. Project languageMode and --!nocheck/--!nonstrict/--!strict apply; nocheck suppresses type checking. Empty results are not a runtime test." };
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{ throw new InvalidOperationException("The language server did not finish diagnostics. No clean result is available; retry or inspect Creator's language-server setup."); }
		finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
	}
}
