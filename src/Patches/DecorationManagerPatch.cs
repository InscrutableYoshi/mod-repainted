using System.Collections.Generic;
using System.Reflection;
using Repainted.Data;
using HarmonyLib;
using UnityEngine;

namespace Repainted.Patches
{
    /// <summary>
    /// Injects the 3 brush shop walls (Stripe+Trim, Trim, High Stripe) into
    /// the decoration list, and owns the shared concrete source textures.
    ///
    /// 1.5 model: the brushes are the only decoration indices we occupy,
    /// and they are pure "paint brushes" — roller-painting one applies its
    /// band shape in the player's ACTIVE palette color, and the commit
    /// writes the game's tile record back to index 0 (FloorClickablePatch).
    /// Brush materials are re-baked in the active color at paint time
    /// (RefreshBrushBake) so the paint animation shows the color that
    /// actually lands.
    ///
    /// Also performs the one-time 1.x → 1.5 purchase-flag migration: the
    /// old FullColor/BottomStripe shop walls are gone (vanilla walls +
    /// palette recoloring replace them), so old flags are re-keyed onto
    /// the 3 kept brushes and orphans removed.
    /// </summary>
    [HarmonyPatch(typeof(DecorationManager), "Awake")]
    public static class DecorationManagerAwakePatch
    {
        /// <summary>Shared albedo asset name for plain concrete walls —
        /// the tint source for all generated overlay textures.</summary>
        private const string CONCRETE_MAP_NAME = "MB_Concrete_Wall_A";

        /// <summary>Game-save flag marking the purchase migration done
        /// (per profile, lives alongside the flags it migrates).</summary>
        private const string PURCHASE_MIGRATION_KEY = "RepaintedPurchaseMigration150";

        public static float BrushPrice = 1000f;

        private static FieldInfo wallMaterialsField;

        // Unity objects we created on the PREVIOUS scene load. Materials and
        // generated textures aren't garbage collected — each re-injection
        // must destroy the last batch or they accumulate every scene load
        // (CODE-REVIEW-2026-07.md M1).
        private static readonly List<Material> ownedMaterials = new List<Material>();
        private static readonly List<Texture2D> ownedTextures = new List<Texture2D>();
        private static Texture2D ownedOriginalBaseMap;

        // Per-brush bake state so RefreshBrushBake only regenerates when
        // the active color actually changed since the last bake.
        private static readonly Dictionary<ModdedWallRegistry.WallType, Color>
            lastBakedColors = new Dictionary<ModdedWallRegistry.WallType, Color>();
        private static readonly Dictionary<ModdedWallRegistry.WallType, Texture2D>
            brushTextures = new Dictionary<ModdedWallRegistry.WallType, Texture2D>();

