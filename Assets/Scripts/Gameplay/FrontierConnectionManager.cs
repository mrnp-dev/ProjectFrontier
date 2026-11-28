using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ProjectFrontier.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace ProjectFrontier
{
    /// <summary>
    /// Online-only (#37) owner: runs the startup gate (internet, UGS sign-in, AI endpoint), owns the central
    /// <see cref="ConnectionMonitor"/> that every LLM and UGS caller reports into via <see cref="ConnectionReports"/>,
    /// polls the OS network interface at 1 Hz, probes cheaply while a drop lasts, and shows two overlays:
    /// "Connect to the internet to play" (with Retry) before gameplay, and "Reconnecting..." during long drops, which
    /// holds the game (<see cref="FrontierGameLoop.SetConnectionHold"/>) until a call or probe succeeds.
    /// Bootstraps itself after the first scene loads and survives scene reloads (restart keeps the passed gate).
    /// No per-frame allocations: per frame it does two float compares and one int compare.
    /// </summary>
    public class FrontierConnectionManager : MonoBehaviour, IConnectionSink
    {
        public static FrontierConnectionManager Instance { get; private set; }

        /// <summary>Probe target when no AI endpoint is configured: any HTTP answer from UGS auth counts.</summary>
        public const string FallbackProbeUrl = "https://player-auth.services.api.unity.com/";
        public const int ProbeTimeoutSeconds = 4;
        public const float SignInWaitSeconds = 15f;
        private const float TickInterval = 0.25f;
        private const float ReachabilityInterval = 1f;

        public ConnectionMonitor Monitor { get; private set; }
        public bool CanStartGameplay => Monitor != null && Monitor.CanStartGameplay;
        public bool IsProbing { get; private set; }
        public int ProbeCount { get; private set; }
        public string LastProbeResult { get; private set; } = "-";

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private float _tickTimer;
        private float _reachTimer;
        private int _seenVersion = -1;
        private bool _startupRunning;
#pragma warning disable 0649 // assigned only by the dev outage drill
        private bool _simulatedOutage;
#pragma warning restore 0649

        // Overlay (built at runtime so the generated scene doesn't change).
        private GameObject _overlayRoot;
        private GameObject _gatePanel;
        private GameObject _reconnectPanel;
        private TextMeshProUGUI _gateTitle;
        private TextMeshProUGUI _gateBody;
        private Button _retryButton;
        private TextMeshProUGUI _reconnectDots;
        private float _dotsTimer;
        private int _dots;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            ConnectionReports.Sink = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null || FrontierGameLoop.Instance == null) return;
            var go = new GameObject("FrontierConnection");
            DontDestroyOnLoad(go);
            go.AddComponent<FrontierConnectionManager>();
        }

        /// <summary>
        /// True when callers should try the network. Without a manager (EditMode, tools) falls back to the OS check.
        /// </summary>
        public static bool AllowsNetworkCalls
        {
            get
            {
                var m = Instance;
                if (m != null && m.Monitor != null) return m.Monitor.AllowsNetworkCalls;
                return Application.internetReachability != NetworkReachability.NotReachable;
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Monitor = new ConnectionMonitor(() => _clock.Elapsed.TotalSeconds);
            ConnectionReports.Sink = this;
            BuildOverlay();
        }

        private void Start() => _ = RunStartupCheckAsync();

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (ReferenceEquals(ConnectionReports.Sink, this)) ConnectionReports.Sink = null;
            Instance = null;
        }

        // ------------------------------------------------------------------ IConnectionSink

        public void ReportSuccess(ConnectionSource source)
        {
            if (_simulatedOutage) return; // dev outage drill: real UGS/LLM successes must not end it
            Monitor.ReportSuccess(source);
        }

        public void ReportFailure(ConnectionSource source, string reason) => Monitor.ReportFailure(source, reason);

        // ------------------------------------------------------------------ startup gate

        /// <summary>Runs (or re-runs, from Retry) the startup gate. Returns the result for verification scripts.</summary>
        public async Task<StartupBlockReason> RunStartupCheckAsync()
        {
            if (_startupRunning) return Monitor.BlockReason;
            _startupRunning = true;
            try
            {
                Monitor.BeginStartupCheck();
                ApplyVisuals();

                bool internet = !_simulatedOutage && Application.internetReachability != NetworkReachability.NotReachable;
                bool signedIn = false, aiReachable = false;
                bool aiConfigured = TryGetAiProbeUrl(out string aiProbeUrl);
                if (internet)
                {
                    signedIn = await WaitForSignInAsync();
                    if (aiConfigured) aiReachable = await ProbeAsync(aiProbeUrl);
                }
                if (this == null) return StartupBlockReason.None;

                StartupBlockReason result = ConnectionMonitor.EvaluateStartup(internet, signedIn, aiConfigured, aiReachable);
                if (!aiConfigured && result == StartupBlockReason.None)
                    Debug.LogWarning("[Net] No AI endpoint configured: the gate passed on internet + sign-in; villagers use built-in lines.");
                Debug.Log($"[Net] Startup gate: {(result == StartupBlockReason.None ? "online" : "blocked (" + result + ")")} " +
                          $"internet={internet} signedIn={signedIn} ai={(aiConfigured ? (aiReachable ? "reachable" : "unreachable") : "not configured")}");
                Monitor.CompleteStartupCheck(result);
                ApplyVisuals();
                return result;
            }
            finally
            {
                _startupRunning = false;
            }
        }

        private async Task<bool> WaitForSignInAsync()
        {
            if (FrontierLiveManager.IsSignedInNow()) return true;
            Task<bool> pending = PlayerAuthentication.SignedIn;
            Task first = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(SignInWaitSeconds)));
            if (first == pending && pending.Result) return true;
            if (first != pending) return false;
            // The launch attempt failed (e.g. started in airplane mode): sign in again now that we're retrying.
            Task<bool> retry = PlayerAuthentication.RetrySignInAsync();
            first = await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(SignInWaitSeconds)));
            return first == retry && retry.Result;
        }

        private static bool TryGetAiProbeUrl(out string url)
        {
            url = null;
            var llm = FrontierLlmManager.Instance;
            if (llm == null || llm.Service == null || !llm.Service.Primary.RequiresNetwork) return false;
            string chat = llm.Config?.ChatCompletionsUrl;
            if (string.IsNullOrEmpty(chat)) return false;
            url = ConnectionMonitor.ModelsUrlFor(chat);
            return true;
        }

        /// <summary>
        /// Cheap reachability probe: one GET with no credentials and a short timeout. Any HTTP status counts as reachable.
        /// Reports its outcome to the monitor.
        /// </summary>
        public async Task<bool> ProbeAsync(string url = null)
        {
            if (url == null && !TryGetAiProbeUrl(out url)) url = FallbackProbeUrl;
            ProbeCount++;
            if (_simulatedOutage)
            {
                LastProbeResult = "simulated outage";
                Monitor.ReportFailure(ConnectionSource.Probe, LastProbeResult);
                return false;
            }

            IsProbing = true;
            try
            {
                using var req = UnityWebRequest.Get(url);
                req.timeout = ProbeTimeoutSeconds;
                var done = new TaskCompletionSource<bool>();
                req.SendWebRequest().completed += _ => done.TrySetResult(true);
                await done.Task;
                bool reached = ConnectionMonitor.IsServerReached(req.responseCode, timedOut: false);
                LastProbeResult = reached ? $"HTTP {req.responseCode}" : (req.error ?? "no response");
                if (reached) ReportSuccess(ConnectionSource.Probe);
                else Monitor.ReportFailure(ConnectionSource.Probe, LastProbeResult);
                return reached;
            }
            catch (Exception e)
            {
                LastProbeResult = e.Message;
                Monitor.ReportFailure(ConnectionSource.Probe, e.Message);
                return false;
            }
            finally
            {
                IsProbing = false;
            }
        }

        // ------------------------------------------------------------------ per-frame

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            _reachTimer += dt;
            if (_reachTimer >= ReachabilityInterval)
            {
                _reachTimer = 0f;
                bool up = !_simulatedOutage && Application.internetReachability != NetworkReachability.NotReachable;
                Monitor.ReportInterfaceReachable(up);
            }

            _tickTimer += dt;
            if (_tickTimer >= TickInterval)
            {
                _tickTimer = 0f;
                if (Monitor.Tick() && !IsProbing) _ = ProbeAsync();
            }

            if (Monitor.Version != _seenVersion) ApplyVisuals();

            var loop = FrontierGameLoop.Instance;
            if (loop != null && loop.ConnectionHold != Monitor.IsHoldingGame) loop.SetConnectionHold(Monitor.IsHoldingGame);

            if (_reconnectPanel != null && _reconnectPanel.activeSelf)
            {
                _dotsTimer += dt;
                if (_dotsTimer >= 0.4f)
                {
                    _dotsTimer = 0f;
                    _dots = (_dots + 1) % 4;
                    if (_reconnectDots != null) _reconnectDots.text = DotStrings[_dots];
                }
            }
        }

        // Hidden dots (TMP has no </alpha>; the tag runs to the end) keep the centred title from shifting as they animate (strings are built once, no per-frame alloc).
        private static readonly string[] DotStrings =
        {
            "RECONNECTING<alpha=#00>...",
            "RECONNECTING.<alpha=#00>..",
            "RECONNECTING..<alpha=#00>.",
            "RECONNECTING..."
        };

        private void ApplyVisuals()
        {
            _seenVersion = Monitor.Version;
            ConnectionState s = Monitor.State;
            bool gate = s == ConnectionState.Checking || s == ConnectionState.Blocked;
            bool reconnecting = s == ConnectionState.Reconnecting;
            if (_overlayRoot != null) _overlayRoot.SetActive(gate || reconnecting);
            if (_gatePanel != null) _gatePanel.SetActive(gate);
            if (_reconnectPanel != null) _reconnectPanel.SetActive(reconnecting);
            if (gate)
            {
                bool blocked = s == ConnectionState.Blocked;
                if (_gateTitle != null) _gateTitle.text = blocked ? "YOU'RE OFFLINE" : "CONNECTING...";
                if (_gateBody != null)
                    _gateBody.text = blocked
                        ? ConnectionMonitor.DescribeBlock(Monitor.BlockReason) + "\nOakhaven's villagers need the internet."
                        : "Checking your connection.";
                if (_retryButton != null) _retryButton.gameObject.SetActive(blocked);
            }
            Debug.Log($"[Net] {s}" + (s == ConnectionState.Degraded || s == ConnectionState.Reconnecting
                ? $" ({Monitor.LastFailureSource}: {Monitor.LastFailureReason})" : string.Empty));
        }

        private void HandleRetry()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            _ = RunStartupCheckAsync();
        }

        // ------------------------------------------------------------------ dev drills (Editor / development builds)

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Outage drill for Play-mode and device checks without touching the real network: the interface reads as
        /// down, probes fail and real successes are ignored, so short and long drops can be timed exactly.
        /// </summary>
        public void SimulateOutage(bool outage)
        {
            _simulatedOutage = outage;
            if (outage) Monitor.ReportInterfaceReachable(false);
            else _reachTimer = ReachabilityInterval; // re-read the real interface on the next frame
            Debug.Log($"[Net] Simulated outage {(outage ? "ON" : "OFF")}");
        }

        public bool SimulatingOutage => _simulatedOutage;
