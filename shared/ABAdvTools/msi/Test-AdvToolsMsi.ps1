<#
.SYNOPSIS
    Shows exactly what an AB .msi would do on this computer - which files it would install where,
    what it would tidy away, and every decision it made - without changing anything.

.DESCRIPTION
    A dry run. The script copies the package, adds one "show an error" step to the copy just
    before Windows Installer starts changing the computer (after InstallValidate), and runs the
    copy silently with full logging. Windows Installer then does all its real work - searches,
    defaults, scope, folder resolution, component decisions - and stops. Nothing is installed,
    registered or removed. The script reads the log and reports.

    Pass msiexec properties to try a scenario, e.g. -Properties 'MSIINSTALLPERUSER=1','REVIT2026=0'.
    To pretend an earlier Setup.exe copy exists, pass the property its search would have set, e.g.
    'AB_EARLIER_USER=1.3.0' (a search that finds nothing leaves a command-line value alone).

    -Screens <folder> instead opens the copy with its full UI, saves a picture of every page it
    walks through with the default choices, and cancels at the end (or lets the dry run stop).

.EXAMPLE
    .\Test-AdvToolsMsi.ps1 -Msi dist\AB.SwitchBack-1.3.1.msi
    .\Test-AdvToolsMsi.ps1 -Msi dist\AB.SwitchBack-1.3.1.msi -Properties 'MSIINSTALLPERUSER=1'
    .\Test-AdvToolsMsi.ps1 -Msi dist\AB.SwitchBack-1.3.1.msi -Screens C:\Temp\pages
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [string[]]$Properties = @(),
    # Several scenarios in one go, each a space-separated property list ('' = defaults).
    [string[]]$Scenario = @(),
    [string]$WorkDirectory,
    [string]$Screens,
    # With -Screens: show the finish page instead of walking the install pages.
    [switch]$ExitPage,
    [string]$ExitMode,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$Msi = (Resolve-Path $Msi).Path
if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'ABAdvToolsMsiTest' }
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null

# ---------------------------------------------------------------------------- Windows Installer COM
$installer = New-Object -ComObject WindowsInstaller.Installer
function Invoke-Com($target, [string]$member, [string]$kind, [object[]]$arguments) {
    $plain = $null
    if ($null -ne $arguments) {
        $plain = New-Object 'object[]' $arguments.Count
        for ($i = 0; $i -lt $arguments.Count; $i++) {
            $value = $arguments[$i]
            if ($value -is [string]) { $plain[$i] = [string]::Copy($value) }
            elseif ($value -is [int]) { $plain[$i] = [int]$value }
            else { $plain[$i] = $value }
        }
    }
    return $target.GetType().InvokeMember($member, $kind, $null, $target, $plain)
}
function Get-Rows($database, [string]$sql) {
    $view = Invoke-Com $database 'OpenView' 'InvokeMethod' @($sql)
    Invoke-Com $view 'Execute' 'InvokeMethod' $null | Out-Null
    $count = Invoke-Com (Invoke-Com $view 'ColumnInfo' 'GetProperty' @(0)) 'FieldCount' 'GetProperty' $null
    $rows = New-Object System.Collections.Generic.List[object]
    while ($true) {
        $record = Invoke-Com $view 'Fetch' 'InvokeMethod' $null
        if ($null -eq $record) { break }
        $rows.Add(@(for ($c = 1; $c -le $count; $c++) { Invoke-Com $record 'StringData' 'GetProperty' @($c) }))
    }
    Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    return , $rows
}
function Invoke-Sql($database, [string]$sql, [object[]]$values) {
    $view = Invoke-Com $database 'OpenView' 'InvokeMethod' @($sql)
    $record = Invoke-Com $installer 'CreateRecord' 'InvokeMethod' @($values.Count)
    for ($i = 0; $i -lt $values.Count; $i++) {
        if ($values[$i] -is [int]) { Invoke-Com $record 'IntegerData' 'SetProperty' @(($i + 1), $values[$i]) | Out-Null }
        else { Invoke-Com $record 'StringData' 'SetProperty' @(($i + 1), [string]$values[$i]) | Out-Null }
    }
    Invoke-Com $view 'Execute' 'InvokeMethod' @($record) | Out-Null
    Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
}

