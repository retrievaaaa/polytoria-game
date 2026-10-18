# Gark: the Polytoria Creator MCP server

Gark lets the AI agent you already use work directly in Polytoria Creator. Creator runs a [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server, and any MCP client can connect to it: Claude Code, Codex, Cursor, VS Code (Copilot agent mode), Claude Desktop, Gemini CLI, Windsurf and others. Creator itself needs no AI account, API key or sign-in. The agent uses its own model and subscription.

## Connecting an agent

1. Click **Gark** in the Creator toolbar and turn **Server on**. The setting is saved. It's off by default, and only programs on this computer can connect.
2. Pick your agent under **Connect an agent** and copy the command or configuration. For Claude Code, Cursor, VS Code and Gemini CLI, **Add to this project** writes the project's config file (`.mcp.json`, `.cursor/mcp.json`, `.vscode/mcp.json` or `.gemini/settings.json`) and keeps any other servers already listed there.
3. Open a project in Creator and ask your agent for something, for example *"build a lobby with a spawn area and a scoreboard UI, then playtest it"*.

| Agent | Setup |
| --- | --- |
| Claude Code | `claude mcp add --transport http polytoria http://127.0.0.1:47830/mcp` (add `--scope user` for every project) |
| Codex CLI / app | `codex mcp add polytoria --url http://127.0.0.1:47830/mcp`, or `[mcp_servers.polytoria]` with `url` and `tool_timeout_sec = 600` in `~/.codex/config.toml` |
| Cursor | `{"mcpServers":{"polytoria":{"url":"http://127.0.0.1:47830/mcp"}}}` |
| VS Code | `{"servers":{"polytoria":{"type":"http","url":"http://127.0.0.1:47830/mcp"}}}` in `.vscode/mcp.json` |
| Gemini CLI | `{"mcpServers":{"polytoria":{"httpUrl":"http://127.0.0.1:47830/mcp","timeout":600000}}}` |
| Windsurf | `{"mcpServers":{"polytoria":{"serverUrl":"http://127.0.0.1:47830/mcp"}}}` |
| Claude Desktop and other stdio-only clients | Command: the Creator executable with `--headless --quiet --gark-mcp` (the Gark page shows the exact path) |

The **Activity** tab lists connected agents and every tool call, with its arguments, result and any images. **Allow edits** switches agents to read-only: tools that change worlds or scripts disappear from the tool list (clients are notified), while inspection, captures and playtests keep working. **Disconnect agents** ends every connection and cancels running tools. **Stop test** stops a playtest an agent started.

The port is configurable (default 47830; Creator Settings → Gark or the Gark page). Launching Creator with `--gark-mcp-serve [port]` runs the server for that session without changing the saved setting, which is useful for scripting.

## Transports

- **Streamable HTTP** at `http://127.0.0.1:<port>/mcp` (protocol versions 2025-11-25, 2025-06-18, 2025-03-26 and 2024-11-05). Quick calls get a JSON reply. Calls that take longer than 1.5 s switch to an event stream when the client accepts one. The stream carries `notifications/progress` (when the client sends a progress token) and keep-alive comments, so long playtests or big builds don't hit client timeouts. A `GET` stream delivers `notifications/tools/list_changed`. Unknown session IDs are adopted rather than rejected, so agents keep working after Creator restarts.
- **HTTP+SSE** (older clients) at `/sse` with `/messages?sessionId=…`.
- **stdio**: `<Creator executable> --headless --quiet --gark-mcp` starts a small bridge without a window. It finds the running Creator through `user://creator/gark/mcp.json` (or `--gark-mcp-url <url>`) and forwards everything, including server notifications. It answers `initialize`, `tools/list`, prompts and guide resources itself while Creator is closed, and reconnects when Creator comes back. In the Godot editor, add `--path <repository>/Polytoria`.

The server binds 127.0.0.1 and ::1 only. It refuses browser requests from other sites (any non-local `Origin`), DNS-rebinding `Host` headers and non-JSON bodies, so a web page cannot drive it. Local tools such as the MCP Inspector (`http://localhost:…`) are allowed. Cancelling a request (`notifications/cancelled`) or disconnecting stops the running tool. Edits that already completed stay in Creator's undo history, and in-progress construction is rolled back.

## What agents get

- **Server instructions** sent on `initialize`: the recommended workflow and Polytoria's verified coordinate and UI conventions. Clients that ignore instructions can read the same text with `get_creator_guide` or the `polytoria://guide/*` resources.
- **Tools** (46), each with a JSON schema and MCP annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) so clients can auto-approve reads.
- **Resources**: `polytoria://guide/workflow|coordinates|ui`, `polytoria://api/polytoria.d.luau` (the complete generated Luau type definitions used by the script editor), `polytoria://context` (live state), and every project script as `polytoria://scripts/{path}`, including unsaved editor changes.
- **Prompts**: `gark` (build something end to end), `explain_project`, `fix_errors`.

