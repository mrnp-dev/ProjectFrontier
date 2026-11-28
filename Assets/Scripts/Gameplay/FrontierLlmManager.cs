using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProjectFrontier.Llm;
using ProjectFrontier.Social;
using ProjectFrontier.World;
using Unity.Services.Authentication;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Scene owner of the Layer 1 chat pipeline: picks the provider from local config, keeps the last few
    /// messages per conversation, builds the prompt from persona + standing + rumor, and applies the model's
    /// deltas through <see cref="FrontierSocialManager"/> (clamped as <see cref="DeltaSource.LlmTurn"/>).
    /// The chat UI (#14) calls <see cref="SendPlayerMessageAsync"/>.
    /// </summary>
    public class FrontierLlmManager : MonoBehaviour
    {
        public static FrontierLlmManager Instance { get; private set; }

        public const string EditorConfigRelativePath = "UserSettings/FrontierLlm.json";
        public const string DeviceConfigFileName = "frontier_llm.json";
        public const string DotEnvFileName = ".env";

        [Tooltip("Messages kept per conversation (player + NPC lines). The prompt replays the last 4; older NPC lines " +
                 "are listed as \"don't repeat\" (#41).")]
        public int storedHistoryMessages = 10;

        public LlmTurnService Service { get; private set; }
        public LlmEndpointConfig Config { get; private set; }
        public bool IsOnlineProvider => Service != null && Service.Primary != Service.Offline;
        public string ProviderLabel => Service?.Primary.Name ?? MockLlmProvider.ProviderName;

        public event Action<LlmTurnResult> TurnCompleted;

        private readonly TurnPromptBuilder _builder = new TurnPromptBuilder();
        private readonly Dictionary<string, List<ChatMessage>> _history = new Dictionary<string, List<ChatMessage>>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private CancellationTokenSource _lifetime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _lifetime = new CancellationTokenSource();
            Configure(LlmEndpointConfig.Load(ConfigPath, DotEnvPath));
        }

        private void Start()
        {
            if (FrontierGameLoop.Instance != null) FrontierGameLoop.Instance.DialogueClosed += HandleDialogueClosed;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (FrontierGameLoop.Instance != null) FrontierGameLoop.Instance.DialogueClosed -= HandleDialogueClosed;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            Instance = null;
        }

        public static string ConfigPath =>
#if UNITY_EDITOR
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", EditorConfigRelativePath);
#else
            Path.Combine(Application.persistentDataPath, DeviceConfigFileName);
#endif

        /// <summary>Project-root .env, read in the Editor only (it never exists on a device).</summary>
        public static string DotEnvPath =>
#if UNITY_EDITOR
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", DotEnvFileName);
#else
            null;
