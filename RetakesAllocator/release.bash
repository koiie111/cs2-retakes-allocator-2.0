#!/usr/bin/env bash

set -euo pipefail
TARGET_DIR="./bin/Release/net10.0"
NEW_DIR="./bin/Release/RetakesAllocator"
SHARPMODMENU_ROOT="${SHARPMODMENU_ROOT:-../../SharpModMenu}"
SHARPMODMENU_COMPILED="$SHARPMODMENU_ROOT/compiled/counterstrikesharp"

# Replace the staging tree on reruns to avoid shipping stale files.
rm -rf -- ./bin/Release/RetakesAllocator
mkdir -p "$NEW_DIR"
cp -R "$TARGET_DIR/." "$NEW_DIR/"
rm -rf -- "$NEW_DIR/runtimes"
mkdir "$NEW_DIR/runtimes"
cp -R "$TARGET_DIR/runtimes/linux-x64" "$TARGET_DIR/runtimes/win-x64" "$NEW_DIR/runtimes/"
rm -f "$NEW_DIR/CounterStrikeSharp.API.dll"

if [ -d "$SHARPMODMENU_COMPILED" ]; then
  echo "Copying SharpModMenu from: $SHARPMODMENU_COMPILED"
  mkdir -p "$NEW_DIR/plugins" "$NEW_DIR/shared" "$NEW_DIR/configs/plugins"
  [ -d "$SHARPMODMENU_COMPILED/plugins/SharpModMenu" ] && cp -rf "$SHARPMODMENU_COMPILED/plugins/SharpModMenu" "$NEW_DIR/plugins/"
  [ -d "$SHARPMODMENU_COMPILED/shared/SharpModMenu" ] && cp -rf "$SHARPMODMENU_COMPILED/shared/SharpModMenu" "$NEW_DIR/shared/"
  [ -d "$SHARPMODMENU_COMPILED/shared/CSSUniversalMenuAPI" ] && cp -rf "$SHARPMODMENU_COMPILED/shared/CSSUniversalMenuAPI" "$NEW_DIR/shared/"
  [ -d "$SHARPMODMENU_COMPILED/configs/plugins/SharpModMenu" ] && cp -rf "$SHARPMODMENU_COMPILED/configs/plugins/SharpModMenu" "$NEW_DIR/configs/plugins/"
  if [ -f "$NEW_DIR/configs/plugins/SharpModMenu/sharpmodmenu_config.jsonc" ]; then
    sed -i "/\"HtmlCompactFooterMessage\"/c\\\t\"HtmlCompactFooterMessage\": \"<font color='#D10D0D'>Select: </font><font color='#F2A10F'>ZS/Use</font>\"," "$NEW_DIR/configs/plugins/SharpModMenu/sharpmodmenu_config.jsonc"
  fi
else
  echo "WARNING: SharpModMenu compiled output not found at $SHARPMODMENU_COMPILED. Build SharpModMenu first or set SHARPMODMENU_ROOT."
fi
