<#
.SYNOPSIS
    Produces dist\AB.RevitMcp-<version>.msi - a single Windows Installer package.

.DESCRIPTION
    1. Builds the add-in for every Revit release requested.
    2. Publishes the MCP server self-contained, so the target machine needs no .NET runtime.
    3. Stages each of those as one part of the package.
    4. Builds the .msi with the AB Adv Tools kit (shared\ABAdvTools\msi) from
       installer\RevitMcp.msi.psd1, plus the AI clients page in installer\RevitMcp.AiClients.wxs.

    The package runs no code of its own, so company PCs with Defender's attack surface reduction
    rules install it like any .msi (1.4.0's AB.RevitMcp.Setup.exe was blocked there). Per user, no
    administrator rights: the add-in into %APPDATA%\Autodesk\Revit\Addins\<year>, the server into
    %LOCALAPPDATA%\ABRevitMcp\Server. The AI clients ticked in the installer are configured by the
    add-in the first time Revit starts.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Versions 2020,2024
    .\build-installer.ps1 -DryRun      # then show what the package would do here, changing nothing
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [int[]] $Versions = @(2020, 2021, 2022, 2023, 2024, 2025, 2026),

    [string] $RevitProgramRoot = 'C:\Program Files\Autodesk',

    [switch] $SkipBuild,

    # After building, show what the package would do on this computer, changing nothing.
    [switch] $DryRun,

    # Code signing. Supply one of these to sign the add-in and server binaries AND the finished .msi.
    # The .msi is no longer blocked unsigned, but the MCP server executable an AI client starts can
    # be (the add-in configures clients to start it through dotnet.exe to avoid that); a signature
    # also gives IT one publisher to allow instead of a hash per build.
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

$repoRoot  = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot ("artifacts\{0}" -f $Configuration)
$stage     = Join-Path $repoRoot 'obj\msi\payload'
$distDir   = Join-Path $repoRoot 'dist'
$kitMsi    = Join-Path $repoRoot 'shared\ABAdvTools\msi'

function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   [ok]   $t" -ForegroundColor Green }
function Info($t) { Write-Host "          $t" -ForegroundColor DarkGray }
function Die($t)  { Write-Host "   [fail] $t" -ForegroundColor Red; exit 1 }

$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

Write-Host ''
Write-Host "  AB Revit MCP Bridge $version - installer builder" -ForegroundColor White
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
    if (Test-Path $serverOut) { [System.IO.Directory]::Delete($serverOut, $true) }
    & dotnet publish $serverProj -c $Configuration -r win-x64 --self-contained true -o $serverOut --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Die 'Server publish failed.' }
    Ok 'server published'
}

# ---------------------------------------------------------------- 2. stage
Step 'Staging the package contents'

if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$packed = @()
foreach ($revit in $Versions) {
    $source = Join-Path $artifacts ("Revit{0}" -f $revit)
    if (-not (Test-Path (Join-Path $source 'AB.RevitMcp.Addin.dll'))) { continue }

    # Only our own assemblies - a stray Revit API DLL beside an add-in causes type-identity bugs
    # that are miserable to diagnose.
    $to = New-Item -ItemType Directory -Force -Path (Join-Path $stage ("Revit{0}" -f $revit))
    Get-ChildItem $source -File |
        Where-Object { $_.Name -like 'AB.RevitMcp.*' -and $_.Extension -ne '.pdb' } |
        ForEach-Object { Copy-Item $_.FullName -Destination $to.FullName }
    Ok ("Revit {0}" -f $revit)
    $packed += $revit
}
if ($packed.Count -eq 0) { Die "No add-in build output found in '$artifacts'. Run without -SkipBuild." }

$serverSource = Join-Path $artifacts 'Server'
if (-not (Test-Path (Join-Path $serverSource 'AB.RevitMcp.Server.exe'))) {
    Die "Server output not found at '$serverSource'. Run without -SkipBuild."
}

