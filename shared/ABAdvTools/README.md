# AB Adv Tools kit

The shared foundation of every AB add-in for Autodesk Revit and Navisworks:

- **One ribbon tab, `AB Adv Tools`**, in each host, whatever combination of AB add-ins is installed.
- **One shared panel** at the end of that tab: **About** (every AB tool loaded, with versions),
  **Check for Updates**, and **LinkedIn**.
- **Release notifications** from each add-in's own GitHub repository: checked in the background at
  startup, at most once a day, announced once per new version, switchable off in About.
- **One installer engine** behind every add-in's `Setup.exe`: finds earlier versions (old MSIs,
  copied files, earlier AB installs), offers to remove them, installs, registers in Apps and
  Features, supports silent deployment, and elevates only when a choice needs it.

Kit version: see [`VERSION`](VERSION).

---

## How it is shared

The canonical copy lives in `AB.AdvTools.Kit`. Every add-in repository carries an identical copy
under **`shared/ABAdvTools/`**, so each repo still builds from a fresh clone with nothing else
checked out. `tools/sync-kit.ps1` keeps the copies identical:

```powershell
.\tools\sync-kit.ps1 -All          # copy the kit into every AB add-in repository
.\tools\sync-kit.ps1 -All -Check   # report drift only; exit 1 if anything differs
```

**Edit the kit here, never the vendored copy**, then sync.

The kit is **compiled into** each add-in rather than shipped as a shared DLL. Two add-ins built on
different kit versions can then load side by side in one Revit or Navisworks process without an
assembly-version conflict. Everything is `internal` except the three Revit command classes Revit
must instantiate by name. Code is C# 7.3 and runs on .NET Framework 4.8 and .NET 8.

| Folder | What | Used by |
|---|---|---|
| `src/Common` | Brand constants, product identity, cross-add-in registry, settings, log, GitHub update checker, About dialog | every add-in and installer |
| `src/Revit` | Shared tab and panel, update notice, the three shared commands | Revit add-ins |
| `src/Navisworks` | Tab merger, shared command handling, update notice | Navisworks plugins |
| `src/Setup` | The installer engine | every `Setup.exe` |
| `build/*.targets` | One `<Import>` wires a project to the kit | project files |
| `assets` | Shared panel icons (PNG for Revit, ICO for Navisworks) and the About logo | build |
| `templates` | The Navisworks shared-panel XAML block | plugin ribbon XAML |
| `tools` | `sync-kit.ps1`, `publish-release.ps1`, `make-assets.ps1`, `make-setup-icon.ps1` | you |
| `tests/KitTests` | Self-test (`dotnet run --project tests\KitTests`) and off-screen UI renders (`--render <folder>`) | you |

---

## How the shared tab works

### Revit

Revit allows several add-ins to add panels to one custom tab. Each add-in calls
`RevitAdvTools.GetToolPanel(application, "<unique panel name>")` in `OnStartup`. The shared panel is
built once, by whichever AB add-in handles `ApplicationInitialized` first. That event fires after
every add-in's `OnStartup`, so the shared panel always comes last.

### Navisworks

Navisworks builds **one tab per plugin** from each plugin's ribbon XAML and has no notion of sharing
a tab. So every AB plugin:

1. declares its tab as `<RibbonTab Id="ID_ABAdvTools" Title="AB Adv Tools">`, and
2. carries a copy of the shared panel (`RibbonPanelSource Id="ABADV_SharedPanel"`).

At startup, `NavisworksRibbonMerger` (run by whichever AB plugin loads first) moves the panels of
every `…ID_ABAdvTools` tab into one tab, removes the emptied tabs, keeps a single shared panel and
puts it last. It re-runs whenever Navisworks rebuilds its ribbon.

This rests on how Navisworks' own ribbon code behaves, checked in `navisworks.gui.roamer` 2026 and 2027:

- Tab and button ids are prefixed with the plugin id at load (`NWRibbonControl.SetPluginPrefix`).
- A button executes through `CommandManager.FindCommand(<its own id>)` (`NWRibbonButton`), so moving
  its panel to another tab changes nothing about what it does.
- A plugin tab's visibility comes from `CanExecuteRibbonTab`, not from its panel count, so an emptied
  tab has to be taken off the ribbon.

The merge uses reflection against AdWindows. If a future release changes those internals, the merge
does nothing, and each AB plugin still shows on its own tab titled "AB Adv Tools", fully working.
The kit log (`%LOCALAPPDATA%\AB Adv Tools\Logs`) records the merged layout.

