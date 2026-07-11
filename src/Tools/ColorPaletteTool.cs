using System;
using System.Collections.Generic;
using System.Reflection;
using Repainted.Data;
using Repainted.Rendering;
using Repainted.UI;
using Repainted.Util;
using UnityEngine;

namespace Repainted.Tools
{
    /// <summary>
    /// A standalone tool for recoloring ANY wall. Completely decoupled
    /// from the vanilla paint system — does NOT dispatch to FloorClickable
    /// mouse events. Instead, performs its own raycast in Update(), identifies
    /// the FloorClickable under the crosshair, and directly manipulates its
    /// overlay (TileColorStore + the tile's cached MaterialPropertyBlock).
    ///
    /// 1.5 model: the palette only RECOLORS — there is no style UI. The
    /// style is inferred (Auto): an existing overlay keeps its style; a
    /// plain vanilla wall gets a Full Color overlay; a vanilla two-tone
    /// gets its colored band recolored; a patterned wall is replaced with
    /// Full Color. Style CHANGES go through the game's own shop + roller
    /// (vanilla walls or our 3 brush walls).
    ///
    /// Interactions:
    ///   Left click         — repaint the hovered wall in the picker color
    ///   Shift + Left click — eyedropper: copy the wall's current color
    ///   Right click        — open/close the color picker UI (unlocks cursor)
    ///
    /// Hover:
    ///   Managed entirely by this tool. Every wall highlights — every wall
    ///   is paintable now.
    /// </summary>
    public class ColorPaletteTool : HotkeyClickable
    {
        private const float RAYCAST_DISTANCE = 6f;
        private const float BOTTOM_BAND_FRACTION = 0.15f;
        private const float HORIZONTAL_MARGIN_FRACTION = 0.1f;
        private const int SAMPLE_PIXEL_STEP = 4;

        public static ColorPaletteTool Instance { get; private set; }

        // Palette model splotch — shows the current active color on the tool.
        // Either a dedicated child renderer (legacy prefab) or a sub-material
        // index on the single palette renderer (2.0 prefab).
        private MeshRenderer splotchRenderer;
        private int splotchMaterialIndex = -1;
        private MaterialPropertyBlock splotchBlock;

        /// <summary>
        /// Canonical order of the 7 outer paint dabs, matching their angular
        /// order around the generated palette model. favorite[i] displays on
        /// dab OuterDabNames[i].
        /// </summary>
        public static readonly string[] OuterDabNames =
        {
            "Paint_Red", "Paint_Orange", "Paint_Yellow", "Paint_Green",
            "Paint_Blue", "Paint_DarkBlue", "Paint_White",
        };

        // Outer dabs — same MPB-per-sub-material mechanism as the splotch.
        // Each entry: sub-material index on dabRenderer + the dab's authored
        // default color (per-dab fallback when that favorite slot is unset).
        private MeshRenderer dabRenderer;
        private readonly List<(int matIndex, Color defaultColor)> outerDabs =
            new List<(int, Color)>();
        private MaterialPropertyBlock dabBlock;

        // Currently hovered wall tile (managed by our own raycast, not RayShooter)
        private FloorClickable hoveredTile;

        // Cached average color of the concrete base texture's bottom band,
        // used to compensate eyedropper samples of vanilla walls whose color
        // is baked into the albedo texture rather than _BaseColor
        private Color? cachedConcreteBottomAvg;

        // Reflection cache for FloorClickable private fields
        private static FieldInfo fcIdField;
        private static FieldInfo fcDecorationIndexField;
        private static FieldInfo fcMeshRendererField;
        private static FieldInfo fcDecorationType;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                UnityEngine.Object.Destroy(gameObject);
                return;
            }
            Instance = this;

            EnsureReflection();

