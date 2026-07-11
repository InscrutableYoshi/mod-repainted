using System;
using Repainted.Data;
using DFTGames.Localization;
using HarmonyLib;

namespace Repainted.Patches
{
    /// <summary>
    /// Supplies display names for our shop walls through the game's own
    /// localization lookup. DecorationUI titles come from
    /// Locale.GetWord("WALL_{index}") — vanilla has no entries for our
    /// injected indices, so this postfix answers for them. Also keeps the
    /// titles correct when the player switches language mid-session
    /// (DecorationUI re-queries GetWord on language change).
    ///
    /// (Pattern borrowed from the DecorationsPlus analysis — cleaner than
    /// overwriting titleText after Initialize.)
    /// </summary>
    [HarmonyPatch(typeof(Locale), nameof(Locale.GetWord), new Type[] { typeof(string) })]
    public static class LocalizationPatch
    {
        static void Postfix(string key, ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !key.StartsWith("WALL_"))
                    return;

                var walls = ModdedWallRegistry.InjectedWalls;
                for (int i = 0; i < walls.Count; i++)
                {
                    if (key == "WALL_" + walls[i].Index)
                    {
                        __result = walls[i].DisplayName;
                        return;
                    }
                }
            }
            catch
            {
                // Never disturb the game's localization path.
            }
        }
    }
}
