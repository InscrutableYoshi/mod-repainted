using System.Collections.Generic;
using Repainted.Data;
using UnityEngine;

namespace Repainted.Util
{
    /// <summary>
    /// Wall classification for the palette (2.0).
    ///
    /// Categories:
    ///   Solid         — shared concrete albedo → full-color overlay.
    ///   TwoTone       — concrete upper half + colored bottom band →
    ///                   band recolor (geometry matches vanilla).
    ///   Patterned     — brick/tile/wallpaper → full-color replace or
    ///                   multiplicative tint (config).
    ///   DecalOverBase — decal art baked over ANOTHER shop wall's albedo
    ///                   (Red Beef Tiles over Red Butcher Tiles; Black
    ///                   Meat Brick over Black Brick). Base recolorable,
    ///                   decal byte-preserved.
    ///   DecalOverFlat — decal art over a flat color + grain (Summer
    ///                   Palm). Same treatment, mask vs the flat mean.
    ///   Excluded      — walls the palette refuses to touch (Toy Speckle:
    ///                   full-surface rainbow, nothing sensible to
    ///                   recolor).
    ///
    /// Decal/excluded classification is DETERMINISTIC — curated tables
    /// keyed by albedo texture NAME (stable across list reorders and the
    /// only stable identifier the assets give us). Measured on 0.6.0:
    /// a heuristic can't safely separate Summer Palm (77% near-flat) from
    /// the Fish Market tile (83% near-flat), and a wrong guess breaks the
    /// "never touch the decal" guarantee. Unknown walls therefore fall
    /// back to Patterned; a log hint flags likely decal walls so new
    /// event walls get added to the tables at the next update pass.
    ///
    /// Two-tone tolerance is 12 (was 30): real two-tones measure ZERO
    /// summed-channel difference from concrete in their upper half, while
    /// the closest patterned wall (Fish Market) measures ~30 — the old
    /// tolerance sat exactly on it.
    /// </summary>
    public static class WallStyleInference
    {
        public enum WallCategory
        {
            Solid,
            TwoTone,
            Patterned,
            DecalOverBase,
            DecalOverFlat,
            Excluded
        }

        public sealed class WallClassInfo
        {
            public WallCategory Category;
            /// <summary>The wall's own albedo texture.</summary>
            public Texture AlbedoTex;
            /// <summary>Resolved clean-base albedo (DecalOverBase only).</summary>
            public Texture BaseTex;
            /// <summary>Mean RAW texture color of the recolorable region
            /// (flat: near-flat mean; pair: base albedo mean; patterned:
            /// overall mean). 0–255 per channel.</summary>
            public Color32 BaseMeanRaw;
            public bool HasMean;
        }

        // ─── Curated tables (albedo texture names, game 0.6.0) ─────────

        /// <summary>Walls the palette must not touch at all.</summary>
        private static readonly HashSet<string> ExcludedAlbedos = new HashSet<string>
        {
            // Toy Speckle Wall (Wall25): full-surface rainbow stripes.
            "Adsız tasarım - 2026-04-16T184512.991",
        };

        /// <summary>Decal albedo name → clean base albedo name (the base
        /// is another shop wall's texture, resolved from the live
        /// materials list).</summary>
        private static readonly Dictionary<string, string> DecalPairAlbedos =
            new Dictionary<string, string>
        {
            { "Wall28", "Wall27" },              // Red Beef Tiles → Red Butcher Tiles
            { "tile23WDecal", "Tile23Albedo" },  // Black Meat Brick → Black Brick
        };

        /// <summary>Decal art over a flat background color.</summary>
        private static readonly HashSet<string> FlatDecalAlbedos = new HashSet<string>
        {
            // Summer Palm Wall (Wall26): palms over flat peach + grain.
            "Adsız tasarım - 2026-06-06T195402.433",
        };

