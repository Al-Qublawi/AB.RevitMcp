# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.0] — 2026-09-14

### Changed
- **The add-in moved to the shared `AB Adv Tools` ribbon tab**, which every AB add-in for Revit now
  uses, as the **MCP Bridge** panel (formerly the *Bridge* panel on its own *AB MCP AI* tab). All
  bridge buttons are unchanged. About and LinkedIn moved to the suite's shared **AB Adv Tools**
  panel at the end of the tab, which also lists every AB tool loaded, with versions.
- **New installer engine.** `AB.RevitMcp.Setup.exe` is now built on the AB Adv Tools installer
  engine (`shared\ABAdvTools`): it detects an earlier copy of the bridge and offers to remove it
  before installing, registers in Apps and Features (per user, no elevation), and adds
  `/uninstall`, `/scan` and `/log:<file>`. Everything the 1.3 installer did is kept: the Revit and
  AI-agent lists, **Add custom agent…** (any JSON or YAML MCP config, with its own server-map key),
  **Verify** at any time without installing, carrying on with Revit open (a locked release is
  skipped and reported, the rest install), `/silent`, `/noclients`, and the
  `%TEMP%\ABRevitMcp-Setup-*.log` silent log.
- **About** is the suite's shared About dialog; selecting the bridge shows what its own About showed
  (tool count, Revit version, units) with an **Open the log folder** button.
- `build-all.ps1` builds every Revit release from the pinned reference packages, so an installer
  built on any machine carries 2020 – 2026. It used to skip releases not installed locally, even
  though the project no longer needs them. `-UseInstalledRevit` keeps the old behaviour.

### Added
- **Release notifications.** Once a day at most, in the background, the add-in checks this
  repository's latest GitHub release and says so once per new version. **Check for Updates** on
  the ribbon checks immediately. Anonymous, no telemetry; off switch in **AB Adv Tools › About**.

### Fixed
- The version shown in the About dialog was a hand-kept constant that still said 1.1.0. It now
  comes from the build.

## [1.3.0] — 2026-09-12

### Fixed
- **`spawn EPERM` - the server could not start on a managed device.** Defender's ASR rule
  `01443614-cd74-433a-b99e-2ecdc07bfc25` refuses to create a process for an unsigned binary with
  no prevalence history, which ours is by definition. Setup now detects .NET and registers clients
  as `dotnet.exe AB.RevitMcp.Server.dll`, using the managed assembly that already ships beside the
  apphost. The process created is then Microsoft-signed `dotnet.exe` and the rule has nothing to
  act on. It only takes this route after proving it works on the machine, and falls back to the
  direct executable otherwise.
- **`--print-config` emitted a config that starts nothing** when the server itself ran under
  `dotnet.exe`: it named itself from `Process.MainModule`, which is `dotnet.exe`, and passed no
  arguments. It now reports the real argument list, in both JSON and YAML.
- **The allowlist file IT uses to whitelist the build had its version hardcoded** to `1.1.0`, so it
  misreported every release since 1.1.0. It now reads the built executable.

### Changed
- **The add-in compiles against pinned Revit API reference packages** (`Nice3point.Revit.Api.*`)
  rather than the DLLs of whatever Revit happens to be installed on the build machine. Revit 2026
  update 26.5 is built against .NET 10, which made the net8 project unbuildable on an updated
  machine (`CS1705`) for reasons unconnected to our code. The pinned 2026.4.10 reference
  assemblies still target `net8.0-windows7.0`, and the resulting add-in loads correctly under
  26.5 - verified against a live 26.5.0.55 session - because .NET stayed backward compatible.
  Targeting .NET 10 instead would break everyone still on 26.4. Build against a local install with
  `-p:UseRevitApiPackages=false`. Revit no longer needs to be installed to build.

## [1.2.0] — 2026-09-02

### Changed
- **Per-tool request budgets.** A single 15-second timeout applied to every call, which made
  `revit_export` impossible to complete: exporting a sheet set is minutes of Revit's own work, and
  unlike a list tool there is no page size the caller can shrink. Tools now declare their own
  budget via `ToolDescriptor.TimeoutMs`:
  - default for ordinary calls: **15 s → 30 s**
  - `revit_export`: **5 minutes**
  - ceiling for `--timeout`: **2 → 10 minutes**

  A declared budget is a **floor, not a cap** — raising `--timeout` still wins, so an unusually
  large export can be given more room without a rebuild, but no flag can silently cut an export
  short. `bridge/describe` now reports a tool's declared budget.

## [1.1.0] — 2026-09-02

The Modify tab, plus the geometry and MEP batches. **78 tools** (24 read / 48 write / 6 destructive).

### Added
- **Modify tools** — `revit_split_element`, `revit_trim_extend_elements`, `revit_align_elements`,
  `revit_offset_elements`, `revit_pin_elements`, `revit_cut_geometry`, `revit_set_element_phase`,
  `revit_list_phases`.
- **Geometry & transform** — `revit_mirror_elements`, `revit_array_elements`, `revit_group_elements`,
  `revit_join_geometry`, `revit_create_reference_plane`, `revit_create_ceiling`, `revit_load_family`,
  `revit_export`.
- **MEP** — `revit_create_duct`, `revit_create_pipe`, `revit_create_cable_tray`,
  `revit_create_conduit`, `revit_list_mep_systems`.
- **Annotation & views** — `revit_create_text_note`, `revit_tag_elements`, `revit_create_detail_line`,
  `revit_create_schedule`, `revit_duplicate_view`, `revit_apply_view_template`,
  `revit_set_element_visibility`, `revit_override_element_graphics`, `revit_set_view_section_box`,
  `revit_get_view_center`.
- **Single-file installer** with automatic MCP client registration for 12 known agents, including
  OpenClaw's nested `mcp.servers` shape.

### Fixed
- **`revit_split_element` destroyed hosted openings.** The split duplicated the host with
  `CopyElement`, which copies the wall alone — so every door and window in the discarded half lost
  its host and was deleted. It now copies the wall together with `Wall.FindInserts`, letting Revit
  re-host the openings onto the copied wall so each survives on the correct side.
- Split now reports `originalKept: true`. The original element keeps its `ElementId` and takes the
  first segment, so tags, dimensions and schedule rows stay attached.

## [1.0.0] — 2026-09-01

First working release. 61 tools, Revit 2020–2026.

### Added
- Named-pipe bridge between the MCP server and a Revit add-in, with all Revit API work marshalled
  onto the main UI thread via `ExternalEvent` + `ConcurrentQueue`.
- Read, write and destructive tool groups; destructive tools gated behind `confirm: true` with
  `dryRun` support.
- Automatic imperial → metric conversion at the boundary (mm, m², m³, degrees).
- stdio and Streamable HTTP transports; `--read-only` and `--revit-version` flags.
- Ribbon tab **AB MCP AI** with connection status and a start/stop toggle.

### Fixed
- **"Add-in Assembly Not Found" on startup**, two independent causes: the contracts assembly was
  `netstandard2.0`-only (Revit 2020–2024 on .NET Framework cannot resolve the `netstandard` facade),
  and the add-in was installed under `%LOCALAPPDATA%`, where endpoint protection blocked it. It now
  multi-targets a real `net48` and installs beside its manifest under `%APPDATA%`.
