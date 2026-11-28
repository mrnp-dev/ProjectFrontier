using System;
using System.Collections.Generic;
using ProjectFrontier.Farm;
using ProjectFrontier.Social;

namespace ProjectFrontier.Guild
{
    [Serializable]
    public class ActiveBountyRecord
    {
        public string bountyId;
        public int acceptedDay;
        public int progress;
        public List<string> trackedTargets = new List<string>();
    }

    [Serializable]
    public class CachedFlavorRecord
    {
        public string key;
        public string text;
    }

    [Serializable]
    public class GuildSaveData
    {
        public int version = 1;
        public long savedAtUnixMs;
        public int guildXp;
        public int completedCount;
        public int abandonedCount;
        public int boardDay;
        public List<string> boardBountyIds = new List<string>();
        public List<ActiveBountyRecord> activeBounties = new List<ActiveBountyRecord>();
        public List<string> completedTodayIds = new List<string>();
        public List<CachedFlavorRecord> flavorCache = new List<CachedFlavorRecord>();
    }

    public readonly struct BountyTurnInResult
    {
        public readonly bool Succeeded;
        public readonly string BountyId;
        public readonly int GoldAwarded;
        public readonly int XpAwarded;
        public readonly GuildRank PreviousRank;
        public readonly GuildRank NewRank;
        public readonly bool RankedUp;
        public readonly string Message;

        public BountyTurnInResult(
            bool succeeded,
            string bountyId,
            int goldAwarded,
            int xpAwarded,
            GuildRank previousRank,
            GuildRank newRank,
            string message)
        {
            Succeeded = succeeded;
            BountyId = bountyId;
            GoldAwarded = goldAwarded;
            XpAwarded = xpAwarded;
            PreviousRank = previousRank;
            NewRank = newRank;
            RankedUp = newRank > previousRank;
            Message = message;
        }
    }

    /// <summary>
    /// Pure-C# Adventurers Guild state machine: daily quest board, C#-enforced rank requirements,
    /// active bounty progress, rewards, rank progression, and Social/Gossip reputation hooks (#20).
    /// </summary>
    public sealed class GuildState
    {
        public const int MaxActiveBounties = 2;
        public const int MaxBoardEntries = 4;
        public const int AbandonAldricTrustPenalty = -3;
        public const int AbandonAldricRespectPenalty = -4;
        public const int RankUpAldricRespectBonus = 4;
        public const int RankUpLyraRespectBonus = 3;

        public const string RumorCompletedBounty = "completed_guild_bounty";
        public const string RumorEarnedPromotion = "earned_guild_promotion";
        public const string RumorAbandonedContract = "abandoned_guild_contract";

        private readonly List<string> _boardBountyIds = new List<string>(MaxBoardEntries);
        private readonly List<ActiveBountyRecord> _active = new List<ActiveBountyRecord>(MaxActiveBounties);
        private readonly HashSet<string> _completedToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _flavorCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int GuildXp { get; private set; }
        public GuildRank Rank => GuildCatalog.GetRankForXp(GuildXp);
        public int CompletedCount { get; private set; }
        public int AbandonedCount { get; private set; }
        public int BoardDay { get; private set; }
        public IReadOnlyList<string> BoardBountyIds => _boardBountyIds;
        public IReadOnlyList<ActiveBountyRecord> ActiveBounties => _active;

        public event Action Changed;
        public event Action<GuildRank, GuildRank> RankChanged;

        public GuildState(int initialDay = 1)
        {
            RefreshBoardForDay(Math.Max(1, initialDay));
        }

        // ------------------------------------------------------------------ board & queries

        public void RefreshBoardForDay(int day)
        {
            int safeDay = Math.Max(1, day);
            if (BoardDay == safeDay && _boardBountyIds.Count > 0) return;

            bool dayAdvanced = safeDay != BoardDay;
            BoardDay = safeDay;
            if (dayAdvanced) _completedToday.Clear();

            RebuildBoardList(safeDay);
            Changed?.Invoke();
        }