            ModdedWallRegistry.OnColorChanged += UpdateSplotchColor;
            TileColorStore.FavoritesChanged += RefreshOuterDabColors;
        }

        private void OnDestroy()
        {
            ModdedWallRegistry.OnColorChanged -= UpdateSplotchColor;
            TileColorStore.FavoritesChanged -= RefreshOuterDabColors;
        }

        /// <summary>
        /// Called by HotKeyManagerPatch after instantiating the palette model.
        /// Caches the splotch renderer (and, for the 2.0 single-renderer
        /// prefab, the sub-material index of the custom-color dab).
        /// </summary>
        public void SetSplotchRenderer(MeshRenderer renderer, int materialIndex = -1)
        {
            splotchRenderer = renderer;
            splotchMaterialIndex = materialIndex;
            splotchBlock = new MaterialPropertyBlock();

            // Initialize with the current active color
            UpdateSplotchColor(ModdedWallRegistry.CurrentColor);
        }

        /// <summary>
        /// Updates the splotch color via MaterialPropertyBlock — per
        /// material index on the 2.0 prefab, whole renderer on the legacy
        /// one. Called whenever the active color changes (apply, eyedrop,
        /// picker).
        /// </summary>
        private void UpdateSplotchColor(Color color)
        {
            if (splotchRenderer == null || splotchBlock == null) return;

            if (splotchMaterialIndex >= 0)
            {
                splotchRenderer.GetPropertyBlock(splotchBlock, splotchMaterialIndex);
                splotchBlock.SetColor("_BaseColor", color);
                splotchRenderer.SetPropertyBlock(splotchBlock, splotchMaterialIndex);
            }
            else
            {
                splotchRenderer.GetPropertyBlock(splotchBlock);
                splotchBlock.SetColor("_BaseColor", color);
                splotchRenderer.SetPropertyBlock(splotchBlock);
            }
        }

        /// <summary>
        /// Called by HotKeyManagerPatch after instantiating the palette model.
        /// Registers the renderer carrying the 7 outer dab sub-materials,
        /// with each dab's sub-material index and authored default color, in
        /// OuterDabNames order.
        /// </summary>
        public void SetOuterDabRenderer(
            MeshRenderer renderer, List<(int matIndex, Color defaultColor)> dabs)
        {
            dabRenderer = renderer;
            outerDabs.Clear();
            if (dabs != null) outerDabs.AddRange(dabs);
            dabBlock = new MaterialPropertyBlock();

            RefreshOuterDabColors();
        }

        /// <summary>
        /// Repaint the outer dabs from the player's favorites: favorite i
        /// shows on dab i; an unset (placeholder) favorite slot falls back
        /// to that dab's authored default color. With no favorites saved the
        /// palette therefore shows its default rainbow. Runs on model setup
        /// and whenever TileColorStore.FavoritesChanged fires (picker toggle,
        /// slot load/wipe).
        /// </summary>
        private void RefreshOuterDabColors()
        {
            if (dabRenderer == null || dabBlock == null) return;

            var favs = TileColorStore.FavoriteColors;
            for (int i = 0; i < outerDabs.Count; i++)
            {
                Color c = outerDabs[i].defaultColor;
                if (i < favs.Count && !TileColorStore.IsPlaceholderFavorite(favs[i]))
                    c = favs[i];

                dabRenderer.GetPropertyBlock(dabBlock, outerDabs[i].matIndex);
                dabBlock.SetColor("_BaseColor", c);
                dabRenderer.SetPropertyBlock(dabBlock, outerDabs[i].matIndex);
            }
        }

        private static void EnsureReflection()
        {
            if (fcIdField != null) return;

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            fcIdField = typeof(FloorClickable).GetField("id", flags);
            fcDecorationIndexField = typeof(FloorClickable).GetField("currentDecorationIndex", flags);
            fcMeshRendererField = typeof(FloorClickable).GetField("meshRenderer", flags);
            fcDecorationType = typeof(FloorClickable).GetField("decorationType", flags);
        }

        public override LayerMask GetInteractableLayers()
        {
            return 1 << RayShooter.WALL_LAYER;
        }

        public override void PickUp()
        {
            base.PickUp();
            UpdateSplotchColor(ModdedWallRegistry.CurrentColor);
            ShowBaseMenu();
        }

        public override void OnPickedUp()
        {
            ShowBaseMenu();
        }

        public override void OnPutDown()
        {
            base.OnPutDown();
            ClearHover();
            ColorPickerOverlay.Instance?.ClosePicker();
        }

        public override void Reset()
        {
            base.Reset();
            ClearHover();
            SingletonBehaviour<ButtonsWindow>.Instance.Close();
            ColorPickerOverlay.Instance?.ClosePicker();
        }

        public override void RepaintButtonsForEndHover()
        {
            // RayShooter may call this but we manage our own hover,
            // so just show base menu
            ShowBaseMenu();
        }

        private void Update()
        {
            if (!IsPicked) return;

            // Don't process any input while game windows are open
            // (pause menu, settings, etc.) — the mod is effectively paused
            if (!SingletonBehaviour<UIManager>.Instance.AllWindowsClosed()) return;

            // Don't process input while color picker is open (cursor is free)
            if (ColorPickerOverlay.Instance != null && ColorPickerOverlay.Instance.IsPickerOpen)
            {
                // Only handle right-click to close picker
                if (Input.GetMouseButtonDown(1))
                {
                    TogglePicker();
                }
                return;
            }

            // Perform our own hover raycast
            UpdateHover();

#if REPAINTED_DEV
            // Dev builds only: visual, instant-feedback model orientation
            // editing while holding the palette (numpad; F6 toggles off).
            HandleTuneInput();
#endif

            // Handle input
            if (Input.GetMouseButtonDown(1))
            {
                TogglePicker();
            }
            else if (Input.GetMouseButtonDown(0))
            {
                if (Input.GetKey(KeyCode.LeftShift))
                {
                    Eyedropper();
                }
                else
                {
                    ApplyColor();
                }
            }
        }

