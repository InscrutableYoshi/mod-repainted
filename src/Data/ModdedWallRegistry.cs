using System;
using System.Collections.Generic;
using UnityEngine;

namespace Repainted.Data
{
    /// <summary>
    /// Tracks the mod's overlay styles, the 3 injected "brush" shop walls,
    /// and the active paint color. Populated by DecorationManagerPatch,
    /// consumed by the overlay renderer, palette tool, and shop UI patch.
    ///
    /// 1.5 model: styles are index-INDEPENDENT (an overlay is tileId →
    /// (style, color) rendered over whatever vanilla wall the tile truly
    /// has). Only the 3 brush shop walls occupy decoration indices, and
    /// those never persist into the game save (commit writes them back
    /// to index 0).
    /// </summary>
    public static class ModdedWallRegistry
    {
        public static List<ModdedWall> InjectedWalls = new List<ModdedWall>();

        /// <summary>The currently selected color for painting.</summary>
        public static Color CurrentColor = new Color(0.8f, 0.8f, 0.8f, 1.0f);

        /// <summary>
        /// Fired whenever the active color changes via ApplyColor().
        /// Subscribers receive the new color. Used by ColorPaletteTool
        /// to update the splotch on the palette model.
        /// </summary>
        public static event Action<Color> OnColorChanged;

        /// <summary>
        /// CPU-readable copy of the shared concrete albedo
        /// (MB_Concrete_Wall_A). Source for band-tinted texture generation
        /// and eyedropper compensation.
        /// </summary>
        public static Texture2D OriginalBaseMap;

        /// <summary>
        /// The shared concrete albedo as the game's own GPU asset (never
        /// destroyed by us). Used directly as the _BaseMap override for
        /// FullColor overlays and as the reference for "is this wall a
        /// plain solid-color wall" checks.
        /// </summary>
        public static Texture VanillaConcreteMap;

        // ─── Style band table (index-independent) ─────────────────────

        /// <summary>Top of the low stripe band — matches the vanilla
        /// two-tone band line (verified in the released mod).</summary>
        public const float STRIPE_TOP = 0.224f;

        /// <summary>Bottom of the top trim band.</summary>
        public const float TOP_TRIM = 0.85f;

        /// <summary>
        /// Band layout for each overlay style. Styles exist independently
        /// of shop walls: all five are valid overlay styles; only three
        /// (BottomTopStripe, Trim, HighStripe) also exist as shop brushes.
        /// </summary>
        public static ColorBand[] GetBands(WallType type)
        {
            switch (type)
            {
                case WallType.FullColor:
                    return new[] { new ColorBand(0f, 1f, 0f) };
                case WallType.BottomStripe:
                    return new[] { new ColorBand(0f, STRIPE_TOP) };
                case WallType.BottomTopStripe:
                    return new[] { new ColorBand(0f, STRIPE_TOP),
                                   new ColorBand(TOP_TRIM, 1.0f) };
                case WallType.Trim:
                    return new[] { new ColorBand(0f, 0.10f),
                                   new ColorBand(TOP_TRIM, 1.0f) };
                case WallType.HighStripe:
                    return new[] { new ColorBand(0.50f, 0.70f) };
                default:
                    return new[] { new ColorBand(0f, 1f, 0f) };
            }
        }

        /// <summary>
        /// Defines a horizontal band on the wall texture where color is applied.
        /// Y values are normalized 0–1 (bottom to top). Feather controls the
        /// smoothness of the edge transition.
        /// </summary>
        public struct ColorBand
        {
            public float StartY;
            public float EndY;
            public float Feather;

            public ColorBand(float startY, float endY, float feather = 0f)
            {
                StartY = startY;
                EndY = endY;
                Feather = feather;
            }
        }

        public struct ModdedWall
        {
            public int Index;
            public string DisplayName;
            public float Price;
            public Material Material;
            public WallType Type;
            public ColorBand[] Bands;

            public ModdedWall(int index, string displayName, Material material,
                WallType type, ColorBand[] bands, float price = 0f)
            {
                Index = index;
                DisplayName = displayName;
                Price = price;
                Material = material;
                Type = type;
                Bands = bands;
            }

            /// <summary>
            /// True if this wall type applies color via _BaseColor only
            /// (no texture generation needed — the entire surface is one color).
            /// </summary>
            public bool IsFullColor => Type == WallType.FullColor;
        }

        /// <summary>
        /// Wall type enum — stored as int in save files for persistence.
        /// New types get new values; old values must not be reused.
        /// </summary>
        public enum WallType
        {
            FullColor = 0,
            // HalfColor = 1,  // Retired — was bottom 50%, replaced by BottomStripe
            BottomStripe = 2,
            BottomTopStripe = 3,
            Trim = 4,
            HighStripe = 5,

            /// <summary>2.0: recolor a decal wall (Red Beef Tiles, Black
            /// Meat Brick, Summer Palm…) EXACTLY like its base wall
            /// recolors in Replace mode — base region synthesized from the
            /// shared concrete albedo × color — with the decal art
            /// byte-preserved on top. Which wall (and therefore which
            /// mask) comes from the entry's underlyingIndex.</summary>
            DecalRecolor = 6,

            /// <summary>2.0: luminance colorize of a patterned wall's own
            /// albedo (brick stays brick, in your color). Underlying wall
            /// identity from the entry's underlyingIndex.</summary>
            PatternTint = 7,

            /// <summary>2.0: decal-wall recolor whose base region matches
            /// the base wall's TINT-mode recolor (luminance colorize of
            /// the paired base albedo), decal byte-preserved on top.
            /// Chosen at paint time from the PatternedRecolor config, same
            /// as the base wall would be.</summary>
            DecalRecolorTint = 8
        }

        /// <summary>
        /// Sets the current picker color and notifies subscribers (palette
        /// splotch, picker UI). Applied to tiles per-paint via
        /// MaterialPropertyBlocks — no shared material modification needed.
        /// </summary>
        public static void ApplyColor(Color color)
        {
            CurrentColor = color;
            OnColorChanged?.Invoke(color);
        }

        /// <summary>
        /// Returns true if the given decoration index belongs to one of our walls.
        /// </summary>
        public static bool IsModdedWall(int decorationIndex)
        {
            foreach (var wall in InjectedWalls)
            {
                if (wall.Index == decorationIndex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the ModdedWall entry for a given decoration index, if it exists.
        /// </summary>
        public static bool TryGetModdedWall(int decorationIndex, out ModdedWall result)
        {
            foreach (var wall in InjectedWalls)
            {
                if (wall.Index == decorationIndex)
                {
                    result = wall;
                    return true;
                }
            }
            result = default;
            return false;
        }

        /// <summary>
        /// Gets the current runtime decoration index for a given WallType.
        /// Used by the save compatibility system: TileColorStore saves WallType
        /// (stable across updates), and this method resolves it to whatever
        /// index that wall type was assigned this session.
        /// </summary>
        public static bool TryGetIndexForWallType(WallType type, out ModdedWall result)
        {
            foreach (var wall in InjectedWalls)
            {
                if (wall.Type == type)
                {
                    result = wall;
                    return true;
                }
            }
            result = default;
            return false;
        }
    }
}
