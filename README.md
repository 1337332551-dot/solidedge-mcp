# solidedge-mcp

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**MCP servers that let AI clients (Claude Desktop, Cursor, Trae, Cline, ...) drive Siemens Solid Edge** — query models, build parametric features, automate drawings, and listen to document events, all through natural conversation.

Solid Edge remains the single source of truth: the AI never replaces your CAD workflow, it operates it — like a pair of hands on your running Solid Edge instance.

- [English](README.md) | [中文文档](README.zh-CN.md)

## Why

Mechanical engineers lose hours on repetitive CAD operations: filling in parametric models, renaming features, checking BOM consistency, exporting drawings. This project exposes Solid Edge's COM API to any MCP-capable AI client, so those steps become a conversation instead of a macro you have to write and maintain.

Real workflows it supports today:

- **Parametric model building** — describe features in a JSON spec, AI fills the parameters (`se_model_build` with static dry-run validation before anything touches your model)
- **Model interrogation** — walk the object tree, read geometry, variables, selection state
- **Drawing automation** — view crops, drawing frames, center marks, driven dimensions
- **Assembly checks** — interference detection, weight rollups
- **Event monitoring** — a second MCP server streams Solid Edge document events to the AI

## Two MCP servers

| Server | Binary | Purpose |
|---|---|---|
| Execution | `solidedge-mcp` | 21 tools: query, document, modeling, scripting |
| Events | `solidedge-event-mcp` | 4 tools: subscribe/wait/query Solid Edge events |

## Tool overview (execution server)

| Category | Tools |
|---|---|
| Query / read | `se_get_document` `se_get_selection` `se_find_paths` `se_describe_object` `se_walk_object` `se_batch_read` `se_read_geometry` `se_get_variables` `se_view_context` `se_capture_viewport` `se_snapshot_diff` `se_validate_features` |
| Document session | `se_open_document` `se_new_document` `se_close_document` |
| Model changing | `se_model_build` `se_extrude_on_face` `se_invoke_member` `se_invoke_chain` `se_recipe_run` |
| Escape hatch | `se_script_run` (run a C# script against the COM API) |

## Permission model

Set the `SE_MCP_MODE` environment variable on the server entry in your MCP config:

| Value | Behavior |
|---|---|
| `full` *(default)* | All 21 tools allowed |
| `readonly` | Only the 12 query tools; model-changing/session/script calls are rejected at the transport layer with a hint on how to switch back |
| anything else | Fail-closed: treated as `readonly` |

Legacy `SE_MCP_READONLY=1` is still honored and maps to `readonly`. Changing the mode requires restarting the MCP session (the AI client reloads the server).

There is also a member-level guardrail (`Guardrail`) as a second onion layer, and every tool call is written to an audit log at `%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`.

## Requirements

- Windows + a running **Siemens Solid Edge** install (interop package targets SE2022 / type library v108; other versions may work — see below)
- **.NET 8 SDK** to build, or use a published binary
- An MCP-capable AI client

## Build

```powershell
git clone https://github.com/<your-account>/solidedge-mcp.git
cd solidedge-mcp
dotnet build src/SolidEdge.Spy.McpServer -c Release
dotnet build src/SolidEdge.Spy.EventMcp  -c Release
dotnet test tests/SolidEdge.Spy.McpServer.Tests
```

No Siemens files are needed from you up front: the COM interop assembly comes from the community-published [`Interop.SolidEdge`](https://www.nuget.org/packages/Interop.SolidEdge) NuGet package (pure type definitions, no Siemens proprietary code is distributed in this repo).

If you prefer building the interop assembly from your own installed Solid Edge (e.g. for a different SE version), use `scripts/gen_interop.ps1` with the .NET Framework `TlbImp.exe` tool and reference the produced DLLs instead.

## Configure your AI client

Point the server at the built binary. Examples:

**Claude Desktop / Cursor / Cline** (`claude_desktop_config.json` / `mcp.json`):

```json
{
  "mcpServers": {
    "solidedge": {
      "command": "D:/path/to/solidedge-mcp/src/SolidEdge.Spy.McpServer/bin/Release/net8.0-windows/solidedge-mcp.exe",
      "env": { "SE_MCP_MODE": "readonly" }
    },
    "solidedge-events": {
      "command": "D:/path/to/solidedge-mcp/src/SolidEdge.Spy.EventMcp/bin/Release/net8.0-windows/solidedge-event-mcp.exe"
    }
  }
}
```

**Trae** (`.trae/mcp.json` in your workspace) uses the same shape.

Start Solid Edge first, then start a new conversation in your client — the server attaches to the running instance automatically.

## CLI mode

The execution binary doubles as a one-shot CLI for debugging without any AI client:

```powershell
solidedge-mcp.exe get_document
solidedge-mcp.exe invoke_member --objectId <id> --member Name
```

## Architecture

```
AI client (Claude/Cursor/Trae)
   │  stdio JSON-RPC
   ▼
PermissionTap ── mode × tool-risk-tier gate (deny fabricated before SDK)
   ▼
JsonRpcTap ── usage metering / audit
   ▼
MCP SDK tool handlers ── Guardrail member-level checks
   ▼
COM interop (IDispatch + PIA) ── running Solid Edge instance
```

```
src/
├── SolidEdge.Spy.McpServer/    # execution MCP server (21 tools)
├── SolidEdge.Spy.EventMcp/     # events MCP server (4 tools)
├── SolidEdge.Shared/           # COM interop infrastructure shared at compile time
tests/                          # 188 unit tests (pure logic, no SE needed)
scripts/                        # helper scripts (interop generation)
```

## Development

```powershell
dotnet test tests/SolidEdge.Spy.McpServer.Tests
```

Tests are pure .NET (no Solid Edge required) and cover parsing, validation rules, the permission tier table, and the transport tap.

## Credits & license

- COM interop infrastructure (`InteropServices/*`, extensions) derives from [Jason Newell's SolidEdgeSpy](https://github.com/JWSingleton/SolidEdgeSpy) — this project started as a fork of that codebase.
- Interop assemblies published by the [Solid Edge Community](https://github.com/SolidEdgeCommunity).

MIT — see [LICENSE](LICENSE).

> This project is not affiliated with or endorsed by Siemens. Solid Edge is a trademark of Siemens Digital Industries Software.