#if REPAINTED_DEV
        // ─── Palette model tune mode (DEV BUILDS ONLY) ─────────────────
        //
        // Enabled by default while holding the palette; the numpad nudges
        // the model with INSTANT visual feedback, writing straight into the
        // config entries (auto-saved, visible in F1):
        //   8/2 = X pitch   4/6 = Y yaw   7/9 = Z roll
        //   +/- = scale     5 = print values   0 = reset
        //   step 15° — hold Ctrl for 1°, Shift for 90°.
        // F6 toggles it off/on if the numpad is needed for something else.
        // Compiled out of release builds (build.sh without --dev).

        private bool tuneMode = true;

        private void HandleTuneInput()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                tuneMode = !tuneMode;
                RepaintedPlugin.Logger.LogMessage(tuneMode
                    ? "Palette tune ON — numpad: 8/2=pitch 4/6=yaw 7/9=roll " +
                      "+/-=scale 5=print 0=reset (Ctrl=1°, Shift=90°)"
                    : "Palette tune OFF");
                UpdateMenu();
            }
            if (!tuneMode) return;

            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            bool changed = false;
            if (!alt)
            {
                // Rotation mode
                float step = ctrl ? 1f : shift ? 90f : 15f;
                if (Input.GetKeyDown(KeyCode.Keypad8)) { Nudge(RepaintedPlugin.CfgPaletteRotX, step); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad2)) { Nudge(RepaintedPlugin.CfgPaletteRotX, -step); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad4)) { Nudge(RepaintedPlugin.CfgPaletteRotY, -step); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad6)) { Nudge(RepaintedPlugin.CfgPaletteRotY, step); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad7)) { Nudge(RepaintedPlugin.CfgPaletteRotZ, -step); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad9)) { Nudge(RepaintedPlugin.CfgPaletteRotZ, step); changed = true; }
            }
            else
            {
                // Position mode (hold Alt): 8/2 fwd/back, 4/6 left/right, 7/9 up/down
                float pstep = ctrl ? 0.002f : shift ? 0.05f : 0.01f;
                if (Input.GetKeyDown(KeyCode.Keypad8)) { NudgeOff(RepaintedPlugin.CfgPaletteOffZ, pstep); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad2)) { NudgeOff(RepaintedPlugin.CfgPaletteOffZ, -pstep); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad4)) { NudgeOff(RepaintedPlugin.CfgPaletteOffX, -pstep); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad6)) { NudgeOff(RepaintedPlugin.CfgPaletteOffX, pstep); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad7)) { NudgeOff(RepaintedPlugin.CfgPaletteOffY, pstep); changed = true; }
                if (Input.GetKeyDown(KeyCode.Keypad9)) { NudgeOff(RepaintedPlugin.CfgPaletteOffY, -pstep); changed = true; }
            }
            if (Input.GetKeyDown(KeyCode.KeypadPlus))
            {
                RepaintedPlugin.CfgPaletteScale.Value =
                    Mathf.Clamp(RepaintedPlugin.CfgPaletteScale.Value + 0.1f, 0.2f, 4f);
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.KeypadMinus))
            {
                RepaintedPlugin.CfgPaletteScale.Value =
                    Mathf.Clamp(RepaintedPlugin.CfgPaletteScale.Value - 0.1f, 0.2f, 4f);
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.KeypadMultiply))
            {
                RepaintedPlugin.CfgWoodSmoothness.Value =
                    Mathf.Clamp01(RepaintedPlugin.CfgWoodSmoothness.Value + 0.05f);
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.KeypadDivide))
            {
                RepaintedPlugin.CfgWoodSmoothness.Value =
                    Mathf.Clamp01(RepaintedPlugin.CfgWoodSmoothness.Value - 0.05f);
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.Keypad0))
            {
                RepaintedPlugin.CfgPaletteRotX.Value = 0f;
                RepaintedPlugin.CfgPaletteRotY.Value = 0f;
                RepaintedPlugin.CfgPaletteRotZ.Value = 0f;
                RepaintedPlugin.CfgPaletteScale.Value = 1f;
                RepaintedPlugin.CfgPaletteOffX.Value = 0f;
                RepaintedPlugin.CfgPaletteOffY.Value = 0f;
                RepaintedPlugin.CfgPaletteOffZ.Value = 0f;
                changed = true;
            }

            if (changed || Input.GetKeyDown(KeyCode.Keypad5))
            {
                RepaintedPlugin.Logger.LogMessage(
                    $"Palette model: rot=({RepaintedPlugin.CfgPaletteRotX.Value:F0}, " +
                    $"{RepaintedPlugin.CfgPaletteRotY.Value:F0}, " +
                    $"{RepaintedPlugin.CfgPaletteRotZ.Value:F0}) " +
                    $"off=({RepaintedPlugin.CfgPaletteOffX.Value:F3}, " +
                    $"{RepaintedPlugin.CfgPaletteOffY.Value:F3}, " +
                    $"{RepaintedPlugin.CfgPaletteOffZ.Value:F3}) " +
                    $"scale={RepaintedPlugin.CfgPaletteScale.Value:F2} " +
                    $"wood={RepaintedPlugin.CfgWoodSmoothness.Value:F2}");
            }
        }

        private static void NudgeOff(
            BepInEx.Configuration.ConfigEntry<float> entry, float delta)
        {
            entry.Value = Mathf.Clamp(entry.Value + delta, -0.5f, 0.5f);
        }

        /// <summary>Nudge a rotation entry, wrapping into [-180, 180] so
        /// repeated presses never hit the config range clamp.</summary>
        private static void Nudge(
            BepInEx.Configuration.ConfigEntry<float> entry, float delta)
        {
            float v = entry.Value + delta;
            entry.Value = Mathf.Repeat(v + 180f, 360f) - 180f;
        }

        /// <summary>
        /// Dev-only on-screen tune panel drawn with IMGUI — deliberately
        /// independent of the game's UI so it can't be hidden by
        /// ButtonsWindow quirks. Visible whenever the palette is held and
        /// tune mode is on; if you can't see this panel, the running DLL
        /// is not a dev build.
        /// </summary>
        private void OnGUI()
        {
            if (!IsPicked || !tuneMode) return;
            try
            {
                float y0 = Screen.height * 0.30f;
                GUI.Box(new Rect(10, y0, 520, 98), GUIContent.none);
                GUI.Label(new Rect(18, y0 + 4, 510, 22),
                    $"PALETTE TUNE (dev)  rot(" +
                    $"{RepaintedPlugin.CfgPaletteRotX.Value:F0}, " +
                    $"{RepaintedPlugin.CfgPaletteRotY.Value:F0}, " +
                    $"{RepaintedPlugin.CfgPaletteRotZ.Value:F0})  " +
                    $"off({RepaintedPlugin.CfgPaletteOffX.Value:F3}, " +
                    $"{RepaintedPlugin.CfgPaletteOffY.Value:F3}, " +
                    $"{RepaintedPlugin.CfgPaletteOffZ.Value:F3})");
                GUI.Label(new Rect(18, y0 + 26, 510, 22),
                    $"scale {RepaintedPlugin.CfgPaletteScale.Value:F2} (+/-)   " +
                    $"wood gloss {RepaintedPlugin.CfgWoodSmoothness.Value:F2} (*/÷)");
                GUI.Label(new Rect(18, y0 + 48, 510, 22),
                    "numpad: 8/2 pitch  4/6 yaw  7/9 roll · +Alt = move (8/2 fwd, 4/6 side, 7/9 up)");
                GUI.Label(new Rect(18, y0 + 70, 510, 22),
                    "Ctrl = fine, Shift = coarse · 5 print · 0 reset · auto-saves · F6 hides");
            }
            catch { }
        }
