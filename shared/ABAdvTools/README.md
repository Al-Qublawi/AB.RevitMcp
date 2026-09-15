# AB Adv Tools kit

The shared foundation of every AB add-in for Autodesk Revit and Navisworks:

- **One ribbon tab, `AB Adv Tools`**, in each host, whatever combination of AB add-ins is installed.
- **One shared panel** at the end of that tab: **About** (every AB tool loaded, with versions),
  **Check for Updates**, and **LinkedIn**.
- **Release notifications** from each add-in's own GitHub repository: checked in the background at
  startup, at most once a day, announced once per new version, switchable off in About.
- **One `.msi` builder** behind every add-in's installer: a code-free Windows Installer package from
  a short definition file — Everyone / Only me, a tick per Autodesk release, upgrades, removal of
  copies left by the retired Setup.exe, silent deployment.

Kit version: see [`VERSION`](VERSION). 2.0.0 replaced the Setup.exe engine (1.x) with the `.msi`
builder.

---

## How it is shared

The canonical copy lives in `AB.AdvTools.Kit`. Every add-in repository carries an identical copy
under **`shared/ABAdvTools/`**, so each repo still builds from a fresh clone with nothing else
checked out. `tools/sync-kit.ps1` keeps the copies identical (line endings ignored):

```powershell
.\tools\sync-kit.ps1 -All          # copy the kit into every AB add-in repository
.\tools\sync-kit.ps1 -All -Check   # report drift only; exit 1 if anything differs
```

**Edit the kit here, never the vendored copy**, then sync.

The code part of the kit is **compiled into** each add-in rather than shipped as a shared DLL. Two
add-ins built on different kit versions can then load side by side in one Revit or Navisworks process
without an assembly-version conflict. Everything is `internal` except the three Revit command classes
Revit must instantiate by name. Code is C# 7.3 and runs on .NET Framework 4.8 and .NET 8.

| Folder | What | Used by |
|---|---|---|
| `src/Common` | Brand constants, product identity, cross-add-in registry, settings, log, GitHub update checker, About dialog | every add-in |
| `src/Revit` | Shared tab and panel, update notice, the three shared commands | Revit add-ins |
| `src/Navisworks` | Tab merger, shared command handling, update notice | Navisworks plugins |
| `build/*.targets` | One `<Import>` wires an add-in project to the kit | project files |
| `msi` | The `.msi` builder, its shared pages, the dry-run tester and the page capturer | installer build scripts |
| `assets` | Shared panel icons (PNG for Revit, ICO for Navisworks), the About logo, the installer artwork | build |
| `templates` | The Navisworks shared-panel XAML block | plugin ribbon XAML |
| `tools` | `sync-kit.ps1`, `publish-release.ps1`, `make-assets.ps1`, `make-msi-art.ps1`, `make-product-icon.ps1` | you |
| `tests/KitTests` | Self-test (`dotnet run --project tests\KitTests`) and off-screen About renders (`--render <folder>`) | you |

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

---

## Installers: code-free `.msi` packages

### Why an .msi, and why no code inside it

Company PCs commonly enforce Microsoft Defender's attack surface reduction rule *"Block executable
files from running unless they meet a prevalence, age, or trusted list criteria"*
(`01443614-cd74-433a-b99e-2ecdc07bfc25`). An unsigned, freshly built `Setup.exe` fails all three
tests by definition — kit 1.x's Setup.exe installers were blocked exactly that way. Windows Installer
(`msiexec.exe`) is trusted, so a package **made only of tables** installs.

So the rule: **nothing inside an AB .msi runs code.** No DLL, EXE, script or JScript custom action —
not even behind a button. `New-AdvToolsMsi.ps1` fails the build if the package holds any custom
action other than *set a property* (51), *set a folder* (35) or *show an error* (19), or any binary
stream other than the WixUI artwork. Anything that needs code belongs in the add-in, which runs
inside Revit or Navisworks — the MCP Bridge's AI-client setup is the example: the installer page
records the choice, the add-in applies it at first start.

### Building one

An add-in repository supplies `installer\<Product>.msi.psd1` and a build script that stages one
folder per installable unit, then calls:

```powershell
& shared\ABAdvTools\msi\New-AdvToolsMsi.ps1 -Definition installer\MyTool.msi.psd1 `
    -Payload obj\msi\payload -Version 1.2.3 -OutputDirectory dist -WorkDirectory obj\msi\work
