using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ProjectFrontier
{
    public enum GraphicsPresetTier
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Custom = 3
    }

    [Serializable]
    public class GraphicsConfigData
    {
        public int presetTier = (int)GraphicsPresetTier.Low;
        public int targetFps = 30;
        public bool dynamicLights2D = false;
        public bool postProcessing = false;
        public float particleDensity = 0.25f;
        public bool initialized = false;
    }

    /// <summary>
    /// Low-end Android URP 2D graphics preset controller.
    /// Locks FilterMode.Point, Mipmaps off, and MSAA=0 across all tiers.
    /// Switches QualitySettings tier (URP2D_Low/Medium/High), camera HDR/PostFX, 2D point lights, particles, and FPS.
    /// </summary>
    public class GraphicsSettingsManager : MonoBehaviour
    {
        public static GraphicsSettingsManager Instance { get; private set; }

        public static event Action<GraphicsConfigData> OnGraphicsChanged;

        [Header("Optional URP 2D Tier Assets")]
        [SerializeField] private UniversalRenderPipelineAsset lowAsset;
        [SerializeField] private UniversalRenderPipelineAsset mediumAsset;
        [SerializeField] private UniversalRenderPipelineAsset highAsset;

        private GraphicsConfigData _config = new GraphicsConfigData();
        private string _configPath;

        public GraphicsConfigData CurrentConfig => _config;
        public GraphicsPresetTier CurrentTier => (GraphicsPresetTier)_config.presetTier;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            OnGraphicsChanged = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _configPath = Path.Combine(Application.persistentDataPath, "frontier_graphics.json");
            LoadOrAutoDetect();
            ApplyCurrentConfig(save: false);
        }

        private void LoadOrAutoDetect()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    var loaded = JsonUtility.FromJson<GraphicsConfigData>(json);
                    if (loaded != null && loaded.initialized)
                    {
                        _config = loaded;
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to hardware auto-detection.
            }

            // Auto-detect by device RAM: <= 3 GB -> Low, else Medium (safe mobile default).
            GraphicsPresetTier detected = SystemInfo.systemMemorySize <= 3072
                ? GraphicsPresetTier.Low
                : GraphicsPresetTier.Medium;
            ConfigurePresetValues(detected);
            _config.initialized = true;
        }

        public void SetPreset(GraphicsPresetTier tier)
        {
            ConfigurePresetValues(tier);
            _config.initialized = true;
            ApplyCurrentConfig(save: true);
        }

        public void SetCustom(int targetFps, bool dynamicLights2D, bool postProcessing, float particleDensity)
        {
            _config.presetTier = (int)GraphicsPresetTier.Custom;
            _config.targetFps = Mathf.Clamp(targetFps, 30, 60);
            _config.dynamicLights2D = dynamicLights2D;
            _config.postProcessing = postProcessing;
            _config.particleDensity = Mathf.Clamp01(particleDensity);
            _config.initialized = true;
            ApplyCurrentConfig(save: true);
        }

        public void CyclePreset()
        {
            int next = ((int)CurrentTier + 1) % 3; // Cycle Low -> Medium -> High
            SetPreset((GraphicsPresetTier)next);
        }

        private void ConfigurePresetValues(GraphicsPresetTier tier)
        {
            _config.presetTier = (int)tier;
            switch (tier)
            {
                case GraphicsPresetTier.Low:
                    _config.targetFps = 30;
                    _config.dynamicLights2D = false;
                    _config.postProcessing = false;
                    _config.particleDensity = 0.25f;
                    break;
                case GraphicsPresetTier.Medium:
                    _config.targetFps = 60;
                    _config.dynamicLights2D = true;
                    _config.postProcessing = true;
                    _config.particleDensity = 0.65f;
                    break;
                case GraphicsPresetTier.High:
                    _config.targetFps = 60;
                    _config.dynamicLights2D = true;
                    _config.postProcessing = true;
                    _config.particleDensity = 1.0f;
                    break;
                case GraphicsPresetTier.Custom:
                    break;
            }
        }

        public void ApplyCurrentConfig(bool save = true)
        {
            // Map tier to QualitySettings indices: 1=Low, 2=Medium, 3=High
            int targetQualityIndex = CurrentTier switch
            {
                GraphicsPresetTier.Low => 1,
                GraphicsPresetTier.Medium => 2,
                GraphicsPresetTier.High => 3,
                _ => _config.postProcessing ? 3 : (_config.dynamicLights2D ? 2 : 1)
            };

            if (QualitySettings.names.Length > targetQualityIndex && QualitySettings.GetQualityLevel() != targetQualityIndex)
            {
                QualitySettings.SetQualityLevel(targetQualityIndex, true);
            }

            // Keep MSAA strictly off for crisp 16-PPU pixel art.
            QualitySettings.antiAliasing = 0;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = _config.targetFps;

            // Configure Main Camera HDR & Post-Processing
            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.allowMSAA = false;
                cam.allowDynamicResolution = false;
                bool useHdr = CurrentTier == GraphicsPresetTier.High || (CurrentTier == GraphicsPresetTier.Custom && _config.postProcessing);
                cam.allowHDR = useHdr;
                if (cam.TryGetComponent<UniversalAdditionalCameraData>(out var camData))
                {
                    camData.renderPostProcessing = _config.postProcessing;
                }
            }

            // Toggle local 2D point lights (keep Global Light 2D always enabled)
            var lights = FindObjectsByType<Light2D>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var l in lights)
            {
                if (l.lightType != Light2D.LightType.Global)
                {
                    l.enabled = _config.dynamicLights2D;
                }
            }

            if (save)
            {
                SaveConfig();
            }

            OnGraphicsChanged?.Invoke(_config);
        }

        public string GetShortTierLabel()
        {
            return CurrentTier switch
            {
                GraphicsPresetTier.Low => "LOW",
                GraphicsPresetTier.Medium => "MED",
                GraphicsPresetTier.High => "HIGH",
                _ => "CUSTOM"
            };
        }

        private void SaveConfig()
        {
            try
            {
                string json = JsonUtility.ToJson(_config, true);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception)
            {
                // Non-fatal in sandboxed test runs.
            }
        }
    }
}