        static void Postfix(DecorationManager __instance)
        {
            // DecorationManager uses SingletonBehaviour without DontDestroyOnLoad,
            // so each scene load creates a fresh instance whose wallMaterials list
            // is rebuilt from the prefab. Our static registry persists — reset it
            // and destroy last scene's Unity objects so nothing accumulates.
            ModdedWallRegistry.InjectedWalls.Clear();
            DestroyOwnedObjects();

            // Every wall tile re-requests its tint on SetMaterialInstant during
            // scene load, so dropping the whole tint cache here is safe and
            // clears any stale bindings from the previous scene/save.
            TextureGenerator.ClearCache();
            Util.WallStyleInference.ClearCache();

            // Configured brush price (editable via ConfigurationManager;
            // applied on each shop rebuild).
            if (RepaintedPlugin.CfgWallPrice != null)
                BrushPrice = RepaintedPlugin.CfgWallPrice.Value;

            // Load tile data for the active save slot. ChangeProfile() has
            // already been called by this point, so the profile index is set.
            TileColorStore.EnsureCorrectSlot();

            try
            {
                if (wallMaterialsField == null)
                {
                    wallMaterialsField = typeof(DecorationManager).GetField(
                        "wallMaterials",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                }

                if (wallMaterialsField == null)
                {
                    RepaintedPlugin.Logger.LogError("Could not find 'wallMaterials' field!");
                    return;
                }

                var wallMaterials = (List<Material>)wallMaterialsField.GetValue(__instance);
                if (wallMaterials == null || wallMaterials.Count == 0)
                {
                    RepaintedPlugin.Logger.LogError("wallMaterials is null or empty!");
                    return;
                }

                int preInjectCount = wallMaterials.Count;
                RepaintedPlugin.Logger.LogInfo($"Vanilla wall count: {preInjectCount}");

                // Resolve the concrete source by TEXTURE NAME rather than a
                // magic list index, so a game update reordering the wall
                // list can't hand us a patterned albedo as the tint source.
                Material sourceWall = null;
                Texture concreteMap = null;
                foreach (var m in wallMaterials)
                {
                    if (m == null || !m.HasProperty("_BaseMap")) continue;
                    var tex = m.GetTexture("_BaseMap");
                    if (tex != null && tex.name == CONCRETE_MAP_NAME)
                    {
                        sourceWall = m;
                        concreteMap = tex;
                        break;
                    }
                }
                if (sourceWall == null)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"No wall material with '{CONCRETE_MAP_NAME}' albedo " +
                        "found — falling back to list index 1. A game update " +
                        "may have renamed the shared concrete texture.");
                    sourceWall = wallMaterials.Count > 1 ? wallMaterials[1] : wallMaterials[0];
                    concreteMap = sourceWall.GetTexture("_BaseMap");
                }

                ModdedWallRegistry.VanillaConcreteMap = concreteMap;

                var sourceBaseMap = concreteMap as Texture2D;
                ModdedWallRegistry.OriginalBaseMap =
                    TextureGenerator.GetReadableTexture(sourceBaseMap);
                // GetReadableTexture returns the source itself when it's
                // already CPU-readable — only track (and later destroy) the
                // copy we own, never a vanilla asset.
                ownedOriginalBaseMap =
                    ModdedWallRegistry.OriginalBaseMap != sourceBaseMap
                        ? ModdedWallRegistry.OriginalBaseMap
                        : null;

                // Bake brushes in the ACTIVE palette color (slot data was
                // loaded above, so this is the player's last-used color).
                Color bakeColor = ModdedWallRegistry.CurrentColor;

                InjectBrush(wallMaterials, sourceWall, bakeColor,
                    "Repainted! Stripe+Trim", "Repainted_BottomTopStripe",
                    ModdedWallRegistry.WallType.BottomTopStripe);
                InjectBrush(wallMaterials, sourceWall, bakeColor,
                    "Repainted! Trim", "Repainted_Trim",
                    ModdedWallRegistry.WallType.Trim);
                InjectBrush(wallMaterials, sourceWall, bakeColor,
                    "Repainted! High Stripe", "Repainted_HighStripe",
                    ModdedWallRegistry.WallType.HighStripe);

                MigratePurchaseFlags(preInjectCount);

                RepaintedPlugin.Logger.LogInfo($"Total walls now: {wallMaterials.Count}");
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError($"Failed to inject brush walls: {ex}");
            }
        }

