using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Repainted.UI
{
    /// <summary>
    /// The Repainted! tab's window component. Subclasses DecorationWindow
    /// so the TabbedPanel machinery treats it natively, but OVERRIDES
    /// Initialize() with a reimplementation that never invokes the base
    /// method — Harmony patches fire on method invocation, so third-party
    /// patches on DecorationWindow.Initialize (DecorationsPlus injects its
    /// entire catalog into the FIRST DecorationWindow that initializes,
    /// and re-flows the layout of EVERY one) never see this window. Their
    /// cards keep landing in the vanilla Decorations tab regardless of
    /// which tab the player opens first, and our stripped-down layout is
    /// never re-flowed by foreign code.
    ///
    /// The reimplementation mirrors the vanilla body: mark initialized,
    /// initialize each card, refresh navigation, re-select for gamepad on
    /// tab-selection changes.
    /// </summary>
    public class RepaintedWindow : DecorationWindow
    {
        private static FieldInfo isInitializedField;
        private static FieldInfo floorsField;
        private static FieldInfo wallsField;
        private static FieldInfo tabButtonField;
        private static MethodInfo refreshNavigationMethod;

        private static void EnsureReflection()
        {
            if (isInitializedField != null) return;
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            isInitializedField = typeof(TabWindow).GetField("isInitialized", flags);
            floorsField = typeof(DecorationWindow).GetField("decorationUIsFloor", flags);
            wallsField = typeof(DecorationWindow).GetField("decorationUIsWall", flags);
            tabButtonField = typeof(DecorationWindow).GetField("decorationTabButton", flags);
            refreshNavigationMethod = typeof(DecorationWindow).GetMethod(
                "RefreshNavigation", flags, null, System.Type.EmptyTypes, null);
        }

        public override void Initialize()
        {
            try
            {
                EnsureReflection();

                // TabWindow.Initialize's only job — do it without calling
                // up the (patched) chain.
                isInitializedField?.SetValue(this, true);

                var floors = (List<DecorationUI>)floorsField?.GetValue(this);
                var walls = (List<DecorationUI>)wallsField?.GetValue(this);
                if (floors != null)
                    foreach (var ui in floors)
                        if (ui != null) ui.Initialize();
                if (walls != null)
                    foreach (var ui in walls)
                        if (ui != null) ui.Initialize();

                RefreshNav();
                EventManager.AddListener(
                    UIEvents.TAB_SELECTED_OBJECT_CHANGED, OnTabSelectionChanged);
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"RepaintedWindow.Initialize crashed: {ex}");
            }
        }

        private void OnTabSelectionChanged()
        {
            try
            {
                if (!IsOpen()) return;
                RefreshNav();
                if (SingletonBehaviour<LastInputDeviceTracker>.Instance != null &&
                    SingletonBehaviour<LastInputDeviceTracker>.Instance.UseGamepad)
                {
                    var tabButton = (Button)tabButtonField?.GetValue(this);
                    if (tabButton != null)
                        SingletonBehaviour<InputManager>.Instance
                            .SelectElement(tabButton.gameObject);
                }
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"RepaintedWindow.OnTabSelectionChanged crashed: {ex}");
            }
        }

        private void RefreshNav()
        {
            refreshNavigationMethod?.Invoke(this, null);
        }

        /// <summary>
        /// Vanilla walks the FLOORS list (ours is empty) — walk our walls
        /// instead so gamepad navigation can enter the window.
        /// </summary>
        public override Selectable GetFirstSelectable()
        {
            EnsureReflection();
            var walls = (List<DecorationUI>)wallsField?.GetValue(this);
            if (walls == null) return null;
            for (int i = 0; i < walls.Count; i++)
            {
                if (walls[i] != null && walls[i].GetSelectable() != null)
                    return walls[i].GetSelectable();
            }
            return null;
        }
    }
}