# The server publish contains .pdb files that add megabytes and help nobody on a user's machine.
$serverStage = New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Server')
Get-ChildItem $serverSource -File |
    Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $serverStage.FullName }
Ok ("Server  ({0} files)" -f (Get-ChildItem $serverStage.FullName -File).Count)

# Sign the inner binaries before packaging: Revit loads the add-in DLLs and an AI client may start
# the server exe, so each needs its own signature - signing only the package would leave everything
# it installs unsigned.
if ($doSign) {
    Step 'Signing the add-in and server binaries'
    $binaries = @(Get-ChildItem $stage -Recurse -File |
        Where-Object { $_.Name -like 'AB.RevitMcp.*' -and $_.Extension -in '.dll', '.exe' } |
        ForEach-Object { $_.FullName })
    & (Join-Path $PSScriptRoot 'sign.ps1') -Files $binaries @signArgs
    if ($LASTEXITCODE -ne 0) { Die 'Signing the binaries failed.' }
}

# ---------------------------------------------------------------- 3. the .msi
Step 'Building the .msi'

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$built = & (Join-Path $kitMsi 'New-AdvToolsMsi.ps1') `
    -Definition (Join-Path $repoRoot 'installer\RevitMcp.msi.psd1') `
    -Payload $stage -Version $version -OutputDirectory $distDir `
    -WorkDirectory (Join-Path $repoRoot 'obj\msi\work')
$msi = $built.Path

# Earlier installers left in dist\ would only confuse whoever picks a file to publish.
Get-ChildItem $distDir -File |
    Where-Object { $_.FullName -ne $msi -and $_.Name -like 'AB.RevitMcp*' } |
    ForEach-Object { [System.IO.File]::Delete($_.FullName) }

if ($doSign) {
    Step 'Signing the .msi'
    & (Join-Path $PSScriptRoot 'sign.ps1') -Files @($msi) @signArgs
    if ($LASTEXITCODE -ne 0) { Die 'Signing the .msi failed.' }
}

# Whether signed or not, publish the details IT needs to recognise this build.
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash
$signature = Get-AuthenticodeSignature $msi
$identity = @(
    'AB Revit MCP Bridge - installer identity'
    'Publisher      : Abdullah Lotfy'
    ('Version        : {0}' -f $version)
    ('File           : {0}' -f (Split-Path -Leaf $msi))
    ('Size           : {0} bytes' -f (Get-Item $msi).Length)
    ('SHA256         : {0}' -f $hash)
    ('Signature      : {0}' -f $signature.Status)
    ('Signer         : {0}' -f $(if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(unsigned)' }))
    ''
    'This is a Windows Installer package with no custom actions that run code. Defender''s attack'
    'surface reduction rule "Block executable files from running unless they meet a prevalence, age,'
    'or trusted list criteria" (01443614-cd74-433a-b99e-2ecdc07bfc25) applies to executables, not to'
    'it. If a policy still blocks it, allow it by publisher certificate or by the SHA256 above.'
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $distDir 'AB.RevitMcp.allowlist.txt'), $identity + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
Ok 'identity written to AB.RevitMcp.allowlist.txt'

Write-Host ''
Write-Host '  Installer ready' -ForegroundColor Green
Write-Host ("  {0}   ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
Write-Host ("  Revit releases inside: {0}" -f ($packed -join ', '))
Write-Host ''
Write-Host '  Double-click it, or deploy silently:'
Write-Host "    msiexec /i AB.RevitMcp-$version.msi /qn              (AI clients found are configured when Revit starts)"
Write-Host "    msiexec /i AB.RevitMcp-$version.msi /qn NOCLIENTS=1  (configure no AI client)"
Write-Host "    msiexec /x AB.RevitMcp-$version.msi /qn"
Write-Host ''

if ($DryRun) {
    & (Join-Path $kitMsi 'Test-AdvToolsMsi.ps1') -Msi $msi -WorkDirectory (Join-Path $repoRoot 'obj\msi\dryrun') | Out-Null
}