        /// <summary>
        /// Curated decal ART layers embedded in the DLL (RGBA: unmixed
        /// foreground + matte), baked offline by
        /// assets/decalmasks/bake_masks.py via color-line matting against
        /// the known base. Recovers proper soft alpha through DXT block
        /// noise and antialiased edges — thin stencil lettering keeps its
        /// counters, dotted lines survive, and recompositing over a new
        /// base color leaves no old-color halos. Keyed by albedo texture
        /// name. The raw runtime diff remains as a fallback for walls
        /// without baked art.
        /// </summary>
        private static readonly Dictionary<string, string> EmbeddedArtResources =
            new Dictionary<string, string>
        {
            { "Wall28", "Repainted.assets.decalmasks.art_Wall28.png" },
            { "tile23WDecal", "Repainted.assets.decalmasks.art_tile23WDecal.png" },
            { "Adsız tasarım - 2026-06-06T195402.433",
              "Repainted.assets.decalmasks.art_summer_palm.png" },
        };

        /// <summary>
        /// Grout/mortar PRESERVE layers for the base walls (RGBA: original
        /// grout color + matte), composited back after face colorization in
        /// Tint flavor — recoloring tiles/bricks leaves the grout alone.
        /// Same unmixing idea as the decal art, one level down. Keyed by
        /// BASE albedo texture name; applies to both the base wall's own
        /// PatternTint and the decal walls' tint-flavor base synthesis.
        /// </summary>
        private static readonly Dictionary<string, string> PreserveLayerResources =
            new Dictionary<string, string>
        {
            { "Wall27", "Repainted.assets.decalmasks.preserve_Wall27.png" },
            { "Tile23Albedo", "Repainted.assets.decalmasks.preserve_Tile23Albedo.png" },
            // Fish Market Wall (Wall24 albedo): white grout over varied
            // blue-grey tiles.
            { "Adsız tasarım - 2026-04-01T200132.265",
              "Repainted.assets.decalmasks.preserve_FishMarket.png" },
        };

        // ─── Thresholds (calibrated against all 30 wall albedos) ───────

        /// <summary>Two-tone upper-half match tolerance (summed channel
        /// diff, 0–765). Real two-tones: 0. Closest patterned: ~30.</summary>
        private const int CONCRETE_MATCH_TOLERANCE = 12;

        /// <summary>Per-pixel "near the flat mean" tolerance used for
        /// flat-decal masks and stats (summed channel diff).</summary>
        private const int NEAR_FLAT_TOLERANCE = 45;

        /// <summary>Soft decal-mask ramp: summed diff at/below D0 is fully
        /// base (tintable); at/above D1 fully decal (byte-preserved).</summary>
        private const int MASK_D0 = 12;
        private const int MASK_D1 = 48;

        private static readonly Dictionary<Texture, WallClassInfo> cache =
            new Dictionary<Texture, WallClassInfo>();
        private static readonly Dictionary<Texture, byte[]> maskCache =
            new Dictionary<Texture, byte[]>();
        private static readonly Dictionary<Texture, Color32[]> artCache =
            new Dictionary<Texture, Color32[]>();
        private static readonly Dictionary<string, Color32[]> preserveCache =
            new Dictionary<string, Color32[]>();

        private static Color32 concreteUpperAvg;
        private static bool concreteAvgComputed;

        public static void ClearCache()
        {
            cache.Clear();
            maskCache.Clear();
            artCache.Clear();
            preserveCache.Clear();
            concreteAvgComputed = false;
        }

        /// <summary>
        /// Lightweight excluded check — name table only, NO texture
        /// readback. Safe to call from the hover path every frame.
        /// </summary>
        public static bool IsExcluded(int decorationIndex)
        {
            var dm = SingletonBehaviour<DecorationManager>.Instance;
            if (dm == null) return false;
            Texture tex = dm.GetTexture(
                DecorationUI.DecorationType.WALL, decorationIndex);
            return tex != null && ExcludedAlbedos.Contains(tex.name);
        }

        /// <summary>
        /// Full classification (cached per texture; unknown textures cost
        /// one GPU→CPU readback on first call — do NOT call per frame).
        /// </summary>
        public static WallClassInfo Classify(int decorationIndex)
        {
            var dm = SingletonBehaviour<DecorationManager>.Instance;
            Texture tex = dm != null
                ? dm.GetTexture(DecorationUI.DecorationType.WALL, decorationIndex)
                : null;

            if (tex == null || tex == ModdedWallRegistry.VanillaConcreteMap)
                return new WallClassInfo
                {
                    Category = WallCategory.Solid,
                    AlbedoTex = tex
                };

            if (cache.TryGetValue(tex, out var cached))
                return cached;

            var info = BuildInfo(tex);
            cache[tex] = info;
            RepaintedPlugin.Logger.LogDebug(
                $"Classified wall {decorationIndex} ('{tex.name}'): " +
                $"{info.Category}");
            return info;
        }

