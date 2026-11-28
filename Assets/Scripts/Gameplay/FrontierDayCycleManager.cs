using System;
using System.Collections;
using System.Collections.Generic;
using ProjectFrontier.World;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ProjectFrontier
{
    /// <summary>
    /// Scene owner of Oakhaven's day/night cycle, Global Light2D colour grading,
    /// sleep -> next-day transition, obstacle NavGrid2D, and frame-budgeted NPC navigation queue.
    /// Strictly respects GraphicsSettingsManager: zero extra point lights are enabled on Low tier.
    /// </summary>
    public class FrontierDayCycleManager : MonoBehaviour
    {
        public static FrontierDayCycleManager Instance { get; private set; }

        [Header("Clock Settings")]
        [SerializeField] private float dayDurationSeconds = DayClock.DefaultDayDurationSeconds;
        [SerializeField] private bool pauseDuringDialogue = true;
        [SerializeField] private bool autoSleepAtMidnight = true;
        [SerializeField] private float sleepFadeOutSeconds = 0.32f;
        [SerializeField] private float sleepHoldSeconds = 0.30f;

        [Header("Frame-Budgeted Navigation")]
        [SerializeField] private float obstaclePadding = 0.22f;
        [SerializeField] private int maxPathRequestsPerFrame = FrameBudgetedNavQueue.DefaultMaxRequestsPerFrame;
        [SerializeField] private int maxNodeExpansionsPerFrame = FrameBudgetedNavQueue.DefaultMaxNodeExpansionsPerFrame;

        public DayClock Clock => _clock;
        public NavGrid2D NavGrid => _navGrid;
        public FrameBudgetedNavQueue NavQueue => _navQueue;
        public int CurrentDay => _clock != null ? _clock.Day : 1;
        public DayPhase CurrentPhase => _clock != null ? _clock.CurrentPhase : DayPhase.Morning;
        public float NormalizedTime => _clock != null ? _clock.NormalizedTime : 0f;
        public bool IsSleeping => _sleepRoutine != null;
        public LightGrade CurrentLightGrade => _lastLightGrade;
        public Light2D GlobalLight => _globalLight;

        public event Action<DayPhase, DayPhase> PhaseChanged;
        public event Action<int> DayAdvanced;
        public event Action<bool> SleepTransitionChanged;

        private DayClock _clock;
        private NavGrid2D _navGrid;
        private FlatAStarPathfinder _pathfinder;
        private FrameBudgetedNavQueue _navQueue;

        private Light2D _globalLight;
        private Light2D[] _pointLights = Array.Empty<Light2D>();
        private float[] _pointLightBaseIntensities = Array.Empty<float>();
        private LightGrade _lastLightGrade;
        private Coroutine _sleepRoutine;

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

            _clock = new DayClock(dayDurationSeconds, 1);
            _clock.PhaseChanged += HandleClockPhaseChanged;
            _clock.DayAdvanced += HandleClockDayAdvanced;
            _clock.MidnightReached += HandleMidnightReached;

            _navGrid = NavGrid2D.CreateOakhavenDefault();
            _pathfinder = new FlatAStarPathfinder(_navGrid);
            _navQueue = new FrameBudgetedNavQueue(_pathfinder, maxPathRequestsPerFrame, maxNodeExpansionsPerFrame);
        }

        private void Start()
        {
            if (FrontierSocialManager.Instance != null)
            {
                _clock.SyncDay(FrontierSocialManager.Instance.State.Day);
            }

            CacheSceneLights();
            RebuildNavGrid();
            GraphicsSettingsManager.OnGraphicsChanged += HandleGraphicsChanged;

            ApplyLightingNow();
            SyncNpcSchedules(snapImmediate: true);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            GraphicsSettingsManager.OnGraphicsChanged -= HandleGraphicsChanged;
            if (_clock != null)
            {
                _clock.PhaseChanged -= HandleClockPhaseChanged;
                _clock.DayAdvanced -= HandleClockDayAdvanced;
                _clock.MidnightReached -= HandleMidnightReached;
            }
            Instance = null;
        }

        private void Update()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop != null && loop.State != RunState.Running) return;

            bool blockedByDialogue = pauseDuringDialogue && ((loop != null && loop.DialogueOpen) || FrontierFarmUI.IsAnyModalOpen);
            if (!blockedByDialogue && !IsSleeping)
            {
                _clock.Tick(Time.deltaTime);
                ApplyLightingNow();
            }

            if (!IsSleeping)
            {
                _navQueue.MaxRequestsPerFrame = Mathf.Max(1, maxPathRequestsPerFrame);
                _navQueue.MaxNodeExpansionsPerFrame = Mathf.Max(16, maxNodeExpansionsPerFrame);
                _navQueue.ProcessFrameBudget();
            }
        }

        // ------------------------------------------------------------------ public API

        public void SetPhase(DayPhase phase, bool snapNpcs = false)
        {
            _clock.SetPhase(phase);
            ApplyLightingNow();
            SyncNpcSchedules(snapImmediate: snapNpcs);
        }

        public void SetNormalizedTime(float normalized, bool snapNpcs = false)
        {
            _clock.SetNormalizedTime(normalized);
            ApplyLightingNow();
            if (snapNpcs) SyncNpcSchedules(snapImmediate: true);
        }

        public void RequestSleep(bool immediate = false)
        {
            if (IsSleeping) return;
            var loop = FrontierGameLoop.Instance;
            if (loop != null && loop.State != RunState.Running && loop.State != RunState.Ready) return;

            loop?.CloseDialogue(completeInteraction: false);

            if (immediate || !gameObject.activeInHierarchy)
            {
                PerformDayRollover();
                return;
            }

            _sleepRoutine = StartCoroutine(SleepRoutine());
        }

        public void RequestNpcPath(string requesterId, Vector2 start, Vector2 goal, List<Vector2> outBuffer, Action<bool> onComplete)
        {
            _navQueue?.Enqueue(requesterId, start, goal, outBuffer, onComplete);
        }

        public void CancelNpcPath(string requesterId)
        {
            _navQueue?.Cancel(requesterId);
        }

        public void SyncNpcSchedules(bool snapImmediate = false)
        {
            var npcs = VillageNpc.AllNpcs;
            DayPhase phase = CurrentPhase;
            int day = CurrentDay;
            for (int i = 0; i < npcs.Count; i++)
            {
                VillageNpc npc = npcs[i];
                if (npc == null || !npc.isActiveAndEnabled) continue;
                NpcScheduleAgent agent = npc.ScheduleAgent;
                if (agent == null) agent = npc.gameObject.AddComponent<NpcScheduleAgent>();
                agent.AssignSchedule(phase, day, snapImmediate);
            }
        }

        public void RebuildNavGrid()
        {
            _navGrid.ClearAll();

            // Prefer static prop colliders under VillageProps; fall back to all non-trigger BoxCollider2Ds.
            GameObject propsRoot = GameObject.Find("VillageProps");
            BoxCollider2D[] boxes = propsRoot != null
                ? propsRoot.GetComponentsInChildren<BoxCollider2D>(includeInactive: false)
                : FindObjectsByType<BoxCollider2D>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < boxes.Length; i++)
            {
                BoxCollider2D box = boxes[i];
                if (box == null || !box.enabled || box.isTrigger) continue;
                if (box.GetComponent<PlayerController2D>() != null || box.GetComponent<VillageNpc>() != null) continue;
                Bounds b = box.bounds;
                _navGrid.MarkBlockedRect(b.min.x, b.min.y, b.max.x, b.max.y, obstaclePadding);
            }
        }

        /// <summary>Returns the number of currently enabled non-global Point Light2Ds in the scene.</summary>
        public int GetActivePointLightCount()
        {
            int count = 0;
            for (int i = 0; i < _pointLights.Length; i++)
            {
                if (_pointLights[i] != null && _pointLights[i].enabled && _pointLights[i].gameObject.activeInHierarchy)
                    count++;
            }
            return count;
        }

        // ------------------------------------------------------------------ internals

        private void CacheSceneLights()
        {
            Light2D[] all = FindObjectsByType<Light2D>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var points = new List<Light2D>(all.Length);
            _globalLight = null;

            for (int i = 0; i < all.Length; i++)
            {
                Light2D l = all[i];
                if (l == null) continue;
                if (l.lightType == Light2D.LightType.Global)
                {
                    if (_globalLight == null) _globalLight = l;
                }
                else
                {
                    points.Add(l);
                }
            }

            _pointLights = points.ToArray();
            _pointLightBaseIntensities = new float[_pointLights.Length];
            for (int i = 0; i < _pointLights.Length; i++)
            {
                _pointLightBaseIntensities[i] = _pointLights[i].intensity;
            }
        }

        private void ApplyLightingNow()
        {
            _lastLightGrade = DayLightingGrading.Evaluate(_clock.NormalizedTime);

            if (_globalLight != null)
            {
                _globalLight.color = _lastLightGrade.Color;
                _globalLight.intensity = _lastLightGrade.Intensity;
            }

            // Low graphics preset disables dynamicLights2D: NEVER enable or add point lights on Low.
            bool allowDynamicPoints = GraphicsSettingsManager.Instance != null
                && GraphicsSettingsManager.Instance.CurrentConfig.dynamicLights2D;

            for (int i = 0; i < _pointLights.Length; i++)
            {
                Light2D l = _pointLights[i];
                if (l == null) continue;
                if (!allowDynamicPoints)
                {
                    if (l.enabled) l.enabled = false;
                }
                else if (l.enabled)
                {
                    l.intensity = _pointLightBaseIntensities[i] * _lastLightGrade.PointLightScale;
                }
            }
        }

        private void HandleGraphicsChanged(GraphicsConfigData cfg)
        {
            ApplyLightingNow();
        }

        private void HandleClockPhaseChanged(DayPhase prev, DayPhase next)
        {
            ApplyLightingNow();
            if (!IsSleeping)
            {
                SyncNpcSchedules(snapImmediate: false);
            }
            PhaseChanged?.Invoke(prev, next);
        }

        private void HandleClockDayAdvanced(int newDay)
        {
            DayAdvanced?.Invoke(newDay);
        }

        private void HandleMidnightReached()
        {
            if (autoSleepAtMidnight && !IsSleeping)
            {
                RequestSleep(immediate: false);
            }
        }

        private IEnumerator SleepRoutine()
        {
            SleepTransitionChanged?.Invoke(true);
            SynthAudioManager.Instance?.PlayUiClick();

            if (sleepFadeOutSeconds > 0f)
                yield return new WaitForSecondsRealtime(sleepFadeOutSeconds);

            PerformDayRollover();

            if (sleepHoldSeconds > 0f)
                yield return new WaitForSecondsRealtime(sleepHoldSeconds);

            _sleepRoutine = null;
            SleepTransitionChanged?.Invoke(false);
            FeelManager.Instance?.PlayMilestoneMoment("DAY " + CurrentDay + " · MORNING", FrontierPalette.HearthGold);
        }

        private void PerformDayRollover()
        {
            _navQueue?.Clear();

            int? syncedDay = null;
            if (FrontierSocialManager.Instance != null)
            {
                FrontierSocialManager.Instance.AdvanceDay();
                syncedDay = FrontierSocialManager.Instance.State.Day;
            }

            _clock.SleepToNextDay(syncedDay);
            ApplyLightingNow();
            SyncNpcSchedules(snapImmediate: true);
        }
    }
}
