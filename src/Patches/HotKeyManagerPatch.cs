using System.Collections.Generic;
using System.Reflection;
using Coffee.UIEffects;
using Repainted.Tools;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Repainted.Patches
{
    /// <summary>
    /// Injects the ColorPaletteTool into the hotbar after the game initializes.
    ///
    /// HotKeyManager holds parallel serialized lists:
    ///   hotkeyClickables  — the tool scripts
    ///   hotkeySlot         — RectTransform for each hotbar slot
    ///   selectedHotkeyOutlines / deselectedHotkeyOutlines — selection visuals
    ///   hotkeyEffects      — UIEffect hover/press effects
    ///
    /// We clone the PaintRoller's slot UI and tool GameObject, swap the script,
    /// and append to all lists so HotKeyManager treats it as a native 4th tool.
    ///
    /// Also patches Update() to add hotkey 4 support since the game only has
    /// InputActionRefs for hotkeys 1-3.
    /// </summary>
    [HarmonyPatch(typeof(HotKeyManager))]
    public static class HotKeyManagerPatch
    {
        private const float PALETTE_TILT_INWARD = 40f;
        private const float PALETTE_TILT_HORIZONTAL = -85f;
        private const float PALETTE_SCALE = 1.5f;
        private const float PAINT_BLOB_SMOOTHNESS = 0.9f;
        private static FieldInfo hotkeyClickablesField;
        private static FieldInfo hotkeySlotField;
        private static FieldInfo selectedOutlinesField;
        private static FieldInfo deselectedOutlinesField;
        private static FieldInfo hotkeyEffectsField;

        private static void EnsureReflection()
        {
            if (hotkeyClickablesField != null) return;

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            hotkeyClickablesField = typeof(HotKeyManager).GetField("hotkeyClickables", flags);
            hotkeySlotField = typeof(HotKeyManager).GetField("hotkeySlot", flags);
            selectedOutlinesField = typeof(HotKeyManager).GetField("selectedHotkeyOutlines", flags);
            deselectedOutlinesField = typeof(HotKeyManager).GetField("deselectedHotkeyOutlines", flags);
            hotkeyEffectsField = typeof(HotKeyManager).GetField("hotkeyEffects", flags);
        }

        [HarmonyPostfix]
        [HarmonyPatch("Start")]
        static void StartPostfix(HotKeyManager __instance)
        {
            EnsureReflection();

            var clickables = (List<HotkeyClickable>)hotkeyClickablesField.GetValue(__instance);
            var slots = (List<RectTransform>)hotkeySlotField.GetValue(__instance);
            var selectedOutlines = (List<Image>)selectedOutlinesField.GetValue(__instance);
            var deselectedOutlines = (List<Image>)deselectedOutlinesField.GetValue(__instance);
            var effects = (List<UIEffect>)hotkeyEffectsField.GetValue(__instance);

            var paintRoller = PaintRoller.Instance;
            if (paintRoller == null)
            {
                RepaintedPlugin.Logger.LogError(
                    "HotKeyManagerPatch: PaintRoller.Instance is null, can't create palette tool"
                );
                return;
            }

            var toolGO = Object.Instantiate(paintRoller.gameObject, paintRoller.transform.parent);
            toolGO.name = "ColorPaletteTool";

            var clonedRoller = toolGO.GetComponent<PaintRoller>();
            if (clonedRoller != null)
            {
                Object.Destroy(clonedRoller);
            }

            var paletteTool = toolGO.AddComponent<ColorPaletteTool>();

            SwapMeshForPalette(toolGO, paletteTool);

            var hotkeyFlags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            var pickUpField = typeof(HotkeyClickable).GetField("pickUpPosition", hotkeyFlags);
            var putDownField = typeof(HotkeyClickable).GetField("putDownPosition", hotkeyFlags);

            var prPickUp = (Transform)pickUpField.GetValue(paintRoller);
            var prPutDown = (Transform)putDownField.GetValue(paintRoller);

            Transform ourPickUp = toolGO.transform.Find(prPickUp.name);
            Transform ourPutDown = toolGO.transform.Find(prPutDown.name);

            if (ourPickUp == null || ourPutDown == null)
            {
                ourPickUp = new GameObject("PickUpPos").transform;
                ourPickUp.SetParent(toolGO.transform, false);
                ourPickUp.localPosition = prPickUp.localPosition;
                ourPickUp.localEulerAngles = prPickUp.localEulerAngles;

                ourPutDown = new GameObject("PutDownPos").transform;
                ourPutDown.SetParent(toolGO.transform, false);
                ourPutDown.localPosition = prPutDown.localPosition;
                ourPutDown.localEulerAngles = prPutDown.localEulerAngles;
            }

            pickUpField.SetValue(paletteTool, ourPickUp);
            putDownField.SetValue(paletteTool, ourPutDown);

            toolGO.SetActive(false);

            if (slots.Count == 0)
            {
                RepaintedPlugin.Logger.LogError(
                    "HotKeyManagerPatch: No hotbar slots to clone from"
                );
                return;
            }

            int sourceSlotIndex = slots.Count - 1;
            var sourceSlot = slots[sourceSlotIndex];
            var newSlotGO = Object.Instantiate(sourceSlot.gameObject, sourceSlot.transform.parent);
            newSlotGO.name = "HotkeySlot_ColorPalette";

            var newSlotRT = newSlotGO.GetComponent<RectTransform>();
            var sourceRT = sourceSlot;
            newSlotRT.anchoredPosition = sourceRT.anchoredPosition +
                new Vector2(sourceRT.sizeDelta.x + 10f, 0f);

            Image newSelectedOutline = null;
            Image newDeselectedOutline = null;
            UIEffect newEffect = null;

            if (selectedOutlines.Count > sourceSlotIndex)
            {
                var sourceSelOutline = selectedOutlines[sourceSlotIndex];
                var sourceDeselOutline = deselectedOutlines[sourceSlotIndex];

                var allImages = newSlotGO.GetComponentsInChildren<Image>(true);
                foreach (var img in allImages)
                {
                    if (img.gameObject.name == sourceSelOutline.gameObject.name)
                    {
                        newSelectedOutline = img;
                        img.enabled = false;
                    }
                    else if (img.gameObject.name == sourceDeselOutline.gameObject.name)
                    {
                        newDeselectedOutline = img;
                        img.enabled = true;
                    }
                }

                if (newSelectedOutline == null && allImages.Length >= 2)
                {
                    newSelectedOutline = allImages[0];
                    newSelectedOutline.enabled = false;
                    newDeselectedOutline = allImages.Length > 1 ? allImages[1] : allImages[0];
                    newDeselectedOutline.enabled = true;
                }
            }

            if (effects.Count > sourceSlotIndex)
            {
                newEffect = newSlotGO.GetComponentInChildren<UIEffect>(true);
            }

            SwapIconSprite(newSlotGO, sourceSlot.gameObject,
                newSelectedOutline, newDeselectedOutline);
            SwapHotkeyLabel(newSlotGO, fromDigit: "3", toDigit: "4");

            clickables.Add(paletteTool);
            slots.Add(newSlotRT);

            if (newSelectedOutline != null)
                selectedOutlines.Add(newSelectedOutline);
            if (newDeselectedOutline != null)
                deselectedOutlines.Add(newDeselectedOutline);
            if (newEffect != null)
                effects.Add(newEffect);

            RepaintedPlugin.Logger.LogInfo(
                $"ColorPaletteTool injected into hotbar at index {clickables.Count - 1}"
            );
        }

        private static void SwapIconSprite(
            GameObject newSlotGO, GameObject sourceSlotGO,
            Image skipSelected, Image skipDeselected)
        {
            if (RepaintedPlugin.PaletteIconSprite == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    "Palette icon sprite not loaded — leaving cloned megaphone icon");
                return;
            }

            var cloneImages = newSlotGO.GetComponentsInChildren<Image>(true);
            var sourceImages = sourceSlotGO.GetComponentsInChildren<Image>(true);

            var skipSet = new HashSet<Image>();
            if (skipSelected != null) skipSet.Add(skipSelected);
            if (skipDeselected != null) skipSet.Add(skipDeselected);

            Image iconImage = null;
            for (int i = 0; i < cloneImages.Length && i < sourceImages.Length; i++)
            {
                var c = cloneImages[i];
                var s = sourceImages[i];
                if (c == null || s == null) continue;
                if (skipSet.Contains(c)) continue;
                if (s.sprite == null) continue;
                if (iconImage == null) iconImage = c;
                var lower = c.gameObject.name.ToLowerInvariant();
                if (lower.Contains("icon") || lower.Contains("image"))
                {
                    iconImage = c;
                    break;
                }
            }

            if (iconImage == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    "Could not identify icon Image on cloned hotbar slot; icon not swapped");
                return;
            }

            iconImage.sprite = RepaintedPlugin.PaletteIconSprite;
            iconImage.preserveAspect = true;
        }

        private static void SwapHotkeyLabel(GameObject newSlotGO, string fromDigit, string toDigit)
        {
            foreach (var tmp in newSlotGO.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp != null && tmp.text != null && tmp.text.Trim() == fromDigit)
                {
                    tmp.text = toDigit;
                    return;
                }
            }
            foreach (var txt in newSlotGO.GetComponentsInChildren<Text>(true))
            {
                if (txt != null && txt.text != null && txt.text.Trim() == fromDigit)
                {
                    txt.text = toDigit;
                    return;
                }
            }

            RepaintedPlugin.Logger.LogWarning(
                $"Could not find a '{fromDigit}' label on the cloned slot to update");
        }

        private static void SwapMeshForPalette(GameObject toolGO, ColorPaletteTool paletteTool)
        {
            if (RepaintedPlugin.PalettePrefab == null)
            {
                RepaintedPlugin.Logger.LogWarning(
                    "HotKeyManagerPatch: PalettePrefab not loaded, keeping cloned roller mesh"
                );
                return;
            }

            MeshRenderer referenceRenderer = null;
            Shader gameShader = null;
            foreach (var mr in toolGO.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr.enabled && mr.sharedMaterial != null)
                {
                    referenceRenderer = mr;
                    gameShader = mr.sharedMaterial.shader;
                    break;
                }
            }

            foreach (var mr in toolGO.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.enabled = false;
            }

            var paletteInstance = Object.Instantiate(
                RepaintedPlugin.PalettePrefab, toolGO.transform
            );
            paletteInstance.name = "PaletteModel";

            paletteInstance.transform.localPosition = Vector3.zero;
            paletteInstance.transform.localRotation = Quaternion.Euler(
                180f,
                180f + PALETTE_TILT_INWARD,
                PALETTE_TILT_HORIZONTAL
            );
            paletteInstance.transform.localScale = Vector3.one * PALETTE_SCALE;

            int toolLayer = referenceRenderer != null
                ? referenceRenderer.gameObject.layer
                : toolGO.layer;

            foreach (var mr in paletteInstance.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.gameObject.layer = toolLayer;

                if (referenceRenderer != null)
                {
                    mr.renderingLayerMask = referenceRenderer.renderingLayerMask;
                    mr.shadowCastingMode = referenceRenderer.shadowCastingMode;
                    mr.receiveShadows = referenceRenderer.receiveShadows;
                    mr.lightProbeUsage = referenceRenderer.lightProbeUsage;
                    mr.reflectionProbeUsage = referenceRenderer.reflectionProbeUsage;
                    mr.probeAnchor = referenceRenderer.probeAnchor;
                }

                if (gameShader != null)
                {
                    var oldMats = mr.sharedMaterials;
                    var newMats = new Material[oldMats.Length];
                    for (int i = 0; i < oldMats.Length; i++)
                    {
                        var oldMat = oldMats[i];
                        var newMat = new Material(gameShader);
                        newMat.name = oldMat != null ? oldMat.name + "_GameShader" : "PaletteMat_GameShader";

                        if (oldMat != null)
                        {
                            if (oldMat.HasProperty("_BaseColor"))
                                newMat.SetColor("_BaseColor", oldMat.GetColor("_BaseColor"));
                            if (oldMat.HasProperty("_BaseMap") && oldMat.GetTexture("_BaseMap") != null)
                            {
                                newMat.SetTexture("_BaseMap", oldMat.GetTexture("_BaseMap"));
                                newMat.SetTextureScale("_BaseMap", oldMat.GetTextureScale("_BaseMap"));
                                newMat.SetTextureOffset("_BaseMap", oldMat.GetTextureOffset("_BaseMap"));
                            }
                            if (oldMat.HasProperty("_Smoothness"))
                                newMat.SetFloat("_Smoothness", oldMat.GetFloat("_Smoothness"));
                            if (oldMat.HasProperty("_Metallic"))
                                newMat.SetFloat("_Metallic", oldMat.GetFloat("_Metallic"));
                            if (oldMat.HasProperty("_BumpMap") && oldMat.GetTexture("_BumpMap") != null)
                                newMat.SetTexture("_BumpMap", oldMat.GetTexture("_BumpMap"));
                            if (oldMat.HasProperty("_BumpScale"))
                                newMat.SetFloat("_BumpScale", oldMat.GetFloat("_BumpScale"));

                            newMat.renderQueue = oldMat.renderQueue;

                            bool isBoardMaterial = oldMat.name.Contains("01___Default");
                            if (!isBoardMaterial)
                            {
                                newMat.SetFloat("_Smoothness", PAINT_BLOB_SMOOTHNESS);
                            }
                        }

                        newMats[i] = newMat;
                    }
                    mr.sharedMaterials = newMats;
                }
            }

            foreach (var t in paletteInstance.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = toolLayer;
            }

            var splotchTransform = paletteInstance.transform.Find("Splotch");
            if (splotchTransform == null)
            {
                foreach (Transform child in paletteInstance.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name.Equals("Splotch", System.StringComparison.OrdinalIgnoreCase))
                    {
                        splotchTransform = child;
                        break;
                    }
                }
            }

            if (splotchTransform != null)
            {
                var splotchRenderer = splotchTransform.GetComponent<MeshRenderer>();
                if (splotchRenderer != null)
                {
                    paletteTool.SetSplotchRenderer(splotchRenderer);
                }
                else
                {
                    RepaintedPlugin.Logger.LogWarning(
                        "Splotch transform found but has no MeshRenderer"
                    );
                }
            }
            else
            {
                RepaintedPlugin.Logger.LogWarning(
                    "Could not find 'Splotch' child in palette prefab"
                );
            }

        }

        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        static void UpdatePostfix(HotKeyManager __instance)
        {
            EnsureReflection();

            var clickables = (List<HotkeyClickable>)hotkeyClickablesField.GetValue(__instance);

            if (clickables.Count > 3 && clickables[3] != null)
            {
                if (Input.GetKeyDown(KeyCode.Alpha4))
                {
                    __instance.SelectButton(3);
                }
            }
        }
    }
}
