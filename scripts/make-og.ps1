# Renders site/assets/og.png (1200x630 social preview) from the logo. Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'site\assets'
$bmp = New-Object System.Drawing.Bitmap(1200, 630)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 1200, 630),
    [System.Drawing.Color]::FromArgb(255, 34, 18, 28), [System.Drawing.Color]::FromArgb(255, 11, 11, 16))
$g.FillRectangle($bg, 0, 0, 1200, 630)
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(-200, -260, 900, 760)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 77, 94)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 77, 94))
$g.FillPath($pgb, $glow)
$logo = [System.Drawing.Image]::FromFile((Join-Path $assets 'icon-512.png'))
$g.DrawImage($logo, 96, 175, 280, 280)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 244, 244, 247))
$muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 162, 162, 181))
$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 120, 90))
$g.DrawString('Hindsight', (New-Object System.Drawing.Font('Segoe UI Semibold', 64)), $white, 430, 170)
# Turkish letters built from code points so the script stays ASCII (Windows PowerShell reads BOM-less files as ANSI).
$i = [char]0x0131; $c = [char]0x00E7; $s = [char]0x015F; $U = [char]0x00DC
$g.DrawString("Oyundaki en iyi an$i ka$c${i}rma.", (New-Object System.Drawing.Font('Segoe UI', 40)), $accent, 436, 280)
$g.DrawString("Arka planda sessizce kaydeder, tek tu${s}la klip.", (New-Object System.Drawing.Font('Segoe UI', 26)), $muted, 440, 360)
$g.DrawString("Windows   |   ${U}cretsiz   |   A$c${i}k kaynak", (New-Object System.Drawing.Font('Segoe UI', 22)), $muted, 440, 415)
$g.Dispose(); $logo.Dispose()
$bmp.Save((Join-Path $assets 'og.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host 'Wrote site/assets/og.png'
