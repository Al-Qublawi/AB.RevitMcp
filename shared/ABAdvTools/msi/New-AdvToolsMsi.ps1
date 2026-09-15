<#
.SYNOPSIS
    Builds an AB add-in's Windows Installer package (.msi) from a short product definition and a
    folder of built files.

.DESCRIPTION
    Every AB add-in ships as an .msi built by this script, so all of them install, upgrade,
    detect earlier versions and uninstall the same way. The add-in repository supplies:

      installer\<Product>.msi.psd1   what the product is and where its files go (see the kit README)
      a payload folder               one subfolder per installable unit, filled by the repo's build
                                     script: Revit2026\, Navisworks2027\, Server\ ...

    and this script writes the WiX source, builds the package with the WiX CLI, and checks it.

    What every package does (details in the kit README, "Installers"):
      - Install for "Only me" or "Everyone" when the product allows both (one dual-purpose .msi).
      - A "Choose releases" page: one tick per Revit year / Navisworks release built, ticked by
        default according to the product's policy, remembered for the next upgrade.
      - Upgrades earlier .msi versions in place (same UpgradeCode), and finds copies installed by
        the retired AB Adv Tools Setup.exe, offering to remove them first.
      - Silent deployment through standard msiexec properties.

    NO CODE RUNS INSIDE THE PACKAGE. Company PCs with Microsoft Defender's attack surface reduction
    rule "Block executable files from running unless they meet a prevalence, age, or trusted list
    criteria" refuse to start unknown programs - which is what Setup.exe was. Windows Installer
    itself is trusted, so an .msi made only of tables installs fine. This script therefore fails the
    build if the package contains any custom action other than setting a property (51), setting a
    folder (35) or showing an error (19). Anything that needs code belongs in the add-in, which runs
    inside Revit or Navisworks.

    Requires the WiX CLI 5 and its UI extension:
        dotnet tool install --global wix --version 5.0.2
        wix extension add -g WixToolset.UI.wixext/5.0.2

.EXAMPLE
    & shared\ABAdvTools\msi\New-AdvToolsMsi.ps1 -Definition installer\SwitchBack.msi.psd1 -Payload obj\msi\payload -Version 1.3.1 -OutputDirectory dist
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Definition,
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$WorkDirectory,
    [switch]$KeepSource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$kitMsi = $PSScriptRoot
$kitRoot = Split-Path -Parent $kitMsi

function Fail([string]$message) { throw "New-AdvToolsMsi: $message" }

# ============================================================================ inputs

$Definition = (Resolve-Path $Definition).Path
$defDir = Split-Path -Parent $Definition
$def = Import-PowerShellDataFile $Definition
$Payload = (Resolve-Path $Payload).Path

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Version must be major.minor.patch, got '$Version'." }

