# Repainted 2.0.0 — decal walls & the Repainted! tab

Builds on 1.5.0 (the overlay rewrite; see CHANGELOG-1.5.0.md).

## New: a dedicated "Repainted!" tab

Our walls have moved out of the vanilla Decorations tab into their own
**Repainted!** tab (rainbow label, end of the management screen's tab
column), in the same card style you know from Decorations. The vanilla
Decorations tab is now completely untouched by the mod — no injected
cards, no layout re-flow — which removes the mod's most brittle UI code
and any interference with the game's own tab. Purchases carry over
(same walls, same shop state), and the tab gives us room to grow the
lineup without crowding vanilla.

## New: recolor the decal walls without touching the art

The 0.6.0 decoration walls that carry baked-in artwork are now
recolorable — the palette recolors the wall UNDERNEATH the art and leaves
the art itself untouched, byte-for-byte:

- **Red Beef Tiles** — recolor the tiles; the beef-cuts diagram stays.
- **Black Meat Brick Wall** — recolor the brick; the cow & chicken
  diagrams stay.
- **Summer Palm Wall** — recolor the background (paper grain preserved);
  the palms stay.

A recolored decal wall renders IDENTICALLY to its recolored base wall —
same tiles, same brick, same stucco — just with the art on top. The base
region is synthesized from the exact same source the base wall's recolor
uses (and follows the same Replace/Tint setting, chosen at paint time):
Red Beef Tiles match Red Butcher Tiles, Black Meat Brick matches Black
Brick Wall, Summer Palm matches the plain stucco walls. The eyedropper on
these walls samples the base color (not the art).

- **Toy Speckle Wall** is intentionally NOT recolorable — its full-surface
  rainbow doesn't combine with recoloring. The palette shows "Can't
  Repaint This Wall" and won't highlight it.

## New: pattern tint mode

`PatternedRecolorMode` config (ConfigurationManager, F1): how the palette
treats patterned walls (brick, tile, wallpaper) and decal-wall bases:

- **Tint** (default): keep the pattern — brick stays brick, in your
  color. The butcher tiles' grout and the black brick's mortar keep
  their ORIGINAL color (only the tile/brick faces recolor), exactly
  like real repainted masonry.
- **Replace**: flat color over the pattern (the 1.5 behavior; suits
  smooth walls, flattens detailed ones).

Applied at paint time, so you can flip it and repaint a neighboring tile
to compare side by side.

## Install change: single file

Repainted is now a single `Repainted.dll` — the palette model is embedded
in it. If you're updating from 1.x, delete the old
`BepInEx/plugins/Repainted/` folder (including `repainted_palette`) and
drop the new DLL in. (An advanced note: a loose `repainted_palette`
AssetBundle placed next to the DLL overrides the embedded model.)

## New: original palette model (CC0)

The palette tool has a brand-new 3D model — an original, procedurally
generated design (broken-egg board, seven rainbow dabs + the live
custom-color splotch), released under CC0-1.0. The old TurboSquid-licensed
model is gone, along with its redistribution restrictions: the mod now
contains no third-party licensed assets at all, and the model source is
fully reproducible from `palette-designs/generator/`.

The held model's orientation and scale are configurable (F1,
"PaletteModel" section, applied live) on top of the model's own authored
orientation — so if a game patch ever shifts the held-tool transforms
again, no mod update is needed to fix the pose. (Dev builds additionally
carry a numpad tuner with instant visual feedback; compiled out of
releases.)

## Fixed

- The held palette rendering rotated: the new model prefab carries its own
  axis correction in its root rotation, which the old code overwrote with
  constants calibrated for the previous model. Orientation offsets now
  compose WITH the prefab's authored rotation. The active-color splotch
  also works on the new single-renderer prefab (driven per sub-material).
- The Fish Market Wall could be misclassified as a two-tone wall and get a
  band recolor instead of a full repaint (the classifier tolerance sat
  exactly on its measured value; real two-tone walls measure zero, so the
  tolerance is now far tighter).

## Compatibility

- **DecorationsPlus**: the Repainted! tab is deliberately invisible to
  DecorationsPlus's window patches — its catalog keeps landing in the
  vanilla Decorations tab no matter which tab is opened first, and our
  tab's layout can't be re-flowed by it (our window subclass never
  invokes the patched Initialize). Recoloring its walls with the palette
  works: they classify as patterned, so both Replace and Tint modes
  apply.
- **Mega Paint Tool**: unchanged from 1.5 — area paint over recolored
  tiles clears our color correctly.

## Internal

- The Repainted! tab is injected by a prefix on `TabbedPanel.Start`
  (clone tab button + clone `FloorDecorationsWindow`, append to the
  panel's parallel serialized lists, let the vanilla wiring do the rest —
  same pattern as the hotbar injection). Scene hierarchy names were read
  from the level1 assets, not guessed. Wall card titles now go through a
  `Locale.GetWord` postfix (correct across language switches);
  `DecorationWindowPatch` (vanilla-tab injection + scroll re-flow) is
  deleted outright.

- Wall classification is now deterministic for decal/excluded walls:
  curated tables keyed by albedo texture name (the only stable identifier
  in the assets). Unknown walls fall back to Patterned; a log hint flags
  likely new decal walls after game updates.
- The decal ART LAYERS are reconstructed offline and embedded in the DLL
  (~1.4 MB): the game doesn't ship the standalone decal images, but since
  the background under every pixel is known (the paired base albedo or
  the flat background color), `assets/decalmasks/bake_masks.py` UNMIXES
  the composited albedos by color-line matting into foreground color +
  true alpha. This recovers clean soft edges through DXT block artifacts
  and antialiasing: thin stencil lettering keeps its counters (no more
  'O's filling in like a real stencil), dotted diagram lines survive, and
  recompositing over a new base color leaves no old-color halos. At
  runtime the decal albedo is never sampled — recolors composite the art
  layer over the synthesized base. A raw pixel-diff fallback remains for
  walls without baked art, and a resolution check flags when a game
  update changes a texture (re-run the bake script). Generated textures
  go through the same refcounted cache as stripe tints, with the
  underlying wall index folded into the cache key.
- Grout/mortar PRESERVE layers (same offline unmixing, one level down):
  Tint-flavor recolors of the butcher tiles, black brick, and Fish
  Market tiles colorize only the tile/brick faces — the colorize ratio
  targets the FACE mean, and the grout is composited back at its
  original color. Applies identically to the base walls and the decal
  walls' base synthesis, so pair parity holds. With this, every vanilla
  wall recolors properly except Toy Speckle (intentionally excluded).
- Generated recolor textures are capped at 2048² (vanilla ships its own
  decal walls at 2048 even over 4096 bases; an uncompressed 4096 chain
  would cost ~89 MB VRAM per color).
- Recolor guarantee: output at the vanilla albedo's native resolution,
  decal pixels byte-copied, only `_BaseMap` behavior changes — the
  material's own `_BaseColor` multiplier, normal map, and metallic map are
  left exactly as vanilla.
- Verified offline against the real 0.6.0 textures: decal regions
  byte-identical, recolored base lands on the picked color.
- Game version: built against Megastore Simulator 0.6.0 (Wall1–Wall30).
