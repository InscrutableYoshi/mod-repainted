using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Repainted.Data
{
    /// <summary>
    /// Persists per-tile color choices and player preferences across sessions.
    ///
    /// All data is stored per save slot at
    /// BepInEx/config/Repainted_Slot_{profileIndex}.json — tile color entries
    /// plus player-facing preferences (active color, picker position, color
    /// history, favorites). Each slot gets its own independent preferences so
    /// they don't cross-contaminate between saves.
    ///
    /// Each file is stamped with a `modVersion` string so future versions can
    /// detect and migrate older data formats if the schema ever changes.
    ///
    /// The active save slot is read from DataSerializer._currentProfileIndex
    /// via reflection. The full slot file is reloaded whenever the slot changes.
    /// </summary>
    public static class TileColorStore
    {
        private static string configDir;

        private static readonly string SlotFilePrefix = "Repainted_Slot_";
        private static readonly string SlotFileExtension = ".json";

        private static Dictionary<int, TileColorEntry> tileColors =
            new Dictionary<int, TileColorEntry>();

        private static bool isTilesDirty = false;
        private static bool isPrefsDirty = false;

        // Track which profile slot is currently loaded (-1 = none)
        private static int loadedProfileIndex = -1;

        // ─── Diagnostic properties (read by DataSerializerPatch) ─────
        public static bool IsTilesDirty => isTilesDirty;
        public static bool IsPrefsDirty => isPrefsDirty;
        public static int TileCount => tileColors.Count;
        public static int LoadedSlot => loadedProfileIndex;

        // ─── Profile index detection ─────────────────────────────

        private static FieldInfo profileIndexField;
        private static bool profileFieldSearched = false;

        /// <summary>
        /// Reads the active save profile index from DataSerializer via reflection.
        /// Returns 0 as a safe default if the field can't be found.
        /// </summary>
        public static int GetActiveProfileIndex()
        {
            if (!profileFieldSearched)
            {
                profileFieldSearched = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = asm.GetType("ToolBox.Serialization.DataSerializer");
                        if (type != null)
                        {
                            profileIndexField = type.GetField("_currentProfileIndex",
                                BindingFlags.NonPublic | BindingFlags.Static);
                            if (profileIndexField != null)
                            {
                                RepaintedPlugin.Logger.LogDebug(
                                    "Found DataSerializer._currentProfileIndex field");
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"Could not locate DataSerializer profile field: {ex.Message}");
                }
            }

            if (profileIndexField != null)
            {
                try
                {
                    return (int)profileIndexField.GetValue(null);
                }
                catch (Exception ex)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"Failed to read active profile index, defaulting to 0: {ex.Message}");
                }
            }
            return 0;
        }

        // ─── History & Favorites ──────────────────────────────────

        private static readonly int MaxHistory = 8;
        private static readonly int MaxFavorites = 8;
        private static readonly Color DefaultFavGray = new Color(0.5f, 0.5f, 0.5f, 1f);

        private static List<Color> historyColors = new List<Color>();
        private static List<Color> favoriteColors = new List<Color>();

        /// <summary>Read-only view of the current history list (most recent first).</summary>
        public static IReadOnlyList<Color> HistoryColors => historyColors;

        /// <summary>Read-only view of the current favorites list.</summary>
        public static IReadOnlyList<Color> FavoriteColors => favoriteColors;

        /// <summary>
        /// Push a color to the front of history. Duplicates are removed first
        /// so the same color doesn't appear twice.
        /// </summary>
        public static void PushHistory(Color color)
        {
            for (int i = historyColors.Count - 1; i >= 0; i--)
            {
                if (ColorsMatch(historyColors[i], color))
                    historyColors.RemoveAt(i);
            }

            historyColors.Insert(0, color);

            while (historyColors.Count > MaxHistory)
                historyColors.RemoveAt(historyColors.Count - 1);

            isPrefsDirty = true;
        }

        /// <summary>
        /// Toggle a color as a favorite. If already favorited, removes it and
        /// pads the list with the default gray. If not, inserts at front and
        /// bumps off the last entry.
        /// Returns true if the color is now favorited.
        /// </summary>
        public static bool ToggleFavorite(Color color)
        {
            for (int i = 0; i < favoriteColors.Count; i++)
            {
                if (ColorsMatch(favoriteColors[i], color))
                {
                    favoriteColors.RemoveAt(i);
                    favoriteColors.Add(DefaultFavGray);
                    isPrefsDirty = true;
                    return false;
                }
            }

            favoriteColors.Insert(0, color);
            if (favoriteColors.Count > MaxFavorites)
                favoriteColors.RemoveAt(favoriteColors.Count - 1);

            isPrefsDirty = true;
            return true;
        }

        /// <summary>Check if a color is in the favorites list.</summary>
        public static bool IsFavorited(Color color)
        {
            foreach (var fav in favoriteColors)
            {
                if (ColorsMatch(fav, color))
                    return true;
            }
            return false;
        }

        /// <summary>Compare two colors with tolerance for floating point drift.</summary>
        private static bool ColorsMatch(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.002f &&
                   Mathf.Abs(a.g - b.g) < 0.002f &&
                   Mathf.Abs(a.b - b.b) < 0.002f;
        }

        // ─── Tile color entry ─────────────────────────────────────

        [Serializable]
        public struct TileColorEntry
        {
            public float r;
            public float g;
            public float b;
            public float a;
            public int wallType; // 0=FullColor, 2=BottomStripe, 3=BottomTopStripe, 4=Trim, 5=HighStripe

            public TileColorEntry(Color color, ModdedWallRegistry.WallType type)
            {
                r = color.r;
                g = color.g;
                b = color.b;
                a = color.a;
                wallType = (int)type;
            }

            public Color ToColor()
            {
                return new Color(r, g, b, a);
            }

            public ModdedWallRegistry.WallType ToWallType()
            {
                return (ModdedWallRegistry.WallType)wallType;
            }
        }

        // ─── Initialization ───────────────────────────────────────

        /// <summary>
        /// Initialize the store. Call once during plugin Awake().
        /// All real data lives per-slot and loads later via LoadSlotData()
        /// once the active save profile is known. This call just resolves the
        /// config directory and seeds default in-memory state for the menu UI.
        /// </summary>
        public static void Initialize()
        {
            configDir = BepInEx.Paths.ConfigPath;
            ResetInMemoryStateToDefaults();
        }

        /// <summary>
        /// Load tile data + preferences for the given save slot. Call this
        /// after DataSerializer.ChangeProfile() has set the active profile
        /// (e.g., from DecorationManager.Awake postfix).
        /// </summary>
        public static void LoadSlotData(int profileIndex)
        {
            // Flush any pending changes from the previous slot before switching
            if (loadedProfileIndex != -1 && (isTilesDirty || isPrefsDirty))
            {
                SaveSlotData();
            }

            loadedProfileIndex = profileIndex;
            ResetInMemoryStateToDefaults();
            isTilesDirty = false;
            isPrefsDirty = false;

            string slotPath = GetSlotPath(profileIndex);
            if (!File.Exists(slotPath))
            {
                RepaintedPlugin.Logger.LogInfo(
                    $"No data for slot {profileIndex}, starting fresh");
                return;
            }

            try
            {
                string json = File.ReadAllText(slotPath);
                ParseSlotJson(json);

                while (favoriteColors.Count < MaxFavorites)
                    favoriteColors.Add(DefaultFavGray);

                // Push the just-loaded active color into runtime state.
                // Without this, ModdedWallRegistry.CurrentColor keeps its
                // hardcoded default (the palette tool's main-menu-time init
                // runs before any slot is known), so Left-Click would paint
                // gray regardless of what was saved. Also resync the picker
                // overlay's internal HSV fields — its Start() already fired
                // (at the main menu, before this slot existed), so without a
                // re-sync opening the picker would show the stale color.
                ModdedWallRegistry.ApplyColor(savedActiveColor);
                UI.ColorPickerOverlay.Instance?.ReapplySavedColor();

                RepaintedPlugin.Logger.LogInfo(
                    $"Loaded slot {profileIndex}: {tileColors.Count} tiles, " +
                    $"history={historyColors.Count}, favorites={favoriteColors.Count}");
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"Failed to load slot {profileIndex}: {ex}");
            }
        }

        /// <summary>
        /// Reset preferences/history/favorites to their factory defaults. Used
        /// before loading a slot so unset fields don't bleed between profiles.
        /// </summary>
        private static void ResetInMemoryStateToDefaults()
        {
            tileColors.Clear();
            historyColors.Clear();
            favoriteColors.Clear();
            for (int i = 0; i < MaxFavorites; i++)
                favoriteColors.Add(DefaultFavGray);
            savedActiveColor = new Color(0.8f, 0.8f, 0.8f, 1.0f);
            savedPickerPosition = new Vector2(-1f, -1f);
        }

        /// <summary>
        /// Manual JSON parser for slot data. Matches tile objects with regex
        /// rather than relying on JsonUtility (which silently fails on nested types).
        /// Also extracts preferences fields (active color, picker position,
        /// history, favorites) from the same file.
        /// </summary>
        private static void ParseSlotJson(string json)
        {
            // Scope the tile regex to the "tiles" array so preferences color
            // entries (which use a 3-field {r,g,b} shape) can't sneak in.
            var tilesArrayMatch = Regex.Match(json,
                @"""tiles""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            string tilesContent = tilesArrayMatch.Success
                ? tilesArrayMatch.Groups[1].Value
                : string.Empty;

            var tilePattern = new Regex(
                @"\{\s*""id"":\s*(\d+)\s*,\s*" +
                @"""r"":\s*([0-9.Ee+-]+)\s*,\s*" +
                @"""g"":\s*([0-9.Ee+-]+)\s*,\s*" +
                @"""b"":\s*([0-9.Ee+-]+)\s*,\s*" +
                @"""a"":\s*([0-9.Ee+-]+)\s*,\s*" +
                @"""wallType"":\s*(\d+)\s*\}");

            foreach (Match m in tilePattern.Matches(tilesContent))
            {
                int id = int.Parse(m.Groups[1].Value);
                float r = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                float g = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                float b = float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                float a = float.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
                int wallType = int.Parse(m.Groups[6].Value);

                tileColors[id] = new TileColorEntry(
                    new Color(r, g, b, a),
                    (ModdedWallRegistry.WallType)wallType
                );
            }

            // Preferences fields (all optional — missing keys fall back to defaults)
            savedActiveColor = new Color(
                ParseFloat(json, "activeR", 0.8f),
                ParseFloat(json, "activeG", 0.8f),
                ParseFloat(json, "activeB", 0.8f),
                ParseFloat(json, "activeA", 1.0f));
            savedPickerPosition = new Vector2(
                ParseFloat(json, "pickerX", -1f),
                ParseFloat(json, "pickerY", -1f));

            var parsedHistory = ParseColorArray(json, "history");
            if (parsedHistory.Count > 0) historyColors = parsedHistory;

            var parsedFavorites = ParseColorArray(json, "favorites");
            if (parsedFavorites.Count > 0) favoriteColors = parsedFavorites;
        }

        /// <summary>
        /// Ensures tile data is loaded for the currently active profile.
        /// Safe to call repeatedly — only reloads if the profile changed.
        /// </summary>
        public static void EnsureCorrectSlot()
        {
            int activeProfile = GetActiveProfileIndex();
            if (activeProfile != loadedProfileIndex)
            {
                RepaintedPlugin.Logger.LogInfo(
                    $"Profile changed: {loadedProfileIndex} → {activeProfile}, reloading tile data");
                LoadSlotData(activeProfile);
            }
        }

        // ─── Active color persistence ──────────────────────────────

        private static Color savedActiveColor = new Color(0.8f, 0.8f, 0.8f, 1.0f);
        private static Vector2 savedPickerPosition = new Vector2(-1f, -1f);

        /// <summary>The last-loaded active color (set during Load).</summary>
        public static Color SavedActiveColor => savedActiveColor;

        /// <summary>The last-loaded picker position (-1,-1 = use default).</summary>
        public static Vector2 SavedPickerPosition => savedPickerPosition;

        /// <summary>Persist the active color for next session.</summary>
        public static void SetActiveColor(Color color)
        {
            savedActiveColor = color;
            isPrefsDirty = true;
        }

        /// <summary>Persist the picker window position for next session.</summary>
        public static void SetPickerPosition(Vector2 pos)
        {
            savedPickerPosition = pos;
            isPrefsDirty = true;
        }

        // ─── Save key for game's wall data ──────────────────────────

        /// <summary>
        /// Game's save key prefix for wall tiles
        /// (matches FloorClickable.LAST_USED_DECORATION_INDEX_WALL_KEY).
        /// </summary>
        private const string WALL_SAVE_KEY_PREFIX = "LFW_";

        /// <summary>
        /// Safe vanilla index written to game save for modded tiles.
        /// If the mod is removed, tiles fall back to Wall #0 (default).
        /// </summary>
        private const int SAFE_VANILLA_INDEX = 0;

        // ─── Shared save entry point ─────────────────────────────────

        /// <summary>
        /// Single entry point for persisting a modded wall paint.
        /// Both the vanilla paint roller path (FloorClickablePatch) and the
        /// palette tool (ColorPaletteTool) MUST call this — never save inline.
        ///
        /// Does three things atomically:
        ///   1. Saves the color + wall type to TileColorStore
        ///   2. Overwrites the game's in-memory save with vanilla index 0
        ///      so the save file never contains modded indices
        ///   3. Flushes pending changes to disk
        /// </summary>
        public static void CommitModdedTile(int tileId, Color color,
            ModdedWallRegistry.WallType wallType)
        {
            SetTileColor(tileId, color, wallType);
            GenericDataSerializer.SaveInt(WALL_SAVE_KEY_PREFIX + tileId, SAFE_VANILLA_INDEX);

            // NOTE: We do NOT flush to disk here. The dirty flag stays set and
            // DataSerializerPatch flushes alongside the game's own save
            // (end of day, manual save, exit to menu/desktop).

            RepaintedPlugin.Logger.LogDebug(
                $"CommitModdedTile: tile {tileId} type={wallType} " +
                $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2}) " +
                $"(game save overwritten to index {SAFE_VANILLA_INDEX})");
        }

        /// <summary>
        /// Remove a modded tile's saved color (e.g., repainted with vanilla).
        /// Does not flush to disk — that happens on game save via the save hook.
        /// </summary>
        public static void CommitVanillaTile(int tileId)
        {
            RemoveTileColor(tileId);
        }

        // ─── Per-tile color persistence ────────────────────────────

        /// <summary>
        /// Save a tile's color. Marks the store dirty for deferred file write.
        /// Prefer CommitModdedTile() which also handles DataSerializer override.
        /// </summary>
        public static void SetTileColor(int tileId, Color color,
            ModdedWallRegistry.WallType wallType)
        {
            tileColors[tileId] = new TileColorEntry(color, wallType);
            isTilesDirty = true;

            RepaintedPlugin.Logger.LogDebug(
                $"Tile {tileId} color saved: RGBA({color.r:F2}, {color.g:F2}, " +
                $"{color.b:F2}, {color.a:F2}) type={wallType}");
        }

        /// <summary>
        /// Get the saved color for a tile, if one exists.
        /// </summary>
        public static bool TryGetTileColor(int tileId, out TileColorEntry entry)
        {
            return tileColors.TryGetValue(tileId, out entry);
        }

        /// <summary>
        /// Remove a tile's saved color (e.g., if player paints it with a
        /// vanilla wall).
        /// </summary>
        public static void RemoveTileColor(int tileId)
        {
            if (tileColors.Remove(tileId))
            {
                isTilesDirty = true;
                RepaintedPlugin.Logger.LogDebug($"Tile {tileId} color data removed");
            }
        }

        /// <summary>
        /// Flush pending changes to disk. Tiles and preferences share a single
        /// per-slot file, so either dirty flag triggers one combined write.
        /// </summary>
        public static void FlushIfDirty()
        {
            if (!isTilesDirty && !isPrefsDirty) return;
            SaveSlotData();
            isTilesDirty = false;
            isPrefsDirty = false;
        }

        // ─── File paths ───────────────────────────────────────────

        private static string GetSlotPath(int profileIndex)
        {
            return Path.Combine(configDir,
                SlotFilePrefix + profileIndex + SlotFileExtension);
        }

        // ─── Save / Load: Slot data ───────────────────────────────

        // Manual JSON serialization — Unity's JsonUtility silently
        // produces "{}" for List<T> fields inside nested classes.
        // Our format is simple enough to write/parse by hand.

        private static void SaveSlotData()
        {
            if (loadedProfileIndex < 0) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");

                // Version stamp — not currently used for migration, but lets
                // future releases detect and convert older file formats.
                sb.Append("  \"modVersion\": \"");
                sb.Append(PluginInfo.PLUGIN_VERSION);
                sb.AppendLine("\",");

                // Preferences block (slot-scoped)
                sb.AppendLine($"  \"activeR\": {savedActiveColor.r.ToString("G", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"activeG\": {savedActiveColor.g.ToString("G", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"activeB\": {savedActiveColor.b.ToString("G", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"activeA\": {savedActiveColor.a.ToString("G", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"pickerX\": {savedPickerPosition.x.ToString("G", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"pickerY\": {savedPickerPosition.y.ToString("G", CultureInfo.InvariantCulture)},");

                sb.Append("  \"history\": [");
                for (int i = 0; i < historyColors.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var c = historyColors[i];
                    sb.Append($"{{ \"r\": {c.r.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"g\": {c.g.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"b\": {c.b.ToString("G", CultureInfo.InvariantCulture)} }}");
                }
                sb.AppendLine("],");

                sb.Append("  \"favorites\": [");
                for (int i = 0; i < favoriteColors.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var c = favoriteColors[i];
                    sb.Append($"{{ \"r\": {c.r.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"g\": {c.g.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"b\": {c.b.ToString("G", CultureInfo.InvariantCulture)} }}");
                }
                sb.AppendLine("],");

                // Tile block
                sb.AppendLine("  \"tiles\": [");
                int t = 0;
                foreach (var kvp in tileColors)
                {
                    if (t > 0) sb.AppendLine(",");
                    var e = kvp.Value;
                    sb.Append($"    {{ \"id\": {kvp.Key}, ");
                    sb.Append($"\"r\": {e.r.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"g\": {e.g.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"b\": {e.b.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"a\": {e.a.ToString("G", CultureInfo.InvariantCulture)}, ");
                    sb.Append($"\"wallType\": {e.wallType} }}");
                    t++;
                }
                sb.AppendLine();
                sb.AppendLine("  ]");
                sb.Append("}");

                string path = GetSlotPath(loadedProfileIndex);
                string json = sb.ToString();
                File.WriteAllText(path, json);

                RepaintedPlugin.Logger.LogInfo(
                    $"Saved slot {loadedProfileIndex}: {tileColors.Count} tiles " +
                    $"({json.Length} bytes)");
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"Failed to save slot {loadedProfileIndex}: {ex}");
            }
        }

        // ─── Parsing helpers ──────────────────────────────────────

        private static float ParseFloat(string json, string key, float fallback)
        {
            var m = Regex.Match(json, $@"""{key}""\s*:\s*([0-9.Ee+-]+)");
            if (m.Success && float.TryParse(m.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                return val;
            return fallback;
        }

        private static List<Color> ParseColorArray(string json, string key)
        {
            var result = new List<Color>();

            // Find the array content: "key": [ ... ]
            var arrayMatch = Regex.Match(json,
                $@"""{key}""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (!arrayMatch.Success) return result;

            string arrayContent = arrayMatch.Groups[1].Value;

            // Match each { "r": F, "g": F, "b": F } in the array
            var colorPattern = new Regex(
                @"\{\s*""r"":\s*([0-9.Ee+-]+)\s*,\s*" +
                @"""g"":\s*([0-9.Ee+-]+)\s*,\s*" +
                @"""b"":\s*([0-9.Ee+-]+)\s*\}");

            foreach (Match m in colorPattern.Matches(arrayContent))
            {
                float r = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                float g = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                float b = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                result.Add(new Color(r, g, b, 1f));
            }

            return result;
        }

    }
}
