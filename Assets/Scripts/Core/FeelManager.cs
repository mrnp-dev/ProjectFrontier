using System.Collections;
using TMPro;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectFrontier
{
    /// <summary>
    /// Game-feel layer: palette-colored pickup bursts, rising floating TMP text, screen flash,
    /// milestone banner moment, and Cinemachine Impulse shake (all UI tweens on unscaled time).
    /// </summary>
    public class FeelManager : MonoBehaviour
    {
        public static FeelManager Instance { get; private set; }

        [Header("Optional Scene References")]
        public UnityEngine.UI.Image flashOverlay;
        public RectTransform milestoneBannerRoot;
        public TextMeshProUGUI milestoneBannerText;
        public CinemachineImpulseSource impulseSource;

        [Header("Tunables")]
        public float floatingTextRise = 0.85f;
        public float floatingTextDuration = 0.8f;
        public Material floatingTextMaterial;

        private Material _particleMat;
        private readonly System.Collections.Generic.Dictionary<GameObject, TextMeshProUGUI> _shadowFor = new System.Collections.Generic.Dictionary<GameObject, TextMeshProUGUI>();
        private Coroutine _milestoneRoutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Shader spriteShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (spriteShader == null) spriteShader = Shader.Find("Sprites/Default");
            _particleMat = new Material(spriteShader) { name = "FrontierSparkMat" };
            _particleMat.mainTexture = PixelArtFactory.GetPixelSparkSprite().texture;
        }

        public void PlayPickupFeedback(Vector3 worldPos, string label)
        {
            float density = GraphicsSettingsManager.Instance != null ? GraphicsSettingsManager.Instance.CurrentConfig.particleDensity : 1f;
            int count = Mathf.Clamp(Mathf.RoundToInt(10 * Mathf.Max(0.4f, density)), 4, 14);
            SpawnPaletteBurst(worldPos, count, FrontierPalette.HearthGold, FrontierPalette.CreamParchment, 2.2f);
            SpawnFloatingWorldText(worldPos + new Vector3(0f, 0.55f, 0f), label, FrontierPalette.HearthGold);
            if (impulseSource != null) impulseSource.GenerateImpulse(0.08f);
        }

        public void PlayMilestoneMoment(string bannerLabel, Color accentColor)
        {
            if (_milestoneRoutine != null) StopCoroutine(_milestoneRoutine);
            _milestoneRoutine = StartCoroutine(MilestoneSequence(bannerLabel, accentColor));
            if (PlayerController2D.Instance != null)
            {
                float density = GraphicsSettingsManager.Instance != null ? GraphicsSettingsManager.Instance.CurrentConfig.particleDensity : 1f;
                int count = Mathf.Clamp(Mathf.RoundToInt(36 * Mathf.Max(0.35f, density)), 12, 42);
                SpawnPaletteBurst(PlayerController2D.Instance.transform.position + Vector3.up * 0.5f, count, accentColor, FrontierPalette.FrontierTeal, 3.6f);
            }
            if (impulseSource != null) impulseSource.GenerateImpulse(0.22f);
        }

        private IEnumerator MilestoneSequence(string bannerLabel, Color accentColor)
        {
            // 0ms: Flash 0.55 -> 0 over 110ms OutCubic (unscaled)
            if (flashOverlay != null)
            {
                flashOverlay.gameObject.SetActive(true);
                StartCoroutine(Ease.Tween(0.12f, t =>
                {
                    flashOverlay.color = FrontierPalette.WithAlpha(accentColor, Mathf.Lerp(0.55f, 0f, t));
                }, Ease.OutCubic, unscaled: true));
            }

            // 50ms: Banner in 0 -> 1 over 350ms OutBack (unscaled)
            yield return new WaitForSecondsRealtime(0.05f);
            if (milestoneBannerRoot != null && milestoneBannerText != null)
            {
                milestoneBannerText.text = bannerLabel;
                milestoneBannerText.color = accentColor;
                milestoneBannerRoot.gameObject.SetActive(true);
                yield return Ease.Tween(0.35f, t =>
                {
                    milestoneBannerRoot.localScale = Vector3.one * t;
                }, Ease.OutBack, unscaled: true);

                yield return new WaitForSecondsRealtime(0.95f);
                yield return Ease.Tween(0.20f, t =>
                {
                    milestoneBannerRoot.localScale = Vector3.one * (1f - t);
                }, Ease.OutCubic, unscaled: true);
                milestoneBannerRoot.gameObject.SetActive(false);
            }

            if (flashOverlay != null) flashOverlay.gameObject.SetActive(false);
            _milestoneRoutine = null;
        }

        public void HideTransientOverlays()
        {
            if (_milestoneRoutine != null) { StopCoroutine(_milestoneRoutine); _milestoneRoutine = null; }
            if (milestoneBannerRoot != null) milestoneBannerRoot.gameObject.SetActive(false);
            if (flashOverlay != null) flashOverlay.gameObject.SetActive(false);
        }

        private void SpawnPaletteBurst(Vector3 pos, int count, Color colorA, Color colorB, float speed)
        {
            var go = new GameObject("PaletteSparkBurst");
            go.transform.position = pos;

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false;
            main.duration = 0.55f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.32f, 0.55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.55f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.24f);
            main.startColor = new ParticleSystem.MinMaxGradient(colorA, colorB);
            main.gravityModifier = 0.35f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction = ParticleSystemStopAction.Destroy;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.15f;

            var colOverLife = ps.colorOverLifetime;
            colOverLife.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(colorA, 0f), new GradientColorKey(colorB, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            colOverLife.color = grad;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _particleMat;
            renderer.sortingOrder = 50;

            ps.Play();
        }

        private void SpawnFloatingWorldText(Vector3 startPos, string label, Color color)
        {
            // Rendered on the native-res overlay canvas (crisp), anchored to the world position each frame.
            // World-space TMP would be rasterized at 480x270 and smear into an unreadable blur.
            Canvas canvas = milestoneBannerRoot != null ? milestoneBannerRoot.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return;
            int sibling = Mathf.Max(0, milestoneBannerRoot.GetSiblingIndex());
            // Hard drop shadow (created first so it renders behind) keeps gold text legible over sand/cobble.
            TextMeshProUGUI shadow = CreateFloatingLabel(canvas.transform, "FloatingPickupShadow", label, FrontierPalette.InkDark, sibling);
            TextMeshProUGUI tmp = CreateFloatingLabel(canvas.transform, "FloatingPickupText", label, color, sibling + 1);
            GameObject go = tmp.gameObject;
            _shadowFor[go] = shadow;

            StartCoroutine(AnimateFloatingText(go, tmp, (RectTransform)canvas.transform, startPos, color));
        }

        private TextMeshProUGUI CreateFloatingLabel(Transform canvas, string name, string label, Color color, int sibling)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            go.transform.SetSiblingIndex(sibling);
            ((RectTransform)go.transform).sizeDelta = new Vector2(320f, 48f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 34f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.characterSpacing = 4f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.color = color;
            tmp.raycastTarget = false;
            if (floatingTextMaterial != null) tmp.fontSharedMaterial = floatingTextMaterial;
            return tmp;
        }

        private IEnumerator AnimateFloatingText(GameObject go, TextMeshProUGUI tmp, RectTransform canvasRect, Vector3 startPos, Color baseColor)
        {
            Vector3 endPos = startPos + Vector3.up * floatingTextRise;
            Camera cam = Camera.main;
            if (cam != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, cam.WorldToScreenPoint(startPos), null, out Vector2 startLocal))
            {
                ((RectTransform)go.transform).anchoredPosition = startLocal;
                if (_shadowFor.TryGetValue(go, out var sh0) && sh0 != null)
                    ((RectTransform)sh0.transform).anchoredPosition = startLocal + new Vector2(3f, -3f);
            }
            yield return Ease.Tween(floatingTextDuration, t =>
            {
                if (go == null || cam == null) return;
                Vector3 screen = cam.WorldToScreenPoint(Vector3.Lerp(startPos, endPos, t));
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, null, out Vector2 local))
                    ((RectTransform)go.transform).anchoredPosition = local;
                float a = t < 0.6f ? 1f : Mathf.Lerp(1f, 0f, (t - 0.6f) / 0.4f);
                if (_shadowFor.TryGetValue(go, out var sh) && sh != null)
                {
                    ((RectTransform)sh.transform).anchoredPosition = ((RectTransform)go.transform).anchoredPosition + new Vector2(3f, -3f);
                    sh.color = FrontierPalette.WithAlpha(FrontierPalette.InkDark, a);
                }
                tmp.color = FrontierPalette.WithAlpha(baseColor, t < 0.6f ? 1f : Mathf.Lerp(1f, 0f, (t - 0.6f) / 0.4f));
            }, Ease.OutCubic, unscaled: false);
            if (_shadowFor.TryGetValue(go, out var shadowTmp))
            {
                if (shadowTmp != null) Destroy(shadowTmp.gameObject);
                _shadowFor.Remove(go);
            }
            if (go != null) Destroy(go);
        }
    }
}
