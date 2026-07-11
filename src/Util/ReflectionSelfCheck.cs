using System;
using System.Collections.Generic;
using System.Reflection;

namespace Repainted.Util
{
    /// <summary>
    /// One-shot startup audit of every game member the mod reflects on or
    /// calls. A game update that renames or removes one of these should
    /// produce a single, prominent, actionable log line at load — not a
    /// stream of per-call exceptions hours later (see the 0.5.5
    /// ButtonsWindow.buttons incident, CODE-REVIEW-2026-07.md C1).
    ///
    /// This validates existence only; it does not replace the per-patch
    /// cached FieldInfo lookups.
    /// </summary>
    public static class ReflectionSelfCheck
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        /// <summary>True if the last Run() found every expected member.</summary>
        public static bool AllOk { get; private set; } = true;

        public static void Run()
        {
            var missing = new List<string>();

            CheckFields(typeof(FloorClickable), missing,
                "meshRenderer", "currentDecorationIndex", "currentProgress",
                "id", "decorationType", "materialPropertyBlock");
            CheckMethods(typeof(FloorClickable), missing,
                "SetMaterialInstant", "OnMouseButtonDown", "OnMouseButtonUp",
                "OnMouseHoverStarted", "OnMouseHoverEnded");

            CheckFields(typeof(RayShooter), missing, "hoveredInteractable");
            CheckMethods(typeof(RayShooter), missing,
                "FixedUpdate", "GetClickedInteractable");

            CheckFields(typeof(HotKeyManager), missing,
                "hotkeyClickables", "hotkeySlot", "selectedHotkeyOutlines",
                "deselectedHotkeyOutlines", "hotkeyEffects");

            CheckFields(typeof(HotkeyClickable), missing,
                "pickUpPosition", "putDownPosition");

            CheckFields(typeof(DecorationManager), missing, "wallMaterials");
            CheckMethods(typeof(DecorationManager), missing,
                "GetMaterial", "GetColor", "GetTexture");

            CheckFields(typeof(DecorationWindow), missing,
                "decorationUIsWall", "decorationUIsFloor", "decorationTabButton");
            CheckFields(typeof(DecorationUI), missing,
                "index", "decorationType", "price", "titleText");

            // Repainted! tab injection (TabbedPanel.Start prefix) and
            // RepaintedWindow's reimplemented Initialize.
            CheckFields(typeof(TabbedPanel), missing,
                "buttons", "buttonBgs", "windows", "demoWindows");
            CheckMethods(typeof(TabbedPanel), missing, "Start");
            CheckFields(typeof(TabWindow), missing, "isInitialized");
            CheckMethods(typeof(DecorationWindow), missing, "RefreshNavigation");

            // Wall display names via the localization hook.
            CheckMethods(typeof(DFTGames.Localization.Locale), missing, "GetWord");

            CheckMethods(typeof(ButtonsWindow), missing, "RepaintWithKeyCodes");

            // DataSerializer lives in an external assembly.
            Type ds = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                ds = asm.GetType("ToolBox.Serialization.DataSerializer");
                if (ds != null) break;
            }
            if (ds == null)
            {
                missing.Add("ToolBox.Serialization.DataSerializer (type)");
            }
            else
            {
                if (ds.GetField("_currentProfileIndex", Any) == null)
                    missing.Add("DataSerializer._currentProfileIndex");
                if (ds.GetMethod("SaveFile", Any, null, Type.EmptyTypes, null) == null
                    && !HasAnyMethod(ds, "SaveFile"))
                    missing.Add("DataSerializer.SaveFile");
                if (!HasAnyMethod(ds, "DeleteData"))
                    missing.Add("DataSerializer.DeleteData");
            }

            AllOk = missing.Count == 0;
            if (AllOk)
            {
                RepaintedPlugin.Logger.LogInfo(
                    "Reflection self-check passed: all expected game members present.");
            }
            else
            {
                RepaintedPlugin.Logger.LogError(
                    "REFLECTION SELF-CHECK FAILED — the game update likely changed " +
                    "internals this mod depends on. Missing: " +
                    string.Join(", ", missing) +
                    ". Related features may misbehave; please report this with your " +
                    "game version at github.com/InscrutableYoshi/mod-repainted/issues");
            }
        }

        private static void CheckFields(Type t, List<string> missing, params string[] names)
        {
            foreach (var n in names)
                if (t.GetField(n, Any) == null)
                    missing.Add($"{t.Name}.{n}");
        }

        private static void CheckMethods(Type t, List<string> missing, params string[] names)
        {
            foreach (var n in names)
                if (!HasAnyMethod(t, n))
                    missing.Add($"{t.Name}.{n}()");
        }

        private static bool HasAnyMethod(Type t, string name)
        {
            foreach (var m in t.GetMethods(Any))
                if (m.Name == name) return true;
            return false;
        }
    }
}
