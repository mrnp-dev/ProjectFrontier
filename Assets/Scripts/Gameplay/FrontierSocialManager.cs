using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ProjectFrontier.Social;
using ProjectFrontier.World;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Scene owner of the <see cref="SocialState"/> (relationships, day count, rumors).
    /// Loads the local JSON cache synchronously in Awake so state is ready before the first conversation,
    /// then reconciles with Cloud Save (newest snapshot wins). Saves locally whenever a conversation closes
    /// and on pause/quit; cloud writes are throttled and respect the LiveOps kill switch.
    /// </summary>
    public class FrontierSocialManager : MonoBehaviour
    {
        public static FrontierSocialManager Instance { get; private set; }

        public const string CloudKey = "social";
        public const string LocalFileName = "frontier_social.json";

        [Tooltip("Minimum seconds between Cloud Save writes outside of pause/quit.")]
        public float cloudSaveIntervalSeconds = 30f;

        [Header("First Meeting (Scripted)")]
        public int firstMeetTrust = 2;
        public int firstMeetAffection = 1;

        public SocialState State { get; } = new SocialState();

        /// <summary>Hidden 5x5 NPC-to-NPC trust weights used by the Gossip &amp; Bias Engine (#16).</summary>
        public GossipTrustMatrix GossipTrust { get; } = GossipTrustMatrix.CreateDefault();

        /// <summary>Raised after each evening exchange with every transfer that happened (may be empty).</summary>
        public event Action<IReadOnlyList<GossipTransfer>> GossipExchanged;

        [Header("Gossip")]
        [Tooltip("Show a banner when evening gossip changes how someone sees the player.")]
        public bool announceGossip = true;

        private readonly List<GossipTransfer> _transfers = new List<GossipTransfer>(16);
        private readonly Dictionary<VillageSpot, List<string>> _spotGroups = new Dictionary<VillageSpot, List<string>>();
        private readonly List<IReadOnlyList<string>> _groupList = new List<IReadOnlyList<string>>(5);
        private FrontierDayCycleManager _dayCycle;
        public string LastSaveStatus { get; private set; } = "Local cache";

        private string _localPath;
        private bool _dirty;
        private long _localSavedAt;
        private float _lastCloudSaveTime = -999f;
        private bool _cloudSaveInFlight;

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
            _localPath = Path.Combine(Application.persistentDataPath, LocalFileName);
            SocialSaveData local = ReadLocal();
            State.LoadFrom(local);
            _localSavedAt = local?.savedAtUnixMs ?? 0;
            State.RelationshipChanged += HandleRelationshipChanged;
        }

        private async void Start()
        {
            if (FrontierGameLoop.Instance != null) FrontierGameLoop.Instance.DialogueClosed += HandleDialogueClosed;
            _dayCycle = FrontierDayCycleManager.Instance;
            if (_dayCycle != null) _dayCycle.PhaseChanged += HandlePhaseChanged;
            await ReconcileWithCloudAsync();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            State.RelationshipChanged -= HandleRelationshipChanged;
            if (FrontierGameLoop.Instance != null) FrontierGameLoop.Instance.DialogueClosed -= HandleDialogueClosed;
            if (_dayCycle != null) _dayCycle.PhaseChanged -= HandlePhaseChanged;
        }

        private void OnApplicationPause(bool paused)
        {
            // Android may kill a backgrounded app without OnApplicationQuit: persist now.
            if (paused) SaveNow(forceCloud: true);
        }

        private void OnApplicationQuit() => SaveNow(forceCloud: true);

        // ------------------------------------------------------------------ gameplay API

        public Relationship Get(string npcId) => State.Get(npcId);

        public RelationshipChange ApplyDelta(string npcId, AxisDeltas deltas, DeltaSource source) =>
            State.ApplyDelta(npcId, deltas, source);

        /// <summary>Called once per villager when their scripted greeting finishes for the first time ever.</summary>
        public void RecordFirstMeeting(string npcId)
        {
            if (State.HasMet(npcId)) return;
            State.MarkMet(npcId);
            State.ApplyDelta(npcId, new AxisDeltas(firstMeetTrust, firstMeetAffection, 0), DeltaSource.Scripted);
            SaveNow(forceCloud: false);
        }

        public void AdvanceDay()
        {
            // The evening exchange is part of the day: if it was skipped (sleep/debug jump), run it before rolling over.
            if (State.LastGossipDay < State.Day) RunEveningGossip();
            State.AdvanceDay();
            _dirty = true;
            SaveNow(forceCloud: true);
        }

        // ------------------------------------------------------------------ gossip (#16)

        /// <summary>Turns a notable chat outcome into a first-hand rumor held by that villager.</summary>
        public Rumor RecordChatOutcome(string npcId, AxisDeltas applied)
        {
            Rumor rumor = GossipEngine.RecordChatOutcome(State, npcId, applied);
            if (rumor != null)
            {
                _dirty = true;
                Debug.Log($"[Gossip] {npcId} remembers: '{rumor.tag}' valence {rumor.valence:+0.00;-0.00} strength {rumor.strength:0.00} until day {rumor.expiresDay}");
            }
            return rumor;
        }

        /// <summary>Evening co-location exchange for the current day. Runs at most once per day unless forced.</summary>
        public int RunEveningGossip(bool force = false)
        {
            if (!force && State.LastGossipDay >= State.Day) return 0;

            NpcScheduleTable.GetCoLocatedGroups(DayPhase.Evening, _spotGroups, State.Day);
            _groupList.Clear();
            for (int spot = (int)VillageSpot.Forge; spot <= (int)VillageSpot.HerbGarden; spot++)
            {
                if (_spotGroups.TryGetValue((VillageSpot)spot, out List<string> group) && group.Count > 1) _groupList.Add(group);
            }

            _transfers.Clear();
            int count = GossipEngine.RunExchange(State, GossipTrust, _groupList, _transfers);
            _dirty = true;
            SaveNow(forceCloud: false);

            bool anyShift = false;
            for (int i = 0; i < _transfers.Count; i++)
            {
                Debug.Log($"[Gossip] Day {State.Day}: {_transfers[i]}");
                if (!_transfers[i].Applied.IsZero) anyShift = true;
            }
            if (count == 0) Debug.Log($"[Gossip] Day {State.Day}: no rumors to share this evening.");

            if (announceGossip && anyShift && FeelManager.Instance != null)
                FeelManager.Instance.PlayMilestoneMoment("WORD SPREADS", FrontierPalette.HearthGold);

            GossipExchanged?.Invoke(_transfers);
            return count;
        }

        public string GetGossipGreeting(string npcId, Func<string, string> nameOf) =>
            GossipEngine.BuildGreetingLine(State.Rumors, npcId, State.Day, nameOf);

        public Rumor GetTopHeardRumor(string npcId) => GossipEngine.TopHeardRumor(State.Rumors, npcId, State.Day);

        private void HandlePhaseChanged(DayPhase previous, DayPhase next)
        {
            if (next == DayPhase.Evening) RunEveningGossip();
        }

        // ------------------------------------------------------------------ persistence

        public void SaveNow(bool forceCloud)
        {
            SocialSaveData snapshot = State.ToSaveData(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteLocal(snapshot);
            _localSavedAt = snapshot.savedAtUnixMs;
            _dirty = false;

            bool due = forceCloud || Time.realtimeSinceStartup - _lastCloudSaveTime >= cloudSaveIntervalSeconds;
            if (due) _ = SaveCloudAsync(snapshot);
        }

        private async Task SaveCloudAsync(SocialSaveData snapshot)
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

        /// <summary>Newest snapshot wins. Cloud data never overwrites unsaved in-session changes.</summary>
        private async Task ReconcileWithCloudAsync()
        {
            if (!LiveOpsAllowsWrites()) return;
            SocialSaveData cloud = await PlayerSave.LoadAsync<SocialSaveData>(CloudKey, null);
            if (this == null) return;

            if (cloud != null && !_dirty && cloud.savedAtUnixMs > _localSavedAt)
            {
                State.LoadFrom(cloud);
                WriteLocal(cloud);
                _localSavedAt = cloud.savedAtUnixMs;
                LastSaveStatus = "Cloud synced";
            }
            else if (_localSavedAt > 0 && (cloud == null || _localSavedAt > cloud.savedAtUnixMs))
            {
                SaveNow(forceCloud: true);   // this device is ahead: push it up
            }
        }

        private static bool LiveOpsAllowsWrites() =>
            FrontierLiveManager.Instance == null || FrontierLiveManager.Instance.LiveOpsEnabled;

        private SocialSaveData ReadLocal()
        {
            try
            {
                if (File.Exists(_localPath)) return JsonUtility.FromJson<SocialSaveData>(File.ReadAllText(_localPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Social] Local save unreadable, starting fresh: {e.Message}");
            }
            return null;
        }

        private void WriteLocal(SocialSaveData data)
        {
            try
            {
                // Write-then-swap so a crash mid-write never leaves a truncated save.
                string tmp = _localPath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(data));
                if (File.Exists(_localPath)) File.Delete(_localPath);
                File.Move(tmp, _localPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Social] Local save failed: {e.Message}");
            }
        }

        private void HandleRelationshipChanged(RelationshipChange change) => _dirty = true;

        private void HandleDialogueClosed()
        {
            if (_dirty) SaveNow(forceCloud: false);
        }
    }
}
