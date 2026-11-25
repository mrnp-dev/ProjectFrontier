using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ProjectFrontier
{
    /// <summary>
    /// Palette-styled uGUI Game Shell for ProjectFrontier:
    /// Title (over live village), Gameplay HUD (goal, animated bar, dusk timer, touch controls, NPC chatbox),
    /// Pause & Settings (Music/SFX sliders + Low/Medium/High/Custom Android Graphics Preset switcher),
    /// End Screen (Won/Lost stats + UGS Rank), and 0.3s unscaled fade transitions.
    /// </summary>
    public class FrontierShellUI : MonoBehaviour
    {
        [Header("Screen Roots")]
        public GameObject titleRoot;
        public GameObject hudRoot;
        public GameObject pauseRoot;
        public GameObject endRoot;
        public GameObject dialogueRoot;
        public GameObject customGraphicsGroup;

        [Header("Title Elements")]
        public TextMeshProUGUI titlePromptText;
        public UnityEngine.UI.Button titleStartButton;

        [Header("HUD Elements")]
        public TextMeshProUGUI goalText;
        public TextMeshProUGUI timerText;
        public UnityEngine.UI.Image progressFillImage;
        public TextMeshProUGUI presetBadgeText;
        public UnityEngine.UI.Button hudSettingsButton;
        public UnityEngine.UI.Button hudPauseButton;
        public UnityEngine.UI.Button hudInteractButton;
        public TextMeshProUGUI hudInteractLabel;

        [Header("Dialogue Chatbox")]
        public TextMeshProUGUI dialogueSpeakerText;
        public TextMeshProUGUI dialogueBodyText;
        public UnityEngine.UI.Button dialogueNextButton;
        public TextMeshProUGUI dialogueNextLabel;

        [Header("Pause & Settings Elements")]
        public UnityEngine.UI.Button resumeButton;
        public UnityEngine.UI.Button pauseRestartButton;
        public UnityEngine.UI.Button pauseTitleButton;
        public UnityEngine.UI.Slider musicSlider;
        public UnityEngine.UI.Slider sfxSlider;
        public UnityEngine.UI.Button presetLowButton;
        public UnityEngine.UI.Button presetMedButton;
        public UnityEngine.UI.Button presetHighButton;
        public UnityEngine.UI.Button presetCustomButton;
        public TextMeshProUGUI presetDetailsText;
        public UnityEngine.UI.Button customFpsButton;
        public TextMeshProUGUI customFpsLabel;
        public UnityEngine.UI.Button customLightsButton;
        public TextMeshProUGUI customLightsLabel;
        public UnityEngine.UI.Button customPostFxButton;
        public TextMeshProUGUI customPostFxLabel;
        public UnityEngine.UI.Button customParticlesButton;
        public TextMeshProUGUI customParticlesLabel;

        [Header("End Screen Elements")]
        public TextMeshProUGUI endTitleText;
        public TextMeshProUGUI endStatsText;
        public UnityEngine.UI.Button endRestartButton;
        public UnityEngine.UI.Button endTitleButton;
        [SerializeField] private float endScreenRevealDelay = 1.45f;

        [Header("Transition Fade")]
        public UnityEngine.UI.Image fadeOverlay;

        private Coroutine _barTween;
        private Coroutine _fadeTween;
        private Coroutine _endRevealRoutine;
        private float _displayedFill;

        private void Start()
        {
            WireButtons();
            BindEvents();
            SyncAudioSliders();
            RefreshGraphicsUi();
            ApplyStateVisibility(FrontierGameLoop.Instance != null ? FrontierGameLoop.Instance.State : RunState.Ready, instant: true);
        }

        private void OnDestroy()
        {
            if (FrontierGameLoop.Instance != null)
            {
                FrontierGameLoop.Instance.StateChanged -= HandleStateChanged;
                FrontierGameLoop.Instance.ProgressChanged -= HandleProgressChanged;
                FrontierGameLoop.Instance.TimerChanged -= HandleTimerChanged;
                FrontierGameLoop.Instance.DialogueUpdated -= HandleDialogueUpdated;
                FrontierGameLoop.Instance.DialogueClosed -= HandleDialogueClosed;
            }
            GraphicsSettingsManager.OnGraphicsChanged -= HandleGraphicsChanged;
            if (FrontierLiveManager.Instance != null)
            {
                FrontierLiveManager.Instance.LiveStateUpdated -= RefreshEndStats;
            }
        }

        private void Update()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null) return;

            // Title prompt pulse (0.5 <-> 1.0 over 1.6s on unscaled time) + press any key to start
            if (loop.State == RunState.Ready)
            {
                if (titlePromptText != null)
                {
                    float alpha = Mathf.Lerp(0.5f, 1f, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * (Mathf.PI * 2f / 1.6f)));
                    titlePromptText.color = FrontierPalette.WithAlpha(FrontierPalette.CreamParchment, alpha);
                }
                if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame)
                {
                    loop.StartRun();
                }
            }
            else if (loop.State == RunState.Running)
            {
                UpdateInteractButtonState();
            }
        }

        private void WireButtons()
        {
            if (titleStartButton != null) titleStartButton.onClick.AddListener(() => FrontierGameLoop.Instance?.StartRun());
            if (hudPauseButton != null) hudPauseButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.SetPaused(true); });
            if (hudSettingsButton != null) hudSettingsButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.SetPaused(true); });
            if (hudInteractButton != null) hudInteractButton.onClick.AddListener(() => PlayerController2D.Instance?.TryInteract());
            if (dialogueNextButton != null) dialogueNextButton.onClick.AddListener(() => FrontierGameLoop.Instance?.AdvanceDialogue());

            if (resumeButton != null) resumeButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.SetPaused(false); });
            if (pauseRestartButton != null) pauseRestartButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.RestartRun(); });
            if (pauseTitleButton != null) pauseTitleButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.ReturnToTitle(); });
            if (endRestartButton != null) endRestartButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.RestartRun(); });
            if (endTitleButton != null) endTitleButton.onClick.AddListener(() => { SynthAudioManager.Instance?.PlayUiClick(); FrontierGameLoop.Instance?.ReturnToTitle(); });

            if (presetLowButton != null) presetLowButton.onClick.AddListener(() => SelectPreset(GraphicsPresetTier.Low));
            if (presetMedButton != null) presetMedButton.onClick.AddListener(() => SelectPreset(GraphicsPresetTier.Medium));
            if (presetHighButton != null) presetHighButton.onClick.AddListener(() => SelectPreset(GraphicsPresetTier.High));
            if (presetCustomButton != null) presetCustomButton.onClick.AddListener(() => SelectCustomPreset());

            if (customFpsButton != null) customFpsButton.onClick.AddListener(ToggleCustomFps);
            if (customLightsButton != null) customLightsButton.onClick.AddListener(ToggleCustomLights);
            if (customPostFxButton != null) customPostFxButton.onClick.AddListener(ToggleCustomPostFx);
            if (customParticlesButton != null) customParticlesButton.onClick.AddListener(CycleCustomParticles);

            if (musicSlider != null)
            {
                musicSlider.onValueChanged.AddListener(v =>
                {
                    if (SynthAudioManager.Instance != null) SynthAudioManager.Instance.MusicVolume = v;
                });
            }
            if (sfxSlider != null)
            {
                sfxSlider.onValueChanged.AddListener(v =>
                {
                    if (SynthAudioManager.Instance != null) SynthAudioManager.Instance.SfxVolume = v;
                });
            }
        }

        private void BindEvents()
        {
            if (FrontierGameLoop.Instance != null)
            {
                FrontierGameLoop.Instance.StateChanged += HandleStateChanged;
                FrontierGameLoop.Instance.ProgressChanged += HandleProgressChanged;
                FrontierGameLoop.Instance.TimerChanged += HandleTimerChanged;
                FrontierGameLoop.Instance.DialogueUpdated += HandleDialogueUpdated;
                FrontierGameLoop.Instance.DialogueClosed += HandleDialogueClosed;
            }
            GraphicsSettingsManager.OnGraphicsChanged += HandleGraphicsChanged;
            if (FrontierLiveManager.Instance != null)
            {
                FrontierLiveManager.Instance.LiveStateUpdated += RefreshEndStats;
            }
        }

        private void SyncAudioSliders()
        {
            if (SynthAudioManager.Instance == null) return;
            if (musicSlider != null) musicSlider.SetValueWithoutNotify(SynthAudioManager.Instance.MusicVolume);
            if (sfxSlider != null) sfxSlider.SetValueWithoutNotify(SynthAudioManager.Instance.SfxVolume);
        }

        private void SelectPreset(GraphicsPresetTier tier)
        {
            SynthAudioManager.Instance?.PlayUiClick();
            GraphicsSettingsManager.Instance?.SetPreset(tier);
        }

        private void SelectCustomPreset()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            var gfx = GraphicsSettingsManager.Instance;
            if (gfx == null) return;
            var c = gfx.CurrentConfig;
            gfx.SetCustom(c.targetFps, c.dynamicLights2D, c.postProcessing, c.particleDensity);
        }

        private void ToggleCustomFps()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            var gfx = GraphicsSettingsManager.Instance;
            if (gfx == null) return;
            var c = gfx.CurrentConfig;
            gfx.SetCustom(c.targetFps >= 60 ? 30 : 60, c.dynamicLights2D, c.postProcessing, c.particleDensity);
        }

        private void ToggleCustomLights()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            var gfx = GraphicsSettingsManager.Instance;
            if (gfx == null) return;
            var c = gfx.CurrentConfig;
            gfx.SetCustom(c.targetFps, !c.dynamicLights2D, c.postProcessing, c.particleDensity);
        }

        private void ToggleCustomPostFx()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            var gfx = GraphicsSettingsManager.Instance;
            if (gfx == null) return;
            var c = gfx.CurrentConfig;
            gfx.SetCustom(c.targetFps, c.dynamicLights2D, !c.postProcessing, c.particleDensity);
        }

        private void CycleCustomParticles()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            var gfx = GraphicsSettingsManager.Instance;
            if (gfx == null) return;
            var c = gfx.CurrentConfig;
            float nextDensity = c.particleDensity < 0.45f ? 0.65f : (c.particleDensity < 0.85f ? 1.0f : 0.25f);
            gfx.SetCustom(c.targetFps, c.dynamicLights2D, c.postProcessing, nextDensity);
        }

        private void HandleGraphicsChanged(GraphicsConfigData cfg)
        {
            RefreshGraphicsUi();
        }

        private void RefreshGraphicsUi()
        {
            var gfx = GraphicsSettingsManager.Instance;
            if (gfx == null) return;
            var cfg = gfx.CurrentConfig;
            string label = gfx.GetShortTierLabel();
            int particlePct = Mathf.RoundToInt(cfg.particleDensity * 100f);

            if (presetBadgeText != null) presetBadgeText.text = $"GFX: {label}";
            if (presetDetailsText != null)
            {
                presetDetailsText.text = $"{label} · {cfg.targetFps} FPS · 2D Lights: {(cfg.dynamicLights2D ? "ON" : "OFF")} · PostFX: {(cfg.postProcessing ? "ON" : "OFF")} · FX: {particlePct}%";
            }

            TintPresetButton(presetLowButton, gfx.CurrentTier == GraphicsPresetTier.Low);
            TintPresetButton(presetMedButton, gfx.CurrentTier == GraphicsPresetTier.Medium);
            TintPresetButton(presetHighButton, gfx.CurrentTier == GraphicsPresetTier.High);
            TintPresetButton(presetCustomButton, gfx.CurrentTier == GraphicsPresetTier.Custom);

            if (customGraphicsGroup != null)
            {
                customGraphicsGroup.SetActive(gfx.CurrentTier == GraphicsPresetTier.Custom);
            }
            if (customFpsLabel != null) customFpsLabel.text = $"FPS: {cfg.targetFps}";
            if (customLightsLabel != null) customLightsLabel.text = $"2D LIGHTS: {(cfg.dynamicLights2D ? "ON" : "OFF")}";
            if (customPostFxLabel != null) customPostFxLabel.text = $"POST-FX: {(cfg.postProcessing ? "ON" : "OFF")}";
            if (customParticlesLabel != null) customParticlesLabel.text = $"PARTICLES: {particlePct}%";
        }

        private static void TintPresetButton(UnityEngine.UI.Button btn, bool selected)
        {
            if (btn == null || btn.targetGraphic == null) return;
            btn.targetGraphic.color = selected ? FrontierPalette.FrontierTeal : FrontierPalette.SlatePanel;
        }

        private void HandleStateChanged(RunState state)
        {
            ApplyStateVisibility(state, instant: false);
            if (state == RunState.Won || state == RunState.Lost)
            {
                RefreshEndStats();
            }
        }

        private void ApplyStateVisibility(RunState state, bool instant)
        {
            if (_endRevealRoutine != null)
            {
                StopCoroutine(_endRevealRoutine);
                _endRevealRoutine = null;
            }

            bool isEndState = state == RunState.Won || state == RunState.Lost;
            bool deferEndReveal = isEndState && !instant && endScreenRevealDelay > 0.01f;

            if (titleRoot != null) titleRoot.SetActive(state == RunState.Ready);
            if (hudRoot != null) hudRoot.SetActive(state == RunState.Running);
            if (pauseRoot != null) pauseRoot.SetActive(state == RunState.Paused);
            if (endRoot != null) endRoot.SetActive(isEndState && !deferEndReveal);
            if (dialogueRoot != null && state != RunState.Running) dialogueRoot.SetActive(false);
            if (state == RunState.Ready || state == RunState.Paused) FeelManager.Instance?.HideTransientOverlays();

            if (deferEndReveal)
            {
                _endRevealRoutine = StartCoroutine(RevealEndScreenAfterBanner(state));
                return;
            }

            if (!instant && fadeOverlay != null)
            {
                if (_fadeTween != null) StopCoroutine(_fadeTween);
                _fadeTween = StartCoroutine(PlayTransitionFade());
            }
        }

        private IEnumerator RevealEndScreenAfterBanner(RunState expectedState)
        {
            yield return new WaitForSecondsRealtime(endScreenRevealDelay);
            _endRevealRoutine = null;
            var loop = FrontierGameLoop.Instance;
            if (loop != null && loop.State != expectedState) yield break;

            if (endRoot != null) endRoot.SetActive(true);
            RefreshEndStats();
            if (fadeOverlay != null)
            {
                if (_fadeTween != null) StopCoroutine(_fadeTween);
                _fadeTween = StartCoroutine(PlayTransitionFade());
            }
        }

        private IEnumerator PlayTransitionFade()
        {
            fadeOverlay.gameObject.SetActive(true);
            fadeOverlay.color = FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.72f);
            yield return Ease.Tween(0.30f, t =>
            {
                fadeOverlay.color = FrontierPalette.WithAlpha(FrontierPalette.InkDark, Mathf.Lerp(0.72f, 0f, t));
            }, Ease.OutCubic, unscaled: true);
            fadeOverlay.gameObject.SetActive(false);
            _fadeTween = null;
        }

        private void HandleProgressChanged(int collected, int goal)
        {
            if (goalText != null)
            {
                goalText.text = $"LANTERNS {collected}/{goal} · LIGHT THE HEARTH";
            }
            if (progressFillImage != null)
            {
                float target = goal > 0 ? Mathf.Clamp01((float)collected / goal) : 0f;
                if (_barTween != null) StopCoroutine(_barTween);
                _barTween = StartCoroutine(TweenBarFill(target));
            }
        }

        private IEnumerator TweenBarFill(float target)
        {
            float start = _displayedFill;
            yield return Ease.Tween(0.30f, t =>
            {
                _displayedFill = Mathf.Lerp(start, target, t);
                if (progressFillImage != null) progressFillImage.fillAmount = _displayedFill;
            }, Ease.OutCubic, unscaled: true);
            _barTween = null;
        }

        private void HandleTimerChanged(float remaining, float total)
        {
            if (timerText == null) return;
            int secs = Mathf.CeilToInt(remaining);
            int mins = secs / 60;
            int rem = secs % 60;
            timerText.text = $"DUSK {mins:00}:{rem:00}";
            timerText.color = remaining <= 15f ? FrontierPalette.DuskAlert : FrontierPalette.CreamParchment;
        }

        private void HandleDialogueUpdated(VillageNpc npc, int lineIndex)
        {
            if (dialogueRoot != null) dialogueRoot.SetActive(true);
            if (dialogueSpeakerText != null) dialogueSpeakerText.text = $"{npc.DisplayName.ToUpperInvariant()} · {npc.RoleTitle}";
            if (dialogueBodyText != null && lineIndex >= 0 && lineIndex < npc.DialogueLines.Length)
            {
                dialogueBodyText.text = npc.DialogueLines[lineIndex];
            }
            if (dialogueNextLabel != null)
            {
                dialogueNextLabel.text = lineIndex >= npc.DialogueLines.Length - 1 ? "CLOSE" : "NEXT";
            }
        }

        private void HandleDialogueClosed()
        {
            if (dialogueRoot != null) dialogueRoot.SetActive(false);
        }

        private void UpdateInteractButtonState()
        {
            if (hudInteractButton == null) return;
            var player = PlayerController2D.Instance;
            var loop = FrontierGameLoop.Instance;
            bool dlgOpen = loop != null && loop.DialogueOpen;
            bool nearNpc = player != null && player.NearestNpc != null;
            bool showBtn = !dlgOpen && nearNpc;
            if (hudInteractButton.gameObject.activeSelf != showBtn)
            {
                hudInteractButton.gameObject.SetActive(showBtn);
            }
            hudInteractButton.interactable = showBtn;
            if (hudInteractButton.targetGraphic != null)
            {
                hudInteractButton.targetGraphic.color = FrontierPalette.FrontierTeal;
            }
            if (hudInteractLabel != null)
            {
                hudInteractLabel.text = "TALK";
            }
        }

        private void RefreshEndStats()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null) return;
            bool won = loop.State == RunState.Won;
            if (endTitleText != null)
            {
                endTitleText.text = won ? "OAKHAVEN HEARTH LIT!" : "DUSK FELL OVER OAKHAVEN";
                endTitleText.color = won ? FrontierPalette.HearthGold : FrontierPalette.DuskAlert;
            }
            if (endStatsText != null)
            {
                var live = FrontierLiveManager.Instance;
                string rank = live != null ? live.LastRankLabel : "Rank: unavailable";
                string saveNote = live != null ? live.LastSaveStatus : "Local cache";
                endStatsText.text =
                    $"LANTERNS: {loop.CollectedLanterns}/{loop.GoalLanterns}\n" +
                    $"TIME LEFT: {Mathf.CeilToInt(loop.TimeRemaining)}S · VILLAGERS MET: {loop.NpcsMetCount}\n" +
                    $"{rank.ToUpperInvariant()}\n" +
                    $"SAVE: {saveNote.ToUpperInvariant()}";
            }
        }
    }
}