| Area | Tools |
| --- | --- |
| Context and discovery | `get_context` (project folder, worlds, selection, camera, undo/redo, playtest), `get_creator_guide`, `list_instances` (search, class filter, chosen property values), `inspect_instance` (one or many, by ID or `/path`), `class_schema` (property types and JSON formats, enums, allowed asset classes, Luau API), `search_api` |
| World editing | `create_instances` (whole hierarchies in one call with `"@N"` parents), `set_properties` (one object, many objects, or per-object edits in one undo step), `delete_instances`, `duplicate_instances`, `reparent_instances`, `group_instances`, `ungroup_instances`, `select_instances` |
| Procedural construction | `run_scene_code`: Luau loops, functions and math, applied as one undoable batch |
| Spatial inspection and placement | `get_bounds`, `measure_distance`, `raycast`, `overlap_box`, `place_on_ground`, `align_instances` |
| Visual inspection | `capture_viewport` (frame objects with `focus_ids`, view presets, several views in one call, PNG or JPEG up to 3840×2160), `focus_camera` (show the user a result) |
| Scripts | `list_scripts`, `read_script`, `grep_scripts`, `patch_script` (several exact edits per call, CRLF-aware), `write_script`, `create_script`, `delete_script`, `script_diagnostics`, `open_script` |
| Playtesting and debugging | `start_playtest`, `stop_playtest`, `inspect_playtest`, `playtest_input`, `playtest_eval` (run Luau on the live server or client and get JSON results), `capture_playtest`, `playtest_logs` |
| Toolbox assets | `search_assets`, `inspect_asset` (metadata and thumbnail), `import_asset` (model, mesh, image, audio; an ID from the user can be imported directly) |
| History, saving and worlds | `undo`, `redo` (with step counts, reporting what changed), `save_world` (current or all open worlds), `switch_world` |

Values use one JSON codec everywhere: vectors `[x,y,z]` or `{x,y,z}`, colors `"#RRGGBB"`, a color name or `[r,g,b]`, enums by name, instance references by ID or path, number ranges and series, `Variant` values, data structs (such as `UIScale`), and asset references as `{"class_name","properties"}` (a bare number is the image, mesh or audio ID). Argument parsing is lenient (numbers or booleans sent as strings, a single ID instead of a list, arguments sent as a JSON string), but unknown argument names are rejected with the list of valid ones, so typos never fail silently. Errors come back as `isError` tool results that say what was wrong and how to fix it.

Read-only tools run concurrently. Tools that change the project or a playtest run one at a time, on Creator's main thread. Each call is pinned to the project and world that were active when it started. If the user switches worlds mid-call, the call stops and asks the agent to re-read the context. World changes use Creator history. Script tools read unsaved editor buffers, update the editor and disk together, keep the user's caret, and undo restores both. `write_script` requires the current SHA-256 so newer edits are never overwritten. Script paths stay inside the active project and cannot traverse symbolic links. Internal Creator objects and static services can't be removed.

There is no tool-count, round or run-duration limit. Large inputs are allowed: up to 10,000 objects or IDs per call, 5 MB scripts and 64 MB requests.

### Construction

`run_scene_code` runs in a private Luau VM inside Creator. It produces only existing instances, editable properties and ordinary asset references, and adds no gameplay classes, serialization changes or client runtime requirements. For example, a whole staircase in one action:

```lua
local stairs = scene.create("Model", {Name="Staircase"})
for i = 1, 20 do
    scene.create("Part", {
        Name = "Step " .. i,
        Position = {i * 3, i, 0},
        Size = {3, 1, 6},
        Anchored = true,
        Color = "#8c929e",
    }, stairs)
end
return stairs
```

