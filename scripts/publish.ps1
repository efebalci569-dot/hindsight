# Builds the self-contained release into artifacts\publish\win-x64 (Hindsight.exe + ffmpeg\).
# Run scripts/fetch-ffmpeg.ps1 first (or pass -FfmpegSource to copy a local build).
# Usage: pwsh scripts/publish.ps1 [-Version 0.1.0] [-FfmpegSource C:\ffmpeg\bin]
param(
    [string]$Version,
    [string]$FfmpegSource
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($FfmpegSource) { & (Join-Path $PSScriptRoot 'fetch-ffmpeg.ps1') -Source $FfmpegSource }
if (-not (Test-Path (Join-Path $root 'ffmpeg\ffmpeg.exe'))) { throw 'Run scripts/fetch-ffmpeg.ps1 first.' }

$out = Join-Path $root 'artifacts\publish\win-x64'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$props = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    # Single file without compression: the exe is memory-mapped, so unused parts never cost RAM.
    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false', '-p:IncludeNativeLibrariesForSelfExtract=false',
    '-p:DebugType=none', '-p:GenerateDocumentationFile=false', '-o', $out
)
if ($Version) { $props += "-p:Version=$Version" }
dotnet publish (Join-Path $root 'src\Hindsight.App\Hindsight.csproj') @props
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $out 'THIRD-PARTY-NOTICES.md')
$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host "Published to $out ($([math]::Round($size / 1MB)) MB)"
