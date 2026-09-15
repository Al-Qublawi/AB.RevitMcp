# AB Revit MCP Bridge

A universal **Model Context Protocol** server that lets any MCP-compatible AI client — Claude
Desktop, Claude Code, Cursor, VS Code, DeepSeek, Continue, n8n, or anything else that speaks MCP —
safely **query, create and modify Autodesk Revit models**.

Revit 2020 through 2026. 78 tools. Metric in, metric out. No AI vendor lock-in.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Revit 2020–2026](https://img.shields.io/badge/Revit-2020--2026-0696D7)
![Tools](https://img.shields.io/badge/MCP%20tools-78-brightgreen)
![.NET](https://img.shields.io/badge/.NET-Framework%204.8%20%7C%208.0-512BD4)

**by [Abdullah Lotfy](https://www.linkedin.com/in/abdullahalqublawi/)**

---

## Architecture

```
   AI / MCP client                MCP server                Revit add-in              Revit
 ┌──────────────────┐        ┌──────────────────┐      ┌──────────────────┐    ┌─────────────┐
 │ Claude / Cursor  │ stdio  │ AB.RevitMcp      │ named│ AB.RevitMcp      │    │             │
 │ VS Code / n8n    │◄──────►│ .Server.exe      │ pipes│ .Addin.dll       │    │  Revit API  │
 │ DeepSeek / ...   │  HTTP  │                  │◄────►│                  │◄──►│  (UI thread)│
 └──────────────────┘        │ JSON-RPC 2.0     │ JSON │ ExternalEvent    │    │             │
                             │ tool catalogue   │      │ + ConcurrentQueue│    └─────────────┘
                             │ schema validation│      │ + Transactions   │
                             └──────────────────┘      └──────────────────┘
      no vendor SDK              own process             inside Revit.exe
```

The single most important line in the codebase is the boundary between the last two boxes.
Named-pipe handlers run on thread-pool threads; **the Revit API may only be touched from Revit's
main UI thread**. `RevitDispatcher` is the only legal crossing: work is queued into a lock-free
`ConcurrentQueue`, an `ExternalEvent` is raised, and Revit calls back on its own thread when it is
safe. Nothing else in the add-in calls the Revit API off that thread.

### Projects

| Project | Target | Role |
| --- | --- | --- |
| `AB.RevitMcp.Contracts` | `net48` + `netstandard2.0` + `net8.0-windows` | JSON DOM, wire protocol, **the tool catalogue**, schema validator. Zero package references. The real `net48` target is mandatory - see below. |
| `AB.RevitMcp.Ipc` | `net48` + `net8.0-windows` | Length-prefixed named-pipe framing, ACL'd pipe server/client, endpoint discovery. |
| `AB.RevitMcp.Addin` | `net48` + `net8.0-windows` | Revit add-in: ribbon, UI-thread dispatcher, transaction policy, 78 tool implementations. |
| `AB.RevitMcp.Server` | `net8.0-windows` | Standalone MCP server: JSON-RPC over stdio or Streamable HTTP. |
| `AB.RevitMcp.MockBridge` | `net8.0-windows` | Fake Revit bridge for testing a client configuration without opening Revit. |
| `installer\` | — | `RevitMcp.msi.psd1` + `RevitMcp.AiClients.wxs`: the `.msi`, built by the AB Adv Tools kit. No code runs inside it. |

The tool catalogue lives in the **shared contracts assembly**, which is why the server can answer
`tools/list` while Revit is closed, and why a schema and its implementation cannot drift apart —
`ToolRouter.Register` refuses any handler whose name is not in the catalogue, and
`revit_bridge_status` reports any catalogue entry that has no handler.

> **Why Contracts must have a real `net48` target.** A `netstandard2.0` assembly references the
> `netstandard, Version=2.0.0.0` facade. Revit 2020-2024 host .NET Framework with no binding
> redirect for it, so the CLR fails to resolve the facade and Revit reports
> *"Failed to initialize the add-in ... because the assembly ... does not exist"* — naming the
> top-level add-in DLL, which is present the entire time. Shipping a genuine `net48` build of
> Contracts removes the facade reference and the add-in loads. The Doctor checks for this on every
> run.

---

## Version compatibility

`AB.RevitMcp.Addin` multi-targets `net48;net8.0-windows` and compiles once per Revit release with
version symbols, so each breaking API change is handled in exactly one place (`Revit/Compat.cs`,
`Revit/Metric.cs`):

| Revit | Runtime | Build with | Notable API differences handled |
| --- | --- | --- | --- |
| 2020 | .NET Framework 4.8 | `-f net48 -p:RevitVersion=2020` | `DisplayUnitType`, `ParameterType` |
| 2021 | .NET Framework 4.8 | `-f net48 -p:RevitVersion=2021` | `ForgeTypeId` / `UnitTypeId`, `GetSpecTypeId()` |
| 2022 | .NET Framework 4.8 | `-f net48 -p:RevitVersion=2022` | `GetDataType()`, `Floor.Create(CurveLoop)` |
| 2023 | .NET Framework 4.8 | `-f net48 -p:RevitVersion=2023` | `Category.BuiltInCategory` |
| 2024 | .NET Framework 4.8 | `-f net48 -p:RevitVersion=2024` | `ElementId.Value` (long), `Document.GetUnusedElements` |
| 2025 | .NET 8 | `-f net8.0-windows -p:RevitVersion=2025` | runtime transition |
| 2026 | .NET 8 | `-f net8.0-windows -p:RevitVersion=2026` | — |

The `.csproj` errors out with a clear message if you pair the wrong TFM with a release, or if
`RevitAPI.dll` is missing.

---

## Install

### Option 1 — the installer (recommended, and what you copy to other machines)

Download **`AB.RevitMcp-<version>.msi`** from the [latest release](../../releases/latest)
and double-click it.

One Windows Installer package, about 33 MB, no prerequisites beyond .NET Framework 4.8 (which
Revit itself requires). Everything is per user: no admin rights, no services. Its pages:

- **Choose releases** — a tick per Revit release, ticked where that Revit is installed;
  remembered for the next upgrade.
- **AI clients** — the AI agents to point at the server (the ones found are ticked), plus an
  optional **custom agent**: any MCP config file, JSON or YAML, with its own server-map key.
- **Earlier versions** — a copy installed by 1.4.0's `AB.RevitMcp.Setup.exe` is removed first
  unless you untick it.

It appears in Apps and Features for uninstall and repair.

**The AI clients are configured by Revit, the first time it starts after installing** — the
package runs no code of its own (see below), so it only records the ticks. Revit then writes each
client's config (backing it up first) and shows what it did. Restart the clients afterwards.
**AI Clients** on the MCP Bridge panel does the same at any time: add a custom agent with a file
browser, configure or remove the `revit` server, and **Verify** the whole chain.

> **Why an .msi.** On company PCs, Windows Defender's Attack Surface Reduction rule *"Block
> executable files from running unless they meet a prevalence, age, or trusted list criteria"*
> blocks unknown executables — 1.4.0's unsigned `AB.RevitMcp.Setup.exe` among them. The rule does
> not apply to Windows Installer packages, and this one contains no step that runs code (the build
> checks). The server itself is started through `dotnet.exe` where .NET is installed, for the same
> reason. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

For unattended IT deployment:

```
msiexec /i AB.RevitMcp-1.5.0.msi /qn               :: clients found are configured when Revit starts
msiexec /i AB.RevitMcp-1.5.0.msi /qn NOCLIENTS=1   :: configure no AI client
msiexec /i AB.RevitMcp-1.5.0.msi /qn REVIT2022=0   :: leave a release out (ALLRELEASES=1: every release)
msiexec /i AB.RevitMcp-1.5.0.msi /l*v setup.log    :: with a log
msiexec /x AB.RevitMcp-1.5.0.msi /qn               :: remove it again
```

If Revit is open while installing, Windows lists it and asks you to close it, or to let the
update finish at the next restart.

The package is built by the AB Adv Tools kit shared by every AB add-in (`shared\ABAdvTools\msi`);
`build\build-installer.ps1 -DryRun` shows what it would do on a machine without changing anything.

### Release notifications

Once a day at most, in the background, the add-in asks GitHub whether a newer release of this
repository exists, and says so **once per version** when Revit is idle with a view open.
Nothing is sent but an anonymous request for the latest release. **Check for Updates** on the
AB Adv Tools tab checks immediately; the checkbox in **AB Adv Tools › About** switches the
automatic check off for every AB add-in.

Build the installer yourself with:

```powershell
.\build\build-installer.ps1
```

### Option 2 — build and install from source

**Close Revit first** — add-ins load only at startup. Then double-click:

```
INSTALL.bat
```

That runs [`build/install.ps1`](build/install.ps1), which:

1. checks prerequisites (PowerShell, .NET Framework 4.8, which Revit releases are present)
2. builds the add-in for every Revit release found on the machine
3. publishes the MCP server **self-contained** — no .NET runtime needs to be installed
4. installs per-user to `%LOCALAPPDATA%\ABRevitMcp` (no elevation, no Program Files, no registry)
5. writes one `.addin` manifest per Revit release
6. registers the server with Claude Desktop, Cursor and VS Code (backing up each config first)
7. runs the **Doctor** to verify the result before you ever start Revit

Prefer the command line, or want finer control:

```powershell
.\build\install.ps1                          # build + install + verify
.\build\install.ps1 -Versions 2024           # one release only
.\build\install.ps1 -ConfigureClients        # also edit AI client configs
.\build\install.ps1 -SelfContained:$false    # smaller; needs .NET 8 Desktop Runtime
.\build\install.ps1 -SkipBuild               # reuse an existing artifacts folder
```

Result:

```
%APPDATA%\Autodesk\Revit\Addins\2024\AB.RevitMcp.addin      <- manifest
%APPDATA%\Autodesk\Revit\Addins\2024\ABRevitMcp\*.dll       <- add-in, beside the manifest
%LOCALAPPDATA%\ABRevitMcp\Server\AB.RevitMcp.Server.exe     <- MCP server (self-contained)
(diagnostics are a mode of the server: AB.RevitMcp.Server.exe --doctor)
```

The add-in assemblies sit **beside their manifest under `%APPDATA%`**, referenced by a relative
path — the layout mainstream Revit add-ins use. They are deliberately *not* placed in
`%LOCALAPPDATA%`: endpoint-protection suites routinely block DLL loads from Local AppData, and
Revit reports that as *"the assembly does not exist"* about a file that is plainly on disk. The
Doctor warns if the add-in ever ends up loading from there.

Then:

1. Start Revit and open a project.
2. Open the **AB Adv Tools** ribbon tab → **MCP Bridge** panel → press **Start Bridge**.
   The icon turns amber (listening) and then green when a client attaches.
3. Press **Copy config** to put a ready-to-paste `mcpServers` block on the clipboard.

To remove everything: **Apps and Features › AB Revit MCP Bridge**, or for a developer install
`UNINSTALL.bat` (or `.\build\uninstall.ps1`).

### Verify without starting Revit

```
%LOCALAPPDATA%\ABRevitMcp\Server\AB.RevitMcp.Server.exe --doctor
```

The Doctor reproduces Revit's own load checks from assembly **metadata** — manifest parsing,
assembly presence, correct runtime per release, and the `netstandard` facade trap that makes Revit
2020–2024 report *"the assembly does not exist"* about a file that is plainly there. It also starts
the MCP server and pings a live bridge if one is running.

It never loads a Revit binary: `RevitAPIUI.dll` pulls in Autodesk native resource DLLs that only
resolve inside `Revit.exe`, and loading it elsewhere makes Windows pop modal
`AnavRes.dll not found` dialogs.

---

## Connect an MCP client

Templates for each client live in [`config/`](config/). Replace `<YOU>` with your Windows username.

**Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json`
**Claude Code** — `.mcp.json` in the project root, or `claude mcp add revit -- <path to exe>`
**Cursor** — `%USERPROFILE%\.cursor\mcp.json`
**VS Code** — `.vscode\mcp.json` (uses `"servers"` instead of `"mcpServers"`)

```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Users\\<YOU>\\AppData\\Local\\ABRevitMcp\\Server\\AB.RevitMcp.Server.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Revit does **not** need to be running when the client starts — the server connects on demand and
reconnects automatically if Revit restarts.

### Useful flags

| Flag | Effect |
| --- | --- |
| `--read-only` | Advertises every tool but refuses all write and destructive calls. |
| `--revit-version 2024` | Pin to one Revit release when several are open. |
| `--timeout 60000` | Raise the per-request budget (default 30 s, max 10 min). Long-running tools carry their own budget already — `revit_export` gets 5 minutes — and this flag only ever raises it. |
| `--http --port 3333` | Streamable HTTP on loopback instead of stdio. |
| `--print-tools` | Print the full tool reference as Markdown. |
| `--verbose` | Log protocol traffic to stderr. |

Environment equivalents: `AB_REVITMCP_PIPE`, `AB_REVITMCP_REVIT_VERSION`,
`AB_REVITMCP_TIMEOUT_MS`, `AB_REVITMCP_READONLY`.

---

## The tools

Full generated reference: **[docs/TOOLS.md](docs/TOOLS.md)**

| Category | Count | Transaction behaviour | Gate |
| --- | ---: | --- | --- |
| **Read** | 24 | never opens a transaction | none |
| **Write** | 48 | one `TransactionGroup`, assimilated into a single undo step | none |
| **Destructive** | 6 | one `TransactionGroup`, rolled back on any failure | `"confirm": true` |
| **Total** | **78** | | |

**Read** — model info, model health, bridge status, categories, levels, phases, worksets, views,
active view, view centre, sheets, schedules, warnings, linked models, materials, family types,
rooms/spaces/areas, grids, MEP systems, element query, text search, element parameters, element
geometry, current selection.

**Write** — *modelling:* walls, columns, beams, floors, ceilings, family instances, doors, windows,
levels, grids, rooms, reference planes; *MEP:* ducts, pipes, cable trays, conduit; *annotation:*
text notes, tags, detail lines, schedules; *views & sheets:* create/duplicate views, sheets,
viewports, view templates, visibility, graphic overrides, section boxes; *modify:* move, rotate,
copy, mirror, array, group, split, trim/extend, align, offset, pin, join geometry, cut geometry,
phase/demolish; *data:* parameter writes, material and workset assignment, Revit selection, family
loading, export.

**Destructive** — delete elements, delete views, purge unused, unload links, remove links, and
`revit_execute_code`. That last one is a deliberate escape hatch for work no typed tool covers; it
is gated behind `confirm: true` like every other destructive tool, and you can remove it entirely
by running the server with `--read-only`, or by deleting its registration from the catalogue.

---

## Safety model

Layered, and enforced in code rather than documented in prose:

1. **Schema validation twice** — the MCP server validates arguments against the tool's JSON Schema
   before contacting Revit, and the add-in validates again before touching the API. A malformed
   request never reaches a transaction.
2. **The destructive interlock** — `ToolRouter` refuses any destructive tool without a literal
   `confirm: true` (the string `"true"` does not count), independently of what the handler does.
   The MCP server refuses it too, so the check survives a rogue client.
3. **`dryRun`** — destructive tools execute for real and then roll the transaction group back,
   reporting the exact blast radius including cascade deletions (deleting a wall also removes its
   doors and windows — the response says so).
4. **Transaction scoping** — every write runs in a `TransactionGroup`. Success assimilates it into
   one undo step named after the tool; any exception rolls it back completely. A failure cannot
   leave the model half-edited.
5. **No modal dialogs** — a `IFailuresPreprocessor` swallows warnings and rolls back on errors, so
   an AI-driven session can never hang Revit behind a dialog nobody is watching.
6. **No arbitrary code execution** — there is no "run this C#/Python" tool, by design. The attack
   surface is exactly the 46 declared schemas.
7. **Timeouts and cancellation** — 30 s per request by default, and a tool that is inherently
   long declares its own budget instead (`revit_export`: 5 minutes), because an export has no
   page size the caller could reduce. On timeout, queued work is
   discarded and the caller gets a structured `TIMEOUT`; work already running on the UI thread is
   allowed to finish, because forcibly aborting Revit's UI thread would corrupt the document.
8. **Local only** — an ACL'd named pipe restricted to the current Windows user. The HTTP transport
   binds loopback and validates `Origin` against DNS rebinding. Nothing leaves the machine.
9. **Opt-in** — no AI can reach the model until a human presses **Start Bridge**.

---

## Units

Revit stores lengths in decimal feet, areas in square feet, volumes in cubic feet and angles in
radians. **None of that crosses the MCP boundary.** Every crossing goes through `Revit/Metric.cs`:

| Quantity | Unit at the MCP boundary |
| --- | --- |
| Length, coordinates | millimetres (mm) |
| Area | square metres (m²) |
| Volume | cubic metres (m³) |
| Angle | degrees |

Parameter reads report the normalised metric `value`, a `unit` label, and the `displayValue`
exactly as Revit shows it in the properties palette. Parameter writes convert metric → internal
using the parameter's own data type, so `{"name": "Sill Height", "value": 900}` means 900 mm.

Responses are kept AI-friendly: lists page at 50 items by default (500 hard maximum) and report
`nextOffset`; solids, meshes and faces are **never** serialised — geometry is reduced to bounding
boxes and location curves.

---

## Testing without Revit

The mock bridge speaks the real IPC protocol and publishes a real discovery endpoint, so a client
configuration can be proven before Revit is involved:

```powershell
.\artifacts\Release\MockBridge\AB.RevitMcp.MockBridge.exe
```

Then point your MCP client at the server as usual and call `revit_get_model_info` or
`revit_list_levels`.

---

## Logging

Structured newline-delimited JSON, one object per line, rolling daily:

```
%LOCALAPPDATA%\ABRevitMcp\logs\bridge-YYYYMMDD.ndjson
```

Every request records `requestId`, `tool`, `toolCategory`, `ok`, `durationMs`, `resultBytes`,
`client`, and on failure `errorCode` and `errorMessage`. The **Status** ribbon button shows live
counters and the last dozen entries without touching the disk.

---

## Further reading

- [docs/CLIENTS.md](docs/CLIENTS.md) — connecting Claude, Cursor, VS Code, DeepSeek, local models
- [docs/TOOLS.md](docs/TOOLS.md) — generated tool reference
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — threading, transactions, wire protocol
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — when it does not connect

---

## Author

**Abdullah Lotfy**  
[linkedin.com/in/abdullahalqublawi](https://www.linkedin.com/in/abdullahalqublawi/)

The **AB Adv Tools** ribbon tab, shared by every AB add-in, carries an **About** button, **Check for Updates** and a LinkedIn link, and
the add-in manifest records the same details as its vendor.
