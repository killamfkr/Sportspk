# Builds Release output and creates a Jellyfin catalog .zip (dll + meta.json + channel-thumb.png).
# Run from repo root:  .\scripts\build-plugin-package.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet build -c Release
$bin = Join-Path $root "bin\Release\net9.0"
$dll = Join-Path $bin "Jellyfin.Plugin.StreamedPk.dll"
$meta = Join-Path $bin "meta.json"
$thumb = Join-Path $bin "channel-thumb.png"
if (-not (Test-Path $dll)) { throw "Build output missing: $dll" }
if (-not (Test-Path $thumb)) { throw "Build output missing: $thumb" }

$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$zipName = "live-matches_1.3.3.0.zip"
$zipPath = Join-Path $artifacts $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $dll, $meta, $thumb -DestinationPath $zipPath -Force

$md5 = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "Created: $zipPath"
Write-Host "MD5 (for manifest.json): $md5"
Write-Host ""
Write-Host "Upload this zip to GitHub Release v1.3.3 as: $zipName"
