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
        /// Chosen to land safely in the gap between the two populations.
        /// </summary>
        private const float UPPER_FLOOR_Y_THRESHOLD = 7.0f;

        // Calibration gap: no known wall population has a bounds-min Y here.
        // A wall landing inside means the calibration needs a re-check.
        private const float GAP_LOW = 6.5f;
        private const float GAP_HIGH = 8.0f;
        private static bool warnedGap;

        /// <summary>
        /// Returns true if the given wall tile is on the upper floor.
        /// Prefer the renderer overload — the Y=7 threshold was calibrated
        /// against MESH BOUNDS, not transform pivots.
        /// </summary>
        public static bool IsUpperFloor(Transform tileTransform)
        {
            if (tileTransform == null) return false;
            return Classify(tileTransform.position.y);
        }

        /// <summary>
        /// Bounds-based check — measures the same quantity the threshold was
        /// calibrated on. Falls back to the transform pivot if no renderer.
        /// </summary>
        public static bool IsUpperFloor(Transform tileTransform, Renderer renderer)
        {
            if (renderer != null)
                return Classify(renderer.bounds.min.y);
            return IsUpperFloor(tileTransform);
        }

        private static bool Classify(float y)
        {
            if (!warnedGap && y > GAP_LOW && y < GAP_HIGH)
            {
                warnedGap = true;
                RepaintedPlugin.Logger.LogWarning(
                    $"FloorResolver: wall Y={y:F2} landed inside the calibration " +
                    "gap (6.5–8.0) — the floor-height heuristic may need " +
                    "recalibration for this game version/store. Stripe placement " +
                    "on upper floors may be off.");
            }
            return y > UPPER_FLOOR_Y_THRESHOLD;
        }
    }
}
