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
        public bool IsFetchingFlavor => _flavorFetches > 0;
        public bool IsCloudReconciled => _reconciled;
        public bool HasPendingCloudSave => _cloudPending;

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
        private bool _cloudPending;            // a cloud write is owed (failed, skipped, or requested mid-flight)
        private bool _reconciled;              // cloud writes are blocked until the cloud copy has been read
        private bool _reconcileInFlight;
        private bool _actedBeforeReconcile;    // player progress made before reconcile finished
        private long _startupLocalSavedAt;     // timestamp of the local save we booted from
        private float _lastReconcileAttempt = -999f;
        private int _flavorFetches;
        private readonly HashSet<string> _flavorFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                _startupLocalSavedAt = local.savedAtUnixMs;
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
            _ = RefreshBoardFlavorAsync();
            await ReconcileWithCloudAsync();
        }

        private void Update()
        {
            // Cheap bool checks; retries a failed/skipped cloud write or an unfinished reconcile on the interval.
            if (!_cloudPending && _reconciled) return;
            float now = Time.realtimeSinceStartup;
            if (!_reconciled)
            {
                if (!_reconcileInFlight && now - _lastReconcileAttempt >= cloudSaveIntervalSeconds)
                    _ = ReconcileWithCloudAsync();
                return;
            }
            if (!_cloudSaveInFlight && now - _lastCloudSaveTime >= cloudSaveIntervalSeconds)
                _ = PumpCloudSavesAsync();
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
                message = "GUILD NOT READY";
                return false;
            }

            bool ok = State.TryAccept(bountyId, CurrentDay, out message);
            if (ok)
            {
                MarkPlayerProgress();
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
                result = new BountyTurnInResult(false, bountyId, 0, 0, GuildRank.Bronze, GuildRank.Bronze, "GUILD NOT READY");
                return false;
            }

            InventoryState inv = FrontierFarmManager.Instance?.Inventory;
            SocialState social = FrontierSocialManager.Instance?.State;
            bool ok = State.TryTurnIn(bountyId, inv, social, out result);
            if (ok)
            {
                MarkPlayerProgress();
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
                message = "GUILD NOT READY";
                return false;
            }

            SocialState social = FrontierSocialManager.Instance?.State;
            bool ok = State.TryAbandon(bountyId, social, out message);
            if (ok)
            {
                MarkPlayerProgress();
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
                MarkPlayerProgress();
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
            _flavorFailed.Clear();
            MarkPlayerProgress();
            SaveNow(forceCloud: false);
        }

        // ------------------------------------------------------------------ Layer 2 async flavor

        /// <summary>
        /// Non-blocking Layer 2 LLM flavor refresh for the current day's posted bounties.
        /// Uses cached values when present and falls back silently to static copy on a connection drop or error.
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
            _flavorFetches++;
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
                    if (_flavorFailed.Contains(key)) continue;   // fell back once today: keep static, don't re-ask
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
                        else
                        {
                            _flavorFailed.Add(key);
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
                _flavorFetches = Math.Max(0, _flavorFetches - 1);
            }
        }

        private BountyFlavorService BuildDefaultFlavorService()
        {
            var llm = FrontierLlmManager.Instance;
            ILLMProvider primary = llm?.Service?.Primary;
            if (primary == null || !primary.RequiresNetwork) return null;
            return new BountyFlavorService(
                primary,
                () => FrontierConnectionManager.AllowsNetworkCalls,
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
            _flavorFailed.Clear();
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

        private void MarkPlayerProgress()
        {
            _dirty = true;
            if (!_reconciled) _actedBeforeReconcile = true;
        }

        /// <summary>
        /// Writes the local file synchronously (source of truth) and, when due, queues a cloud write.
        /// Cloud writes never block the main thread and are never dropped: failures and requests made
        /// while a write is in flight leave <see cref="_cloudPending"/> set and are retried.
        /// </summary>
        public void SaveNow(bool forceCloud)
        {
            if (State == null || string.IsNullOrEmpty(_localPath)) return;
            GuildSaveData snapshot = State.ToSaveData(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteLocal(snapshot);
            _localSavedAt = snapshot.savedAtUnixMs;
            _dirty = false;

            bool due = forceCloud || _cloudPending || Time.realtimeSinceStartup - _lastCloudSaveTime >= cloudSaveIntervalSeconds;
            if (!due) return;
            _cloudPending = true;
            if (_reconciled && !_cloudSaveInFlight) _ = PumpCloudSavesAsync();
            // Not reconciled yet: stays pending; reconcile pushes it once the cloud copy has been read.
        }

        private async Task PumpCloudSavesAsync()
        {
            if (_cloudSaveInFlight || !_reconciled) return;
            _cloudSaveInFlight = true;
            try
            {
                // Loop so a save requested mid-flight (e.g. pause/quit) runs as a trailing write.
                while (_cloudPending && this != null && State != null)
                {
                    if (!LiveOpsAllowsWrites())
                    {
                        _lastCloudSaveTime = Time.realtimeSinceStartup;   // stays pending; retry on the interval
                        break;
                    }
                    _cloudPending = false;
                    _lastCloudSaveTime = Time.realtimeSinceStartup;
                    GuildSaveData snapshot = State.ToSaveData(_localSavedAt);
                    bool ok;
                    try
                    {
                        ok = await PlayerSave.SaveAsync(CloudKey, snapshot);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Guild] Cloud save threw: {e.Message}");
                        ok = false;
                    }
                    if (this == null) return;
                    if (!ok)
                    {
                        _cloudPending = true;
                        LastSaveStatus = "Local cache (cloud retry pending)";
                        break;
                    }
                    LastSaveStatus = "Cloud synced";
                }
            }
            catch (Exception e)
            {
                _cloudPending = true;
                Debug.LogWarning($"[Guild] Cloud save loop failed: {e.Message}");
            }
            finally
            {
                _cloudSaveInFlight = false;
            }
        }

        /// <summary>
        /// Reads the cloud copy (awaiting sign-in) and applies <see cref="GuildCloudSync.Decide"/>.
        /// Cloud writes stay blocked until the read succeeds. If the cloud copy is newer than the local save we
        /// booted from, it is adopted even when the player already acted this session (those actions are dropped),
        /// so starter or stale local state never overwrites newer cloud progress. Otherwise local is pushed up.
        /// </summary>
        private async Task ReconcileWithCloudAsync()
        {
            if (_reconciled || _reconcileInFlight) return;
            _lastReconcileAttempt = Time.realtimeSinceStartup;   // throttle retries even when LiveOps is off
            if (FrontierLiveManager.Instance != null && !FrontierLiveManager.Instance.LiveOpsEnabled) return;
            _reconcileInFlight = true;
            try
            {
                var (ok, found, cloud) = await PlayerSave.TryLoadAsync<GuildSaveData>(CloudKey);
                if (this == null || State == null) return;
                bool hasCloud = found && cloud != null;

                GuildReconcileAction action = GuildCloudSync.Decide(
                    ok, hasCloud, hasCloud ? cloud.savedAtUnixMs : 0, _startupLocalSavedAt, _actedBeforeReconcile);

                switch (action)
                {
                    case GuildReconcileAction.Retry:
                        return;   // _lastReconcileAttempt throttles the Update retry
                    case GuildReconcileAction.AdoptCloud:
                        State.LoadFrom(cloud, CurrentDay);
                        WriteLocal(cloud);
                        _localSavedAt = cloud.savedAtUnixMs;
                        _dirty = false;
                        _cloudPending = false;
                        _actedBeforeReconcile = false;
                        LastSaveStatus = "Cloud loaded";
                        StatusFeedback?.Invoke("Guild progress restored from cloud");
                        _flavorFailed.Clear();
                        _ = RefreshBoardFlavorAsync();
                        break;
                    case GuildReconcileAction.PushLocal:
                        _cloudPending = true;
                        break;
                    case GuildReconcileAction.InSync:
                        break;
                }

                _reconciled = true;
                if (_cloudPending) _ = PumpCloudSavesAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Guild] Cloud reconcile failed: {e.Message}");
            }
            finally
            {
                _reconcileInFlight = false;
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
            FrontierLiveManager.Instance == null || FrontierLiveManager.Instance.LiveOpsEnabled;
    }
}
