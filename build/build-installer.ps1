<#
.SYNOPSIS
    Produces dist\AB.RevitMcp.Setup.exe - a single self-contained installer.

.DESCRIPTION
    1. Builds the add-in for every Revit release requested (and installed).
    2. Publishes the MCP server self-contained, so the target machine needs no .NET runtime.
    3. Zips each of those into installer\AB.RevitMcp.Setup\payload\.
    4. Builds the setup executable, which embeds those zips as resources.

    The result is ONE file you can copy to any Windows machine. It needs no .NET SDK, no runtime
    and no admin rights - .NET Framework 4.8 (which Revit itself requires) is the only prerequisite.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Versions 2020,2024
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [int[]] $Versions = @(2020, 2021, 2022, 2023, 2024, 2025, 2026),

    [string] $RevitProgramRoot = 'C:\Program Files\Autodesk',

    [switch] $SkipBuild,

    # Code signing. Supply one of these to sign the payload binaries AND the finished installer -
    # the real fix for Defender's ASR "unless they meet a prevalence, age, or trusted list
    # criteria" rule, which blocks any executable it has not seen before.
    [string] $CertThumbprint,
    [string] $CertPfx,
    [System.Security.SecureString] $CertPassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$signArgs = @{}
if ($CertThumbprint) { $signArgs['Thumbprint'] = $CertThumbprint }
elseif ($CertPfx)    { $signArgs['PfxPath'] = $CertPfx; if ($CertPassword) { $signArgs['PfxPassword'] = $CertPassword } }
$doSign = $signArgs.Count -gt 0
if ($doSign) { $signArgs['TimestampUrl'] = $TimestampUrl }

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$artifacts  = Join-Path $repoRoot ("artifacts\{0}" -f $Configuration)
$setupProj  = Join-Path $repoRoot 'installer\AB.RevitMcp.Setup\AB.RevitMcp.Setup.csproj'
$payloadDir = Join-Path $repoRoot 'installer\AB.RevitMcp.Setup\payload'
$distDir    = Join-Path $repoRoot 'dist'

