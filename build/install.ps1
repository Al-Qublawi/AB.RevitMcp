<#
.SYNOPSIS
    One-step installer for the AB Revit MCP Bridge.

.DESCRIPTION
    Installs everything needed to drive Revit from an MCP-compatible AI client:

      * builds the add-in for every Revit release installed on this machine
      * publishes the MCP server SELF-CONTAINED by default, so no .NET runtime is required
      * copies binaries to %LOCALAPPDATA%\ABRevitMcp  (per-user, no elevation, no registry)
      * writes one .addin manifest per Revit release
      * optionally registers the server with Claude Desktop, Cursor and VS Code (with backups)
      * runs the Doctor to verify the result before you ever start Revit

    Everything is per-user. Nothing is written to Program Files, the registry, or the GAC.

.PARAMETER SelfContained
    Bundle the .NET 8 runtime with the server (default). Use -SelfContained:$false to produce a
    smaller install that requires the .NET 8 Desktop Runtime to already be present.

.PARAMETER ConfigureClients
    Also register the server in the MCP configuration of any AI client found on this machine.
    Existing files are backed up first.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Versions 2024 -ConfigureClients
    .\install.ps1 -SkipBuild            # reuse an existing artifacts folder
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [int[]] $Versions = @(2020, 2021, 2022, 2023, 2024, 2025, 2026, 2027),

    [string] $RevitProgramRoot = 'C:\Program Files\Autodesk',

    [bool]   $SelfContained = $true,

    [switch] $ConfigureClients,

    [switch] $SkipBuild,

    [switch] $SkipVerify,

    # Only (re)write AI client configuration. Touches no binaries, so it works while Revit runs.
    [switch] $ConfigureClientsOnly,

    # Fail instead of stopping a running MCP server process that is locking its own files.
    [switch] $NoStopServer
)

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$artifacts    = Join-Path $repoRoot ("artifacts\{0}" -f $Configuration)
$templatePath = Join-Path $repoRoot 'src\AB.RevitMcp.Addin\Manifests\AB.RevitMcp.addin.template'

$installRoot  = Join-Path $env:LOCALAPPDATA 'ABRevitMcp'
$binRoot      = Join-Path $installRoot 'bin'
$serverTarget = Join-Path $installRoot 'Server'
$doctorTarget = Join-Path $installRoot 'Doctor'
$serverExe    = Join-Path $serverTarget 'AB.RevitMcp.Server.exe'
$addinsRoot   = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

# UTF-8 WITHOUT a BOM. Windows PowerShell's -Encoding UTF8 writes a BOM, and while Revit
# tolerates it, hand-written manifests conventionally have none - one less variable.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Step($text)  { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Ok($text)    { Write-Host "   [ok]   $text" -ForegroundColor Green }
function Info($text)  { Write-Host "          $text" -ForegroundColor DarkGray }
function Warn2($text) { Write-Host "   [warn] $text" -ForegroundColor Yellow }
function Die($text)   { Write-Host "   [fail] $text" -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host '  AB Revit MCP Bridge - installer' -ForegroundColor White
Write-Host '  ------------------------------' -ForegroundColor DarkGray

# ============================================================ 1. preflight
Step 'Checking prerequisites'

if ($PSVersionTable.PSVersion.Major -lt 5) {
    Die "PowerShell 5.1 or newer is required (found $($PSVersionTable.PSVersion))."
}
Ok "PowerShell $($PSVersionTable.PSVersion)"

# Revit itself requires .NET Framework 4.8; the add-in targets it for Revit 2020-2024.
$ndpRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if ($ndpRelease -and $ndpRelease -ge 528040) { Ok ".NET Framework 4.8 present (release $ndpRelease)" }
elseif ($ndpRelease)                          { Warn2 ".NET Framework release $ndpRelease is older than 4.8; Revit 2020-2024 support may fail." }
else                                          { Warn2 'Could not detect .NET Framework 4.8.' }

# Which Revit releases are actually here?
$installedRevit = @()
foreach ($v in $Versions) {
    if (Test-Path (Join-Path $RevitProgramRoot ("Revit {0}\RevitAPI.dll" -f $v))) { $installedRevit += $v }
}
if ($installedRevit.Count -eq 0) {
    Die "No Revit installation found under '$RevitProgramRoot'. Pass -RevitProgramRoot if Revit lives elsewhere."
}
Ok ("Revit detected: {0}" -f ($installedRevit -join ', '))

# Revit must be closed: its DLLs are locked while it runs, and add-ins load only at startup.
$revitRunning = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($revitRunning) {
    Warn2 "Revit is running (PID $($revitRunning.Id -join ', '))."
    Warn2 'Installed files may be locked, and Revit only loads add-ins at startup.'
    Warn2 'Close Revit, then re-run this installer, and start Revit again afterwards.'
}

if (-not $SkipBuild) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Die 'The .NET SDK is required to build. Install it, or re-run with -SkipBuild to use a prebuilt artifacts folder.' }
    Ok ("dotnet SDK {0}" -f (& dotnet --version))
}

