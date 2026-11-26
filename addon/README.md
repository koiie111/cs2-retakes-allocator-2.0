# RETAKE #17 in Panorama

`retake17_menu` contains the welcome screen and the interactive grid. `retake17_toast` displays the bombsite, the team goal and the current queue position. The toast never captures player input. The [design reference](../docs/retake-17-design.md) records the approved visual rules. The interactive HTML prototype is in `docs/retake-17-preview`.

## Build and test

1. Build the allocator with .NET 10 and `-p:PlayerSettingsApiPath=/path/to/PlayerSettingsApi.dll` from the same shared assembly as the server. This variant requires the matching shared PlayerSettingsApi assembly at runtime and uses `settings:nfcore` for the welcome preference. The standard CI build uses a chat greeting and supports opening the introduction from `!retake`.
2. Generate the three approved images with `python addon/prepare-assets.py /path/to/4ill/public` and Pillow. Copy `addon/panorama` and `addon/materials` to the Workshop Tools addon's content directory. Compile both layouts, the stylesheet and three VTEX files. Game icons resolve from the base game.
3. Install the companion core build with the queue and command capabilities. The allocator reads `Config.EnablePanoramaHud` from its existing configuration. The default value is false. When migrating the local alias, disable the separate `RetakeMenuCompat` plugin so `css_guns` has one handler. `css_guns_legacy` opens the existing menu where its SharpModMenu driver is installed.
4. In an isolated LAN client, use a private search path for the compiled addon and test `!retake`, `!guns`, `!retake_admin` and `css_retake_close`. Verify the round toast, button clicks, preferences and applicable admin permissions. Keep `EnablePanoramaHud=false` on public servers until an unmodified remote client has downloaded the addon via the intended Workshop/MultiAddonManager route, rendered the menu, clicked a button and released input. A successful server-side spawn or local search-path test cannot establish delivery. Roll out the core first, then the allocator and shared dependencies. The center fallback stays available until the client has clicked the HUD.

The weapon preferences still use the existing allocator storage. For explicit default and random selection the persisted item IDs are reserved values outside the game's item IDs. Do not change those values after use.
