using System;
using System.Collections.Generic;

namespace ProjectFrontier.Social
{
    /// <summary>The player's standing with one villager.</summary>
    public sealed class Relationship
    {
        public string NpcId { get; }
        public int Trust { get; private set; }
        public int Affection { get; private set; }
        public int Respect { get; private set; }
        public RelationshipState State { get; private set; } = RelationshipState.Neutral;

        public Relationship(string npcId) { NpcId = npcId; }

        public int Get(RelationshipAxis axis) => axis switch
        {
            RelationshipAxis.Trust => Trust,
            RelationshipAxis.Affection => Affection,
            _ => Respect
        };

        /// <summary>Adds already-clamped deltas, clamps axes to range, then re-resolves state.
        /// Returns the delta that actually landed (may be smaller near the axis bounds).</summary>
        internal AxisDeltas Apply(AxisDeltas d)
        {
            int t = RelationshipRules.ClampAxis(Trust + d.trust);
            int a = RelationshipRules.ClampAxis(Affection + d.affection);
            int r = RelationshipRules.ClampAxis(Respect + d.respect);
            var landed = new AxisDeltas(t - Trust, a - Affection, r - Respect);
            Trust = t; Affection = a; Respect = r;
            State = RelationshipRules.Resolve(State, Trust, Affection, Respect);
            return landed;
        }

        /// <summary>Restores saved values. Out-of-range data is clamped; an invalid state is re-evaluated.</summary>
        internal void Restore(int trust, int affection, int respect, int state)
        {
            Trust = RelationshipRules.ClampAxis(trust);
            Affection = RelationshipRules.ClampAxis(affection);
            Respect = RelationshipRules.ClampAxis(respect);
            State = Enum.IsDefined(typeof(RelationshipState), state)
                ? (RelationshipState)state
                : RelationshipRules.Evaluate(Trust, Affection, Respect);
        }

        /// <summary>Compact line for the LLM prompt (#13), e.g. "Standing: Friend (trust 40, affection 28, respect 5)".</summary>
        public string ToPromptLine() => $"Standing: {State} (trust {Trust}, affection {Affection}, respect {Respect})";
    }

    public readonly struct RelationshipChange
    {
        public readonly string NpcId;
        public readonly DeltaSource Source;
        public readonly AxisDeltas Requested;
        public readonly AxisDeltas Applied;
        public readonly RelationshipState Before;
        public readonly RelationshipState After;

        public RelationshipChange(string npcId, DeltaSource source, AxisDeltas requested, AxisDeltas applied,
            RelationshipState before, RelationshipState after)
        {
            NpcId = npcId; Source = source; Requested = requested; Applied = applied; Before = before; After = after;
        }

        public bool StateChanged => Before != After;
    }

    /// <summary>A belief one villager holds about the player (populated by the Gossip engine, #16).</summary>
    [Serializable]
    public class Rumor
    {
        public string holderId;   // villager currently believing it
        public string sourceId;   // villager it originated from
        public string tag;        // e.g. "insulted_craft"
        public float valence;     // [-1, 1]
        public float strength;    // [0, 1]
        public int expiresDay;
    }

    /// <summary>
    /// The whole social simulation state: day counter, one <see cref="Relationship"/> per villager, active rumors.
    /// Pure C# so it can be unit-tested without a scene. The only way to change a relationship is
    /// <see cref="ApplyDelta"/>, which always clamps by source.
    /// </summary>
    public sealed class SocialState
    {
        public const int CurrentSaveVersion = 1;

        private readonly Dictionary<string, Relationship> _relationships = new Dictionary<string, Relationship>();
        private readonly List<Rumor> _rumors = new List<Rumor>();
        private readonly HashSet<string> _met = new HashSet<string>();

        public int Day { get; private set; } = 1;
        public IReadOnlyList<Rumor> Rumors => _rumors;
        public IEnumerable<Relationship> All => _relationships.Values;

        public event Action<RelationshipChange> RelationshipChanged;

        public Relationship Get(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) throw new ArgumentException("npcId is required", nameof(npcId));
            if (!_relationships.TryGetValue(npcId, out var rel))
            {
                rel = new Relationship(npcId);
                _relationships.Add(npcId, rel);
            }
            return rel;
        }

