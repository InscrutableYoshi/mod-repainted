using System.Collections.Generic;
using Repainted.Data;
using UnityEngine;

namespace Repainted
{
    /// <summary>
    /// Generates tinted wall textures at runtime with a refcount-based cache.
    ///
    /// Cache key is (color, wallType, isUpperFloor). Each entry tracks how many
    /// wall tiles currently reference it; when that count hits zero the texture
    /// is destroyed and the entry removed. The cache is therefore bounded by
    /// the number of unique (color, wallType, floor) combinations currently
    /// in use in the world — no orphan accumulation, no cap, no flush.
    ///
    /// Runtime callers (FloorClickablePatch, ColorPaletteTool) must go through
    /// <see cref="GetAndBindTintedTexture"/> / <see cref="ReleaseWall"/> so
    /// refcounts stay accurate. The injection-time caller in
    /// DecorationManagerPatch uses <see cref="GenerateBandTintedTexture"/>
    /// directly since shop-preview materials are never swapped or released.
    /// </summary>
    public static class TextureGenerator
    {
        /// <summary>
        /// Asymmetric resolution for generated tinted base-maps. Our band
        /// math only varies by Y, so horizontal detail in the generated
        /// texture is irrelevant — keeping it low is free perf. The vertical
        /// axis carries the stripe edge, where bilinear filtering width
        /// scales with 1/TILE_RES_V; matching vanilla here keeps edges as
        /// sharp as the 2K baseline.
        ///
        /// Cost per texture (RGBA32 + mipmaps): TILE_RES_H × TILE_RES_V × 4 ÷ 0.75.
        /// Default 512 × 2048 → ~4 MB per texture (vs 16 MB at 2K², 1 MB at 512²).
        /// </summary>
        private const int TILE_RES_H = 512;
        private const int TILE_RES_V = 2048;

        /// <summary>
        /// Visible mesh-V ceiling on upper-floor walls. The upper floor's
        /// ceiling is lower than the ground floor's, so the top of each
        /// 7.4-unit-tall wall mesh extends above the visible ceiling and
        /// gets clipped out. Empirical fit: on ground floor the "high stripe"
        /// (V∈[0.5, 0.7]) sits well below the ceiling; on upper floor the
        /// same band's top is essentially at the ceiling, giving vMax ≈ 0.71.
        /// </summary>
        public const float UPPER_FLOOR_V_MAX = 0.71f;

        private class CacheEntry
        {
            public Texture2D Texture;
            public int RefCount;
        }

        // Key layout: bit 40 = floor, bits 32-39 = wallType, bits 0-31 = packed RGBA.
        private static readonly Dictionary<long, CacheEntry> tintCache =
            new Dictionary<long, CacheEntry>();

        // tileId → the cache key this tile's MPB currently points at, so
        // rebinds can decrement the old key before incrementing the new one.
        private static readonly Dictionary<int, long> tileToCacheKey =
            new Dictionary<int, long>();

        /// <summary>
        /// Atomically rebinds a wall tile to a tinted texture keyed by
        /// (tint, wallType, isUpperFloor). Handles the refcount bookkeeping:
        /// decrements the tile's prior key (if any) and increments the new
        /// one. Generates the texture on a cache miss and destroys the prior
        /// entry's texture if the decrement drops its refcount to zero.
        /// </summary>
        public static Texture2D GetAndBindTintedTexture(
            int tileId,
            Texture2D source, Color tint,
            ModdedWallRegistry.WallType wallType,
            ModdedWallRegistry.ColorBand[] bands,
            bool isUpperFloor = false)
        {
            long newKey = MakeCacheKey(tint, wallType, isUpperFloor);

            // Early out — already bound to the same key.
            if (tileToCacheKey.TryGetValue(tileId, out long oldKey) &&
                oldKey == newKey &&
                tintCache.TryGetValue(newKey, out var existing) &&
                existing.Texture != null)
            {
                return existing.Texture;
            }

            // Release prior binding (decrements old entry; evicts + destroys
            // the texture if the decrement drops refcount to zero).
            ReleaseWallInternal(tileId);

            // Get or create the new entry.
            if (!tintCache.TryGetValue(newKey, out var entry) || entry.Texture == null)
            {
                ModdedWallRegistry.ColorBand[] effectiveBands = isUpperFloor
                    ? TransformBandsForUpperFloor(bands, UPPER_FLOOR_V_MAX)
                    : bands;

                Texture2D generated = GenerateBandTintedTexture(source, tint, effectiveBands);
                if (generated == null) return null;

                entry = new CacheEntry { Texture = generated, RefCount = 0 };
                tintCache[newKey] = entry;

                RepaintedPlugin.Logger.LogDebug(
                    $"Generated tint texture for {wallType} color #{PackColor(tint):X8} " +
                    $"floor={(isUpperFloor ? "upper" : "ground")} " +
                    $"(live entries: {tintCache.Count})");
            }

            entry.RefCount++;
            tileToCacheKey[tileId] = newKey;
            return entry.Texture;
        }

