using System;
using System.Collections.Generic;

namespace ProjectFrontier.Social
{
    /// <summary>
    /// Hidden NPC-to-NPC trust weights (GDD §6). <c>Get(listener, speaker)</c> is how much the listener
    /// believes what the speaker tells them, in [0, 1]. Unknown villagers default to <see cref="DefaultTrust"/>.
    /// </summary>
    public sealed class GossipTrustMatrix
    {
        public const float DefaultTrust = 0.5f;

        private readonly string[] _ids;
        private readonly float[] _weights;   // row = listener, column = speaker

        public GossipTrustMatrix(string[] ids)
        {
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _weights = new float[_ids.Length * _ids.Length];
            for (int i = 0; i < _weights.Length; i++) _weights[i] = DefaultTrust;
            for (int i = 0; i < _ids.Length; i++) _weights[i * _ids.Length + i] = 1f;
        }

        public int Count => _ids.Length;
        public IReadOnlyList<string> Ids => _ids;

        public int IndexOf(string id)
        {
            for (int i = 0; i < _ids.Length; i++) if (_ids[i] == id) return i;
            return -1;
        }

        public float Get(string listenerId, string speakerId)
        {
            int l = IndexOf(listenerId), s = IndexOf(speakerId);
            if (l < 0 || s < 0) return DefaultTrust;
            return _weights[l * _ids.Length + s];
        }

        public void Set(string listenerId, string speakerId, float trust)
        {
            int l = IndexOf(listenerId), s = IndexOf(speakerId);
            if (l < 0 || s < 0) throw new ArgumentException($"Unknown villager pair {listenerId}/{speakerId}");
            _weights[l * _ids.Length + s] = Math.Clamp(trust, 0f, 1f);
        }

        public static readonly string[] Cast = { "bram", "mira", "aldric", "lyra", "pip" };

        /// <summary>The 5x5 default matrix for the Oakhaven cast (rows: listener, columns: speaker).</summary>
        public static GossipTrustMatrix CreateDefault()
        {
            var m = new GossipTrustMatrix(Cast);
            // Bram: trusts Mira, respects Aldric, distrusts Lyra (GDD example).
            m.Set("bram", "mira", 0.8f); m.Set("bram", "aldric", 0.6f); m.Set("bram", "lyra", 0.2f); m.Set("bram", "pip", 0.5f);
            // Mira: rumor broker, believes most people; Bram's word carries weight (PRD gossip ripple: 0.8).
            m.Set("mira", "bram", 0.8f); m.Set("mira", "aldric", 0.6f); m.Set("mira", "lyra", 0.5f); m.Set("mira", "pip", 0.7f);
            // Aldric: by-the-book, trusts Mira's ear and Bram's steadiness, wary of the young ones.
            m.Set("aldric", "bram", 0.6f); m.Set("aldric", "mira", 0.7f); m.Set("aldric", "lyra", 0.4f); m.Set("aldric", "pip", 0.4f);
            // Lyra: rival of Bram, close to Pip.
            m.Set("lyra", "bram", 0.3f); m.Set("lyra", "mira", 0.6f); m.Set("lyra", "aldric", 0.5f); m.Set("lyra", "pip", 0.6f);
            // Pip: believes nearly everyone, idolises Lyra and Mira.
            m.Set("pip", "bram", 0.6f); m.Set("pip", "mira", 0.8f); m.Set("pip", "aldric", 0.5f); m.Set("pip", "lyra", 0.7f);
            return m;
        }
    }

    /// <summary>
    /// Rumor tags for notable player actions, plus how to phrase them. "Origin" phrasing is used when the
    /// villager who was wronged/helped is the one telling it ("mocked his smithing"); "relay" phrasing names them.
    /// </summary>
    public static class RumorCatalog
    {
        private struct Entry
        {
            public string npcId, tag, originPhrase, relayPhrase;
            public bool positive;
            public Entry(string npcId, bool positive, string tag, string originPhrase, string relayPhrase)
            { this.npcId = npcId; this.positive = positive; this.tag = tag; this.originPhrase = originPhrase; this.relayPhrase = relayPhrase; }
        }

