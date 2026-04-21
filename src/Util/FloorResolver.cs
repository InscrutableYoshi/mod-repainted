using UnityEngine;

namespace Repainted.Util
{
    /// <summary>
    /// Determines which floor a wall tile lives on.
    ///
    /// Diagnostics (see FloorDiagnosticPatch) showed two clearly separated
    /// populations of WALL-type FloorClickables in Zolmart:
    ///   • Ground floor: mesh bottom Y ≈ 0.5, height 7.4 (incl. doorway-top
    ///     stubs at Y 5–6 with short heights 2.2–2.8 — still ground).
    ///   • Upper floor:  mesh bottom Y ≈ 8.25, height 7.4.
    ///
    /// There is no wall with Y between 1 and 8, so a threshold at Y=7 is
    /// bulletproof. We use transform.position.y rather than MeshRenderer
    /// bounds because the transform is always available on the instance
    /// FloorClickable passes us, and the pivot is close enough to the
    /// bottom to make the threshold decision correct for every tile.
    /// </summary>
    public static class FloorResolver
    {
        /// <summary>
        /// World-Y threshold above which a wall tile is considered upper floor.
        /// Chosen to land safely in the ~7-unit gap between the two populations.
        /// </summary>
        private const float UPPER_FLOOR_Y_THRESHOLD = 7.0f;

        /// <summary>Returns true if the given wall tile is on the upper floor.</summary>
        public static bool IsUpperFloor(Transform tileTransform)
        {
            if (tileTransform == null) return false;
            return tileTransform.position.y > UPPER_FLOOR_Y_THRESHOLD;
        }
    }
}
