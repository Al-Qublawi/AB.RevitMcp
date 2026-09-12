Universal MCP server + Revit add-in. Any MCP-compatible AI client — Claude Desktop, Claude Code,
Cursor, VS Code, Windsurf, Cline, LM Studio, OpenClaw, or a local Qwen/Llama/DeepSeek behind an
MCP agent — can safely query, create and modify Autodesk Revit models.

**Revit 2020 – 2026 · 78 tools · metric in, metric out · no AI vendor lock-in.**

## What's new in 1.3.0

### `spawn EPERM` is fixed — the server now starts on managed devices

If your AI client reported `spawn EPERM`, or Defender showed **"Risky action blocked"**, the
server was never being allowed to start. That is Defender's Attack Surface Reduction rule:

```
Block executable files from running unless they meet a prevalence, age, or trusted list criteria
```

A policy rule, not a malware detection — and a freshly built, unsigned executable fails all three
tests by definition.

**Setup now works around it.** The publish already ships the managed `AB.RevitMcp.Server.dll`
beside its apphost `.exe`, so the identical program can start through `dotnet.exe`. The process
Windows is asked to create is then `dotnet.exe` — Microsoft-signed, about as prevalent as software
gets — and the rule has nothing to act on. The `.dll` is *loaded*, not executed as a process.
Nothing is disabled or bypassed: same code, same user, same permissions.

Setup only takes that route after **proving** it works on your machine, and falls back to the
direct executable when .NET is absent. Already installed? See
[TROUBLESHOOTING.md](https://github.com/Al-Qublawi/AB.RevitMcp/blob/main/docs/TROUBLESHOOTING.md)
for the two-line config edit.

### Revit 2026 update 26.5 no longer breaks the build

Autodesk's 26.5 update is built against .NET 10, which made the add-in unbuildable on an updated
machine. The add-in now compiles against pinned Revit API reference packages instead of whatever
Revit is installed, so the build is reproducible and **Revit is no longer required to build it**.

Your installed add-in was never affected — verified against a live 26.5.0.55 session.

### Also fixed

- `--print-config` emitted a config that starts nothing when the server ran under `dotnet.exe`.
- The IT allowlist file had its version hardcoded to `1.1.0` and misreported every release since.

## Install

1. Download **`AB.RevitMcp.Setup.exe`** below.
2. **Close Revit** — and any AI client, so the running MCP server releases its lock.
3. Run it. Tick the Revit releases you want, and let it register your AI clients.

One file, no prerequisites beyond .NET Framework 4.8 (which Revit already requires). No admin
rights, no registry writes, no services. Silent deployment: `AB.RevitMcp.Setup.exe /silent`.

> **Not code-signed.** The installer itself can still be blocked by the same ASR rule.
> `AB.RevitMcp.Setup.allowlist.txt` in the repo has the publisher, version and SHA256 to give IT,
> and `build/sign.ps1` signs the build if you have a certificate.

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
bfabbcb1824b568e21c7c592af151cbbe8c307a14c53a26a4a1b38507ac9192d
```

## Known limitations

- The installer is **unsigned** (see above).
- Write and destructive tools are compile-verified across Revit 2020/2024/2026 and exercised by
  hand, but there is no automated test suite running inside Revit. **Try new tools on a scratch
  model before a live one.**
- `revit_purge_unused` and `revit_get_model_health` use the 30-second default budget. Both can run
  long on a very large model.
- `revit_execute_code` is a deliberate escape hatch, gated like any destructive tool. Remove it
  from the catalogue or run `--read-only` if you would rather it did not exist.

---

Built by [Abdullah Lotfy](https://www.linkedin.com/in/abdullahalqublawi/). MIT licensed.
Not affiliated with Autodesk.