        public RelationshipChange ApplyDelta(string npcId, AxisDeltas requested, DeltaSource source)
        {
            Relationship rel = Get(npcId);
            RelationshipState before = rel.State;
            AxisDeltas applied = rel.Apply(RelationshipRules.ClampDeltas(requested, source));
            var change = new RelationshipChange(npcId, source, requested, applied, before, rel.State);
            if (!applied.IsZero || change.StateChanged) RelationshipChanged?.Invoke(change);
            return change;
        }

        /// <summary>Whether the player has ever finished a conversation with this villager (persists across runs).</summary>
        public bool HasMet(string npcId) => !string.IsNullOrEmpty(npcId) && _met.Contains(npcId);
        public void MarkMet(string npcId) { if (!string.IsNullOrEmpty(npcId)) _met.Add(npcId); }

        /// <summary>Advances the day and drops rumors that have expired.</summary>
        public void AdvanceDay()
        {
            Day++;
            _rumors.RemoveAll(r => r.expiresDay < Day);
        }

        public void AddRumor(Rumor rumor)
        {
            if (rumor == null || string.IsNullOrEmpty(rumor.holderId)) return;
            rumor.valence = Math.Clamp(rumor.valence, -1f, 1f);
            rumor.strength = Math.Clamp(rumor.strength, 0f, 1f);
            _rumors.Add(rumor);
        }

        // ------------------------------------------------------------------ persistence

        public SocialSaveData ToSaveData(long savedAtUnixMs)
        {
            var data = new SocialSaveData { version = CurrentSaveVersion, day = Day, savedAtUnixMs = savedAtUnixMs };
            foreach (var rel in _relationships.Values)
            {
                data.relationships.Add(new RelationshipRecord
                {
                    npcId = rel.NpcId, trust = rel.Trust, affection = rel.Affection, respect = rel.Respect, state = (int)rel.State
                });
            }
            data.metNpcIds.AddRange(_met);
            foreach (var r in _rumors)
            {
                data.rumors.Add(new Rumor
                {
                    holderId = r.holderId, sourceId = r.sourceId, tag = r.tag, valence = r.valence, strength = r.strength, expiresDay = r.expiresDay
                });
            }
            return data;
        }

        /// <summary>Replaces all state with <paramref name="data"/>. Null or malformed records are skipped.</summary>
        public void LoadFrom(SocialSaveData data)
        {
            _relationships.Clear();
            _rumors.Clear();
            _met.Clear();
            Day = 1;
            if (data == null) return;

            Day = Math.Max(1, data.day);
            if (data.relationships != null)
            {
                foreach (var rec in data.relationships)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.npcId)) continue;
                    Get(rec.npcId).Restore(rec.trust, rec.affection, rec.respect, rec.state);
                }
            }
            if (data.metNpcIds != null)
            {
                foreach (var id in data.metNpcIds) MarkMet(id);
            }
            if (data.rumors != null)
            {
                foreach (var r in data.rumors)
                {
                    if (r != null && r.expiresDay >= Day) AddRumor(r);
                }
            }
        }
    }

    [Serializable]
    public class RelationshipRecord
    {
        public string npcId;
        public int trust;
        public int affection;
        public int respect;
        public int state;
    }

    /// <summary>JsonUtility- and Cloud Save-friendly snapshot (lists only, no dictionaries).</summary>
    [Serializable]
    public class SocialSaveData
    {
        public int version = SocialState.CurrentSaveVersion;
        public int day = 1;
        public long savedAtUnixMs;
        public List<RelationshipRecord> relationships = new List<RelationshipRecord>();
        public List<string> metNpcIds = new List<string>();
        public List<Rumor> rumors = new List<Rumor>();

        /// <summary>Cloud vs local reconcile: the most recently saved snapshot wins; null loses.</summary>
        public static SocialSaveData PickNewest(SocialSaveData a, SocialSaveData b)
        {
            if (a == null) return b;
            if (b == null) return a;
            if (a.savedAtUnixMs != b.savedAtUnixMs) return a.savedAtUnixMs > b.savedAtUnixMs ? a : b;
            return a.day >= b.day ? a : b;
        }
    }
}
