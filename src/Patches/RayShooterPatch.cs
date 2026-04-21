using System.Reflection;
using HarmonyLib;
using Repainted.Tools;
using UnityEngine;

namespace Repainted.Patches
{
    /// <summary>
    /// Suppresses RayShooter's hover and click processing while the
    /// ColorPaletteTool is active. Without this, RayShooter falls through
    /// its hardcoded tool if/else chain to GetAllLayers(), causing hover
    /// events on unrelated Clickables (boxes, shelves, etc.) which show
    /// stale "E - COLLECT" interaction UI.
    ///
    /// Our tool handles its own raycasting in Update(), so we don't need
    /// RayShooter at all while it's active.
    /// </summary>
    [HarmonyPatch(typeof(RayShooter))]
    public static class RayShooterPatch
    {
        private static FieldInfo hoveredInteractableField;

        private static void EnsureReflection()
        {
            if (hoveredInteractableField != null) return;
            hoveredInteractableField = typeof(RayShooter).GetField(
                "hoveredInteractable",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
        }

        /// <summary>
        /// Skip hover processing while palette tool is active.
        /// Also cleans up any existing hover when tool first activates.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("FixedUpdate")]
        static bool FixedUpdatePrefix(RayShooter __instance)
        {
            if (ColorPaletteTool.Instance == null || !ColorPaletteTool.Instance.IsPicked)
                return true; // not our tool — run original

            // If RayShooter still has something hovered, end it cleanly
            EnsureReflection();
            var hovered = (Interactable)hoveredInteractableField.GetValue(__instance);
            if (hovered != null)
            {
                hovered.OnMouseHoverEnded();
                hoveredInteractableField.SetValue(__instance, null);

                if (SingletonBehaviour<HotKeyManager>.Instance.SelectedHotkeyIndex != -1)
                {
                    SingletonBehaviour<HotKeyManager>.Instance.RepaintButtonsForEndHover();
                }
            }

            return false; // skip original
        }

        /// <summary>
        /// Prevent RayShooter from dispatching click events to random
        /// Interactables while our tool is active.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("GetClickedInteractable")]
        static bool GetClickedInteractablePrefix(ref Interactable __result)
        {
            if (ColorPaletteTool.Instance != null && ColorPaletteTool.Instance.IsPicked)
            {
                __result = null;
                return false; // skip original
            }
            return true;
        }
    }
}