        /// <summary>
        /// Current wallMaterials list length, or 0 if unavailable. Used for
        /// bounds checks before calling DecorationManager getters (which
        /// silently clamp bad indices to 0).
        /// </summary>
        public static int GetWallMaterialCount()
        {
            try
            {
                var dm = SingletonBehaviour<DecorationManager>.Instance;
                if (dm == null || wallMaterialsField == null) return 0;
                var list = (List<Material>)wallMaterialsField.GetValue(dm);
                return list?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Finds the albedo texture of the wall material whose _BaseMap
        /// carries the given texture name, scanning the LIVE materials
        /// list. Used by WallStyleInference to resolve decal-pair bases
        /// by name (stable across list reorders). Returns null if absent.
        /// </summary>
        public static Texture FindWallAlbedoByName(string textureName)
        {
            try
            {
                var dm = SingletonBehaviour<DecorationManager>.Instance;
                if (dm == null || wallMaterialsField == null) return null;
                var list = (List<Material>)wallMaterialsField.GetValue(dm);
                if (list == null) return null;
                foreach (var m in list)
                {
                    if (m == null || !m.HasProperty("_BaseMap")) continue;
                    var tex = m.GetTexture("_BaseMap");
                    if (tex != null && tex.name == textureName)
                        return tex;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Re-bake a brush's material in the active palette color if it
        /// changed since the last bake. Called from the OnMouseButtonDown
        /// prefix so the paint animation (which samples GetTexture/GetColor
        /// for the active index) shows the color that will actually land.
        /// At most one texture generation per paint gesture.
        /// </summary>
        public static void RefreshBrushBake(ModdedWallRegistry.WallType type)
        {
            if (!ModdedWallRegistry.TryGetIndexForWallType(type, out var brush) ||
                brush.Material == null)
                return;

            Color active = ModdedWallRegistry.CurrentColor;
            if (lastBakedColors.TryGetValue(type, out var baked) &&
                ColorsClose(baked, active))
                return;

            Texture2D newTex = TextureGenerator.GenerateBandTintedTexture(
                ModdedWallRegistry.OriginalBaseMap, active,
                ModdedWallRegistry.GetBands(type));
            if (newTex == null) return;

            brush.Material.SetTexture("_BaseMap", newTex);

            if (brushTextures.TryGetValue(type, out var oldTex) && oldTex != null)
            {
                ownedTextures.Remove(oldTex);
                Object.Destroy(oldTex);
            }
            brushTextures[type] = newTex;
            ownedTextures.Add(newTex);
            lastBakedColors[type] = active;

            RepaintedPlugin.Logger.LogDebug(
                $"Re-baked brush {type} in active color " +
                $"({active.r:F2}, {active.g:F2}, {active.b:F2})");
        }

        private static bool ColorsClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.002f &&
                   Mathf.Abs(a.g - b.g) < 0.002f &&
                   Mathf.Abs(a.b - b.b) < 0.002f;
        }

        /// <summary>
        /// One-time (per profile) purchase-flag migration from the 1.x
        /// five-wall lineup to the 1.5 three-brush lineup. The game keys
        /// purchase state by "{DecorationType}{index}"; our old five
        /// occupied [preInjectCount .. preInjectCount+4] in the last 1.x
        /// session (best effort — assumes the vanilla/mod lineup didn't
        /// change in between). If the player had purchased ANY old wall,
        /// all 3 brushes are granted; old flags are removed either way so
        /// future vanilla walls can't inherit them.
        /// </summary>
        private static void MigratePurchaseFlags(int preInjectCount)
        {
            try
            {
                if (GenericDataSerializer.HasKey(PURCHASE_MIGRATION_KEY)) return;
                GenericDataSerializer.SaveBool(PURCHASE_MIGRATION_KEY, true);

                string wall = DecorationUI.DecorationType.WALL.ToString();
                bool anyOld = false;
                for (int i = 0; i < 5; i++)
                {
                    string key = wall + (preInjectCount + i);
                    if (GenericDataSerializer.HasKey(key))
                    {
                        anyOld = true;
                        GenericDataSerializer.DeleteKey(key);
                    }
                }

                if (anyOld)
                {
                    foreach (var brush in ModdedWallRegistry.InjectedWalls)
                        GenericDataSerializer.SaveBool(wall + brush.Index, true);
                    RepaintedPlugin.Logger.LogInfo(
                        "Migrated 1.x wall purchases: all 3 brushes granted, " +
                        "old flags removed.");
                }
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"Purchase-flag migration failed (non-fatal): {ex}");
            }
        }

        /// <summary>
        /// Destroys the materials/textures created by the previous injection
        /// pass. The vanilla list they were appended to is per-scene and has
        /// already been discarded by the time Awake runs again.
        /// </summary>
        private static void DestroyOwnedObjects()
        {
            foreach (var m in ownedMaterials)
                if (m != null) Object.Destroy(m);
            ownedMaterials.Clear();

            foreach (var t in ownedTextures)
                if (t != null) Object.Destroy(t);
            ownedTextures.Clear();
            brushTextures.Clear();
            lastBakedColors.Clear();

            if (ownedOriginalBaseMap != null)
            {
                Object.Destroy(ownedOriginalBaseMap);
                ownedOriginalBaseMap = null;
                ModdedWallRegistry.OriginalBaseMap = null;
            }
            ModdedWallRegistry.VanillaConcreteMap = null;
        }

        /// <summary>
        /// Creates a cloned brush material with its band texture baked in
        /// the given color, appends it to the wall materials list, and
        /// registers it in ModdedWallRegistry.
        /// </summary>
        private static void InjectBrush(
            List<Material> wallMaterials, Material sourceWall, Color bakeColor,
            string displayName, string materialName,
            ModdedWallRegistry.WallType wallType)
        {
            Material mat = Object.Instantiate(sourceWall);
            mat.name = materialName;
            ownedMaterials.Add(mat);

            var bands = ModdedWallRegistry.GetBands(wallType);
            Texture2D tintedBaseMap = TextureGenerator.GenerateBandTintedTexture(
                ModdedWallRegistry.OriginalBaseMap, bakeColor, bands);
            if (tintedBaseMap != null)
            {
                ownedTextures.Add(tintedBaseMap);
                brushTextures[wallType] = tintedBaseMap;
                lastBakedColors[wallType] = bakeColor;
                mat.SetTexture("_BaseMap", tintedBaseMap);
                mat.SetColor("_BaseColor", Color.white);
            }
            else
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Could not generate band texture for {displayName}, " +
                    "falling back to full tint");
                mat.SetColor("_BaseColor", bakeColor);
            }

            wallMaterials.Add(mat);
            int index = wallMaterials.Count - 1;

            ModdedWallRegistry.InjectedWalls.Add(
                new ModdedWallRegistry.ModdedWall(
                    index, displayName, mat, wallType, bands, BrushPrice
                )
            );

            RepaintedPlugin.Logger.LogDebug(
                $"Injected brush '{displayName}' ({wallType}) at index {index}");
        }
    }
}
