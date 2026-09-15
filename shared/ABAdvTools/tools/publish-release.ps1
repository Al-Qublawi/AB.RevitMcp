<#
.SYNOPSIS
    Publishes an AB add-in release on GitHub: tag v<version> on the current commit, with the
    installer attached. Installed copies of the add-in are told about it from there.

.DESCRIPTION
    Checks before publishing, and stops at the first problem:
      - the GitHub CLI is installed and signed in
      - the working tree is clean and the commit has been pushed
      - the installer exists and its version matches the tag
      - the tag is not already released (use -Clobber to replace the asset of an existing release)
    and warns when the repository is private, because the add-ins read releases anonymously and a
    private repository's releases are invisible to them.

    Nothing is pushed or made public by this script. Push the branch, and change repository
    visibility, yourself.

.EXAMPLE
    .\tools\publish-release.ps1 -Repository ..\ABClashApprover -Asset ..\ABClashApprover\deploy\AB.ClashApprover-1.4.1.msi -NotesFile ..\ABClashApprover\RELEASE_NOTES.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Asset,
    [string]$NotesFile,
    [string]$Title,
    [switch]$Clobber,
    [switch]$Draft
)

# Not 'Stop': gh writes ordinary answers to stderr, which Windows PowerShell 5.1 would turn into
# terminating errors. Every native call is judged by its exit code instead.
$ErrorActionPreference = 'Continue'

function Die([string]$message) { Write-Host "  [FAIL] $message" -ForegroundColor Red; exit 1 }
function Ok([string]$message) { Write-Host "  [ok]   $message" -ForegroundColor Green }

$Repository = (Resolve-Path $Repository -ErrorAction SilentlyContinue).Path
if (-not $Repository) { Die 'Repository folder not found.' }
$Asset = (Resolve-Path $Asset -ErrorAction SilentlyContinue).Path
if (-not $Asset) { Die 'Installer not found. Build it first.' }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Die 'The GitHub CLI (gh) is not installed.' }
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Die 'gh is not signed in. Run: gh auth login' }
Ok 'GitHub CLI signed in'

Push-Location $Repository
try {
    $remote = (& git remote get-url origin 2>$null)
    if (-not $remote -or $remote -notmatch 'github\.com[:/](.+?)(\.git)?$') { Die 'origin is not a GitHub repository.' }
    $slug = $Matches[1]
    Ok "repository $slug"

    if (& git status --porcelain) { Die 'The working tree has uncommitted changes. Commit them first.' }
    $head = (& git rev-parse HEAD).Trim()
    & git fetch origin --quiet 2>$null
    $onRemote = & git branch -r --contains $head 2>$null
    if (-not $onRemote) { Die "Commit $($head.Substring(0, 8)) is not on GitHub yet. Push it first." }
    Ok "commit $($head.Substring(0, 8)) is pushed"

    if ($Asset -like '*.msi') {
        # The package's own ProductVersion, straight from its Property table.
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @([string]::Copy($Asset), 0))
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        $version = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
    }
    else {
        $version = (Get-Item $Asset).VersionInfo.ProductVersion
    }
    if ($version -match '^(\d+\.\d+\.\d+)') { $version = $Matches[1] } else { Die "Cannot read a version from $Asset." }
    $tag = "v$version"
    Ok "installer version $version -> tag $tag"

    $visibility = (& gh repo view $slug --json visibility -q .visibility 2>$null)
    if ($visibility -ne 'PUBLIC') {
        Write-Host "  [warn] $slug is $visibility. Installed add-ins cannot see its releases until it is public." -ForegroundColor Yellow
    }

    & gh release view $tag --repo $slug *> $null
    if ($LASTEXITCODE -eq 0) {
        if (-not $Clobber) { Die "$tag is already released. Bump the version, or pass -Clobber to replace its installer." }
        & gh release upload $tag $Asset --repo $slug --clobber
        if ($LASTEXITCODE -ne 0) { Die 'Uploading the installer failed.' }
        Ok "replaced the installer on $tag"
    }
    else {
        $arguments = @('release', 'create', $tag, $Asset, '--repo', $slug, '--target', $head,
                       '--title', $(if ($Title) { $Title } else { "$(Split-Path $Repository -Leaf) $version" }))
        if ($NotesFile) { $arguments += @('--notes-file', (Resolve-Path $NotesFile).Path) }
        else { $arguments += @('--generate-notes') }
        if ($Draft) { $arguments += '--draft' }

        & gh @arguments
        if ($LASTEXITCODE -ne 0) { Die 'Creating the release failed.' }
        Ok "released $tag"
    }

    Write-Host ''
    Write-Host "  https://github.com/$slug/releases/tag/$tag" -ForegroundColor Cyan
}
finally {
    Pop-Location
}