        private void RebuildBoardList(int day)
        {
            _boardBountyIds.Clear();
            GuildRank currentRank = Rank;

            var eligible = new List<BountyDef>();
            var nextTier = new List<BountyDef>();
            var all = GuildCatalog.AllBounties;
            for (int i = 0; i < all.Count; i++)
            {
                BountyDef b = all[i];
                if (b.MinRank <= currentRank) eligible.Add(b);
                else if ((int)b.MinRank == (int)currentRank + 1) nextTier.Add(b);
            }

            // Keep any currently active bounties visible on the board first
            for (int i = 0; i < _active.Count && _boardBountyIds.Count < MaxBoardEntries; i++)
            {
                if (!_boardBountyIds.Contains(_active[i].bountyId))
                    _boardBountyIds.Add(_active[i].bountyId);
            }

            int eligibleTarget = nextTier.Count > 0 ? MaxBoardEntries - 1 : MaxBoardEntries;
            if (eligible.Count > 0)
            {
                // Prioritize current-rank bounties first, then rotate deterministically by day
                eligible.Sort((a, b) =>
                {
                    int rankCmp = b.MinRank.CompareTo(a.MinRank);
                    if (rankCmp != 0) return rankCmp;
                    return string.CompareOrdinal(a.Id, b.Id);
                });
                int offset = (day - 1) % eligible.Count;
                for (int i = 0; i < eligible.Count && _boardBountyIds.Count < eligibleTarget; i++)
                {
                    BountyDef b = eligible[(offset + i) % eligible.Count];
                    if (!_boardBountyIds.Contains(b.Id)) _boardBountyIds.Add(b.Id);
                }
            }

            // Show 1 next-rank preview bounty so players see what rank progression unlocks
            if (nextTier.Count > 0 && _boardBountyIds.Count < MaxBoardEntries)
            {
                BountyDef preview = nextTier[(day - 1) % nextTier.Count];
                if (!_boardBountyIds.Contains(preview.Id)) _boardBountyIds.Add(preview.Id);
            }
        }

        public bool IsActive(string bountyId) => FindActive(bountyId) != null;

        public bool IsCompletedToday(string bountyId) =>
            !string.IsNullOrEmpty(bountyId) && _completedToday.Contains(bountyId);

        public ActiveBountyRecord FindActive(string bountyId)
        {
            if (string.IsNullOrEmpty(bountyId)) return null;
            for (int i = 0; i < _active.Count; i++)
            {
                if (string.Equals(_active[i].bountyId, bountyId, StringComparison.OrdinalIgnoreCase))
                    return _active[i];
            }
            return null;
        }

        public int GetEffectiveProgress(ActiveBountyRecord rec, InventoryState inv)
        {
            if (rec == null) return 0;
            BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
            if (def == null) return 0;
            if (def.ObjectiveKind == BountyObjectiveKind.DeliverItem)
            {
                int have = inv != null ? inv.GetCount(def.TargetId) : 0;
                return Math.Min(have, def.RequiredCount);
            }
            return Math.Min(Math.Max(0, rec.progress), def.RequiredCount);
        }

        public bool IsReadyToTurnIn(string bountyId, InventoryState inv)
        {
            ActiveBountyRecord rec = FindActive(bountyId);
            if (rec == null) return false;
            BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
            if (def == null) return false;
            return GetEffectiveProgress(rec, inv) >= def.RequiredCount;
        }

        public int CountReadyToTurnIn(InventoryState inv)
        {
            int n = 0;
            for (int i = 0; i < _active.Count; i++)
                if (IsReadyToTurnIn(_active[i].bountyId, inv)) n++;
            return n;
        }

        // ------------------------------------------------------------------ C#-enforced mutations

