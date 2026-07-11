# Repainted 1.5.0 — "recolor ANY wall"

Includes everything from the unreleased 1.1.0 (see CHANGELOG-1.1.0.md);
1.1.0 was folded into this release.

## The big change: the overlay rewrite

Repainted no longer owns wall tiles. Your recolors are now a pure overlay
layer rendered on top of whatever vanilla wall each tile truly has — the
game's save always holds the honest vanilla wall underneath.

What that means for you:

- **The Palette can repaint ANY wall.** Solid colors, the two-tone walls,
  brick, tile, wallpaper — click and it's your color. No purchase, no
  charge, no config option: recoloring is just what the palette does.
  (The old Instant Repaint options are gone — this is always on, and free.)
- **Style is automatic.** Recoloring a solid wall gives a full-color
  finish; recoloring a two-tone wall recolors its accent band (the band
  line matches vanilla exactly); patterned walls get a clean full-color
  repaint. To CHANGE a wall's style, paint it with a different wall from
  the shop — including our three shapes below.
- **The shop lineup is now 3 shape brushes**: Stripe+Trim, Trim, and
  High Stripe (1000 each, in line with vanilla walls). The old Full Color
  and Low Stripe entries are gone — vanilla's own solid and two-tone
  walls + the palette cover those looks. If you'd purchased any of the
  old walls, all 3 brushes are unlocked for you automatically.
- **Brushes paint in your active palette color** — including the paint
  animation. Repaint the same wall in a new color with the palette any
  time, still free.
- **Uninstalling the mod leaves a fully vanilla save.** Palette-recolored
  walls revert to the real wall underneath; brush-painted walls revert to
  the default wall. No leftover references, ever.
- **Old saves just work.** Existing recolors load unchanged — no
  migration, nothing to do.

## Fixed

- Painted walls no longer briefly revert to their base look while hovered
  with a tool (the overlay now lives in the same property block the game
  uses for hover/paint states).
- Painting a wall and dragging off before releasing the button can no
  longer leave a broken tile record in the save; stale records from older
  versions are repaired automatically on load.
- Shop scroll area no longer risks growing taller on repeated openings.

## Compatibility

- **Mega Paint Tool**: area-painting over Repainted-colored tiles now
  correctly clears our color (it used to be able to leave stale colors).
  One known quirk: area-painting with the exact same wall a recolor sits
  on leaves the recolor in place — repaint with the roller directly or
  use a different wall.
- **DecorationsPlus**: recoloring its custom walls with the palette works
  (our overlay applies last). Unsupported-but-functional; if you see
  conflicts, paint vanilla first, then recolor.
- Game version: built and tested against Megastore Simulator 0.6.0.

## Internal

- Save-time LFW_ sanitization, index re-pointing, and the instant-convert
  special case are all deleted — the mod's most update-sensitive machinery
  no longer exists. The only game-save writes left are a truthful index-0
  write-back at brush commit and a defensive repair of dangling records.
- New: `OverlayRenderer` (cached-MPB overlay application),
  `WallStyleInference` (auto style from the wall's albedo), per-entry
  `underlyingIndex` store field (additive; old files parse unchanged).
- Texture generation switched to 32-bit pixel buffers (¼ the allocation).
- Tint-source material now resolved by texture name, not list position.
