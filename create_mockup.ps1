# create_mockup.ps1
# Usage: Open PowerShell in workspace root and run:
#   powershell -ExecutionPolicy Bypass -File .\create_mockup.ps1
# Requirements: ImageMagick (magick) in PATH, SGR.ico present in workspace root.

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$root = $scriptDir
$magick = "magick"

if (-not (Get-Command $magick -ErrorAction SilentlyContinue)) {
    Write-Error "ImageMagick 'magick' not found in PATH. Install ImageMagick and ensure 'magick' is available."
    exit 1
}

$iconSrc = Join-Path $root "SGR.ico"
if (-not (Test-Path $iconSrc)) {
    Write-Error "SGR.ico not found in workspace root: $iconSrc"
    exit 1
}

$mockupDir = Join-Path $root "Assets\Mockups"
New-Item -ItemType Directory -Path $mockupDir -Force | Out-Null

$icon512 = Join-Path $mockupDir "SGR_512.png"
$icon128 = Join-Path $mockupDir "SGR_128.png"
$mockupFull = Join-Path $mockupDir "dashboard_mockup_1920x1080.png"
$mockupThumb = Join-Path $mockupDir "dashboard_mockup_thumb.png"
$readme = Join-Path $mockupDir "README.md"

Write-Host "Converting icon..."
& $magick "$iconSrc" -resize 512x512 "$icon512"
& $magick "$iconSrc" -resize 128x128 "$icon128"

Write-Host "Compositing mockup (this may take a few seconds)..."
& $magick -size 1920x1080 xc:"#1E1E1E" `
  ( -size 1920x1080 gradient:"#263044"-"#111217" -rotate 45 -alpha set -channel A -evaluate set 50% ) -compose overlay -composite `
  ( "$icon512" -resize 72x72 ) -gravity northwest -geometry +40+20 -composite `
  -font "Segoe UI" -fill "#FFFFFF" -pointsize 36 -gravity northwest -annotate +120+26 "HZ Lua Manager" `
  -font "Segoe UI" -fill "#A8A8A8" -pointsize 16 -gravity northwest -annotate +120+62 "Your Comprehensive lua management solution" `
  -fill "#232326" -draw "roundrectangle 120,180 540,340 12,12" `
  -draw "roundrectangle 580,180 1000,340 12,12" -draw "roundrectangle 120,360 540,520 12,12" -draw "roundrectangle 580,360 1000,520 12,12" `
  -fill "rgba(58,134,255,0.12)" -stroke none -draw "roundrectangle 140,320 260,352 8,8" -draw "roundrectangle 600,320 720,352 8,8" `
  -fill "#A8A8A8" -pointsize 14 -font "Segoe UI Semibold" -gravity northwest -annotate +140+196 "Scripts" -annotate +600+196 "Plugins" `
  -fill "#A8A8A8" -pointsize 16 -gravity northwest -annotate +140+234 "Manage Lua scripts, installs, and updates." -annotate +600+234 "Browse installed plugins and settings." `
  -fill "#FFFFFF" -pointsize 14 -gravity northwest -annotate +140+400 "Recent Activity" -fill "#A8A8A8" -pointsize 12 -annotate +140+430 "Last run 2 mins ago · 4 pending updates" `
  -gravity south -fill "#A8A8A8" -pointsize 12 -annotate +0+20 "v1.0.0  •  © HZ Lua Manager" "$mockupFull"

if (-not (Test-Path $mockupFull)) {
    Write-Error "Failed to generate mockup at $mockupFull"
    exit 1
}

Write-Host "Creating thumbnail..."
& $magick "$mockupFull" -resize 512x288 "$mockupThumb"

Write-Host "Writing README..."
@"
Mockups for HZ Lua Manager

Files:
- SGR_512.png
- SGR_128.png
- dashboard_mockup_1920x1080.png
- dashboard_mockup_thumb.png

Design tokens:
- Accent: #3A86FF
- Background: #1E1E1E
- Card background suggestion: #232326
- Font: Segoe UI (system)

How to reproduce:
1. Install ImageMagick (magick) and ensure it's in PATH.
2. Place SGR.ico in workspace root.
3. Run: powershell -ExecutionPolicy Bypass -File .\create_mockup.ps1
"@ | Out-File -FilePath $readme -Encoding utf8

Write-Host "Done. Generated files in: $mockupDir"
Get-ChildItem -Path $mockupDir | Select-Object Name, Length
