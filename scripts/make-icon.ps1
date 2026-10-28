# Renders the Hindsight logo (rewind arc + record dot) to PNG/ICO with System.Drawing.
# Usage: powershell -File scripts/make-icon.ps1   (Windows PowerShell 5.1)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$resources = Join-Path $root 'src\Hindsight.App\Resources'
$siteAssets = Join-Path $root 'site\assets'
New-Item -ItemType Directory -Force $resources, $siteAssets | Out-Null

function New-Logo([int]$size, [bool]$background = $true) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    if ($background) {
        $radius = 58 * $s
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
        $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
        $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
        $path.CloseFigure()
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size),
            [System.Drawing.Color]::FromArgb(255, 30, 27, 40), [System.Drawing.Color]::FromArgb(255, 11, 11, 16))
        $g.FillPath($bg, $path)
    }

    $cx = 128 * $s; $cy = 128 * $s; $r = 74 * $s
    $arcPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single](22 * $s))
    $arcPen.StartCap = 'Flat'; $arcPen.EndCap = 'Round'
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF ([single]($cx - $r)), ([single]($cy - $r))),
        (New-Object System.Drawing.PointF ([single]($cx + $r)), ([single]($cy + $r))),
        [System.Drawing.Color]::FromArgb(255, 255, 159, 67), [System.Drawing.Color]::FromArgb(255, 255, 77, 94))
    $arcPen.Brush = $grad
    $start = 252.0; $sweep = 290.0
    $g.DrawArc($arcPen, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2), [single]$start, [single]$sweep)

    # Arrow head at the arc start, pointing counter-clockwise ("rewind").
    $a = $start * [Math]::PI / 180
    $px = $cx + $r * [Math]::Cos($a); $py = $cy + $r * [Math]::Sin($a)
    $dx = [Math]::Sin($a); $dy = -[Math]::Cos($a)       # counter-clockwise tangent
    $nx = [Math]::Cos($a); $ny = [Math]::Sin($a)         # radial
    $tip = New-Object System.Drawing.PointF ([single]($px + $dx * 36 * $s)), ([single]($py + $dy * 36 * $s))
    $b1 = New-Object System.Drawing.PointF ([single]($px + $nx * 25 * $s - $dx * 2 * $s)), ([single]($py + $ny * 25 * $s - $dy * 2 * $s))
    $b2 = New-Object System.Drawing.PointF ([single]($px - $nx * 25 * $s - $dx * 2 * $s)), ([single]($py - $ny * 25 * $s - $dy * 2 * $s))
    $headBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 140, 70))
    $g.FillPolygon($headBrush, [System.Drawing.PointF[]]@($tip, $b1, $b2))

    # Record dot with a soft glow.
    $dot = 30 * $s
    for ($i = 4; $i -ge 1; $i--) {
        $gr = $dot + $i * 6 * $s
        $glow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(18, 255, 77, 94))
        $g.FillEllipse($glow, [single]($cx - $gr), [single]($cy - $gr), [single]($gr * 2), [single]($gr * 2))
    }
    $dotBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF ([single]($cx - $dot)), ([single]($cy - $dot))),
        (New-Object System.Drawing.PointF ([single]($cx + $dot)), ([single]($cy + $dot))),
        [System.Drawing.Color]::FromArgb(255, 255, 110, 120), [System.Drawing.Color]::FromArgb(255, 230, 40, 70))
    $g.FillEllipse($dotBrush, [single]($cx - $dot), [single]($cy - $dot), [single]($dot * 2), [single]($dot * 2))
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }

# ICO with PNG-compressed entries (Vista+).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-Logo $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$icoPath = Join-Path $resources 'hindsight.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Close()

$logo = New-Logo 512; Save-Png $logo (Join-Path $resources 'logo.png'); Save-Png $logo (Join-Path $siteAssets 'icon-512.png'); $logo.Dispose()
$touch = New-Logo 180; Save-Png $touch (Join-Path $siteAssets 'apple-touch-icon.png'); $touch.Dispose()
$mark = New-Logo 512 $false; Save-Png $mark (Join-Path $siteAssets 'mark.png'); $mark.Dispose()
Write-Host "Wrote $icoPath and PNGs."