        /// <summary>
        /// Releases any tint-texture binding this tile currently holds. Use
        /// when a wall switches to FullColor (which doesn't use a tint
        /// texture), reverts to a vanilla decoration, or stops being a
        /// striped modded wall for any other reason. Safe to call on tiles
        /// with no binding.
        /// </summary>
        public static void ReleaseWall(int tileId)
        {
            ReleaseWallInternal(tileId);
        }

        private static void ReleaseWallInternal(int tileId)
        {
            if (!tileToCacheKey.TryGetValue(tileId, out long key))
                return;

            tileToCacheKey.Remove(tileId);

            if (!tintCache.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                if (entry.Texture != null)
                    Object.Destroy(entry.Texture);
                tintCache.Remove(key);
            }
        }

        /// <summary>
        /// Remaps a ground-floor band layout onto the visible portion of an
        /// upper-floor wall, keeping each band's V-width (= world-height,
        /// since the two floors' wall meshes are the same size) and scaling
        /// the whitespace above it proportionally.
        ///
        /// Rules:
        ///   • Bottom-anchored bands (StartY == 0): unchanged. Bottom stripe
        ///     always reads from the floor, independent of ceiling height.
        ///   • Top-anchored bands (EndY == 1): shift down so EndY == vMax.
        ///     Top trim moves from [0.85, 1.0] → [vMax-0.15, vMax].
        ///   • Middle-floating bands: scale "distance from ceiling" by vMax,
        ///     preserving V-width. High stripe (top at V=0.7) with ground-
        ///     floor gap-above 0.3 → new gap-above 0.3*vMax ≈ 0.21, placing
        ///     the stripe lower in mesh-V but the same size.
        /// </summary>
        private static ModdedWallRegistry.ColorBand[] TransformBandsForUpperFloor(
            ModdedWallRegistry.ColorBand[] bands, float vMax)
        {
            if (bands == null) return null;
            var result = new ModdedWallRegistry.ColorBand[bands.Length];
            for (int i = 0; i < bands.Length; i++)
            {
                var b = bands[i];
                float width = b.EndY - b.StartY;

                // Bottom-anchored — keep as-is.
                if (b.StartY <= 0f)
                {
                    result[i] = b;
                    continue;
                }

                // Top-anchored — flush to new ceiling.
                if (b.EndY >= 1f)
                {
                    float newEnd = vMax;
                    float newStart = Mathf.Max(0f, newEnd - width);
                    result[i] = new ModdedWallRegistry.ColorBand(newStart, newEnd, b.Feather);
                    continue;
                }

                // Middle-floating — scale distance from ceiling by vMax.
                float gapAbove = 1f - b.EndY;
                float newEndMid = Mathf.Clamp01(vMax - gapAbove * vMax);
                float newStartMid = Mathf.Max(0f, newEndMid - width);
                result[i] = new ModdedWallRegistry.ColorBand(newStartMid, newEndMid, b.Feather);
            }
            return result;
        }

        /// <summary>
        /// Combines a packed color int, wall type, and upper-floor flag into
        /// a single cache key.
        /// </summary>
        private static long MakeCacheKey(Color c, ModdedWallRegistry.WallType wallType, bool isUpperFloor)
        {
            long colorKey = (long)(uint)PackColor(c);
            long typeKey = (long)wallType << 32;
            long floorKey = isUpperFloor ? (1L << 40) : 0L;
            return floorKey | typeKey | colorKey;
        }

        /// <summary>
        /// Packs a Color into a single int for use as a dictionary key.
        /// Quantizes to 8 bits per channel to merge near-identical colors.
        /// </summary>
        private static int PackColor(Color c)
        {
            int ri = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int gi = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int bi = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            int ai = Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
            return (ri << 24) | (gi << 16) | (bi << 8) | ai;
        }

        /// <summary>
        /// Destroys every cached texture and clears all bindings. Use on
        /// scene unload / slot change if desired; with refcounting this is
        /// rarely necessary since entries evict naturally as walls rebind.
        /// </summary>
        public static void ClearCache()
        {
            foreach (var e in tintCache.Values)
            {
                if (e.Texture != null)
                    Object.Destroy(e.Texture);
            }
            tintCache.Clear();
            tileToCacheKey.Clear();
        }

