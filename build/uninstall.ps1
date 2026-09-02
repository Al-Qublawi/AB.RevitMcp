<#
.SYNOPSIS
    Removes the AB Revit MCP add-in and server for the current user.

.EXAMPLE
    .\uninstall.ps1
    .\uninstall.ps1 -KeepLogs
#>
[CmdletBinding()]
param(
    [int[]] $Versions = @(2020, 2021, 2022, 2023, 2024, 2025, 2026, 2027),
    [switch] $KeepLogs
)

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'ABRevitMcp'
$addinsRoot  = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

Write-Host ''
Write-Host 'AB Revit MCP - uninstall' -ForegroundColor Cyan
Write-Host ''

$revit = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($revit) {
    Write-Warning 'Revit is running. Close it first, or the add-in DLLs will be locked.'
}

foreach ($version in $Versions) {
    $addinDir = Join-Path $addinsRoot $version
    $manifest = Join-Path $addinDir 'AB.RevitMcp.addin'
    if (Test-Path $manifest) {
        Remove-Item $manifest -Force
        Write-Host ("  removed manifest for Revit {0}" -f $version) -ForegroundColor Green
    }
    # assemblies live beside the manifest
    $assemblies = Join-Path $addinDir 'ABRevitMcp'
    if (Test-Path $assemblies) {
        try {
            Remove-Item $assemblies -Recurse -Force
            Write-Host ("  removed assemblies for Revit {0}" -f $version) -ForegroundColor Green
        }
        catch { Write-Warning ("Could not remove {0}: {1}" -f $assemblies, $_.Exception.Message) }
    }
}

# 'Doctor' is only present in pre-1.1 installs; the diagnostic is now a mode of the server.
foreach ($folder in @('bin', 'Server', 'Doctor', 'endpoints')) {
    $path = Join-Path $installRoot $folder
    if (Test-Path $path) {
        try {
            Remove-Item $path -Recurse -Force
            Write-Host ("  removed {0}" -f $path) -ForegroundColor Green
        }
        catch {
            Write-Warning ("Could not remove {0}: {1}" -f $path, $_.Exception.Message)
        }
    }
}

$settings = Join-Path $installRoot 'settings.json'
if (Test-Path $settings) { Remove-Item $settings -Force }

if (-not $KeepLogs) {
    $logs = Join-Path $installRoot 'logs'
    if (Test-Path $logs) {
        Remove-Item $logs -Recurse -Force
        Write-Host ("  removed {0}" -f $logs) -ForegroundColor Green
    }
}
else {
    Write-Host ('  kept logs in {0}' -f (Join-Path $installRoot 'logs')) -ForegroundColor DarkGray
}

if ((Test-Path $installRoot) -and -not (Get-ChildItem $installRoot -Force)) {
    Remove-Item $installRoot -Force
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Cyan
Write-Host 'Remember to remove the "revit" entry from your MCP client configuration'
Write-Host '(a .abmcp-backup file sits next to any config the installer edited).'
Write-Host ''
