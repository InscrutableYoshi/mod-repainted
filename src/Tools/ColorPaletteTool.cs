using System;
using System.Collections.Generic;
using System.Reflection;
using Repainted.Data;
using Repainted.UI;
using Repainted.Util;
using UnityEngine;

namespace Repainted.Tools
{
    /// <summary>
    /// A standalone tool for colorizing modded walls. Completely decoupled
    /// from the vanilla paint system — does NOT dispatch to FloorClickable
    /// mouse events. Instead, performs its own raycast in Update(), identifies
    /// the FloorClickable under the crosshair, and directly manipulates its
    /// MaterialPropertyBlock + TileColorStore.
    ///
    /// Interactions:
    ///   Left click        — apply current picker color to the hovered modded wall
    ///   Shift + Left click — eyedropper: copy the tile's saved color
    ///   Right click        — open/close the color picker UI (unlocks cursor)
    ///
    /// Hover:
    ///   Managed entirely by this tool. Sets _Hover=1 on the hovered wall's
    ///   MaterialPropertyBlock. Only highlights walls that have a modded
    ///   wall decoration.
    /// </summary>
    public class ColorPaletteTool : HotkeyClickable
    {
        private const float RAYCAST_DISTANCE = 6f;
        private const float BOTTOM_BAND_FRACTION = 0.15f;
        private const float HORIZONTAL_MARGIN_FRACTION = 0.1f;
        private const int SAMPLE_PIXEL_STEP = 4;

        public static ColorPaletteTool Instance { get; private set; }

        // Palette model splotch — shows the current active color on the tool
        private MeshRenderer splotchRenderer;
        private MaterialPropertyBlock splotchBlock;

        // Currently hovered wall tile (managed by our own raycast, not RayShooter)
        private FloorClickable hoveredTile;
        private bool hoveredIsModded;

        // Cached average color of the concrete base texture's bottom band,
        // used to compensate vanilla half-color wall eyedropper samples
        private Color? cachedConcreteBottomAvg;

        // Reflection cache for FloorClickable private fields
        private static FieldInfo fcIdField;
        private static FieldInfo fcDecorationIndexField;
        private static FieldInfo fcMeshRendererField;
        private static FieldInfo fcDecorationType;
        private static FieldInfo fcMaterialPropertyBlock;

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
        }

        private void OnDestroy()
        {
            ModdedWallRegistry.OnColorChanged -= UpdateSplotchColor;
        }

        /// <summary>
        /// Called by HotKeyManagerPatch after instantiating the palette model.
        /// Caches the splotch MeshRenderer for runtime color updates.
        /// </summary>
        public void SetSplotchRenderer(MeshRenderer renderer)
        {
            splotchRenderer = renderer;
            splotchBlock = new MaterialPropertyBlock();

            // Initialize with the current active color
            UpdateSplotchColor(ModdedWallRegistry.CurrentColor);
        }

        /// <summary>
        /// Updates the splotch submesh color via MaterialPropertyBlock.
        /// Called whenever the active color changes (apply, eyedrop, picker).
        /// </summary>
        private void UpdateSplotchColor(Color color)
        {
            if (splotchRenderer == null || splotchBlock == null) return;

            splotchRenderer.GetPropertyBlock(splotchBlock);
            splotchBlock.SetColor("_BaseColor", color);
            splotchRenderer.SetPropertyBlock(splotchBlock);
        }

        private static void EnsureReflection()
        {
            if (fcIdField != null) return;

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            fcIdField = typeof(FloorClickable).GetField("id", flags);
            fcDecorationIndexField = typeof(FloorClickable).GetField("currentDecorationIndex", flags);
            fcMeshRendererField = typeof(FloorClickable).GetField("meshRenderer", flags);
            fcDecorationType = typeof(FloorClickable).GetField("decorationType", flags);
            fcMaterialPropertyBlock = typeof(FloorClickable).GetField("materialPropertyBlock", flags);
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

        private bool hoverHighlightActive;
        private bool lastShiftState;

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
            bool newIsModded = false;

            if (Physics.Raycast(cam.transform.position,
                cam.transform.TransformDirection(Vector3.forward),
                out RaycastHit hit, RAYCAST_DISTANCE, mask))
            {
                var fc = hit.collider.GetComponent<FloorClickable>();
                if (fc != null)
                {
                    var decType = (DecorationUI.DecorationType)fcDecorationType.GetValue(fc);
                    if (decType == DecorationUI.DecorationType.WALL)
                    {
                        int decorationIndex = (int)fcDecorationIndexField.GetValue(fc);
                        newHover = fc;
                        newIsModded = ModdedWallRegistry.IsModdedWall(decorationIndex);
                    }
                }
            }

            bool shiftHeld = Input.GetKey(KeyCode.LeftShift);

            // Should we show a highlight on the current target?
            // Modded walls: always. Vanilla walls: only when shift is held.
            bool shouldHighlight = newHover != null && (newIsModded || shiftHeld);

            // Transition hover target
            if (newHover != hoveredTile)
            {
                // End highlight on old tile
                if (hoveredTile != null && hoverHighlightActive)
                {
                    hoveredTile.OnMouseHoverEnded();
                    hoverHighlightActive = false;
                }

                hoveredTile = newHover;
                hoveredIsModded = newIsModded;
                lastShiftState = shiftHeld;

                // Start highlight if appropriate
                if (hoveredTile != null && shouldHighlight)
                {
                    hoveredTile.OnMouseHoverStarted();
                    hoverHighlightActive = true;
                }

                UpdateMenu();
            }
            else
            {
                bool needsMenuUpdate = false;

                // Same tile — but shift state or modded state may have changed
                if (newHover != null && newIsModded != hoveredIsModded)
                {
                    hoveredIsModded = newIsModded;
                    needsMenuUpdate = true;
                }

                // Shift state changed — update menu hints (affects both
                // modded walls and vanilla wall highlight toggle)
                if (shiftHeld != lastShiftState)
                {
                    lastShiftState = shiftHeld;
                    needsMenuUpdate = true;
                }

                // Toggle highlight on/off as shift is pressed/released on vanilla walls
                if (shouldHighlight && !hoverHighlightActive)
                {
                    hoveredTile.OnMouseHoverStarted();
                    hoverHighlightActive = true;
                    needsMenuUpdate = true;
                }
                else if (!shouldHighlight && hoverHighlightActive)
                {
                    hoveredTile.OnMouseHoverEnded();
                    hoverHighlightActive = false;
                    needsMenuUpdate = true;
                }

                if (needsMenuUpdate)
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
                hoveredIsModded = false;
            }
        }

