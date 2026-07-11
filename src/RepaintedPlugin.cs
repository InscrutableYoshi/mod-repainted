using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Repainted.Data;
using Repainted.Patches;
using Repainted.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Repainted
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class RepaintedPlugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        /// <summary>
        /// The loaded palette prefab from the AssetBundle. HotKeyManagerPatch
        /// reads this to swap the cloned PaintRoller mesh for the palette model.
        /// </summary>
        internal static GameObject PalettePrefab;

        /// <summary>
        /// The hotbar icon sprite for the palette tool, loaded from an
        /// embedded PNG resource. HotKeyManagerPatch uses this to replace
        /// the cloned megaphone icon on our slot.
        /// </summary>
        internal static Sprite PaletteIconSprite;

        // ─── Config (editable in-game via ConfigurationManager) ───

        /// <summary>Picker layout styles for the color picker overlay.</summary>
        public enum PickerStyleKind { Wheel, Sliders }

        /// <summary>How the palette recolors patterned walls (brick, tile,
        /// wallpaper): Replace = flat color over the pattern; Tint = keep
        /// the pattern, shifted to your color.</summary>
        public enum PatternedRecolorKind { Replace, Tint }

        internal static ConfigEntry<PickerStyleKind> CfgPickerStyle;
        internal static ConfigEntry<float> CfgWallPrice;
        internal static ConfigEntry<PatternedRecolorKind> CfgPatternedMode;

        // ─── Palette model pose (baked from the dev tuner, 2026-07-10) ──
        // Release builds use these constants; DEV builds expose them as
        // live config (F1 "PaletteModel") + the numpad tuner.
#if REPAINTED_DEV
        internal static ConfigEntry<float> CfgPaletteRotX;
        internal static ConfigEntry<float> CfgPaletteRotY;
        internal static ConfigEntry<float> CfgPaletteRotZ;
        internal static ConfigEntry<float> CfgPaletteScale;
        internal static ConfigEntry<float> CfgPaletteOffX;
        internal static ConfigEntry<float> CfgPaletteOffY;
        internal static ConfigEntry<float> CfgPaletteOffZ;
        internal static ConfigEntry<float> CfgWoodSmoothness;
#endif

        internal static Vector3 PaletteRotation =>
#if REPAINTED_DEV
            new Vector3(CfgPaletteRotX?.Value ?? -80f,
                        CfgPaletteRotY?.Value ?? 60f,
                        CfgPaletteRotZ?.Value ?? 60f);
#else
            new Vector3(-80f, 60f, 60f);
#endif

        internal static Vector3 PaletteOffset =>
#if REPAINTED_DEV
            new Vector3(CfgPaletteOffX?.Value ?? 0.03f,
                        CfgPaletteOffY?.Value ?? 0.02f,
                        CfgPaletteOffZ?.Value ?? 0.03f);
#else
            new Vector3(0.03f, 0.02f, 0.03f);
#endif

        internal static float PaletteScale =>
#if REPAINTED_DEV
            CfgPaletteScale?.Value ?? 1.4f;
#else
            1.4f;
#endif

        internal static float WoodSmoothness =>
#if REPAINTED_DEV
            CfgWoodSmoothness?.Value ?? 0.6f;
#else
            0.6f;
#endif

        /// <summary>
        /// Preview sprites shown in the Repainted! tab, keyed by WallType.
        /// Pre-baked rainbow-over-Wall1 so every entry visually advertises
        /// where its colored bands sit. RepaintedTabPatch swaps these onto
        /// each cloned DecorationUI card.
        /// </summary>
        internal static readonly Dictionary<Data.ModdedWallRegistry.WallType, Sprite>
            WallPreviewSprites = new Dictionary<Data.ModdedWallRegistry.WallType, Sprite>();

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loading...");

            CfgPickerStyle = Config.Bind("UI", "PickerStyle", PickerStyleKind.Wheel,
                "Custom color control style in the picker: HSV color wheel with " +
                "a brightness slider, or the classic H/S/V sliders.");
            CfgWallPrice = Config.Bind("Shop", "WallPrice", 1000f,
                "Shop price for each Repainted brush wall entry. Applied on " +
                "the next scene load (shop is rebuilt per save load). " +
                "Recoloring with the palette is always free.");
            // Key renamed from "PatternedRecolor" (pre-release 2.0 dev
            // builds) so the new Tint default applies — flat Replace
            // provably doesn't suit detailed walls like brick/tile.
            CfgPatternedMode = Config.Bind("General", "PatternedRecolorMode",
                PatternedRecolorKind.Tint,
                "How the palette recolors patterned walls (brick, tile, " +
                "wallpaper) and decal-wall bases. Tint (default): keep the " +
                "pattern, shifted to your color. Replace: flat color over " +
                "the pattern. Applied at paint time — flip it and repaint " +
                "a neighboring tile to compare.");

            // 1.x \u2192 1.5: the old default was 2500; the new lineup is priced
            // in line with vanilla walls (1000). If the player never changed
            // the old default, move them to the new one.
            if (Mathf.Approximately(CfgWallPrice.Value, 2500f))
            {
                CfgWallPrice.Value = 1000f;
                Logger.LogInfo("WallPrice migrated from old default 2500 to 1000.");
            }

            // Held-palette orientation offsets, applied ON TOP of the
            // prefab's own authored rotation. Live-tunable in-game via
            // ConfigurationManager (F1) \u2014 dial it in, then we bake the
            // final values as defaults.