#endif

        /// <summary>(Re)builds the provider stack. Public so dev tools and verification scripts can swap endpoints.</summary>
        public void Configure(LlmEndpointConfig config)
        {
            Config = config ?? new LlmEndpointConfig();
            var offline = new MockLlmProvider { SimulatedLatencyMs = Mathf.Max(0, Config.offlineLatencyMs) };
            ILLMProvider primary = offline;

            switch (Config.Mode)
            {
                case LlmMode.DirectDev:
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (!Config.HasEndpoint)
                        Debug.LogWarning("[LLM] DirectDev needs FRONTIER_LLM_BASE_URL; using built-in lines.");
                    else if (string.IsNullOrWhiteSpace(Config.apiKey) && !Config.IsLocalEndpoint)
                        Debug.LogWarning("[LLM] DirectDev needs FRONTIER_LLM_API_KEY for a remote endpoint; using built-in lines.");
                    else
                        primary = new OpenAiCompatibleProvider(Config, () => Config.apiKey);
#else
                    Debug.LogWarning("[LLM] DirectDev mode is disabled in release builds; using built-in lines.");
#endif
                    break;
                case LlmMode.Proxy:
                    // The proxy holds the real key; the client only proves who it is with its UGS session token.
                    if (Config.HasEndpoint) primary = new OpenAiCompatibleProvider(Config, PlayerAccessToken);
                    break;
            }

            _builder.MaxOutputTokens = Mathf.Clamp(Config.maxTokens, 32, TurnPromptBuilder.DefaultMaxOutputTokens);
            // Online-only (#37): the connection monitor decides when to skip the network and answer from built-in lines.
            Service = new LlmTurnService(primary, offline, Config.timeoutSeconds, () => FrontierConnectionManager.AllowsNetworkCalls);
            Debug.Log($"[LLM] Provider: {primary.Name} ({Config})");
        }

        private static string PlayerAccessToken()
        {
            try
            {
                return FrontierLiveManager.IsSignedInNow() ? AuthenticationService.Instance.AccessToken : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool IsBusy(string npcId) => _inFlight.Contains(npcId);

        public IReadOnlyList<ChatMessage> GetHistory(string npcId) =>
            _history.TryGetValue(npcId, out var h) ? h : (IReadOnlyList<ChatMessage>)Array.Empty<ChatMessage>();

        public void ClearAllHistory()
        {
            _history.Clear();
            _transcripts.Clear();
        }

        /// <summary>The whole current conversation with a villager, as the memory extractor will see it (#45).</summary>
        public IReadOnlyList<ChatMessage> GetTranscript(string npcId) =>
            _transcripts.TryGetValue(npcId, out var t) ? t : (IReadOnlyList<ChatMessage>)Array.Empty<ChatMessage>();

        [Header("Long-term memory (#42)")]
        [Tooltip("After a conversation closes, one small LLM call extracts 0-3 facts the villager keeps across days.")]
        public bool extractMemories = true;
        [Tooltip("Recalled memories added to each chat prompt (top N by relevance, recency and importance).")]
        public int recallMemories = NpcMemoryStore.DefaultRecallCount;

        /// <summary>Raised when an extraction finishes (also when it remembered nothing). For logs, tests and the PR demo.</summary>
        public event Action<string, MemoryExtractionResult> MemoriesExtracted;

        /// <summary>The memories recalled for the most recent turn per villager (debug/verification).</summary>
        public IReadOnlyList<MemoryFact> LastRecalled(string npcId) =>
            _lastRecalled.TryGetValue(npcId, out var l) ? l : (IReadOnlyList<MemoryFact>)Array.Empty<MemoryFact>();

        private readonly Dictionary<string, List<MemoryFact>> _lastRecalled = new Dictionary<string, List<MemoryFact>>();
        private readonly Dictionary<string, string> _lastMood = new Dictionary<string, string>();

        /// <summary>Conversation over: extract memories from each chat (async, best effort), then forget the short-term history.</summary>
        private void HandleDialogueClosed()
        {
            var finished = new List<KeyValuePair<string, List<ChatMessage>>>(_transcripts);
            ClearAllHistory();
            if (!extractMemories) return;
            int day = CurrentDay();
            foreach (var kv in finished)
            {
                _lastMood.TryGetValue(kv.Key, out string mood);
                _ = RunPendingSafelyAsync(new PendingExtraction { NpcId = kv.Key, Conversation = kv.Value, Day = day, Mood = mood }, false);
            }
        }

        [Tooltip("Whole-conversation transcript kept per villager for memory extraction (#45). The opening exchange is " +
                 "always kept; the extractor fits the rest into its 1200-character cap.")]
        public int storedTranscriptMessages = 80;
        private const int TranscriptOpeningMessages = 2;
        private const float DrainCheckSeconds = 1f;

        private readonly Dictionary<string, List<ChatMessage>> _transcripts = new Dictionary<string, List<ChatMessage>>();
        private readonly MemoryExtractionQueue _pending = new MemoryExtractionQueue();
        private bool _draining;
        private float _drainTimer;

        /// <summary>Extractions waiting for the connection to come back (#45). In memory only.</summary>
        public MemoryExtractionQueue PendingExtractions => _pending;

        /// <summary>
        /// Runs the after-conversation extraction for one villager and stores the facts. Public so verification
        /// scripts can drive it directly; returns null if the manager was destroyed or the call was cancelled.
        /// A no-connection failure is queued and retried once the connection is back (#45).
        /// </summary>
        public Task<MemoryExtractionResult> ExtractAndRetainAsync(string npcId, IReadOnlyList<ChatMessage> conversation)
        {
            if (conversation == null) return Task.FromResult<MemoryExtractionResult>(null);
            _lastMood.TryGetValue(npcId ?? string.Empty, out string mood);
            var item = new PendingExtraction { NpcId = npcId, Conversation = new List<ChatMessage>(conversation), Day = CurrentDay(), Mood = mood };
            return RunPendingSafelyAsync(item, false);
        }

        /// <summary>Fire-and-forget safe: an early throw is logged as a warning instead of vanishing (#45).</summary>
        private Task<MemoryExtractionResult> RunPendingSafelyAsync(PendingExtraction item, bool fromQueue) =>
            MemoryExtractionQueue.RunSafelyAsync(() => RunPendingAsync(item, fromQueue), msg => Debug.LogWarning(msg));

        private async Task<MemoryExtractionResult> RunPendingAsync(PendingExtraction item, bool fromQueue)
        {
            LlmRequest request = MemoryExtractor.BuildRequest(item.NpcId, DisplayNameOf(item.NpcId), item.Conversation);
            if (request == null) return null;
            try
            {
                MemoryExtractionResult r = await MemoryExtractor.RunAsync(Service.Primary, request, item.Day,
                    Mathf.Max(Config.timeoutSeconds, 8f), _lifetime.Token, item.Mood, FrontierConnectionManager.AllowsNetworkCalls);
                if (this == null) return null;
                if (r.CountsTowardUsage)
                    Service.Usage.RecordFeature(MemoryExtractor.FeatureTag, r.PromptTokens, r.CompletionTokens, r.Error != null);
                var social = FrontierSocialManager.Instance;
                if (social != null) social.RetainMemories(r.Facts);
                string note = string.Empty;
                if (MemoryExtractionQueue.ShouldQueue(r))
                {
                    MemoryExtractionQueue.Outcome outcome = _pending.Complete(item, r, fromQueue);
                    note = outcome == MemoryExtractionQueue.Outcome.Requeued
                        ? $" [queued until online, {_pending.Count} waiting, try {item.Attempts}/{_pending.MaxAttempts}]"
                        : " [dropped after retries]";
                }
                string tpd = LlmTurnService.IsTpdCap(r.Error) ? " [tpd_cap]" : string.Empty;
                Debug.Log($"[Memory] extract {item.NpcId}: {MemoryExtractor.Describe(r)}{tpd}{note} | {MemoryExtractor.FeatureTag} {Service.Usage.GetFeature(MemoryExtractor.FeatureTag)}");
                MemoriesExtracted?.Invoke(item.NpcId, r);
                return r;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private void Update()
        {
            if (_pending.Count == 0 || _draining) return;
            _drainTimer -= Time.unscaledDeltaTime;
            if (_drainTimer > 0f) return;
            _drainTimer = DrainCheckSeconds;
            if (IsConnectionOnline()) _ = DrainPendingAsync();
        }

        /// <summary>Runs queued extractions one at a time while the connection stays Online (#45).</summary>
        private async Task DrainPendingAsync()
        {
            _draining = true;
            try
            {
                while (this != null && _pending.Count > 0 && IsConnectionOnline())
                {
                    PendingExtraction item = _pending.TakeNext();
                    MemoryExtractionResult r = await RunPendingSafelyAsync(item, true);
                    if (MemoryExtractionQueue.ShouldQueue(r)) break; // dropped again: wait for the next Online
                }
            }
            finally
            {
                _draining = false;
            }
        }

        private static bool IsConnectionOnline()
        {
            var m = FrontierConnectionManager.Instance;
            if (m != null && m.Monitor != null) return m.Monitor.State == ProjectFrontier.Net.ConnectionState.Online;
            return FrontierConnectionManager.AllowsNetworkCalls;
        }

        private static int CurrentDay()
        {
            var social = FrontierSocialManager.Instance;
            return social != null ? social.State.Day : 1;
        }

        /// <summary>
        /// Runs one player -> NPC turn. Always returns a playable reply (built-in line on any provider failure, #37),
        /// or null if a turn for this villager is already in flight or the manager was destroyed.
        /// </summary>
        public async Task<LlmTurnResult> SendPlayerMessageAsync(NpcPersona persona, string playerText)
        {
            if (persona == null) throw new ArgumentNullException(nameof(persona));
            string npcId = persona.npcId;
            if (!_inFlight.Add(npcId)) return null;

            try
            {
                var social = FrontierSocialManager.Instance;
                SocialState state = social != null ? social.State : null;
                string message = TurnPromptBuilder.SanitizePlayerMessage(playerText);
                List<MemoryFact> recalled = social != null && recallMemories > 0
                    ? social.RecallMemories(npcId, message, recallMemories) : new List<MemoryFact>();
                _lastRecalled[npcId] = recalled;

                LlmRequest request = _builder.Build(new TurnPromptBuilder.Context
                {
                    NpcId = npcId,
                    NpcDisplayName = persona.displayName,
                    PersonaCard = persona.ToPromptCard(),
                    NowLine = BuildNowLine(npcId, state?.Day ?? 1),
                    StandingLine = state?.Get(npcId).ToPromptLine(),
                    RumorLine = state != null ? TurnPromptBuilder.BuildRumorLine(state.Rumors, npcId, state.Day, DisplayNameOf) : null,
                    Day = state?.Day ?? 1,
                    MemoryBlock = NpcMemoryStore.BuildPromptBlock(recalled, state?.Day ?? 1),
                    History = GetHistory(npcId),
                    PlayerMessage = message
                });
                request.Temperature = Config.temperature;

                LlmTurnResult result = await Service.RunTurnAsync(request, persona.GetStarterQuickReplies(), _lifetime.Token);
                if (this == null) return null;

                Remember(npcId, new ChatMessage(ChatRole.User, message));
                Remember(npcId, new ChatMessage(ChatRole.Assistant, result.Dialogue));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Dev-only transcript line so repetition can be checked from logcat (#41). Never in release builds.
                Debug.Log($"[LLM] chat {npcId}: P=\"{message}\" NPC=\"{result.Dialogue}\" mood={result.Mood}");
#endif
                _lastMood[npcId] = result.Mood;
                if (recalled.Count > 0)
                    MemoryLog.Text($"[Memory] {npcId} recalled {recalled.Count}: " + string.Join(" | ", recalled.ConvertAll(m => m.text)));   // dev-only (#45)

                if (social != null && !result.Deltas.IsZero)
                    result.AppliedDeltas = social.ApplyDelta(npcId, result.Deltas, DeltaSource.LlmTurn).Applied;
                if (social != null && !result.AppliedDeltas.IsZero)
                    social.RecordChatOutcome(npcId, result.AppliedDeltas);   // notable chats become rumors (#16)

                TurnCompleted?.Invoke(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _inFlight.Remove(npcId);
            }
        }

        private void Remember(string npcId, ChatMessage message)
        {
            if (!_history.TryGetValue(npcId, out var list)) _history[npcId] = list = new List<ChatMessage>();
            list.Add(message);
            int overflow = list.Count - Mathf.Max(2, storedHistoryMessages);
            if (overflow > 0) list.RemoveRange(0, overflow);

            // Whole conversation for memory extraction (#45): bounded, keeping the opening exchange.
            if (!_transcripts.TryGetValue(npcId, out var transcript)) _transcripts[npcId] = transcript = new List<ChatMessage>();
            transcript.Add(message);
            int cap = Mathf.Max(TranscriptOpeningMessages + 2, storedTranscriptMessages);
            if (transcript.Count > cap) transcript.RemoveRange(TranscriptOpeningMessages, transcript.Count - cap);
        }

        /// <summary>Live "Now:" line: day, phase, where this villager is per the schedule, and lantern progress.</summary>
        private static string BuildNowLine(string npcId, int day)
        {
            var cycle = FrontierDayCycleManager.Instance;
            DayPhase phase = cycle != null ? cycle.CurrentPhase : DayPhase.Morning;
            if (cycle != null) day = cycle.CurrentDay;
            string place = NpcScheduleTable.SpotDisplayName(NpcScheduleTable.GetScheduledSpot(npcId, phase, day));
            var loop = FrontierGameLoop.Instance;
            return TurnPromptBuilder.BuildNowLine(day, phase.ToString(), place,
                loop != null ? loop.CollectedLanterns : 0, loop != null ? loop.GoalLanterns : 0);
        }

        private static string DisplayNameOf(string npcId)
        {
            foreach (var npc in VillageNpc.AllNpcs)
                if (npc != null && npc.NpcId == npcId) return npc.DisplayName;
            return npcId;
        }
    }
}