        private void UpdateMenu()
        {
            if (!IsPicked) return;

            if (hoveredTile != null && hoveredIsModded)
            {
                // Hovering a modded wall — show all actions
                SingletonBehaviour<ButtonsWindow>.Instance.RepaintWithKeyCodes(
                    new Dictionary<KeyCode, (string, Action)>
                    {
                        { KeyCode.Mouse1, ("Color Picker", null) },
                        { KeyCode.LeftShift, ("+ Click: Eyedrop", null) },
                        { KeyCode.Mouse0, ("Apply Color", null) },
                    },
                    transform, false
                );
            }
            else if (hoveredTile != null && !hoveredIsModded)
            {
                // Hovering a vanilla wall — eyedropper available, no apply
                SingletonBehaviour<ButtonsWindow>.Instance.RepaintWithKeyCodes(
                    new Dictionary<KeyCode, (string, Action)>
                    {
                        { KeyCode.Mouse1, ("Color Picker", null) },
                        { KeyCode.LeftShift, ("+ Click: Eyedrop", null) },
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
            if (hoveredTile == null || !hoveredIsModded) return;

            int tileId = (int)fcIdField.GetValue(hoveredTile);
            int decorationIndex = (int)fcDecorationIndexField.GetValue(hoveredTile);
            var renderer = (MeshRenderer)fcMeshRendererField.GetValue(hoveredTile);
            var block = (MaterialPropertyBlock)fcMaterialPropertyBlock.GetValue(hoveredTile);

            if (!ModdedWallRegistry.TryGetModdedWall(decorationIndex, out var moddedWall))
                return;
            if (renderer == null || block == null) return;

            Color color = ModdedWallRegistry.CurrentColor;

            if (moddedWall.IsFullColor)
            {
                // FullColor doesn't use a tint texture — release any prior
                // tint-cache binding this tile might still hold.
                TextureGenerator.ReleaseWall(tileId);
                block.SetColor("_BaseColor", color);
            }
            else
            {
                bool isUpper = FloorResolver.IsUpperFloor(hoveredTile.transform);
                Texture2D tinted = TextureGenerator.GetAndBindTintedTexture(
                    tileId,
                    ModdedWallRegistry.OriginalBaseMap, color,
                    moddedWall.Type, moddedWall.Bands, isUpper
                );
                if (tinted != null)
                {
                    block.SetTexture("_BaseMap", tinted);
                    block.SetColor("_BaseColor", Color.white);
                }
            }

            renderer.SetPropertyBlock(block);

            TileColorStore.CommitModdedTile(tileId, color, moddedWall.Type);
            TileColorStore.PushHistory(color);
            ColorPickerOverlay.Instance?.RefreshHistorySwatches();

            RepaintedPlugin.Logger.LogDebug(
                $"Palette: applied color to tile {tileId} — " +
                $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})"
            );
        }

        private void Eyedropper()
        {
            if (hoveredTile == null) return;

            int tileId = (int)fcIdField.GetValue(hoveredTile);
            int decorationIndex = (int)fcDecorationIndexField.GetValue(hoveredTile);

            Color? picked = null;

            if (hoveredIsModded)
            {
                // Modded wall — read from our per-tile color store
                if (TileColorStore.TryGetTileColor(tileId, out var entry))
                {
                    picked = entry.ToColor();
                }
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
                    $"(decor {decorationIndex}, {(hoveredIsModded ? "modded" : "vanilla")}) — " +
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

            // DecorationManager uses 0-based id for GetColor/GetMaterial
            // decorationIndex from FloorClickable is already 0-based

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

            Color sum = Color.black;
            int count = 0;

            for (int y = 0; y < sampleHeight; y++)
            {
                for (int x = marginX; x < marginX + sampleWidth; x += SAMPLE_PIXEL_STEP)
                {
                    sum += readable.GetPixel(x, y);
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