#endif

        // ------------------------------------------------------------------ overlay

        private void BuildOverlay()
        {
            _overlayRoot = new GameObject("ConnectionOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _overlayRoot.transform.SetParent(transform, false);
            var canvas = _overlayRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500; // above the shell (100) and its modals
            var scaler = _overlayRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // Full-screen dim that swallows every tap underneath.
            var dim = NewImage(_overlayRoot.transform, "Dim", FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.86f));
            Stretch(dim.rectTransform);
            dim.raycastTarget = true;

            // Startup gate panel.
            _gatePanel = NewPanel(_overlayRoot.transform, "GatePanel", new Vector2(640f, 300f));
            _gateTitle = NewText(_gatePanel.transform, "Title", "CONNECTING...", 40f, FrontierPalette.HearthGold, new Vector2(0f, 90f), new Vector2(600f, 60f));
            _gateBody = NewText(_gatePanel.transform, "Body", string.Empty, 22f, FrontierPalette.CreamParchment, new Vector2(0f, 10f), new Vector2(580f, 100f));
            _retryButton = NewButton(_gatePanel.transform, "RetryButton", "RETRY", new Vector2(0f, -90f), new Vector2(220f, 60f));
            _retryButton.onClick.AddListener(HandleRetry);

            // Long-drop panel (no buttons: it resumes on its own).
            _reconnectPanel = NewPanel(_overlayRoot.transform, "ReconnectPanel", new Vector2(560f, 200f));
            _reconnectDots = NewText(_reconnectPanel.transform, "Title", DotStrings[0], 40f, FrontierPalette.HearthGold, new Vector2(0f, 40f), new Vector2(520f, 60f));
            NewText(_reconnectPanel.transform, "Body", "Game paused. It resumes when you're back online.", 20f,
                FrontierPalette.CreamParchment, new Vector2(0f, -36f), new Vector2(520f, 60f));

            _gatePanel.SetActive(false);
            _reconnectPanel.SetActive(false);
            _overlayRoot.SetActive(true); // Checking state shows from the first frame
        }

        private TMP_FontAsset ShellFont(out Material material)
        {
            material = null;
            var shell = FindFirstObjectByType<FrontierShellUI>();
            TextMeshProUGUI sample = shell != null ? shell.titlePromptText : null;
            if (sample == null) return null;
            material = sample.fontSharedMaterial;
            return sample.font;
        }

        private static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static GameObject NewPanel(Transform parent, string name, Vector2 size)
        {
            var border = NewImage(parent, name, FrontierPalette.BrassBorder);
            border.rectTransform.sizeDelta = size + new Vector2(8f, 8f);
            border.raycastTarget = true;
            var fill = NewImage(border.transform, "Fill", FrontierPalette.SlatePanel);
            Stretch(fill.rectTransform, 4f);
            return border.gameObject;
        }

        private TextMeshProUGUI NewText(Transform parent, string name, string text, float size, Color color, Vector2 pos, Vector2 box)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            TMP_FontAsset font = ShellFont(out Material mat);
            if (font != null) t.font = font;
            if (mat != null) t.fontSharedMaterial = mat;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.raycastTarget = false;
            var rt = t.rectTransform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = box;
            return t;
        }

        private Button NewButton(Transform parent, string name, string label, Vector2 pos, Vector2 size)
        {
            var img = NewImage(parent, name, FrontierPalette.FrontierTeal);
            img.raycastTarget = true;
            img.rectTransform.anchoredPosition = pos;
            img.rectTransform.sizeDelta = size;
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            NewText(img.transform, "Label", label, 26f, FrontierPalette.CreamParchment, Vector2.zero, size);
            return btn;
        }

        private static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