function Get-Def([string]$key, $default) {
    if ($def.ContainsKey($key) -and $null -ne $def[$key] -and "$($def[$key])" -ne '') { return $def[$key] }
    return $default
}
function Need([string]$key) {
    $value = Get-Def $key $null
    if ($null -eq $value) { Fail "$Definition has no '$key'." }
    return $value
}
function Resolve-DefPath([string]$path) {
    if ([string]::IsNullOrEmpty($path)) { return $null }
    $full = if ([System.IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $defDir $path }
    if (-not (Test-Path $full)) { Fail "File not found: $full" }
    return (Resolve-Path $full).Path
}

$id = Need 'Id'
$name = Need 'Name'
$packageName = Get-Def 'ArpName' $name
$description = Need 'Description'
$repository = Need 'Repository'
$upgradeCode = ([guid](Need 'UpgradeCode')).ToString('B').ToUpperInvariant()
$scopeMode = Get-Def 'Scope' 'UserOrMachine'
$defaultScope = Get-Def 'DefaultScope' 'Machine'
$fileBase = Get-Def 'FileName' ($name -replace '\s', '.')
$icon = Resolve-DefPath (Need 'Icon')
$license = Resolve-DefPath (Get-Def 'License' $null)
$nextSteps = Need 'NextSteps'
$removedText = Get-Def 'RemovedText' "$packageName has been removed from this computer. Settings and logs are kept."
$revit = Get-Def 'RevitAddin' $null
$navisPlugin = Get-Def 'NavisworksPlugin' $null
$bundle = Get-Def 'NavisworksBundle' $null
$extraFolders = @(Get-Def 'ExtraFolders' @())
$removeOnUninstall = @(Get-Def 'RemoveOnUninstall' @())
$fragments = @(Get-Def 'Fragments' @() | ForEach-Object { Resolve-DefPath $_ })
$optionDialogs = @(Get-Def 'OptionDialogs' @())
$componentGroups = @(Get-Def 'ComponentGroups' @())

if ($scopeMode -notin 'User', 'UserOrMachine') { Fail "Scope must be 'User' or 'UserOrMachine'." }
if ($defaultScope -notin 'User', 'Machine') { Fail "DefaultScope must be 'User' or 'Machine'." }
$dual = $scopeMode -eq 'UserOrMachine'
if (-not $dual) { $defaultScope = 'User' }
if ($navisPlugin -and -not $dual) { Fail 'A NavisworksPlugin goes into Program Files, so the product must allow Scope = UserOrMachine.' }
if ($bundle -and $navisPlugin) { Fail 'A product has either a NavisworksBundle or a NavisworksPlugin, not both.' }
foreach ($extra in $extraFolders) {
    if ($dual -or $extra.Root -ne 'LocalAppData') { Fail 'ExtraFolders are supported for Scope = User products, under LocalAppData.' }
}

if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "ABAdvToolsMsi-$id" }
if (Test-Path $WorkDirectory) { Remove-Item $WorkDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkDirectory, $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$wixExe = (Get-Command wix -ErrorAction SilentlyContinue)
$wixExe = if ($wixExe) { $wixExe.Source } else { Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe' }
if (-not (Test-Path $wixExe)) { Fail 'The WiX CLI was not found. Install it: dotnet tool install --global wix --version 5.0.2' }

# ============================================================================ helpers

$md5 = [System.Security.Cryptography.MD5]::Create()
function Get-Hash([string]$seed, [int]$chars = 16) {
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("$upgradeCode|$seed"))
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join '').Substring(0, $chars)
}
function Get-StableGuid([string]$seed) {
    # Name-based, so a component keeps its GUID from one build to the next.
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("$upgradeCode|guid|$seed"))
    $bytes[7] = ($bytes[7] -band 0x0F) -bor 0x30
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return (New-Object System.Guid (, $bytes)).ToString('B').ToUpperInvariant()
}
function X([string]$text) { return [System.Security.SecurityElement]::Escape($text) }
function Write-Utf8([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Join-Rel([string]$a, [string]$b) {
    if ([string]::IsNullOrEmpty($b)) { return $a }
    if ([string]::IsNullOrEmpty($a)) { return $b }
    return "$a\$b"
}
function Get-YearRange([int[]]$years) {
    if ($years.Count -eq 0) { return '' }
    $sorted = @($years | Sort-Object)
    if ($sorted.Count -eq 1) { return "$($sorted[0])" }
    return "$($sorted[0])-$($sorted[-1])"
}

$MACHINE = if ($dual) { 'ALLUSERS=1 OR (ALLUSERS=2 AND MSIINSTALLPERUSER<>"1")' } else { '0' }
$GUARD = 'NOT AB_UIRAN'   # defaults: always in the UI; in the install itself only when no UI ran

# ---------------------------------------------------------------------------- folders
# Folder trees are collected as nested nodes and written out at the end. Roots:
#   REVIT / PLUGINS  set at install time to ProgramData or AppData, by scope
#   LOCAL / CM / CU / PF  fixed standard folders (CM, CU and PF only for tidying earlier copies)
#   NW:<tag>         a Navisworks install folder, read from the registry at install time

$roots = [ordered]@{}
function Add-Root([string]$key, [string]$kind, [string]$dirId, [string]$dirName) {
    $roots[$key] = @{ Kind = $kind; Id = $dirId; Name = $dirName; Children = [ordered]@{} }
}
Add-Root 'REVIT' 'Target' 'AB_ROOT_REVITADDINS' 'ABRevitAddins'
Add-Root 'PLUGINS' 'Target' 'AB_ROOT_APPPLUGINS' 'ABApplicationPlugins'
Add-Root 'LOCAL' 'Standard' 'LocalAppDataFolder' $null
Add-Root 'CM' 'Standard' 'CommonAppDataFolder' $null
Add-Root 'CU' 'Standard' 'AppDataFolder' $null
Add-Root 'PF' 'Standard' 'ProgramFiles64Folder' $null

function Get-Dir([string]$rootKey, [string]$relative) {
    $node = $roots[$rootKey]
    if (-not $node) { Fail "Unknown folder root $rootKey." }
    if ([string]::IsNullOrEmpty($relative)) { return $node.Id }
    $path = ''
    foreach ($segment in ($relative -split '\\' | Where-Object { $_ })) {
        $path = if ($path) { "$path\$segment" } else { $segment }
        if (-not $node.Children.Contains($segment)) {
            $node.Children[$segment] = @{ Kind = 'Dir'; Id = 'd' + (Get-Hash "dir|$rootKey|$path" 14); Name = $segment; Children = [ordered]@{} }
        }
        $node = $node.Children[$segment]
    }
    return $node.Id
}

function Write-DirNode($node, [System.Text.StringBuilder]$out, [string]$indent) {
    foreach ($child in $node.Children.Values) {
        if ($child.Children.Count -eq 0) {
            [void]$out.AppendLine("$indent<Directory Id=`"$($child.Id)`" Name=`"$(X $child.Name)`" />")
        }
        else {
            [void]$out.AppendLine("$indent<Directory Id=`"$($child.Id)`" Name=`"$(X $child.Name)`">")
            Write-DirNode $child $out "$indent  "
            [void]$out.AppendLine("$indent</Directory>")
        }
    }
}

# ---------------------------------------------------------------------------- collected XML

$components = New-Object System.Text.StringBuilder
$properties = New-Object System.Text.StringBuilder
$actions = New-Object System.Text.StringBuilder
$fileCount = 0
$lastAction = 'AppSearch'

function Add-Component([string]$dirId, [string]$source, [string]$fileName, [string]$condition, [string]$seed, [string]$shortName) {
    $script:fileCount++
    $cid = 'c' + (Get-Hash "cmp|$seed" 20)
    $fid = 'f' + (Get-Hash "fil|$seed" 20)
    $guid = Get-StableGuid "cmp|$seed"
    $cond = if ($condition) { " Condition=`"$(X $condition)`"" } else { '' }
    [void]$components.AppendLine("    <Component Id=`"$cid`" Directory=`"$dirId`" Guid=`"$guid`" Bitness=`"always64`"$cond>")
    $short = if ($shortName) { " ShortName=`"$shortName`"" } else { '' }
    [void]$components.AppendLine("      <File Id=`"$fid`" Name=`"$(X $fileName)`"$short Source=`"$(X $source)`" KeyPath=`"yes`" />")
    [void]$components.AppendLine('    </Component>')
}

function Add-Property([string]$propertyId, [string]$value, [switch]$Secure, [string]$inner) {
    $valueAttr = if ($null -ne $value -and $value -ne '') { " Value=`"$(X $value)`"" } else { '' }
    $secureAttr = if ($Secure) { ' Secure="yes"' } else { '' }
    if ($inner) {
        [void]$properties.AppendLine("    <Property Id=`"$propertyId`"$valueAttr$secureAttr>")
        [void]$properties.Append($inner)
        [void]$properties.AppendLine('    </Property>')
    }
    else {
        [void]$properties.AppendLine("    <Property Id=`"$propertyId`"$valueAttr$secureAttr />")
    }
}

# Property-setting actions, run in the order added. Those for both sequences chain right after
# AppSearch; UI-only ones chain after LaunchConditions, so a "both" action never has to follow an
# action the install sequence does not have.
$uiActions = New-Object System.Text.StringBuilder
$lastUiAction = 'LaunchConditions'
function Add-SetProperty([string]$propertyId, [string]$value, [string]$condition, [string]$sequence = 'both') {
    $action = 'AbSet' + (Get-Hash "set|$propertyId|$value|$condition|$sequence" 16)
    if ($sequence -eq 'ui') {
        [void]$uiActions.AppendLine("    <SetProperty Id=`"$propertyId`" Action=`"$action`" Value=`"$(X $value)`" After=`"$script:lastUiAction`" Sequence=`"ui`" Condition=`"$(X $condition)`" />")
        $script:lastUiAction = $action
        return
    }
    [void]$actions.AppendLine("    <SetProperty Id=`"$propertyId`" Action=`"$action`" Value=`"$(X $value)`" After=`"$script:lastAction`" Sequence=`"$sequence`" Condition=`"$(X $condition)`" />")
    $script:lastAction = $action
}

function Get-FilesUnder([string]$folder) {
    $prefix = (Resolve-Path $folder).Path.TrimEnd('\') + '\'
    return @(Get-ChildItem $folder -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Full = $_.FullName; Relative = $_.FullName.Substring($prefix.Length); Name = $_.Name
                           Folder = Split-Path -Parent $_.FullName.Substring($prefix.Length) }
    })
}
function Get-SubfoldersUnder([string]$folder) {
    $prefix = (Resolve-Path $folder).Path.TrimEnd('\') + '\'
    return @(Get-ChildItem $folder -Recurse -Directory | ForEach-Object { $_.FullName.Substring($prefix.Length) } | Sort-Object -Descending)
}

# ---------------------------------------------------------------------------- tidying earlier Setup.exe copies
# Removal lists per context. Each entry: @{ Root; Folder; Name } (Name '*' = every file in Folder)
# or @{ Root; Folder; RemoveFolder = $true }.

$cleanMachine = New-Object System.Collections.Generic.List[object]
$cleanUser = New-Object System.Collections.Generic.List[object]
$cleanNavisworks = New-Object System.Collections.Generic.List[object]

function Add-CleanTree([System.Collections.Generic.List[object]]$list, [string]$root, [string]$folder, [string[]]$subfolders) {
    foreach ($sub in $subfolders) {
        $list.Add(@{ Root = $root; Folder = "$folder\$sub"; Name = '*' })
        $list.Add(@{ Root = $root; Folder = "$folder\$sub"; RemoveFolder = $true })
    }
    $list.Add(@{ Root = $root; Folder = $folder; Name = '*' })
    $list.Add(@{ Root = $root; Folder = $folder; RemoveFolder = $true })
}

# ---------------------------------------------------------------------------- releases

$releases = New-Object System.Collections.Generic.List[object]
$units = @(Get-ChildItem $Payload -Directory | Select-Object -ExpandProperty Name)
$revitYears = @($units | Where-Object { $_ -match '^Revit\d{4}$' } | ForEach-Object { [int]$_.Substring(5) } | Sort-Object)
$navisYears = @($units | Where-Object { $_ -match '^Navisworks\d{4}$' } | ForEach-Object { [int]$_.Substring(10) } | Sort-Object)

if ($revit -and $revitYears.Count -eq 0) { Fail "RevitAddin is defined but $Payload has no Revit<year> folder." }
if (($bundle -or $navisPlugin) -and $navisYears.Count -eq 0) { Fail "A Navisworks layout is defined but $Payload has no Navisworks<year> folder." }

$revitLanguages = '0409', '0809', '0407', '040C', '0410', '0C0A', '040A', '0411', '0412', '0804', '0404', '0405', '0415', '0416', '0419', '040E'

function Add-RevitDetection([int]$year) {
    $found = "AB_FOUND_REVIT$year"
    $inner = New-Object System.Text.StringBuilder
    $keys = @("SOFTWARE\Autodesk\Revit\$year\REVIT-05") + ($revitLanguages | ForEach-Object { "SOFTWARE\Autodesk\Revit\$year\REVIT-05:$_" })
    foreach ($key in $keys) {
        $h = Get-Hash "rvt|$year|$key" 12
        [void]$inner.AppendLine("      <RegistrySearch Id=`"rs$h`" Root=`"HKLM`" Key=`"$(X $key)`" Name=`"InstallationLocation`" Type=`"directory`" Bitness=`"always64`">")
        [void]$inner.AppendLine("        <FileSearch Id=`"fs$h`" Name=`"RevitAPI.dll`" />")
        [void]$inner.AppendLine('      </RegistrySearch>')
    }
    # The real Program Files: in an "Only me" install [ProgramFiles64Folder] means %LOCALAPPDATA%\Programs.
    $h = Get-Hash "rvtpf|$year" 12
    [void]$inner.AppendLine("      <DirectorySearch Id=`"ds$h`" Path=`"[%ProgramW6432]\Autodesk\Revit $year`" Depth=`"0`">")
    [void]$inner.AppendLine("        <FileSearch Id=`"fs$h`" Name=`"RevitAPI.dll`" />")
    [void]$inner.AppendLine('      </DirectorySearch>')
    Add-Property $found $null -inner $inner.ToString()
    return $found
}

function Add-NavisworksDetection([string]$edition, [int]$year, [switch]$WithLocation) {
    $tag = "NW$($edition.ToUpperInvariant())$year"
    $major = $year - 2003
    $key = "SOFTWARE\Autodesk\Navisworks $edition\$major.0\Location"
    $inner = New-Object System.Text.StringBuilder
    $h = Get-Hash "nw|$tag" 12
    [void]$inner.AppendLine("      <RegistrySearch Id=`"rs$h`" Root=`"HKLM`" Key=`"$(X $key)`" Name=`"Path`" Type=`"directory`" Bitness=`"always64`">")
    [void]$inner.AppendLine("        <FileSearch Id=`"fs$h`" Name=`"Autodesk.Navisworks.Api.dll`" />")
    [void]$inner.AppendLine('      </RegistrySearch>')
    $h2 = Get-Hash "nwpf|$tag" 12
    [void]$inner.AppendLine("      <DirectorySearch Id=`"ds$h2`" Path=`"[%ProgramW6432]\Autodesk\Navisworks $edition $year`" Depth=`"0`">")
    [void]$inner.AppendLine("        <FileSearch Id=`"fs$h2`" Name=`"Autodesk.Navisworks.Api.dll`" />")
    [void]$inner.AppendLine('      </DirectorySearch>')
    Add-Property "AB_FOUND_$tag" $null -inner $inner.ToString()

    if ($WithLocation) {
        $h3 = Get-Hash "nwloc|$tag" 12
        Add-Property "AB_NWLOC_$tag" $null -inner "      <RegistrySearch Id=`"rs$h3`" Root=`"HKLM`" Key=`"$(X $key)`" Name=`"Path`" Type=`"directory`" Bitness=`"always64`" />`r`n"
    }
    return $tag
}

# ---- Revit add-in: Addins\<year>\<manifest> + Addins\<year>\<FolderName>\...
if ($revit) {
    foreach ($k in 'ManifestFileName', 'FolderName', 'AssemblyFileName', 'AddInName', 'FullClassName', 'AddInId', 'VendorId', 'VendorDescription') {
        if (-not $revit.ContainsKey($k)) { Fail "RevitAddin has no '$k'." }
    }
    $policy = if ($revit.ContainsKey('Releases')) { $revit.Releases } else { 'Detected' }
    if ($policy -notin 'AllBuilt', 'Detected') { Fail "RevitAddin.Releases must be AllBuilt or Detected." }

    # Relative assembly path, resolved by Revit against the manifest's folder. UTF-8 without a BOM:
    # some Revit releases ignore a manifest that has one.
    $manifest = Join-Path $WorkDirectory "manifest\$($revit.ManifestFileName)"
    Write-Utf8 $manifest ("<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n" +
        "<!-- Installed by the $(X $revit.AddInName) installer (AB Adv Tools). -->`r`n" +
        "<RevitAddIns>`r`n" +
        "  <AddIn Type=`"Application`">`r`n" +
        "    <Name>$(X $revit.AddInName)</Name>`r`n" +
        "    <Assembly>$(X ($revit.FolderName + '\' + $revit.AssemblyFileName))</Assembly>`r`n" +
        "    <AddInId>$($revit.AddInId)</AddInId>`r`n" +
        "    <FullClassName>$(X $revit.FullClassName)</FullClassName>`r`n" +
        "    <VendorId>$(X $revit.VendorId)</VendorId>`r`n" +
        "    <VendorDescription>$(X $revit.VendorDescription)</VendorDescription>`r`n" +
        "  </AddIn>`r`n" +
        "</RevitAddIns>`r`n")

    foreach ($year in $revitYears) {
        $unit = Join-Path $Payload "Revit$year"
        if (-not (Test-Path (Join-Path $unit $revit.AssemblyFileName))) { Fail "$unit has no $($revit.AssemblyFileName)." }
        $prop = "REVIT$year"
        $found = Add-RevitDetection $year

        Add-Component (Get-Dir 'REVIT' "$year") $manifest $revit.ManifestFileName "$prop=`"1`"" "revit|$year|manifest"
        foreach ($file in (Get-FilesUnder $unit)) {
            $dir = Get-Dir 'REVIT' (Join-Rel "$year\$($revit.FolderName)" $file.Folder)
            Add-Component $dir $file.Full $file.Name "$prop=`"1`"" "revit|$year|$($file.Relative)"
        }

        $subs = Get-SubfoldersUnder $unit
        foreach ($target in @(@{ List = $cleanUser; Root = 'CU' }, @{ List = $cleanMachine; Root = 'CM' })) {
            if ($target.Root -eq 'CM' -and -not $dual) { continue }
            $target.List.Add(@{ Root = $target.Root; Folder = "Autodesk\Revit\Addins\$year"; Name = $revit.ManifestFileName })
            Add-CleanTree $target.List $target.Root "Autodesk\Revit\Addins\$year\$($revit.FolderName)" $subs
        }

        $releases.Add([pscustomobject]@{ Prop = $prop; Group = 'Revit'; Label = "Revit $year"; Found = $found
                                         Policy = $policy; NeedsMachine = $false })
    }
}

# ---- Navisworks plugin folder: <install>\Plugins\<FolderName>\...  (everyone only)
if ($navisPlugin) {
    $editions = @($navisPlugin.Editions)
    foreach ($year in $navisYears) {
        $unit = Join-Path $Payload "Navisworks$year"
        foreach ($edition in $editions) {
            $tag = Add-NavisworksDetection $edition $year -WithLocation
            $dirId = "AB_DIR_$tag"
            Add-Root "NW:$tag" 'Target' $dirId "AB$tag"
            [void]$actions.AppendLine("    <SetDirectory Id=`"$dirId`" Action=`"AbDir$tag`" Value=`"[AB_NWLOC_$tag]`" Sequence=`"both`" Condition=`"AB_NWLOC_$tag`" />")
            [void]$actions.AppendLine("    <SetDirectory Id=`"$dirId`" Action=`"AbDirPf$tag`" Value=`"[%ProgramW6432]\Autodesk\Navisworks $edition $year\`" Sequence=`"both`" Condition=`"NOT AB_NWLOC_$tag`" />")

            $condition = "$tag=`"1`" AND AB_FOUND_$tag AND ($MACHINE)"
            foreach ($file in (Get-FilesUnder $unit)) {
                $dir = Get-Dir "NW:$tag" (Join-Rel "Plugins\$($navisPlugin.FolderName)" $file.Folder)
                Add-Component $dir $file.Full $file.Name $condition "nwplugin|$tag|$($file.Relative)"
            }
            Add-CleanTree $cleanNavisworks "NW:$tag" "Plugins\$($navisPlugin.FolderName)" (Get-SubfoldersUnder $unit)

            $releases.Add([pscustomobject]@{ Prop = $tag; Group = 'Navisworks'; Label = "$edition $year"; Found = "AB_FOUND_$tag"
                                             Policy = 'Detected'; NeedsMachine = $true })
        }
    }
}

# ---- Navisworks application bundle: ApplicationPlugins\<Bundle>\PackageContents.xml + Contents\<year>\...
if ($bundle) {
    foreach ($k in 'BundleName', 'ModuleFileName', 'AppName', 'Description', 'ProductCode', 'UpgradeCode', 'Editions') {
        if (-not $bundle.ContainsKey($k)) { Fail "NavisworksBundle has no '$k'." }
    }
    $policy = if ($bundle.ContainsKey('Releases')) { $bundle.Releases } else { 'Detected' }
    if ($navisYears.Count -gt 8) { Fail 'A bundle with more than 8 Navisworks releases needs a different PackageContents strategy.' }
    $platforms = (@($bundle.Editions) | ForEach-Object { if ($_ -eq 'Simulate') { 'NAVSIM' } else { 'NAVMAN' } }) -join '|'

    foreach ($year in $navisYears) {
        $unit = Join-Path $Payload "Navisworks$year"
        if (-not (Test-Path (Join-Path $unit $bundle.ModuleFileName))) { Fail "$unit has no $($bundle.ModuleFileName)." }
        $prop = "NW$year"

        # The release counts as installed when any supported edition of it is.
        $inner = New-Object System.Text.StringBuilder
        foreach ($edition in @($bundle.Editions)) {
            $major = $year - 2003
            $key = "SOFTWARE\Autodesk\Navisworks $edition\$major.0\Location"
            $h = Get-Hash "nwb|$edition|$year" 12
            [void]$inner.AppendLine("      <RegistrySearch Id=`"rs$h`" Root=`"HKLM`" Key=`"$(X $key)`" Name=`"Path`" Type=`"directory`" Bitness=`"always64`">")
            [void]$inner.AppendLine("        <FileSearch Id=`"fs$h`" Name=`"Autodesk.Navisworks.Api.dll`" />")
            [void]$inner.AppendLine('      </RegistrySearch>')
            $h2 = Get-Hash "nwbpf|$edition|$year" 12
            [void]$inner.AppendLine("      <DirectorySearch Id=`"ds$h2`" Path=`"[%ProgramW6432]\Autodesk\Navisworks $edition $year`" Depth=`"0`">")
            [void]$inner.AppendLine("        <FileSearch Id=`"fs$h2`" Name=`"Autodesk.Navisworks.Api.dll`" />")
            [void]$inner.AppendLine('      </DirectorySearch>')
        }
        Add-Property "AB_FOUND_$prop" $null -inner $inner.ToString()

        foreach ($file in (Get-FilesUnder $unit)) {
            $dir = Get-Dir 'PLUGINS' (Join-Rel "$($bundle.BundleName)\Contents\$year" $file.Folder)
            Add-Component $dir $file.Full $file.Name "$prop=`"1`"" "bundle|$year|$($file.Relative)"
        }
        $subs = Get-SubfoldersUnder $unit
        foreach ($target in @(@{ List = $cleanUser; Root = 'CU' }, @{ List = $cleanMachine; Root = 'CM' })) {
            if ($target.Root -eq 'CM' -and -not $dual) { continue }
            Add-CleanTree $target.List $target.Root "Autodesk\ApplicationPlugins\$($bundle.BundleName)\Contents\$year" $subs
        }

        $releases.Add([pscustomobject]@{ Prop = $prop; Group = 'Navisworks'; Label = "Navisworks $year"; Found = "AB_FOUND_$prop"
                                         Policy = $policy; NeedsMachine = $false })
    }

    # Navisworks reads one PackageContents.xml listing the releases present, and Windows Installer
    # cannot edit XML without running code. So every combination of ticked releases gets its own
    # prepared file, and exactly one of them - the one matching the ticks - is installed.
    $n = $navisYears.Count
    for ($mask = 1; $mask -lt [Math]::Pow(2, $n); $mask++) {
        $chosen = @(); $conditions = @()
        for ($i = 0; $i -lt $n; $i++) {
            $y = $navisYears[$i]
            if ($mask -band [int][Math]::Pow(2, $i)) { $chosen += $y; $conditions += "NW$y=`"1`"" }
            else { $conditions += "NW$y<>`"1`"" }
        }

        $xml = New-Object System.Text.StringBuilder
        [void]$xml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
        [void]$xml.AppendLine("<!-- Installed by the $(X $bundle.AppName) installer (AB Adv Tools). -->")
        [void]$xml.AppendLine('<ApplicationPackage')
        [void]$xml.AppendLine('    SchemaVersion="1.0"')
        [void]$xml.AppendLine('    ProductType="Application"')
        [void]$xml.AppendLine("    Name=`"$(X $bundle.AppName)`"")
        [void]$xml.AppendLine("    AppVersion=`"$Version`"")
        [void]$xml.AppendLine("    FriendlyVersion=`"$Version`"")
        [void]$xml.AppendLine("    Description=`"$(X $bundle.Description)`"")
        [void]$xml.AppendLine('    Author="Abdullah Lotfy"')
        [void]$xml.AppendLine("    ProductCode=`"$($bundle.ProductCode)`"")
        [void]$xml.AppendLine("    UpgradeCode=`"$($bundle.UpgradeCode)`">")
        [void]$xml.AppendLine('')
        [void]$xml.AppendLine('  <CompanyDetails Name="Abdullah Lotfy" />')
        [void]$xml.AppendLine('')
        foreach ($y in $chosen) {
            $series = "Nw$($y - 2003)"
            [void]$xml.AppendLine("  <Components Description=`"$(X $bundle.AppName) for Navisworks $y`">")
            [void]$xml.AppendLine("    <RuntimeRequirements OS=`"Win64`" Platform=`"$platforms`" SeriesMin=`"$series`" SeriesMax=`"$series`" />")
            [void]$xml.AppendLine('    <ComponentEntry')
            [void]$xml.AppendLine("        AppName=`"$(X $bundle.AppName) $y`"")
            [void]$xml.AppendLine('        AppType="ManagedPlugin"')
            [void]$xml.AppendLine("        Version=`"$Version`"")
            [void]$xml.AppendLine("        ModuleName=`"./Contents/$y/$($bundle.ModuleFileName)`"")
            [void]$xml.AppendLine("        AppDescription=`"$(X $bundle.Description)`" />")
            [void]$xml.AppendLine('  </Components>')
            [void]$xml.AppendLine('')
        }
        [void]$xml.AppendLine('</ApplicationPackage>')

        $variant = Join-Path $WorkDirectory "packagecontents\$mask\PackageContents.xml"
        Write-Utf8 $variant $xml.ToString()
        # Only one variant is ever installed, so they may share a name; each gets its own 8.3 name.
        Add-Component (Get-Dir 'PLUGINS' $bundle.BundleName) $variant 'PackageContents.xml' ($conditions -join ' AND ') "bundle|packagecontents|$mask" ("PKG{0:D5}.XML" -f $mask)
    }

    foreach ($target in @(@{ List = $cleanUser; Root = 'CU' }, @{ List = $cleanMachine; Root = 'CM' })) {
        if ($target.Root -eq 'CM' -and -not $dual) { continue }
        $b = "Autodesk\ApplicationPlugins\$($bundle.BundleName)"
        $target.List.Add(@{ Root = $target.Root; Folder = $b; Name = 'PackageContents.xml' })
        $target.List.Add(@{ Root = $target.Root; Folder = "$b\Contents"; RemoveFolder = $true })
        $target.List.Add(@{ Root = $target.Root; Folder = $b; RemoveFolder = $true })
    }
}

# ---- extra folders (per-user products), e.g. the MCP server
foreach ($extra in $extraFolders) {
    $unit = Join-Path $Payload $extra.Payload
    if (-not (Test-Path $unit)) { Fail "ExtraFolders: $unit not found." }
    foreach ($file in (Get-FilesUnder $unit)) {
        Add-Component (Get-Dir 'LOCAL' (Join-Rel $extra.Path $file.Folder)) $file.Full $file.Name $null "extra|$($extra.Payload)|$($file.Relative)"
    }
    Add-CleanTree $cleanUser 'LOCAL' $extra.Path (Get-SubfoldersUnder $unit)
}

if ($releases.Count -eq 0) { Fail 'Nothing to install: no RevitAddin, NavisworksPlugin or NavisworksBundle.' }

# The retired Setup.exe kept a copy of itself for Apps and Features.
$cleanUser.Add(@{ Root = 'LOCAL'; Folder = "AB Adv Tools\Setup\$id"; Name = '*' })
$cleanUser.Add(@{ Root = 'LOCAL'; Folder = "AB Adv Tools\Setup\$id"; RemoveFolder = $true })
$cleanUser.Add(@{ Root = 'LOCAL'; Folder = 'AB Adv Tools\Setup'; RemoveFolder = $true })
if ($dual) {
    $cleanMachine.Add(@{ Root = 'PF'; Folder = "AB Adv Tools\$id"; Name = '*' })
    $cleanMachine.Add(@{ Root = 'PF'; Folder = "AB Adv Tools\$id"; RemoveFolder = $true })
    $cleanMachine.Add(@{ Root = 'PF'; Folder = 'AB Adv Tools'; RemoveFolder = $true })
}

# ============================================================================ properties and defaults

$years = @()
if ($revit) { $years += "Revit $(Get-YearRange $revitYears)" }
if ($navisPlugin) { $years += "Navisworks $((@($navisPlugin.Editions)) -join ' and ') $(Get-YearRange $navisYears)" }
if ($bundle) { $years += "Navisworks $((@($bundle.Editions)) -join ' and ') $(Get-YearRange $navisYears)" }
$hostSummary = Get-Def 'HostSummary' ("Autodesk " + ($years -join ' and '))

$repoUrl = "https://github.com/Al-Qublawi/$repository"
Add-Property 'AB_DESCRIPTION' $description
Add-Property 'AB_HOSTSUMMARY' $hostSummary
Add-Property 'AB_DISPLAYVERSION' $Version
Add-Property 'AB_NEXTSTEPS' $nextSteps
Add-Property 'AB_REMOVEDTEXT' $removedText
Add-Property 'AB_RELEASEURL' "$repoUrl/releases/tag/v$Version"
Add-Property 'AB_SCOPENOTE' (Get-Def 'ScopeNote' 'Everyone installs into ProgramData and needs administrator rights. Only me installs into your own profile.')
Add-Property 'ARPPRODUCTICON' 'ABProductIcon.ico'
Add-Property 'ARPURLINFOABOUT' $repoUrl
Add-Property 'ARPURLUPDATEINFO' "$repoUrl/releases/latest"
Add-Property 'ARPHELPLINK' 'https://www.linkedin.com/in/abdullahalqublawi/'
Add-Property 'ARPCONTACT' 'Abdullah Lotfy'
Add-Property 'ARPCOMMENTS' "AB Adv Tools - $description"
Add-Property 'ARPNOMODIFY' '1'
# A running Revit or Navisworks holds these files. Left on, the Restart Manager offers to close
# those applications, which risks a modeller losing unsaved work; off, Windows asks the user to
# close them instead.
Add-Property 'MSIRESTARTMANAGERCONTROL' 'Disable'
# Overwrite every file, whatever its date or version. Copies left by Setup.exe carry their
# original timestamps, and Windows Installer would otherwise keep them as "modified by the user".
Add-Property 'REINSTALLMODE' 'amus'
Add-Property 'AB_SCOPE' $(if ($defaultScope -eq 'Machine') { 'machine' } else { 'user' }) -Secure
Add-Property 'AB_UIRAN' $null -Secure
Add-Property 'ALLRELEASES' $null -Secure
Add-Property 'KEEPEARLIER' $null -Secure

# Earlier Setup.exe installs, through their Apps and Features entries.
$uninstallKey = "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ABAdvTools.$id"
$stateKey = "Software\AB Adv Tools\Installer\$id"
Add-Property 'AB_EARLIER_USER' $null -Secure -inner "      <RegistrySearch Id=`"rsEarlierUser`" Root=`"HKCU`" Key=`"$uninstallKey`" Name=`"DisplayVersion`" Type=`"raw`" />`r`n"
Add-Property 'REMOVEEARLIER_USER' $null -Secure
Add-Property 'AB_PREVMSI_USER' $null -Secure -inner "      <RegistrySearch Id=`"rsPrevUser`" Root=`"HKCU`" Key=`"$stateKey`" Name=`"Scope`" Type=`"raw`" />`r`n"
if ($dual) {
    Add-Property 'AB_EARLIER_MACHINE' $null -Secure -inner "      <RegistrySearch Id=`"rsEarlierMachine`" Root=`"HKLM`" Key=`"$uninstallKey`" Name=`"DisplayVersion`" Type=`"raw`" Bitness=`"always64`" />`r`n"
    Add-Property 'REMOVEEARLIER_MACHINE' $null -Secure
    Add-Property 'AB_PREVMSI_MACHINE' $null -Secure -inner "      <RegistrySearch Id=`"rsPrevMachine`" Root=`"HKLM`" Key=`"$stateKey`" Name=`"Scope`" Type=`"raw`" Bitness=`"always64`" />`r`n"
}

foreach ($release in $releases) {
    $prev = "AB_PREV_$($release.Prop)"
    $inner = "      <RegistrySearch Id=`"rsPrevU$($release.Prop)`" Root=`"HKCU`" Key=`"$stateKey`" Name=`"$($release.Prop)`" Type=`"raw`" />`r`n"
    if ($dual) {
        $inner += "      <RegistrySearch Id=`"rsPrevM$($release.Prop)`" Root=`"HKLM`" Key=`"$stateKey`" Name=`"$($release.Prop)`" Type=`"raw`" Bitness=`"always64`" />`r`n"
    }
    Add-Property $prev $null -inner $inner
    Add-Property $release.Prop $null -Secure
}

# ---- defaults, in order. Each runs in the UI; in the install itself only when no UI ran, so a
#      choice made on a page is never overwritten.
if ($dual) {
    Add-SetProperty 'AB_SCOPE' 'machine' "$GUARD AND ($MACHINE)"
    Add-SetProperty 'AB_SCOPE' 'user' "$GUARD AND NOT ($MACHINE)"
    # A Setup.exe copy for everyone can only be removed by an install for everyone: suggest it.
    Add-SetProperty 'AB_SCOPE' 'machine' 'AB_EARLIER_MACHINE AND NOT AB_EARLIER_USER' 'ui'
    # An earlier .msi of this product decides the scope: Windows Installer only upgrades within one.
    Add-SetProperty 'AB_SCOPE' 'machine' 'AB_PREVMSI_MACHINE' 'ui'
    Add-SetProperty 'AB_SCOPE' 'user' 'AB_PREVMSI_USER AND NOT AB_PREVMSI_MACHINE' 'ui'
}

foreach ($release in $releases) {
    $p = $release.Prop
    $prev = "AB_PREV_$p"
    # Remembered from the last install; otherwise the product's policy; ALLRELEASES=1 ticks everything.
    $policyExpr = if ($release.Policy -eq 'AllBuilt') { "NOT $prev" } else { "(NOT $prev AND $($release.Found))" }
    $on = "ALLRELEASES=`"1`" OR $prev=`"x1`" OR $policyExpr"
    if ($release.NeedsMachine) { $on = "$($release.Found) AND ($on)" }
    Add-SetProperty $p '1' "$GUARD AND NOT $p AND ($on)"
    # A command-line value other than 1 (e.g. REVIT2026=0) means "leave it out"; show it unticked.
    Add-SetProperty $p '[AB_NOTHING]' "$p AND $p<>`"1`"" 'ui'
}

Add-SetProperty 'REMOVEEARLIER_USER' '1' "$GUARD AND AB_EARLIER_USER AND NOT REMOVEEARLIER_USER AND NOT KEEPEARLIER"
if ($dual) {
    Add-SetProperty 'REMOVEEARLIER_MACHINE' '1' "$GUARD AND AB_EARLIER_MACHINE AND NOT REMOVEEARLIER_MACHINE AND NOT KEEPEARLIER"
}
Add-SetProperty 'AB_DEFAULTSDONE' '1' '1'
# Product fragments schedule their own defaults After="AbDefaultsEnd".
$actions = New-Object System.Text.StringBuilder ($actions.ToString().Replace("Action=`"$lastAction`"", 'Action="AbDefaultsEnd"'))
$lastAction = 'AbDefaultsEnd'
[void]$actions.Append($uiActions.ToString())
[void]$actions.AppendLine("    <SetProperty Id=`"AB_UIRAN`" Action=`"AbUiRan`" Value=`"1`" After=`"$lastUiAction`" Sequence=`"ui`" />")

$revitSub = 'Autodesk\Revit\Addins\'
$pluginsSub = 'Autodesk\ApplicationPlugins\'
foreach ($r in @(@{ Id = 'AB_ROOT_REVITADDINS'; Sub = $revitSub }, @{ Id = 'AB_ROOT_APPPLUGINS'; Sub = $pluginsSub })) {
    if ($dual) {
        [void]$actions.AppendLine("    <SetDirectory Id=`"$($r.Id)`" Action=`"AbDirM$($r.Id)`" Value=`"[CommonAppDataFolder]$($r.Sub)`" Sequence=`"both`" Condition=`"$(X $MACHINE)`" />")
        [void]$actions.AppendLine("    <SetDirectory Id=`"$($r.Id)`" Action=`"AbDirU$($r.Id)`" Value=`"[AppDataFolder]$($r.Sub)`" Sequence=`"both`" Condition=`"$(X "NOT ($MACHINE)")`" />")
    }
    else {
        [void]$actions.AppendLine("    <SetDirectory Id=`"$($r.Id)`" Action=`"AbDirU$($r.Id)`" Value=`"[AppDataFolder]$($r.Sub)`" Sequence=`"both`" />")
    }
}

# Scope guard for silent installs: a product already installed in the other scope would otherwise
# end up installed twice (Windows Installer upgrades only within one scope).
$guards = New-Object System.Text.StringBuilder
if ($dual) {
    [void]$guards.AppendLine("    <CustomAction Id=`"AbInstalledForEveryone`" Error=`"$(X "$packageName is installed for everyone on this computer, so install this version for everyone too (ALLUSERS=1), or remove the installed one first.")`" />")
    [void]$guards.AppendLine("    <CustomAction Id=`"AbInstalledForUser`" Error=`"$(X "$packageName is installed for [LogonUser] only, so install this version for that user too (MSIINSTALLPERUSER=1), or remove the installed one first.")`" />")
    [void]$guards.AppendLine("    <CustomAction Id=`"AbEarlierForEveryone`" Error=`"$(X "$packageName was installed for everyone by the AB Adv Tools setup program. Install this version for everyone (ALLUSERS=1) so it can replace that copy, or add KEEPEARLIER=1 to keep both.")`" />")
    [void]$guards.AppendLine('    <InstallExecuteSequence>')
    [void]$guards.AppendLine("      <Custom Action=`"AbInstalledForEveryone`" After=`"AbDefaultsEnd`" Condition=`"$(X "NOT Installed AND AB_PREVMSI_MACHINE AND NOT ($MACHINE)")`" />")
    [void]$guards.AppendLine("      <Custom Action=`"AbInstalledForUser`" After=`"AbInstalledForEveryone`" Condition=`"$(X "NOT Installed AND AB_PREVMSI_USER AND NOT AB_PREVMSI_MACHINE AND ($MACHINE)")`" />")
    # Silent only (the pages explain it): an Only me install would leave that copy loading too.
    [void]$guards.AppendLine("      <Custom Action=`"AbEarlierForEveryone`" After=`"AbInstalledForUser`" Condition=`"$(X "NOT Installed AND NOT AB_UIRAN AND AB_EARLIER_MACHINE AND NOT KEEPEARLIER AND NOT ($MACHINE)")`" />")
    [void]$guards.AppendLine('    </InstallExecuteSequence>')
}

# ============================================================================ state and tidying components

$state = New-Object System.Text.StringBuilder
$stateGuid = Get-StableGuid 'state'
[void]$state.AppendLine("    <Component Id=`"AbInstallerState`" Directory=`"TARGETDIR`" Guid=`"$stateGuid`" Bitness=`"always64`">")
[void]$state.AppendLine("      <RegistryKey Root=`"HKMU`" Key=`"$stateKey`">")
[void]$state.AppendLine('        <RegistryValue Name="Version" Value="[ProductVersion]" Type="string" KeyPath="yes" />')
[void]$state.AppendLine('        <RegistryValue Name="Scope" Value="[AB_SCOPE]" Type="string" />')
foreach ($release in $releases) {
    # "x1" ticked, "x" not ticked: the x keeps an unticked release from reading as "never seen".
    [void]$state.AppendLine("        <RegistryValue Name=`"$($release.Prop)`" Value=`"x[$($release.Prop)]`" Type=`"string`" />")
}
[void]$state.AppendLine('      </RegistryKey>')
$n = 0
foreach ($item in $removeOnUninstall) {
    $n++
    $dir = Get-Dir 'LOCAL' $item.Path
    [void]$state.AppendLine("      <RemoveFile Id=`"AbUninstallFiles$n`" Directory=`"$dir`" Name=`"*`" On=`"uninstall`" />")
    [void]$state.AppendLine("      <RemoveFolder Id=`"AbUninstallFolder$n`" Directory=`"$dir`" On=`"uninstall`" />")
}
[void]$state.AppendLine('    </Component>')

function Write-CleanupComponent([string]$componentId, [string]$condition, [string]$keyRoot, $entries, [string]$arpRoot) {
    if ($entries.Count -eq 0 -and -not $arpRoot) { return }
    $guid = Get-StableGuid "cleanup|$componentId"
    $bitness = if ($keyRoot -eq 'HKLM') { ' Bitness="always64"' } else { '' }
    # Transitive, with NOT Installed: it acts once, on first install, and a later repair drops it
    # instead of deleting the product's own freshly repaired files.
    [void]$state.AppendLine("    <Component Id=`"$componentId`" Directory=`"TARGETDIR`" Guid=`"$guid`" Transitive=`"yes`"$bitness Condition=`"$(X $condition)`">")
    [void]$state.AppendLine("      <RegistryValue Root=`"$keyRoot`" Key=`"$stateKey`" Name=`"$componentId`" Value=`"removed`" Type=`"string`" KeyPath=`"yes`" />")
    $i = 0
    foreach ($entry in $entries) {
        $i++
        $dir = Get-Dir $entry.Root $entry.Folder
        $rid = 'r' + (Get-Hash "clean|$componentId|$i" 18)
        if ($entry.ContainsKey('RemoveFolder')) {
            [void]$state.AppendLine("      <RemoveFolder Id=`"$rid`" Directory=`"$dir`" On=`"install`" />")
        }
        else {
            [void]$state.AppendLine("      <RemoveFile Id=`"$rid`" Directory=`"$dir`" Name=`"$(X $entry.Name)`" On=`"install`" />")
        }
    }
    if ($arpRoot) {
        [void]$state.AppendLine("      <RemoveRegistryKey Id=`"AbArp$componentId`" Root=`"$arpRoot`" Key=`"$uninstallKey`" Action=`"removeOnInstall`" />")
    }
    [void]$state.AppendLine('    </Component>')
}

Write-CleanupComponent 'AbRemoveEarlierUser' 'NOT Installed AND AB_EARLIER_USER AND REMOVEEARLIER_USER="1"' 'HKCU' $cleanUser 'HKCU'
if ($dual) {
    Write-CleanupComponent 'AbRemoveEarlierMachine' "NOT Installed AND AB_EARLIER_MACHINE AND REMOVEEARLIER_MACHINE=`"1`" AND ($MACHINE)" 'HKLM' $cleanMachine 'HKLM'
}
if ($navisPlugin) {
    # Setup.exe put the Navisworks plugin in Program Files whichever scope it installed for.
    Write-CleanupComponent 'AbRemoveEarlierNavisworks' "NOT Installed AND ((AB_EARLIER_MACHINE AND REMOVEEARLIER_MACHINE=`"1`") OR (AB_EARLIER_USER AND REMOVEEARLIER_USER=`"1`")) AND ($MACHINE)" 'HKLM' $cleanNavisworks $null
}

# ============================================================================ the "Choose releases" page

$ui = New-Object System.Text.StringBuilder
$revitReleases = @($releases | Where-Object { $_.Group -eq 'Revit' })
$navisReleases = @($releases | Where-Object { $_.Group -eq 'Navisworks' })
$columns = @()
if ($revitReleases.Count -gt 0) { $columns += , @('Revit', $revitReleases) }
if ($navisReleases.Count -gt 0) { $columns += , @('Navisworks', $navisReleases) }
if (($revitReleases.Count -gt 8) -or ($navisReleases.Count -gt 8)) { Fail 'The releases page holds at most 8 releases per application.' }

[void]$ui.AppendLine('      <Dialog Id="ABReleasesDlg" Width="370" Height="270" Title="[ProductName] Setup">')
[void]$ui.AppendLine('        <Control Id="BannerBitmap" Type="Bitmap" X="0" Y="0" Width="370" Height="44" TabSkip="no" Text="WixUI_Bmp_Banner" />')
[void]$ui.AppendLine('        <Control Id="BannerLine" Type="Line" X="0" Y="44" Width="370" Height="0" />')
[void]$ui.AppendLine('        <Control Id="BottomLine" Type="Line" X="0" Y="234" Width="370" Height="0" />')
[void]$ui.AppendLine('        <Control Id="Title" Type="Text" X="15" Y="6" Width="300" Height="15" Transparent="yes" NoPrefix="yes" Text="{\WixUI_Font_Title}Choose releases" />')
[void]$ui.AppendLine('        <Control Id="Description" Type="Text" X="25" Y="23" Width="320" Height="15" Transparent="yes" NoPrefix="yes" Text="Tick the Autodesk releases to install [ProductName] into." />')

$columnWidth = if ($columns.Count -eq 2) { 165 } else { 330 }
$tickWidth = if ($columns.Count -eq 2) { 74 } else { 110 }
$col = 0
foreach ($column in $columns) {
    $x = 20 + $col * 175
    [void]$ui.AppendLine("        <Control Id=`"Head$($column[0])`" Type=`"Text`" X=`"$x`" Y=`"52`" Width=`"$columnWidth`" Height=`"12`" NoPrefix=`"yes`" Text=`"{\WixUI_Font_Emphasized}$($column[0])`" />")
    $row = 0
    foreach ($release in $column[1]) {
        $y = 68 + $row * 13
        $p = $release.Prop
        $disable = ''
        if ($release.NeedsMachine) {
            # Built in plain variables: quotes nested inside a PowerShell subexpression get lost.
            $disableWhen = 'NOT ' + $release.Found + ' OR AB_SCOPE <> "machine"'
            $enableWhen = $release.Found + ' AND AB_SCOPE = "machine"'
            $disable = ' DisableCondition="' + (X $disableWhen) + '" EnableCondition="' + (X $enableWhen) + '"'
        }
        [void]$ui.AppendLine("        <Control Id=`"Tick$p`" Type=`"CheckBox`" X=`"$x`" Y=`"$y`" Width=`"$tickWidth`" Height=`"12`" CheckBoxValue=`"1`" Property=`"$p`" Text=`"$(X $release.Label)`"$disable />")
        $notX = $x + $tickWidth + 2
        $notWidth = $columnWidth - $tickWidth - 2
        $missing = if ($release.Policy -eq 'AllBuilt' -and -not $release.NeedsMachine) { 'not installed yet' } else { 'not installed' }
        [void]$ui.AppendLine("        <Control Id=`"On$p`" Type=`"Text`" X=`"$notX`" Y=`"$($y + 2)`" Width=`"$notWidth`" Height=`"10`" NoPrefix=`"yes`" Hidden=`"yes`" ShowCondition=`"$($release.Found)`" Text=`"{\ABAdv_Font_Note}installed`" />")
        [void]$ui.AppendLine("        <Control Id=`"Off$p`" Type=`"Text`" X=`"$notX`" Y=`"$($y + 2)`" Width=`"$notWidth`" Height=`"10`" NoPrefix=`"yes`" Hidden=`"yes`" ShowCondition=`"NOT $($release.Found)`" Text=`"{\ABAdv_Font_Note}$missing`" />")
        $row++
    }
    $col++
}

$notes = @()
if (@($releases | Where-Object { $_.Policy -eq 'AllBuilt' -and -not $_.NeedsMachine }).Count -gt 0) {
    $notes += 'A release that is not installed yet can stay ticked: the add-in is then ready as soon as it is.'
}
if ($navisPlugin) {
    $notes += 'The Navisworks plugin goes into the Navisworks program folder, so it installs only for Everyone, and only where that Navisworks is installed.'
}
$notes += (Get-Def 'ReleasesNote' $null)
$noteText = (@($notes | Where-Object { $_ }) -join ' ')
if ($noteText) {
    [void]$ui.AppendLine("        <Control Id=`"Note`" Type=`"Text`" X=`"20`" Y=`"178`" Width=`"330`" Height=`"50`" NoPrefix=`"yes`" Text=`"$(X $noteText)`" />")
}
[void]$ui.AppendLine('        <Control Id="Back" Type="PushButton" X="180" Y="243" Width="56" Height="17" Text="&lt; &amp;Back" />')
[void]$ui.AppendLine('        <Control Id="Next" Type="PushButton" X="236" Y="243" Width="56" Height="17" Default="yes" Text="&amp;Next &gt;" />')
[void]$ui.AppendLine('        <Control Id="Cancel" Type="PushButton" X="304" Y="243" Width="56" Height="17" Cancel="yes" Text="Cancel">')
[void]$ui.AppendLine('          <Publish Event="SpawnDialog" Value="CancelDlg" />')
[void]$ui.AppendLine('        </Control>')
[void]$ui.AppendLine('      </Dialog>')

# ---- page order
$pages = @('ABWelcomeDlg')
if ($license) { $pages += 'ABLicenseDlg' }
if ($dual) { $pages += 'ABScopeDlg' }
$pages += 'ABReleasesDlg'
$pages += $optionDialogs
$pages += 'ABEarlierDlg'
$pages += 'VerifyReadyDlg'

$earlier = if ($dual) { 'AB_EARLIER_MACHINE OR AB_EARLIER_USER' } else { 'AB_EARLIER_USER' }
# A page condition holds at most 255 characters, so "is anything ticked?" is worked out one release
# at a time into AB_ANYRELEASE when Next is pressed, and the page change tests that.
$anyRelease = 'AB_ANYRELEASE'

$flow = New-Object System.Text.StringBuilder
function Publish([string]$dialog, [string]$control, [string]$event, [string]$value, [string]$condition, [int]$order) {
    $cond = if ($condition) { " Condition=`"$(X $condition)`"" } else { '' }
    [void]$flow.AppendLine("      <Publish Dialog=`"$dialog`" Control=`"$control`" Event=`"$event`" Value=`"$(X $value)`"$cond Order=`"$order`" />")
}
function PublishProperty([string]$dialog, [string]$control, [string]$property, [string]$value, [string]$condition, [int]$order) {
    $cond = if ($condition) { " Condition=`"$(X $condition)`"" } else { '' }
    [void]$flow.AppendLine("      <Publish Dialog=`"$dialog`" Control=`"$control`" Property=`"$property`" Value=`"$(X $value)`"$cond Order=`"$order`" />")
}

for ($i = 0; $i -lt $pages.Count - 1; $i++) {
    $from = $pages[$i]
    $to = $pages[$i + 1]
    $order = 10

    if ($from -eq 'ABScopeDlg') {
        PublishProperty $from 'Next' 'ALLUSERS' '2' $null 1
        PublishProperty $from 'Next' 'MSIINSTALLPERUSER' '1' 'AB_SCOPE="user"' 2
        PublishProperty $from 'Next' 'MSIINSTALLPERUSER' '{}' 'AB_SCOPE="machine"' 3
    }
    if ($from -eq 'ABReleasesDlg') {
        PublishProperty $from 'Next' 'AB_ANYRELEASE' '{}' $null 1
        $o = 2
        foreach ($release in $releases) {
            $ticked = if ($release.NeedsMachine) { $release.Prop + '="1" AND ' + $release.Found + ' AND AB_SCOPE="machine"' } else { $release.Prop + '="1"' }
            PublishProperty $from 'Next' 'AB_ANYRELEASE' '1' $ticked $o
            $o++
        }
        Publish $from 'Next' 'SpawnDialog' 'ABNoReleaseDlg' 'NOT AB_ANYRELEASE' $o
        $order = $o + 1
        $gate = $anyRelease
    }
    else { $gate = $null }

    function Join-Condition([string]$a, [string]$b) {
        if (-not $a) { return $b }
        if (-not $b) { return $a }
        return "($a) AND ($b)"
    }

    if ($to -eq 'ABEarlierDlg') {
        $after = $pages[$i + 2]
        Publish $from 'Next' 'NewDialog' 'ABEarlierDlg' (Join-Condition $gate $earlier) $order
        Publish $from 'Next' 'NewDialog' $after (Join-Condition $gate "NOT ($earlier)") ($order + 1)
        Publish 'ABEarlierDlg' 'Back' 'NewDialog' $from $null 1
        Publish $after 'Back' 'NewDialog' 'ABEarlierDlg' "NOT Installed AND ($earlier)" 1
        Publish $after 'Back' 'NewDialog' $from "NOT Installed AND NOT ($earlier)" 2
    }
    elseif ($from -eq 'ABEarlierDlg') {
        Publish $from 'Next' 'NewDialog' $to $null $order
    }
    else {
        Publish $from 'Next' 'NewDialog' $to (Join-Condition $gate '1') $order
        $backCondition = if ($to -eq 'VerifyReadyDlg') { 'NOT Installed' } else { $null }
        Publish $to 'Back' 'NewDialog' $from $backCondition 1
    }
}

# ============================================================================ write and build

$licenseXml = if ($license) { "    <WixVariable Id=`"ABAdvToolsLicenseRtf`" Value=`"$(X $license)`" />`r`n" } else { '' }
$groupRefs = (@($componentGroups | ForEach-Object { "      <ComponentGroupRef Id=`"$_`" />" }) -join "`r`n")

$dirs = New-Object System.Text.StringBuilder
[void]$dirs.AppendLine('    <StandardDirectory Id="TARGETDIR">')
foreach ($key in $roots.Keys) {
    $root = $roots[$key]
    if ($root.Kind -ne 'Target') { continue }
    [void]$dirs.AppendLine("      <Directory Id=`"$($root.Id)`" Name=`"$($root.Name)`">")
    Write-DirNode $root $dirs '        '
    [void]$dirs.AppendLine('      </Directory>')
}
[void]$dirs.AppendLine('    </StandardDirectory>')
foreach ($key in $roots.Keys) {
    $root = $roots[$key]
    if ($root.Kind -ne 'Standard' -or $root.Children.Count -eq 0) { continue }
    [void]$dirs.AppendLine("    <StandardDirectory Id=`"$($root.Id)`">")
    Write-DirNode $root $dirs '      '
    [void]$dirs.AppendLine('    </StandardDirectory>')
}

$scopeAttr = if ($dual) { 'perUserOrMachine' } else { 'perUser' }
$msiVersion = "$Version.0"

$wxs = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by AB Adv Tools kit msi\New-AdvToolsMsi.ps1 from $(X (Split-Path -Leaf $Definition)). Do not edit. -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">
  <Package Name="$(X $packageName)" Manufacturer="Abdullah Lotfy" Version="$msiVersion" UpgradeCode="$upgradeCode"
           Scope="$scopeAttr" Compressed="yes" InstallerVersion="500" Language="1033">

    <SummaryInformation Description="$(X "$packageName - AB Adv Tools")" Manufacturer="Abdullah Lotfy" />
    <MajorUpgrade AllowSameVersionUpgrades="yes" IgnoreLanguage="yes" DowngradeErrorMessage="$(X "A newer version of $packageName is already installed.")" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />

    <Icon Id="ABProductIcon.ico" SourceFile="$(X $icon)" />
    <WixVariable Id="WixUIBannerBmp" Value="$(X (Join-Path $kitRoot 'assets\msi_banner.bmp'))" />
    <WixVariable Id="WixUIDialogBmp" Value="$(X (Join-Path $kitRoot 'assets\msi_dialog.bmp'))" />
$licenseXml
$($properties.ToString())
$($actions.ToString())
$($guards.ToString())
$($dirs.ToString())
    <ComponentGroup Id="AbFiles">
$($components.ToString())
$($state.ToString())
    </ComponentGroup>

    <Feature Id="Main" Title="$(X $packageName)" Level="1" AllowAbsent="no" AllowAdvertise="no">
      <ComponentGroupRef Id="AbFiles" />
$groupRefs
    </Feature>

    <UI Id="ABAdvTools_Product">
      <UIRef Id="ABAdvTools_Common" />
$(if ($license) { '      <DialogRef Id="ABLicenseDlg" />' })
$(if ($dual) { '      <DialogRef Id="ABScopeDlg" />' })
      <DialogRef Id="ABEarlierDlg" />
$($ui.ToString())
$($flow.ToString())
    </UI>
  </Package>
</Wix>
"@

$wxsPath = Join-Path $WorkDirectory "$id.wxs"
Write-Utf8 $wxsPath $wxs

$msiName = "$fileBase-$Version.msi"
$msiPath = Join-Path $OutputDirectory $msiName
if (Test-Path $msiPath) { Remove-Item $msiPath -Force }

$sources = @($wxsPath, (Join-Path $kitMsi 'AdvToolsUI.wxs')) + $fragments
Write-Host "  building $msiName ($fileCount files, $($releases.Count) releases)" -ForegroundColor Cyan
$wixArgs = @('build') + $sources + @('-ext', 'WixToolset.UI.wixext', '-arch', 'x64', '-o', $msiPath,
                                    '-intermediateFolder', (Join-Path $WorkDirectory 'wixobj'), '-nologo')
# Not 'Stop' here: Windows PowerShell 5.1 turns a native program's stderr into a terminating error.
$ErrorActionPreference = 'Continue'
$output = & $wixExe @wixArgs 2>&1
$exit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$output | ForEach-Object { Write-Host "    $_" }
if ($exit -ne 0) { Fail "wix build failed ($exit). Source kept at $wxsPath." }
Remove-Item (Join-Path $OutputDirectory '*.wixpdb') -Force -ErrorAction SilentlyContinue

# ============================================================================ post-build: default scope, then checks

$installer = New-Object -ComObject WindowsInstaller.Installer
function Invoke-Com($target, [string]$member, [string]$kind, [object[]]$arguments) {
    # Late-bound COM rejects PowerShell's wrapped values (Join-Path output, for one): unwrap them.
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
function Open-Package([string]$path, [int]$mode) {
    # Antivirus scans a freshly written package and briefly holds it; opening it for update then
    # fails with a sharing violation. Give the scan a few seconds.
    for ($attempt = 1; ; $attempt++) {
        try { return Invoke-Com $installer 'OpenDatabase' 'InvokeMethod' @($path, $mode) }
        catch {
            if ($attempt -ge 30) { throw }
            [System.Threading.Thread]::Sleep(500)
        }
    }
}
function Get-Rows($database, [string]$sql) {
    $view = Invoke-Com $database 'OpenView' 'InvokeMethod' @($sql)
    Invoke-Com $view 'Execute' 'InvokeMethod' $null | Out-Null
    $columns = Invoke-Com (Invoke-Com $view 'ColumnInfo' 'GetProperty' @(0)) 'FieldCount' 'GetProperty' $null
    $rows = @()
    while ($true) {
        $record = Invoke-Com $view 'Fetch' 'InvokeMethod' $null
        if ($null -eq $record) { break }
        $rows += , @(for ($c = 1; $c -le $columns; $c++) { Invoke-Com $record 'StringData' 'GetProperty' @($c) })
    }
    Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
    return $rows
}
function Test-Table($database, [string]$table) {
    $rows = Get-Rows $database "SELECT ``Name`` FROM ``_Tables`` WHERE ``Name``='$table'"
    return $rows.Count -gt 0
}

if ($dual -and $defaultScope -eq 'Machine') {
    # WiX's perUserOrMachine defaults to "Only me". Without MSIINSTALLPERUSER the default - and a
    # plain silent install - is Everyone, as the product's earlier installers did.
    $db = Open-Package $msiPath 1
    $view = Invoke-Com $db 'OpenView' 'InvokeMethod' @("DELETE FROM ``Property`` WHERE ``Property``='MSIINSTALLPERUSER'")
    Invoke-Com $view 'Execute' 'InvokeMethod' $null | Out-Null
    Invoke-Com $view 'Close' 'InvokeMethod' $null | Out-Null
    Invoke-Com $db 'Commit' 'InvokeMethod' $null | Out-Null
    # The package stays locked until every COM reference to it is gone.
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
    $view = $null; $db = $null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

# Windows Installer's own consistency checks (ICEs) - they catch a bad or over-long condition that
# would otherwise only surface as "error 2806" halfway through the pages.
#   ICE38 and ICE64 are skipped: they are roaming-profile rules - a registry key path for every file
#   in a user profile, RemoveFile rows for profile folders - that do not apply to a per-user add-in
#   installed on one machine, and folders Windows Installer created go with their last file anyway.
#   ICE40 (REINSTALLMODE), ICE61 (same-version upgrades) and ICE30 for the PackageContents.xml
#   variants (mutually exclusive by construction) are expected warnings, as is ICE105 for a
#   dual-purpose package that defaults to Only me.
$ErrorActionPreference = 'Continue'
$validation = @(& $wixExe msi validate $msiPath -sice ICE38 -sice ICE64 2>&1 | ForEach-Object { "$_" })
$ErrorActionPreference = 'Stop'
$problems = @($validation | Where-Object { $_ -match 'error WIX' } | ForEach-Object { ($_ -split ' : ', 2)[-1] })
$validation | Where-Object { $_ -match 'warning WIX' -and $_ -notmatch 'ICE40|ICE61|ICE91|ICE105' -and $_ -notmatch 'ICE30.*PackageContents\.xml' } | ForEach-Object { Write-Host "  [warn] $(($_ -split ' : ', 2)[-1])" -ForegroundColor Yellow }

$db = Open-Package $msiPath 0

if (Test-Table $db 'CustomAction') {
    foreach ($row in (Get-Rows $db 'SELECT `Action`, `Type` FROM `CustomAction`')) {
        $base = ([int]$row[1]) -band 0x3F
        if ($base -notin 19, 35, 51) { $problems += "custom action $($row[0]) has type $($row[1]) - code is not allowed in AB packages" }
    }
}
if (Test-Table $db 'Binary') {
    foreach ($row in (Get-Rows $db 'SELECT `Name` FROM `Binary`')) {
        if ($row[0] -notmatch '^WixUI_(Bmp|Ico)_') { $problems += "unexpected binary stream $($row[0])" }
    }
}
$files = (Get-Rows $db 'SELECT `File` FROM `File`').Count
if ($files -ne $fileCount) { $problems += "the package holds $files files, $fileCount expected" }
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($db) | Out-Null

if ($problems.Count -gt 0) {
    Remove-Item $msiPath -Force -ErrorAction SilentlyContinue
    $problems | ForEach-Object { Write-Host "  [FAIL] $_" -ForegroundColor Red }
    Fail 'The package failed its checks and was deleted.'
}

if (-not $KeepSource) { Remove-Item (Join-Path $WorkDirectory 'wixobj') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ("  [ok]   {0}  ({1:N0} KB, {2} files, no code custom actions)" -f $msiPath, ((Get-Item $msiPath).Length / 1KB), $files) -ForegroundColor Green
Write-Host ("         releases: {0}" -f (($releases | ForEach-Object { $_.Prop }) -join ', '))

[pscustomobject]@{
    Path = $msiPath
    Source = $wxsPath
    Files = $files
    Releases = @($releases | ForEach-Object { $_.Prop })
    Sha256 = (Get-FileHash $msiPath -Algorithm SHA256).Hash
}
