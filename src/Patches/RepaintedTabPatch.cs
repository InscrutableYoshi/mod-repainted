using System.Collections.Generic;
using System.Reflection;
using Repainted.Data;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Repainted.Patches
{
    /// <summary>
    /// Adds a dedicated "Repainted!" tab (rainbow label, first in the tab
    /// column) to the management screen, containing our shop walls in the
    /// same card style as the vanilla decorations tab. The vanilla
    /// decorations tab is NO LONGER touched at all — no injected cards, no
    /// scroll re-flow (that layout math was the mod's most brittle UI code).
    ///
    /// Mechanism: TabbedPanel wires its tabs in Start() from parallel
    /// serialized lists (buttons / buttonBgs / windows). This PREFIX runs
    /// before that wiring, finds the panel that hosts the DecorationWindow
    /// (the ManagementWindow), and appends:
    ///   • a cloned tab button (label "Repainted!", rainbow TMP vertex
    ///     gradient, placed first via SetAsFirstSibling — list order and
    ///     visual order are independent), and
    ///   • a cloned FloorDecorationsWindow stripped down to one walls
    ///     section holding our cards.
    /// The vanilla Start() then wires click listeners, pool positioning,
    /// and tab selection for our entries exactly like the native ones.
    /// Scene hierarchy names (Content/FloorsTitle/WallsTitle/…) were read
    /// from the level1 assets — see RESEARCH-2.0-decal-walls.md notes.
    ///
    /// Card titles come from a Locale.GetWord postfix (LocalizationPatch),
    /// which also keeps them correct across language switches.
    /// </summary>
    [HarmonyPatch(typeof(TabbedPanel), "Start")]
    public static class RepaintedTabPatch
    {
        private const string TAB_NAME = "RepaintedTab";
        private const string WINDOW_NAME = "RepaintedWindow";

        private static FieldInfo buttonsField;
        private static FieldInfo buttonBgsField;
        private static FieldInfo windowsField;
        private static FieldInfo demoWindowsField;
        private static FieldInfo uiIndexField;
        private static FieldInfo uiTypeField;
        private static FieldInfo uiPriceField;
        private static FieldInfo decTabButtonField;
        private static FieldInfo decFloorsField;
        private static FieldInfo decWallsField;

        private static void EnsureReflection()
        {
            if (buttonsField != null) return;
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            buttonsField = typeof(TabbedPanel).GetField("buttons", flags);
            buttonBgsField = typeof(TabbedPanel).GetField("buttonBgs", flags);
            windowsField = typeof(TabbedPanel).GetField("windows", flags);
            demoWindowsField = typeof(TabbedPanel).GetField("demoWindows", flags);
            uiIndexField = typeof(DecorationUI).GetField("index", flags);
            uiTypeField = typeof(DecorationUI).GetField("decorationType", flags);
            uiPriceField = typeof(DecorationUI).GetField("price", flags);
            decTabButtonField = typeof(DecorationWindow).GetField("decorationTabButton", flags);
            decFloorsField = typeof(DecorationWindow).GetField("decorationUIsFloor", flags);
            decWallsField = typeof(DecorationWindow).GetField("decorationUIsWall", flags);
        }

        [HarmonyPrefix]
        static void StartPrefix(TabbedPanel __instance)
        {
            try
            {
                EnsureReflection();
                if (buttonsField == null || windowsField == null ||
                    decWallsField == null) return;

                var windows = (List<TabWindow>)windowsField.GetValue(__instance);
                if (windows == null) return;
                int decIdx = windows.FindIndex(
                    w => w is DecorationWindow && !(w is UI.RepaintedWindow));
                if (decIdx < 0) return; // not the management panel

                if (ModdedWallRegistry.InjectedWalls.Count == 0) return;

                var demoWindows = (List<TabWindow>)demoWindowsField?.GetValue(__instance);
                bool demoHasDec = demoWindows != null &&
                    demoWindows.Exists(w => w is DecorationWindow);
                if (GameManager.isDemo && !demoHasDec) return;

                var buttons = (List<Button>)buttonsField.GetValue(__instance);
                var buttonBgs = (List<Image>)buttonBgsField.GetValue(__instance);
                if (buttons == null || buttonBgs == null ||
                    buttons.Count != buttonBgs.Count ||
                    decIdx >= buttons.Count) return;

                var decWindow = (DecorationWindow)windows[decIdx];

                // Idempotence (Start should only run once per scene, but
                // cheap to guard).
                if (decWindow.transform.parent.Find(WINDOW_NAME) != null) return;

                Button ourButton = CreateTabButton(
                    buttons[decIdx], buttonBgs[decIdx], out Image ourBg);
                if (ourButton == null) return;

                DecorationWindow ourWindow = CreateWindow(decWindow, ourButton);
                if (ourWindow == null)
                {
                    Object.Destroy(ourButton.gameObject);
                    return;
                }

                buttons.Add(ourButton);
                buttonBgs.Add(ourBg);
                windows.Add(ourWindow);
                if (demoHasDec)
                    demoWindows.Add(ourWindow);

                RepaintedPlugin.Logger.LogInfo(
                    $"Repainted! tab injected (windows: {windows.Count}, " +
                    $"cards: {ModdedWallRegistry.InjectedWalls.Count})");
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"RepaintedTabPatch.StartPrefix crashed — no Repainted " +
                    $"tab this session: {ex}");
            }
        }

        // ─── Tab button ────────────────────────────────────────────────

        private static Button CreateTabButton(Button srcButton, Image srcBg,
            out Image ourBg)
        {
            ourBg = null;

            var go = Object.Instantiate(srcButton.gameObject,
                srcButton.transform.parent);
            go.name = TAB_NAME;
            // Visually last in the tab column; the parallel lists are
            // index-based so sibling order is free to differ.
            go.transform.SetAsLastSibling();

            var btn = go.GetComponent<Button>();
            if (btn == null)
            {
                Object.Destroy(go);
                return null;
            }
            btn.onClick.RemoveAllListeners();

            // Strip any localization bindings cloned from the vanilla tab —
            // they'd overwrite our label on load/language change.
            foreach (var loc in go.GetComponentsInChildren<DFTGames.Localization.LocalizeBase>(true))
                Object.Destroy(loc);

            var tmp = go.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                tmp.text = "Repainted!";
                tmp.enableVertexGradient = true;
                tmp.colorGradient = new VertexGradient(
                    new Color(1f, 0.45f, 0.45f),   // top-left: red
                    new Color(1f, 0.88f, 0.40f),   // top-right: yellow
                    new Color(0.45f, 0.65f, 1f),   // bottom-left: blue
                    new Color(0.50f, 0.90f, 0.55f) // bottom-right: green
                );
            }

            // Resolve the clone's counterpart of the tab's background Image
            // (buttonBgs is a parallel list; the bg may be the button's own
            // Image or live on a child).
            Transform bgT = ResolveCounterpart(
                srcBg != null ? srcBg.transform : null,
                srcButton.transform, go.transform);
            ourBg = bgT != null ? bgT.GetComponent<Image>() : null;
            if (ourBg == null) ourBg = go.GetComponent<Image>();
            if (ourBg == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    "Repainted tab: no background Image resolved on the " +
                    "cloned tab button — using any child Image.");
                ourBg = go.GetComponentInChildren<Image>(true);
            }
            if (ourBg == null)
            {
                Object.Destroy(go);
                return null;
            }
            // Start deselected like the others.
            ourBg.color = TabbedPanel.deselectedBGColor;
            return btn;
        }

        /// <summary>Finds the transform in a clone hierarchy matching a
        /// transform in the source hierarchy (by child-index path).</summary>
        private static Transform ResolveCounterpart(Transform target,
            Transform srcRoot, Transform cloneRoot)
        {
            if (target == null) return null;
            if (target == srcRoot) return cloneRoot;

            var indices = new List<int>();
            Transform cur = target;
            while (cur != null && cur != srcRoot)
            {
                indices.Add(cur.GetSiblingIndex());
                cur = cur.parent;
            }
            if (cur != srcRoot) return null; // not inside the source subtree

            Transform result = cloneRoot;
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                if (indices[i] >= result.childCount) return null;
                result = result.GetChild(indices[i]);
            }
            return result;
        }

        // ─── Window ────────────────────────────────────────────────────

        private static DecorationWindow CreateWindow(DecorationWindow src,
            Button ourTabButton)
        {
            var go = Object.Instantiate(src.gameObject, src.transform.parent);
            go.name = WINDOW_NAME;

            // Swap the cloned DecorationWindow for our RepaintedWindow
            // subclass (copying every inherited serialized field). Its
            // Initialize() override never invokes the patched base method,
            // so third-party DecorationWindow.Initialize patches
            // (DecorationsPlus card injection + layout re-flow) can't
            // target this window — see RepaintedWindow.
            var cloned = go.GetComponent<DecorationWindow>();
            if (cloned == null) { Object.Destroy(go); return null; }
            var win = go.AddComponent<UI.RepaintedWindow>();
            CopyComponentFields(cloned, win);
            Object.DestroyImmediate(cloned);

            var floors = (List<DecorationUI>)decFloorsField.GetValue(win);
            var walls = (List<DecorationUI>)decWallsField.GetValue(win);
            if (walls == null || walls.Count == 0)
            {
                RepaintedPlugin.Logger.LogWarning(
                    "Repainted tab: cloned window has no wall cards to " +
                    "use as template.");
                Object.Destroy(go);
                return null;
            }

            DecorationUI template = walls[walls.Count - 1];
            Transform section = template.transform.parent;   // WallsSection
            Transform content = section.parent;               // Content

            // Build our cards while the template is still alive.
            var ourCards = new List<DecorationUI>();
            foreach (var brush in ModdedWallRegistry.InjectedWalls)
            {
                var cardGO = Object.Instantiate(template.gameObject, section);
                cardGO.name = "RepaintedItem_" + brush.Type;
                var ui = cardGO.GetComponent<DecorationUI>();
                if (ui == null) { Object.Destroy(cardGO); continue; }
                uiIndexField.SetValue(ui, brush.Index);
                uiTypeField.SetValue(ui, DecorationUI.DecorationType.WALL);
                uiPriceField.SetValue(ui, brush.Price);
                SwapPreviewSprite(cardGO, brush.Type);
                cardGO.SetActive(true);
                ourCards.Add(ui);
            }
            if (ourCards.Count == 0)
            {
                Object.Destroy(go);
                return null;
            }

            // Capture the top-of-content anchors BEFORE stripping, so our
            // single section can move up into the floors section's slot.
            Transform floorsTitle = content.Find("FloorsTitle");
            Transform wallsTitle = content.Find("WallsTitle");
            Transform floorsSection = (floors != null && floors.Count > 0)
                ? floors[0].transform.parent
                : content.Find("FloorsSection");

            Vector2? titleTop = null, sectionTop = null;
            if (floorsTitle is RectTransform ftRT)
                titleTop = ftRT.anchoredPosition;
            if (floorsSection is RectTransform fsRT)
                sectionTop = fsRT.anchoredPosition;

            // Strip: everything under Content except the background, the
            // walls title, and our section. (Also covers the second
            // walls-section the scene carries and any future additions.)
            foreach (Transform child in content)
            {
                if (child == section || child == wallsTitle) continue;
                if (child.name == "BG") continue;
                Object.Destroy(child.gameObject);
            }
            // Remove every original card inside our kept section.
            foreach (var ui in section.GetComponentsInChildren<DecorationUI>(true))
            {
                if (!ourCards.Contains(ui))
                    Object.Destroy(ui.gameObject);
            }

            // Move the walls title + section into the floors slot (Content
            // has no auto-layout; sections sit at fixed anchors).
            if (titleTop.HasValue && wallsTitle is RectTransform wtRT)
                wtRT.anchoredPosition = titleTop.Value;
            if (sectionTop.HasValue && section is RectTransform secRT)
                secRT.anchoredPosition = sectionTop.Value;

            // Shrink the scroll content to fit one section: one card row
            // (ours fit in a single 3-column row) + the title band.
            if (content is RectTransform contentRT)
            {
                float rowH = 300f;
                var grid = section.GetComponent<GridLayoutGroup>();
                if (grid != null)
                    rowH = grid.cellSize.y + grid.spacing.y;
                int rows = Mathf.CeilToInt(ourCards.Count / 3f);
                float top = sectionTop.HasValue
                    ? Mathf.Abs(sectionTop.Value.y)
                    : Mathf.Abs(((RectTransform)section).anchoredPosition.y);
                contentRT.sizeDelta = new Vector2(
                    contentRT.sizeDelta.x, top + rows * rowH + 40f);
            }

            // Swap the serialized lists: no floors, only our cards.
            floors?.Clear();
            walls.Clear();
            walls.AddRange(ourCards);

            // Gamepad navigation up from the cards should land on OUR tab.
            if (decTabButtonField != null)
                decTabButtonField.SetValue(win, ourTabButton);

            return win;
        }

        /// <summary>
        /// Copies every instance field (public + private, whole inheritance
        /// chain below MonoBehaviour) from one component to another. Used
        /// to transplant the cloned DecorationWindow's serialized state
        /// onto our RepaintedWindow subclass.
        /// </summary>
        private static void CopyComponentFields(Component from, Component to)
        {
            var t = from.GetType();
            while (t != null && t != typeof(MonoBehaviour) &&
                   t != typeof(Behaviour) && t != typeof(Component))
            {
                var fields = t.GetFields(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                foreach (var f in fields)
                    f.SetValue(to, f.GetValue(from));
                t = t.BaseType;
            }
        }

        /// <summary>
        /// Child path (relative to the DecorationUI root) of the preview
        /// thumbnail Image within each cloned card. Identified by one-shot
        /// log-dump diagnostic: the Image at Frame/TileMask/Tile is the
        /// wall preview (vanilla entries carry sprite names like "Wall23").
        /// </summary>
        private const string PREVIEW_IMAGE_PATH = "Frame/TileMask/Tile";

        private static void SwapPreviewSprite(
            GameObject cardGO, ModdedWallRegistry.WallType wallType)
        {
            if (!RepaintedPlugin.WallPreviewSprites.TryGetValue(wallType, out var previewSprite)
                || previewSprite == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"No preview sprite for {wallType}; keeping cloned template sprite.");
                return;
            }

            Transform tileT = cardGO.transform.Find(PREVIEW_IMAGE_PATH);
            if (tileT == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Preview thumbnail transform '{PREVIEW_IMAGE_PATH}' not found on card for {wallType}.");
                return;
            }

            Image tileImg = tileT.GetComponent<Image>();
            if (tileImg == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"'{PREVIEW_IMAGE_PATH}' has no Image component on card for {wallType}.");
                return;
            }

            tileImg.sprite = previewSprite;
        }
    }
}
