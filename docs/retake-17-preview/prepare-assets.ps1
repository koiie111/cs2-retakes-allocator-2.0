param(
    [Parameter(Mandatory)][string]$SitePublic,
    [Parameter(Mandatory)][string]$GameArchive,
    [Parameter(Mandatory)][string]$Source2Viewer,
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot 'assets'
$equipment = Join-Path $assets 'equipment'
New-Item -ItemType Directory -Path $equipment -Force | Out-Null

$weapons = @('m4a1', 'm4a1_silencer', 'ak47', 'glock', 'usp_silencer', 'deagle', 'awp',
    'famas', 'galilar', 'p250', 'ssg08', 'mp9', 'mac10')
foreach ($weapon in $weapons) {
    $output = Join-Path $equipment "$weapon.svg"
    & $Source2Viewer -i $GameArchive -d -f "panorama/images/icons/equipment/$weapon.vsvg_c" -o $output
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $output)) {
        throw "Couldn't extract the CS2 equipment icon $weapon."
    }
}

$ui = Join-Path $assets 'ui'
New-Item -ItemType Directory -Path $ui -Force | Out-Null
$uiIcons = @{
    ct      = 'ct_logo'
    t       = 't_logo'
    default = 'ui/loadout'
    random  = 'ui/random'
    none    = 'ui/cancel'
}
foreach ($icon in $uiIcons.GetEnumerator()) {
    $output = Join-Path $ui "$($icon.Key).svg"
    & $Source2Viewer -i $GameArchive -d -f "panorama/images/icons/$($icon.Value).vsvg_c" -o $output
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $output)) {
        throw "Couldn't extract the CS2 UI icon $($icon.Value)."
    }
}

$radar = Join-Path $assets 'radar.png'
& $Source2Viewer -i $GameArchive -d -f 'panorama/images/overheadmaps/de_mirage_radar_psd.vtex_c' -o $radar
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $radar)) {
    throw "Couldn't extract the CS2 Mirage radar."
}

@'
from pathlib import Path
from sys import argv
from PIL import Image

source = Path(argv[1]) / "static" / "images"
target = Path(argv[2])
for filename, output, size in [
    (source / "maps/de_mirage.webp", "mirage.webp", 1600),
    (source / "maps/de_mirage_night.webp", "mirage-night.webp", 1600),
    (source / "skins/agents/t_agent_default.png", "agent-t.webp", 440),
    (source / "skins/agents/ct_agent_default.webp", "agent-ct.webp", 440),
    (target / "radar.png", "radar.webp", 256),
]:
    image = Image.open(filename).convert("RGBA")
    if output.startswith("agent-") and image.getextrema()[3][0] < 255:
        image = image.crop(image.getbbox())
    image.thumbnail((size, size))
    image.save(target / output, quality=88, method=6)
'@ | & $Python -X utf8 - $SitePublic $assets
if ($LASTEXITCODE -ne 0) {
    throw "Couldn't prepare map, agent and radar images. Check that Pillow is installed."
}
Remove-Item -LiteralPath $radar
