using System.Collections.Generic;
using System.Reflection;
using Repainted.Data;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Repainted.Patches
{
    /// <summary>
    /// Clones DecorationUI entries for each wall we injected into the materials list.
    ///
    /// DecorationWindow.Initialize() iterates decorationUIsWall and calls Initialize()
    /// on each entry. We use a Prefix so our cloned entries are in the list before
    /// that loop runs — they get wired up automatically by the vanilla code.
    ///
    /// A Postfix then overwrites the titleText for our entries, since vanilla's
    /// Initialize() sets it from a locale key ("WALL_25" etc.) that doesn't exist,
    /// and expands the scroll content height to fit the new entries.
    /// </summary>
    [HarmonyPatch(typeof(DecorationWindow), "Initialize")]
    public static class DecorationWindowInitializePatch
    {
        // Cached reflection
        private static FieldInfo decorationUIsWallField;
        private static FieldInfo uiIndexField;
        private static FieldInfo uiDecorationTypeField;
        private static FieldInfo uiPriceField;
        private static FieldInfo uiTitleTextField;

        // Snapshot before cloning: the single item height measured from a template
        private static float itemHeight;
        private static Transform savedContentParent;

        static void Prefix(DecorationWindow __instance)
        {
            if (ModdedWallRegistry.InjectedWalls.Count == 0) return;

            RepaintedPlugin.Logger.LogDebug(
                $"DecorationWindow Prefix: injecting {ModdedWallRegistry.InjectedWalls.Count} wall UI entries..."
            );

            try
            {
                if (decorationUIsWallField == null)
                {
                    decorationUIsWallField = typeof(DecorationWindow).GetField(
                        "decorationUIsWall",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                    uiIndexField = typeof(DecorationUI).GetField(
                        "index",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                    uiDecorationTypeField = typeof(DecorationUI).GetField(
                        "decorationType",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                    uiPriceField = typeof(DecorationUI).GetField(
                        "price",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                    uiTitleTextField = typeof(DecorationUI).GetField(
                        "titleText",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                }

                if (decorationUIsWallField == null || uiIndexField == null)
                {
                    RepaintedPlugin.Logger.LogError("Failed to resolve reflection fields!");
                    return;
                }

                var wallUIs = (List<DecorationUI>)decorationUIsWallField.GetValue(__instance);
                if (wallUIs == null || wallUIs.Count == 0)
                {
                    RepaintedPlugin.Logger.LogError("decorationUIsWall is null or empty!");
                    return;
                }

                DecorationUI template = wallUIs[wallUIs.Count - 1];
                Transform layoutParent = template.transform.parent;
                savedContentParent = layoutParent;

                RectTransform templateRect = template.GetComponent<RectTransform>();
                itemHeight = templateRect != null ? templateRect.rect.height : 0f;

                foreach (var moddedWall in ModdedWallRegistry.InjectedWalls)
                {
                    GameObject cloneObj = Object.Instantiate(
                        template.gameObject,
                        layoutParent
                    );
                    cloneObj.name = $"DecorationUI_Wall_{moddedWall.DisplayName}";

                    DecorationUI cloneUI = cloneObj.GetComponent<DecorationUI>();
                    if (cloneUI == null)
                    {
                        RepaintedPlugin.Logger.LogError("Cloned object missing DecorationUI component!");
                        Object.Destroy(cloneObj);
                        continue;
                    }

                    uiIndexField.SetValue(cloneUI, moddedWall.Index);
                    uiDecorationTypeField.SetValue(cloneUI, DecorationUI.DecorationType.WALL);
                    uiPriceField.SetValue(cloneUI, moddedWall.Price);
                    wallUIs.Add(cloneUI);
                    cloneObj.SetActive(true);

                    SwapPreviewSprite(cloneObj, moddedWall.Type);

                    RepaintedPlugin.Logger.LogDebug(
                        $"Cloned DecorationUI for '{moddedWall.DisplayName}' at index {moddedWall.Index}"
                    );
                }
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError($"Failed to inject wall UI entries: {ex}");
            }
        }

        static void Postfix(DecorationWindow __instance)
        {
            if (ModdedWallRegistry.InjectedWalls.Count == 0) return;

            try
            {
                var wallUIs = (List<DecorationUI>)decorationUIsWallField.GetValue(__instance);
                if (wallUIs == null) return;

                foreach (var ui in wallUIs)
                {
                    int idx = (int)uiIndexField.GetValue(ui);
                    if (ModdedWallRegistry.TryGetModdedWall(idx, out var moddedWall))
                    {
                        var titleTMP = (TMPro.TextMeshProUGUI)uiTitleTextField.GetValue(ui);
                        if (titleTMP != null)
                            titleTMP.text = moddedWall.DisplayName;
                    }
                }

                if (savedContentParent == null) return;
                ExpandScrollContent(savedContentParent);
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError($"Failed to set modded wall titles: {ex}");
            }
        }

        private static void ExpandScrollContent(Transform wallsSection)
        {
            ScrollRect scrollRect = null;
            Transform current = wallsSection;
            while (current != null)
            {
                scrollRect = current.GetComponent<ScrollRect>();
                if (scrollRect != null) break;
                current = current.parent;
            }

            if (scrollRect == null)
            {
                RepaintedPlugin.Logger.LogWarning("No ScrollRect found in hierarchy!");
                return;
            }

            RectTransform scrollContent = scrollRect.content;
            if (scrollContent == null)
            {
                RepaintedPlugin.Logger.LogWarning("ScrollRect.content is null!");
                return;
            }

            const int COLS = 3;
            int addedItems = ModdedWallRegistry.InjectedWalls.Count;
            int vanillaChildren = wallsSection.childCount - addedItems;
            int vanillaRemainder = vanillaChildren % COLS;
            int slotsFillingExistingRow = vanillaRemainder > 0
                ? Mathf.Min(addedItems, COLS - vanillaRemainder)
                : 0;
            int itemsNeedingNewRows = addedItems - slotsFillingExistingRow;
            int extraRows = Mathf.CeilToInt((float)itemsNeedingNewRows / COLS);

            if (extraRows <= 0) return;

            float rowHeight = itemHeight;
            var grid = wallsSection.GetComponent<GridLayoutGroup>();
            if (grid != null)
            {
                rowHeight = grid.cellSize.y + grid.spacing.y;
            }

            float extraHeight = extraRows * rowHeight;

            Vector2 sd = scrollContent.sizeDelta;
            scrollContent.sizeDelta = new Vector2(sd.x, sd.y + extraHeight);

            RectTransform wallsRect = wallsSection as RectTransform;
            if (wallsRect != null)
            {
                Vector2 wsd = wallsRect.sizeDelta;
                wallsRect.sizeDelta = new Vector2(wsd.x, wsd.y + extraHeight);
            }

            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// Child path (relative to the DecorationUI root) of the preview
        /// thumbnail Image within each cloned slot. Identified by
        /// one-shot log-dump diagnostic: the Image at Frame/TileMask/Tile
        /// is the wall preview (vanilla entries carry sprite names like
        /// "Wall23" here). The three sibling Images under "Frame" form a
        /// masked nested structure but only the innermost "Tile" is the
        /// actual preview we want to repaint.
        /// </summary>
        private const string PREVIEW_IMAGE_PATH = "Frame/TileMask/Tile";

        private static void SwapPreviewSprite(
            GameObject cloneObj, ModdedWallRegistry.WallType wallType)
        {
            if (!RepaintedPlugin.WallPreviewSprites.TryGetValue(wallType, out var previewSprite)
                || previewSprite == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"No preview sprite for {wallType}; keeping cloned template sprite."
                );
                return;
            }

            Transform tileT = cloneObj.transform.Find(PREVIEW_IMAGE_PATH);
            if (tileT == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Preview thumbnail transform '{PREVIEW_IMAGE_PATH}' not found on clone for {wallType}."
                );
                return;
            }

            Image tileImg = tileT.GetComponent<Image>();
            if (tileImg == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"'{PREVIEW_IMAGE_PATH}' has no Image component on clone for {wallType}."
                );
                return;
            }

            tileImg.sprite = previewSprite;
        }
    }
}