---

## Release notifications

Each add-in checks **its own repository**:
`GET https://api.github.com/repos/Al-Qublawi/<repo>/releases/latest`, anonymous, with a 10-second timeout.

- Automatic check: background thread at startup, at most once per 24 h per add-in.
- Notice: once per new version. Revit shows it when a view activates, Navisworks once the window
  has settled. Clicking through opens the release page.
- **Check for Updates** on the ribbon checks every loaded AB tool immediately.
- Off switch: the checkbox in About (`%LOCALAPPDATA%\AB Adv Tools\settings.ini`). An add-in can add
  its own gate via `AdvToolsProduct.AutomaticChecksAllowed`; SwitchBack does, for its Settings option.
- **The repository must be public.** GitHub answers a private repository's releases API with 404 to
  anonymous callers, and the add-in then reports "No release information", silently.

Publishing a release: tag `v<version>` on the add-in's repository with the installer attached.
`tools/publish-release.ps1` does exactly that:

```powershell
.\tools\publish-release.ps1 -Repository ..\ABClashApprover -Asset ..\ABClashApprover\deploy\AB.ClashApprover.Setup.exe -NotesFile notes.md
```

---

## The installer engine

A product's installer project is a `SetupProduct` subclass plus three lines of `Main`:

```csharp
[STAThread]
static int Main(string[] args) { return SetupProgram.Run(new MyToolSetup(), args); }
```

