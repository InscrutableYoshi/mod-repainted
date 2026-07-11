# Repainted 1.1.0

## Fixed
- **Hover tooltips and button hints broken for all tools** (scanner inventory
  tooltip, etc.): removed an obsolete ButtonsWindow patch that worked around a
  vanilla bug the game itself fixed in 0.5.5; since then the patch threw an
  exception on every hotbar UI refresh.
- Save-delete hook could read the wrong save slot when deleting the current
  profile; the slot index is now captured before the game's delete runs.
- Eyedropper on vanilla patterned walls caused a visible hitch (bulk pixel
  read instead of ~150K individual GetPixel calls).
- Upper/ground floor detection now measures mesh bounds (the quantity it was
  calibrated on) instead of the transform pivot, and warns if a wall lands in
  the calibration gap.
- Materials and generated textures from previous scene loads are now
  destroyed on re-injection (slow memory growth over long sessions).
- Slot files are written atomically (crash mid-write can no longer truncate
  them); unreadable slot files are backed up to `.bad` instead of silently
  discarded; tile parsing now tolerates field reordering.
- If the save-sync hook fails to install, colors now flush to disk on every
  paint (previously the advertised fallback didn't exist).
- Eyedropper bounds-checks decoration indices (the game silently clamps bad
  indices to wall 0).

## Added
- **Color wheel picker**: HSV wheel + brightness slider is the new default
  custom-color view; the classic H/S/V sliders remain one click away
  (UI > PickerStyle in config).
- **Instant Repaint** (config, off by default): the Palette can paint vanilla
  walls directly — the tile converts to a Repainted Full Color wall in the
  current color. Optionally charges the wall's shop price once per tile
  (on by default).
- Config file (`BepInEx/config/com.custommods.repainted.cfg`), editable
  in-game via ConfigurationManager (F1): instant repaint, price charging,
  picker style, wall shop price.
- Startup reflection self-check: if a game update changes internals the mod
  depends on, one clear error is logged at load instead of per-click
  exceptions later.

## Internal
- Every Harmony patch body is now exception-guarded.
- Hotbar/tool injection failures degrade gracefully with a single error line.
- Diagnostic-only patches are compiled out of release builds.
- Shop grid math reads the actual column count from the layout group.
