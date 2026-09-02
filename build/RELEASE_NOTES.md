Universal MCP server + Revit add-in. Any MCP-compatible AI client — Claude Desktop, Claude Code,
Cursor, VS Code, Windsurf, Cline, LM Studio, OpenClaw, or a local Qwen/Llama/DeepSeek behind an
MCP agent — can safely query, create and modify Autodesk Revit models.

**Revit 2020 – 2026 · 78 tools · metric in, metric out · no AI vendor lock-in.**

## Install

1. Download **`AB.RevitMcp.Setup.exe`** below.
2. **Close Revit.**
3. Run it. Tick the Revit releases you want, and let it register your AI clients.

One file, no prerequisites beyond .NET Framework 4.8 (which Revit already requires). No admin
rights, no registry writes, no services. Silent deployment: `AB.RevitMcp.Setup.exe /silent`.

> **Not code-signed.** On a managed machine, Defender's Attack Surface Reduction rule *"Block
> executable files from running unless they meet a prevalence, age, or trusted list criterion"*
> will block it. `AB.RevitMcp.Setup.allowlist.txt` in the repo has the identity details to give
> IT, and `build/sign.ps1` signs the build if you have a certificate.

## What's in this release

**Modify tools** — split, trim/extend, align, offset, pin, cut geometry, phase/demolish, list phases.

**Geometry & transform** — mirror, array, group, join geometry, reference planes, ceilings, family
loading, export.

**MEP** — ducts, pipes, cable trays, conduit, MEP system listing.

**Annotation & views** — text notes, tags, detail lines, schedules, view duplication, view
templates, visibility, graphic overrides, section boxes.

### Fixed

- **`revit_split_element` destroyed hosted openings.** The split duplicated the host wall with
  `CopyElement`, which copies the wall alone — so every door and window in the discarded half lost
  its host and was silently deleted. It now copies the wall together with `Wall.FindInserts`, so
  Revit re-hosts the openings and each survives on the correct side.

## How it works

```
AI / MCP client  ──stdio/HTTP──▶  MCP server  ──named pipes──▶  Revit add-in  ──▶  Revit API
```

Named-pipe handlers run on thread-pool threads, but **the Revit API may only be touched from
Revit's main UI thread**. Every call crosses that boundary through one dispatcher: work is queued
into a lock-free `ConcurrentQueue`, an `ExternalEvent` is raised, and Revit calls back when it is
safe. Nothing else in the add-in calls the API off that thread.

## Safety

| Group | Count | Behaviour |
| --- | ---: | --- |
| Read | 24 | never opens a transaction |
| Write | 48 | one `TransactionGroup`, assimilated into a single undo step |
| Destructive | 6 | rolled back on any failure; requires `"confirm": true` |

Every write is one Revit undo step, so anything an AI does is reversible with Ctrl+Z. Most
destructive tools accept `"dryRun": true`, which reports the exact blast radius and rolls back.
Run the server with `--read-only` to refuse every write and destructive tool outright.

## Verify your download

```
CertUtil -hashfile AB.RevitMcp.Setup.exe SHA256
```

```
5920f9739bed65fd18b163ae2b5f6db7955f2e8fe35148ba9a5b55023df457d6
```

## Known limitations

- The installer is **unsigned** (see above).
- Write and destructive tools are compile-verified against Revit 2020/2024/2026 and exercised by
  hand, but there is no automated test suite running inside Revit. **Try new tools on a scratch
  model before a live one.**
- `revit_execute_code` is a deliberate escape hatch, gated like any destructive tool. Remove it
  from the catalogue or run `--read-only` if you would rather it did not exist.

---

Built by [Abdullah Lotfy](https://www.linkedin.com/in/abdullahalqublawi/). MIT licensed.
Not affiliated with Autodesk.