Its build script zips one payload per release into `payload\` (`Revit2026.zip`,
`Navisworks2027.zip`, …) and builds the project, which embeds every zip found.

The engine provides:

- **Earlier versions.** A page lists every copy found, ticked for removal. Unticking asks for confirmation,
  because two copies load the add-in twice. Earlier MSIs are found by UpgradeCode and removed with
  msiexec. Copied files, and installs made by this engine (found through their Apps and Features
  entry `ABAdvTools.<Id>`), are removed directly.
- **Elevation only when needed.** Setup starts without a prompt. An all-users install, removing an
  all-users copy, or writing into a Program Files plugin folder restarts it elevated with the same
  choices, and the user's own folders are carried over.
- **Apps and Features.** Setup copies itself beside the entry, so Uninstall works after the download is gone.
- **Command line**, identical for every AB installer:

  | | |
  |---|---|
  | `/silent` | install every supported release, removing earlier versions |
  | `/allusers`, `/currentuser` | choose the scope |
  | `/targets:Revit2024,Revit2026` | only these releases |
  | `/keepold` | leave earlier versions in place (not recommended) |
  | `/uninstall [/silent]` | remove |
  | `/log:<file>` | also log to a file |
  | `/scan` | report releases and earlier versions found; change nothing |
  | `/sandbox:<folder>` | redirect every file and registry write into a folder (testing) |
  | `/interactive` | with `/uninstall`, always show the window (Apps and Features uses this) |
  | `/render:<folder>` | save a picture of every setup page, change nothing (layout QA) |

  Exit codes: `0` ok, `1` failed, `2` Revit/Navisworks running, `3` nothing to install,
  `740` needs an elevated prompt.

Helpers for the common layouts: `NavisworksBundle` (ApplicationPlugins bundle with a generated
`PackageContents.xml`) and `RevitAddin` (manifest plus a binaries folder per year).

### Never downgrade an add-in's installer

Moving an add-in onto the engine must keep everything its earlier installer did. The engine has a
hook for each of these; use it rather than dropping the behaviour:

| Earlier installer did… | Hook |
|---|---|
| extra options on its window (e.g. AI clients, custom clients) | `CreateOptionsControl(width)` + `CaptureOptions()`. Build the control at the width given; resizing an anchored panel afterwards pushes its buttons off screen. |
| a button usable without installing (e.g. Verify) | `ToolActions` |
| a button after installing | `FinishActions` |
| a read-me / licence page (MSI `WixUI_Minimal`) | `<ABAdvToolsLicense>License.rtf</ABAdvToolsLicense>` in the setup project |
| installed for releases not installed yet (MSI shipping every build) | `AddReleasesNotInstalled(...)` in `FindTargets` |
| let you carry on with the host open, skipping locked releases | `AllowInstallWhileHostRunning` + `RevitAddin.Install(..., skipLocked: true)` + `ctx.Problem(...)` |
| removed silently on a bare `/uninstall` | `UninstallSwitchIsSilent` |
| wrote its silent log under a known name | `LogFilePrefix` |
| said something after uninstalling | `AfterUninstall` |

Check the result with `/render:<folder>` and compare every page with the old installer.

### Never downgrade an add-in's About

An add-in's own About dialog is replaced by the shared one, so carry its content across:
`AdvToolsProduct.Details` (live text, e.g. tool count or host version), `AddAction(text, run)` (e.g.
"Open the log folder"), and `UpdateSettingsHint` when the add-in has its own switch for release checks.

---

## Adding a new AB add-in

### Revit add-in

1. Add the repo name to `$knownRepositories` in `tools/sync-kit.ps1`, then run `.\tools\sync-kit.ps1 -All`.
2. In the add-in `.csproj`: `<Import Project="..\..\shared\ABAdvTools\build\ABAdvTools.Revit.targets" />`
3. In `OnStartup`:

   ```csharp
   RevitAdvTools.Initialize(application, new AdvToolsProduct(
       "MyTool", "AB My Tool", "One-line tagline", AdvToolsHost.Revit,
       "AB.MyTool.Revit", typeof(App).Assembly));

   RibbonPanel panel = RevitAdvTools.GetToolPanel(application, "My Tool");
   // add your buttons to panel - no About or LinkedIn buttons, the shared panel has them
   ```

   and `RevitAdvTools.Shutdown(application);` in `OnShutdown`.

### Navisworks plugin

1. Add the repo to `sync-kit.ps1` and sync, as above.
2. In the plugin `.csproj`:

   ```xml
   <PropertyGroup>
     <ABAdvToolsRibbonXaml>$(MSBuildProjectDirectory)\MyTool.xaml</ABAdvToolsRibbonXaml>
   </PropertyGroup>
   <Import Project="..\..\shared\ABAdvTools\build\ABAdvTools.Navisworks.targets" />
   ```

   The build fails if the XAML loses the shared tab id or the shared panel.
3. In the ribbon XAML, use `<RibbonTab Id="ID_ABAdvTools" Title="AB Adv Tools" KeyTip="AB">`, then your
   panels, then an empty marker pair that `sync-kit.ps1` fills with the shared panel:

   ```xml
   <!-- BEGIN AB ADV TOOLS SHARED PANEL -->
   <!-- END AB ADV TOOLS SHARED PANEL -->
   ```
4. On the `CommandHandlerPlugin`: `[RibbonTab(NavisworksAdvTools.RibbonTabId, DisplayName = NavisworksAdvTools.RibbonTabTitle, LoadForCanExecute = true)]`,
   the three shared `[Command(NavisworksAdvTools.AboutCommandId …)]` attributes (copy them from any AB
   plugin), and first thing in `ExecuteCommand`:

   ```csharp
   if (NavisworksAdvTools.TryExecuteSharedCommand(commandId)) return 0;
   ```
5. Add an `EventWatcherPlugin` (the only plugin kind Navisworks loads at startup):

   ```csharp
   [Plugin("MyTool.Startup", "ABxx")]
   public sealed class MyToolStartup : EventWatcherPlugin
   {
       public override void OnLoaded() { NavisworksAdvTools.Start(new AdvToolsProduct(
           "MyTool", "AB My Tool", "One-line tagline", AdvToolsHost.Navisworks,
           "AB.MyTool.Navisworks", typeof(MyToolStartup).Assembly)); }
       public override void OnUnloading() { NavisworksAdvTools.Stop(); }
   }
   ```

### Installer

1. `installer\MyTool.Setup\MyTool.Setup.csproj`: a net48 WinExe with
   `<ABAdvToolsProductLogo>` and `<Import Project="…\shared\ABAdvTools\build\ABAdvTools.Setup.targets" />`.
2. `MyToolSetup : SetupProduct`: copy the closest existing one (Clash Approver for a Navisworks bundle,
   SwitchBack for Revit plus a Navisworks plugin folder, MCP Bridge for per-user with options).
   Give it a new `Id`. If an earlier release shipped as an MSI, add its UpgradeCode.
3. A build script that zips one payload per release into `payload\`, builds the setup with
   `-p:Version=<add-in version>` and `--no-incremental`, and checks the payload count.
4. Test without touching the machine:

   ```powershell
   MyTool.Setup.exe /scan
   MyTool.Setup.exe /silent /sandbox:C:\Temp\sb /log:C:\Temp\sb.log
   ```

### Release

Make the repository **public**, then tag `v<version>` with the Setup.exe attached (`tools/publish-release.ps1`).
