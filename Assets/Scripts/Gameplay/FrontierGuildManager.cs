using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProjectFrontier.Farm;
using ProjectFrontier.Guild;
using ProjectFrontier.Llm;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Scene owner of the Adventurers Guild (#20): manages Captain Aldric's quest board,
    /// C#-enforced bounty contracts &amp; rank progression, async Layer 2 LLM flavor descriptions
    /// with static fallback, relationship/gossip hooks, and local + Cloud Save persistence.
    /// </summary>
    public class FrontierGuildManager : MonoBehaviour
    {
        public static FrontierGuildManager Instance { get; private set; }

        public const string CloudKey = "guild";
        public const string LocalFileName = "frontier_guild.json";

        [Header("Persistence")]
        public float cloudSaveIntervalSeconds = 30f;

        public GuildState State { get; private set; }
        public string LastSaveStatus { get; private set; } = "Local cache";
        public string LastFlavorSource { get; private set; } = "STATIC";
        public bool IsFetchingFlavor { get; private set; }

        public int CurrentDay => FrontierDayCycleManager.Instance != null
            ? FrontierDayCycleManager.Instance.CurrentDay
            : (FrontierSocialManager.Instance != null ? FrontierSocialManager.Instance.State.Day : 1);

        public event Action<string> StatusFeedback;
        public event Action FlavorUpdated;

        private FrontierDayCycleManager _dayCycle;
        private FrontierFarmManager _farm;
        private FrontierGameLoop _loop;
        private FrontierLlmManager _llm;
        private string _localPath;
        private bool _dirty;
        private long _localSavedAt;
        private float _lastCloudSaveTime = -999f;
        private bool _cloudSaveInFlight;
        private int _lastSeenLanterns;
        private CancellationTokenSource _lifetime;
        private readonly HashSet<string> _flavorInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            State = new GuildState(initialDay: CurrentDay);
            _localPath = Path.Combine(Application.persistentDataPath, LocalFileName);
            GuildSaveData local = ReadLocal();
            if (local != null)
            {
                State.LoadFrom(local, CurrentDay);
                _localSavedAt = local.savedAtUnixMs;
            }
            State.Changed += HandleStateChanged;
            State.RankChanged += HandleRankChanged;
        }

        private async void Start()
        {
            _dayCycle = FrontierDayCycleManager.Instance;
            if (_dayCycle != null)
            {
                _dayCycle.DayAdvanced += HandleDayAdvanced;
            }

            _farm = FrontierFarmManager.Instance;
            if (_farm != null)
            {
                _farm.CropHarvested += HandleCropHarvested;
                _farm.ItemCrafted += HandleItemCrafted;
            }

            _loop = FrontierGameLoop.Instance;
            if (_loop != null)
            {
                _lastSeenLanterns = _loop.CollectedLanterns;
                _loop.ProgressChanged += HandleLanternProgressChanged;
                _loop.DialogueUpdated += HandleDialogueUpdated;
            }

            _llm = FrontierLlmManager.Instance;
            if (_llm != null)
            {
                _llm.TurnCompleted += HandleLlmTurnCompleted;
            }

            State.RefreshBoardForDay(CurrentDay);
            await ReconcileWithCloudAsync();
            if (this == null) return;
            _ = RefreshBoardFlavorAsync();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (State != null)
            {
                State.Changed -= HandleStateChanged;
                State.RankChanged -= HandleRankChanged;
            }
            if (_dayCycle != null) _dayCycle.DayAdvanced -= HandleDayAdvanced;
            if (_farm != null)
            {
                _farm.CropHarvested -= HandleCropHarvested;
                _farm.ItemCrafted -= HandleItemCrafted;
            }
            if (_loop != null)
            {
                _loop.ProgressChanged -= HandleLanternProgressChanged;
                _loop.DialogueUpdated -= HandleDialogueUpdated;
            }
            if (_llm != null) _llm.TurnCompleted -= HandleLlmTurnCompleted;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            Instance = null;
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) SaveNow(forceCloud: true);
        }

        private void OnApplicationQuit() => SaveNow(forceCloud: true);

        // ------------------------------------------------------------------ gameplay actions

        public bool AcceptBounty(string bountyId, out string message)
        {
            if (State == null)
            {
                message = "GUILD OFFLINE";
                return false;
            }

            bool ok = State.TryAccept(bountyId, CurrentDay, out message);
            if (ok)
            {
                _dirty = true;
                SynthAudioManager.Instance?.PlayUiClick();
                SaveNow(forceCloud: false);
            }
            else
            {
                SynthAudioManager.Instance?.PlayUiClick();
            }
            StatusFeedback?.Invoke(message);
            return ok;
        }

        public bool TurnInBounty(string bountyId, out BountyTurnInResult result)
        {
            if (State == null)
            {
                result = new BountyTurnInResult(false, bountyId, 0, 0, GuildRank.Bronze, GuildRank.Bronze, "GUILD OFFLINE");
                return false;
            }

            InventoryState inv = FrontierFarmManager.Instance?.Inventory;
            SocialState social = FrontierSocialManager.Instance?.State;
            bool ok = State.TryTurnIn(bountyId, inv, social, out result);
            if (ok)
            {
                _dirty = true;
                SynthAudioManager.Instance?.PlayPickup();
                if (PlayerController2D.Instance != null)
                {
                    PlayerController2D.Instance.Motion?.PlayPickupPop();
                    FeelManager.Instance?.PlayPickupFeedback(
                        PlayerController2D.Instance.transform.position + Vector3.up * 0.55f,
                        $"+{result.GoldAwarded}G +{result.XpAwarded}XP");
                }
                if (result.RankedUp)
                {
                    SynthAudioManager.Instance?.PlayMilestone();
                    FeelManager.Instance?.PlayMilestoneMoment(
                        $"RANK UP · {GuildCatalog.GetRankShortLabel(result.NewRank)}!",
                        FrontierPalette.HearthGold);
                    _ = RefreshBoardFlavorAsync();
                }
                FrontierSocialManager.Instance?.SaveNow(forceCloud: false);
                FrontierFarmManager.Instance?.SaveNow(forceCloud: false);
                SaveNow(forceCloud: false);
            }
            else
            {
                SynthAudioManager.Instance?.PlayUiClick();
            }
            StatusFeedback?.Invoke(result.Message);
            return ok;
        }

        public bool AbandonBounty(string bountyId, out string message)
        {
            if (State == null)
            {
                message = "GUILD OFFLINE";
                return false;
            }

            SocialState social = FrontierSocialManager.Instance?.State;
            bool ok = State.TryAbandon(bountyId, social, out message);
            if (ok)
            {
                _dirty = true;
                SynthAudioManager.Instance?.PlayUiClick();
                FrontierSocialManager.Instance?.SaveNow(forceCloud: false);
                SaveNow(forceCloud: false);
            }
            StatusFeedback?.Invoke(message);
            return ok;
        }

        /// <summary>
        /// External progress hook used by Farming, Dialogue, Lanterns, and upcoming Dungeon/Combat (#21-#22).
        /// </summary>
        public int RecordObjectiveProgress(BountyObjectiveKind kind, string targetId = null, int delta = 1)
        {
            if (State == null) return 0;
            int updated = State.RecordProgress(kind, targetId, delta);
            if (updated > 0)
            {
                _dirty = true;
                SaveNow(forceCloud: false);
            }
            return updated;
        }

        public string GetBountyDescription(BountyDef def)
        {
            if (def == null) return string.Empty;
            return State != null ? State.GetDisplayDescription(def, CurrentDay) : def.StaticDescription;
        }

        public void ResetToStarterState()
        {
            if (State == null) return;
            State.ResetToStarter(CurrentDay);
            LastFlavorSource = "STATIC";
            _dirty = true;
            SaveNow(forceCloud: false);
        }

        // ------------------------------------------------------------------ Layer 2 async flavor

        /// <summary>
        /// Non-blocking Layer 2 LLM flavor refresh for the current day's posted bounties.
        /// Uses cached values when present and falls back silently to static copy when offline or on error.
        /// </summary>
        public async Task RefreshBoardFlavorAsync(BountyFlavorService customService = null)
        {
            if (State == null || this == null) return;
            int day = CurrentDay;
            GuildRank rank = State.Rank;
            var boardSnapshot = new List<string>(State.BoardBountyIds);
            if (boardSnapshot.Count == 0) return;

            BountyFlavorService service = customService ?? BuildDefaultFlavorService();
            if (service == null)
            {
                LastFlavorSource = "STATIC";
                return;
            }

            string townHint = BuildTownContextHint();
            IsFetchingFlavor = true;
            try
            {
                for (int i = 0; i < boardSnapshot.Count; i++)
                {
                    if (this == null || _lifetime.IsCancellationRequested) return;
                    string bountyId = boardSnapshot[i];
                    if (State.TryGetCachedFlavor(day, bountyId, out _)) continue;
                    BountyDef def = GuildCatalog.GetBounty(bountyId);
                    if (def == null) continue;

                    string key = State.MakeFlavorKey(day, bountyId);
                    if (!_flavorInFlight.Add(key)) continue;
                    try
                    {
                        BountyFlavorResult res = await service.GenerateFlavorAsync(def, day, rank, townHint, _lifetime.Token);
                        if (this == null) return;
                        if (!res.UsedFallback && !string.IsNullOrWhiteSpace(res.Text))
                        {
                            State.SetCachedFlavor(day, bountyId, res.Text);
                            LastFlavorSource = $"LAYER 2 · {res.ProviderName.ToUpperInvariant()}";
                            _dirty = true;
                            FlavorUpdated?.Invoke();
                        }
                        else if (LastFlavorSource == "STATIC")
                        {
                            LastFlavorSource = "STATIC";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    finally
                    {
                        _flavorInFlight.Remove(key);
                    }
                }
                if (_dirty) SaveNow(forceCloud: false);
            }
            finally
            {
                IsFetchingFlavor = false;
            }
        }

        private BountyFlavorService BuildDefaultFlavorService()
        {
            var llm = FrontierLlmManager.Instance;
            ILLMProvider primary = llm?.Service?.Primary;
            if (primary == null || !primary.RequiresNetwork) return null;
            return new BountyFlavorService(
                primary,
                () => Application.internetReachability != NetworkReachability.NotReachable,
                BountyFlavorService.DefaultTimeoutSeconds);
        }

        private string BuildTownContextHint()
        {
            var social = FrontierSocialManager.Instance;
            if (social == null) return string.Empty;
            Relationship aldric = social.Get("aldric");
            return $"Aldric standing: {aldric.State}";
        }

        // ------------------------------------------------------------------ event subscriptions

        private void HandleStateChanged() => _dirty = true;

        private void HandleRankChanged(GuildRank previous, GuildRank next)
        {
            Debug.Log($"[Guild] Promoted from {previous} to {next} ({State.GuildXp} XP).");
        }

        private void HandleDayAdvanced(int newDay)
        {
            if (State == null) return;
            State.RefreshBoardForDay(newDay);
            _dirty = true;
            SaveNow(forceCloud: false);
            _ = RefreshBoardFlavorAsync();
        }

        private void HandleCropHarvested(string cropId, int quantity)
        {
            RecordObjectiveProgress(BountyObjectiveKind.HarvestCrops, cropId, 1);
        }

        private void HandleItemCrafted(string itemId, int quantity)
        {
            RecordObjectiveProgress(BountyObjectiveKind.CraftPotions, itemId, Math.Max(1, quantity));
        }

        private void HandleLanternProgressChanged(int collected, int goal)
        {
            int delta = collected - _lastSeenLanterns;
            _lastSeenLanterns = collected;
            if (delta > 0)
                RecordObjectiveProgress(BountyObjectiveKind.CollectLanternShards, null, delta);
        }

        private void HandleDialogueUpdated(VillageNpc npc, int lineIndex)
        {
            if (npc != null && lineIndex == 0 && !string.IsNullOrEmpty(npc.NpcId))
                RecordObjectiveProgress(BountyObjectiveKind.SpeakWithVillager, npc.NpcId, 1);
        }

        private void HandleLlmTurnCompleted(LlmTurnResult result)
        {
            if (result != null && !string.IsNullOrEmpty(result.NpcId))
                RecordObjectiveProgress(BountyObjectiveKind.SpeakWithVillager, result.NpcId, 1);
        }

        // ------------------------------------------------------------------ persistence

        public void SaveNow(bool forceCloud)
        {
            if (State == null || string.IsNullOrEmpty(_localPath)) return;
            GuildSaveData snapshot = State.ToSaveData(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteLocal(snapshot);
            _localSavedAt = snapshot.savedAtUnixMs;
            _dirty = false;

            bool due = forceCloud || Time.realtimeSinceStartup - _lastCloudSaveTime >= cloudSaveIntervalSeconds;
            if (due) _ = SaveCloudAsync(snapshot);
        }

        private async Task SaveCloudAsync(GuildSaveData snapshot)
        {
            if (_cloudSaveInFlight || !LiveOpsAllowsWrites()) return;
            _cloudSaveInFlight = true;
            _lastCloudSaveTime = Time.realtimeSinceStartup;
            try
            {
                bool ok = await PlayerSave.SaveAsync(CloudKey, snapshot);
                LastSaveStatus = ok ? "Cloud synced" : "Local cache";
            }
            finally
            {
                _cloudSaveInFlight = false;
            }
        }

        private async Task ReconcileWithCloudAsync()
        {
            if (!LiveOpsAllowsWrites()) return;
            GuildSaveData cloud = await PlayerSave.LoadAsync<GuildSaveData>(CloudKey, null);
            if (this == null) return;

            if (cloud != null && !_dirty && cloud.savedAtUnixMs > _localSavedAt)
            {
                State.LoadFrom(cloud, CurrentDay);
                WriteLocal(cloud);
                _localSavedAt = cloud.savedAtUnixMs;
                LastSaveStatus = "Cloud loaded";
            }
        }

        private GuildSaveData ReadLocal()
        {
            try
            {
                if (!File.Exists(_localPath)) return null;
                string json = File.ReadAllText(_localPath);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonUtility.FromJson<GuildSaveData>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Guild] Local save unreadable ({e.Message}); starting fresh.");
                return null;
            }
        }

        private void WriteLocal(GuildSaveData snapshot)
        {
            try
            {
                string dir = Path.GetDirectoryName(_localPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_localPath, JsonUtility.ToJson(snapshot));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Guild] Local save failed: {e.Message}");
            }
        }

        private static bool LiveOpsAllowsWrites() =>
            FrontierLiveManager.IsSignedInNow() && (FrontierLiveManager.Instance == null || FrontierLiveManager.Instance.LiveOpsEnabled);
    }
}
