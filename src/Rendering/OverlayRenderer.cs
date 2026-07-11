using System;
using System.Reflection;
using Repainted.Data;
using Repainted.Util;
using UnityEngine;

namespace Repainted.Rendering
{
    /// <summary>
    /// Applies a tile's overlay entry — (style, color) rendered over
    /// whatever vanilla wall the tile truly has — to its renderer.
    ///
    /// CRITICAL INVARIANT (see ROADMAP-1.5 sharp edges): the overlay is
    /// written into the game's own cached MaterialPropertyBlock, never a
    /// fresh block. The game pushes that cached block itself on hover
    /// (onto the paint surface shader) and during the paint animation; a
    /// fresh renderer-only block would be stomped the moment either
    /// happens, making the overlay visibly flicker off.
    ///
    /// Property mapping:
    ///   _BaseMap    — read by BOTH the at-rest URP Lit material and the
    ///                 hover/paint surface shader.
    ///   _BaseColor  — at-rest URP Lit tint.
    ///   _BaseColorA — surface shader's base-layer tint (hover/painting).
    /// Setting all three keeps the overlay identical across every state.
    ///
    /// FullColor overlays: shared concrete albedo + tinted colors.
    /// Striped overlays:   generated band-tinted albedo + white colors
    ///                     (tint is baked into the texture).
    /// </summary>
    public static class OverlayRenderer
    {
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseColorAId = Shader.PropertyToID("_BaseColorA");

        private static FieldInfo mpbField;
        private static bool mpbFieldSearched;

        private static MaterialPropertyBlock GetCachedBlock(FloorClickable tile)
        {
            if (!mpbFieldSearched)
            {
                mpbFieldSearched = true;
                mpbField = typeof(FloorClickable).GetField("materialPropertyBlock",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (mpbField == null)
                {
                    RepaintedPlugin.Logger.LogError(
                        "OverlayRenderer: FloorClickable.materialPropertyBlock " +
                        "field not found — overlays cannot render.");
                }
            }
            if (mpbField == null) return null;

            var block = (MaterialPropertyBlock)mpbField.GetValue(tile);
            if (block == null)
            {
                // The game creates this in FloorClickable.Start(); only
                // absent if we run before Start (shouldn't happen — we
                // postfix methods Start itself calls). Create defensively.
                block = new MaterialPropertyBlock();
                mpbField.SetValue(tile, block);
            }
            return block;
        }

        /// <summary>
        /// Renders an overlay entry onto a tile. Writes into the game's
        /// cached MPB and pushes it. Safe to call repeatedly.
        /// </summary>
        public static void Apply(FloorClickable tile, MeshRenderer renderer,
            int tileId, TileColorStore.TileColorEntry entry)
        {
            if (tile == null || renderer == null) return;

            var block = GetCachedBlock(tile);
            if (block == null) return;

            var style = entry.ToWallType();
            Color savedColor = entry.ToColor();

            // 2.0 styles: decal-preserving recolor and pattern tint render
            // through their own path (native-res textures, material's own
            // color multiplier preserved).
            if (style == ModdedWallRegistry.WallType.DecalRecolor ||
                style == ModdedWallRegistry.WallType.DecalRecolorTint ||
                style == ModdedWallRegistry.WallType.PatternTint)
            {
                ApplyGeneratedAlbedo(tile, renderer, tileId, entry, style,
                    savedColor, block);
                return;
            }

            Texture map;
            Color tint;

            if (style == ModdedWallRegistry.WallType.FullColor)
            {
                // Full color: plain concrete albedo, color via tint —
                // identical mechanism to vanilla solid-color walls. Also
                // release any striped-texture binding this tile still holds.
                TextureGenerator.ReleaseWall(tileId);
                map = ModdedWallRegistry.VanillaConcreteMap;
                tint = savedColor;

                if (map == null)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"OverlayRenderer: no concrete map available for tile " +
                        $"{tileId}; tinting the underlying albedo instead.");
                }
            }
            else
            {
                bool isUpper = FloorResolver.IsUpperFloor(tile.transform, renderer);
                map = TextureGenerator.GetAndBindTintedTexture(
                    tileId,
                    ModdedWallRegistry.OriginalBaseMap, savedColor,
                    style, ModdedWallRegistry.GetBands(style), isUpper);
                tint = Color.white;

                if (map == null)
                {
                    // Generation failed — degrade to a full tint rather
                    // than rendering nothing.
                    map = ModdedWallRegistry.VanillaConcreteMap;
                    tint = savedColor;
                }
            }

