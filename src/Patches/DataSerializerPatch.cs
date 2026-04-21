using System;
using System.Reflection;
using Repainted.Data;
using HarmonyLib;

namespace Repainted.Patches
{
    /// <summary>
    /// Hooks DataSerializer.SaveFile() to flush our TileColorStore data
    /// at the exact same time the game saves to disk.
    ///
    /// DataSerializer lives in ToolBox.Serialization (external DLL), so we
    /// can't use attribute-based [HarmonyPatch]. Instead, we resolve the
    /// type at runtime and apply a manual postfix.
    ///
    /// The game calls SaveFile() on: end of day, manual save (pause menu),
    /// exit to menu, and exit to desktop.
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
                        "Save-sync hook not installed — tile data will flush on paint instead.");
                    return;
                }

                // SaveFile has two overloads: SaveFile() and SaveFile(Action callback).
                // Both write to disk. Patch the no-arg version and the Action version.
                var postfix = new HarmonyMethod(
                    typeof(DataSerializerPatch).GetMethod(nameof(SaveFilePostfix),
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
                }

                if (patched == 0)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        "DataSerializerPatch: Found DataSerializer but no SaveFile methods!");
                }
                else
                {
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
    }
}