```

Payload folders: `Revit<year>\` (the add-in's binaries), `Navisworks<year>\` (the plugin folder, or a
bundle's `Contents\<year>`), and any name an `ExtraFolders` entry refers to. The script writes the WiX
source, builds with the WiX CLI (`dotnet tool install --global wix --version 5.0.2`, then
`wix extension add -g WixToolset.UI.wixext/5.0.2`), runs Windows Installer's validation (ICEs), checks
the custom actions, and prints the SHA-256.

### The definition file

| Key | |
|---|---|
| `Id` | Stable product id. Keys the installer's registry state and finds Setup.exe copies (`Uninstall\ABAdvTools.<Id>`). Never rename. |
| `Name`, `ArpName` | Product name; `ArpName` (optional) is the package / Apps and Features name. |
| `FileName` | Output is `<FileName>-<version>.msi`. |
| `Repository` | GitHub repository: Apps and Features links and the finish page's release-notes link. |
| `UpgradeCode` | Constant for the life of the product. Reuse an earlier MSI's code so it is upgraded in place. |
| `Description`, `NextSteps`, `RemovedText`, `HostSummary` | Welcome page, finish page (installed / removed); `HostSummary` is computed if absent. |
| `Icon`, `License` | Apps and Features icon (.ico, `tools\make-product-icon.ps1`); optional RTF read-me / terms page. |
| `Scope` | `User` (per user only) or `UserOrMachine` (one dual-purpose package, "Install for" page). |
| `DefaultScope` | `Machine` or `User`, for `UserOrMachine`. |
| `ScopeNote`, `ReleasesNote` | Extra text on those pages. |
| `RevitAddin` | `ManifestFileName`, `FolderName`, `AssemblyFileName`, `AddInName`, `FullClassName`, `AddInId`, `VendorId`, `VendorDescription`, `Releases` (`AllBuilt` or `Detected`). The `.addin` manifest is written by the kit. |
| `NavisworksPlugin` | `FolderName`, `Editions`. Into `<install folder>\Plugins\<FolderName>`, per edition and release found; Everyone only. |
| `NavisworksBundle` | `BundleName`, `ModuleFileName`, `AppName`, `Description`, `ProductCode`, `UpgradeCode`, `Editions`, `Releases`. `PackageContents.xml` is generated for exactly the releases ticked (one prepared variant per combination; up to 8 releases). |
| `ExtraFolders` | `@{ Payload; Root = 'LocalAppData'; Path }` — `User` scope products, e.g. the MCP server. |
| `RemoveOnUninstall` | `@{ Root = 'LocalAppData'; Path }` — files the add-in writes that uninstall should clear. |
| `Fragments`, `OptionDialogs`, `ComponentGroups` | Product WiX: extra pages (inserted after "Choose releases"), their components. Schedule defaults `After="AbDefaultsEnd"`, guard them with `NOT AB_UIRAN`. |

### What every package does

- **Pages:** Welcome → (read-me) → (Install for) → Choose releases → (product pages) → (Earlier
  versions) → Ready → progress → finish, plus Repair / Remove from Apps and Features. Artwork from
  `assets\msi_dialog.bmp` / `msi_banner.bmp` (`tools\make-msi-art.ps1`).
- **Choose releases:** a tick per Revit year, Navisworks edition (plugin) or Navisworks release
  (bundle) built. `AllBuilt` ticks every release (ready for a release installed later); `Detected`
  ticks the ones installed. Detection reads the Autodesk registry keys and `[%ProgramW6432]`.
  Ticks are remembered (`HKMU\Software\AB Adv Tools\Installer\<Id>`) and restored on the next upgrade.
- **Earlier versions:** earlier `.msi`s with the same UpgradeCode are upgraded in place. A copy the
  retired Setup.exe installed is listed and removed first unless unticked — files, its own setup copy
  and its Apps and Features key — once, on first install. A copy installed for everyone can only be
  removed by an Everyone install, so the scope page suggests Everyone then.
- **Scope guards:** Windows Installer upgrades only within one scope, so an earlier `.msi` locks the
  scope, and a silent install in the other scope stops with a clear error.
- **Never closes Revit or Navisworks:** `MSIRESTARTMANAGERCONTROL=Disable` — Windows lists the program
  holding files and asks the user to close it (or finishes at restart).
- **Overwrites what Setup.exe left:** `REINSTALLMODE=amus`.

Silent deployment uses standard `msiexec` properties:

| | |
|---|---|
| `msiexec /i X.msi /qn` | default scope and default ticks |
| `ALLUSERS=1` / `MSIINSTALLPERUSER=1` | Everyone / Only me |
| `REVIT2026=0`, `NW2027=0`, `NWMANAGE2026=0` … | leave a release out |
| `ALLRELEASES=1` | tick every built release |
| `KEEPEARLIER=1` | keep a Setup.exe copy (not recommended) |
| `msiexec /x X.msi /qn` | uninstall |
| `/l*v file.log` | log |

### Testing without installing

```powershell
& shared\ABAdvTools\msi\Test-AdvToolsMsi.ps1 -Msi dist\MyTool-1.2.3.msi                      # dry run
& shared\ABAdvTools\msi\Test-AdvToolsMsi.ps1 -Msi dist\MyTool-1.2.3.msi -Scenario '', 'MSIINSTALLPERUSER=1', 'REVIT2026=0'
& shared\ABAdvTools\msi\Test-AdvToolsMsi.ps1 -Msi dist\MyTool-1.2.3.msi -Screens C:\Temp\pages # picture of every page
& shared\ABAdvTools\msi\Test-AdvToolsMsi.ps1 -Msi dist\MyTool-1.2.3.msi -Screens C:\Temp\exit -ExitPage -ExitMode Remove
```

A **dry run** copies the package, removes `RemoveExistingProducts` from the copy, adds a *show an
error* step right after `InstallValidate`, and runs the copy silently with a log. Windows Installer
does all its real work — searches, defaults, scope, folders, component decisions — then stops before
changing anything. The report lists every file it would install, where, and everything it would
tidy. Pretend an earlier Setup.exe copy exists with `AB_EARLIER_USER=1.3.0` or
`AB_EARLIER_MACHINE=1.3.0`.

`-Screens` opens the copy with its full UI, saves each page, presses Next, and presses Cancel on the
Ready page — never Install.

### Traps (each cost real time)

- **A dry run must not keep `RemoveExistingProducts`.** It uninstalls an earlier version as its own
  transaction, which the dry run's stop does not roll back. One dry run tried to uninstall a real
  SwitchBack 1.2.0; only error 1730 (not an administrator) saved it. The tester refuses to run if the
  copy still has it.
- **`MSIRESTARTMANAGERCONTROL` must stay `Disable`.** With `DisableShutdown`, a silent install still
  killed a test process holding a target file — it would close Revit with unsaved work. The price:
  `InstallValidate` takes over a minute when files already exist.
- **Page conditions hold 255 characters.** Longer ones fail as error 2806 halfway through the pages;
  the ICE run catches it. Build long conditions from short property steps.
- **`MajorUpgrade` needs `IgnoreLanguage="yes"`:** old WiX packages have language 0.
- **"Only me" moves `[ProgramFiles64Folder]`** to `%LOCALAPPDATA%\Programs`: search Autodesk installs
  with `[%ProgramW6432]`.
- **Checkbox defaults never go in the Property table:** an unticked box would be reset by the
  table value when the install runs. Defaults are set by actions, in the UI and in silent installs only.
- **COM from PowerShell:** unwrap `Join-Path` results before `InvokeMember`, and release every view
  and database, or the package stays locked.

### Never downgrade an add-in's installer

Moving an add-in onto a new installer must keep everything its previous installer did. How kit 1.x
Setup.exe features map onto the `.msi`:

| Setup.exe did… | `.msi` |
|---|---|
| all users / current user choice | `Scope = 'UserOrMachine'` + `DefaultScope` |
| release checklist, incl. releases not installed | `Releases = 'AllBuilt'` / `'Detected'` |
| read-me / licence page | `License` |
| find and remove earlier versions | same UpgradeCode for MSIs; Setup.exe copies automatically |
| options on its window (e.g. AI clients) | a product page via `Fragments` + `OptionDialogs`, recording the choice; the add-in applies it |
| buttons usable without installing (Verify) | a ribbon button in the add-in |
| text after uninstalling | `RemovedText` |
| `/silent`, `/uninstall`, `/allusers`, `/currentuser`, `/targets:`, `/keepold`, `/log:` | `/qn`, `/x`, `ALLUSERS=1`, `MSIINSTALLPERUSER=1`, release properties, `KEEPEARLIER=1`, `/l*v` |

Check the result with `Test-AdvToolsMsi.ps1 -Screens` and compare every page with the previous installer.

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

1. `installer\MyTool.msi.psd1`: copy the closest existing one — Clash Approver (Navisworks bundle, read-me
   page, Everyone by default), Tagger (bundle, Only me by default), SwitchBack (Revit plus a Navisworks
   plugin folder), MCP Bridge (per user, extra folder, a product page). Give it a new `Id` and a new
   `UpgradeCode` (or an earlier MSI's).
2. `installer\product.ico` from the product logo: `tools\make-product-icon.ps1`.
3. A build script: build the add-in, stage `obj\msi\payload\<unit>` folders, call `New-AdvToolsMsi.ps1`
   with the add-in's version. Add `-DryRun` support that calls `Test-AdvToolsMsi.ps1`.
4. Dry-run it, capture its pages, and read both.

### Release

Tag `v<version>` with the `.msi` attached: `tools\publish-release.ps1` (it reads the version from the
package). Make the repository public for release notifications to work.