        private static readonly Entry[] Entries =
        {
            new Entry("bram", false, "mocked_bram_smithing", "mocked his smithing", "mocked Bram's smithing"),
            new Entry("bram", true, "praised_bram_smithing", "praised his smithing", "praised Bram's smithing"),
            new Entry("mira", false, "was_rude_to_mira", "was rude to her at the tavern", "was rude to Mira at the tavern"),
            new Entry("mira", true, "was_kind_to_mira", "was kind to her at the tavern", "was kind to Mira at the tavern"),
            new Entry("aldric", false, "disrespected_the_guild", "disrespected the Guild", "disrespected the Guild"),
            new Entry("aldric", true, "honored_the_guild", "showed the Guild real respect", "showed the Guild real respect"),
            new Entry("aldric", true, "completed_guild_bounty", "completed a Guild bounty with discipline", "completed a Guild bounty for Aldric"),
            new Entry("aldric", true, "earned_guild_promotion", "earned a Guild rank promotion", "earned a Guild rank promotion under Aldric"),
            new Entry("aldric", false, "abandoned_guild_contract", "walked away from a signed Guild contract", "walked away from one of Aldric's Guild contracts"),
            new Entry("lyra", false, "belittled_lyra", "belittled her skills", "belittled Lyra's skills"),
            new Entry("lyra", true, "impressed_lyra", "impressed her", "impressed Lyra"),
            new Entry("pip", false, "was_mean_to_pip", "was mean to him", "was mean to Pip"),
            new Entry("pip", true, "was_gentle_with_pip", "was gentle with him", "was gentle with Pip"),
        };

