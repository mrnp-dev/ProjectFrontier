using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Leaderboards;
using UnityEngine;

namespace ProjectFrontier
{
    [Serializable]
    public class FrontierProgressData
    {
        public int bestLanterns;
        public int runsCompleted;
        public float bestTimeRemaining;
        public string lastPreset = "Low";
    }

    /// <summary>
    /// Live floor wired to create-generated PlayerAuthentication, LiveOps (liveops_enabled), and PlayerSave,
    /// plus LeaderboardsService with graceful headless fallback when dashboard resources are not yet provisioned.
    /// </summary>
    public class FrontierLiveManager : MonoBehaviour
    {
        public static FrontierLiveManager Instance { get; private set; }

        public const string LeaderboardId = "frontier_lanterns";

        public bool LiveOpsEnabled { get; private set; } = true;
        public string LastRankLabel { get; private set; } = "Rank: unavailable";
        public string LastSaveStatus { get; private set; } = "Local cache";
        public FrontierProgressData Progress { get; private set; } = new FrontierProgressData();

        public event Action LiveStateUpdated;

        private string _localCachePath;
        private float _lastFetchTime = -999f;
        private const float RefetchIntervalSeconds = 120f;

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
            _localCachePath = Path.Combine(Application.persistentDataPath, "frontier_progress.json");
            LoadLocalCache();
        }

        private async void Start()
        {
            if (FrontierGameLoop.Instance != null)
            {
                FrontierGameLoop.Instance.StateChanged += HandleRunStateChanged;
            }
            await RefreshLiveOpsAndLoadAsync();
        }

        private void OnDestroy()
        {
            if (FrontierGameLoop.Instance != null)
            {
                FrontierGameLoop.Instance.StateChanged -= HandleRunStateChanged;
            }
        }

        private async void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && Time.realtimeSinceStartup - _lastFetchTime >= RefetchIntervalSeconds)
            {
                await RefetchKillSwitchAsync();
            }
        }

        private async void OnApplicationQuit()
        {
            SaveLocalCache();
            if (LiveOpsEnabled && IsSignedInNow())
            {
                await PlayerSave.SaveAsync(Progress);
            }
        }

        private async void HandleRunStateChanged(RunState state)
        {
            if (state != RunState.Won && state != RunState.Lost) return;

            var loop = FrontierGameLoop.Instance;
            if (loop == null) return;

            Progress.bestLanterns = Mathf.Max(Progress.bestLanterns, loop.CollectedLanterns);
            if (state == RunState.Won)
            {
                Progress.runsCompleted++;
                Progress.bestTimeRemaining = Mathf.Max(Progress.bestTimeRemaining, loop.TimeRemaining);
            }
            if (GraphicsSettingsManager.Instance != null)
            {
                Progress.lastPreset = GraphicsSettingsManager.Instance.GetShortTierLabel();
            }
            SaveLocalCache();

            // Re-fetch kill switch at run-end seam before any live write
            await RefetchKillSwitchAsync();
            if (!LiveOpsEnabled)
            {
                LastSaveStatus = "LiveOps paused (local)";
                LastRankLabel = "Rank: paused";
                LiveStateUpdated?.Invoke();
                return;
            }

            bool savedCloud = await SaveProgressCloudAsync();
            LastSaveStatus = savedCloud ? "Cloud synced (this device)" : "Local cache";

            int score = loop.CollectedLanterns * 100 + Mathf.RoundToInt(loop.TimeRemaining);
            await SubmitScoreAsync(score);
            LiveStateUpdated?.Invoke();
        }

        public async Task<bool> RefetchKillSwitchAsync()
        {
            _lastFetchTime = Time.realtimeSinceStartup;
            LiveOpsEnabled = await LiveOps.IsEnabledAsync();
            return LiveOpsEnabled;
        }

        public async Task RefreshLiveOpsAndLoadAsync()
        {
            await RefetchKillSwitchAsync();
            if (!LiveOpsEnabled)
            {
                LastSaveStatus = "LiveOps paused (local)";
                LiveStateUpdated?.Invoke();
                return;
            }

            if (await PlayerAuthentication.SignedIn)
            {
                var cloud = await PlayerSave.LoadAsync<FrontierProgressData>(null);
                if (cloud != null)
                {
                    Progress.bestLanterns = Mathf.Max(Progress.bestLanterns, cloud.bestLanterns);
                    Progress.runsCompleted = Mathf.Max(Progress.runsCompleted, cloud.runsCompleted);
                    Progress.bestTimeRemaining = Mathf.Max(Progress.bestTimeRemaining, cloud.bestTimeRemaining);
                    SaveLocalCache();
                    LastSaveStatus = "Cloud synced (this device)";
                }
            }
            LiveStateUpdated?.Invoke();
        }

        public async Task<bool> SaveProgressCloudAsync()
        {
            if (!LiveOpsEnabled) return false;
            return await PlayerSave.SaveAsync(Progress);
        }

        public async Task SubmitScoreAsync(int score)
        {
            if (!LiveOpsEnabled)
            {
                LastRankLabel = "Rank: paused";
                return;
            }
            try
            {
                if (!await PlayerAuthentication.SignedIn)
                {
                    LastRankLabel = "Rank: unavailable";
                    return;
                }
                await LeaderboardsService.Instance.AddPlayerScoreAsync(LeaderboardId, score);
                var entry = await LeaderboardsService.Instance.GetPlayerScoreAsync(LeaderboardId);
                LastRankLabel = entry != null ? $"Rank: #{entry.Rank + 1} ({Mathf.RoundToInt((float)entry.Score)} pts)" : "Rank: unavailable";
            }
            catch (Exception)
            {
                // Graceful degradation when leaderboard resource is not yet deployed on UGS Dashboard.
                LastRankLabel = "Rank: unavailable";
            }
        }

        public static bool IsSignedInNow()
        {
            return UnityServices.State == ServicesInitializationState.Initialized
                && AuthenticationService.Instance != null
                && AuthenticationService.Instance.IsSignedIn
                && !string.IsNullOrEmpty(AuthenticationService.Instance.PlayerId);
        }

        private void LoadLocalCache()
        {
            try
            {
                if (File.Exists(_localCachePath))
                {
                    var loaded = JsonUtility.FromJson<FrontierProgressData>(File.ReadAllText(_localCachePath));
                    if (loaded != null) Progress = loaded;
                }
            }
            catch (Exception) { }
        }

        private void SaveLocalCache()
        {
            try
            {
                File.WriteAllText(_localCachePath, JsonUtility.ToJson(Progress, true));
            }
            catch (Exception) { }
        }
    }
}
