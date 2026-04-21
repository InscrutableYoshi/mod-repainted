<p align="center">
  <img src="images/logo.png" alt="Repainted" width="420">
</p>

A Megastore Simulator mod that adds five new wallpapers to the decoration shop and a **Palette** tool that lets you recolor them on the fly — any color, any time, no cost.

<p align="center">
  <img src="images/rainbow.jpg" alt="A rainbow of repainted walls" width="800">
</p>

## What it adds

**5 new wallpapers**, sold in the decoration shop alongside the vanilla walls:

- **Repainted! Full Color** — a solid-color wall.
- **Repainted! Low Stripe** — a colored band at the bottom (same shape as the vanilla two-tone walls).
- **Repainted! Stripe + Trim** — a low stripe plus a top trim.
- **Repainted! Trim** — a low and top trim.
- **Repainted! High Stripe** — a colored band across the upper section of the wall.

Two of these mirror the shape of existing vanilla walls; three are new designs.

<p align="center">
  <img src="images/wallvariations.jpg" alt="The five Repainted! wall styles" width="800">
</p>

**The Palette** — a new hotbar tool that sits next to the megaphone (hotkey **4**). Point it at any Repainted! wall and recolor it instantly: no paint cost, no paint-roller animation, just pick a color and click.

<p align="center">
  <img src="images/palette.jpg" alt="The Palette tool in use" width="800">
</p>

## Usage

1. Buy a **Repainted!** wall from the decoration shop.
2. Apply it to a wall with the paint roller, exactly like any other wallpaper.
3. Switch to the **Palette** tool (hotkey **4**).
4. Point it at a Repainted! wall and:
   - **Left click** — paint the wall with the currently selected color.
   - **Right click** — open the color picker to choose a new color.
   - **Shift + left click** — eyedropper: copy the color from whatever wall you're pointing at.

## Installation

**Requires Tobey's [BepInEx Pack for Megastore Simulator](https://www.nexusmods.com/megastoresimulator/mods/2)** 

1. Install Tobey's BepInEx pack into your Megastore Simulator folder per its own instructions, then launch the game once so it can generate its folder structure.
2. Download the latest `Repainted-v*.zip` from the [Releases](../../releases) page.
3. Extract it into `Megastore Simulator/BepInEx/plugins/`. You should end up with:
   ```
   BepInEx/plugins/Repainted/
     Repainted.dll
     repainted_palette
   ```
4. Launch the game.

## Uninstallation

Delete the `BepInEx/plugins/Repainted/` folder. Any tiles previously painted with Repainted! walls will revert to a plain vanilla wall.

Your saved color data is kept in `BepInEx/config/` in case you reinstall later. Delete the Repainted files there too if you want to wipe all saved walls and settings.

## License

Code and authored assets: **MIT** (see `LICENSE`).

One third-party asset shipped in releases is separately licensed — see `NOTICE.md` for details.

## Credits

Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony).

Megastore Simulator is a game by Yolo Games Studio. This mod is unofficial and not affiliated with or endorsed by the developer.