        /// <summary>
        /// Creates a new <c>TILE_RES_H × TILE_RES_V</c> Texture2D where
        /// pixels inside any of the specified bands are tinted with the
        /// given color. The source albedo is downsampled via GPU blit (one
        /// step) into the asymmetric target — the vertical axis stays at
        /// vanilla 2K to keep stripe edges sharp under bilinear filtering,
        /// while the horizontal axis drops to 512 since our tint has no
        /// horizontal variation worth preserving.
        ///
        /// Mipmaps are generated so distant walls don't shimmer; total
        /// per-texture cost is ≈4 MB of VRAM.
        /// </summary>
        public static Texture2D GenerateBandTintedTexture(
            Texture2D source,
            Color tint,
            ModdedWallRegistry.ColorBand[] bands)
        {
            if (source == null)
            {
                RepaintedPlugin.Logger.LogError("Source texture is null!");
                return null;
            }

            // GPU blit source → TILE_RES_H × TILE_RES_V RT. Downsamples in one shot.
            RenderTexture tmp = RenderTexture.GetTemporary(
                TILE_RES_H, TILE_RES_V, 0,
                RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);

            // Capture prev BEFORE the blit. Graphics.Blit leaves the dest
            // bound as RenderTexture.active, so capturing after would set
            // prev = tmp and we'd restore it as active right before releasing
            // it — which Unity warns about.
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, tmp);
            RenderTexture.active = tmp;

            Texture2D readable = new Texture2D(
                TILE_RES_H, TILE_RES_V, TextureFormat.RGBA32, mipChain: false);
            readable.ReadPixels(new Rect(0, 0, TILE_RES_H, TILE_RES_V), 0, 0);
            readable.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);

            Color[] pixels = readable.GetPixels();

            for (int y = 0; y < TILE_RES_V; y++)
            {
                float normalizedY = (float)y / TILE_RES_V;

                // Maximum tint strength at this row from all bands.
                float tintStrength = 0f;
                foreach (var band in bands)
                {
                    float s = GetBandStrength(normalizedY, band);
                    if (s > tintStrength) tintStrength = s;
                }

                if (tintStrength < 0.001f) continue; // untinted row, skip

                Color rowTint = Color.Lerp(Color.white, tint, tintStrength);

                int rowBase = y * TILE_RES_H;
                for (int x = 0; x < TILE_RES_H; x++)
                {
                    pixels[rowBase + x] *= rowTint;
                }
            }

            Texture2D result = new Texture2D(
                TILE_RES_H, TILE_RES_V, TextureFormat.RGBA32, mipChain: true);
            result.name = source.name + "_BandTinted";
            result.wrapMode = source.wrapMode;
            result.filterMode = source.filterMode;
            result.SetPixels(pixels);
            result.Apply(updateMipmaps: true);

            Object.Destroy(readable);

            return result;
        }

        /// <summary>
        /// Returns how strongly a band applies at the given normalizedY (0–1).
        /// Inside the band: 1.0. Outside: 0.0. If feather > 0, edges are
        /// smoothly blended; if feather == 0, edges are pixel-sharp.
        /// </summary>
        private static float GetBandStrength(float normalizedY, ModdedWallRegistry.ColorBand band)
        {
            float feather = band.Feather;

            // Hard edge path — no blending at all
            if (feather <= 0f)
            {
                return (normalizedY >= band.StartY && normalizedY <= band.EndY) ? 1f : 0f;
            }

            // Below the band
            if (normalizedY < band.StartY - feather)
                return 0f;

            // Above the band
            if (normalizedY > band.EndY + feather)
                return 0f;

            // In the core of the band
            if (normalizedY >= band.StartY + feather && normalizedY <= band.EndY - feather)
                return 1f;

            // In the bottom feather zone
            if (normalizedY < band.StartY + feather)
            {
                return Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(band.StartY - feather, band.StartY + feather, normalizedY));
            }

            // In the top feather zone
            return Mathf.SmoothStep(1f, 0f,
                Mathf.InverseLerp(band.EndY - feather, band.EndY + feather, normalizedY));
        }

        /// <summary>
        /// Returns a CPU-readable copy of a texture at the source's native
        /// resolution. Used by the eyedropper to sample vanilla wall colors,
        /// where we need accurate per-pixel data. The tint generator does NOT
        /// go through this — it downsamples via a single GPU blit directly.
        /// </summary>
        public static Texture2D GetReadableTexture(Texture2D source)
        {
            if (source.isReadable)
                return source;

            RenderTexture tmp = RenderTexture.GetTemporary(
                source.width, source.height, 0,
                RenderTextureFormat.Default, RenderTextureReadWrite.sRGB
            );

            // Capture before the blit — see GenerateBandTintedTexture for why.
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, tmp);
            RenderTexture.active = tmp;

            Texture2D readable = new Texture2D(
                source.width, source.height,
                TextureFormat.RGBA32, true
            );
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            return readable;
        }
    }
}
