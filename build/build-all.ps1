<#
.SYNOPSIS
    Builds the AB Revit MCP solution for every installed Revit release, plus the MCP server.

.DESCRIPTION
    Revit 2020-2024 run on .NET Framework 4.8; Revit 2025+ run on .NET 8. This script picks the
    right target framework per release, skips releases that are not installed, and drops
    everything into artifacts\<Configuration>\.

.EXAMPLE
    .\build-all.ps1
    .\build-all.ps1 -Configuration Debug -Versions 2024,2026
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [int[]] $Versions = @(2020, 2021, 2022, 2023, 2024, 2025, 2026),

    [string] $RevitProgramRoot = 'C:\Program Files\Autodesk',

    [switch] $SkipServer
)

$ErrorActionPreference = 'Stop'
$repoRoot  = Split-Path -Parent $PSScriptRoot
$addinProj = Join-Path $repoRoot 'src\AB.RevitMcp.Addin\AB.RevitMcp.Addin.csproj'
$serverProj = Join-Path $repoRoot 'src\AB.RevitMcp.Server\AB.RevitMcp.Server.csproj'

function Get-TargetFramework([int] $version) {
    if ($version -ge 2025) { return 'net8.0-windows' }
    return 'net48'
}

Write-Host ''
Write-Host 'AB Revit MCP - build' -ForegroundColor Cyan
Write-Host ("Configuration : {0}" -f $Configuration)
Write-Host ("Revit root    : {0}" -f $RevitProgramRoot)
Write-Host ''

$built   = @()
$skipped = @()
$failed  = @()

foreach ($version in $Versions) {
    $apiDir = Join-Path $RevitProgramRoot ("Revit {0}" -f $version)
    $apiDll = Join-Path $apiDir 'RevitAPI.dll'

    if (-not (Test-Path $apiDll)) {
        Write-Host ("  SKIP  Revit {0} - RevitAPI.dll not found in {1}" -f $version, $apiDir) -ForegroundColor DarkGray
        $skipped += $version
        continue
    }

    $tfm = Get-TargetFramework $version
    Write-Host ("  BUILD Revit {0} ({1})" -f $version, $tfm) -ForegroundColor Yellow

    & dotnet build $addinProj `
        -c $Configuration `
        -f $tfm `
        -p:RevitVersion=$version `
        -p:RevitProgramRoot=$RevitProgramRoot `
        --nologo -v quiet | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Host ("        FAILED (exit {0})" -f $LASTEXITCODE) -ForegroundColor Red
        $failed += $version
    }
    else {
        Write-Host '        ok' -ForegroundColor Green
        $built += $version
    }
}

if (-not $SkipServer) {
    Write-Host ''
    Write-Host '  BUILD MCP server (net8.0-windows)' -ForegroundColor Yellow
    & dotnet build $serverProj -c $Configuration --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("        FAILED (exit {0})" -f $LASTEXITCODE) -ForegroundColor Red
        $failed += 'server'
    }
    else {
        Write-Host '        ok' -ForegroundColor Green
    }
}

Write-Host ''
Write-Host ('Built   : {0}' -f ($(if ($built.Count) { $built -join ', ' } else { 'none' })))
Write-Host ('Skipped : {0}' -f ($(if ($skipped.Count) { $skipped -join ', ' } else { 'none' })))
if ($failed.Count) {
    Write-Host ('Failed  : {0}' -f ($failed -join ', ')) -ForegroundColor Red
}
Write-Host ('Output  : {0}' -f (Join-Path $repoRoot ("artifacts\{0}" -f $Configuration)))
Write-Host ''

if ($failed.Count) { exit 1 }
if ($built.Count -eq 0) {
    Write-Warning 'No Revit release was built. Check -RevitProgramRoot and that Revit is installed.'
    exit 1
}
exit 0