`scene.create`, `scene.set`, `scene.reparent` and `scene.delete` stage edits. Creation returns temporary handles, and the tool result maps them to real instance IDs. `scene.get`, `scene.children`, `scene.descendants`, `scene.find`, `scene.selection`, `scene.bounds`, `scene.raycast` and `scene.overlap` inspect the scene as it was before the batch, so query newly created geometry in the next call. Normal Luau computation libraries, `print` and plain-table return values are supported. Gameplay globals, IO and networking are absent. The VM yields regularly to keep Creator responsive, with no total execution limit. An error, a world switch or a cancellation rolls back the current batch, and completed batches remain undoable.

### Spatial tools

Bounds are world-space axis-aligned boxes, including rotation and scale. Box overlap and bounds-gap measurements are conservative. Raycasts use the existing collision shapes. Ground placement casts down from each object's bounding-box top center, then places its bottom at the hit height plus optional clearance. Alignment supports minimum, center and maximum bounds along each axis. Placement operations are single undo actions.

### Playtests

Playtests pack a temporary snapshot in the normal project format, including open worlds and unsaved scripts. Gark owns a separate local server and one rendered client, controlled through authenticated loopback IPC that exists only in the Creator build. Tests never save the editing world or copy gameplay changes back. Restart the test after editing to pick up a new snapshot.

- `inspect_playtest` returns the player, the camera and runtime objects, including UI rectangles and button text.
- `playtest_input` sends keys, movement actions, mouse clicks and drags, and text to the client.
- `playtest_eval` runs a Luau chunk as a normal game script on the server or client. A bare expression is returned REPL-style. It returns the chunk's values as JSON, plus its output and errors.
- `capture_playtest` returns the real game viewport with screen UI.
- `playtest_logs` returns structured runtime messages and labeled engine output, filterable by severity and source, and available after the test stops.

### Toolbox

Search uses the same public catalog and asset endpoints as Creator. Thumbnails reach the agent as images. Models use the existing packed or legacy importer, and their dependencies go into a unique Toolbox folder in the project. Imported scripts never run in Creator. Mesh, image and audio imports use the existing `PTMeshAsset`, `PTImageAsset` and `PTAudioAsset` references.

### Privacy

Everything an agent requests (scene data, scripts, captures, logs, asset metadata) goes to that agent and its model provider under the agent's own terms. Gark has no shell, arbitrary file access, publishing or credential tools, and tool results tell agents to treat project content as data, not instructions.

## Verification

Build the Creator configuration and run the Godot .NET executable with:

```text
--path <repository>/Polytoria --creator --garktest --rendering-method gl_compatibility
```

This uses a disposable project under `target/gark-qa`. It covers:

- Real Creator world, history and script tools; procedural rollback, undo and redo; infinite-loop cancellation; spatial queries; Toolbox previews and imports against a fake catalog; real Luau diagnostics; GPU captures.
- The MCP protocol: version negotiation, annotations, read-only mode, prompts and resources.
- The HTTP transport end to end: sessions, batches, image content, Origin, Host and Content-Type protection, SSE upgrade with progress for long calls, cancellation by notification and by disconnect, `list_changed` pushes, legacy SSE and session deletion.
- The stdio bridge, launched as a separate process.
- The Gark page, including writing project config files.

Add:

- `--garkplaytest` for a real disposable server and client: UI clicks, movement, camera drags, `playtest_eval` on server and client, screenshots, runtime errors, cancellation and process cleanup.
- `--garkassetlive` to search and import a real public Toolbox model.
- `--garkinterop --gark-mcp-serve <port>` to leave Creator running on a disposable project, so real agents can be pointed at it.

## Icon

Asset: `assets/textures/creator/gark.png`. Generated with the built-in ImageGen tool, using the supplied Gark avatar as the character reference. Final prompt:

> Use case: logo-brand. Create one production-ready flat toolbar icon for Gark, the game-creation assistant, using the attached image as the character reference. Preserve its unmistakable bright lemon-yellow round head, two peach/orange cat ears with pink inner ears, two simple black round eyes and small friendly black curved smile. Head only, front facing, centered, bold clean silhouettes and very few shapes, crisp flat vector-like bitmap style, readable at 24 and 32 pixels, no gradients, no lighting, no 3D, no body, no shadows, no text, no border, no extra decoration. Square image with genuinely transparent background and about 8 percent transparent padding. Deliver an actual transparent PNG icon.
