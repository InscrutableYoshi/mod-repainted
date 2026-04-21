using System;
using System.Collections.Generic;
using UnityEngine;

namespace Repainted.Data
{
    /// <summary>
    /// Tracks which wall indices were injected by our mod, their materials,
    /// and current colors. Populated by DecorationManagerPatch, consumed by
    /// DecorationWindowPatch and ColorPickerOverlay.
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

        /// <summary>The original (unmodified) base texture from the vanilla source wall.</summary>
        public static Texture2D OriginalBaseMap;

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
            HighStripe = 5
        }

        /// <summary>
        /// Sets the current picker color. The paint system picks this up
        /// via our GetColor/GetTexture patches on DecorationManager — no
        /// shared material modification needed.
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