function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   [ok]   $t" -ForegroundColor Green }
function Info($t) { Write-Host "          $t" -ForegroundColor DarkGray }
function Die($t)  { Write-Host "   [fail] $t" -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host '  AB Revit MCP Bridge - installer builder' -ForegroundColor White
Write-Host '  by Abdullah Lotfy' -ForegroundColor DarkGray

# ---------------------------------------------------------------- 1. build
if (-not $SkipBuild) {
    Step 'Building the add-in for each Revit release'
    & (Join-Path $PSScriptRoot 'build-all.ps1') `
        -Configuration $Configuration -Versions $Versions -RevitProgramRoot $RevitProgramRoot -SkipServer |
        Where-Object { $_ -match 'BUILD|ok|SKIP|FAILED' } | ForEach-Object { Info $_.Trim() }
    if ($LASTEXITCODE -ne 0) { Die 'Add-in build failed.' }

    Step 'Publishing the MCP server (self-contained)'
    $serverProj = Join-Path $repoRoot 'src\AB.RevitMcp.Server\AB.RevitMcp.Server.csproj'
    $serverOut  = Join-Path $artifacts 'Server'
    if (Test-Path $serverOut) { Remove-Item $serverOut -Recurse -Force }
    & dotnet publish $serverProj -c $Configuration -r win-x64 --self-contained true -o $serverOut --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Die 'Server publish failed.' }
    Ok 'server published'
}

# ---------------------------------------------------------------- 2. payload
Step 'Packing the payload'

if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem

$packed = @()
foreach ($version in $Versions) {
    $source = Join-Path $artifacts ("Revit{0}" -f $version)
    if (-not (Test-Path (Join-Path $source 'AB.RevitMcp.Addin.dll'))) { continue }

    # Stage only our own assemblies - a stray Revit API DLL beside an add-in causes
    # type-identity bugs that are miserable to diagnose.
    $stage = Join-Path $env:TEMP ("abmcp_stage_{0}" -f $version)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Get-ChildItem $source -File |
        Where-Object { $_.Name -like 'AB.RevitMcp.*' -and $_.Extension -ne '.pdb' } |
        ForEach-Object { Copy-Item $_.FullName -Destination $stage }

    $zip = Join-Path $payloadDir ("Revit{0}.zip" -f $version)
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Remove-Item $stage -Recurse -Force

    $sizeKb = [math]::Round((Get-Item $zip).Length / 1KB, 0)
    Ok ("Revit{0}.zip  ({1} KB)" -f $version, $sizeKb)
    $packed += $version
}

if ($packed.Count -eq 0) { Die "No add-in build output found in '$artifacts'. Run without -SkipBuild." }

$serverSource = Join-Path $artifacts 'Server'
if (-not (Test-Path (Join-Path $serverSource 'AB.RevitMcp.Server.exe'))) {
    Die "Server output not found at '$serverSource'. Run without -SkipBuild."
}

# The server publish contains .pdb files that add megabytes and help nobody on a user's machine.
$serverStage = Join-Path $env:TEMP 'abmcp_stage_server'
if (Test-Path $serverStage) { Remove-Item $serverStage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $serverStage | Out-Null
Get-ChildItem $serverSource -File |
    Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $serverStage }

# Sign the inner binaries BEFORE zipping. The add-in DLLs are what Revit loads and the server
# exe is what the AI client launches, so both need their own signature - signing only the
# installer would leave everything it drops behind unsigned.
if ($doSign) {
    Step 'Signing payload binaries'
    $payloadBinaries = @()
    foreach ($version in $packed) {
        $payloadBinaries += (Get-ChildItem (Join-Path $artifacts ("Revit{0}" -f $version)) -Filter 'AB.RevitMcp.*.dll' -File |
                             ForEach-Object { $_.FullName })
    }
    $payloadBinaries += (Get-ChildItem $serverStage -Filter 'AB.RevitMcp.*' -File |
                         Where-Object { $_.Extension -in '.dll', '.exe' } |
                         ForEach-Object { $_.FullName })

    & (Join-Path $PSScriptRoot 'sign.ps1') -Files $payloadBinaries @signArgs
    if ($LASTEXITCODE -ne 0) { Die 'Signing the payload failed.' }
}

$serverZip = Join-Path $payloadDir 'Server.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($serverStage, $serverZip)
Remove-Item $serverStage -Recurse -Force
Ok ("Server.zip  ({0} MB)" -f [math]::Round((Get-Item $serverZip).Length / 1MB, 1))

# ---------------------------------------------------------------- 3. setup exe
Step 'Staging the shared contracts assembly'

# The setup exe compiles against, and embeds, the net48 build of Contracts so the finished
# installer is a single file with no loose dependencies beside it.
$contractsProj = Join-Path $repoRoot 'src\AB.RevitMcp.Contracts\AB.RevitMcp.Contracts.csproj'
& dotnet build $contractsProj -c $Configuration -f net48 --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Die 'Contracts build failed.' }

$contractsDll = Join-Path $repoRoot ('src\AB.RevitMcp.Contracts\bin\' + $Configuration + '\net48\AB.RevitMcp.Contracts.dll')
if (-not (Test-Path $contractsDll)) { Die "Contracts assembly not found at '$contractsDll'." }

$libDir = Join-Path $repoRoot 'installer\AB.RevitMcp.Setup\lib'
New-Item -ItemType Directory -Force -Path $libDir | Out-Null
Copy-Item $contractsDll (Join-Path $libDir 'AB.RevitMcp.Contracts.dll') -Force
Ok 'contracts staged and will be embedded'

Step 'Building the setup executable'

# Force a rebuild: the payload is embedded at compile time, so an incremental build would
# happily ship yesterday's binaries inside today's installer.
& dotnet build $setupProj -c $Configuration --nologo -v quiet --no-incremental | Out-Null
if ($LASTEXITCODE -ne 0) { Die 'Setup build failed.' }

$builtExe = Join-Path $artifacts 'Setup\AB.RevitMcp.Setup.exe'
if (-not (Test-Path $builtExe)) { Die "Setup executable not found at '$builtExe'." }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$finalExe = Join-Path $distDir 'AB.RevitMcp.Setup.exe'

# The previous installer is very often still open when you rebuild.
$running = @(Get-Process -Name 'AB.RevitMcp.Setup' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Die ("The installer is currently running (PID {0}). Close that window and re-run." -f ($running.Id -join ', '))
}

try {
    Copy-Item $builtExe $finalExe -Force -ErrorAction Stop
}
catch {
    Die "Could not write '$finalExe': $($_.Exception.Message) Close anything using it and re-run."
}

# It must be genuinely standalone - the whole point is copying ONE file to another machine, so
# fail loudly rather than shipping something that dies with a missing-assembly error over there.
$loose = @(Get-ChildItem (Join-Path $artifacts 'Setup') -File | Where-Object { $_.Extension -eq '.dll' })
if ($loose.Count -gt 0) {
    Write-Host ''
    Write-Host '   [fail] the setup build produced loose DLLs beside the exe:' -ForegroundColor Red
    $loose | ForEach-Object { Info $_.Name }
    Die 'The installer would not be self-contained. Every dependency must be an EmbeddedResource.'
}
Ok 'no loose dependencies - the installer is a single file'

if ($doSign) {
    Step 'Signing the installer'
    & (Join-Path $PSScriptRoot 'sign.ps1') -Files @($finalExe) @signArgs
    if ($LASTEXITCODE -ne 0) { Die 'Signing the installer failed.' }
}

# Whether signed or not, publish the details IT needs to allowlist this build. An unsigned build
# can only be allowlisted by hash - which changes every rebuild, so signing is worth the money.
$hash = (Get-FileHash $finalExe -Algorithm SHA256).Hash
$signature = Get-AuthenticodeSignature $finalExe
$hashFile = Join-Path $distDir 'AB.RevitMcp.Setup.allowlist.txt'
@(
    'AB Revit MCP Bridge - installer identity'
    'Publisher      : Abdullah Lotfy'
    ('Version        : {0}' -f (Get-Item $setupExe).VersionInfo.FileVersion)
    ('File           : {0}' -f (Split-Path -Leaf $finalExe))
    ('Size           : {0} bytes' -f (Get-Item $finalExe).Length)
    ('SHA256         : {0}' -f $hash)
    ('Signature      : {0}' -f $signature.Status)
    ('Signer         : {0}' -f $(if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(unsigned)' }))
    ''
    'If Microsoft Defender blocks this file with:'
    '  "Block executable files from running unless they meet a prevalence, age, or trusted list criteria"'
    'that is an Attack Surface Reduction POLICY rule, not a malware detection.'
    'ASR rule GUID: 01443614-cd74-433a-b99e-2ecdc07bfc25'
    ''
    'Ask your IT administrator to allow it by publisher certificate (preferred) or by the'
    'SHA256 above. On a managed device the end user cannot lift this themselves.'
) | ForEach-Object { $_ }  | Out-String | ForEach-Object {
    # UTF8 without a BOM: this file gets pasted into ticket systems and read by scripts.
    [System.IO.File]::WriteAllText($hashFile, $_, (New-Object System.Text.UTF8Encoding($false)))
}

Ok ("identity written to {0}" -f (Split-Path -Leaf $hashFile))

$sizeMb = [math]::Round((Get-Item $finalExe).Length / 1MB, 1)

Write-Host ''
Write-Host '  Installer ready' -ForegroundColor Green
Write-Host ("  {0}   ({1} MB)" -f $finalExe, $sizeMb)
Write-Host ("  Revit releases inside: {0}" -f ($packed -join ', '))
Write-Host ''
Write-Host '  Copy that single file to any Windows machine and run it.'
Write-Host '  Silent deployment:  AB.RevitMcp.Setup.exe /silent'
Write-Host ''
if ($doSign) {
    Write-Host ('  Signed: {0}' -f (Get-AuthenticodeSignature $finalExe).Status) -ForegroundColor Green
} else {
    Write-Host '  NOT SIGNED - Defender ASR will block this on managed machines.' -ForegroundColor Yellow
    Write-Host '  Re-run with -CertThumbprint or -CertPfx once you have a code-signing certificate.'
    Write-Host '  See docs\DEPLOYMENT.md.'
}
Write-Host ''
