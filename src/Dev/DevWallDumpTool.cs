#if REPAINTED_DEV
// =========================================================================
//  DEV-ONLY FILE — not compiled unless built with `./build.sh --dev`.
//  Exists to help craft stress-test JSON save files with every wall painted.
//  Do NOT reference from any file that ships in the release build.
// =========================================================================

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Repainted.Data;
using UnityEngine;

namespace Repainted.Dev
{
    /// <summary>
    /// Debug helper: enumerates every paintable wall tile in the current scene
    /// and prints their IDs (plus current decoration index and world position)
    /// to the BepInEx log. Also can emit a ready-made JSON file with entries
    /// for all walls, to be hand-edited and swapped into
    /// BepInEx/config/Repainted_Slot_{N}.json for stress-testing a fully-
    /// painted store.
    ///
    /// Hotkeys (active only while the scene has FloorClickable instances):
    ///   F9              → log a dump of every wall tile
    ///   Ctrl+F9         → write Repainted_Slot_{N}_DUMP.json next to the real slot file
    ///
    /// Attached to a DontDestroyOnLoad GameObject from RepaintedPlugin.Awake
    /// under the same REPAINTED_DEV guard.
    /// </summary>
    public class DevWallDumpTool : MonoBehaviour
    {
        private static FieldInfo idField;
        private static FieldInfo decorationTypeField;
        private static FieldInfo currentDecorationIndexField;

        void Awake()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            idField = typeof(FloorClickable).GetField("id", flags);
            decorationTypeField = typeof(FloorClickable).GetField("decorationType", flags);
            currentDecorationIndexField = typeof(FloorClickable).GetField("currentDecorationIndex", flags);

            RepaintedPlugin.Logger.LogInfo(
                "[DEV] DevWallDumpTool active. F9 = log dump, Ctrl+F9 = write JSON.");
        }

        void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F9)) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var walls = CollectWallTiles();

            if (ctrl) WriteDumpJson(walls);
            else LogDump(walls);
        }

        private struct WallInfo
        {
            public int Id;
            public int DecorationIndex;
            public Vector3 Position;
        }

        private List<WallInfo> CollectWallTiles()
        {
            var all = UnityEngine.Object.FindObjectsOfType<FloorClickable>();
            var walls = new List<WallInfo>(all.Length);

            foreach (var fc in all)
            {
                var dt = (DecorationUI.DecorationType)decorationTypeField.GetValue(fc);
                if (dt != DecorationUI.DecorationType.WALL) continue;

                walls.Add(new WallInfo
                {
                    Id = (int)idField.GetValue(fc),
                    DecorationIndex = (int)currentDecorationIndexField.GetValue(fc),
                    Position = fc.transform.position,
                });
            }

            walls.Sort((a, b) => a.Id.CompareTo(b.Id));
            return walls;
        }

        private void LogDump(List<WallInfo> walls)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[DEV] Wall tile dump — {walls.Count} wall tiles in scene:");
            sb.AppendLine("      id   decIdx    x       y       z");
            foreach (var w in walls)
            {
                sb.AppendLine(
                    $"    {w.Id,5}   {w.DecorationIndex,4}   " +
                    $"{w.Position.x,6:F2}  {w.Position.y,6:F2}  {w.Position.z,6:F2}");
            }
            RepaintedPlugin.Logger.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Writes a Repainted_Slot_{N}_DUMP.json file with entries for every
        /// wall tile, pre-filled with a simple rainbow sweep keyed by tile id.
        /// User edits colors/wallTypes as desired, then renames over the real
        /// slot file (backed up first!) to stress-test the mod.
        /// </summary>
        private void WriteDumpJson(List<WallInfo> walls)
        {
            int slot = TileColorStore.GetActiveProfileIndex();
            string path = Path.Combine(
                BepInEx.Paths.ConfigPath,
                $"Repainted_Slot_{slot}_DUMP.json");

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"modVersion\": \"dev-dump\",");
            sb.AppendLine("  \"activeR\": 0.8, \"activeG\": 0.8, \"activeB\": 0.8, \"activeA\": 1.0,");
            sb.AppendLine("  \"pickerX\": -1, \"pickerY\": -1,");
            sb.AppendLine("  \"history\": [],");
            sb.AppendLine("  \"favorites\": [],");
            sb.AppendLine("  \"tiles\": [");

            // WallType values:   0=FullColor  2=BottomStripe  3=BottomTopStripe
            //                    4=Trim       5=HighStripe
            // We cycle tiles through all five types and a rainbow hue so a dump
            // run lands every variant of modded wall somewhere in the store.
            int[] wallTypes = { 0, 2, 3, 4, 5 };

            for (int i = 0; i < walls.Count; i++)
            {
                var w = walls[i];
                float hue = (i / (float)Mathf.Max(1, walls.Count)) % 1f;
                Color c = Color.HSVToRGB(hue, 0.65f, 0.95f);
                int wt = wallTypes[i % wallTypes.Length];

                string comma = (i < walls.Count - 1) ? "," : "";
                sb.Append($"    {{ \"id\": {w.Id}, ");
                sb.Append($"\"r\": {c.r.ToString("G", CultureInfo.InvariantCulture)}, ");
                sb.Append($"\"g\": {c.g.ToString("G", CultureInfo.InvariantCulture)}, ");
                sb.Append($"\"b\": {c.b.ToString("G", CultureInfo.InvariantCulture)}, ");
                sb.Append($"\"a\": 1.0, ");
                sb.AppendLine($"\"wallType\": {wt} }}{comma}");
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(path, sb.ToString());
            RepaintedPlugin.Logger.LogInfo(
                $"[DEV] Wrote dump JSON: {path} ({walls.Count} tiles)");
        }
    }
}
#endif