        private static WallClassInfo BuildInfo(Texture tex)
        {
            var info = new WallClassInfo { AlbedoTex = tex };

            // Deterministic tables first — no pixels needed.
            if (ExcludedAlbedos.Contains(tex.name))
            {
                info.Category = WallCategory.Excluded;
                return info;
            }
            if (DecalPairAlbedos.TryGetValue(tex.name, out var baseName))
            {
                var baseTex = Patches.DecorationManagerAwakePatch
                    .FindWallAlbedoByName(baseName);
                if (baseTex != null)
                {
                    info.Category = WallCategory.DecalOverBase;
                    info.BaseTex = baseTex;
                    // Base mean now (one readback) so the eyedropper works
                    // before any paint has built the mask.
                    if (baseTex is Texture2D b2d)
                    {
                        try
                        {
                            ComputeStats(b2d, out _, out var baseMean, out _, out _);
                            info.BaseMeanRaw = baseMean;
                            info.HasMean = true;
                        }
                        catch (System.Exception ex)
                        {
                            RepaintedPlugin.Logger.LogWarning(
                                $"Base-mean stats failed for '{baseName}': {ex.Message}");
                        }
                    }
                    return info;
                }
                RepaintedPlugin.Logger.LogWarning(
                    $"Decal wall '{tex.name}' expects base albedo " +
                    $"'{baseName}' but no wall material carries it — a game " +
                    "update may have renamed it. Treating as Patterned.");
                // fall through to the stats pass
            }

            // Stats pass: one readback, stride-sampled.
            var t2d = tex as Texture2D;
            if (t2d == null || ModdedWallRegistry.OriginalBaseMap == null)
            {
                info.Category = WallCategory.Patterned;
                return info;
            }

            try
            {
                ComputeStats(t2d, out var upperAvg, out var meanAll,
                    out var nearMean, out float nearFrac);

                if (!concreteAvgComputed)
                {
                    ComputeStats(ModdedWallRegistry.OriginalBaseMap,
                        out concreteUpperAvg, out _, out _, out _,
                        alreadyReadable: true);
                    concreteAvgComputed = true;
                }

                int upDiff = Mathf.Abs(upperAvg.r - concreteUpperAvg.r)
                           + Mathf.Abs(upperAvg.g - concreteUpperAvg.g)
                           + Mathf.Abs(upperAvg.b - concreteUpperAvg.b);

                if (FlatDecalAlbedos.Contains(tex.name))
                {
                    info.Category = WallCategory.DecalOverFlat;
                    info.BaseMeanRaw = nearMean;
                    info.HasMean = true;
                    return info;
                }

                if (upDiff <= CONCRETE_MATCH_TOLERANCE)
                {
                    info.Category = WallCategory.TwoTone;
                    return info;
                }

                info.Category = WallCategory.Patterned;
                info.BaseMeanRaw = meanAll;
                info.HasMean = true;

                // Log-only hint: looks like an untabled flat-decal wall.
                if (nearFrac >= 0.60f)
                {
                    RepaintedPlugin.Logger.LogInfo(
                        $"Wall albedo '{tex.name}' is {nearFrac:P0} " +
                        "near-flat — if this is a new decal wall, add it " +
                        "to WallStyleInference's tables.");
                }
                return info;
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Wall classification failed for '{tex.name}': " +
                    $"{ex.Message} — defaulting to Patterned.");
                info.Category = WallCategory.Patterned;
                return info;
            }
        }

