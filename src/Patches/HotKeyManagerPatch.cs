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
    /// and append to all lists so HotKeyManager treats it as a native extra tool.
    /// The palette always occupies the slot after the last vanilla tool (slot 5
    /// as of game v0.5.5, which added a native 4th tool).
    ///
    /// Also patches Update() to add a hotkey for our slot, since the game only
    /// has InputActionRefs for its own tools (hotkeys 1-4 as of v0.5.5).
    /// </summary>
    [HarmonyPatch(typeof(HotKeyManager))]
    public static class HotKeyManagerPatch
    {
        private const float PAINT_BLOB_SMOOTHNESS = 0.9f;

        /// <summary>Hotbar index our palette landed on (set during injection).</summary>
        private static int paletteIndex = -1;

        // The live palette model instance + the prefab's own authored
        // rotation, kept so the config offsets can be re-applied on the fly
        // (tune via ConfigurationManager, F1). The 2.0 prefab carries its
        // Z-up→Y-up correction in its root rotation — earlier code
        // OVERWROTE localRotation with constants calibrated for the old
        // model, which is what made the new palette render rotated. The
        // config rotation now composes WITH the authored rotation.
        private static Transform paletteModelInstance;
        private static Quaternion paletteBaseRotation = Quaternion.identity;

        /// <summary>
        /// (Re)apply the configured rotation offset + scale to the palette
        /// model, relative to the prefab's authored orientation. Hooked to
        /// the config entries' SettingChanged so tuning is live in-game.
        /// </summary>
        public static void ReapplyPaletteTransform()
        {
            if (paletteModelInstance == null) return;
            try
            {
                paletteModelInstance.localRotation =
                    Quaternion.Euler(RepaintedPlugin.PaletteRotation) *
                    paletteBaseRotation;
                paletteModelInstance.localScale =
                    Vector3.one * RepaintedPlugin.PaletteScale;
                paletteModelInstance.localPosition = RepaintedPlugin.PaletteOffset;
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"ReapplyPaletteTransform failed: {ex.Message}");
            }
        }

        // Clones of the board (wood) materials, for live smoothness tuning.
        private static readonly List<Material> boardMaterials = new List<Material>();

        // ALL palette material clones created by SwapMeshForPalette.
        // HotKeyManager is per-scene (no DontDestroyOnLoad), so injection
        // re-runs each scene load — the previous batch must be destroyed
        // or the materials leak (same class of leak DecorationManagerPatch
        // fixes with DestroyOwnedObjects).
        private static readonly List<Material> ownedPaletteMaterials = new List<Material>();

        /// <summary>Apply the configured board gloss to the wood material
        /// clones (live via SettingChanged / the dev tuner).</summary>
        public static void ReapplyWoodSmoothness()
        {
            float s = RepaintedPlugin.WoodSmoothness;
            foreach (var m in boardMaterials)
                if (m != null && m.HasProperty("_Smoothness"))
                    m.SetFloat("_Smoothness", s);
        }

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
            // Everything below runs inside HotKeyManager.Start — an
            // exception here would disrupt the game's own hotbar init
            // (same failure class as the 0.5.5 ButtonsWindow incident),
            // so the entire body is guarded.
            try
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

            // PARALLEL-LIST INVARIANT: the game indexes all five hotbar
            // lists by the same slot index (SelectButton touches the
            // outlines; RefreshEnablity iterates hotkeyEffects up to
            // hotkeySlot.Count). If any list is parallel with the slots
            // but we failed to resolve its entry for our slot, appending
            // to the others would leave that list short and hard-crash
            // the game's own iteration later. Abort cleanly instead.
            bool outlinesParallel = selectedOutlines.Count == slots.Count &&
                                    deselectedOutlines.Count == slots.Count;
            bool effectsParallel = effects.Count == slots.Count;
            if ((outlinesParallel &&
                 (newSelectedOutline == null || newDeselectedOutline == null)) ||
                (effectsParallel && newEffect == null))
            {
                RepaintedPlugin.Logger.LogError(
                    "HotKeyManagerPatch: could not resolve outline/effect " +
                    "for the cloned slot — aborting palette injection to " +
                    "keep the game's hotbar lists consistent.");
                Object.Destroy(toolGO);
                Object.Destroy(newSlotGO);
                return;
            }

            SwapIconSprite(newSlotGO, sourceSlot.gameObject,
                newSelectedOutline, newDeselectedOutline);
            // Source slot is the last vanilla tool; its label is its 1-based
            // slot number. Ours is one past it.
            SwapHotkeyLabel(newSlotGO,
                fromDigit: (sourceSlotIndex + 1).ToString(),
                toDigit: (sourceSlotIndex + 2).ToString());

            clickables.Add(paletteTool);
            slots.Add(newSlotRT);
            paletteIndex = clickables.Count - 1;

            if (outlinesParallel)
            {
                selectedOutlines.Add(newSelectedOutline);
                deselectedOutlines.Add(newDeselectedOutline);
            }
            if (effectsParallel)
                effects.Add(newEffect);

            RepaintedPlugin.Logger.LogInfo(
                $"ColorPaletteTool injected into hotbar at index {clickables.Count - 1}"
            );
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"HotKeyManagerPatch.StartPostfix crashed — palette tool not " +
                    $"installed this session: {ex}");
            }
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

            // Destroy the previous scene's material clones before making
            // this scene's batch (see ownedPaletteMaterials).
            foreach (var m in ownedPaletteMaterials)
                if (m != null) Object.Destroy(m);
            ownedPaletteMaterials.Clear();
            boardMaterials.Clear();

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
            // Preserve the prefab's authored orientation (the 2.0 model
            // carries its axis correction in the root rotation) and apply
            // the configurable offset/scale on top.
            paletteModelInstance = paletteInstance.transform;
            paletteBaseRotation = paletteInstance.transform.localRotation;
            ReapplyPaletteTransform();

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
                        ownedPaletteMaterials.Add(newMat);

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

                            // Enable the normal-map path when a bump map came
                            // through the copy (MPB/material property alone
                            // isn't enough — URP Lit needs the keyword).
                            if (newMat.HasProperty("_BumpMap") &&
                                newMat.GetTexture("_BumpMap") != null)
                            {
                                newMat.EnableKeyword("_NORMALMAP");
                            }

                            // Board material of the CC0 palette model (renamed
                            // from the old TurboSquid "01___Default").
                            bool isBoardMaterial = oldMat.name.Contains("Wood");
                            if (isBoardMaterial)
                            {
                                // The flat board reads poorly without some
                                // specular response — gloss is configurable
                                // (F1 / dev tuner) and applied live.
                                newMat.SetFloat("_Smoothness",
                                    RepaintedPlugin.WoodSmoothness);
                                boardMaterials.Add(newMat);
                            }
                            else
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

            // Diagnostic: confirm every sub-material made it through the
            // clone (a submesh/material mismatch renders the whole model
            // with one material — "all wood").
            foreach (var mr in paletteInstance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                int subs = mf != null && mf.sharedMesh != null
                    ? mf.sharedMesh.subMeshCount : -1;
                RepaintedPlugin.Logger.LogDebug(
                    $"Palette renderer '{mr.gameObject.name}': {subs} submeshes, " +
                    $"{mr.sharedMaterials.Length} materials [" +
                    string.Join(", ", System.Array.ConvertAll(
                        mr.sharedMaterials, m => m != null ? m.name : "null")) + "]");
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

            if (splotchTransform != null &&
                splotchTransform.GetComponent<MeshRenderer>() != null)
            {
                paletteTool.SetSplotchRenderer(
                    splotchTransform.GetComponent<MeshRenderer>());
            }
            else
            {
                // 2.0 prefab: the whole palette is ONE renderer with the
                // dabs as sub-materials — find the custom-color dab by
                // material name and drive it per material index.
                bool found = false;
                foreach (var mr in paletteInstance.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var shared = mr.sharedMaterials;
                    for (int i = 0; i < shared.Length; i++)
                    {
                        var m = shared[i];
                        if (m == null) continue;
                        if (m.name.Contains("Splotch") ||
                            m.name.Contains("Paint_Custom_ColorPicker"))
                        {
                            paletteTool.SetSplotchRenderer(mr, i);
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
                if (!found)
                {
                    RepaintedPlugin.Logger.LogWarning(
                        "No splotch found in palette prefab (child 'Splotch' " +
                        "or material 'Paint_Custom_ColorPicker') — the " +
                        "active-color dab won't update.");
                }
            }

            RegisterOuterDabs(paletteInstance, paletteTool);
        }

        /// <summary>
        /// Locate the 7 outer paint dabs on the palette model (sub-materials
        /// named Paint_&lt;Color&gt;, cloned to Paint_&lt;Color&gt;_GameShader) and hand
        /// them to the tool in canonical order, each with its sub-material
        /// index and authored default color. Same per-sub-material MPB
        /// mechanism the central splotch already uses for the active color —
        /// these show the player's favorite colors instead.
        /// </summary>
        private static void RegisterOuterDabs(
            GameObject paletteInstance, ColorPaletteTool paletteTool)
        {
            // Pick the renderer carrying the most dab materials (the 2.0
            // prefab is a single renderer; legacy prefabs without Paint_*
            // dabs simply register nothing).
            MeshRenderer bestRenderer = null;
            List<(int, Color)> bestDabs = null;

            foreach (var mr in paletteInstance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var shared = mr.sharedMaterials;
                var dabs = new List<(int, Color)>();

                foreach (var dabName in ColorPaletteTool.OuterDabNames)
                {
                    for (int i = 0; i < shared.Length; i++)
                    {
                        var m = shared[i];
                        // Clone names are "<original>_GameShader"; StartsWith
                        // keeps Paint_Blue from matching Paint_DarkBlue.
                        if (m == null || !m.name.StartsWith(dabName)) continue;

                        Color authored = m.HasProperty("_BaseColor")
                            ? m.GetColor("_BaseColor")
                            : Color.white;
                        dabs.Add((i, authored));
                        break;
                    }
                }

                if (bestDabs == null || dabs.Count > bestDabs.Count)
                {
                    bestRenderer = mr;
                    bestDabs = dabs;
                }
            }

            if (bestRenderer != null && bestDabs != null && bestDabs.Count > 0)
            {
                paletteTool.SetOuterDabRenderer(bestRenderer, bestDabs);
                RepaintedPlugin.Logger.LogInfo(
                    $"Palette: registered {bestDabs.Count}/" +
                    $"{ColorPaletteTool.OuterDabNames.Length} outer dabs " +
                    "for favorite-color display.");
            }
            else
            {
                RepaintedPlugin.Logger.LogInfo(
                    "Palette: no Paint_<Color> dab materials found — outer " +
                    "dabs keep their baked colors (legacy prefab?).");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        static void UpdatePostfix(HotKeyManager __instance)
        {
            // Digit keys only go up to 9 (Alpha1..Alpha9).
            if (paletteIndex < 0 || paletteIndex > 8)
            {
                return;
            }

            try
            {
                EnsureReflection();

                var clickables = (List<HotkeyClickable>)hotkeyClickablesField.GetValue(__instance);

                if (clickables.Count > paletteIndex && clickables[paletteIndex] != null)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + paletteIndex))
                    {
                        __instance.SelectButton(paletteIndex);
                    }
                }
            }
            catch (System.Exception ex)
            {
                RepaintedPlugin.Logger.LogError(
                    $"HotKeyManagerPatch.UpdatePostfix crashed: {ex}");
            }
        }
    }
}
