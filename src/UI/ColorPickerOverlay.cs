using Repainted.Data;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Repainted.UI
{
    /// <summary>
    /// Canvas-based color picker overlay for the ColorPaletteTool.
    /// Opened/closed exclusively via the tool's right-click or the X button.
    ///
    /// Uses Screen Space – Overlay canvas (renders on top of everything,
    /// unaffected by post-processing or IMGUI skin transparency).
    ///
    /// All UI is built programmatically with runtime-generated rounded-rect
    /// sprites (9-sliced for resolution independence). No external assets needed.
    ///
    /// When opened, unlocks the cursor via PlayerLook.LockCursor(false)
    /// so the player can interact with the picker, and re-locks on close.
    /// Closes on Escape or when a game window opens.
    ///
    /// Layout scales with screen height so it looks right at any resolution.
    /// Draggable by header area. Position persists across sessions.
    /// </summary>
    public class ColorPickerOverlay : MonoBehaviour
    {
        public static ColorPickerOverlay Instance { get; private set; }

        private bool cursorUnlocked = false;
        private Color currentColor;

        // HSV for the sliders
        private float hue = 0f;
        private float saturation = 0f;
        private float brightness = 0.8f;

        // Preset color palette (2 rows × 8 columns)
        private static readonly Color[] presetColors = new Color[]
        {
            // Row 1: neutrals + earth tones
            new Color(1.0f, 1.0f, 1.0f),       // white
            new Color(0.75f, 0.75f, 0.75f),     // light gray
            new Color(0.35f, 0.35f, 0.35f),     // dark gray
            new Color(0.10f, 0.10f, 0.10f),     // near-black
            new Color(0.93f, 0.90f, 0.85f),     // cream
            new Color(0.82f, 0.71f, 0.55f),     // tan
            new Color(0.55f, 0.37f, 0.24f),     // brown
            new Color(0.50f, 0.25f, 0.0f),      // dark brown

            // Row 2: vivid spectrum
            new Color(0.90f, 0.20f, 0.20f),     // red
            new Color(1.0f, 0.55f, 0.0f),       // orange
            new Color(1.0f, 0.80f, 0.20f),      // yellow
            new Color(0.30f, 0.75f, 0.30f),     // green
            new Color(0.20f, 0.80f, 0.60f),     // teal
            new Color(0.20f, 0.50f, 0.90f),     // blue
            new Color(0.55f, 0.25f, 0.75f),     // purple
            new Color(0.80f, 0.40f, 0.70f),     // pink
        };

        // Wheel view (HSV disc + brightness slider) vs classic sliders view
        private GameObject wheelView, slidersView;
        private RectTransform wheelRect;
        private RectTransform wheelKnob;
        private Slider wheelVSlider;
        private Text wheelVValue;
        private Text viewToggleLabel;
        private float wheelRadius;
        private bool wheelActive = true;

        // Canvas UI references
        private Canvas canvas;
        private GameObject panel;
        private Image previewImage;
        private Slider hueSlider, satSlider, briSlider;
        private Text hueValue, satValue, briValue;
        private RectTransform panelRect;

        // New feature refs
        private InputField hexInput;
        private Text[] hexShadows;        // black outline copies behind hex text
        private Text heartTextField;
        private GameObject[] historySwatches = new GameObject[8];
        private GameObject[] favoriteSwatches = new GameObject[8];
        private bool updatingHex = false; // prevent feedback loop

        // Dragging — covers the full header area (title + spacing above preview)
        private bool isDragging = false;
        private Vector2 dragOffset;
        private float dragZoneHeight;

        void Start()
        {
            Instance = this;

            // Restore persisted active color
            Color savedColor = TileColorStore.SavedActiveColor;
            ModdedWallRegistry.ApplyColor(savedColor);
            currentColor = savedColor;
            Color.RGBToHSV(currentColor, out hue, out saturation, out brightness);

            BuildUI();

            // Restore saved position and ensure it's on-screen
            Vector2 savedPos = TileColorStore.SavedPickerPosition;
            if (savedPos.x >= 0f && savedPos.y >= 0f)
            {
                panelRect.anchoredPosition = savedPos;
            }
            ClampToScreen();

            // Start hidden
            panel.SetActive(false);
        }

        void Update()
        {
            if (!IsPickerOpen) return;

            // If a game window opened (pause menu, settings, etc.),
            // close the picker but DON'T touch cursor state — the game
            // window is now managing the cursor.
            var uiManager = SingletonBehaviour<UIManager>.Instance;
            if (uiManager != null && !uiManager.AllWindowsClosed())
            {
                panel.SetActive(false);
                cursorUnlocked = false;
                SavePosition();
                return;
            }

            // Close on Escape — DON'T relock cursor. The game's own Escape
            // handler fires in the same frame and will open the pause menu,
            // which manages cursor state itself. If we call LockCursor(true)
            // here, it fights with the pause menu's unlock and the cursor
            // ends up invisible.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                panel.SetActive(false);
                cursorUnlocked = false;
                SavePosition();
                return;
            }

            // Deselect hex input if user clicks anywhere outside it
            if (hexInput != null && hexInput.isFocused && Input.GetMouseButtonDown(0))
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(
                        hexInput.GetComponent<RectTransform>(), Input.mousePosition, null))
                {
                    hexInput.DeactivateInputField();
                    EventSystem.current?.SetSelectedGameObject(null);
                }
            }

            // Handle dragging
            HandleDrag();
        }

        void OnDestroy()
        {
            if (cursorUnlocked)
                RelockCursor();
        }

        // ─── Public API (unchanged from IMGUI version) ────────────

        public bool IsPickerOpen => panel != null && panel.activeSelf;

        public void OpenPicker()
        {
            panel.SetActive(true);
            ClampToScreen();

            // Clear any lingering hex input focus from a previous session
            if (hexInput != null)
            {
                hexInput.DeactivateInputField();
                EventSystem.current?.SetSelectedGameObject(null);
            }

            UpdatePreview();
            UpdateSliders();
            UpdateHexField();
            UpdateHeartVisual();
            RefreshHistorySwatches();
            RefreshFavoriteSwatches();
            UnlockCursor();
        }

        public void ClosePicker()
        {
            panel.SetActive(false);
            RelockCursor();
            SavePosition();
        }

        /// <summary>
        /// Resync the overlay's internal color/HSV fields from
        /// TileColorStore.SavedActiveColor without marking prefs dirty.
        /// Called after a save slot finishes loading — Start() already ran
        /// back at the main menu (before any slot existed), so without this
        /// the overlay would open showing the hardcoded default color.
        /// </summary>
        public void ReapplySavedColor()
        {
            Color c = TileColorStore.SavedActiveColor;
            currentColor = c;
            Color.RGBToHSV(c, out hue, out saturation, out brightness);
            if (IsPickerOpen)
            {
                UpdatePreview();
                UpdateSliders();
                UpdateHexField();
                UpdateHeartVisual();
            }
        }

        public void SetColorFromExternal(Color color)
        {
            currentColor = color;
            Color.RGBToHSV(color, out hue, out saturation, out brightness);
            TileColorStore.SetActiveColor(color);
            if (IsPickerOpen)
            {
                UpdatePreview();
                UpdateSliders();
                UpdateHexField();
                UpdateHeartVisual();
            }
        }

        // ─── Cursor management ─────────────────────────────────────

        private void UnlockCursor()
        {
            if (!cursorUnlocked)
            {
                SingletonBehaviour<PlayerLook>.Instance?.LockCursor(false);
                cursorUnlocked = true;
            }
        }

        private void RelockCursor()
        {
            if (cursorUnlocked)
            {
                SingletonBehaviour<PlayerLook>.Instance?.LockCursor(true);
                cursorUnlocked = false;
            }
        }

        // ─── Persistence ───────────────────────────────────────────

        private void SavePosition()
        {
            if (panelRect != null)
            {
                TileColorStore.SetPickerPosition(panelRect.anchoredPosition);
                // Dirty flag stays set — flushed on game save via DataSerializerPatch
            }
        }

        // ─── Color logic ───────────────────────────────────────────

        private void SetColor(Color color)
        {
            currentColor = color;
            Color.RGBToHSV(color, out hue, out saturation, out brightness);
            ModdedWallRegistry.ApplyColor(color);
            TileColorStore.SetActiveColor(color);
            UpdatePreview();
            UpdateSliders();
            UpdateHexField();
            UpdateHeartVisual();
        }

        private void OnSliderChanged(float _)
        {
            hue = hueSlider.value;
            saturation = satSlider.value;
            brightness = briSlider.value;

            currentColor = Color.HSVToRGB(hue, saturation, brightness);
            ModdedWallRegistry.ApplyColor(currentColor);
            TileColorStore.SetActiveColor(currentColor);
            UpdatePreview();
            UpdateSliderLabels();
            UpdateHexField();
            UpdateHeartVisual();
        }

        private void UpdatePreview()
        {
            if (previewImage != null)
                previewImage.color = currentColor;
        }

        private void UpdateSliders()
        {
            // Temporarily remove listeners to avoid feedback loop
            hueSlider.onValueChanged.RemoveAllListeners();
            satSlider.onValueChanged.RemoveAllListeners();
            briSlider.onValueChanged.RemoveAllListeners();

            hueSlider.value = hue;
            satSlider.value = saturation;
            briSlider.value = brightness;

            hueSlider.onValueChanged.AddListener(OnSliderChanged);
            satSlider.onValueChanged.AddListener(OnSliderChanged);
            briSlider.onValueChanged.AddListener(OnSliderChanged);

            UpdateSliderLabels();
            UpdateWheelVisual();
        }

        private void UpdateSliderLabels()
        {
            hueValue.text = Mathf.RoundToInt(hue * 360f).ToString();
            satValue.text = Mathf.RoundToInt(saturation * 100f).ToString();
            briValue.text = Mathf.RoundToInt(brightness * 100f).ToString();
        }

        // ─── Hex field ─────────────────────────────────────────────

        private void UpdateHexField()
        {
            if (hexInput == null) return;
            updatingHex = true;
            string hex = "#" + ColorToHex(currentColor);
            hexInput.text = hex;
            // Sync shadow copies
            if (hexShadows != null)
            {
                foreach (var s in hexShadows)
                    if (s != null) s.text = hex;
            }
            updatingHex = false;
        }

        private void OnHexInput(string hex)
        {
            if (updatingHex) return;

            hex = hex.Trim().TrimStart('#');
            if (hex.Length >= 6)
            {
                hex = hex.Substring(0, 6);
                if (ColorUtility.TryParseHtmlString("#" + hex, out Color parsed))
                {
                    currentColor = parsed;
                    Color.RGBToHSV(parsed, out hue, out saturation, out brightness);
                    ModdedWallRegistry.ApplyColor(currentColor);
                    TileColorStore.SetActiveColor(currentColor);
                    UpdatePreview();
                    UpdateSliders();
                    UpdateHexField(); // re-format with # prefix and sync shadows
                    UpdateHeartVisual();
                    return;
                }
            }
            // Invalid input — revert to current color
            UpdateHexField();
        }

        /// <summary>Keep shadow copies in sync while user types.</summary>
        private void OnHexTyping(string text)
        {
            if (updatingHex || hexShadows == null) return;
            foreach (var s in hexShadows)
                if (s != null) s.text = text;
        }

        private static string ColorToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"{r:X2}{g:X2}{b:X2}";
        }

        // ─── Heart / Favorites ─────────────────────────────────────

        private void OnHeartClicked()
        {
            TileColorStore.ToggleFavorite(currentColor);
            UpdateHeartVisual();
            RefreshFavoriteSwatches();
        }

        private void UpdateHeartVisual()
        {
            if (heartTextField == null) return;
            bool isFav = TileColorStore.IsFavorited(currentColor);
            heartTextField.color = isFav
                ? new Color(0.95f, 0.30f, 0.35f)   // lit red heart
                : new Color(0.50f, 0.50f, 0.50f);   // dim gray heart
        }

        // ─── Dynamic swatch refresh ────────────────────────────────

        public void RefreshHistorySwatches()
        {
            var history = TileColorStore.HistoryColors;
            for (int i = 0; i < historySwatches.Length; i++)
            {
                if (historySwatches[i] == null) continue;
                Color c = i < history.Count ? history[i] : new Color(0.3f, 0.3f, 0.3f);
                // Update the inner fill color
                var fill = historySwatches[i].transform.Find("Fill");
                if (fill != null)
                    fill.GetComponent<Image>().color = c;
            }
        }

        private void RefreshFavoriteSwatches()
        {
            var favorites = TileColorStore.FavoriteColors;
            for (int i = 0; i < favoriteSwatches.Length; i++)
            {
                if (favoriteSwatches[i] == null) continue;
                Color c = i < favorites.Count ? favorites[i] : new Color(0.5f, 0.5f, 0.5f);
                var fill = favoriteSwatches[i].transform.Find("Fill");
                if (fill != null)
                    fill.GetComponent<Image>().color = c;
            }
        }

        // ─── Dragging ──────────────────────────────────────────────

        private void HandleDrag()
        {
            if (Input.GetMouseButtonDown(0))
            {
                // Check if click is in the header zone (above the color preview)
                Vector2 localPoint;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    panelRect, Input.mousePosition, null, out localPoint))
                {
                    float panelH = panelRect.sizeDelta.y;
                    // localPoint.y is relative to pivot (center), so top = +panelH/2
                    if (localPoint.y > panelH / 2f - dragZoneHeight &&
                        localPoint.y < panelH / 2f)
                    {
                        isDragging = true;
                        dragOffset = (Vector2)Input.mousePosition -
                            (Vector2)panelRect.position;
                    }
                }
            }

            if (isDragging)
            {
                if (Input.GetMouseButton(0))
                {
                    panelRect.position = (Vector2)Input.mousePosition - dragOffset;
                    ClampToScreen();
                }
                else
                {
                    isDragging = false;
                    ClampToScreen();
                }
            }
        }

        /// <summary>
        /// Keeps the panel fully within the screen bounds. Called on open,
        /// during/after drag, and on initial load — handles resolution changes,
        /// window resizes, and out-of-bounds saved positions.
        /// </summary>
        private void ClampToScreen()
        {
            if (panelRect == null) return;

            Vector2 size = panelRect.sizeDelta;
            float halfW = size.x / 2f;
            float halfH = size.y / 2f;

            // Screen bounds in anchored-position space (center pivot, center anchor)
            float minX = -Screen.width / 2f + halfW;
            float maxX =  Screen.width / 2f - halfW;
            float minY = -Screen.height / 2f + halfH;
            float maxY =  Screen.height / 2f - halfH;

            Vector2 pos = panelRect.anchoredPosition;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.y = Mathf.Clamp(pos.y, minY, maxY);
            panelRect.anchoredPosition = pos;
        }

        // ─── Rounded-rect sprite generation ────────────────────────

        /// <summary>
        /// Generate a white-filled rounded-rectangle sprite at runtime.
        /// Uses anti-aliased edges and 9-slice borders so it scales to any
        /// RectTransform size without distorting the corners.
        ///
        /// Apply the desired color via Image.color — the white fill gets
        /// multiplied by the tint, and transparent corner pixels stay clear.
        /// </summary>
        private static Sprite MakeRoundedSprite(int cornerRadius)
        {
            // Texture needs room for both corners plus a generous stretchable
            // center. A tiny center (2-4px) causes bilinear filtering to bleed
            // between the corner edge and the stretched center, creating smudgy
            // artifacts. 16px of center gives clean separation.
            int center = 16;
            int texSize = Mathf.Max(cornerRadius * 2 + center, 16);
            var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[texSize * texSize];
            float r = cornerRadius;

            for (int py = 0; py < texSize; py++)
            {
                for (int px = 0; px < texSize; px++)
                {
                    // How far are we into the corner region?
                    float dx = 0f, dy = 0f;
                    if (px < r) dx = r - px;
                    else if (px > texSize - 1 - r) dx = px - (texSize - 1 - r);
                    if (py < r) dy = r - py;
                    else if (py > texSize - 1 - r) dy = py - (texSize - 1 - r);

                    // Only pixels in the corner quadrants need distance checks
                    if (dx > 0f && dy > 0f)
                    {
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist > r + 0.5f)
                        {
                            pixels[py * texSize + px] = Color.clear;
                            continue;
                        }
                        if (dist > r - 0.5f)
                        {
                            // Anti-alias the edge over 1 pixel
                            float a = Mathf.Clamp01(r + 0.5f - dist);
                            pixels[py * texSize + px] = new Color(1f, 1f, 1f, a);
                            continue;
                        }
                    }

                    pixels[py * texSize + px] = Color.white;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            // 9-slice border = corner radius, so the stretchable zone starts
            // exactly where the curve ends and solid fill begins.
            float b = Mathf.Max(cornerRadius, 1);
            return Sprite.Create(
                tex,
                new Rect(0, 0, texSize, texSize),
                new Vector2(0.5f, 0.5f),
                100f, // pixelsPerUnit must match canvas referencePixelsPerUnit (100)
                0,
                SpriteMeshType.FullRect,
                new Vector4(b, b, b, b)
            );
        }

        // ─── UI Construction ───────────────────────────────────────

        private void BuildUI()
        {
            float scale = (Screen.height / 1080f) * 1.5f;

            // Generate rounded sprites scaled for this resolution.
            //
            // All radii are derived from two "visible" targets so every
            // curve at the same tier looks identical. Bordered elements get
            // a larger outer radius (visible + border inset) so the inner
            // curve lands exactly on the target.
            float overlapPx = 1f; // inner fill overlaps border by 1px to hide AA seam

            // Border widths
            float panelBorderW = 5f * scale;
            float smallBorderW = Mathf.Round(3f * scale);
            float smallInset   = Mathf.Max(Mathf.Round(smallBorderW) - overlapPx, 1f);

            // Visible corner radii (what you actually see on screen)
            int panelVisibleR = Mathf.RoundToInt(8f * scale);
            int smallVisibleR = Mathf.RoundToInt(8f * scale);

            // Outer radii = visible + border inset (so inner curve = visible)
            int panelOuterR = panelVisibleR + Mathf.RoundToInt(panelBorderW - overlapPx);
            int smallOuterR = smallVisibleR + Mathf.RoundToInt(smallInset);

            // Sprites
            Sprite panelSpr      = MakeRoundedSprite(panelOuterR);
            Sprite panelInnerSpr = MakeRoundedSprite(panelVisibleR);
            Sprite smallOuterSpr = MakeRoundedSprite(smallOuterR);
            Sprite smallInnerSpr = MakeRoundedSprite(smallVisibleR);
            Sprite trackSpr      = MakeRoundedSprite(Mathf.RoundToInt(3f * scale));

            // Aliases for readability
            Sprite boxSpr          = smallOuterSpr;
            Sprite swatchSpr       = smallOuterSpr;
            Sprite previewInnerSpr = smallInnerSpr;
            Sprite swatchInnerSpr  = smallInnerSpr;
            float swatchBorderW    = smallBorderW;
            float previewBorderW   = smallBorderW;

            // Thumb sprite — radius must fit within the handle dimensions
            float thumbW = 10f * scale;
            float thumbH = 7f * scale;
            int thumbR = Mathf.Max(Mathf.FloorToInt(Mathf.Min(thumbW, thumbH) / 4f), 1);
            Sprite thumbSpr = MakeRoundedSprite(thumbR);

            float winW = 260f * scale;
            float winH = 520f * scale;
            float pad = 10f * scale;
            float lineH = 20f * scale;
            float spacing = 6f * scale;
            float headerH = 28f * scale;

            // ── Canvas (Screen Space Overlay — renders on top of everything) ──

            var canvasGO = new GameObject("Repainted_PickerCanvas");
            DontDestroyOnLoad(canvasGO);
            canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;
            canvas.pixelPerfect = true;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            canvasGO.AddComponent<GraphicRaycaster>();

            // Ensure an EventSystem exists (the game likely has one already)
            if (EventSystem.current == null)
            {
                var esGO = new GameObject("Repainted_EventSystem");
                DontDestroyOnLoad(esGO);
                esGO.AddComponent<EventSystem>();
                esGO.AddComponent<StandaloneInputModule>();
            }

            // ── Main panel (rounded, with border) ──

            panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGO.transform, false);
            panelRect = panel.AddComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(winW, winH);

            // The panel Image itself is the border color
            var panelImg = panel.AddComponent<Image>();
            panelImg.sprite = panelSpr;
            panelImg.type = Image.Type.Sliced;
            panelImg.color = new Color(0.42f, 0.42f, 0.42f, 0.95f);

            // Inner background (overlaps border by 1px to hide AA seam)
            float panelInset = panelBorderW - overlapPx;
            var bgInner = new GameObject("Background");
            bgInner.transform.SetParent(panel.transform, false);
            var bgInnerRT = bgInner.AddComponent<RectTransform>();
            bgInnerRT.anchorMin = Vector2.zero;
            bgInnerRT.anchorMax = Vector2.one;
            bgInnerRT.offsetMin = new Vector2(panelInset, panelInset);
            bgInnerRT.offsetMax = new Vector2(-panelInset, -panelInset);
            var bgInnerImg = bgInner.AddComponent<Image>();
            bgInnerImg.sprite = panelInnerSpr;
            bgInnerImg.type = Image.Type.Sliced;
            bgInnerImg.color = new Color(0.22f, 0.22f, 0.22f, 0.92f);

            // Default position: middle-right of screen
            float defaultX = Screen.width / 2f - winW / 2f - 20f * scale;
            panelRect.anchoredPosition = new Vector2(defaultX, 0);

            float contentW = winW - pad * 2f;
            float yPos = winH / 2f - pad; // start from top (relative to center pivot)

            // ── Header: title text + close button ──
            // (No separate title bar background — the panel's rounded corners
            //  provide the top shape, and the entire header is the drag zone.)

            var titleText = CreateText(panel.transform, "Wall Color",
                Mathf.RoundToInt(14f * scale), TextAnchor.MiddleCenter, Color.white);
            SetAnchored(titleText, 0, yPos - headerH / 2f, contentW, headerH);

            // Close button (top-right corner)
            float closeBtnSize = 22f * scale;
            var closeBtn = CreateRoundedButton(panel.transform, "X",
                Mathf.RoundToInt(12f * scale), closeBtnSize, closeBtnSize,
                new Color(0.35f, 0.35f, 0.35f), smallInnerSpr);
            SetAnchored(closeBtn, winW / 2f - pad - closeBtnSize / 2f,
                yPos - headerH / 2f, closeBtnSize, closeBtnSize);
            closeBtn.GetComponent<Button>().onClick.AddListener(() => ClosePicker());

            yPos -= headerH + spacing;

            // Drag zone covers everything from panel top to here
            dragZoneHeight = pad + headerH + spacing;

            // ── Color preview + heart button row ──
            // The hex code is overlaid directly on the preview as outlined text.

            float previewH = 28f * scale;
            float pvInset = previewBorderW - overlapPx;
            float heartSize = previewH;
            float heartGap = 6f * scale;
            float previewW = contentW - heartSize - heartGap;

            // Outer border
            var previewBorder = CreateRoundedImage(panel.transform, previewW, previewH,
                new Color(0.12f, 0.12f, 0.12f), boxSpr);
            SetAnchored(previewBorder, -heartSize / 2f - heartGap / 2f,
                yPos - previewH / 2f, previewW, previewH);

            // Inner fill (the actual color preview)
            var previewFill = new GameObject("PreviewFill");
            previewFill.transform.SetParent(previewBorder.transform, false);
            var pvFillRT = previewFill.AddComponent<RectTransform>();
            pvFillRT.anchorMin = Vector2.zero;
            pvFillRT.anchorMax = Vector2.one;
            pvFillRT.offsetMin = new Vector2(pvInset, pvInset);
            pvFillRT.offsetMax = new Vector2(-pvInset, -pvInset);
            previewImage = previewFill.AddComponent<Image>();
            previewImage.sprite = previewInnerSpr;
            previewImage.type = Image.Type.Sliced;
            previewImage.color = currentColor;

            // Hex overlay: 4 shadow copies (offset N/S/E/W) + 1 white on top.
            // This gives a thick black outline that's readable on any color.
            // The InputField sits on top with transparent visuals — its text IS
            // the white foreground layer, and the shadows sit behind it.
            int hexFontSize = Mathf.RoundToInt(12f * scale);
            float outlineOff = Mathf.Max(1f, Mathf.Round(1.5f * scale));

            // Shadow layers (black text offset in 4 directions)
            Vector2[] offsets = new Vector2[]
            {
                new Vector2(-outlineOff, 0),
                new Vector2(outlineOff, 0),
                new Vector2(0, -outlineOff),
                new Vector2(0, outlineOff),
            };
            hexShadows = new Text[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
            {
                var shadowGO = new GameObject("HexShadow");
                shadowGO.transform.SetParent(previewFill.transform, false);
                var sRT = shadowGO.AddComponent<RectTransform>();
                sRT.anchorMin = Vector2.zero;
                sRT.anchorMax = Vector2.one;
                sRT.offsetMin = Vector2.zero;
                sRT.offsetMax = Vector2.zero;
                sRT.anchoredPosition = offsets[i];
                var sTxt = shadowGO.AddComponent<Text>();
                sTxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                sTxt.fontSize = hexFontSize;
                sTxt.fontStyle = FontStyle.Bold;
                sTxt.alignment = TextAnchor.MiddleCenter;
                sTxt.color = Color.black;
                sTxt.raycastTarget = false;
                sTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
                hexShadows[i] = sTxt;
            }

            // InputField (white foreground text, transparent background)
            var inputGO = new GameObject("HexInput");
            inputGO.transform.SetParent(previewFill.transform, false);
            var inputRT = inputGO.AddComponent<RectTransform>();
            inputRT.anchorMin = Vector2.zero;
            inputRT.anchorMax = Vector2.one;
            inputRT.offsetMin = Vector2.zero;
            inputRT.offsetMax = Vector2.zero;

            var inputText = inputGO.AddComponent<Text>();
            inputText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            inputText.fontSize = hexFontSize;
            inputText.fontStyle = FontStyle.Bold;
            inputText.color = Color.white;
            inputText.alignment = TextAnchor.MiddleCenter;
            inputText.supportRichText = false;
            inputText.horizontalOverflow = HorizontalWrapMode.Overflow;

            hexInput = inputGO.AddComponent<InputField>();
            hexInput.textComponent = inputText;
            hexInput.characterLimit = 7; // allow # prefix
            hexInput.onEndEdit.AddListener(OnHexInput);
            hexInput.onValueChanged.AddListener(OnHexTyping);

            UpdateHexField();

            // Heart toggle button — large heart glyph with invisible background.
            // Uses EventTrigger for hover scale effect instead of Button color tint.
            float heartBtnSize = previewH * 1.4f;
            var heartBtn = new GameObject("HeartButton");
            heartBtn.transform.SetParent(panel.transform, false);
            var heartBtnRT = heartBtn.AddComponent<RectTransform>();
            SetAnchored(heartBtn,
                contentW / 2f - heartSize / 2f,
                yPos - previewH / 2f, heartBtnSize, heartBtnSize);

            // Invisible image as click target
            var heartBtnImg = heartBtn.AddComponent<Image>();
            heartBtnImg.color = Color.clear;

            var heartButton = heartBtn.AddComponent<Button>();
            heartButton.transition = Selectable.Transition.None;
            heartButton.onClick.AddListener(OnHeartClicked);

            // Heart glyph as child text
            var heartTextGO = CreateText(heartBtn.transform, "\u2665",
                Mathf.RoundToInt(24f * scale), TextAnchor.MiddleCenter, Color.gray);
            var heartTextRT = heartTextGO.GetComponent<RectTransform>();
            heartTextRT.anchorMin = Vector2.zero;
            heartTextRT.anchorMax = Vector2.one;
            heartTextRT.sizeDelta = Vector2.zero;
            heartTextRT.anchoredPosition = Vector2.zero;
            heartTextField = heartTextGO.GetComponent<Text>();

            // Hover scale via EventTrigger
            var heartTrigger = heartBtn.AddComponent<EventTrigger>();
            var pointerEnter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            pointerEnter.callback.AddListener((_) =>
                heartBtn.transform.localScale = new Vector3(1.15f, 1.15f, 1f));
            heartTrigger.triggers.Add(pointerEnter);
            var pointerExit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            pointerExit.callback.AddListener((_) =>
                heartBtn.transform.localScale = Vector3.one);
            heartTrigger.triggers.Add(pointerExit);

            UpdateHeartVisual();

            yPos -= previewH + spacing;

            // ── Shared swatch dimensions ──

            int cols = 8;
            float gap = 3f * scale;
            float btnSize = (contentW - (cols - 1) * gap) / cols;
            float snappedSize = Mathf.Round(btnSize);

            // ── "Favorites" label + row (at top for quick access) ──

            var favLabel = CreateText(panel.transform, "Favorites",
                Mathf.RoundToInt(11f * scale), TextAnchor.MiddleLeft, Color.white);
            SetAnchored(favLabel, 0, yPos - lineH / 2f, contentW, lineH);
            yPos -= lineH + 2f * scale;

            var favorites = TileColorStore.FavoriteColors;
            for (int i = 0; i < 8; i++)
            {
                float bx = Mathf.Round(-contentW / 2f + i * (btnSize + gap) + btnSize / 2f);
                float by = Mathf.Round(yPos - btnSize / 2f);
                Color c = i < favorites.Count ? favorites[i] : new Color(0.5f, 0.5f, 0.5f);

                var swatch = CreateRoundedButton(panel.transform, "",
                    10, snappedSize, snappedSize, c, swatchSpr,
                    swatchBorderW, new Color(0.12f, 0.12f, 0.12f),
                    swatchInnerSpr);
                SetAnchored(swatch, bx, by, snappedSize, snappedSize);
                favoriteSwatches[i] = swatch;

                int idx = i;
                swatch.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (idx < TileColorStore.FavoriteColors.Count)
                        SetColor(TileColorStore.FavoriteColors[idx]);
                });
            }

            yPos -= (btnSize + gap) + spacing;

            // ── "Presets" label ──

            var presetsLabel = CreateText(panel.transform, "Presets",
                Mathf.RoundToInt(11f * scale), TextAnchor.MiddleLeft, Color.white);
            SetAnchored(presetsLabel, 0, yPos - lineH / 2f, contentW, lineH);
            yPos -= lineH + 2f * scale;

            // ── Preset grid (8 columns × 2 rows) ──

            for (int i = 0; i < presetColors.Length; i++)
            {
                int col = i % cols;
                int row = i / cols;

                float bx = Mathf.Round(-contentW / 2f + col * (btnSize + gap) + btnSize / 2f);
                float by = Mathf.Round(yPos - row * (btnSize + gap) - btnSize / 2f);

                var swatch = CreateRoundedButton(panel.transform, "",
                    10, snappedSize, snappedSize, presetColors[i], swatchSpr,
                    swatchBorderW, new Color(0.12f, 0.12f, 0.12f),
                    swatchInnerSpr);
                SetAnchored(swatch, bx, by, snappedSize, snappedSize);

                int colorIndex = i;
                swatch.GetComponent<Button>().onClick.AddListener(
                    () => SetColor(presetColors[colorIndex]));
            }

            int presetRows = (presetColors.Length + cols - 1) / cols;
            yPos -= presetRows * (btnSize + gap) + spacing;

            // ── "History" label + row ──

            var historyLabel = CreateText(panel.transform, "History",
                Mathf.RoundToInt(11f * scale), TextAnchor.MiddleLeft, Color.white);
            SetAnchored(historyLabel, 0, yPos - lineH / 2f, contentW, lineH);
            yPos -= lineH + 2f * scale;

            var history = TileColorStore.HistoryColors;
            for (int i = 0; i < 8; i++)
            {
                float bx = Mathf.Round(-contentW / 2f + i * (btnSize + gap) + btnSize / 2f);
                float by = Mathf.Round(yPos - btnSize / 2f);
                Color c = i < history.Count ? history[i] : new Color(0.3f, 0.3f, 0.3f);

                var swatch = CreateRoundedButton(panel.transform, "",
                    10, snappedSize, snappedSize, c, swatchSpr,
                    swatchBorderW, new Color(0.12f, 0.12f, 0.12f),
                    swatchInnerSpr);
                SetAnchored(swatch, bx, by, snappedSize, snappedSize);
                historySwatches[i] = swatch;

                int idx = i;
                swatch.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (idx < TileColorStore.HistoryColors.Count)
                        SetColor(TileColorStore.HistoryColors[idx]);
                });
            }

            yPos -= (btnSize + gap) + spacing;

            // ── "Custom Color" label ──

            var customLabel = CreateText(panel.transform, "Custom Color",
                Mathf.RoundToInt(11f * scale), TextAnchor.MiddleLeft, Color.white);
            SetAnchored(customLabel, -40f * scale, yPos - lineH / 2f,
                contentW - 80f * scale, lineH);

            // View toggle button (wheel <-> sliders), right of the label
            float toggleW = 60f * scale;
            float toggleH = 18f * scale;
            var toggleBtn = CreateRoundedButton(panel.transform, "Sliders",
                Mathf.RoundToInt(9f * scale), toggleW, toggleH,
                new Color(0.35f, 0.35f, 0.35f), smallInnerSpr);
            SetAnchored(toggleBtn, contentW / 2f - toggleW / 2f,
                yPos - lineH / 2f, toggleW, toggleH);
            viewToggleLabel = toggleBtn.GetComponentInChildren<Text>();
            toggleBtn.GetComponent<Button>().onClick.AddListener(ToggleView);

            yPos -= lineH + 4f * scale;

            // Both views live in zero-size containers centered on the panel,
            // so child coordinates stay in the same space SetAnchored expects.
            slidersView = new GameObject("SlidersView");
            slidersView.transform.SetParent(panel.transform, false);
            var svRT = slidersView.AddComponent<RectTransform>();
            svRT.anchoredPosition = Vector2.zero;
            svRT.sizeDelta = Vector2.zero;

            wheelView = new GameObject("WheelView");
            wheelView.transform.SetParent(panel.transform, false);
            var wvRT = wheelView.AddComponent<RectTransform>();
            wvRT.anchoredPosition = Vector2.zero;
            wvRT.sizeDelta = Vector2.zero;

            // ── HSV Sliders (classic view) ──

            float labelW = 16f * scale;
            float valueW = 36f * scale;
            float sliderW = contentW - labelW - valueW - 12f * scale;
            float rowH = 28f * scale;

            float ySliders = yPos;
            hueSlider = CreateSliderRow(slidersView.transform, "H", ref ySliders,
                contentW, labelW, sliderW, valueW, rowH, scale,
                trackSpr, thumbSpr, thumbW, thumbH, out hueValue);
            satSlider = CreateSliderRow(slidersView.transform, "S", ref ySliders,
                contentW, labelW, sliderW, valueW, rowH, scale,
                trackSpr, thumbSpr, thumbW, thumbH, out satValue);
            briSlider = CreateSliderRow(slidersView.transform, "V", ref ySliders,
                contentW, labelW, sliderW, valueW, rowH, scale,
                trackSpr, thumbSpr, thumbW, thumbH, out briValue);

            // Wire up slider events
            hueSlider.onValueChanged.AddListener(OnSliderChanged);
            satSlider.onValueChanged.AddListener(OnSliderChanged);
            briSlider.onValueChanged.AddListener(OnSliderChanged);

            // ── HSV Wheel (default view) ──

            float wheelD = 130f * scale;
            var wheelGO = new GameObject("ColorWheel");
            wheelGO.transform.SetParent(wheelView.transform, false);
            wheelRect = wheelGO.AddComponent<RectTransform>();
            var wheelImg = wheelGO.AddComponent<Image>();
            wheelImg.sprite = MakeWheelSprite(256);
            wheelImg.preserveAspect = true;
            SetAnchored(wheelGO, 0, yPos - wheelD / 2f, wheelD, wheelD);
            wheelRadius = wheelD / 2f;
            var wheelHandler = wheelGO.AddComponent<WheelInputHandler>();
            wheelHandler.owner = this;

            // Selector knob: dark ring with white core, non-interactive
            var knobOuter = CreateRoundedImage(wheelGO.transform,
                14f * scale, 14f * scale, new Color(0.1f, 0.1f, 0.1f, 0.9f),
                MakeCircleSprite(16));
            knobOuter.name = "WheelKnob";
            knobOuter.GetComponent<Image>().raycastTarget = false;
            wheelKnob = knobOuter.GetComponent<RectTransform>();
            var knobInner = CreateRoundedImage(knobOuter.transform,
                9f * scale, 9f * scale, Color.white, MakeCircleSprite(16));
            knobInner.GetComponent<Image>().raycastTarget = false;

            // Brightness slider under the wheel
            float yWheelSlider = yPos - wheelD - 6f * scale;
            wheelVSlider = CreateSliderRow(wheelView.transform, "V", ref yWheelSlider,
                contentW, labelW, sliderW, valueW, rowH, scale,
                trackSpr, thumbSpr, thumbW, thumbH, out wheelVValue);
            wheelVSlider.onValueChanged.AddListener(OnWheelVChanged);

            // Initial view from config
            wheelActive = RepaintedPlugin.CfgPickerStyle == null ||
                RepaintedPlugin.CfgPickerStyle.Value ==
                    RepaintedPlugin.PickerStyleKind.Wheel;
            ApplyPickerView();

            UpdateSliders();
            UpdateHexField();
        }

        // ─── Wheel view logic ──────────────────────────────────────

        /// <summary>Routes uGUI pointer events from the wheel Image.</summary>
        private class WheelInputHandler : MonoBehaviour,
            IPointerDownHandler, IDragHandler
        {
            public ColorPickerOverlay owner;
            public void OnPointerDown(PointerEventData e) => owner.HandleWheelPointer(e);
            public void OnDrag(PointerEventData e) => owner.HandleWheelPointer(e);
        }

        private void HandleWheelPointer(PointerEventData e)
        {
            if (wheelRect == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    wheelRect, e.position, e.pressEventCamera, out Vector2 local))
                return;

            float dist = local.magnitude;
            saturation = Mathf.Clamp01(dist / wheelRadius);
            float h = Mathf.Atan2(local.y, local.x) / (2f * Mathf.PI);
            if (h < 0f) h += 1f;
            hue = h;
            brightness = wheelVSlider != null ? wheelVSlider.value : brightness;

            currentColor = Color.HSVToRGB(hue, saturation, brightness);
            ModdedWallRegistry.ApplyColor(currentColor);
            TileColorStore.SetActiveColor(currentColor);
            UpdatePreview();
            UpdateSliders();   // also repositions the knob via UpdateWheelVisual
            UpdateHexField();
            UpdateHeartVisual();
        }

        private void OnWheelVChanged(float _)
        {
            brightness = wheelVSlider.value;
            currentColor = Color.HSVToRGB(hue, saturation, brightness);
            ModdedWallRegistry.ApplyColor(currentColor);
            TileColorStore.SetActiveColor(currentColor);
            UpdatePreview();
            UpdateSliders();
            UpdateHexField();
            UpdateHeartVisual();
        }

        private void ToggleView()
        {
            wheelActive = !wheelActive;
            ApplyPickerView();
            if (RepaintedPlugin.CfgPickerStyle != null)
            {
                RepaintedPlugin.CfgPickerStyle.Value = wheelActive
                    ? RepaintedPlugin.PickerStyleKind.Wheel
                    : RepaintedPlugin.PickerStyleKind.Sliders;
            }
        }

        private void ApplyPickerView()
        {
            if (wheelView != null) wheelView.SetActive(wheelActive);
            if (slidersView != null) slidersView.SetActive(!wheelActive);
            if (viewToggleLabel != null)
                viewToggleLabel.text = wheelActive ? "Sliders" : "Wheel";
        }

        /// <summary>Sync wheel knob + brightness slider to current HSV.</summary>
        private void UpdateWheelVisual()
        {
            if (wheelKnob != null)
            {
                float ang = hue * 2f * Mathf.PI;
                float dist = saturation * wheelRadius;
                wheelKnob.anchoredPosition = new Vector2(
                    Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist);
            }
            if (wheelVSlider != null)
            {
                wheelVSlider.onValueChanged.RemoveAllListeners();
                wheelVSlider.value = brightness;
                wheelVSlider.onValueChanged.AddListener(OnWheelVChanged);
                if (wheelVValue != null)
                    wheelVValue.text = Mathf.RoundToInt(brightness * 100f).ToString();
            }
        }

        /// <summary>
        /// Generate the HSV disc texture: hue around the circle, saturation
        /// from center to rim, full value (brightness comes from the slider).
        /// </summary>
        private static Sprite MakeWheelSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var pixels = new Color[size * size];
            float c = (size - 1) / 2f;
            float radius = c - 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c;
                    float dy = y - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radius + 0.5f)
                    {
                        pixels[y * size + x] = Color.clear;
                        continue;
                    }
                    float h = Mathf.Atan2(dy, dx) / (2f * Mathf.PI);
                    if (h < 0f) h += 1f;
                    float sat = Mathf.Clamp01(dist / radius);
                    Color col = Color.HSVToRGB(h, sat, 1f);
                    // Anti-alias the rim over one pixel
                    col.a = Mathf.Clamp01(radius + 0.5f - dist);
                    pixels[y * size + x] = col;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Simple anti-aliased filled circle sprite.</summary>
        private static Sprite MakeCircleSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color[size * size];
            float c = (size - 1) / 2f;
            float radius = c - 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius + 0.5f - dist);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f);
        }

        // ─── UI Helper methods ─────────────────────────────────────

        /// <summary>Create a rounded Image element (9-sliced sprite, tinted).</summary>
        private GameObject CreateRoundedImage(Transform parent, float w, float h,
            Color color, Sprite sprite)
        {
            var go = new GameObject("RoundedImage");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(w, h);

            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
            img.color = color;

            return go;
        }

        /// <summary>Create a plain Text element.</summary>
        private GameObject CreateText(Transform parent, string text, int fontSize,
            TextAnchor alignment, Color color)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var txt = go.AddComponent<Text>();
            txt.text = text;
            txt.fontSize = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = alignment;
            txt.color = color;
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;

            return go;
        }

        /// <summary>
        /// Create a clickable Button with a rounded sprite background.
        /// If borderWidth > 0, the outer Image acts as the border and a
        /// child fill Image (inset by borderWidth) shows the actual bgColor.
        /// </summary>
        private GameObject CreateRoundedButton(Transform parent, string label,
            int fontSize, float w, float h, Color bgColor, Sprite sprite,
            float borderWidth = 0f, Color borderColor = default,
            Sprite innerSprite = null)
        {
            var go = new GameObject("Button");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(w, h);

            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Sliced;

            Transform contentParent = go.transform;

            if (borderWidth > 0f)
            {
                // Snap border to whole pixels, overlap fill by 1px to hide AA seam
                float b = Mathf.Round(borderWidth);
                float inset = Mathf.Max(b - 1f, 1f);

                // Outer image = border color (rounded)
                img.color = borderColor;

                // Inner fill with concentric rounded corners
                var fillGO = new GameObject("Fill");
                fillGO.transform.SetParent(go.transform, false);
                var fillRT = fillGO.AddComponent<RectTransform>();
                fillRT.anchorMin = Vector2.zero;
                fillRT.anchorMax = Vector2.one;
                fillRT.offsetMin = new Vector2(inset, inset);
                fillRT.offsetMax = new Vector2(-inset, -inset);
                var fillImg = fillGO.AddComponent<Image>();
                fillImg.sprite = innerSprite;
                fillImg.type = Image.Type.Sliced;
                fillImg.color = bgColor;

                contentParent = fillGO.transform;
            }
            else
            {
                img.color = bgColor;
            }

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            btn.colors = colors;
            btn.targetGraphic = img;

            if (!string.IsNullOrEmpty(label))
            {
                var textGO = CreateText(contentParent, label, fontSize,
                    TextAnchor.MiddleCenter, Color.white);
                var textRT = textGO.GetComponent<RectTransform>();
                textRT.anchorMin = Vector2.zero;
                textRT.anchorMax = Vector2.one;
                textRT.sizeDelta = Vector2.zero;
            }

            return go;
        }

        /// <summary>Create one HSV slider row (label + slider + value readout).</summary>
        private Slider CreateSliderRow(Transform parent, string label, ref float yPos,
            float contentW, float labelW, float sliderW, float valueW,
            float rowH, float scale, Sprite trackSpr, Sprite thumbSpr,
            float thumbW, float thumbH, out Text valueText)
        {
            float midY = yPos - rowH / 2f;

            // Label (e.g. "H")
            var lbl = CreateText(parent, label, Mathf.RoundToInt(12f * scale),
                TextAnchor.MiddleLeft, Color.white);
            SetAnchored(lbl, -contentW / 2f + labelW / 2f, midY, labelW, rowH);

            // Slider root
            var sliderGO = new GameObject("Slider_" + label);
            sliderGO.transform.SetParent(parent, false);
            sliderGO.AddComponent<RectTransform>();
            float sliderX = -contentW / 2f + labelW + 6f * scale + sliderW / 2f;
            SetAnchored(sliderGO, sliderX, midY, sliderW, rowH);

            // Background track (rounded)
            float trackH = 6f * scale;
            var bgGO = CreateRoundedImage(sliderGO.transform, sliderW, trackH,
                new Color(0.15f, 0.15f, 0.15f), trackSpr);
            var bgRT = bgGO.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0, 0.5f);
            bgRT.anchorMax = new Vector2(1, 0.5f);
            bgRT.sizeDelta = new Vector2(0, trackH);
            bgRT.anchoredPosition = Vector2.zero;

            // Fill area
            var fillAreaGO = new GameObject("FillArea");
            fillAreaGO.transform.SetParent(sliderGO.transform, false);
            var fillAreaRT = fillAreaGO.AddComponent<RectTransform>();
            fillAreaRT.anchorMin = new Vector2(0, 0.5f);
            fillAreaRT.anchorMax = new Vector2(1, 0.5f);
            fillAreaRT.sizeDelta = new Vector2(-10f * scale, trackH);
            fillAreaRT.anchoredPosition = Vector2.zero;

            var fillGO = CreateRoundedImage(fillAreaGO.transform, 0, trackH,
                new Color(0.4f, 0.6f, 0.9f), trackSpr);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = new Vector2(0, 1);
            fillRT.sizeDelta = Vector2.zero;

            // Handle slide area — height matches desired thumb height so the
            // Slider component (which stretches the handle to fill this area
            // vertically via anchors 0→1) produces the right visual size.
            float tW = Mathf.Round(thumbW);
            float tH = Mathf.Round(thumbH);
            var handleAreaGO = new GameObject("HandleArea");
            handleAreaGO.transform.SetParent(sliderGO.transform, false);
            var handleAreaRT = handleAreaGO.AddComponent<RectTransform>();
            handleAreaRT.anchorMin = new Vector2(0, 0.5f);
            handleAreaRT.anchorMax = new Vector2(1, 0.5f);
            handleAreaRT.sizeDelta = new Vector2(-10f * scale, tH);
            handleAreaRT.anchoredPosition = Vector2.zero;

            // Handle thumb — sizeDelta.y = 0 so it matches handleArea height exactly
            var handleGO = CreateRoundedImage(handleAreaGO.transform, tW, tH,
                new Color(0.82f, 0.82f, 0.82f), thumbSpr);
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.sizeDelta = new Vector2(tW, 0);

            // Slider component
            var slider = sliderGO.AddComponent<Slider>();
            slider.targetGraphic = handleGO.GetComponent<Image>();
            slider.fillRect = fillRT;
            slider.handleRect = handleRT;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;

            // Value label
            var valGO = CreateText(parent, "0", Mathf.RoundToInt(12f * scale),
                TextAnchor.MiddleRight, Color.white);
            SetAnchored(valGO, contentW / 2f - valueW / 2f, midY, valueW, rowH);
            valueText = valGO.GetComponent<Text>();

            yPos -= rowH;
            return slider;
        }

        /// <summary>Set a RectTransform's anchored position and size.</summary>
        private static void SetAnchored(GameObject go, float x, float y, float w, float h)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }
    }
}