        /// <summary>
        /// Single stride-sampled stats pass over an albedo: upper-region
        /// average (V 0.5–0.9; texture row 0 = wall bottom), overall mean,
        /// near-flat mean, and near-flat fraction.
        /// </summary>
        private static void ComputeStats(Texture2D source,
            out Color32 upperAvg, out Color32 meanAll,
            out Color32 nearMean, out float nearFrac,
            bool alreadyReadable = false)
        {
            Texture2D readable = alreadyReadable
                ? source
                : TextureGenerator.GetReadableTexture(source);

            int w = readable.width, h = readable.height;
            Color32[] px = readable.GetPixels32();
            const int STEP = 8;

            long r = 0, g = 0, b = 0, n = 0;
            long ur = 0, ug = 0, ub = 0; long un = 0;
            int yUpFrom = Mathf.RoundToInt(h * 0.5f);
            int yUpTo = Mathf.RoundToInt(h * 0.9f);

            for (int y = 0; y < h; y += STEP)
            {
                int row = y * w;
                bool inUpper = y >= yUpFrom && y < yUpTo;
                for (int x = 0; x < w; x += STEP)
                {
                    var c = px[row + x];
                    r += c.r; g += c.g; b += c.b; n++;
                    if (inUpper) { ur += c.r; ug += c.g; ub += c.b; un++; }
                }
            }
            meanAll = Avg(r, g, b, n);
            upperAvg = un > 0 ? Avg(ur, ug, ub, un) : meanAll;

            // Second stride pass: near-flat stats relative to the mean.
            long nr = 0, ng = 0, nb = 0, nn = 0, total = 0;
            for (int y = 0; y < h; y += STEP)
            {
                int row = y * w;
                for (int x = 0; x < w; x += STEP)
                {
                    var c = px[row + x];
                    total++;
                    int d = System.Math.Abs(c.r - meanAll.r)
                          + System.Math.Abs(c.g - meanAll.g)
                          + System.Math.Abs(c.b - meanAll.b);
                    if (d < NEAR_FLAT_TOLERANCE)
                    {
                        nr += c.r; ng += c.g; nb += c.b; nn++;
                    }
                }
            }
            nearFrac = total > 0 ? (float)nn / total : 0f;
            nearMean = nn > 0 ? Avg(nr, ng, nb, nn) : meanAll;

            if (readable != source)
                Object.Destroy(readable);
        }

        private static Color32 Avg(long r, long g, long b, long n)
        {
            return new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
        }

        // ─── Decal masks ───────────────────────────────────────────────

        /// <summary>
        /// Per-pixel decal alpha for a decal wall, at the decal albedo's
        /// native resolution: 0 = base (tintable), 255 = decal
        /// (byte-preserved), soft ramp between MASK_D0..MASK_D1 summed
        /// diff. Cached per texture; cleared per scene.
        ///
        /// For DecalOverBase, also fills info.BaseMeanRaw from the base
        /// albedo (stride-sampled) the first time.
        /// </summary>
        public static byte[] GetDecalMask(WallClassInfo info,
            Color32[] decalPixels, int w, int h)
        {
            if (maskCache.TryGetValue(info.AlbedoTex, out var cachedMask))
                return cachedMask;

            byte[] mask = new byte[w * h];

            if (info.Category == WallCategory.DecalOverBase)
            {
                // Base resampled to the DECAL's resolution — only the mask
                // reference is resampled, never the decal pixels.
                Texture2D baseReadable = TextureGenerator.GetReadableTexture(
                    (Texture2D)info.BaseTex, w, h);
                Color32[] basePx = baseReadable.GetPixels32();

                long r = 0, g = 0, b = 0, n = 0;
                for (int i = 0; i < mask.Length; i++)
                {
                    var dp = decalPixels[i];
                    var bp = basePx[i];
                    int d = System.Math.Abs(dp.r - bp.r)
                          + System.Math.Abs(dp.g - bp.g)
                          + System.Math.Abs(dp.b - bp.b);
                    mask[i] = Ramp(d);
                    if ((i & 63) == 0) { r += bp.r; g += bp.g; b += bp.b; n++; }
                }
                if (!info.HasMean)
                {
                    info.BaseMeanRaw = Avg(r, g, b, n);
                    info.HasMean = true;
                }
                Object.Destroy(baseReadable);
            }
            else // DecalOverFlat
            {
                var m = info.BaseMeanRaw;
                for (int i = 0; i < mask.Length; i++)
                {
                    var dp = decalPixels[i];
                    int d = System.Math.Abs(dp.r - m.r)
                          + System.Math.Abs(dp.g - m.g)
                          + System.Math.Abs(dp.b - m.b);
                    mask[i] = Ramp(d);
                }
            }

            maskCache[info.AlbedoTex] = mask;
            return mask;
        }

        private static byte Ramp(int diff)
        {
            if (diff <= MASK_D0) return 0;
            if (diff >= MASK_D1) return 255;
            return (byte)((diff - MASK_D0) * 255 / (MASK_D1 - MASK_D0));
        }

