<#
.SYNOPSIS
    Generates the icon assets from the master logo PNG.

.DESCRIPTION
    Produces, from src\AB.RevitMcp.Addin\Resources\logo.png:

      logo_16.png / logo_32.png   ribbon images for the Revit add-in (WPF reads PNG directly)
      logo.ico                    multi-size application icon for the installer executable

    The .ico is written by hand because System.Drawing's Icon.Save cannot produce a multi-size,
    32-bit-alpha icon - it downgrades to a single low-colour frame. The Vista+ ICO format simply
    embeds PNG frames, which is what this writes.

.EXAMPLE
    .\make-icons.ps1
#>
[CmdletBinding()]
param(
    [string] $Source
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repoRoot 'src\AB.RevitMcp.Addin\Resources\logo.png' }
if (-not (Test-Path $Source)) { throw "Master logo not found: $Source" }

$addinRes = Join-Path $repoRoot 'src\AB.RevitMcp.Addin\Resources'
$setupRes = Join-Path $repoRoot 'installer\AB.RevitMcp.Setup\Resources'
New-Item -ItemType Directory -Force -Path $addinRes, $setupRes | Out-Null

Write-Host ''
Write-Host '  Generating icon assets' -ForegroundColor Cyan
Write-Host ("  source: {0}" -f $Source)

$master = [System.Drawing.Image]::FromFile($Source)
try {
    Write-Host ("  master: {0}x{1}" -f $master.Width, $master.Height)

    function New-ScaledPngBytes {
        param([System.Drawing.Image] $Image, [int] $Size)

        $bmp = New-Object System.Drawing.Bitmap $Size, $Size,
            ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($Image, (New-Object System.Drawing.Rectangle 0, 0, $Size, $Size))
        }
        finally { $g.Dispose() }

        $ms = New-Object System.IO.MemoryStream
        try {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            # The leading comma stops PowerShell unrolling the byte[] into the pipeline. Without it
            # the caller receives an Object[] of boxed bytes, BinaryWriter then resolves to the
            # single-byte overload, and the .ico ends up containing one byte per frame.
            return ,$ms.ToArray()
        }
        finally { $ms.Dispose(); $bmp.Dispose() }
    }

    # ---- ribbon PNGs (WPF loads PNG natively, so no .ico needed for the add-in) ----
    foreach ($size in 16, 32) {
        [byte[]] $bytes = New-ScaledPngBytes -Image $master -Size $size
        $path = Join-Path $addinRes ("logo_{0}.png" -f $size)
        [System.IO.File]::WriteAllBytes($path, $bytes)
        Write-Host ("   [ok]   logo_{0}.png  ({1} bytes)" -f $size, $bytes.Length) -ForegroundColor Green
    }

    # ---- installer banner ----
    [byte[]] $bannerBytes = New-ScaledPngBytes -Image $master -Size 96
    [System.IO.File]::WriteAllBytes((Join-Path $setupRes 'logo_96.png'), $bannerBytes)
    Write-Host ("   [ok]   logo_96.png  ({0} bytes)" -f $bannerBytes.Length) -ForegroundColor Green

    # ---- multi-size .ico for the setup executable ----
    $iconSizes = 16, 24, 32, 48, 64, 128, 256
    $frames = @()
    foreach ($size in $iconSizes) { $frames += ,([byte[]] (New-ScaledPngBytes -Image $master -Size $size)) }

    $ico = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $ico
    try {
        $writer.Write([UInt16]0)                     # reserved
        $writer.Write([UInt16]1)                     # type: 1 = icon
        $writer.Write([UInt16]$iconSizes.Count)      # frame count

        # Image data starts after the directory.
        $offset = 6 + (16 * $iconSizes.Count)
        for ($i = 0; $i -lt $iconSizes.Count; $i++) {
            $size = $iconSizes[$i]
            [byte[]] $bytes = $frames[$i]

            # 256 is encoded as 0 in the single-byte width/height fields.
            $dim = if ($size -ge 256) { 0 } else { $size }
            $writer.Write([Byte]$dim)                # width
            $writer.Write([Byte]$dim)                # height
            $writer.Write([Byte]0)                   # palette size (0 = no palette)
            $writer.Write([Byte]0)                   # reserved
            $writer.Write([UInt16]1)                 # colour planes
            $writer.Write([UInt16]32)                # bits per pixel
            $writer.Write([UInt32]$bytes.Length)     # bytes in resource
            $writer.Write([UInt32]$offset)           # offset of this frame
            $offset += $bytes.Length
        }

        foreach ($frame in $frames) { $writer.Write([byte[]] $frame, 0, ([byte[]] $frame).Length) }
        $writer.Flush()

        $icoPath = Join-Path $setupRes 'logo.ico'
        [System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())

        # Sanity-check: header alone is 6 + 16*frames bytes, so anything near that means the
        # frame payloads were silently dropped.
        $headerOnly = 6 + (16 * $iconSizes.Count)
        if ($ico.Length -le $headerOnly + 64) {
            throw "logo.ico is only $($ico.Length) bytes - the frame data was not written."
        }
        Write-Host ("   [ok]   logo.ico  ({0} frames, {1} KB)" -f $iconSizes.Count,
                    [math]::Round($ico.Length / 1KB, 1)) -ForegroundColor Green
    }
    finally { $writer.Dispose(); $ico.Dispose() }
}
finally { $master.Dispose() }

Write-Host ''
Write-Host '  Done.' -ForegroundColor Green
Write-Host ''
