# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