#endif

        private bool hoverHighlightActive;

        // True while the hovered wall is palette-excluded (Toy Speckle):
        // no highlight, no Repaint entry, clicks do nothing. Computed on
        // hover TRANSITION only (IsExcluded is a name check, but it still
        // calls into DecorationManager).
        private bool hoveredExcluded;

        // Cache the last raycast collider→FloorClickable lookup; while the
        // player stares at one wall this avoids a GetComponent per frame.
        private Collider lastHitCollider;
        private FloorClickable lastHitClickable;

        private void UpdateHover()
        {
            // Don't hover while UI windows are open
            if (!SingletonBehaviour<UIManager>.Instance.AllWindowsClosed())
            {
                ClearHover();
                return;
            }

            Camera cam = SingletonBehaviour<PlayerLook>.Instance.MainCamera;
            LayerMask mask = 1 << RayShooter.WALL_LAYER;

            FloorClickable newHover = null;

            if (Physics.Raycast(cam.transform.position,
                cam.transform.TransformDirection(Vector3.forward),
                out RaycastHit hit, RAYCAST_DISTANCE, mask))
            {
                FloorClickable fc;
                if (hit.collider == lastHitCollider)
                {
                    fc = lastHitClickable;
                }
                else
                {
                    fc = hit.collider.GetComponent<FloorClickable>();
                    lastHitCollider = hit.collider;
                    lastHitClickable = fc;
                }
                if (fc != null)
                {
                    var decType = (DecorationUI.DecorationType)fcDecorationType.GetValue(fc);
                    if (decType == DecorationUI.DecorationType.WALL)
                    {
                        newHover = fc;
                    }
                }
            }

            // Transition hover target. Every wall is paintable — except
            // the excluded ones (Toy Speckle), which get no highlight as
            // the "can't repaint" signal.
            if (newHover != hoveredTile)
            {
                if (hoveredTile != null && hoverHighlightActive)
                {
                    hoveredTile.OnMouseHoverEnded();
                    hoverHighlightActive = false;
                }

                hoveredTile = newHover;
                hoveredExcluded = false;

                if (hoveredTile != null)
                {
                    int decorationIndex = (int)fcDecorationIndexField.GetValue(hoveredTile);
                    hoveredExcluded = WallStyleInference.IsExcluded(decorationIndex);

                    if (!hoveredExcluded)
                    {
                        hoveredTile.OnMouseHoverStarted();
                        hoverHighlightActive = true;
                    }
                }

                UpdateMenu();
            }
        }

        private void ClearHover()
        {
            if (hoveredTile != null)
            {
                if (hoverHighlightActive)
                {
                    hoveredTile.OnMouseHoverEnded();
                    hoverHighlightActive = false;
                }
                hoveredTile = null;
            }
        }

        private void UpdateMenu()
        {
            if (!IsPicked) return;

            if (hoveredTile != null && hoveredExcluded)
            {
                // Excluded wall (Toy Speckle): no Repaint entry — the
                // pattern doesn't combine with recoloring at all.
                SingletonBehaviour<ButtonsWindow>.Instance.RepaintWithKeyCodes(
                    new Dictionary<KeyCode, (string, Action)>
                    {
                        { KeyCode.Mouse1, ("Color Picker", null) },
                        { KeyCode.Mouse0, ("Can't Repaint This Wall", null) },
                    },
                    transform, false
                );
            }
            else if (hoveredTile != null)
            {
                SingletonBehaviour<ButtonsWindow>.Instance.RepaintWithKeyCodes(
                    new Dictionary<KeyCode, (string, Action)>
                    {
                        { KeyCode.Mouse1, ("Color Picker", null) },
                        { KeyCode.LeftShift, ("+ Click: Eyedrop", null) },
                        { KeyCode.Mouse0, ("Repaint Wall", null) },
                    },
                    transform, false
                );
            }
            else
            {
                ShowBaseMenu();
            }
        }

        private void ShowBaseMenu()
        {
            if (!IsPicked) return;

            SingletonBehaviour<ButtonsWindow>.Instance.RepaintWithKeyCodes(
                new Dictionary<KeyCode, (string, Action)>
                {
                    { KeyCode.Mouse1, ("Color Picker", null) },
                },
                transform, false
            );
        }

        private void ApplyColor()
        {
            if (hoveredTile == null || hoveredExcluded) return;

            int tileId = (int)fcIdField.GetValue(hoveredTile);
            int decorationIndex = (int)fcDecorationIndexField.GetValue(hoveredTile);
            var renderer = (MeshRenderer)fcMeshRendererField.GetValue(hoveredTile);
            if (renderer == null) return;

            Color color = ModdedWallRegistry.CurrentColor;

            // Auto style + underlying index: an existing overlay keeps its
            // style and underlying wall; a fresh paint infers the style
            // from the wall underneath and records that wall as underlying.
            ModdedWallRegistry.WallType style;
            int underlyingIndex;
            if (TileColorStore.TryGetTileColor(tileId, out var existing))
            {
                style = existing.ToWallType();
                underlyingIndex = existing.underlyingIndex;
            }
            else
            {
                if (ModdedWallRegistry.IsModdedWall(decorationIndex))
                {
                    // A brush index outside a commit window shouldn't
                    // happen; don't record an untruthful underlying index.
                    RepaintedPlugin.Logger.LogWarning(
                        $"Palette: tile {tileId} unexpectedly points at brush " +
                        $"index {decorationIndex}; skipping paint.");
                    return;
                }
                int wallCount = Patches.DecorationManagerAwakePatch.GetWallMaterialCount();
                if (decorationIndex < 0 ||
                    (wallCount > 0 && decorationIndex >= wallCount))
                {
                    // Dangling record that hasn't been repaired yet — never
                    // store an invalid underlying index. Repair to 0 first.
                    RepaintedPlugin.Logger.LogWarning(
                        $"Palette: tile {tileId} had dangling index " +
                        $"{decorationIndex}; repairing to 0 before paint.");
                    fcDecorationIndexField.SetValue(hoveredTile, 0);
                    TileColorStore.WriteWallIndex(tileId, 0);
                    decorationIndex = 0;
                }

                bool tintMode = RepaintedPlugin.CfgPatternedMode != null &&
                    RepaintedPlugin.CfgPatternedMode.Value ==
                        RepaintedPlugin.PatternedRecolorKind.Tint;

                var info = WallStyleInference.Classify(decorationIndex);
                switch (info.Category)
                {
                    case WallStyleInference.WallCategory.Excluded:
                        return; // hover should have blocked this already

                    case WallStyleInference.WallCategory.DecalOverBase:
                        // Base region follows the SAME Replace/Tint decision
                        // the paired base wall would get, locked in at paint
                        // time — decal + base walls painted the same color
                        // render identically, modulo the decal art.
                        style = tintMode
                            ? ModdedWallRegistry.WallType.DecalRecolorTint
                            : ModdedWallRegistry.WallType.DecalRecolor;
                        break;

                    case WallStyleInference.WallCategory.DecalOverFlat:
                        // Flat-background decals (Summer Palm) sit on the
                        // stucco family — their base always recolors like a
                        // solid wall (concrete × color).
                        style = ModdedWallRegistry.WallType.DecalRecolor;
                        break;

                    case WallStyleInference.WallCategory.TwoTone:
                        style = ModdedWallRegistry.WallType.BottomStripe;
                        break;

                    case WallStyleInference.WallCategory.Patterned:
                        style = tintMode && info.HasMean
                            ? ModdedWallRegistry.WallType.PatternTint
                            : ModdedWallRegistry.WallType.FullColor;
                        break;

                    default: // Solid
                        style = ModdedWallRegistry.WallType.FullColor;
                        break;
                }
                underlyingIndex = decorationIndex;
            }

            TileColorStore.CommitOverlayTile(tileId, color, style, underlyingIndex);

            if (TileColorStore.TryGetTileColor(tileId, out var entry))
                OverlayRenderer.Apply(hoveredTile, renderer, tileId, entry);

            TileColorStore.PushHistory(color);
            ColorPickerOverlay.Instance?.RefreshHistorySwatches();

            RepaintedPlugin.Logger.LogDebug(
                $"Palette: applied color to tile {tileId} (style {style}, " +
                $"underlying {underlyingIndex}) — " +
                $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})"
            );
        }

        private void Eyedropper()
        {
            if (hoveredTile == null) return;

            int tileId = (int)fcIdField.GetValue(hoveredTile);
            int decorationIndex = (int)fcDecorationIndexField.GetValue(hoveredTile);

            Color? picked = null;
            bool fromOverlay = false;

            if (TileColorStore.TryGetTileColor(tileId, out var entry))
            {
                // Overlaid wall — read from our per-tile color store
                picked = entry.ToColor();
                fromOverlay = true;
            }
            else
            {
                // Vanilla wall — extract color from the game's material system
                picked = SampleVanillaWallColor(decorationIndex);
            }

            if (picked.HasValue)
            {
                Color color = picked.Value;
                ModdedWallRegistry.ApplyColor(color);
                TileColorStore.PushHistory(color);
                ColorPickerOverlay.Instance?.SetColorFromExternal(color);

                RepaintedPlugin.Logger.LogDebug(
                    $"Palette: eyedropped color from tile {tileId} " +
                    $"(decor {decorationIndex}, {(fromOverlay ? "overlay" : "vanilla")}) — " +
                    $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})"
                );
            }
            else
            {
                RepaintedPlugin.Logger.LogDebug(
                    $"Palette: tile {tileId} has no color to eyedrop"
                );
            }
        }

        private Color? SampleVanillaWallColor(int decorationIndex)
        {
            var dm = SingletonBehaviour<DecorationManager>.Instance;
            if (dm == null) return null;

            // GetColor/GetTexture silently CLAMP out-of-range indices to 0,
            // which would make us sample the default wall and report it as
            // this wall's color — bounds-check first.
            int wallCount = Patches.DecorationManagerAwakePatch.GetWallMaterialCount();
            if (decorationIndex < 0 || (wallCount > 0 && decorationIndex >= wallCount))
            {
                RepaintedPlugin.Logger.LogWarning(
                    $"Vanilla eyedrop: decoration index {decorationIndex} out of " +
                    $"range (wall count {wallCount}) — skipping sample.");
                return null;
            }

            // DecorationManager uses 0-based id for GetColor/GetMaterial
            // decorationIndex from FloorClickable is already 0-based

            // Decal and patterned walls: sample the recolorable BASE region
            // (classification's mean) as it renders on screen — that's the
            // color that, painted back, reproduces the wall's current look.
            var info = WallStyleInference.Classify(decorationIndex);
            if (info.HasMean &&
                (info.Category == WallStyleInference.WallCategory.DecalOverBase ||
                 info.Category == WallStyleInference.WallCategory.DecalOverFlat ||
                 info.Category == WallStyleInference.WallCategory.Patterned))
            {
                Color mat = dm.GetColor(DecorationUI.DecorationType.WALL, decorationIndex);
                var mean = info.BaseMeanRaw;
                Color rendered = new Color(
                    Mathf.Clamp01(mean.r / 255f * mat.r),
                    Mathf.Clamp01(mean.g / 255f * mat.g),
                    Mathf.Clamp01(mean.b / 255f * mat.b),
                    1f);
                RepaintedPlugin.Logger.LogDebug(
                    $"Vanilla eyedrop: {info.Category} wall (index " +
                    $"{decorationIndex}), base color = " +
                    $"({rendered.r:F2}, {rendered.g:F2}, {rendered.b:F2})");
                return rendered;
            }

            // Try GetColor first — solid-color walls return a meaningful tint
            Color baseColor = dm.GetColor(DecorationUI.DecorationType.WALL, decorationIndex);

            // Solid-color walls have a non-white _BaseColor
            if (!IsEffectivelyWhite(baseColor))
            {
                RepaintedPlugin.Logger.LogDebug(
                    $"Vanilla eyedrop: solid-color wall (index {decorationIndex}), " +
                    $"_BaseColor = ({baseColor.r:F2}, {baseColor.g:F2}, {baseColor.b:F2})"
                );
                return baseColor;
            }

            Texture tex = dm.GetTexture(DecorationUI.DecorationType.WALL, decorationIndex);
            if (tex is Texture2D tex2d)
            {
                Color sampled = SampleTextureBottomBand(tex2d);
                if (!IsEffectivelyWhite(sampled) && !IsEffectivelyBlack(sampled))
                {
                    // Compensate for baked-in concrete texture
                    if (!cachedConcreteBottomAvg.HasValue)
                    {
                        Texture2D concreteBase = ModdedWallRegistry.OriginalBaseMap;
                        if (concreteBase != null)
                            cachedConcreteBottomAvg = SampleTextureBottomBand(concreteBase);
                    }
                    if (cachedConcreteBottomAvg.HasValue)
                    {
                        Color ca = cachedConcreteBottomAvg.Value;
                        sampled = new Color(
                            ca.r > 0.01f ? Mathf.Clamp01(sampled.r / ca.r) : sampled.r,
                            ca.g > 0.01f ? Mathf.Clamp01(sampled.g / ca.g) : sampled.g,
                            ca.b > 0.01f ? Mathf.Clamp01(sampled.b / ca.b) : sampled.b,
                            1f
                        );
                    }

                    RepaintedPlugin.Logger.LogDebug(
                        $"Vanilla eyedrop: half-color wall (index {decorationIndex}), " +
                        $"compensated color = ({sampled.r:F2}, {sampled.g:F2}, {sampled.b:F2})"
                    );
                    return sampled;
                }
            }

            // Could not extract a meaningful color (pattern-only wall, fish market, etc.)
            RepaintedPlugin.Logger.LogDebug(
                $"Vanilla eyedrop: no meaningful color for wall index {decorationIndex}"
            );
            return Color.white;
        }

        private Color SampleTextureBottomBand(Texture2D source)
        {
            Texture2D readable = TextureGenerator.GetReadableTexture(source);
            if (readable == null) return Color.white;

            int width = readable.width;
            int height = readable.height;

            int sampleHeight = Mathf.Max(1, Mathf.RoundToInt(height * BOTTOM_BAND_FRACTION));

            int marginX = Mathf.RoundToInt(width * HORIZONTAL_MARGIN_FRACTION);
            int sampleWidth = width - marginX * 2;
            if (sampleWidth <= 0) sampleWidth = width;

            // One bulk readback instead of ~150K marshalled GetPixel calls —
            // the per-call path caused a visible hitch on 2K textures.
            Color32[] pixels = readable.GetPixels32();

            Color sum = Color.black;
            int count = 0;

            for (int y = 0; y < sampleHeight; y++)
            {
                int rowBase = y * width;
                for (int x = marginX; x < marginX + sampleWidth; x += SAMPLE_PIXEL_STEP)
                {
                    Color32 c = pixels[rowBase + x];
                    sum.r += c.r / 255f;
                    sum.g += c.g / 255f;
                    sum.b += c.b / 255f;
                    count++;
                }
            }

            if (readable != source)
                UnityEngine.Object.Destroy(readable);

            return count > 0 ? sum / count : Color.white;
        }

        private static bool IsEffectivelyWhite(Color c)
        {
            return c.r > 0.95f && c.g > 0.95f && c.b > 0.95f;
        }

        private static bool IsEffectivelyBlack(Color c)
        {
            return c.r < 0.05f && c.g < 0.05f && c.b < 0.05f;
        }

        /// <summary>Toggle the color picker overlay.</summary>
        private void TogglePicker()
        {
            if (ColorPickerOverlay.Instance == null) return;

            if (ColorPickerOverlay.Instance.IsPickerOpen)
            {
                ColorPickerOverlay.Instance.ClosePicker();
            }
            else
            {
                ColorPickerOverlay.Instance.OpenPicker();
            }
        }
    }
}
