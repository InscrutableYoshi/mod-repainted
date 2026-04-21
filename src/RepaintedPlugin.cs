using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
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

        /// <summary>
        /// Preview sprites shown in the decoration shop, keyed by WallType.
        /// Pre-baked rainbow-over-Wall1 so every entry visually advertises
        /// where its colored bands sit. DecorationWindowInitializePatch
        /// swaps these onto each cloned DecorationUI slot.
        /// </summary>
        internal static readonly Dictionary<Data.ModdedWallRegistry.WallType, Sprite>
            WallPreviewSprites = new Dictionary<Data.ModdedWallRegistry.WallType, Sprite>();

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loading...");

            TileColorStore.Initialize();
            LoadPaletteBundle();
            LoadPaletteIcon();
            LoadWallPreviewSprites();

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            DataSerializerPatch.Apply(harmony);

            var overlayGO = new GameObject("Repainted_ColorPicker");
            DontDestroyOnLoad(overlayGO);
            overlayGO.AddComponent<ColorPickerOverlay>();

#if REPAINTED_DEV
            var devGO = new GameObject("Repainted_DevTools");
            DontDestroyOnLoad(devGO);
            devGO.AddComponent<Dev.DevWallDumpTool>();
#endif

            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded. Patches applied.");
        }

        private void LoadPaletteBundle()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string bundlePath = Path.Combine(pluginDir, "repainted_palette");

            if (!File.Exists(bundlePath))
            {
                Logger.LogWarning($"Palette AssetBundle not found at: {bundlePath}");
                return;
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Logger.LogError("Failed to load palette AssetBundle!");
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

                    byte[] bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);

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
        /// Loads the five pre-baked preview PNGs (rainbow-over-Wall1) from
        /// embedded resources into Sprites, keyed by WallType. Each PNG is
        /// baked by assets/previews/bake_previews.py and mirrors the
        /// ColorBand definitions in DecorationManagerPatch.
        /// </summary>
        private void LoadWallPreviewSprites()
        {
            var previews = new[]
            {
                (Data.ModdedWallRegistry.WallType.FullColor,
                 "Repainted.assets.previews.Repainted_Full.png"),
                (Data.ModdedWallRegistry.WallType.BottomStripe,
                 "Repainted.assets.previews.Repainted_BottomStripe.png"),
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
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);

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
            Logger.LogInfo($"Wall preview sprites loaded: {WallPreviewSprites.Count}/5");
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.custommods.repainted";
        public const string PLUGIN_NAME = "Repainted";
        public const string PLUGIN_VERSION = "1.0.0";
    }

    /// <summary>
    /// Fixes a vanilla bug in ButtonsWindow.RepaintWithInputActions where
    /// the cleanup loop uses buttons.Count instead of pcUIs.Count, leaving
    /// orphaned pcUI elements visible when pcUIs.Count > buttons.Count.
    /// </summary>
    [HarmonyPatch(typeof(ButtonsWindow), "RepaintWithInputActions")]
    public static class ButtonsWindowOrphanFixPatch
    {
        private static System.Reflection.FieldInfo buttonsField;
        private static System.Reflection.FieldInfo pcUIsField;

        static void Postfix(ButtonsWindow __instance)
        {
            if (buttonsField == null)
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                buttonsField = typeof(ButtonsWindow).GetField("buttons", flags);
                pcUIsField = typeof(ButtonsWindow).GetField("pcUIs", flags);
            }

            var buttons = (List<UIButton>)buttonsField.GetValue(__instance);
            var pcUIs = (List<UIButton>)pcUIsField.GetValue(__instance);

            // The original loop only deactivates up to buttons.Count.
            // Deactivate any pcUIs beyond that range.
            for (int i = buttons.Count; i < pcUIs.Count; i++)
            {
                pcUIs[i].gameObject.SetActive(false);
            }
        }
    }
}
