<#
.SYNOPSIS
    Draws the AB Adv Tools artwork every AB .msi shows: the side picture of the welcome and finish
    pages, and the banner along the top of every other page.

.DESCRIPTION
    Windows Installer shows these at fixed sizes, so they are drawn at exactly those sizes:

      assets\msi_dialog.bmp   493 x 312   welcome and finish pages (text starts 180 px in)
      assets\msi_banner.bmp   493 x 58    top of every other page (text on the left)

    Run it again only when the suite logo (assets\abadv_256.png) changes, then sync the kit.

.EXAMPLE
    .\tools\make-msi-art.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$kit = Split-Path -Parent $PSScriptRoot
$logo = [System.Drawing.Image]::FromFile((Join-Path $kit 'assets\abadv_256.png'))

$navy = [System.Drawing.Color]::FromArgb(17, 24, 39)
$accent = [System.Drawing.Color]::FromArgb(59, 130, 246)
$grey = [System.Drawing.Color]::FromArgb(156, 163, 175)

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::White)
    return @($bmp, $g)
}

# ---- welcome / finish side picture
$canvas = New-Canvas 493 312
$bmp = $canvas[0]; $g = $canvas[1]
$band = 164
$g.FillRectangle((New-Object System.Drawing.SolidBrush($navy)), 0, 0, $band, 312)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), $band - 3, 0, 3, 312)
$g.DrawImage($logo, [int](($band - 112) / 2), 44, 112, 112)

$center = New-Object System.Drawing.StringFormat
$center.Alignment = [System.Drawing.StringAlignment]::Center
$title = New-Object System.Drawing.Font('Segoe UI Semibold', 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$small = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('AB Adv Tools', $title, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 172, $band, 22)), $center)
$g.DrawString('for Revit and Navisworks', $small, (New-Object System.Drawing.SolidBrush($grey)), (New-Object System.Drawing.RectangleF(0, 194, $band, 18)), $center)
$g.DrawString('by Abdullah Lotfy', $small, (New-Object System.Drawing.SolidBrush($grey)), (New-Object System.Drawing.RectangleF(0, 270, $band, 18)), $center)
$g.Dispose()
$bmp.Save((Join-Path $kit 'assets\msi_dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()

# ---- banner
$canvas = New-Canvas 493 58
$bmp = $canvas[0]; $g = $canvas[1]
$g.DrawImage($logo, 493 - 50, 7, 44, 44)
$g.Dispose()
$bmp.Save((Join-Path $kit 'assets\msi_banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()

$logo.Dispose()
Write-Host 'Wrote assets\msi_dialog.bmp and assets\msi_banner.bmp'