# ---------------------------------------------------------------------------- the stopping copy
$copy = Join-Path $WorkDirectory ('dryrun-' + [System.IO.Path]::GetFileName($Msi))
Copy-Item $Msi $copy -Force
Set-ItemProperty $copy -Name IsReadOnly -Value $false

$db = Invoke-Com $installer 'OpenDatabase' 'InvokeMethod' @($copy, 1)
$sequence = [int](Get-Rows $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='InstallValidate'")[0][0]

# SAFETY: RemoveExistingProducts sits right after InstallValidate, and uninstalls an earlier
# version as its own transaction - one the stop below would NOT roll back. A dry run must never
# reach it, so the copy loses it. (Found the hard way: a dry run on a machine with SwitchBack 1.2.0
# tried to remove it and was only saved by error 1730, "You must be an Administrator".)
$view = Invoke-Com $db 'OpenView' 'InvokeMethod' @("DELETE FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveExistingProducts'")
Invoke-Com $view 'Execute' 'InvokeMethod' $null | Out-Null
Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
[System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
$between = Get-Rows $db "SELECT ``Action``, ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Sequence`` > $sequence AND ``Sequence`` < 1500"
if ($between.Count -gt 0) { throw "Unexpected actions between InstallValidate and InstallInitialize: $(($between | ForEach-Object { $_[0] }) -join ', '). Not running." }
Invoke-Sql $db 'INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES (?, ?, ?, ?)' @('AbDryRunStop', 19, '', 'AB dry run: stopped before changing anything')
Invoke-Sql $db 'INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (?, ?, ?)' @('AbDryRunStop', '', ($sequence + 1))
Invoke-Com $db 'Commit' 'InvokeMethod' $null | Out-Null
$check = Get-Rows $db "SELECT ``Action`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveExistingProducts'"
if ($check.Count -gt 0) { throw 'The dry-run copy still removes earlier versions. Not running.' }

# Table data for the report.
$directories = @{}
foreach ($row in (Get-Rows $db 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`')) { $directories[$row[0]] = $row }
$componentDirs = @{}
$componentConditions = @{}
foreach ($row in (Get-Rows $db 'SELECT `Component`, `Directory_`, `Condition` FROM `Component`')) { $componentDirs[$row[0]] = $row[1]; $componentConditions[$row[0]] = $row[2] }
$filesByComponent = @{}
foreach ($row in (Get-Rows $db 'SELECT `Component_`, `FileName` FROM `File`')) {
    $long = ($row[1] -split '\|')[-1]
    if (-not $filesByComponent.ContainsKey($row[0])) { $filesByComponent[$row[0]] = @() }
    $filesByComponent[$row[0]] += $long
}
$removals = @()
if ((Get-Rows $db "SELECT ``Name`` FROM ``_Tables`` WHERE ``Name``='RemoveFile'").Count -gt 0) {
    foreach ($row in (Get-Rows $db 'SELECT `Component_`, `FileName`, `DirProperty`, `InstallMode` FROM `RemoveFile`')) {
        $removals += [pscustomobject]@{ Component = $row[0]; Name = ($row[1] -split '\|')[-1]; Dir = $row[2]; Mode = [int]$row[3] }
    }
}
[System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
$db = $null
[GC]::Collect(); [GC]::WaitForPendingFinalizers()

# ---------------------------------------------------------------------------- screens
if ($Screens) {
    & (Join-Path $PSScriptRoot 'Save-AdvToolsMsiPages.ps1') -Msi $copy -Folder $Screens -Properties $Properties -ExitPage:$ExitPage -ExitMode $ExitMode
    return
}

# ---------------------------------------------------------------------------- run
function Resolve-DirPath($props, [string]$dirId) {
    if ($props.ContainsKey($dirId)) { return $props[$dirId] }
    return "[$dirId]"
}

$runs = if ($Scenario.Count -gt 0) { $Scenario } else { , ($Properties -join ' ') }
$index = 0
foreach ($run in $runs) {
    $index++
    $runProperties = @(([string]$run) -split '\s+' | Where-Object { $_ })
    $log = Join-Path $WorkDirectory ('{0}-dryrun-{1}.log' -f [System.IO.Path]::GetFileNameWithoutExtension($Msi), $index)
    $arguments = @('/i', "`"$copy`"", '/qn', '/l*v', "`"$log`"") + $runProperties
    $process = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
    $lines = Get-Content $log

    $props = @{}
    $actions = @{}
    $reached = $false
    $errors = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line.StartsWith('Property(S): ')) {
            $eq = $line.IndexOf(' = ')
            if ($eq -gt 13) { $props[$line.Substring(13, $eq - 13)] = $line.Substring($eq + 3) }
        }
        elseif ($line.Contains('Component: ') -and $line -match 'Component: ([^;]+); Installed: ([^;]+);\s+Request: ([^;]+);\s+Action: ([^;]+?)(;|$)') {
            $actions[$Matches[1]] = $Matches[4].Trim()
        }
        elseif ($line.Contains('AB dry run: stopped before changing anything')) { $reached = $true }
        elseif ($line.StartsWith('MSI (s)') -and $line.Contains('Product: ') -and $line.Contains(' -- ')) {
            $errors.Add(($line -split ' -- ', 2)[1])
        }
    }

    $installs = New-Object System.Collections.Generic.List[string]
    $tidy = New-Object System.Collections.Generic.List[string]
    foreach ($component in $actions.Keys) {
        if ($actions[$component] -ne 'Local') { continue }
        if ($filesByComponent.ContainsKey($component)) {
            foreach ($name in $filesByComponent[$component]) { $installs.Add((Resolve-DirPath $props $componentDirs[$component]) + $name) }
        }
        foreach ($removal in ($removals | Where-Object { $_.Component -eq $component -and ($_.Mode -band 1) })) {
            $what = if ($removal.Name) { $removal.Name } else { '(the folder, if empty)' }
            $tidy.Add((Resolve-DirPath $props $removal.Dir) + $what)
        }
    }

    $result = [pscustomobject]@{
        Scenario = [string]$run
        ExitCode = $process.ExitCode
        ReachedEnd = $reached
        Errors = @($errors | Where-Object { $_ -notmatch 'AB dry run|Installation failed' })
        Properties = $props
        Install = @($installs | Sort-Object)
        Tidy = @($tidy | Sort-Object)
        ComponentActions = $actions
        Log = $log
    }

    if (-not $Quiet) {
        Write-Host ''
        Write-Host ("Dry run of {0}  {1}" -f [System.IO.Path]::GetFileName($Msi), $run) -ForegroundColor Cyan
        if ($reached) { Write-Host '  reached the point of changing the computer, and stopped there' -ForegroundColor Green }
        else { Write-Host "  stopped early (exit $($process.ExitCode)): $($result.Errors -join ' | ')" -ForegroundColor Yellow }
        $interesting = $props.Keys | Where-Object { $_ -match '^(AB_SCOPE|ALLUSERS|MSIINSTALLPERUSER|REVIT\d{4}|NW\w*\d{4}|AB_FOUND_\w+|AB_EARLIER_\w+|REMOVEEARLIER_\w+|AB_PREV\w*|WIX_UPGRADE_DETECTED|AGENT_\w+|AB_CUSTOM\w*)$' } | Sort-Object
        foreach ($key in $interesting) { Write-Host ("  {0,-28} {1}" -f $key, $props[$key]) }
        Write-Host ("  would install {0} file(s):" -f $installs.Count)
        $result.Install | Group-Object { Split-Path -Parent $_ } | ForEach-Object { Write-Host ("    {0}  ({1})" -f $_.Name, $_.Count) }
        if ($tidy.Count -gt 0) {
            Write-Host ("  would tidy {0} item(s) left by Setup.exe:" -f $tidy.Count)
            $result.Tidy | ForEach-Object { Write-Host "    $_" }
        }
    }

    $result
}