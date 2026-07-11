using System.Collections.Generic;
using Repainted.Data;
using Repainted.Util;
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

        // Key layout: bits 41-56 = underlying wall index (decal/pattern
        // styles only; 0 otherwise), bit 40 = floor, bits 32-39 = wallType,
        // bits 0-31 = packed RGBA. Underlying-index keying is safe because
        // the cache is session-scoped (cleared on every scene inject) and
        // decoration indices cannot shift mid-session.
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
            long key = MakeCacheKey(tint, wallType, isUpperFloor, 0);
            return GetAndBind(tileId, key, () =>
            {
                ModdedWallRegistry.ColorBand[] effectiveBands = isUpperFloor
                    ? TransformBandsForUpperFloor(bands, UPPER_FLOOR_V_MAX)
                    : bands;
                return GenerateBandTintedTexture(source, tint, effectiveBands);
            });
        }

        /// <summary>
        /// Generic bind: rebinds a tile to the texture for an arbitrary
        /// cache key, invoking the factory only on a cache miss. Same
        /// refcount semantics as GetAndBindTintedTexture.
        /// </summary>
        public static Texture2D GetAndBind(int tileId, long newKey,
            System.Func<Texture2D> factory)
        {
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
                Texture2D generated = factory();
                if (generated == null) return null;

                entry = new CacheEntry { Texture = generated, RefCount = 0 };
                tintCache[newKey] = entry;

                RepaintedPlugin.Logger.LogDebug(
                    $"Generated texture for key {newKey:X} " +
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
        /// Combines a packed color, wall type, upper-floor flag, and (for
        /// decal/pattern styles) the underlying wall index into a single
        /// cache key.
        /// </summary>
        public static long MakeCacheKey(Color c, ModdedWallRegistry.WallType wallType,
            bool isUpperFloor, int underlyingIndex)
        {
            long colorKey = (long)(uint)PackColor(c);
            long typeKey = ((long)wallType & 0xFF) << 32;
            long floorKey = isUpperFloor ? (1L << 40) : 0L;
            long underlyingKey = ((long)(underlyingIndex & 0xFFFF)) << 41;
            return underlyingKey | floorKey | typeKey | colorKey;
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

            // 32-bit path: quarter the managed allocation of GetPixels()
            // (Color32[] is 4 bytes/px vs Color's 16).
            Color32[] pixels = readable.GetPixels32();

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
                int mr = Mathf.RoundToInt(rowTint.r * 255f);
                int mg = Mathf.RoundToInt(rowTint.g * 255f);
                int mb = Mathf.RoundToInt(rowTint.b * 255f);

                int rowBase = y * TILE_RES_H;
                for (int x = 0; x < TILE_RES_H; x++)
                {
                    int i = rowBase + x;
                    Color32 p = pixels[i];
                    p.r = (byte)((p.r * mr + 127) / 255);
                    p.g = (byte)((p.g * mg + 127) / 255);
                    p.b = (byte)((p.b * mb + 127) / 255);
                    pixels[i] = p;
                }
            }

            Texture2D result = new Texture2D(
                TILE_RES_H, TILE_RES_V, TextureFormat.RGBA32, mipChain: true);
            result.name = source.name + "_BandTinted";
            result.wrapMode = source.wrapMode;
            result.filterMode = source.filterMode;
            result.SetPixels32(pixels);
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
            return GetReadableTexture(source, source.width, source.height);
        }

        /// <summary>
        /// CPU-readable copy at an explicit resolution (GPU blit resample).
        /// Always returns a NEW texture the caller must destroy — even when
        /// the source is readable — since resampling may be required.
        /// </summary>
        public static Texture2D GetReadableTexture(Texture2D source, int width, int height)
        {
            RenderTexture tmp = RenderTexture.GetTemporary(
                width, height, 0,
                RenderTextureFormat.Default, RenderTextureReadWrite.sRGB
            );

            // Capture before the blit — see GenerateBandTintedTexture for why.
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, tmp);
            RenderTexture.active = tmp;

            Texture2D readable = new Texture2D(
                width, height,
                TextureFormat.RGBA32, true
            );
            readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            return readable;
        }

        // ─── 2.0: decal-preserving recolor + pattern tint ──────────────

        /// <summary>
        /// Recolors a decal wall so its base region is IDENTICAL to what
        /// recoloring its base wall produces, with the decal art
        /// byte-preserved on top ("same thing, just with the decal
        /// appearing or not"):
        ///
        ///   • tintFlavor=false (Replace semantics, and always for
        ///     flat-background decals like Summer Palm whose base is the
        ///     stucco family): base region = shared concrete albedo ×
        ///     color — exactly the FullColor overlay the base wall gets.
        ///   • tintFlavor=true (Tint semantics, pair walls only): base
        ///     region = luminance colorize of the PAIRED BASE ALBEDO —
        ///     exactly the PatternTint the base wall gets.
        ///
        /// The base region is synthesized from the base source texture,
        /// NOT from the decal albedo's own pixels — so grout offsets,
        /// spackle highlights, and any other sub-threshold compositing
        /// noise in the decal albedo can't leak through. Decal pixels
        /// (mask 255) are copied verbatim; output is at the decal albedo's
        /// native resolution with identical UVs.
        /// </summary>
        public static Texture2D GenerateDecalRecolorTexture(
            WallStyleInference.WallClassInfo info, Color target, Color matColor,
            bool tintFlavor)
        {
            var decalTex = info.AlbedoTex as Texture2D;
            if (decalTex == null) return null;
            // Same VRAM cap as pattern tints; current decal albedos are
            // 2048² so this is a no-op today, but a future 4096² decal
            // would otherwise cost ~89 MB per cached color.
            int w = Mathf.Min(decalTex.width, MAX_GEN_RES);
            int h = Mathf.Min(decalTex.height, MAX_GEN_RES);

            // Base source: what the base wall's own recolor would sample.
            Texture2D baseSrc;
            if (tintFlavor &&
                info.Category == WallStyleInference.WallCategory.DecalOverBase &&
                info.BaseTex is Texture2D pairBase)
            {
                baseSrc = pairBase;
            }
            else
            {
                tintFlavor = false;
                baseSrc = ModdedWallRegistry.VanillaConcreteMap as Texture2D;
            }

            // Preferred path: curated art layer (unmixed foreground +
            // matte) composited over the synthesized base — the decal
            // albedo is never sampled, so its compositing noise, DXT
            // artifacts, and AA fringes can't leak into the result.
            Color32[] art = WallStyleInference.GetDecalArt(info, w, h);

            Color32[] outPx;
            if (baseSrc != null)
            {
                Texture2D baseReadable = GetReadableTexture(baseSrc, w, h);
                outPx = baseReadable.GetPixels32();
                Object.Destroy(baseReadable);

                if (tintFlavor)
                {
                    // Grout preserve layer (if the base wall has one):
                    // colorize the faces, then restore the grout at its
                    // original color — identical to the base wall's own
                    // PatternTint treatment.
                    Color32[] preserve = WallStyleInference.GetPreserveLayer(
                        baseSrc.name, w, h);
                    int lumaMean = preserve != null
                        ? FaceLumaMean(outPx, preserve)
                        : (77 * info.BaseMeanRaw.r + 150 * info.BaseMeanRaw.g +
                           29 * info.BaseMeanRaw.b) >> 8;
                    TintBaseInPlaceLuma(outPx, lumaMean, target, matColor);
                    if (preserve != null)
                        CompositeLayer(outPx, preserve);
                }
                else
                {
                    MultiplyBaseInPlace(outPx, target, matColor);
                }
            }
            else
            {
                // No base source available — degrade to colorizing the
                // decal albedo in place rather than failing outright.
                RepaintedPlugin.Logger.LogWarning(
                    $"Decal recolor: no base source texture for " +
                    $"'{decalTex.name}' — colorizing in place.");
                Texture2D readable0 = GetReadableTexture(decalTex, w, h);
                outPx = readable0.GetPixels32();
                byte[] fallbackMask = WallStyleInference.GetDecalMask(info, outPx, w, h);
                TintInPlace(outPx, fallbackMask, info.BaseMeanRaw, target, matColor);
                if (readable0 != decalTex) Object.Destroy(readable0);
                art = null;
            }

            if (art != null)
            {
                // Composite the curated art over the synthesized base.
                for (int i = 0; i < outPx.Length; i++)
                {
                    int a = art[i].a;
                    if (a == 0) continue;
                    if (a == 255)
                    {
                        outPx[i] = new Color32(art[i].r, art[i].g, art[i].b, 255);
                        continue;
                    }
                    int ia = 255 - a;
                    Color32 b = outPx[i];
                    outPx[i] = new Color32(
                        (byte)((b.r * ia + art[i].r * a + 127) / 255),
                        (byte)((b.g * ia + art[i].g * a + 127) / 255),
                        (byte)((b.b * ia + art[i].b * a + 127) / 255),
                        255);
                }
            }
            else if (baseSrc != null)
            {
                // Fallback: no curated art — blend the decal albedo's own
                // pixels over the synthesized base via the diff-ramp mask.
                Texture2D readable = GetReadableTexture(decalTex, w, h);
                Color32[] decalPx = readable.GetPixels32();
                byte[] mask = WallStyleInference.GetDecalMask(info, decalPx, w, h);
                for (int i = 0; i < outPx.Length; i++)
                {
                    int a = mask[i];
                    if (a == 0) continue;
                    if (a == 255) { outPx[i] = decalPx[i]; continue; }
                    int ia = 255 - a;
                    Color32 b = outPx[i];
                    Color32 dp = decalPx[i];
                    outPx[i] = new Color32(
                        (byte)((b.r * ia + dp.r * a + 127) / 255),
                        (byte)((b.g * ia + dp.g * a + 127) / 255),
                        (byte)((b.b * ia + dp.b * a + 127) / 255),
                        255);
                }
                if (readable != decalTex) Object.Destroy(readable);
            }

            Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true);
            result.name = decalTex.name + "_DecalRecolor";
            result.wrapMode = decalTex.wrapMode;
            result.filterMode = decalTex.filterMode;
            result.SetPixels32(outPx);
            result.Apply(updateMipmaps: true);
            return result;
        }

        /// <summary>
        /// Whole-buffer basePx × (target / matColor): after the material's
        /// own _BaseColor multiplier the base renders as source × target —
        /// identical to the base wall's FullColor (Replace) recolor.
        /// </summary>
        private static void MultiplyBaseInPlace(Color32[] px, Color target, Color matColor)
        {
            int mr = MulFP(target.r, matColor.r);
            int mg = MulFP(target.g, matColor.g);
            int mb = MulFP(target.b, matColor.b);

            for (int i = 0; i < px.Length; i++)
            {
                Color32 b = px[i];
                px[i] = new Color32(
                    (byte)System.Math.Min(255, (b.r * mr) >> 8),
                    (byte)System.Math.Min(255, (b.g * mg) >> 8),
                    (byte)System.Math.Min(255, (b.b * mb) >> 8),
                    255);
            }
        }

        /// <summary>
        /// Whole-buffer luminance colorize — identical math to the base
        /// wall's PatternTint recolor.
        /// </summary>
        private static void TintBaseInPlaceLuma(Color32[] px, int lumaMean,
            Color target, Color matColor)
        {
            int rr = RatioFP(target.r, lumaMean, matColor.r);
            int rg = RatioFP(target.g, lumaMean, matColor.g);
            int rb = RatioFP(target.b, lumaMean, matColor.b);

            for (int i = 0; i < px.Length; i++)
            {
                Color32 b = px[i];
                int luma = (77 * b.r + 150 * b.g + 29 * b.b) >> 8;
                px[i] = new Color32(
                    (byte)System.Math.Min(255, (luma * rr) >> 8),
                    (byte)System.Math.Min(255, (luma * rg) >> 8),
                    (byte)System.Math.Min(255, (luma * rb) >> 8),
                    255);
            }
        }

        /// <summary>Fixed-point (×256) multiplier target/matColor, clamped.</summary>
        private static int MulFP(float targetC, float matColorC)
        {
            float m = Mathf.Clamp(targetC / Mathf.Max(0.02f, matColorC), 0f, 6f);
            return Mathf.RoundToInt(m * 256f);
        }

        /// <summary>
        /// Cap for generated recolor textures. Vanilla ships its own decal
        /// walls at 2048 even when the base is 4096, and an uncompressed
        /// 4096 RGBA32 chain costs ~89 MB VRAM per color — 2048 matches
        /// vanilla decal fidelity at a quarter the memory.
        /// </summary>
        private const int MAX_GEN_RES = 2048;

        /// <summary>
        /// Luminance colorize of a patterned wall's own albedo — brick
        /// stays brick, in the target color. Walls with a baked PRESERVE
        /// layer (butcher tile grout, black-brick mortar) get their grout
        /// composited back at its ORIGINAL color, and the colorize ratio
        /// targets the face mean (excluding grout) so faces land exactly
        /// on the picked color.
        /// </summary>
        public static Texture2D GeneratePatternTintTexture(
            WallStyleInference.WallClassInfo info, Color target, Color matColor)
        {
            var albedo = info.AlbedoTex as Texture2D;
            if (albedo == null) return null;

            int w = Mathf.Min(albedo.width, MAX_GEN_RES);
            int h = Mathf.Min(albedo.height, MAX_GEN_RES);

            Texture2D readable = GetReadableTexture(albedo, w, h);
            Color32[] px = readable.GetPixels32();
            Object.Destroy(readable);

            Color32[] preserve = WallStyleInference.GetPreserveLayer(albedo.name, w, h);
            int lumaMean = preserve != null
                ? FaceLumaMean(px, preserve)
                : (77 * info.BaseMeanRaw.r + 150 * info.BaseMeanRaw.g +
                   29 * info.BaseMeanRaw.b) >> 8;

            TintBaseInPlaceLuma(px, lumaMean, target, matColor);
            if (preserve != null)
                CompositeLayer(px, preserve);

            Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true);
            result.name = albedo.name + "_PatternTint";
            result.wrapMode = albedo.wrapMode;
            result.filterMode = albedo.filterMode;
            result.SetPixels32(px);
            result.Apply(updateMipmaps: true);
            return result;
        }

        /// <summary>Mean luminance of the non-preserved (face) pixels —
        /// the region the colorize ratio should land on target.</summary>
        private static int FaceLumaMean(Color32[] px, Color32[] preserve)
        {
            long sum = 0, n = 0;
            for (int i = 0; i < px.Length; i += 8)
            {
                if (preserve[i].a >= 64) continue;
                sum += (77 * px[i].r + 150 * px[i].g + 29 * px[i].b) >> 8;
                n++;
            }
            return n > 0 ? (int)(sum / n) : 128;
        }

        /// <summary>Straight-alpha composite of an RGBA layer over px.</summary>
        private static void CompositeLayer(Color32[] px, Color32[] layer)
        {
            for (int i = 0; i < px.Length; i++)
            {
                int a = layer[i].a;
                if (a == 0) continue;
                if (a == 255)
                {
                    px[i] = new Color32(layer[i].r, layer[i].g, layer[i].b, 255);
                    continue;
                }
                int ia = 255 - a;
                Color32 b = px[i];
                px[i] = new Color32(
                    (byte)((b.r * ia + layer[i].r * a + 127) / 255),
                    (byte)((b.g * ia + layer[i].g * a + 127) / 255),
                    (byte)((b.b * ia + layer[i].b * a + 127) / 255),
                    255);
            }
        }

        /// <summary>
        /// In-place LUMINANCE colorize with optional decal mask (255 =
        /// pixel byte-preserved, soft ramp blends the AA edge).
        ///
        /// Each base pixel's luminance carries the texture detail (grout
        /// lines, mortar, paper grain); the target color is mapped onto it
        /// so that a pixel at the base's mean luminance renders EXACTLY the
        /// picked color (after the material's own _BaseColor multiplier).
        /// Pure multiplicative tint was tried first and fails on saturated
        /// bases — the butcher wall's deep red has G/B channels near zero,
        /// which no multiplier can turn teal. Luminance colorize hits the
        /// target regardless of base hue. Fixed-point (×256) integer math.
        /// </summary>
        private static void TintInPlace(Color32[] px, byte[] mask,
            Color32 baseMeanRaw, Color target, Color matColor)
        {
            // Mean base luminance (Rec.601 integer approx).
            int lumaMean = (77 * baseMeanRaw.r + 150 * baseMeanRaw.g +
                            29 * baseMeanRaw.b) >> 8;

            // ratio_c = target_c / (lumaMean/255 × matColor_c), ×256 FP,
            // clamped so near-black bases can't blow out.
            int rr = RatioFP(target.r, lumaMean, matColor.r);
            int rg = RatioFP(target.g, lumaMean, matColor.g);
            int rb = RatioFP(target.b, lumaMean, matColor.b);

            for (int i = 0; i < px.Length; i++)
            {
                int a = mask != null ? mask[i] : 0;
                if (a == 255) continue; // pure decal — byte-preserved

                Color32 p = px[i];
                int luma = (77 * p.r + 150 * p.g + 29 * p.b) >> 8;
                int tr = System.Math.Min(255, (luma * rr) >> 8);
                int tg = System.Math.Min(255, (luma * rg) >> 8);
                int tb = System.Math.Min(255, (luma * rb) >> 8);

                if (a == 0)
                {
                    p.r = (byte)tr; p.g = (byte)tg; p.b = (byte)tb;
                }
                else
                {
                    int ia = 255 - a;
                    p.r = (byte)((tr * ia + p.r * a + 127) / 255);
                    p.g = (byte)((tg * ia + p.g * a + 127) / 255);
                    p.b = (byte)((tb * ia + p.b * a + 127) / 255);
                }
                px[i] = p;
            }
        }

        /// <summary>Fixed-point (×256) colorize ratio, clamped to [0, 6×]
        /// so near-black bases can't blow out.</summary>
        private static int RatioFP(float targetC, int lumaMean, float matColorC)
        {
            float denom = (lumaMean / 255f) * Mathf.Max(0.02f, matColorC);
            float ratio = Mathf.Clamp(targetC / Mathf.Max(0.02f, denom), 0f, 6f);
            return Mathf.RoundToInt(ratio * 256f);
        }
    }
}
