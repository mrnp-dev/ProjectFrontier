using System;

namespace ProjectFrontier.Social
{
    public enum RelationshipAxis { Trust, Affection, Respect }

    /// <summary>Ordered by warmth: a relationship may only move one rank per update.</summary>
    public enum RelationshipState { Enemy = 0, Neutral = 1, Friend = 2, Lover = 3 }

    /// <summary>
    /// Who is asking for a change. Each source has its own per-update cap so no single system
    /// (least of all the LLM) can swing a relationship far in one step.
    /// </summary>
    public enum DeltaSource
    {
        /// <summary>Suggested by the LLM in a chat turn. Tightest cap.</summary>
        LlmTurn,
        /// <summary>Rumor bias from the Gossip engine (#16).</summary>
        Gossip,
        /// <summary>Deterministic game rules (gifts, quests, first meeting).</summary>
        Scripted
    }

    /// <summary>
    /// Signed change to the three relationship axes. Deliberately has no gold / item / quest fields:
    /// the LLM turn contract can only ever express relationship nudges.
    /// </summary>
    [Serializable]
    public struct AxisDeltas : IEquatable<AxisDeltas>
    {
        public int trust;
        public int affection;
        public int respect;

        public AxisDeltas(int trust, int affection, int respect)
        {
            this.trust = trust;
            this.affection = affection;
            this.respect = respect;
        }

        public static readonly AxisDeltas Zero = default;
        public bool IsZero => trust == 0 && affection == 0 && respect == 0;

        public AxisDeltas ClampEach(int limit)
        {
            limit = Math.Abs(limit);
            return new AxisDeltas(
                Math.Clamp(trust, -limit, limit),
                Math.Clamp(affection, -limit, limit),
                Math.Clamp(respect, -limit, limit));
        }

        public bool Equals(AxisDeltas o) => trust == o.trust && affection == o.affection && respect == o.respect;
        public override bool Equals(object obj) => obj is AxisDeltas d && Equals(d);
        public override int GetHashCode() => (trust * 397) ^ (affection * 31) ^ respect;
        public override string ToString() => $"(T{trust:+0;-0;0} A{affection:+0;-0;0} R{respect:+0;-0;0})";
    }

    /// <summary>
    /// All relationship tuning in one place (GDD §5). Pure functions: no Unity, no state.
    /// Axes live in [AxisMin, AxisMax]; states resolve from axes with hysteresis and a one-rank-per-update limit.
    /// </summary>
    public static class RelationshipRules
    {
        public const int AxisMin = -100;
        public const int AxisMax = 100;

        public const int LlmTurnCap = 5;   // TECH_SPEC: clamp LLM deltas to [-5, +5] per turn
        public const int GossipCap = 5;
        public const int ScriptedCap = 25;

        // Thresholds to ENTER a state.
        public const int LoverMinAffection = 75;
        public const int LoverMinTrust = 60;
        public const int FriendMinTrust = 35;
        public const int FriendMinAffection = 25;
        public const int WarmMinRespect = -30;   // Friend/Lover need at least grudging respect
        public const int EnemyMaxTrust = -40;
        public const int EnemyMaxAffection = -50;

        /// <summary>Extra slack before LEAVING a state, so values near a line don't flicker.</summary>
        public const int Hysteresis = 8;

        public static int CapFor(DeltaSource source) => source switch
        {
            DeltaSource.LlmTurn => LlmTurnCap,
            DeltaSource.Gossip => GossipCap,
            _ => ScriptedCap
        };

        public static AxisDeltas ClampDeltas(AxisDeltas requested, DeltaSource source) => requested.ClampEach(CapFor(source));

        public static int ClampAxis(int value) => Math.Clamp(value, AxisMin, AxisMax);

        /// <summary>True if the axes satisfy <paramref name="state"/>. <paramref name="slack"/> loosens the
        /// thresholds (used to decide whether to stay in the current state).</summary>
        public static bool Qualifies(RelationshipState state, int trust, int affection, int respect, int slack = 0)
        {
            switch (state)
            {
                case RelationshipState.Lover:
                    return affection >= LoverMinAffection - slack && trust >= LoverMinTrust - slack && respect >= WarmMinRespect - slack;
                case RelationshipState.Friend:
                    return trust >= FriendMinTrust - slack && affection >= FriendMinAffection - slack && respect >= WarmMinRespect - slack;
                case RelationshipState.Enemy:
                    return trust <= EnemyMaxTrust + slack || affection <= EnemyMaxAffection + slack;
                default:
                    return true;
            }
        }

        /// <summary>The state the axes point to with no history (strict thresholds).</summary>
        public static RelationshipState Evaluate(int trust, int affection, int respect)
        {
            if (Qualifies(RelationshipState.Enemy, trust, affection, respect)) return RelationshipState.Enemy;
            if (Qualifies(RelationshipState.Lover, trust, affection, respect)) return RelationshipState.Lover;
            if (Qualifies(RelationshipState.Friend, trust, affection, respect)) return RelationshipState.Friend;
            return RelationshipState.Neutral;
        }

        /// <summary>
        /// Next state given the current one: hysteresis keeps the current state while it still qualifies
        /// within <see cref="Hysteresis"/>, and any change moves at most one rank (Enemy can never jump to Lover).
        /// </summary>
        public static RelationshipState Resolve(RelationshipState current, int trust, int affection, int respect)
        {
            RelationshipState target = Evaluate(trust, affection, respect);
            if (target == current) return current;

            bool movingAway = current != RelationshipState.Neutral &&
                              (current == RelationshipState.Enemy ? target > current : target < current);
            if (movingAway && Qualifies(current, trust, affection, respect, Hysteresis)) return current;

            return target > current ? current + 1 : current - 1;
        }
    }
}
