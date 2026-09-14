<#
.SYNOPSIS
    Turns a product logo PNG into a multi-size .ico for a setup executable.

.DESCRIPTION
    Writes 16, 24, 32, 48 and 256 px frames, each PNG-compressed (supported since Windows
    Vista), so the installer looks sharp in Explorer, the taskbar and Apps and Features.

.EXAMPLE
    .\tools\make-setup-icon.ps1 -Png ..\ABClashApprover\src\ABClashApprover\Images\logo.png -Ico ..\ABClashApprover\installer\ABClashApprover.Setup\setup.ico
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Png,
    [Parameter(Mandatory = $true)][string]$Ico
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Image]::FromFile((Resolve-Path $Png).Path)
$sizes = 16, 24, 32, 48, 256
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fit without distorting a non-square logo; centre it on the transparent square.
    $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
    $w = [int][Math]::Round($source.Width * $scale)
    $h = [int][Math]::Round($source.Height * $scale)
    $g.DrawImage($source, [int](($size - $w) / 2), [int](($size - $h) / 2), $w, $h)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($size, $ms.ToArray())
    $bmp.Dispose()
}
$source.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $size = $frame[0]; $data = $frame[1]
    $w.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $w.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($frame in $frames) { $w.Write([byte[]]$frame[1]) }
$w.Flush()

New-Item -ItemType Directory -Force -Path (Split-Path $Ico -Parent) | Out-Null
[System.IO.File]::WriteAllBytes($Ico, $out.ToArray())
Write-Host "Wrote $Ico ($($frames.Count) sizes)"
