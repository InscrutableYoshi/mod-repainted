using System.Collections.Generic;
using System.Reflection;
using Repainted.Data;
using HarmonyLib;
using UnityEngine;

namespace Repainted.Patches
{
    /// <summary>
    /// Injects color-variable wall materials into the decoration list.
    ///
    /// Uses the vanilla URP Lit shader (same as all game walls) to guarantee
    /// pixel-identical rendering. Color tinting is achieved by:
    ///   - Full-color wall: sets _BaseColor on a cloned vanilla material
    ///   - Stripe walls: generates a tinted _BaseMap texture at runtime
    ///     with color applied only within defined bands
    ///
    /// DecorationManager holds a serialized List&lt;Material&gt; wallMaterials (private).
    /// We postfix Awake() to append our materials after the vanilla list is loaded.
    /// </summary>
    [HarmonyPatch(typeof(DecorationManager), "Awake")]
    public static class DecorationManagerAwakePatch
    {
        // Shop prices for each modded wall. Declared as variables rather than
        // consts so a future options menu can rewrite them at runtime. Values
        // are applied whenever DecorationManager.Awake runs (per scene load),
        // so changes take effect the next time the shop is rebuilt.
        public static float PriceFullColor = 2500f;
        public static float PriceLowStripe = 2500f;
        public static float PriceStripeTrim = 2500f;
        public static float PriceTrim = 2500f;
        public static float PriceHighStripe = 2500f;

        private static FieldInfo wallMaterialsField;

        static void Postfix(DecorationManager __instance)
        {
            // DecorationManager uses SingletonBehaviour without DontDestroyOnLoad,
            // so each scene load creates a fresh instance whose wallMaterials list
            // is rebuilt from the prefab. Our static InjectedWalls list, however,
            // persists — so without this clear we'd accumulate stale duplicates
            // (with dangling Material refs) on every scene transition. Resetting
            // here keeps the static registry in lockstep with the per-instance list.
            ModdedWallRegistry.InjectedWalls.Clear();

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

                RepaintedPlugin.Logger.LogInfo($"Vanilla wall count: {wallMaterials.Count}");

                Material sourceWall = wallMaterials.Count > 1 ? wallMaterials[1] : wallMaterials[0];

                ModdedWallRegistry.OriginalBaseMap =
                    TextureGenerator.GetReadableTexture(sourceWall.GetTexture("_BaseMap") as Texture2D);

                Color neutralDefault = new Color(0.8f, 0.8f, 0.8f, 1.0f);

                const float STRIPE_TOP = 0.224f;
                const float TOP_TRIM = 0.85f;

                InjectWall(wallMaterials, sourceWall, neutralDefault,
                    "Repainted! Full Color", "Repainted_Full",
                    ModdedWallRegistry.WallType.FullColor,
                    new ModdedWallRegistry.ColorBand[]
                    {
                        new ModdedWallRegistry.ColorBand(0f, 1f, 0f)
                    },
                    PriceFullColor
                );

                InjectWall(wallMaterials, sourceWall, neutralDefault,
                    "Repainted! Low Stripe", "Repainted_BottomStripe",
                    ModdedWallRegistry.WallType.BottomStripe,
                    new ModdedWallRegistry.ColorBand[]
                    {
                        new ModdedWallRegistry.ColorBand(0f, STRIPE_TOP)
                    },
                    PriceLowStripe
                );

                InjectWall(wallMaterials, sourceWall, neutralDefault,
                    "Repainted! Stripe+Trim", "Repainted_BottomTopStripe",
                    ModdedWallRegistry.WallType.BottomTopStripe,
                    new ModdedWallRegistry.ColorBand[]
                    {
                        new ModdedWallRegistry.ColorBand(0f, STRIPE_TOP),
                        new ModdedWallRegistry.ColorBand(TOP_TRIM, 1.0f)
                    },
                    PriceStripeTrim
                );

                InjectWall(wallMaterials, sourceWall, neutralDefault,
                    "Repainted! Trim", "Repainted_Trim",
                    ModdedWallRegistry.WallType.Trim,
                    new ModdedWallRegistry.ColorBand[]
                    {
                        new ModdedWallRegistry.ColorBand(0f, 0.10f),
                        new ModdedWallRegistry.ColorBand(TOP_TRIM, 1.0f)
                    },
                    PriceTrim
                );

                InjectWall(wallMaterials, sourceWall, neutralDefault,
                    "Repainted! High Stripe", "Repainted_HighStripe",
                    ModdedWallRegistry.WallType.HighStripe,
                    new ModdedWallRegistry.ColorBand[]
                    {
                        new ModdedWallRegistry.ColorBand(0.50f, 0.70f)
                    },
                    PriceHighStripe
                );

                RepaintedPlugin.Logger.LogInfo($"Total walls now: {wallMaterials.Count}");
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError($"Failed to inject walls: {ex}");
            }
        }

        /// <summary>
        /// Creates a cloned material, generates a tinted texture if needed,
        /// adds it to the wall materials list, and registers it in ModdedWallRegistry.
        /// </summary>
        private static void InjectWall(
            List<Material> wallMaterials, Material sourceWall, Color neutralDefault,
            string displayName, string materialName,
            ModdedWallRegistry.WallType wallType,
            ModdedWallRegistry.ColorBand[] bands,
            float price)
        {
            Material mat = Object.Instantiate(sourceWall);
            mat.name = materialName;

            if (wallType == ModdedWallRegistry.WallType.FullColor)
            {
                mat.SetColor("_BaseColor", neutralDefault);
            }
            else
            {
                Texture2D tintedBaseMap = TextureGenerator.GenerateBandTintedTexture(
                    ModdedWallRegistry.OriginalBaseMap, neutralDefault, bands
                );
                if (tintedBaseMap != null)
                {
                    mat.SetTexture("_BaseMap", tintedBaseMap);
                    mat.SetColor("_BaseColor", Color.white);
                }
                else
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"Could not generate tinted texture for {displayName}, falling back to full tint"
                    );
                    mat.SetColor("_BaseColor", neutralDefault);
                }
            }

            wallMaterials.Add(mat);
            int index = wallMaterials.Count - 1;

            ModdedWallRegistry.InjectedWalls.Add(
                new ModdedWallRegistry.ModdedWall(
                    index, displayName, mat, wallType, bands, price
                )
            );

            RepaintedPlugin.Logger.LogDebug(
                $"Injected '{displayName}' ({wallType}) at index {index}"
            );
        }
    }
}
