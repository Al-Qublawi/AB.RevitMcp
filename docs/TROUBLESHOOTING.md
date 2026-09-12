# Troubleshooting

## First: run the Doctor

```
%LOCALAPPDATA%\ABRevitMcp\Doctor\AB.RevitMcp.Doctor.exe
```

It reproduces Revit's own checks **without starting Revit** — manifest parsing, assembly presence,
the dependency closure, the MCP server, and a live ping if the bridge is running. It names the
problem and the fix. Start here before reading anything below.

## "Failed to initialize the add-in … because the assembly … does not exist"

**Revit is usually lying.** This dialog appears whenever the CLR cannot load the add-in, and Revit
reports it by naming the top-level DLL — which is very often sitting right there on disk. Confirm
that first:

```powershell
Test-Path "$env:LOCALAPPDATA\ABRevitMcp\bin\Revit2024\AB.RevitMcp.Addin.dll"
```

If that prints `True`, the file is fine and the real fault is a **dependency that cannot be
resolved**. In order of likelihood:

### 1. The `netstandard` facade (the big one, Revit 2020–2024)

A `netstandard2.0` assembly carries a reference to `netstandard, Version=2.0.0.0`. Revit 2020–2024
host .NET Framework with a `Revit.exe.config` that declares .NET 4.7 and has **no binding redirect
for that facade**, so the load fails and Revit blames the add-in DLL.

This is why `AB.RevitMcp.Contracts` multi-targets `net48;netstandard2.0;net8.0-windows` — the
add-in for Revit 2020–2024 must get a genuine `net48` build. Check it:

```powershell
$a = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom(
        "$env:LOCALAPPDATA\ABRevitMcp\bin\Revit2024\AB.RevitMcp.Contracts.dll")
$a.GetReferencedAssemblies() | Select-Object Name
```

`mscorlib` only = correct. If you see `netstandard`, rebuild — the Doctor flags this automatically.

### 2. Wrong runtime for the release

Revit 2020–2024 need `net48`; Revit 2025+ need `net8.0-windows`. A `net48` DLL in a Revit 2026
folder (or the reverse) produces the same dialog. The Doctor checks this too.

### 3. Endpoint protection blocking `%LOCALAPPDATA%`

Bitdefender, CrowdStrike, Cortex XDR and similar products commonly block DLL loads out of Local
AppData. Revit's `File.Exists` check then fails and you get the same misleading dialog.

Tell-tale sign: **every other add-in that loads successfully lives under `%APPDATA%`** and yours is
the only one under `%LOCALAPPDATA%`. Compare them:

```powershell
Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins4\*.addin" | ForEach-Object {
    $asm = ([xml](Get-Content $_.FullName)).RevitAddIns.AddIn.Assembly
    '{0,-32} {1}' -f $_.Name, $asm
}
```

The installer places the add-in beside its manifest under
`%APPDATA%\Autodesk\Revit\Addins\<version>\ABRevitMcp\` for exactly this reason. If you moved
it, move it back — the Doctor warns when the add-in is loading from Local AppData.

### 4. Blocked files

Revit refuses assemblies marked as downloaded from the internet:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\ABRevitMcp" -Recurse | Unblock-File
```

### 5. Missing sibling assemblies

`AB.RevitMcp.Addin.dll`, `AB.RevitMcp.Contracts.dll` and `AB.RevitMcp.Ipc.dll` must all be in the
same folder. Re-run the installer if any is missing.

> **Do not diagnose this by loading `RevitAPIUI.dll` outside Revit.** It drags in Autodesk native
> resource DLLs that only resolve inside `Revit.exe`, and Windows will pop modal
> `AnavRes.dll not found` / `adui23res.dll not found` dialogs. The Doctor reads assembly metadata
> instead and never loads a Revit binary.

## The client says `spawn EPERM`, or Defender reports "Risky action blocked"

The MCP server is not being allowed to **start**. `spawn EPERM` is the client reporting that
Windows refused to create the process; the client config is usually perfect.

The cause is Microsoft Defender's Attack Surface Reduction rule

```
Block executable files from running unless they meet a prevalence, age, or trusted list criteria
ASR rule GUID: 01443614-cd74-433a-b99e-2ecdc07bfc25
```

This is a **policy rule, not a malware detection**. A freshly built, unsigned executable fails all
three tests by definition, and on a managed device the end user cannot lift it.

**The fix, and what the installer now does automatically.** The server publish ships the managed
`AB.RevitMcp.Server.dll` beside its apphost `.exe`, so the identical program can be started
through `dotnet.exe` instead. The process Windows is asked to create is then `dotnet.exe` -
Microsoft-signed, about as prevalent as software gets - and the rule has nothing to act on. The
`.dll` is *loaded*, not executed as a process. Nothing is disabled or bypassed: same code, same
user, same permissions.

Setup detects this and registers your clients in that form when .NET is present. To fix an
existing install by hand, change the entry from:

```json
{ "command": "C:\Users\you\AppData\Local\ABRevitMcp\Server\AB.RevitMcp.Server.exe",
  "args": [] }
```

to:

```json
{ "command": "C:\Program Files\dotnet\dotnet.exe",
  "args": ["C:\Users\you\AppData\Local\ABRevitMcp\Server\AB.RevitMcp.Server.dll"] }
```

then restart the client **completely** (Claude Desktop: quit from the tray icon, not just the
window). Confirm .NET is present with `dotnet --info`.

If the *installer itself* is blocked, that is the same rule applied to `AB.RevitMcp.Setup.exe`.
Give your IT administrator `dist\AB.RevitMcp.Setup.allowlist.txt`, which carries the publisher,
version and SHA256 needed to allowlist it. The durable fix is code signing - see
[DEPLOYMENT.md](DEPLOYMENT.md).

