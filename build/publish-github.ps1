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

# ---------------------------------------------------------------------------
# NOT 'Stop'. Windows PowerShell 5.1 wraps a native executable's stderr in a
# NativeCommandError and, under $ErrorActionPreference='Stop', treats it as TERMINATING - even
# when the process exits 0. gh writes to stderr routinely: "Could not resolve to a Repository"
# is the NORMAL answer when asking whether a repo exists yet. So every native call below is
# judged by $LASTEXITCODE explicitly, never by exception.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Step($m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "   [ok]   $m" -ForegroundColor Green }
function Die($m)  { Write-Host ''; Write-Host "   [FAIL] $m" -ForegroundColor Red; exit 1 }

# Runs gh, swallowing the stderr-as-error behaviour. Returns exit code + combined output.
function Invoke-Gh {
    param([string[]] $GhArgs)
    $out  = & $script:GhExe @GhArgs 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $code
        Output   = ($out | Out-String).Trim()
    }
}

# ---- 1. the GitHub CLI ---------------------------------------------------
Step 'Checking the GitHub CLI'
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh) {
    $script:GhExe = $gh.Source
} else {
    Write-Host '   gh not found - installing via winget...'
    & winget install --id GitHub.cli --silent --accept-package-agreements --accept-source-agreements
    # winget only updates PATH for NEW processes, so locate the exe directly this session.
    $candidate = "$env:ProgramFiles\GitHub CLI\gh.exe"
    if (Test-Path $candidate) { $script:GhExe = $candidate }
    else { Die 'gh installed but not on PATH yet. Open a NEW terminal and re-run this script.' }
}
Ok "gh at $script:GhExe"

# ---- 2. authentication ---------------------------------------------------
Step 'Checking authentication'
$auth = Invoke-Gh @('auth', 'status')
if ($auth.ExitCode -ne 0) {
    Write-Host ''
    Write-Host '   Not authenticated. Run this yourself, then re-run this script:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '       gh auth login' -ForegroundColor White
    Write-Host ''
    Write-Host '   Choose: GitHub.com -> HTTPS -> authenticate in browser.'
    exit 1
}

$whoResult = Invoke-Gh @('api', 'user', '--jq', '.login')
if ($whoResult.ExitCode -ne 0) { Die "could not read the authenticated user: $($whoResult.Output)" }
$who = $whoResult.Output
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

$slug = "$who/$RepoName"
$look = Invoke-Gh @('repo', 'view', $slug, '--json', 'name')

if ($look.ExitCode -eq 0) {
    Ok "$slug already exists - reusing it"
    $remotes = & git remote
    if ($remotes -notcontains 'origin') {
        & git remote add origin "https://github.com/$slug.git"
        if ($LASTEXITCODE -ne 0) { Die 'could not add the origin remote' }
    }
    & git push -u origin main
    if ($LASTEXITCODE -ne 0) { Die 'push failed' }
} else {
    Write-Host '   repository does not exist yet - creating it'
    $desc = 'Universal MCP server + Revit add-in: let any MCP-compatible AI client safely query, create and modify Autodesk Revit models. Revit 2020-2026, 78 tools, metric.'
    $create = Invoke-Gh @('repo', 'create', $RepoName, $visibility, '--source=.', '--remote=origin', '--push', '--description', $desc)
    if ($create.ExitCode -ne 0) { Die "repo create failed:`n$($create.Output)" }
}
Ok "https://github.com/$slug"

# ---- 5. topics -----------------------------------------------------------
Step 'Setting topics'
$topics = @('repo', 'edit', $slug)
foreach ($t in @('revit','bim','mcp','model-context-protocol','revit-api','autodesk','aec','csharp','dotnet','ai-agents')) {
    $topics += '--add-topic'
    $topics += $t
}
$topicResult = Invoke-Gh $topics
if ($topicResult.ExitCode -ne 0) { Write-Host "   [warn] topics not set: $($topicResult.Output)" -ForegroundColor Yellow }
else { Ok 'topics set' }

# ---- 6. the release ------------------------------------------------------
Step "Creating release $Tag"
$notes = Join-Path $repoRoot 'build\RELEASE_NOTES.md'
if (-not (Test-Path $notes)) { Die "release notes missing: $notes" }

$rel = Invoke-Gh @('release', 'view', $Tag, '--repo', $slug)
if ($rel.ExitCode -eq 0) {
    Write-Host "   release $Tag already exists - re-uploading the asset"
    $up = Invoke-Gh @('release', 'upload', $Tag, $Asset, '--repo', $slug, '--clobber')
    if ($up.ExitCode -ne 0) { Die "asset upload failed:`n$($up.Output)" }
} else {
    Write-Host '   uploading installer, this takes a moment (33 MB)...'
    $mk = Invoke-Gh @('release', 'create', $Tag, $Asset, '--repo', $slug,
                      '--title', "AB Revit MCP Bridge $Tag", '--notes-file', $notes)
    if ($mk.ExitCode -ne 0) { Die "release failed:`n$($mk.Output)" }
}

Write-Host ''
Write-Host '  Published' -ForegroundColor Green
Write-Host "  Repo    : https://github.com/$slug"
Write-Host "  Release : https://github.com/$slug/releases/tag/$Tag"
Write-Host ''
