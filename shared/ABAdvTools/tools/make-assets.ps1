<#
.SYNOPSIS
    Draws the AB Adv Tools shared panel icons: About (the AB monogram), Check for Updates and
    LinkedIn, as 16 and 32 px PNG (Revit) and ICO (Navisworks), plus the 64/256 px logo used
    by the About dialog.

.DESCRIPTION
    Run once; the results are committed under assets\. Re-run only to change the artwork.

    System.Drawing's Icon.FromHandle(...).Save() drops the alpha channel and leaves black
    fringes on the ribbon, so each ICO is written by hand as a 32-bit BGRA bitmap.

.PARAMETER LinkedInIcon
    An existing 32 px LinkedIn .ico to reuse, so the badge matches the one the add-ins
    already show.
#>
[CmdletBinding()]
param(
    [string]$LinkedInIcon
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$kit = Split-Path -Parent $PSScriptRoot
$out = Join-Path $kit 'assets'
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not $LinkedInIcon) {
    # The kit's own copy, first taken from the add-ins' LinkedIn badge.
    $LinkedInIcon = Join-Path $out 'abadv_linkedin_32.ico'
}

# ------------------------------------------------------------------ ICO writer

function Write-Ico {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $xor = New-Object byte[] ($w * $h * 4)
    for ($y = 0; $y -lt $h; $y++) {
        $srcY = $h - 1 - $y
        for ($x = 0; $x -lt $w; $x++) {
            $c = $Bitmap.GetPixel($x, $srcY)
            $i = ($y * $w + $x) * 4
            $xor[$i] = $c.B; $xor[$i + 1] = $c.G; $xor[$i + 2] = $c.R; $xor[$i + 3] = $c.A
        }
    }

    $maskRow = [int][Math]::Floor((($w + 31) / 32)) * 4
    $andMask = New-Object byte[] ($maskRow * $h)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $imageSize = 40 + $xor.Length + $andMask.Length

    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
    $bw.Write([byte]$w); $bw.Write([byte]$h); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$imageSize); $bw.Write([uint32]22)

    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($xor.Length + $andMask.Length))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write($xor); $bw.Write($andMask)
    $bw.Flush()

    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose()
}

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$navyTop    = [System.Drawing.Color]::FromArgb(255, 18, 30, 48)
$navyBottom = [System.Drawing.Color]::FromArgb(255, 6, 12, 22)
$glow       = [System.Drawing.Color]::FromArgb(255, 31, 111, 235)
$bBlue      = [System.Drawing.Color]::FromArgb(255, 46, 168, 255)
$white      = [System.Drawing.Color]::FromArgb(255, 238, 242, 248)

# ------------------------------------------------------------------ AB monogram

function Draw-Logo([int]$size) {
    $canvas = New-Canvas $size; $bmp = $canvas[0]; $g = $canvas[1]

    $pad = [single]([Math]::Max(0.5, $size * 0.04))
    $rect = New-RoundedRect $pad $pad ($size - 2 * $pad) ($size - 2 * $pad) ([single]($size * 0.22))

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, $size)), $navyTop, $navyBottom)
    $g.FillPath($brush, $rect)

    $penWidth = [single]([Math]::Max(1, $size * 0.045))
    $pen = New-Object System.Drawing.Pen($glow, $penWidth)
    $g.DrawPath($pen, $rect)

    # "A" in white, "B" in blue, as on every AB product logo.
    $fontSize = [single]($size * 0.50)
    $font = New-Object System.Drawing.Font('Segoe UI Black', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Near
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoClip

    $aSize = $g.MeasureString('A', $font, 1000, [System.Drawing.StringFormat]::GenericTypographic)
    $bSize = $g.MeasureString('B', $font, 1000, [System.Drawing.StringFormat]::GenericTypographic)
    $total = $aSize.Width + $bSize.Width - $size * 0.04
    $left = ($size - $total) / 2
    $midY = if ($size -ge 64) { $size * 0.44 } else { $size * 0.52 }

    $typo = [System.Drawing.StringFormat]::GenericTypographic
    $aTop = $midY - $aSize.Height / 2
    $g.DrawString('A', $font, (New-Object System.Drawing.SolidBrush($white)), [single]$left, [single]$aTop, $typo)
    $g.DrawString('B', $font, (New-Object System.Drawing.SolidBrush($bBlue)), [single]($left + $aSize.Width - $size * 0.04), [single]$aTop, $typo)

    if ($size -ge 64) {
        $small = New-Object System.Drawing.Font('Segoe UI Semibold', [single]($size * 0.105), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $centre = New-Object System.Drawing.StringFormat
        $centre.Alignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString('ADV TOOLS', $small, (New-Object System.Drawing.SolidBrush($bBlue)),
            (New-Object System.Drawing.RectangleF(0, [single]($size * 0.70), $size, [single]($size * 0.16))), $centre)
    }

    $g.Dispose()
    return $bmp
}

# ------------------------------------------------------------------ update badge

function Draw-Update([int]$size) {
    $canvas = New-Canvas $size; $bmp = $canvas[0]; $g = $canvas[1]

    $pad = [single]([Math]::Max(0.5, $size * 0.06))
    $d = [single]($size - 2 * $pad)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($glow)), $pad, $pad, $d, $d)

    # Downward arrow into a tray: "a new release to download".
    $c = [single]($size / 2)
    $stroke = [single]([Math]::Max(1.6, $size * 0.11))
    $pen = New-Object System.Drawing.Pen($white, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $g.DrawLine($pen, $c, [single]($size * 0.24), $c, [single]($size * 0.60))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([single]($size * 0.33), [single]($size * 0.45))),
        (New-Object System.Drawing.PointF($c, [single]($size * 0.62))),
        (New-Object System.Drawing.PointF([single]($size * 0.67), [single]($size * 0.45)))))
    $g.DrawLine($pen, [single]($size * 0.30), [single]($size * 0.76), [single]($size * 0.70), [single]($size * 0.76))

    $g.Dispose()
    return $bmp
}

# ------------------------------------------------------------------ write

function Save-Pair([System.Drawing.Bitmap]$bmp, [string]$name) {
    $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Ico $bmp (Join-Path $out "$name.ico")
    Write-Host "  $name.png / .ico"
}

foreach ($size in 16, 32) {
    Save-Pair (Draw-Logo $size) "abadv_about_$size"
    Save-Pair (Draw-Update $size) "abadv_update_$size"
}

foreach ($size in 64, 256) {
    $bmp = Draw-Logo $size
    $bmp.Save((Join-Path $out "abadv_$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "  abadv_$size.png"
}

if (Test-Path $LinkedInIcon) {
    foreach ($size in 16, 32) {
        $source = $LinkedInIcon -replace '_32\.ico$', "_$size.ico"
        if (-not (Test-Path $source)) { $source = $LinkedInIcon }
        $icon = New-Object System.Drawing.Icon($source, $size, $size)
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawIcon($icon, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
        $g.Dispose()
        $icon.Dispose()   # release the file before it may be overwritten
        Save-Pair $bmp "abadv_linkedin_$size"
    }
}
else {
    Write-Warning "LinkedIn icon not found at $LinkedInIcon - skipped."
}

Write-Host "Assets written to $out" -ForegroundColor Green
