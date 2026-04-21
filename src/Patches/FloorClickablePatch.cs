using System;
using System.Reflection;
using Repainted.Data;
using Repainted.Util;
using HarmonyLib;
using UnityEngine;

namespace Repainted.Patches
{
    /// <summary>
    /// Per-tile color support for modded walls.
    ///
    /// Save compatibility strategy ("full ownership"):
    /// TileColorStore is the sole authority on whether a tile has a modded wall.
    /// The game's save data (DataSerializer) always stores a safe vanilla index
    /// (0) for modded tiles, so index shifts from game updates are harmless.
    ///
    /// SetMaterialInstant postfix: checks TileColorStore by tileId FIRST.
    /// If we own the tile, resolves the correct current index for that WallType,
    /// overrides the material, and applies saved per-tile color.
    ///
    /// OnMouseButtonDown postfix: captures which tile is being painted.
    ///
    /// OnMouseButtonUp postfix: on modded wall paint commit, saves color to
    /// TileColorStore and overwrites the game's save with vanilla index 0.
    /// </summary>
    [HarmonyPatch(typeof(FloorClickable))]
    public static class FloorClickablePatch
    {
        // Cached reflection for private fields
        private static FieldInfo meshRendererField;
        private static FieldInfo currentDecorationIndexField;
        private static FieldInfo currentProgressField;
        private static FieldInfo idField;
        private static FieldInfo decorationTypeField;

        private static FloorClickable currentlyPaintingTile;
        private static int paintingWithIndex = -1;

        private static void EnsureReflection()
        {
            if (meshRendererField != null) return;

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            meshRendererField = typeof(FloorClickable).GetField("meshRenderer", flags);
            currentDecorationIndexField = typeof(FloorClickable).GetField("currentDecorationIndex", flags);
            currentProgressField = typeof(FloorClickable).GetField("currentProgress", flags);
            idField = typeof(FloorClickable).GetField("id", flags);
            decorationTypeField = typeof(FloorClickable).GetField("decorationType", flags);

            if (meshRendererField == null || currentDecorationIndexField == null ||
                currentProgressField == null || idField == null || decorationTypeField == null)
            {
                RepaintedPlugin.Logger.LogError(
                    "FloorClickablePatch: Failed to resolve one or more reflection fields! " +
                    $"meshRenderer={meshRendererField != null}, " +
                    $"currentDecorationIndex={currentDecorationIndexField != null}, " +
                    $"currentProgress={currentProgressField != null}, " +
                    $"id={idField != null}, " +
                    $"decorationType={decorationTypeField != null}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("SetMaterialInstant")]
        static void SetMaterialInstantPostfix(FloorClickable __instance)
        {
            try
            {
                EnsureReflection();

                var decType = (DecorationUI.DecorationType)decorationTypeField.GetValue(__instance);
                if (decType != DecorationUI.DecorationType.WALL) return;

                int tileId = (int)idField.GetValue(__instance);
                var renderer = (MeshRenderer)meshRendererField.GetValue(__instance);
                if (renderer == null) return;

                if (!TileColorStore.TryGetTileColor(tileId, out var entry))
                {
                    // Wall is vanilla (or just lost its modded entry) — release
                    // any tint-cache binding it may have held so the entry's
                    // refcount can drop and free the texture.
                    TextureGenerator.ReleaseWall(tileId);
                    return;
                }

                var wallType = entry.ToWallType();
                if (!ModdedWallRegistry.TryGetIndexForWallType(wallType, out var moddedWall))
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"Tile {tileId} has saved WallType {wallType} but no matching " +
                        "wall is registered. Removing stale entry.");
                    TileColorStore.RemoveTileColor(tileId);
                    TextureGenerator.ReleaseWall(tileId);
                    return;
                }

                int correctIndex = moddedWall.Index;
                int currentIndex = (int)currentDecorationIndexField.GetValue(__instance);

                if (currentIndex != correctIndex)
                {
                    currentDecorationIndexField.SetValue(__instance, correctIndex);
                    var dm = SingletonBehaviour<DecorationManager>.Instance;
                    renderer.sharedMaterial = dm.GetMaterial(decType, correctIndex);
                }

                var block = new MaterialPropertyBlock();
                Color savedColor = entry.ToColor();

                if (moddedWall.IsFullColor)
                {
                    // FullColor doesn't use a tint texture — release any prior
                    // tint binding (in case this tile was recently a striped wall).
                    TextureGenerator.ReleaseWall(tileId);
                    block.SetColor("_BaseColor", savedColor);
                    renderer.SetPropertyBlock(block);
                }
                else
                {
                    bool isUpper = FloorResolver.IsUpperFloor(__instance.transform);
                    Texture2D tinted = TextureGenerator.GetAndBindTintedTexture(
                        tileId,
                        ModdedWallRegistry.OriginalBaseMap, savedColor,
                        moddedWall.Type, moddedWall.Bands, isUpper
                    );
                    if (tinted != null)
                    {
                        block.SetTexture("_BaseMap", tinted);
                        block.SetColor("_BaseColor", Color.white);
                        renderer.SetPropertyBlock(block);
                    }
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"SetMaterialInstantPostfix crashed: {ex}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("OnMouseButtonDown")]
        static void OnMouseButtonDownPostfix(FloorClickable __instance)
        {
            try
            {
                EnsureReflection();

                var decType = (DecorationUI.DecorationType)decorationTypeField.GetValue(__instance);
                if (decType != DecorationUI.DecorationType.WALL) return;

                var dm = SingletonBehaviour<DecorationManager>.Instance;
                if (dm == null) return;

                int targetIndex = dm.lastUsedDecorationIndex;

                if (ModdedWallRegistry.IsModdedWall(targetIndex))
                {
                    currentlyPaintingTile = __instance;
                    paintingWithIndex = targetIndex;
                }
                else
                {
                    currentlyPaintingTile = null;
                    paintingWithIndex = -1;
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"OnMouseButtonDownPostfix crashed: {ex}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("OnMouseButtonUp")]
        static void OnMouseButtonUpPostfix(FloorClickable __instance)
        {
            try
            {
                EnsureReflection();

                var decType = (DecorationUI.DecorationType)decorationTypeField.GetValue(__instance);
                if (decType != DecorationUI.DecorationType.WALL) return;

                float progress = (float)currentProgressField.GetValue(__instance);
                if (progress <= 0.9f) return;

                int tileId = (int)idField.GetValue(__instance);
                int newDecorationIndex = (int)currentDecorationIndexField.GetValue(__instance);

                if (ModdedWallRegistry.TryGetModdedWall(newDecorationIndex, out var moddedWall))
                {
                    Color color = ModdedWallRegistry.CurrentColor;
                    TileColorStore.CommitModdedTile(tileId, color, moddedWall.Type);
                }
                else
                {
                    TileColorStore.CommitVanillaTile(tileId);
                }

                currentlyPaintingTile = null;
                paintingWithIndex = -1;
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"OnMouseButtonUpPostfix crashed: {ex}");
            }
        }
    }
}
