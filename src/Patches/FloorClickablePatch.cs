using System;
using System.Reflection;
using Repainted.Data;
using Repainted.Rendering;
using HarmonyLib;
using UnityEngine;

namespace Repainted.Patches
{
    /// <summary>
    /// Overlay layer integration with the game's wall tiles (1.5 model).
    ///
    /// The game's tile record (currentDecorationIndex + LFW_ save key) is
    /// always a truthful vanilla index. Our overlay — tileId → (style,
    /// color, underlyingIndex) in TileColorStore — renders ON TOP via the
    /// tile's cached MaterialPropertyBlock (see OverlayRenderer).
    ///
    /// SetMaterialInstant postfix (priority Low so it runs AFTER other
    /// mods' postfixes, e.g. DecorationsPlus, and our overlay wins the
    /// block): applies the overlay if an entry exists, with a
    /// validity-guarded mismatch check:
    ///   • incoming == entry.underlyingIndex → normal re-apply.
    ///   • incoming is a DIFFERENT valid vanilla index → the tile was
    ///     genuinely repainted outside our commit path (Mega Paint Tool
    ///     area paint, etc.) → clear the overlay.
    ///   • incoming is out of range or one of our brush indices → stale/
    ///     dangling record (1.x drag-off hole, third-party writes) →
    ///     repair the record to underlyingIndex and KEEP the overlay.
    /// Tiles without an entry get the same dangling repair to index 0.
    ///
    /// OnMouseButtonDown prefix: if painting with one of our brushes,
    /// re-bakes the brush material in the active palette color first so
    /// the paint animation shows the color that will actually land.
    ///
    /// OnMouseButtonUp postfix: on commit (progress > 0.9), a brush paint
    /// stores an overlay entry and writes the game record back to index 0;
    /// a vanilla paint clears any overlay entry.
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

        /// <summary>
        /// True if the index is a real vanilla decoration index this
        /// session: in range for the wall materials list and not one of
        /// our injected brushes. (Another content mod's injected walls DO
        /// count as valid — they're in range and truthfully paintable.)
        /// </summary>
        private static bool IsValidVanillaIndex(int index)
        {
            if (index < 0) return false;
            if (ModdedWallRegistry.IsModdedWall(index)) return false;
            int count = DecorationManagerAwakePatch.GetWallMaterialCount();
            // If the count is unavailable, only index 0 is safe to trust.
            if (count <= 0) return index == 0;
            return index < count;
        }

        /// <summary>
        /// Point a tile's runtime record + saved key at a vanilla index.
        /// Used to repair dangling records (see class doc).
        /// </summary>
        private static void RepairIndex(FloorClickable tile, MeshRenderer renderer,
            int tileId, int vanillaIndex, string reason)
        {
            currentDecorationIndexField.SetValue(tile, vanillaIndex);
            var dm = SingletonBehaviour<DecorationManager>.Instance;
            if (dm != null && renderer != null)
            {
                renderer.sharedMaterial = dm.GetMaterial(
                    DecorationUI.DecorationType.WALL, vanillaIndex);
            }
            TileColorStore.WriteWallIndex(tileId, vanillaIndex);
            RepaintedPlugin.Logger.LogWarning(
                $"Repaired tile {tileId}: {reason} → rewrote record to " +
                $"vanilla index {vanillaIndex}.");
        }

        // Run after other mods' SetMaterialInstant postfixes (lower
        // priority = later for postfixes) so our overlay is the last
        // writer of the property block on contested tiles.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
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

                int incoming = (int)currentDecorationIndexField.GetValue(__instance);

                if (!TileColorStore.TryGetTileColor(tileId, out var entry))
                {
                    // No overlay. Release any stale tint binding, and repair
                    // a dangling record (our index / out-of-range) to 0.
                    TextureGenerator.ReleaseWall(tileId);
                    if (!IsValidVanillaIndex(incoming))
                    {
                        RepairIndex(__instance, renderer, tileId, 0,
                            $"dangling index {incoming} with no overlay entry");
                    }
                    return;
                }

                if (incoming == entry.underlyingIndex)
                {
                    // Normal case: the truthful underlying wall — overlay on top.
                    OverlayRenderer.Apply(__instance, renderer, tileId, entry);
                }
                else if (IsValidVanillaIndex(incoming))
                {
                    // A different REAL vanilla index means the tile was
                    // repainted outside our commit path (e.g. Mega Paint
                    // Tool area paint). Honor it: the overlay is gone.
                    RepaintedPlugin.Logger.LogDebug(
                        $"Tile {tileId} repainted externally to index " +
                        $"{incoming} (underlying was {entry.underlyingIndex}) " +
                        "— clearing overlay.");
                    TileColorStore.RemoveTileColor(tileId);
                    TextureGenerator.ReleaseWall(tileId);
                }
                else
                {
                    // Stale/dangling index (1.x drag-off hole, removed
                    // content, our own brush index). NOT a repaint — keep
                    // the player's overlay, repair the record.
                    RepairIndex(__instance, renderer, tileId, entry.underlyingIndex,
                        $"stale index {incoming} under an overlay");
                    OverlayRenderer.Apply(__instance, renderer, tileId, entry);
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"SetMaterialInstantPostfix crashed: {ex}");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("OnMouseButtonDown")]
        static void OnMouseButtonDownPrefix(FloorClickable __instance)
        {
            try
            {
                EnsureReflection();

                var decType = (DecorationUI.DecorationType)decorationTypeField.GetValue(__instance);
                if (decType != DecorationUI.DecorationType.WALL) return;

                var dm = SingletonBehaviour<DecorationManager>.Instance;
                if (dm == null) return;

                // Painting with one of our brushes? Re-bake its material in
                // the active palette color BEFORE the game samples
                // GetTexture/GetColor for the paint animation.
                if (ModdedWallRegistry.TryGetModdedWall(
                        dm.lastUsedDecorationIndex, out var brush))
                {
                    DecorationManagerAwakePatch.RefreshBrushBake(brush.Type);
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"OnMouseButtonDownPrefix crashed: {ex}");
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

                if (ModdedWallRegistry.TryGetModdedWall(newDecorationIndex, out var brush))
                {
                    // Brush commit: store the overlay in the active color and
                    // write the game record back to the default wall — the
                    // save never holds our index.
                    Color color = ModdedWallRegistry.CurrentColor;
                    TileColorStore.CommitBrushTile(tileId, color, brush.Type);

                    var renderer = (MeshRenderer)meshRendererField.GetValue(__instance);
                    currentDecorationIndexField.SetValue(__instance, 0);
                    var dm = SingletonBehaviour<DecorationManager>.Instance;
                    if (dm != null && renderer != null)
                    {
                        renderer.sharedMaterial = dm.GetMaterial(
                            DecorationUI.DecorationType.WALL, 0);
                    }

                    // Show the final overlaid result immediately (hover-end
                    // would also get there via SetMaterialInstant(0)).
                    if (TileColorStore.TryGetTileColor(tileId, out var entry))
                        OverlayRenderer.Apply(__instance, renderer, tileId, entry);
                }
                else
                {
                    // Vanilla paint clears any overlay (empty entry = do nothing).
                    TileColorStore.CommitVanillaTile(tileId);
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"OnMouseButtonUpPostfix crashed: {ex}");
            }
        }
    }
}