        /// <summary>
        /// Loads a wall's curated decal ART layer (RGBA: unmixed foreground
        /// color + matte alpha) from embedded resources, cached per texture.
        /// PNG decode goes through Texture2D.LoadImage, so the art lands in
        /// the same bottom-up pixel order as base-albedo readbacks — indices
        /// align 1:1. Returns null (with a warning) if the resource is
        /// missing or the resolution doesn't match the live decal texture
        /// (game update changed it → re-bake with bake_masks.py).
        /// </summary>
        public static Color32[] GetDecalArt(WallClassInfo info, int w, int h)
        {
            if (info.AlbedoTex == null) return null;
            if (artCache.TryGetValue(info.AlbedoTex, out var cachedArt))
                return cachedArt;

            if (!EmbeddedArtResources.TryGetValue(info.AlbedoTex.name, out var resource))
                return null;

            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        RepaintedPlugin.Logger.LogWarning(
                            $"Embedded decal art '{resource}' not found — " +
                            "using runtime diff fallback.");
                        return null;
                    }
                    byte[] png;
                    using (var ms = new System.IO.MemoryStream())
                    {
                        stream.CopyTo(ms);
                        png = ms.ToArray();
                    }

                    var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                    if (!tmp.LoadImage(png))
                    {
                        Object.Destroy(tmp);
                        RepaintedPlugin.Logger.LogWarning(
                            $"Could not decode decal art '{resource}' — " +
                            "using runtime diff fallback.");
                        return null;
                    }
                    if (tmp.width != w || tmp.height != h)
                    {
                        RepaintedPlugin.Logger.LogWarning(
                            $"Decal art '{resource}' is {tmp.width}x{tmp.height} " +
                            $"but the live albedo is {w}x{h} — the game likely " +
                            "updated this texture; re-run " +
                            "assets/decalmasks/bake_masks.py. Using runtime " +
                            "diff fallback.");
                        Object.Destroy(tmp);
                        return null;
                    }

                    Color32[] art = tmp.GetPixels32();
                    Object.Destroy(tmp);
                    artCache[info.AlbedoTex] = art;
                    RepaintedPlugin.Logger.LogDebug(
                        $"Loaded curated decal art for '{info.AlbedoTex.name}'.");
                    return art;
                }
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Loading decal art '{resource}' failed: {ex.Message} — " +
                    "using runtime diff fallback.");
                return null;
            }
        }

        /// <summary>
        /// Loads a base wall's grout/mortar preserve layer at the requested
        /// resolution (GPU blit resample if the baked resolution differs).
        /// Returns null when the wall has no preserve layer — the caller
        /// colorizes the whole albedo as before.
        /// </summary>
        public static Color32[] GetPreserveLayer(string baseAlbedoName, int w, int h)
        {
            if (baseAlbedoName == null ||
                !PreserveLayerResources.TryGetValue(baseAlbedoName, out var resource))
                return null;

            string cacheKey = $"{baseAlbedoName}:{w}x{h}";
            if (preserveCache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        RepaintedPlugin.Logger.LogWarning(
                            $"Preserve layer '{resource}' not found — grout " +
                            "will be colorized with the faces.");
                        return null;
                    }
                    byte[] png;
                    using (var ms = new System.IO.MemoryStream())
                    {
                        stream.CopyTo(ms);
                        png = ms.ToArray();
                    }

                    var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                    if (!tmp.LoadImage(png))
                    {
                        Object.Destroy(tmp);
                        return null;
                    }

                    Color32[] px;
                    if (tmp.width == w && tmp.height == h)
                    {
                        px = tmp.GetPixels32();
                    }
                    else
                    {
                        var resized = TextureGenerator.GetReadableTexture(tmp, w, h);
                        px = resized.GetPixels32();
                        Object.Destroy(resized);
                    }
                    Object.Destroy(tmp);

                    preserveCache[cacheKey] = px;
                    RepaintedPlugin.Logger.LogDebug(
                        $"Loaded preserve layer for '{baseAlbedoName}' at {w}x{h}.");
                    return px;
                }
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Loading preserve layer '{resource}' failed: {ex.Message}.");
                return null;
            }
        }
    }
}
