using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ProjectFrontier
{
    /// <summary>
    /// On-device frame-rate readout for the Milestone 2 "≥ 30 FPS on low-end Android" gate.
    /// Shows average FPS, 1% low and average frame time over a rolling 1 s window, plus the active preset's target.
    /// Toggle: triple-tap the top-left corner (persisted). Every 10 s it also logs a summary line, readable with
    /// <c>adb logcat -s Unity | grep "\[Perf\]"</c>, whether or not the overlay is visible.
    /// Zero per-frame allocations; text is rebuilt once per second.
    /// </summary>
    public sealed class PerfOverlay : MonoBehaviour
    {
        private const string PrefKey = "frontier_perf_overlay";
        private const int WindowFrames = 240;        // ring buffer, enough for 1 s at 240 Hz
        private const float RefreshSeconds = 1f;
        private const float LogSeconds = 10f;
        private const float CornerFraction = 0.12f;  // top-left tap zone, fraction of the shorter screen side
        private const float TripleTapSeconds = 0.8f;

        private readonly float[] _frameMs = new float[WindowFrames];
        private readonly float[] _sortScratch = new float[WindowFrames];
        private int _head, _count;
        private float _windowStart, _windowElapsed;
        private int _windowFrames;

        private float _logStart;
        private int _logFrames;
        private float _logWorstLow = float.MaxValue;

        private int _taps;
        private float _firstTapTime;

        private GameObject _canvasGo;
        private TextMeshProUGUI _label;

        public static PerfOverlay Instance { get; private set; }
        public float LastAvgFps { get; private set; }
        public float LastLow1Fps { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("PerfOverlay");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PerfOverlay>();
        }

        public bool Visible
        {
            get => _canvasGo != null && _canvasGo.activeSelf;
            set
            {
                if (_canvasGo == null) BuildUi();
                _canvasGo.SetActive(value);
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
            }
        }

        private void Start()
        {
            _windowStart = _logStart = Time.unscaledTime;
            BuildUi();
            _canvasGo.SetActive(PlayerPrefs.GetInt(PrefKey, 0) == 1);
        }

        private void BuildUi()
        {
            if (_canvasGo != null) return;
            _canvasGo = new GameObject("PerfCanvas", typeof(Canvas), typeof(CanvasScaler));
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960, 540);
            scaler.matchWidthOrHeight = 1f;

            var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(_canvasGo.transform, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = bgRt.anchorMax = bgRt.pivot = new Vector2(0f, 1f);
            bgRt.anchoredPosition = new Vector2(6f, -6f);
            bgRt.sizeDelta = new Vector2(250f, 40f);
            var img = bg.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.6f);
            img.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(bg.transform, false);
            var rt = (RectTransform)textGo.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 2f);
            rt.offsetMax = new Vector2(-6f, -2f);
            _label = textGo.GetComponent<TextMeshProUGUI>();
            _label.fontSize = 14f;
            _label.color = Color.white;
            _label.raycastTarget = false;
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.alignment = TextAlignmentOptions.MidlineLeft;
            _label.SetText("FPS --");
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _frameMs[_head] = dt * 1000f;
            _head = (_head + 1) % WindowFrames;
            if (_count < WindowFrames) _count++;
            _windowFrames++;
            _logFrames++;

            HandleToggleGesture();

            float now = Time.unscaledTime;
            _windowElapsed = now - _windowStart;
            if (_windowElapsed >= RefreshSeconds) Refresh(now);
            if (now - _logStart >= LogSeconds) LogSummary(now);
        }

        private void Refresh(float now)
        {
            int n = Mathf.Min(_windowFrames, _count);
            LastAvgFps = _windowFrames / _windowElapsed;
            LastLow1Fps = ComputeLow1Fps(n);
            if (LastLow1Fps < _logWorstLow) _logWorstLow = LastLow1Fps;

            if (_label != null && _canvasGo.activeSelf)
            {
                int target = Application.targetFrameRate;
                _label.SetText("FPS {0:1}  1%low {1:0}  {2:1}ms  cap {3:0}",
                    LastAvgFps, LastLow1Fps, 1000f / Mathf.Max(LastAvgFps, 0.01f), target > 0 ? target : 0);
                _label.color = LastAvgFps >= 30f ? Color.white : new Color(1f, 0.45f, 0.4f);
            }

            _windowStart = now;
            _windowFrames = 0;
        }

        private float ComputeLow1Fps(int n)
        {
            if (n <= 0) return 0f;
            int idx = _head;
            for (int i = 0; i < n; i++)
            {
                idx = (idx - 1 + WindowFrames) % WindowFrames;
                _sortScratch[i] = _frameMs[idx];
            }
            return Low1Fps(_sortScratch, n);
        }

        /// <summary>
        /// FPS equivalent of the mean of the slowest 1% of <paramref name="n"/> frame times (at least one frame).
        /// Sorts the first <paramref name="n"/> entries of <paramref name="frameMs"/> in place.
        /// </summary>
        public static float Low1Fps(float[] frameMs, int n)
        {
            if (frameMs == null || n <= 0) return 0f;
            n = Mathf.Min(n, frameMs.Length);
            System.Array.Sort(frameMs, 0, n);
            int worst = Mathf.Max(1, n / 100);
            float sum = 0f;
            for (int i = n - worst; i < n; i++) sum += frameMs[i];
            float ms = sum / worst;
            return ms > 0f ? 1000f / ms : 0f;
        }

        private void LogSummary(float now)
        {
            float avg = _logFrames / (now - _logStart);
            Debug.Log($"[Perf] avg={avg:0.0}fps worst1%low={(_logWorstLow == float.MaxValue ? 0f : _logWorstLow):0.0}fps " +
                      $"cap={Application.targetFrameRate} quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} " +
                      $"res={Screen.width}x{Screen.height} t={now:0}s");
            _logStart = now;
            _logFrames = 0;
            _logWorstLow = float.MaxValue;
        }

        private void HandleToggleGesture()
        {
            Pointer p = Pointer.current;
            if (p == null || !p.press.wasPressedThisFrame) return;
            Vector2 pos = p.position.ReadValue();
            float zone = Mathf.Min(Screen.width, Screen.height) * CornerFraction;
            if (pos.x > zone || pos.y < Screen.height - zone) { _taps = 0; return; }

            float now = Time.unscaledTime;
            if (_taps == 0 || now - _firstTapTime > TripleTapSeconds)
            {
                _taps = 1;
                _firstTapTime = now;
                return;
            }
            if (++_taps >= 3)
            {
                _taps = 0;
                Visible = !Visible;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