        /// <summary>Tag for a notable chat with <paramref name="npcId"/>; generic fallback for unknown villagers.</summary>
        public static string TagFor(string npcId, bool positive)
        {
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].npcId == npcId && Entries[i].positive == positive) return Entries[i].tag;
            return positive ? "was_kind_to_" + npcId : "was_rude_to_" + npcId;
        }

        /// <summary>"mocked his smithing" / "mocked Bram's smithing"; unknown tags fall back to underscores -> spaces.</summary>
        public static string Describe(string tag, bool toldByOrigin)
        {
            if (string.IsNullOrEmpty(tag)) return "did something";
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].tag == tag) return toldByOrigin ? Entries[i].originPhrase : Entries[i].relayPhrase;
            return tag.Replace('_', ' ');
        }
    }

    /// <summary>One rumor passed from a speaker to a listener during an exchange (for logs, UI and tests).</summary>
    public readonly struct GossipTransfer
    {
        public readonly string SpeakerId;
        public readonly string ListenerId;
        public readonly string OriginId;
        public readonly string Tag;
        public readonly float BiasDelta;
        public readonly AxisDeltas Applied;

        public GossipTransfer(string speakerId, string listenerId, string originId, string tag, float biasDelta, AxisDeltas applied)
        {
            SpeakerId = speakerId; ListenerId = listenerId; OriginId = originId; Tag = tag; BiasDelta = biasDelta; Applied = applied;
        }

        public override string ToString() =>
            $"{SpeakerId}->{ListenerId} '{Tag}' (origin {OriginId}) bias {BiasDelta:+0.00;-0.00;0.00} {Applied}";
    }

    /// <summary>
    /// Deterministic, zero-token Gossip &amp; Bias Engine (GDD §6 / TECH_SPEC). Pure C#:
    /// <list type="number">
    /// <item>Notable player chats create a first-hand <see cref="Rumor"/> held by that villager.</item>
    /// <item>In the evening, villagers sharing a schedule spot tell each other their strongest live rumor:
    /// <c>biasDelta = valence * listenerTrustInSpeaker * strength</c> shifts the listener's Trust and Affection
    /// (clamped as <see cref="DeltaSource.Gossip"/>) and the listener stores the rumor until it expires.</item>
    /// <item>The strongest heard rumor becomes the one-line <c>RUMOR:</c> prompt hint and a scripted greeting line.</item>
    /// </list>
    /// </summary>
    public static class GossipEngine
    {
        /// <summary>A chat counts as notable when any applied axis moved at least this much...</summary>
        public const int NotableAxisDelta = 4;
        /// <summary>...or the applied axes sum to at least this much in one direction.</summary>
        public const int NotableNetDelta = 6;
        /// <summary>How many days a new rumor stays live (GDD example: created day 2, expires day 6).</summary>
        public const int RumorLifetimeDays = 4;
        /// <summary>Axis points per 1.0 of bias. bias -0.38 -> -3 points; the Gossip cap (±5) still applies.</summary>
        public const float BiasPointsScale = 8f;
        /// <summary>Heard rumors weaker than this are forgotten instead of stored.</summary>
        public const float MinBeliefToStore = 0.1f;

        public static bool IsNotable(AxisDeltas applied)
        {
            int net = applied.trust + applied.affection + applied.respect;
            return Math.Abs(applied.trust) >= NotableAxisDelta || Math.Abs(applied.affection) >= NotableAxisDelta ||
                   Math.Abs(applied.respect) >= NotableAxisDelta || Math.Abs(net) >= NotableNetDelta;
        }

        /// <summary>
        /// Records a notable player action with <paramref name="npcId"/> as a first-hand rumor. Repeating the same kind of
        /// action strengthens and refreshes the existing rumor instead of duplicating it. Returns the rumor, or null if not notable.
        /// </summary>
        public static Rumor RecordChatOutcome(SocialState state, string npcId, AxisDeltas applied)
        {
            if (state == null || string.IsNullOrEmpty(npcId) || !IsNotable(applied)) return null;
            int net = applied.trust + applied.affection + applied.respect;
            if (net == 0) return null;
            int magnitude = Math.Abs(applied.trust) + Math.Abs(applied.affection) + Math.Abs(applied.respect);
            float valence = Math.Clamp(net / 15f, -1f, 1f);
            float strength = Math.Clamp(0.4f + 0.04f * magnitude, 0f, 1f);
            return RecordAction(state, npcId, RumorCatalog.TagFor(npcId, net > 0), valence, strength);
        }

        /// <summary>Scripted hook (quests, gifts, raids): creates or reinforces a first-hand rumor.</summary>
        public static Rumor RecordAction(SocialState state, string npcId, string tag, float valence, float strength)
        {
            if (state == null || string.IsNullOrEmpty(npcId) || string.IsNullOrEmpty(tag)) return null;
            int expires = state.Day + RumorLifetimeDays;
            Rumor existing = state.FindRumor(npcId, npcId, tag);
            if (existing != null)
            {
                existing.strength = Math.Clamp(Math.Max(existing.strength, strength) + 0.1f, 0f, 1f);
                existing.valence = Math.Abs(valence) > Math.Abs(existing.valence) ? Math.Clamp(valence, -1f, 1f) : existing.valence;
                existing.expiresDay = Math.Max(existing.expiresDay, expires);
                return existing;
            }
            var rumor = new Rumor
            {
                holderId = npcId, sourceId = npcId, originId = npcId, tag = tag,
                valence = valence, strength = strength, expiresDay = expires, heardDay = state.Day
            };
            state.AddRumor(rumor);
            return rumor;
        }

        /// <summary>Strongest live rumor this villager could pass on (by strength, then |valence|, then list order).</summary>
        public static Rumor TopRumor(SocialState state, string holderId)
        {
            if (state == null) return null;
            Rumor best = null;
            var list = state.RumorList;
            for (int i = 0; i < list.Count; i++)
            {
                Rumor r = list[i];
                if (r.holderId != holderId || !r.IsLive(state.Day)) continue;
                if (best == null || r.strength > best.strength ||
                    (r.strength == best.strength && Math.Abs(r.valence) > Math.Abs(best.valence))) best = r;
            }
            return best;
        }

        /// <summary>Strongest live second-hand rumor this villager heard (the one shown in prompts and greetings).</summary>
        public static Rumor TopHeardRumor(IReadOnlyList<Rumor> rumors, string holderId, int day)
        {
            if (rumors == null || string.IsNullOrEmpty(holderId)) return null;
            Rumor best = null;
            float bestScore = 0f;
            for (int i = 0; i < rumors.Count; i++)
            {
                Rumor r = rumors[i];
                if (r == null || r.holderId != holderId || !r.IsLive(day) || r.IsFirstHand) continue;
                float score = Math.Abs(r.valence) * r.strength;
                if (best == null || score > bestScore) { best = r; bestScore = score; }
            }
            return best;
        }

        /// <summary>
        /// Runs one evening exchange. Every speaker's top rumor is snapshotted first, so the result does not depend on
        /// iteration order and a rumor travels at most one hop per evening. Returns the number of transfers.
        /// </summary>
        public static int RunExchange(SocialState state, GossipTrustMatrix trust, IReadOnlyList<IReadOnlyList<string>> groups,
            List<GossipTransfer> transfers = null)
        {
            if (state == null || trust == null || groups == null) return 0;
            int day = state.Day;
            int count = 0;

            for (int g = 0; g < groups.Count; g++)
            {
                IReadOnlyList<string> group = groups[g];
                if (group == null || group.Count < 2) continue;

                // Snapshot (clones) before anybody hears anything in this group.
                var told = new Rumor[group.Count];
                for (int s = 0; s < group.Count; s++)
                {
                    Rumor top = TopRumor(state, group[s]);
                    told[s] = top?.Clone();
                }

                for (int s = 0; s < group.Count; s++)
                {
                    Rumor rumor = told[s];
                    if (rumor == null) continue;
                    string speaker = group[s];
                    string origin = rumor.Origin;

                    for (int l = 0; l < group.Count; l++)
                    {
                        string listener = group[l];
                        if (l == s || listener == speaker || listener == origin) continue;
                        if (state.FindRumor(listener, origin, rumor.tag) != null) continue;   // already knows

                        float listenerTrust = trust.Get(listener, speaker);
                        float bias = rumor.valence * listenerTrust * rumor.strength;
                        int points = (int)Math.Round(bias * BiasPointsScale, MidpointRounding.AwayFromZero);
                        AxisDeltas applied = AxisDeltas.Zero;
                        if (points != 0)
                            applied = state.ApplyDelta(listener, new AxisDeltas(points, points, 0), DeltaSource.Gossip).Applied;

                        float belief = rumor.strength * listenerTrust;
                        if (belief >= MinBeliefToStore)
                        {
                            state.AddRumor(new Rumor
                            {
                                holderId = listener, sourceId = speaker, originId = origin, tag = rumor.tag,
                                valence = rumor.valence, strength = belief, expiresDay = rumor.expiresDay, heardDay = day,
                                appliedTrust = applied.trust, appliedAffection = applied.affection
                            });
                        }
                        transfers?.Add(new GossipTransfer(speaker, listener, origin, rumor.tag, bias, applied));
                        count++;
                    }
                }
            }
            state.MarkGossipRan(day);
            return count;
        }

        /// <summary>
        /// One-line prompt hint (~15 tokens), e.g. "RUMOR: Bram says the player mocked his smithing (you mostly believe Bram)."
        /// Only second-hand rumors are injected: a villager already remembers what you said to them.
        /// </summary>
        public static string BuildPromptLine(IReadOnlyList<Rumor> rumors, string holderId, int day, Func<string, string> nameOf)
        {
            Rumor r = TopHeardRumor(rumors, holderId, day);
            if (r == null) return null;
            string source = NameOf(nameOf, r.sourceId);
            string what = RumorCatalog.Describe(r.tag, r.sourceId == r.Origin);
            string belief = r.strength >= 0.7f ? "you believe " + source
                : r.strength >= 0.35f ? "you mostly believe " + source
                : "you doubt " + source;
            return $"RUMOR: {source} says the player {what} ({belief}).";
        }

        /// <summary>Scripted (0-token) greeting opener for a villager who heard a rumor, or null.</summary>
        public static string BuildGreetingLine(IReadOnlyList<Rumor> rumors, string holderId, int day, Func<string, string> nameOf)
        {
            Rumor r = TopHeardRumor(rumors, holderId, day);
            if (r == null) return null;
            string source = NameOf(nameOf, r.sourceId);
            string what = RumorCatalog.Describe(r.tag, r.sourceId == r.Origin);
            if (r.valence < 0f)
                return r.strength >= 0.35f ? $"{source} told me you {what}. Not a good look." : $"{source} claims you {what}. I'm not sure I buy it.";
            return r.strength >= 0.35f ? $"{source} says you {what}. Good to hear." : $"{source} mentioned you {what}, if that's true.";
        }

        private static string NameOf(Func<string, string> nameOf, string id)
        {
            string n = nameOf?.Invoke(id);
            if (!string.IsNullOrEmpty(n)) return n;
            if (string.IsNullOrEmpty(id)) return "Someone";
            return char.ToUpperInvariant(id[0]) + id.Substring(1);
        }
    }
}
