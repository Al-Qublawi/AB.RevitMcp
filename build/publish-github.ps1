<#
.SYNOPSIS
    Publishes this repository to GitHub and attaches the installer as a release asset.

.DESCRIPTION
    One-shot publish. Creates the GitHub repository, pushes main, then creates the release
    and uploads dist\AB.RevitMcp.Setup.exe to it.

    Requires the GitHub CLI, authenticated. If gh is missing this script installs it via
    winget; authentication is interactive and must be done by you:

        gh auth login

.EXAMPLE
    .\build\publish-github.ps1
    .\build\publish-github.ps1 -RepoName ab-revit-mcp -Private
#>
[CmdletBinding()]
param(
    [string] $RepoName = 'AB.RevitMcp',
    [string] $Tag      = 'v1.1.0',
    [string] $Asset    = 'dist\AB.RevitMcp.Setup.exe',

    # Publish private instead of public. Default is public, as intended for this project.
    [switch] $Private
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Step($m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "   [ok]   $m" -ForegroundColor Green }
function Die($m)  { Write-Host "   [FAIL] $m" -ForegroundColor Red; exit 1 }

# ---- 1. the GitHub CLI ---------------------------------------------------
Step 'Checking the GitHub CLI'
$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Host '   gh not found - installing via winget...'
    & winget install --id GitHub.cli --silent --accept-package-agreements --accept-source-agreements
    # winget updates PATH for new processes only; find the exe directly this session.
    $candidate = "$env:ProgramFiles\GitHub CLI\gh.exe"
    if (Test-Path $candidate) { $gh = Get-Item $candidate }
    else { Die 'gh installed but not found. Open a NEW terminal and re-run this script.' }
}
$ghExe = $gh.Source
if (-not $ghExe) { $ghExe = $gh.FullName }
Ok "gh at $ghExe"

# ---- 2. authentication ---------------------------------------------------
Step 'Checking authentication'
& $ghExe auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '   Not authenticated. Run this yourself, then re-run this script:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '       gh auth login' -ForegroundColor White
    Write-Host ''
    Write-Host '   Choose: GitHub.com -> HTTPS -> authenticate in browser.'
    exit 1
}
$who = (& $ghExe api user --jq .login)
Ok "authenticated as $who"

# ---- 3. sanity checks ----------------------------------------------------
Step 'Pre-flight'
if (-not (Test-Path $Asset)) { Die "$Asset not found. Run build\build-installer.ps1 first." }
$assetSize = [math]::Round((Get-Item $Asset).Length / 1MB, 1)
Ok "$Asset present ($assetSize MB)"

$dirty = & git status --porcelain
if ($dirty) {
    Write-Host '   Uncommitted changes:' -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host "     $_" }
    Die 'Commit or stash before publishing.'
}
Ok 'working tree clean'

# ---- 4. create the repository -------------------------------------------
Step 'Creating the GitHub repository'
$visibility = '--public'
if ($Private) { $visibility = '--private' }

$existing = & $ghExe repo view "$who/$RepoName" --json name 2>$null
if ($LASTEXITCODE -eq 0) {
    Ok "$who/$RepoName already exists - reusing it"
    $remotes = & git remote
    if ($remotes -notcontains 'origin') {
        & git remote add origin "https://github.com/$who/$RepoName.git"
    }
    & git push -u origin main
    if ($LASTEXITCODE -ne 0) { Die 'push failed' }
} else {
    & $ghExe repo create $RepoName $visibility --source=. --remote=origin --push `
        --description 'Universal MCP server + Revit add-in: let any MCP-compatible AI client safely query, create and modify Autodesk Revit models. Revit 2020-2026, 78 tools, metric.'
    if ($LASTEXITCODE -ne 0) { Die 'repo create failed' }
}
Ok "https://github.com/$who/$RepoName"

# ---- 5. topics -----------------------------------------------------------
Step 'Setting topics'
& $ghExe repo edit "$who/$RepoName" `
    --add-topic revit --add-topic bim --add-topic mcp --add-topic model-context-protocol `
    --add-topic revit-api --add-topic autodesk --add-topic aec --add-topic csharp `
    --add-topic dotnet --add-topic ai-agents 2>&1 | Out-Null
Ok 'topics set'

# ---- 6. the release ------------------------------------------------------
Step "Creating release $Tag"
$notes = Join-Path $repoRoot 'build\RELEASE_NOTES.md'
if (-not (Test-Path $notes)) { Die "release notes missing: $notes" }

& $ghExe release view $Tag --repo "$who/$RepoName" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "   release $Tag exists - uploading asset with --clobber"
    & $ghExe release upload $Tag $Asset --repo "$who/$RepoName" --clobber
} else {
    & $ghExe release create $Tag $Asset `
        --repo "$who/$RepoName" `
        --title "AB Revit MCP Bridge $Tag" `
        --notes-file $notes
}
if ($LASTEXITCODE -ne 0) { Die 'release failed' }

Write-Host ''
Write-Host '  Published' -ForegroundColor Green
Write-Host "  Repo    : https://github.com/$who/$RepoName"
Write-Host "  Release : https://github.com/$who/$RepoName/releases/tag/$Tag"
Write-Host ''
