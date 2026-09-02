# Architecture

## 1. The threading problem, and the one solution

The Revit API is **single-threaded and context-bound**. Calling it from anywhere other than
Revit's main UI thread, or outside a valid API context, produces anything from a silent no-op to
an access violation that takes the whole application down with the user's unsaved model.

A named-pipe server, by contrast, is inherently multi-threaded: `WaitForConnectionAsync`,
`ReadAsync` and every continuation land on thread-pool threads.

`Bridge/RevitDispatcher.cs` is the only bridge between the two worlds:

```
 thread-pool thread                              Revit UI thread
 ──────────────────                              ───────────────
 PipeServer.ServeConnectionAsync
   └─ BridgeService.HandleRequestAsync
        └─ dispatcher.EnqueueAsync(work) ──┐
             await TaskCompletionSource     │  ConcurrentQueue<WorkItem>
                                            │
             ExternalEvent.Raise() ─────────┼──► Revit schedules the handler
                                            │
                                            └──► RevitDispatcher.Execute(UIApplication)
                                                   ├─ dequeue
                                                   ├─ ToolRouter.Execute(...)   ← all Revit API calls
                                                   └─ TaskCompletionSource.TrySetResult
             ◄──────────────────────────────────────┘  (continuation resumes on the pool)
```

Three details make this correct rather than merely plausible:

**`TaskCreationOptions.RunContinuationsAsynchronously`** on every `TaskCompletionSource`. Without
it, `TrySetResult` would run the awaiting pipe-handler continuation *inline on Revit's UI thread*,
which can deadlock the application. This is not a micro-optimisation; it is a correctness
requirement.

**UI slicing.** `Execute` drains the queue but yields back to Revit after 1.5 s of continuous work,
re-raising the `ExternalEvent` to be called again. A burst of requests therefore cannot freeze
Revit's interface.

**Timeouts protect the caller, not Revit.** On timeout the work item is marked abandoned; if it has
not started it is skipped when dequeued, and the caller receives a structured `TIMEOUT`. Work
already executing is *allowed to finish* — aborting Revit's UI thread mid-transaction would corrupt
the document. This is a deliberate, documented asymmetry.

## 2. Wire protocol

Length-prefixed frames over a duplex named pipe:

```
┌────────────┬──────────────────────────────┐
│ 4 bytes LE │ N bytes UTF-8 JSON           │
│ length = N │                              │
└────────────┴──────────────────────────────┘
```

Length prefixing rather than newline delimiting keeps the reader O(1) and makes an embedded newline
in a Revit element name a non-issue. Frames are capped at 8 MB; a corrupt prefix is detected
immediately rather than blocking forever on a bogus read.

**Request**

```json
{ "v": 1, "id": "3f2a...", "tool": "revit_query_elements",
  "args": { "category": "Walls", "limit": 50 },
  "timeoutMs": 15000, "client": "claude-desktop" }
```

**Response**

```json
{ "v": 1, "id": "3f2a...", "ok": true, "durationMs": 42.7, "result": { ... } }
{ "v": 1, "id": "3f2a...", "ok": false, "durationMs": 3.1,
  "error": { "code": "NOT_FOUND", "message": "Level 'Levl 01' was not found...",
             "details": { "didYouMean": ["Level 01", "Level 02"] } } }
```

Three tool names are reserved and answered by the bridge itself without entering the Revit API, so
they still work while Revit is busy: `bridge/ping`, `bridge/describe`, `bridge/status`.

### Discovery

Each running bridge writes a descriptor to
`%LOCALAPPDATA%\ABRevitMcp\endpoints\revit-<pid>.json` containing its pipe name, process id, Revit
version and document title. The MCP server reads that folder, **verifies each process is still
alive** (deleting descriptors left behind by a crashed Revit), and connects to the most recently
started session. `--revit-version` or `--pipe` pins a specific one when several are open.

### Pipe security

The pipe is created with an ACL granting `ReadWrite | CreateNewInstance` to the current user SID
only (`net48`), or with `PipeOptions.CurrentUserOnly` (`net8.0-windows`). The **client** does not
set `CurrentUserOnly`: when Revit runs elevated the pipe's owner can be the Administrators group
rather than the user SID, and that flag would then reject a legitimate connection. Access remains
enforced by the ACL the server applies.

