using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectFrontier
{
    public enum RunState
    {
        Ready,
        Running,
        Paused,
        Won,
        Lost
    }

    /// <summary>
    /// Single owner of ProjectFrontier's run state, goal progress, and dusk countdown pressure.
    /// Shell UI, Feel, Audio, Camera, and Live floors subscribe to StateChanged / ProgressChanged.
    /// </summary>
    public class FrontierGameLoop : MonoBehaviour
    {
        public static FrontierGameLoop Instance { get; private set; }

        [Header("Tunable Loop Parameters")]
        [SerializeField] private int goalLanterns = 5;
        [SerializeField] private float runDurationSeconds = 90f;
        [SerializeField] private bool enableDuskLoseTimer = false;

        public RunState State { get; private set; } = RunState.Ready;
        public int CollectedLanterns { get; private set; }
        public int GoalLanterns { get => goalLanterns; set => goalLanterns = Mathf.Max(1, value); }
        public float TimeRemaining { get; private set; }
        public float RunDurationSeconds { get => runDurationSeconds; set => runDurationSeconds = Mathf.Max(5f, value); }
        public int NpcsMetCount => _metNpcIds.Count;
        public bool DialogueOpen { get; private set; }
        /// <summary>
        /// Online-only (#37): true while the "Reconnecting..." overlay is up. Freezes time (day clock, timers, NPCs) and
        /// player input without changing <see cref="State"/>, so an open chat survives and resumes where it was.
        /// </summary>
        public bool ConnectionHold { get; private set; }
        public VillageNpc ActiveNpc => _activeNpc;

        public event Action<RunState> StateChanged;
        public event Action<int, int> ProgressChanged;
        public event Action<float, float> TimerChanged;
        public event Action<VillageNpc, int> DialogueUpdated;
        public event Action DialogueClosed;
        public event Action<bool> ConnectionHoldChanged;

        private readonly HashSet<string> _metNpcIds = new HashSet<string>();
        private VillageNpc _activeNpc;
        private int _dialogueLineIndex;

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
            Time.timeScale = 1f;
            CollectedLanterns = 0;
            TimeRemaining = runDurationSeconds;
            _metNpcIds.Clear();
            State = RunState.Ready;
        }

        private void Start()
        {
            StateChanged?.Invoke(State);
            ProgressChanged?.Invoke(CollectedLanterns, goalLanterns);
            TimerChanged?.Invoke(TimeRemaining, runDurationSeconds);
        }

        private void Update()
        {
            if (State != RunState.Running) return;
            if (DialogueOpen) return; // Pause timer kindly while reading NPC dialogue
            if (!enableDuskLoseTimer || FrontierDayCycleManager.Instance != null) return;

            TimeRemaining = Mathf.Max(0f, TimeRemaining - Time.deltaTime);
            TimerChanged?.Invoke(TimeRemaining, runDurationSeconds);
            if (TimeRemaining <= 0f)
            {
                Lose();
            }
        }

        public void StartRun()
        {
            if (State != RunState.Ready) return;
            // Online-only (#37): the startup gate must pass before gameplay starts.
            var net = FrontierConnectionManager.Instance;
            if (net != null && !net.CanStartGameplay) return;
            ApplyTimeScale();
            CollectedLanterns = 0;
            TimeRemaining = runDurationSeconds;
            _metNpcIds.Clear();
            CloseDialogue(completeInteraction: false);
            SetState(RunState.Running);
            ProgressChanged?.Invoke(CollectedLanterns, goalLanterns);
            TimerChanged?.Invoke(TimeRemaining, runDurationSeconds);
            SynthAudioManager.Instance?.PlayUiClick();
        }

        public void SetPaused(bool paused)
        {
            if (paused && State == RunState.Running)
            {
                SetState(RunState.Paused);
                ApplyTimeScale();
            }
            else if (!paused && State == RunState.Paused)
            {
                SetState(RunState.Running);
                ApplyTimeScale();
            }
        }

        /// <summary>Online-only (#37): raised/lowered by <see cref="FrontierConnectionManager"/> for long drops.</summary>
        public void SetConnectionHold(bool hold)
        {
            if (ConnectionHold == hold) return;
            ConnectionHold = hold;
            ApplyTimeScale();
            ConnectionHoldChanged?.Invoke(hold);
        }

        /// <summary>Time stops for the pause menu or a connection hold, and resumes only when neither applies.</summary>
        private void ApplyTimeScale()
        {
            bool frozen = State == RunState.Paused || ConnectionHold;
            Time.timeScale = frozen ? 0f : 1f;
            SynthAudioManager.Instance?.SetPaused(frozen);
        }

        public void TogglePause()
        {
            if (State == RunState.Running) SetPaused(true);
            else if (State == RunState.Paused) SetPaused(false);
        }

        public void CollectLantern(Vector3 worldPos, int amount = 1)
        {
            if (State != RunState.Running) return;
            CollectedLanterns = Mathf.Clamp(CollectedLanterns + amount, 0, goalLanterns);
            ProgressChanged?.Invoke(CollectedLanterns, goalLanterns);

            SynthAudioManager.Instance?.PlayPickup();
            FeelManager.Instance?.PlayPickupFeedback(worldPos, "+" + amount + " SHARD");

            if (CollectedLanterns >= goalLanterns)
            {
                Win();
            }
        }

        public void OpenOrAdvanceDialogue(VillageNpc npc)
        {
            if (State != RunState.Running || npc == null || ConnectionHold) return;

            if (!DialogueOpen || _activeNpc != npc)
            {
                _activeNpc = npc;
                _dialogueLineIndex = 0;
                DialogueOpen = true;
                _metNpcIds.Add(npc.NpcId);
                SynthAudioManager.Instance?.PlayUiClick();
                DialogueUpdated?.Invoke(_activeNpc, _dialogueLineIndex);
            }
            else
            {
                AdvanceDialogue();
            }
        }

        public void AdvanceDialogue()
        {
            if (!DialogueOpen || _activeNpc == null) return;
            SynthAudioManager.Instance?.PlayUiClick();
            _dialogueLineIndex++;
            if (_dialogueLineIndex >= _activeNpc.DialogueLines.Length)
            {
                CloseDialogue();
            }
            else
            {
                DialogueUpdated?.Invoke(_activeNpc, _dialogueLineIndex);
            }
        }

        public void CloseDialogue(bool completeInteraction = true)
        {
            if (!DialogueOpen && _activeNpc == null) return;
            VillageNpc finishedNpc = _activeNpc;
            bool shouldComplete = completeInteraction && State == RunState.Running;
            DialogueOpen = false;
            _activeNpc = null;
            _dialogueLineIndex = 0;
            DialogueClosed?.Invoke();
            if (shouldComplete && finishedNpc != null)
            {
                finishedNpc.OnDialogueCompleted(this);
            }
        }

        public void Win()
        {
            if (State != RunState.Running) return;
            CloseDialogue(completeInteraction: false);
            SetState(RunState.Won);
            SynthAudioManager.Instance?.PlayMilestone();
            FeelManager.Instance?.PlayMilestoneMoment("VILLAGE LIT!", FrontierPalette.HearthGold);
        }

        public void Lose()
        {
            if (State != RunState.Running) return;
            CloseDialogue(completeInteraction: false);
            SetState(RunState.Lost);
            SynthAudioManager.Instance?.PlayLose();
            FeelManager.Instance?.PlayMilestoneMoment("DUSK FELL", FrontierPalette.DuskAlert);
        }

        public void ForceSetProgress(int count)
        {
            if (State != RunState.Running) return;
            CollectedLanterns = Mathf.Clamp(count, 0, goalLanterns);
            ProgressChanged?.Invoke(CollectedLanterns, goalLanterns);
            if (CollectedLanterns >= goalLanterns) Win();
        }

        public void ForceSetTimeRemaining(float seconds)
        {
            if (State != RunState.Running) return;
            TimeRemaining = Mathf.Max(0f, seconds);
            TimerChanged?.Invoke(TimeRemaining, runDurationSeconds);
            if (TimeRemaining <= 0f) Lose();
        }

        public void RestartRun()
        {
            Time.timeScale = 1f;
            SynthAudioManager.Instance?.SetPaused(false);
            Scene active = SceneManager.GetActiveScene();
            SceneManager.LoadScene(active.buildIndex >= 0 ? active.buildIndex : 0);
        }

        public void ReturnToTitle()
        {
            RestartRun();
        }

        private void SetState(RunState next)
        {
            if (State == next) return;
            State = next;
            StateChanged?.Invoke(State);
        }
    }
}