## The ribbon tab does not appear

1. Did you **restart Revit** after installing? Add-ins load only at startup.
2. Check the manifest exists: `%APPDATA%\Autodesk\Revit\Addins\<version>\AB.RevitMcp.addin`
3. Run the Doctor.
4. Search the journal for the real error:
   ```powershell
   Select-String -Path "$env:LOCALAPPDATA\Autodesk\Revit\Autodesk Revit 2024\Journals\journal.*.txt" `
                 -Pattern 'AB.RevitMcp' | Select-Object -Last 5
   ```

## "No running Revit session is exposing the MCP bridge"

The server found no live endpoint. In order:

- Is Revit open with a **project** (not a family) loaded?
- Did you press **Start Bridge** on the AB MCP AI tab? The icon must be amber or green, not grey.
- Check the discovery folder: `%LOCALAPPDATA%\ABRevitMcp\endpoints\` should contain a
  `revit-<pid>.json` file.
- If a stale file is present for a Revit that crashed, the server deletes it automatically on the
  next attempt — just retry.

You do **not** need to restart your AI client. The server connects on demand.

## "Access to the Revit bridge pipe was denied"

Revit and the MCP server must run as the **same Windows user**. This most often happens when Revit
was started with *Run as administrator* and the AI client was not (or vice versa). Run both at the
same elevation level.

## Tools time out

The default budget is 30 seconds per request. Tools that are inherently long-running declare
their own instead - `revit_export` gets 5 minutes, because exporting a sheet set is minutes of
Revit's own work and there is no page size to shrink.

- **Revit is showing a modal dialog.** Nothing can execute until a human dismisses it. This is the
  most common cause by far.
- **The query is too large.** Use `limit` and `offset`, and narrow with `category` / `levelName`
  rather than scanning the whole model.
- **The operation is genuinely slow** (purge on a 500 MB model, a big copy array). Raise the
  budget:
  ```json
  { "mcpServers": { "revit": { "command": "...", "args": ["--timeout", "60000"] } } }
  ```

Check `revit_bridge_status` — `queueDepth`, `timedOut` and `averageExecutionMs` tell you whether
Revit is saturated or simply blocked.

## A destructive tool refuses to run

By design. Pass `"confirm": true`, and only after a human has approved that specific change. Run it
with `"dryRun": true` first to see exactly what would be affected — including cascade deletions.

If the server was started with `--read-only` or `AB_REVITMCP_READONLY=1`, every write and
destructive tool is refused regardless of `confirm`. Check the tool description: read-only mode
prefixes it with `[DISABLED - this server runs in read-only mode]`.

## "The active document is read-only"

Write tools cannot run against a linked model, a worksharing-detached preview, or a file opened
read-only. Open the model normally.

## A parameter write is reported as "skipped"

The response says why per element. The usual reasons:

- **read-only or calculated** — Revit computes it (area, volume, most `HOST_` parameters);
- **the parameter does not exist on that element** — check with `revit_get_element_parameters`,
  and remember instance and type parameters are different (use `applyToType: true` for the latter);
- **owned by another user** — on a workshared model, synchronise and retry.

A partial success is deliberate: 900 of 1000 elements updated with 100 explained skips is more
useful than an all-or-nothing failure.

## Element names / levels are "not found" but they look right

Matching is case-insensitive but otherwise exact — a trailing space or a non-breaking hyphen will
miss. The error carries a `didYouMean` list; use it. To discover exact names:

- `revit_list_levels`
- `revit_list_categories`
- `revit_list_family_types` (with `category` to narrow)

## Several Revit versions are open and the wrong one is used

The server picks the most recently started session. Pin one:

```json
{ "mcpServers": { "revit": {
    "command": "...",
    "args": ["--revit-version", "2024"]
} } }
```

or set `AB_REVITMCP_REVIT_VERSION=2024` in the client's `env` block.

## The MCP client shows a JSON parse error on connect

Something wrote to **stdout** that was not protocol. The server itself never does — every
diagnostic goes to stderr — so check that no wrapper script (`echo`, a batch file banner, a
PowerShell profile) is prepending output. Run the server directly rather than through a wrapper.

## Verify the plumbing without Revit

```powershell
.\artifacts\Release\MockBridge\AB.RevitMcp.MockBridge.exe
```

The mock publishes a real endpoint on a real pipe and answers `revit_get_model_info`,
`revit_list_levels` and `revit_bridge_status` with canned data. If the mock works and Revit does
not, the problem is the add-in; if neither works, it is the client configuration.

## Where to look next

Structured logs, one JSON object per line:

```
%LOCALAPPDATA%\ABRevitMcp\logs\bridge-YYYYMMDD.ndjson
```

```powershell
# every failed request today
Get-Content "$env:LOCALAPPDATA\ABRevitMcp\logs\bridge-$(Get-Date -f yyyyMMdd).ndjson" |
    ConvertFrom-Json | Where-Object { $_.ok -eq $false } |
    Select-Object ts, tool, errorCode, errorMessage
```

```powershell
# slowest tools
Get-Content "$env:LOCALAPPDATA\ABRevitMcp\logs\bridge-$(Get-Date -f yyyyMMdd).ndjson" |
    ConvertFrom-Json | Sort-Object durationMs -Descending |
    Select-Object -First 10 tool, durationMs
```

Add `--verbose` to the server arguments to log every JSON-RPC method to stderr; your MCP client
usually surfaces that in its own log.
