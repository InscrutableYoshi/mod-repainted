using System;
using System.Reflection;
using Repainted.Data;
using HarmonyLib;

namespace Repainted.Patches
{
    /// <summary>
    /// Hooks DataSerializer.SaveFile() to flush our TileColorStore data
    /// at the exact same time the game saves to disk, and
    /// DataSerializer.DeleteData() so deleting a game save also removes
    /// our matching color data for that slot.
    ///
    /// Both hooks are POSTFIXES: the game's own save/delete completes
    /// fully before our code runs, and every handler is wrapped in
    /// try/catch, so nothing we do can disturb the game's save files.
    ///
    /// DataSerializer lives in ToolBox.Serialization (external DLL), so we
    /// can't use attribute-based [HarmonyPatch]. Instead, we resolve the
    /// type at runtime and apply manual postfixes.
    ///
    /// The game calls SaveFile() on: end of day, manual save (pause menu),
    /// exit to menu, and exit to desktop. It calls DeleteData(int) from the
    /// save-slot delete button, and DeleteData() (current profile) when
    /// starting a new game.
    /// </summary>
    public static class DataSerializerPatch
    {
        /// <summary>
        /// Call this from RepaintedPlugin.Awake() AFTER harmony.PatchAll(),
        /// passing the Harmony instance so we can apply the manual patch.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            try
            {
                // Find DataSerializer in the loaded assemblies
                Type dataSerializerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    dataSerializerType = asm.GetType("ToolBox.Serialization.DataSerializer");
                    if (dataSerializerType != null) break;
                }

                if (dataSerializerType == null)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        "DataSerializerPatch: Could not find DataSerializer type. " +
                        "Save-sync hook not installed — falling back to flushing on every paint.");
                    return;
                }

                // SaveFile has two overloads: SaveFile() and SaveFile(Action callback).
                // Both write to disk. Patch the no-arg version and the Action version.
                var postfix = new HarmonyMethod(
                    typeof(DataSerializerPatch).GetMethod(nameof(SaveFilePostfix),
                        BindingFlags.Static | BindingFlags.NonPublic));

                var deleteNoArgsPrefix = new HarmonyMethod(
                    typeof(DataSerializerPatch).GetMethod(nameof(DeleteDataNoArgsPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic));
                var deleteNoArgsPostfix = new HarmonyMethod(
                    typeof(DataSerializerPatch).GetMethod(nameof(DeleteDataNoArgsPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic));
                var deleteIndexPostfix = new HarmonyMethod(
                    typeof(DataSerializerPatch).GetMethod(nameof(DeleteDataIndexPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic));

                int patched = 0;
                foreach (var method in dataSerializerType.GetMethods(
                    BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "SaveFile")
                    {
                        harmony.Patch(method, postfix: postfix);
                        patched++;
                        RepaintedPlugin.Logger.LogInfo(
                            $"Patched DataSerializer.SaveFile overload " +
                            $"({method.GetParameters().Length} params)");
                    }
                    else if (method.Name == "DeleteData")
                    {
                        // DeleteData() deletes the current profile;
                        // DeleteData(int) deletes a specific slot. Each needs
                        // a postfix with a matching signature.
                        bool hasIndexParam = method.GetParameters().Length == 1;
                        if (hasIndexParam)
                        {
                            harmony.Patch(method, postfix: deleteIndexPostfix);
                        }
                        else
                        {
                            // The no-arg overload deletes the CURRENT profile,
                            // and the game may mutate _currentProfileIndex
                            // during the delete — so capture the index in a
                            // Prefix (before the game runs) and hand it to
                            // the Postfix via __state.
                            harmony.Patch(method,
                                prefix: deleteNoArgsPrefix,
                                postfix: deleteNoArgsPostfix);
                        }
                        RepaintedPlugin.Logger.LogInfo(
                            $"Patched DataSerializer.DeleteData overload " +
                            $"({method.GetParameters().Length} params)");
                    }
                }

                if (patched == 0)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        "DataSerializerPatch: Found DataSerializer but no SaveFile methods!");
                }
                else
                {
                    TileColorStore.SaveHookActive = true;
                    RepaintedPlugin.Logger.LogInfo(
                        $"DataSerializerPatch: Hooked {patched} SaveFile overload(s)");
                }
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"DataSerializerPatch: Failed to apply save hook: {ex}");
            }
        }

        /// <summary>
        /// Postfix on DataSerializer.SaveFile — flushes our tile color data
        /// and preferences to disk alongside the game's save.
        /// </summary>
        static void SaveFilePostfix()
        {
            try
            {
                RepaintedPlugin.Logger.LogDebug(
                    $"Save hook fired — tiles dirty={TileColorStore.IsTilesDirty}, " +
                    $"prefs dirty={TileColorStore.IsPrefsDirty}, " +
                    $"tile count={TileColorStore.TileCount}, " +
                    $"loaded slot={TileColorStore.LoadedSlot}");
                TileColorStore.FlushIfDirty();
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"Save hook: failed to flush tile colors: {ex}");
            }
        }

        /// <summary>
        /// Postfix on DataSerializer.DeleteData() — the no-arg overload
        /// deletes the current profile's save, so remove our color data
        /// for the active profile alongside it.
        /// </summary>
        static void DeleteDataNoArgsPrefix(ref int __state)
        {
            // Runs BEFORE the game's delete — the profile index is still valid.
            __state = TileColorStore.GetActiveProfileIndex();
        }

        static void DeleteDataNoArgsPostfix(int __state)
        {
            HandleGameSaveDeleted(__state);
        }

        /// <summary>
        /// Postfix on DataSerializer.DeleteData(int profileIndex) — fired
        /// from the save-slot delete button.
        /// </summary>
        static void DeleteDataIndexPostfix(int profileIndex)
        {
            HandleGameSaveDeleted(profileIndex);
        }

        static void HandleGameSaveDeleted(int profileIndex)
        {
            try
            {
                RepaintedPlugin.Logger.LogInfo(
                    $"Game save deleted for slot {profileIndex} — removing matching color data");
                TileColorStore.DeleteSlotData(profileIndex);
            }
            catch (Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"Delete hook: failed to remove color data for slot {profileIndex}: {ex}");
            }
        }
    }
}