            if (map != null)
                block.SetTexture(BaseMapId, map);
            block.SetColor(BaseColorId, tint);
            block.SetColor(BaseColorAId, tint);
            renderer.SetPropertyBlock(block);
        }

        /// <summary>
        /// 2.0 path: DecalRecolor / PatternTint. The generated texture is
        /// at the vanilla albedo's native resolution; the block's colors
        /// are set to the MATERIAL's own _BaseColor (not white, not the
        /// picked color) so decal pixels render mathematically identical
        /// to vanilla — the picked color is folded into the texture's tint
        /// ratio instead. Setting the colors explicitly (rather than
        /// leaving them) keeps the block stale-proof against earlier
        /// overlays on the same tile.
        /// </summary>
        private static void ApplyGeneratedAlbedo(FloorClickable tile,
            MeshRenderer renderer, int tileId,
            TileColorStore.TileColorEntry entry,
            ModdedWallRegistry.WallType style, Color savedColor,
            MaterialPropertyBlock block)
        {
            var dm = SingletonBehaviour<DecorationManager>.Instance;
            if (dm == null) return;

            int underlying = entry.underlyingIndex;
            var info = WallStyleInference.Classify(underlying);
            Color matColor = dm.GetColor(
                DecorationUI.DecorationType.WALL, underlying);

            // Degrade gracefully if the wall no longer matches the stored
            // style (game update changed the albedo): a decal style over a
            // non-decal wall falls back to a plain pattern tint; tint over
            // a wall with no stats falls back to full color.
            bool isDecal =
                info.Category == WallStyleInference.WallCategory.DecalOverBase ||
                info.Category == WallStyleInference.WallCategory.DecalOverFlat;
            bool isDecalStyle =
                style == ModdedWallRegistry.WallType.DecalRecolor ||
                style == ModdedWallRegistry.WallType.DecalRecolorTint;

            Texture map = null;
            if (isDecalStyle && isDecal)
            {
                bool tintFlavor =
                    style == ModdedWallRegistry.WallType.DecalRecolorTint;
                long key = TextureGenerator.MakeCacheKey(
                    savedColor, style, false, underlying);
                map = TextureGenerator.GetAndBind(tileId, key,
                    () => TextureGenerator.GenerateDecalRecolorTexture(
                        info, savedColor, matColor, tintFlavor));
            }
            else if (info.HasMean)
            {
                long key = TextureGenerator.MakeCacheKey(
                    savedColor, ModdedWallRegistry.WallType.PatternTint,
                    false, underlying);
                map = TextureGenerator.GetAndBind(tileId, key,
                    () => TextureGenerator.GeneratePatternTintTexture(
                        info, savedColor, matColor));
                if (isDecalStyle)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"Tile {tileId}: stored {style} but wall " +
                        $"{underlying} no longer classifies as a decal wall " +
                        $"({info.Category}) — rendering as pattern tint.");
                }
            }

            if (map == null)
            {
                // Last resort: behave like a FullColor overlay.
                TextureGenerator.ReleaseWall(tileId);
                map = ModdedWallRegistry.VanillaConcreteMap;
                if (map != null) block.SetTexture(BaseMapId, map);
                block.SetColor(BaseColorId, savedColor);
                block.SetColor(BaseColorAId, savedColor);
                renderer.SetPropertyBlock(block);
                return;
            }

            block.SetTexture(BaseMapId, map);
            block.SetColor(BaseColorId, matColor);
            block.SetColor(BaseColorAId, matColor);
            renderer.SetPropertyBlock(block);
        }
    }
}
