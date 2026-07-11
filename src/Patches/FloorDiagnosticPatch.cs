#if REPAINTED_DEV
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Repainted.Patches
{
    /// <summary>
    /// One-shot diagnostic: when FloorClickable.Start runs, log each tile's
    /// id, decoration type, world-space Y, mesh bounds height, and parent
    /// chain. Used to pick a Y threshold for the ground-vs-upper floor
    /// binary check and to correlate save-file tile ids with physical
    /// locations.
    ///
    /// Logs are emitted once per scene load per unique tile id so switching
    /// between saves doesn't spam the log.
    /// </summary>
    [HarmonyPatch(typeof(FloorClickable), "Start")]
    public static class FloorDiagnosticPatch
    {
        /// <summary>
        /// Set to true to re-enable the per-tile log spam for troubleshooting
        /// (e.g. re-calibrating the upper-floor threshold after a game update).
        /// Left compiled-in but inert by default to keep logs clean.
        /// </summary>
        private const bool ENABLED = false;

        private static readonly HashSet<int> loggedIds = new HashSet<int>();

        /// <summary>
        /// Call at scene unload / profile change to clear the per-session
        /// dedup set so tiles get re-logged after a save swap.
        /// </summary>
        public static void ResetDedup() => loggedIds.Clear();

        [HarmonyPostfix]
        private static void Postfix(FloorClickable __instance)
        {
            if (!ENABLED) return;
#pragma warning disable CS0162 // unreachable while ENABLED is const false
            if (__instance == null) return;

            int id = __instance.ID;
            if (!loggedIds.Add(id)) return;

            var t = __instance.transform;
            Vector3 pos = t.position;

            // Mesh height, if we can read it — useful since the bottom-Y
            // of the wall may differ from transform.position depending on
            // how the prefab is pivoted.
            float meshMinY = float.NaN, meshMaxY = float.NaN, meshHeight = float.NaN;
            var mr = __instance.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Bounds b = mr.bounds;
                meshMinY = b.min.y;
                meshMaxY = b.max.y;
                meshHeight = b.size.y;
            }

            // Parent chain (up to 4 levels) — often encodes the growth
            // section / floor grouping we can leverage as a fallback.
            string parentChain = BuildParentChain(t, 4);

            RepaintedPlugin.Logger.LogInfo(
                $"[FloorDiag] id={id} type={__instance.DecorationType} " +
                $"posY={pos.y:F3} meshY=[{meshMinY:F3}..{meshMaxY:F3}] h={meshHeight:F3} " +
                $"parents={parentChain}");
#pragma warning restore CS0162
        }

        private static string BuildParentChain(Transform t, int depth)
        {
            var names = new List<string>();
            Transform cur = t.parent;
            for (int i = 0; i < depth && cur != null; i++)
            {
                names.Add(cur.name);
                cur = cur.parent;
            }
            return string.Join(" < ", names);
        }
    }
}
#endif
