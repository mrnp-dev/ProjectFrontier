using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProjectFrontier.Llm;
using ProjectFrontier.Social;
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

        [Tooltip("Messages kept per conversation (player + NPC lines). The prompt uses at most 4 of them.")]
        public int storedHistoryMessages = 8;

        public LlmTurnService Service { get; private set; }
        public LlmEndpointConfig Config { get; private set; }
        public bool IsOnlineProvider => Service != null && Service.Primary != Service.Offline;
        public string ProviderLabel => Service?.Primary.Name ?? "offline";

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
            if (FrontierGameLoop.Instance != null) FrontierGameLoop.Instance.DialogueClosed += ClearAllHistory;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (FrontierGameLoop.Instance != null) FrontierGameLoop.Instance.DialogueClosed -= ClearAllHistory;
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
                        Debug.LogWarning("[LLM] DirectDev needs FRONTIER_LLM_BASE_URL; using offline replies.");
                    else if (string.IsNullOrWhiteSpace(Config.apiKey) && !Config.IsLocalEndpoint)
                        Debug.LogWarning("[LLM] DirectDev needs FRONTIER_LLM_API_KEY for a remote endpoint; using offline replies.");
                    else
                        primary = new OpenAiCompatibleProvider(Config, () => Config.apiKey);
#else
                    Debug.LogWarning("[LLM] DirectDev mode is disabled in release builds; using offline replies.");
#endif
                    break;
                case LlmMode.Proxy:
                    // The proxy holds the real key; the client only proves who it is with its UGS session token.
                    if (Config.HasEndpoint) primary = new OpenAiCompatibleProvider(Config, PlayerAccessToken);
                    break;
            }

            _builder.MaxOutputTokens = Mathf.Clamp(Config.maxTokens, 32, TurnPromptBuilder.DefaultMaxOutputTokens);
            Service = new LlmTurnService(primary, offline, Config.timeoutSeconds,
                () => Application.internetReachability != NetworkReachability.NotReachable);
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

        public void ClearAllHistory() => _history.Clear();

        /// <summary>
        /// Runs one player -> NPC turn. Always returns a playable reply (offline fallback on any provider failure),
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

                LlmRequest request = _builder.Build(new TurnPromptBuilder.Context
                {
                    NpcId = npcId,
                    NpcDisplayName = persona.displayName,
                    PersonaCard = persona.ToPromptCard(),
                    StandingLine = state?.Get(npcId).ToPromptLine(),
                    RumorLine = state != null ? TurnPromptBuilder.BuildRumorLine(state.Rumors, npcId, state.Day, DisplayNameOf) : null,
                    Day = state?.Day ?? 1,
                    History = GetHistory(npcId),
                    PlayerMessage = message
                });
                request.Temperature = Config.temperature;

                LlmTurnResult result = await Service.RunTurnAsync(request, persona.GetStarterQuickReplies(), _lifetime.Token);
                if (this == null) return null;

                Remember(npcId, new ChatMessage(ChatRole.User, message));
                Remember(npcId, new ChatMessage(ChatRole.Assistant, result.Dialogue));

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
        }

        private static string DisplayNameOf(string npcId)
        {
            foreach (var npc in VillageNpc.AllNpcs)
                if (npc != null && npc.NpcId == npcId) return npc.DisplayName;
            return npcId;
        }
    }
}
