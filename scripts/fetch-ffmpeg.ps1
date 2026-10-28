# Downloads the FFmpeg build that ships with Hindsight (BtbN/FFmpeg-Builds, win64 gpl-shared - the same
# variant the recorder is developed and tested against) into <repo>\ffmpeg, keeping only what Hindsight uses,
# and checks that the features the recorder needs are present.
# Usage: pwsh scripts/fetch-ffmpeg.ps1 [-Tag latest] [-Source C:\ffmpeg\bin]   (-Source copies a local build instead)
param(
    [string]$Tag = 'latest',
    [string]$Source
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'ffmpeg'
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
New-Item -ItemType Directory -Force $target | Out-Null

if ($Source) {
    Write-Host "Copying local FFmpeg from $Source"
    Copy-Item (Join-Path $Source 'ffmpeg.exe'), (Join-Path $Source 'ffprobe.exe') $target
    Get-ChildItem $Source -Filter '*.dll' | Copy-Item -Destination $target
} else {
    $work = Join-Path ([IO.Path]::GetTempPath()) "hindsight-ffmpeg-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        $api = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/$Tag"
        $headers = @{ 'User-Agent' = 'hindsight-build' }
        if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
        $release = Invoke-RestMethod -Uri $api -Headers $headers
        $asset = $release.assets | Where-Object { $_.name -match 'win64-gpl-shared\.zip$' } | Select-Object -First 1
        if (-not $asset) { throw "No win64-gpl-shared build in release '$Tag'." }
        Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)"
        $zip = Join-Path $work 'ffmpeg.zip'
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
        Expand-Archive $zip -DestinationPath $work
        $bin = Get-ChildItem $work -Recurse -Directory -Filter bin | Select-Object -First 1
        Copy-Item (Join-Path $bin.FullName 'ffmpeg.exe'), (Join-Path $bin.FullName 'ffprobe.exe') $target
        Get-ChildItem $bin.FullName -Filter '*.dll' | Copy-Item -Destination $target
        $license = Get-ChildItem $work -Recurse -File -Filter 'LICENSE*' | Select-Object -First 1
        if ($license) { Copy-Item $license.FullName (Join-Path $target 'LICENSE.txt') }
        "Source: https://github.com/BtbN/FFmpeg-Builds/releases/tag/$Tag ($($asset.name))`nFFmpeg source code: https://github.com/FFmpeg/FFmpeg" |
            Set-Content (Join-Path $target 'SOURCE.txt') -Encoding utf8
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

# Verify what the recorder relies on.
$ffmpeg = Join-Path $target 'ffmpeg.exe'
$filters = & $ffmpeg -hide_banner -filters 2>&1 | Out-String
$encoders = & $ffmpeg -hide_banner -encoders 2>&1 | Out-String
$missing = @()
foreach ($f in 'gfxcapture', 'ddagrab', 'hwdownload', 'palettegen') { if ($filters -notmatch " $f ") { $missing += "filter $f" } }
foreach ($e in 'h264_nvenc', 'h264_amf', 'h264_qsv', 'h264_mf', 'aac', 'mjpeg', 'png', 'gif') { if ($encoders -notmatch " $e ") { $missing += "encoder $e" } }
if ($missing.Count -gt 0) { throw "FFmpeg build is missing: $($missing -join ', ')" }
$version = (& $ffmpeg -hide_banner -version | Select-Object -First 1)
$size = (Get-ChildItem $target | Measure-Object Length -Sum).Sum
Write-Host "OK: $version - $([math]::Round($size / 1MB)) MB in $target"
