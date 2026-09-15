<#
.SYNOPSIS
    Authenticode-signs the built binaries.

.DESCRIPTION
    Code signing is the real fix for Defender's Attack Surface Reduction rule
    "Block executable files from running unless they meet a prevalence, age, or trusted list
    criteria" (GUID 01443614-cd74-433a-b99e-2ecdc07bfc25). That rule is not a malware verdict -
    it blocks anything it has not seen before, which is every freshly built unsigned executable.

    A signature does two things:
      * gives the file a stable publisher identity IT can allowlist once, instead of a new hash
        allowlist for every build
      * lets SmartScreen and Defender accumulate reputation against the certificate rather than
        against each individual file

    Reality check: a brand-new standard (OV) certificate has no reputation on day one, so the
    first few builds may still be flagged on managed fleets. An EV certificate carries immediate
    SmartScreen reputation. On a machine that says "your administrator has blocked this action",
    only your IT team can lift it - see docs/DEPLOYMENT.md.

    ALWAYS timestamps: without a timestamp the signature dies when the certificate expires.

.EXAMPLE
    .\sign.ps1 -Files .\dist\AB.RevitMcp-1.5.0.msi -Thumbprint A1B2C3...
    .\sign.ps1 -Files .\dist\AB.RevitMcp-1.5.0.msi -PfxPath cert.pfx -PfxPassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Files,

    # Certificate already installed in CurrentUser\My - the usual case for a hardware token.
    [string] $Thumbprint,

    # Or a .pfx on disk.
    [string] $PfxPath,
    [System.Security.SecureString] $PfxPassword,

    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    # Report what would be signed and stop.
    [switch] $WhatIfOnly
)

$ErrorActionPreference = 'Stop'

function Info($t) { Write-Host "          $t" -ForegroundColor DarkGray }
function Ok($t)   { Write-Host "   [ok]   $t" -ForegroundColor Green }
function Warn2($t){ Write-Host "   [warn] $t" -ForegroundColor Yellow }
function Die($t)  { Write-Host "   [fail] $t" -ForegroundColor Red; exit 1 }

if (-not $Thumbprint -and -not $PfxPath) {
    Die 'Supply either -Thumbprint (certificate in CurrentUser\My) or -PfxPath.'
}

$existing = @($Files | Where-Object { Test-Path $_ })
if ($existing.Count -eq 0) { Die 'None of the files to sign exist.' }

Write-Host ''
Write-Host '  Code signing' -ForegroundColor Cyan
Info ("files     : {0}" -f $existing.Count)
Info ("timestamp : {0}" -f $TimestampUrl)

if ($WhatIfOnly) {
    $existing | ForEach-Object { Info $_ }
    Ok 'what-if only; nothing was signed'
    exit 0
}

# signtool handles hardware tokens and dual signing better than the PowerShell cmdlet, so prefer
# it when the Windows SDK is present.
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

$signed = 0
$failed = 0

foreach ($file in $existing) {
    try {
        if ($signtool) {
            $args = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl)

            if ($Thumbprint) { $args += @('/sha1', $Thumbprint) }
            else {
                $args += @('/f', $PfxPath)
                if ($PfxPassword) {
                    $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
                        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
                    $args += @('/p', $plain)
                }
            }
            $args += $file

            $output = & $signtool.FullName @args 2>&1
            if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
        }
        else {
            Warn2 'signtool.exe not found; falling back to Set-AuthenticodeSignature'

            $cert = if ($Thumbprint) {
                Get-ChildItem "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
            } else {
                New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
                    ($PfxPath, $PfxPassword)
            }

            $result = Set-AuthenticodeSignature -FilePath $file -Certificate $cert `
                        -HashAlgorithm SHA256 -TimestampServer $TimestampUrl -ErrorAction Stop
            if ($result.Status -ne 'Valid') { throw $result.StatusMessage }
        }

        Ok (Split-Path -Leaf $file)
        $signed++
    }
    catch {
        Write-Host ("   [fail] {0}: {1}" -f (Split-Path -Leaf $file), $_.Exception.Message) -ForegroundColor Red
        $failed++
    }
}

Write-Host ''
Write-Host ("  Signed {0}, failed {1}" -f $signed, $failed) -ForegroundColor $(if ($failed) { 'Yellow' } else { 'Green' })
Write-Host ''

if ($failed -gt 0) { exit 1 }
exit 0