        public bool CanAccept(string bountyId, out string reason)
        {
            BountyDef def = GuildCatalog.GetBounty(bountyId);
            if (def == null)
            {
                reason = "UNKNOWN BOUNTY";
                return false;
            }
            if (Rank < def.MinRank)
            {
                reason = $"REQUIRES {GuildCatalog.GetRankShortLabel(def.MinRank)} RANK";
                return false;
            }
            if (IsActive(def.Id))
            {
                reason = "ALREADY ACCEPTED";
                return false;
            }
            if (IsCompletedToday(def.Id))
            {
                reason = "COMPLETED TODAY";
                return false;
            }
            if (_active.Count >= MaxActiveBounties)
            {
                reason = $"MAX {MaxActiveBounties} ACTIVE CONTRACTS";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public bool TryAccept(string bountyId, int currentDay, out string message)
        {
            if (!CanAccept(bountyId, out message)) return false;
            BountyDef def = GuildCatalog.GetBounty(bountyId);
            _active.Add(new ActiveBountyRecord
            {
                bountyId = def.Id,
                acceptedDay = Math.Max(1, currentDay),
                progress = 0
            });
            message = $"ACCEPTED: {def.Title.ToUpperInvariant()}";
            Changed?.Invoke();
            return true;
        }

        public bool TryAbandon(string bountyId, SocialState social, out string message)
        {
            ActiveBountyRecord rec = FindActive(bountyId);
            if (rec == null)
            {
                message = "CONTRACT NOT ACTIVE";
                return false;
            }
            BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
            _active.Remove(rec);
            AbandonedCount++;

            if (social != null)
            {
                social.ApplyDelta("aldric", new AxisDeltas(AbandonAldricTrustPenalty, 0, AbandonAldricRespectPenalty), DeltaSource.Scripted);
                GossipEngine.RecordAction(social, "aldric", RumorAbandonedContract, valence: -0.60f, strength: 0.70f);
            }

            message = def != null
                ? $"ABANDONED {def.Title.ToUpperInvariant()} (ALDRIC T{AbandonAldricTrustPenalty} R{AbandonAldricRespectPenalty})"
                : "CONTRACT ABANDONED";
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// Advances non-delivery active bounties matching <paramref name="kind"/>.
        /// For <see cref="BountyObjectiveKind.SpeakWithVillager"/>, tracks distinct villager IDs when provided.
        /// Returns the number of active bounties whose progress changed.
        /// </summary>
        public int RecordProgress(BountyObjectiveKind kind, string targetId = null, int delta = 1)
        {
            if (delta <= 0 || kind == BountyObjectiveKind.DeliverItem) return 0;
            int updated = 0;
            string normTarget = (targetId ?? string.Empty).Trim().ToLowerInvariant();

            for (int i = 0; i < _active.Count; i++)
            {
                ActiveBountyRecord rec = _active[i];
                BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
                if (def == null || def.ObjectiveKind != kind) continue;
                if (rec.progress >= def.RequiredCount) continue;

                if (!string.IsNullOrEmpty(def.TargetId) &&
                    !string.Equals(def.TargetId, normTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (kind == BountyObjectiveKind.SpeakWithVillager && !string.IsNullOrEmpty(normTarget))
                {
                    if (rec.trackedTargets == null) rec.trackedTargets = new List<string>();
                    if (rec.trackedTargets.Contains(normTarget)) continue;
                    rec.trackedTargets.Add(normTarget);
                    rec.progress = Math.Min(def.RequiredCount, rec.progress + 1);
                    updated++;
                    continue;
                }

                int next = Math.Min(def.RequiredCount, rec.progress + delta);
                if (next != rec.progress)
                {
                    rec.progress = next;
                    updated++;
                }
            }

            if (updated > 0) Changed?.Invoke();
            return updated;
        }

        public bool CanTurnIn(string bountyId, InventoryState inv, out string reason)
        {
            ActiveBountyRecord rec = FindActive(bountyId);
            if (rec == null)
            {
                reason = "NOT ACTIVE";
                return false;
            }
            BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
            if (def == null)
            {
                reason = "UNKNOWN BOUNTY";
                return false;
            }
            int prog = GetEffectiveProgress(rec, inv);
            if (prog < def.RequiredCount)
            {
                reason = $"PROGRESS {prog}/{def.RequiredCount}";
                return false;
            }
            if (def.ObjectiveKind == BountyObjectiveKind.DeliverItem && (inv == null || inv.GetCount(def.TargetId) < def.RequiredCount))
            {
                reason = "MISSING DELIVERY ITEMS";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public bool TryTurnIn(string bountyId, InventoryState inv, SocialState social, out BountyTurnInResult result)
        {
            if (!CanTurnIn(bountyId, inv, out string failReason))
            {
                result = new BountyTurnInResult(false, bountyId, 0, 0, Rank, Rank, failReason);
                return false;
            }

            ActiveBountyRecord rec = FindActive(bountyId);
            BountyDef def = GuildCatalog.GetBounty(rec.bountyId);

            if (def.ObjectiveKind == BountyObjectiveKind.DeliverItem)
            {
                if (!inv.TryConsumeItem(def.TargetId, def.RequiredCount))
                {
                    result = new BountyTurnInResult(false, def.Id, 0, 0, Rank, Rank, "MISSING DELIVERY ITEMS");
                    return false;
                }
            }

            _active.Remove(rec);
            _completedToday.Add(def.Id);
            CompletedCount++;

            GuildRank prevRank = Rank;
            GuildXp += def.GuildXpReward;
            GuildRank newRank = Rank;
            bool rankedUp = newRank > prevRank;

            inv?.AddGold(def.GoldReward);

            if (social != null)
            {
                int aldricRespect = def.AldricRespectDelta + (rankedUp ? RankUpAldricRespectBonus : 0);
                social.ApplyDelta("aldric", new AxisDeltas(def.AldricTrustDelta, 0, aldricRespect), DeltaSource.Scripted);

                if (!string.IsNullOrEmpty(def.SponsorNpcId) && !string.Equals(def.SponsorNpcId, "aldric", StringComparison.OrdinalIgnoreCase))
                {
                    social.ApplyDelta(def.SponsorNpcId, new AxisDeltas(def.SponsorTrustDelta, 0, def.SponsorRespectDelta), DeltaSource.Scripted);
                }

                GossipEngine.RecordAction(social, "aldric", RumorCompletedBounty, valence: 0.65f, strength: 0.72f);
                if (rankedUp)
                {
                    social.ApplyDelta("lyra", new AxisDeltas(0, 0, RankUpLyraRespectBonus), DeltaSource.Scripted);
                    GossipEngine.RecordAction(social, "aldric", RumorEarnedPromotion, valence: 0.85f, strength: 0.90f);
                }
            }

            if (rankedUp)
            {
                RebuildBoardList(Math.Max(1, BoardDay));
            }

            string msg = rankedUp
                ? $"RANK UP: {GuildCatalog.GetRankTitle(newRank)}! (+{def.GoldReward}G, +{def.GuildXpReward} XP)"
                : $"TURNED IN! +{def.GoldReward}G · +{def.GuildXpReward} XP";

            result = new BountyTurnInResult(true, def.Id, def.GoldReward, def.GuildXpReward, prevRank, newRank, msg);
            if (rankedUp) RankChanged?.Invoke(prevRank, newRank);
            Changed?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------ Layer 2 flavor cache

        public string MakeFlavorKey(int day, string bountyId) => $"{Math.Max(1, day)}:{bountyId}";

        public bool TryGetCachedFlavor(int day, string bountyId, out string flavor)
        {
            return _flavorCache.TryGetValue(MakeFlavorKey(day, bountyId), out flavor) && !string.IsNullOrWhiteSpace(flavor);
        }

        public string GetDisplayDescription(BountyDef def, int day)
        {
            if (def == null) return string.Empty;
            if (TryGetCachedFlavor(day, def.Id, out string cached)) return cached;
            return def.StaticDescription;
        }

        public void SetCachedFlavor(int day, string bountyId, string flavor)
        {
            if (string.IsNullOrEmpty(bountyId) || string.IsNullOrWhiteSpace(flavor)) return;
            string key = MakeFlavorKey(day, bountyId);
            if (_flavorCache.TryGetValue(key, out string prev) && string.Equals(prev, flavor, StringComparison.Ordinal)) return;
            _flavorCache[key] = flavor.Trim();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ persistence

        public GuildSaveData ToSaveData(long savedAtUnixMs)
        {
            var data = new GuildSaveData
            {
                version = 1,
                savedAtUnixMs = savedAtUnixMs,
                guildXp = GuildXp,
                completedCount = CompletedCount,
                abandonedCount = AbandonedCount,
                boardDay = BoardDay
            };
            data.boardBountyIds.AddRange(_boardBountyIds);
            for (int i = 0; i < _active.Count; i++)
            {
                var src = _active[i];
                var copy = new ActiveBountyRecord
                {
                    bountyId = src.bountyId,
                    acceptedDay = src.acceptedDay,
                    progress = src.progress
                };
                if (src.trackedTargets != null) copy.trackedTargets.AddRange(src.trackedTargets);
                data.activeBounties.Add(copy);
            }
            foreach (string id in _completedToday) data.completedTodayIds.Add(id);
            foreach (var kv in _flavorCache) data.flavorCache.Add(new CachedFlavorRecord { key = kv.Key, text = kv.Value });
            return data;
        }

        public void LoadFrom(GuildSaveData data, int fallbackDay = 1)
        {
            _boardBountyIds.Clear();
            _active.Clear();
            _completedToday.Clear();
            _flavorCache.Clear();

            if (data == null)
            {
                GuildXp = 0;
                CompletedCount = 0;
                AbandonedCount = 0;
                BoardDay = 0;
                RefreshBoardForDay(Math.Max(1, fallbackDay));
                return;
            }

            GuildXp = Math.Max(0, data.guildXp);
            CompletedCount = Math.Max(0, data.completedCount);
            AbandonedCount = Math.Max(0, data.abandonedCount);
            BoardDay = Math.Max(1, data.boardDay);

            if (data.activeBounties != null)
            {
                for (int i = 0; i < data.activeBounties.Count && _active.Count < MaxActiveBounties; i++)
                {
                    var rec = data.activeBounties[i];
                    if (rec == null) continue;
                    BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
                    if (def == null || IsActive(def.Id)) continue;
                    var copy = new ActiveBountyRecord
                    {
                        bountyId = def.Id,
                        acceptedDay = Math.Max(1, rec.acceptedDay),
                        progress = Math.Clamp(rec.progress, 0, def.RequiredCount)
                    };
                    if (rec.trackedTargets != null) copy.trackedTargets.AddRange(rec.trackedTargets);
                    _active.Add(copy);
                }
            }

            if (data.completedTodayIds != null)
            {
                for (int i = 0; i < data.completedTodayIds.Count; i++)
                {
                    string id = data.completedTodayIds[i];
                    if (GuildCatalog.GetBounty(id) != null) _completedToday.Add(id);
                }
            }

            if (data.boardBountyIds != null)
            {
                for (int i = 0; i < data.boardBountyIds.Count && _boardBountyIds.Count < MaxBoardEntries; i++)
                {
                    string id = data.boardBountyIds[i];
                    if (GuildCatalog.GetBounty(id) != null && !_boardBountyIds.Contains(id))
                        _boardBountyIds.Add(id);
                }
            }

            if (data.flavorCache != null)
            {
                for (int i = 0; i < data.flavorCache.Count; i++)
                {
                    var kv = data.flavorCache[i];
                    if (kv != null && !string.IsNullOrEmpty(kv.key) && !string.IsNullOrWhiteSpace(kv.text))
                        _flavorCache[kv.key] = kv.text.Trim();
                }
            }

            if (_boardBountyIds.Count == 0) RebuildBoardList(BoardDay);
            Changed?.Invoke();
        }

        public void ResetToStarter(int day = 1)
        {
            LoadFrom(null, day);
        }
    }
}