## 3. Transaction policy

Applied centrally in `Bridge/ToolRouter.cs`, not left to individual handlers:

| Category | Behaviour |
| --- | --- |
| Read | Handler runs directly. No transaction is ever opened. |
| Write | `TransactionGroup` → handler → `Assimilate()`. One undo step named `MCP: <tool>`. |
| Destructive | Same, plus a mandatory `confirm: true` gate checked **before** the handler runs. |

Any exception rolls the group back in full. A handler may call `ctx.RequestRollback()` — that is
how `dryRun` works: the operation genuinely executes, so the reported blast radius is exact, and
then the model is restored.

Inside the group, `ToolContext.InTransaction` opens each `Transaction` with a
`SilentFailureHandler` (`IFailuresPreprocessor`) that acknowledges warnings and returns
`ProceedWithRollBack` on errors. Revit therefore never raises a modal dialog during an AI-driven
session, and the captured failure text is surfaced in the tool response instead of being lost.

Two tools are deliberately excluded from transaction wrapping:

- `revit_set_selection` — touches UI state only; a transaction would pointlessly fail on a
  read-only model.
- `revit_unload_links` — `RevitLinkType.Unload` manages its own document state and the Revit API
  refuses to run it under transaction control.

Both still pass through full validation and the confirmation policy.

## 4. Version compatibility

Every Revit API breaking change between 2020 and 2026 is isolated in two files.

`Revit/Compat.cs`:

- `ElementId.IntegerValue` (int) → `ElementId.Value` (long) in 2024
- `(BuiltInCategory)category.Id.IntegerValue` → `Category.BuiltInCategory` in 2023
- `doc.Create.NewFloor(CurveArray, ...)` → `Floor.Create(doc, IList<CurveLoop>, ...)` in 2022
- purge via the `PerformanceAdviser` rule GUID → `Document.GetUnusedElements` in 2024

`Revit/Metric.cs`:

- `DisplayUnitType` → `ForgeTypeId` / `UnitTypeId` in 2021
- `Definition.ParameterType` → `GetSpecTypeId()` (2021) → `GetDataType()` (2022)
- non-measurable specs (`SpecTypeId.Boolean.YesNo` and friends) only exist from 2022

`ForgeTypeId` values are compared by their `TypeId` **string**: equality semantics have shifted
between releases, the identifier has not.

The conversion factors themselves are applied directly rather than through `UnitUtils`, because
1 ft = 0.3048 m is exact by definition — that is both faster and immune to the 2021 API break.

## 5. Why no vendor SDK, and no JSON library

`AB.RevitMcp.Contracts` has **zero package references**, including the JSON stack, which is
hand-written (`Json/JsonValue.cs`, `JsonParser.cs`, `JsonWriter.cs`).

This is not invented-here syndrome. Anything shipped into `Revit.exe` shares one assembly load
context with Revit's own dependencies. `Newtonsoft.Json`, `System.Text.Json`, `System.Memory` and
`System.Buffers` are all present in some Revit releases at versions that differ across 2020–2026,
and a binding conflict there surfaces as a `TypeLoadException` at the worst possible moment. A
self-contained ~600-line JSON implementation removes that entire class of failure, and gives both
sides of the pipe byte-identical serialisation semantics.

The same reasoning applies to the MCP layer: JSON-RPC 2.0 over stdio is a few hundred lines, and
hand-rolling it keeps the server genuinely decoupled from any AI provider's SDK release cadence.

Integers keep their textual form through the DOM, so 64-bit element ids survive a round trip
exactly rather than degrading through a `double`.

## 6. The single source of truth

`Contracts/Tools/ToolCatalog.cs` holds the name, description, risk category and JSON Schema of all
46 tools. Both processes read it:

- the **server** answers `tools/list` from it — so tools are advertised even while Revit is closed;
- the **add-in** binds handlers to it — `ToolRouter.Register` throws at startup if a handler names
  a tool that is not in the catalogue, and `revit_bridge_status` reports every catalogue entry that
  has no handler.

A schema and its implementation therefore cannot silently drift apart, and `--print-tools`
generates the documentation from the same descriptors.
