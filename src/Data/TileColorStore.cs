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

        /// <summary>
        /// Set by DataSerializerPatch once the SaveFile hook is installed.
        /// When false (hook failed to install), commits flush to disk
        /// immediately so painted colors can't be lost to a crash.
        /// </summary>
        public static bool SaveHookActive = false;

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
        /// Fired whenever the favorites list changes: a toggle in the picker,
        /// a slot load, or a slot wipe. Consumers (the palette model's outer
        /// dabs) re-read FavoriteColors on this.
        /// </summary>
        public static event Action FavoritesChanged;

        private static void RaiseFavoritesChanged()
        {
            try { FavoritesChanged?.Invoke(); }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"FavoritesChanged handler threw: {ex.Message}");
            }
        }

        /// <summary>
        /// True if this favorites entry is the unset-slot placeholder gray
        /// rather than a color the player actually favorited. (A player who
        /// favorites exactly mid-gray will match too — accepted limitation
        /// of the padded-list storage format.)
        /// </summary>
        public static bool IsPlaceholderFavorite(Color c)
        {
            return ColorsMatch(c, DefaultFavGray);
        }

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
                    RaiseFavoritesChanged();
                    return false;
                }
            }

            favoriteColors.Insert(0, color);
            if (favoriteColors.Count > MaxFavorites)
                favoriteColors.RemoveAt(favoriteColors.Count - 1);

            isPrefsDirty = true;
            RaiseFavoritesChanged();
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

            /// <summary>
            /// The vanilla decoration index truly saved in the game's tile
            /// record while this overlay is active. Defaults to 0, which is
            /// exactly correct for every 1.x-era entry (1.x sanitization
            /// guaranteed LFW_ = 0) — so old files need no migration.
            /// Used by the SetMaterialInstant mismatch check to distinguish
            /// "player vanilla-repainted this tile" from "stale/dangling
            /// index that needs repair".
            /// </summary>
            public int underlyingIndex;

            public TileColorEntry(Color color, ModdedWallRegistry.WallType type,
                int underlying = 0)
            {
                r = color.r;
                g = color.g;
                b = color.b;
                a = color.a;
                wallType = (int)type;
                underlyingIndex = underlying;
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
                RaiseFavoritesChanged();
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
                // Preserve the unreadable file for inspection/recovery
                // instead of silently overwriting it on the next save.
                try
                {
                    File.Copy(slotPath, slotPath + ".bad", overwrite: true);
                    RepaintedPlugin.Logger.LogWarning(
                        $"Backed up unreadable slot file to {slotPath}.bad");
                }
                catch (Exception copyEx)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        $"Could not back up unreadable slot file: {copyEx.Message}");
                }
            }

            RaiseFavoritesChanged();
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

            // Parse each {...} object field-by-field so field ORDER and
            // whitespace don't matter (hand-edited or reformatted files
            // used to silently drop every tile).
            var objectPattern = new Regex(@"\{[^{}]*\}", RegexOptions.Singleline);
            int objectCount = 0, parsedCount = 0;
            foreach (Match m in objectPattern.Matches(tilesContent))
            {
                objectCount++;
                string obj = m.Value;
                if (!TryField(obj, "id", out float idF)) continue;
                if (!TryField(obj, "r", out float r) ||
                    !TryField(obj, "g", out float g) ||
                    !TryField(obj, "b", out float b)) continue;
                TryField(obj, "a", out float a, 1f);
                TryField(obj, "wallType", out float wt, 0f);
                // Absent in pre-1.5 files; 0 is guaranteed correct there
                // (1.x sanitized every modded tile's game record to 0).
                TryField(obj, "underlyingIndex", out float ui, 0f);

                tileColors[(int)idF] = new TileColorEntry(
                    new Color(r, g, b, a),
                    (ModdedWallRegistry.WallType)(int)wt,
                    (int)ui
                );
                parsedCount++;
            }
            if (parsedCount < objectCount)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Slot file: parsed {parsedCount}/{objectCount} tile " +
                    "entries — some entries were malformed and skipped.");
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
        /// Remove all of our stored data for a slot. Called (via
        /// DataSerializerPatch) after the game deletes a save, so a future
        /// new game in that slot starts with no leftover colors/preferences.
        ///
        /// Only touches our own Repainted_Slot_N.json — never game files.
        /// </summary>
        public static void DeleteSlotData(int profileIndex)
        {
            try
            {
                string slotPath = GetSlotPath(profileIndex);
                if (File.Exists(slotPath))
                {
                    File.Delete(slotPath);
                    RepaintedPlugin.Logger.LogInfo(
                        $"Deleted color data file for slot {profileIndex}");
                }

                // If the deleted slot is the one currently in memory, wipe the
                // in-memory state too. Without this, EnsureCorrectSlot() sees
                // an unchanged index on a later "new game" in this slot and
                // would keep (and re-save) the dead save's data.
                if (loadedProfileIndex == profileIndex)
                {
                    ResetInMemoryStateToDefaults();
                    isTilesDirty = false;
                    isPrefsDirty = false;

                    // Resync runtime color state, same as a fresh slot load.
                    ModdedWallRegistry.ApplyColor(savedActiveColor);
                    UI.ColorPickerOverlay.Instance?.ReapplySavedColor();
                    RaiseFavoritesChanged();
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"Failed to delete color data for slot {profileIndex}: {ex}");
            }
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
        /// Vanilla index written to the game save when a tile is painted
        /// with one of our 3 shop brushes: the default half-stripe blue
        /// wall. If the mod is removed, brush-painted tiles fall back to it.
        /// </summary>
        private const int BRUSH_WRITEBACK_INDEX = 0;

        /// <summary>
        /// Overwrite the game's per-tile wall record. Used by the brush
        /// commit (write-back to index 0) and by the load-time repair of
        /// dangling indices. This is the ONLY place we ever touch the
        /// game's own save data, and it always writes a truthful,
        /// currently-valid vanilla index.
        /// </summary>
        public static void WriteWallIndex(int tileId, int vanillaIndex)
        {
            GenericDataSerializer.SaveInt(WALL_SAVE_KEY_PREFIX + tileId, vanillaIndex);
        }

        // ─── Shared save entry points ────────────────────────────────

        /// <summary>
        /// Persist a palette overlay paint. The game's tile record is NOT
        /// touched — it already truthfully holds the vanilla wall the
        /// overlay sits on (recorded here as underlyingIndex).
        /// </summary>
        public static void CommitOverlayTile(int tileId, Color color,
            ModdedWallRegistry.WallType wallType, int underlyingIndex)
        {
            SetTileColor(tileId, color, wallType, underlyingIndex);

            // Normally we do NOT flush to disk here — the dirty flag stays set
            // and DataSerializerPatch flushes alongside the game's own save
            // (end of day, manual save, exit to menu/desktop). But if the save
            // hook failed to install, that flush never comes: fall back to
            // flushing per paint so data can't be lost to a crash.
            if (!SaveHookActive)
                FlushIfDirty();

            RepaintedPlugin.Logger.LogDebug(
                $"CommitOverlayTile: tile {tileId} type={wallType} " +
                $"underlying={underlyingIndex} " +
                $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})");
        }

        /// <summary>
        /// Persist a roller paint made with one of our 3 shop brushes.
        /// The game just wrote OUR decoration index into its tile record;
        /// write it back to the default wall (index 0) so the game save
        /// never contains a modded index, and store the overlay entry.
        /// </summary>
        public static void CommitBrushTile(int tileId, Color color,
            ModdedWallRegistry.WallType wallType)
        {
            SetTileColor(tileId, color, wallType, BRUSH_WRITEBACK_INDEX);
            WriteWallIndex(tileId, BRUSH_WRITEBACK_INDEX);

            if (!SaveHookActive)
                FlushIfDirty();

            RepaintedPlugin.Logger.LogDebug(
                $"CommitBrushTile: tile {tileId} type={wallType} " +
                $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2}) " +
                $"(game record written back to index {BRUSH_WRITEBACK_INDEX})");
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
        /// Save a tile's overlay. Marks the store dirty for deferred file
        /// write. Prefer CommitOverlayTile()/CommitBrushTile().
        /// </summary>
        public static void SetTileColor(int tileId, Color color,
            ModdedWallRegistry.WallType wallType, int underlyingIndex)
        {
            tileColors[tileId] = new TileColorEntry(color, wallType, underlyingIndex);
            isTilesDirty = true;

            RepaintedPlugin.Logger.LogDebug(
                $"Tile {tileId} color saved: RGBA({color.r:F2}, {color.g:F2}, " +
                $"{color.b:F2}, {color.a:F2}) type={wallType} " +
                $"underlying={underlyingIndex}");
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
                    sb.Append($"\"wallType\": {e.wallType}, ");
                    sb.Append($"\"underlyingIndex\": {e.underlyingIndex} }}");
                    t++;
                }
                sb.AppendLine();
                sb.AppendLine("  ]");
                sb.Append("}");

                string path = GetSlotPath(loadedProfileIndex);
                string json = sb.ToString();
                WriteAtomic(path, json);

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

        /// <summary>
        /// Write via a temp file + rename so a crash mid-write can never
        /// leave a truncated slot file behind.
        /// </summary>
        private static void WriteAtomic(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception)
            {
                // File.Replace can fail across filesystems/platform quirks —
                // fall back to delete+move (a shorter unsafe window than a
                // direct overwrite).
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        /// <summary>
        /// Extract a single numeric field from a JSON object fragment,
        /// tolerant of field order and whitespace.
        /// </summary>
        private static bool TryField(string obj, string key, out float value,
            float fallback = 0f)
        {
            var m = Regex.Match(obj, $@"""{key}""\s*:\s*([0-9.Ee+-]+)");
            if (m.Success && float.TryParse(m.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
            value = fallback;
            return false;
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