# ConfigureClientsOnly skips every binary step - useful when Revit is open and all you want is
# to (re)register the server with an AI client.
if ($ConfigureClientsOnly) {
    $ConfigureClients = $true
    $SkipBuild = $true
}

# ============================================================ 2. build
if ($ConfigureClientsOnly) {
    Step 'Client configuration only - no binaries will be touched'
}
elseif ($SkipBuild) {
    Step 'Using existing build output'
    if (-not (Test-Path $artifacts)) { Die "No build output at '$artifacts'. Run without -SkipBuild." }
    Info $artifacts
}
else {
    Step 'Building the add-in'
    & (Join-Path $PSScriptRoot 'build-all.ps1') `
        -Configuration $Configuration -Versions $installedRevit -RevitProgramRoot $RevitProgramRoot -SkipServer |
        Where-Object { $_ -match 'BUILD|ok|FAILED|SKIP' } | ForEach-Object { Info $_.Trim() }
    if ($LASTEXITCODE -ne 0) { Die 'The add-in build failed. Scroll up for the compiler errors.' }
    Ok 'add-in built'

    Step ("Publishing the MCP server ({0})" -f $(if ($SelfContained) { 'self-contained - no .NET runtime needed' } else { 'framework-dependent' }))
    $serverProj = Join-Path $repoRoot 'src\AB.RevitMcp.Server\AB.RevitMcp.Server.csproj'
    $publishDir = Join-Path $artifacts 'Server'

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    if ($SelfContained) {
        & dotnet publish $serverProj -c $Configuration -r win-x64 --self-contained true -o $publishDir --nologo -v quiet | Out-Null
    }
    else {
        & dotnet publish $serverProj -c $Configuration --self-contained false -o $publishDir --nologo -v quiet | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { Die 'Publishing the MCP server failed.' }
    Ok 'server published'

}

# ============================================================ 3. install files
if (-not $ConfigureClientsOnly) {
Step 'Installing'

if (-not (Test-Path $templatePath)) { Die "Manifest template missing: $templatePath" }
$template = [System.IO.File]::ReadAllText($templatePath)

# ---- server ----
$serverSource = Join-Path $artifacts 'Server'
if (-not (Test-Path $serverSource)) { Die "Server output not found at '$serverSource'." }

# The MCP server is a child process of whatever AI client is open (Claude Desktop, Cursor, ...),
# so it is very often RUNNING during an upgrade and holding its own DLLs. Stopping it is safe:
# it is stateless, and the client relaunches it on the next tool call.
$runningServers = @(Get-Process -Name 'AB.RevitMcp.Server' -ErrorAction SilentlyContinue)
if ($runningServers.Count -gt 0) {
    if ($NoStopServer) {
        Die ("The MCP server is running (PID {0}) and is locking its files. " -f ($runningServers.Id -join ', ')) +
            "Close your AI client, or re-run without -NoStopServer."
    }
    Info ("stopping running MCP server (PID {0}) so its files can be replaced" -f ($runningServers.Id -join ', '))
    foreach ($proc in $runningServers) {
        try { $proc.Kill(); $proc.WaitForExit(5000) } catch { Warn2 "Could not stop PID $($proc.Id): $($_.Exception.Message)" }
    }
    Start-Sleep -Milliseconds 400
    Info 'your AI client will relaunch it automatically on the next Revit tool call'
}

if (Test-Path $serverTarget) { Remove-Item $serverTarget -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $serverTarget | Out-Null
try {
    Copy-Item -Path (Join-Path $serverSource '*') -Destination $serverTarget -Recurse -Force -ErrorAction Stop
}
catch {
    Die "Could not install the MCP server: $($_.Exception.Message) " +
        "Close every AI client that has the Revit connector loaded, then re-run."
}
Ok "server  -> $serverTarget"

# The diagnostic is a MODE of the server (AB.RevitMcp.Server.exe --doctor) rather than a separate
# executable: the server ships self-contained, so the diagnostic runs on a machine that has never
# had .NET installed. A second framework-dependent exe would not have started at all.

# ---- add-in, one folder + one manifest per Revit release ----
$installed = @()
foreach ($version in $installedRevit) {
    $source = Join-Path $artifacts ("Revit{0}" -f $version)
    $dll    = Join-Path $source 'AB.RevitMcp.Addin.dll'
    if (-not (Test-Path $dll)) { Warn2 "no build output for Revit $version - skipped"; continue }

    # The add-in assemblies go BESIDE the manifest, under %APPDATA%, and the manifest references
    # them with a RELATIVE path - the layout every mainstream Revit add-in uses.
    #
    # They deliberately do NOT go in %LOCALAPPDATA%. Endpoint protection suites (Bitdefender,
    # CrowdStrike, Cortex, ...) very commonly block DLL loads from Local AppData. When that happens
    # Revit cannot load the file and reports "the assembly ... does not exist" about a DLL that is
    # plainly on disk - a genuinely misleading dead end. %APPDATA%\Autodesk\Revit\Addins is the
    # path those products already trust for Revit.
    $addinDir = Join-Path $addinsRoot $version
    $target   = Join-Path $addinDir 'ABRevitMcp'
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    # Only our own assemblies. A Revit API DLL copied next to an add-in causes type-identity bugs
    # that are extremely hard to diagnose.
    foreach ($file in (Get-ChildItem -Path $source -File | Where-Object { $_.Name -like 'AB.RevitMcp.*' })) {
        $dest = Join-Path $target $file.Name
        try {
            Copy-Item $file.FullName -Destination $dest -Force
        }
        catch {
            # Revit holds a lock on the add-in DLLs while it is running. If what is already
            # installed is byte-identical there is genuinely nothing to do, so a running Revit
            # should not turn a no-op reinstall into a hard failure.
            $identical = $false
            if (Test-Path $dest) {
                try {
                    $identical = (Get-FileHash $file.FullName -Algorithm SHA256).Hash -eq
                                 (Get-FileHash $dest -Algorithm SHA256).Hash
                }
                catch { $identical = $false }
            }
            if ($identical) {
                Info "$($file.Name) already up to date (locked by running Revit)"
            }
            else {
                Die ("Could not update '{0}' for Revit {1} - it is locked by a running Revit. " -f $file.Name, $version) +
                    "Close Revit and re-run this installer."
            }
        }
    }

    $manifest = $template.Replace('{ASSEMBLY_PATH}', 'ABRevitMcp\AB.RevitMcp.Addin.dll')
    [System.IO.File]::WriteAllText((Join-Path $addinDir 'AB.RevitMcp.addin'), $manifest, $utf8NoBom)

    # Remove the pre-1.1 layout so a machine cannot end up loading two copies.
    $legacy = Join-Path $binRoot ("Revit{0}" -f $version)
    if (Test-Path $legacy) {
        try { Remove-Item $legacy -Recurse -Force; Info "removed legacy folder $legacy" } catch { }
    }

    Ok "Revit $version -> $target"
    $installed += $version
}

if ($installed.Count -eq 0) { Die 'Nothing was installed - no matching build output was found.' }
}
else { $installed = @('(unchanged)') }

# ============================================================ 4. MCP clients
$configJson = @"
{
  "mcpServers": {
    "revit": {
      "command": "$($serverExe.Replace('\','\\'))",
      "args": [],
      "env": {}
    }
  }
}
"@

if ($ConfigureClients) {
    Step 'Registering with MCP clients'

    function Set-McpServerEntry {
        param([string] $Path, [string] $RootKey, [string] $ClientName)

        try {
            $dir = Split-Path -Parent $Path
            if (-not (Test-Path $dir)) { Info "$ClientName not installed - skipped"; return }

            $doc = $null
            if (Test-Path $Path) {
                $backup = "$Path.abmcp-backup"
                Copy-Item $Path $backup -Force
                Info "backed up $ClientName config -> $(Split-Path -Leaf $backup)"
                $raw = Get-Content $Path -Raw
                if ($raw.Trim()) { $doc = $raw | ConvertFrom-Json }
            }
            if (-not $doc) { $doc = New-Object PSObject }

            if (-not ($doc.PSObject.Properties.Name -contains $RootKey)) {
                $doc | Add-Member -MemberType NoteProperty -Name $RootKey -Value (New-Object PSObject)
            }

            $entry = New-Object PSObject
            $entry | Add-Member -MemberType NoteProperty -Name 'command' -Value $serverExe
            $entry | Add-Member -MemberType NoteProperty -Name 'args'    -Value @()

            $servers = $doc.$RootKey
            if ($servers.PSObject.Properties.Name -contains 'revit') {
                $servers.PSObject.Properties.Remove('revit')
            }
            $servers | Add-Member -MemberType NoteProperty -Name 'revit' -Value $entry

            $json = $doc | ConvertTo-Json -Depth 12
            [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
            Ok "$ClientName configured ($Path)"
        }
        catch {
            Warn2 "Could not update $ClientName config: $($_.Exception.Message)"
        }
    }

    Set-McpServerEntry -Path (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json') -RootKey 'mcpServers' -ClientName 'Claude Desktop'
    Set-McpServerEntry -Path (Join-Path $env:USERPROFILE '.cursor\mcp.json')              -RootKey 'mcpServers' -ClientName 'Cursor'
    Set-McpServerEntry -Path (Join-Path $env:APPDATA 'Code\User\mcp.json')                -RootKey 'servers'    -ClientName 'VS Code'

    # Claude Code keeps MCP servers inside ~\.claude.json alongside live session state.
    # Editing that file from a script risks clobbering it, so print the supported command instead.
    Info 'Claude Code: run the command shown at the end of this output.'
}

# ============================================================ 5. verify
if (-not $SkipVerify) {
    if (Test-Path $serverExe) {
        Step 'Verifying the installation'
        # Out-Host keeps the native tool's output in order when the whole script is piped.
        & $serverExe --doctor --revit-root $RevitProgramRoot | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            Warn2 'The Doctor reported problems - see above. The installation is on disk but may not load in Revit.'
        }
    }
}

# ============================================================ 6. next steps
Write-Host ''
Write-Host '  Installed' -ForegroundColor Green
Write-Host ('  Revit releases : {0}' -f ($installed -join ', '))
Write-Host ("  Server         : {0}" -f $serverExe)
Write-Host ("  Runtime        : {0}" -f $(if ($SelfContained) { 'bundled (no .NET install required)' } else { 'requires .NET 8 Desktop Runtime' }))
Write-Host ''
Write-Host '  Next steps' -ForegroundColor Cyan
Write-Host '   1. Start Revit and open a project.'
Write-Host '   2. Ribbon tab "AB MCP AI" -> panel "Bridge" -> press "Start Bridge".'
if (-not $ConfigureClients) {
    Write-Host '   3. Add this to your MCP client configuration:'
    Write-Host ''
    Write-Host $configJson
    Write-Host ''
    Write-Host ('      Claude Desktop  {0}\Claude\claude_desktop_config.json' -f $env:APPDATA)
    Write-Host ('      Cursor          {0}\.cursor\mcp.json' -f $env:USERPROFILE)
    Write-Host ('      VS Code         {0}\Code\User\mcp.json   (use "servers" not "mcpServers")' -f $env:APPDATA)
}
else {
    Write-Host '   3. Restart your AI client so it picks up the new server.'
}
Write-Host ''
Write-Host '      Claude Code:'
Write-Host ("        claude mcp add revit `"{0}`"" -f $serverExe)
Write-Host ''
Write-Host ('  Re-check any time:  "{0}" --doctor' -f $serverExe)
Write-Host ''
exit 0