#if REPAINTED_DEV
            // Defaults = values dialed in with the dev tuner (2026-07-10).
            CfgPaletteRotX = Config.Bind("PaletteModel", "RotationX", -80f,
                new ConfigDescription(
                    "Held-palette rotation offset around X (degrees), " +
                    "relative to the model's authored orientation. Live.",
                    new AcceptableValueRange<float>(-180f, 180f)));
            CfgPaletteRotY = Config.Bind("PaletteModel", "RotationY", 60f,
                new ConfigDescription(
                    "Held-palette rotation offset around Y (degrees). Live.",
                    new AcceptableValueRange<float>(-180f, 180f)));
            CfgPaletteRotZ = Config.Bind("PaletteModel", "RotationZ", 60f,
                new ConfigDescription(
                    "Held-palette rotation offset around Z (degrees). Live.",
                    new AcceptableValueRange<float>(-180f, 180f)));
            CfgPaletteScale = Config.Bind("PaletteModel", "Scale", 1.4f,
                new ConfigDescription(
                    "Held-palette model scale. Live.",
                    new AcceptableValueRange<float>(0.2f, 4f)));
            CfgPaletteOffX = Config.Bind("PaletteModel", "OffsetX", 0f,
                new ConfigDescription(
                    "Held-palette position offset (meters, tool-local X). Live.",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));
            CfgPaletteOffY = Config.Bind("PaletteModel", "OffsetY", 0f,
                new ConfigDescription(
                    "Held-palette position offset (meters, tool-local Y). Live.",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));
            CfgPaletteOffZ = Config.Bind("PaletteModel", "OffsetZ", 0f,
                new ConfigDescription(
                    "Held-palette position offset (meters, tool-local Z). Live.",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));
            CfgWoodSmoothness = Config.Bind("PaletteModel", "WoodSmoothness", 0.6f,
                new ConfigDescription(
                    "Smoothness (gloss) of the palette's wooden board. The " +
                    "flat board reads poorly under the store's ambient " +
                    "lighting without some specular response. Live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            CfgPaletteRotX.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgPaletteRotY.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgPaletteRotZ.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgPaletteScale.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgPaletteOffX.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgPaletteOffY.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgPaletteOffZ.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyPaletteTransform();
            CfgWoodSmoothness.SettingChanged += (_, __) =>
                Patches.HotKeyManagerPatch.ReapplyWoodSmoothness();
#endif

            TileColorStore.Initialize();
            LoadPaletteBundle();
            LoadPaletteIcon();
            LoadWallPreviewSprites();

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            DataSerializerPatch.Apply(harmony);

            // One-shot startup audit: verify every game member we reflect on
            // or call still exists in this game build. Logs one consolidated
            // error listing anything missing (see CODE-REVIEW-2026-07.md).
            Util.ReflectionSelfCheck.Run();

            var overlayGO = new GameObject("Repainted_ColorPicker");
            DontDestroyOnLoad(overlayGO);
            overlayGO.AddComponent<ColorPickerOverlay>();

#if REPAINTED_DEV
            var devGO = new GameObject("Repainted_DevTools");
            DontDestroyOnLoad(devGO);
            devGO.AddComponent<Dev.DevWallDumpTool>();
#endif

#if REPAINTED_DEV
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded. Patches applied. [DEV BUILD]");
#else
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded. Patches applied.");
#endif
        }

        private void LoadPaletteBundle()
        {
            // A loose bundle file next to the DLL OVERRIDES the embedded
            // one (dev iteration via deploy.sh --bundle, or user model
            // swaps); otherwise load the embedded copy — release installs
            // are a single DLL.
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string bundlePath = Path.Combine(pluginDir, "repainted_palette");

            AssetBundle bundle = null;
            if (File.Exists(bundlePath))
            {
                Logger.LogInfo("Loading palette AssetBundle from loose file " +
                    "(overrides the embedded copy).");
                bundle = AssetBundle.LoadFromFile(bundlePath);
            }
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(
                    "Repainted.assets.palette.repainted_palette"))
                {
                    if (stream != null)
                        bundle = AssetBundle.LoadFromMemory(ReadFully(stream));
                }
            }

            if (bundle == null)
            {
                Logger.LogError(
                    "Palette AssetBundle unavailable (no embedded resource, " +
                    $"no file at {bundlePath}) — the palette tool will keep " +
                    "the cloned roller mesh.");
                return;
            }

            PalettePrefab = bundle.LoadAsset<GameObject>("PalettePrefab");
            if (PalettePrefab == null)
            {
                var allNames = bundle.GetAllAssetNames();
                Logger.LogInfo($"Bundle assets: {string.Join(", ", allNames)}");
                foreach (var name in allNames)
                {
                    var obj = bundle.LoadAsset<GameObject>(name);
                    if (obj != null)
                    {
                        PalettePrefab = obj;
                        Logger.LogInfo($"Loaded palette prefab from: {name}");
                        break;
                    }
                }
            }

            if (PalettePrefab != null)
            {
                Logger.LogInfo($"Palette prefab loaded: {PalettePrefab.name}, " +
                    $"children: {PalettePrefab.transform.childCount}");
            }
            else
            {
                Logger.LogError("Could not find any GameObject in palette bundle!");
            }

            bundle.Unload(false);
        }

        /// <summary>
        /// Reads a stream to the end. Stream.Read is not guaranteed to fill
        /// the buffer in a single call, so loop until it does.
        /// </summary>
        private static byte[] ReadFully(Stream stream)
        {
            using (var ms = new MemoryStream(
                stream.CanSeek ? (int)stream.Length : 4096))
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Loads the hotbar icon PNG from the embedded assembly resource,
        /// decodes it into a Texture2D, and wraps it in a Sprite. The
        /// resource is embedded via Repainted.csproj so no external file
        /// deploy is needed.
        /// </summary>
        private void LoadPaletteIcon()
        {
            const string resourceName = "Repainted.assets.paletteIcon.png";
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Logger.LogWarning(
                            $"Embedded icon resource '{resourceName}' not found; " +
                            "hotbar will keep the cloned megaphone icon. " +
                            $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");
                        return;
                    }

                    byte[] bytes = ReadFully(stream);

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                    if (!tex.LoadImage(bytes))
                    {
                        Logger.LogError("Failed to decode palette icon PNG data");
                        return;
                    }
                    tex.name = "PaletteIconTex";
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;

                    PaletteIconSprite = Sprite.Create(
                        tex,
                        new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f),
                        100f
                    );
                    PaletteIconSprite.name = "PaletteIconSprite";

                    Logger.LogInfo(
                        $"Palette icon loaded: {tex.width}×{tex.height}");
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Exception loading palette icon: {ex}");
            }
        }

        /// <summary>
        /// Loads the pre-baked preview PNGs (rainbow-over-Wall1) for the 3
        /// brush shop walls from embedded resources into Sprites, keyed by
        /// WallType. Each PNG is baked by assets/previews/bake_previews.py
        /// and mirrors the ColorBand definitions in ModdedWallRegistry.
        /// </summary>
        private void LoadWallPreviewSprites()
        {
            var previews = new[]
            {
                (Data.ModdedWallRegistry.WallType.BottomTopStripe,
                 "Repainted.assets.previews.Repainted_BottomTopStripe.png"),
                (Data.ModdedWallRegistry.WallType.Trim,
                 "Repainted.assets.previews.Repainted_Trim.png"),
                (Data.ModdedWallRegistry.WallType.HighStripe,
                 "Repainted.assets.previews.Repainted_HighStripe.png"),
            };

            var asm = Assembly.GetExecutingAssembly();
            foreach (var (type, resourceName) in previews)
            {
                try
                {
                    using (Stream stream = asm.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                        {
                            Logger.LogWarning(
                                $"Preview resource '{resourceName}' not found; " +
                                "slot will keep the cloned template sprite.");
                            continue;
                        }
                        byte[] bytes = ReadFully(stream);

                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                        if (!tex.LoadImage(bytes))
                        {
                            Logger.LogError($"Failed to decode preview PNG {resourceName}");
                            continue;
                        }
                        tex.name = $"PreviewTex_{type}";
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode = TextureWrapMode.Clamp;

                        var sprite = Sprite.Create(
                            tex,
                            new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f),
                            100f);
                        sprite.name = $"PreviewSprite_{type}";
                        WallPreviewSprites[type] = sprite;
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.LogError($"Exception loading preview '{resourceName}': {ex}");
                }
            }
            Logger.LogInfo($"Wall preview sprites loaded: {WallPreviewSprites.Count}/3");
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.custommods.repainted";
        public const string PLUGIN_NAME = "Repainted";
        public const string PLUGIN_VERSION = "2.0.0";
    }

}
