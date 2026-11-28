using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectFrontier.Social
{
    /// <summary>Where a villager learned a fact (#42).</summary>
    public enum MemorySource
    {
        PlayerChat = 0,
        Gossip = 1,
        Witnessed = 2
    }

    /// <summary>
    /// One long-term memory a villager holds (#42), Hindsight-style: a short fact plus the entities it is about.
    /// Flavour only: memories never change gold, items, quests or relationships.
    /// JsonUtility-friendly (public fields, arrays) so it rides inside <see cref="SocialSaveData"/> and Cloud Save.
    /// </summary>
    [Serializable]
    public class MemoryFact
    {
        public string holderId;   // villager who remembers it
        public string text;       // one short sentence, e.g. "The player's favourite crop is pumpkins."
        public string[] about;    // lowercase entities/keywords, e.g. { "player", "pumpkins" }
        public string key;        // optional topic slot ("favorite_crop"): a newer fact on the same slot replaces the old one
        public int source;        // MemorySource
        public string fromId;     // who told them (gossip) or the villager themself (player chat)
        public int day;           // day learned (or last reinforced)
        public int importance;    // 1 (trivia) .. 3 (promise, strong preference)
        public string mood;       // holder's mood when it was learned (optional)

        public MemorySource Source => (MemorySource)source;

        public MemoryFact Clone()
        {
            var c = (MemoryFact)MemberwiseClone();
            c.about = about == null ? null : (string[])about.Clone();
            return c;
        }

        public override string ToString() => $"{holderId} d{day} i{importance} {(MemorySource)source}: {text}";
    }

    /// <summary>
    /// Per-villager long-term memory: retain (dedupe + newest-wins), recall (on-device keyword/entity + recency +
    /// importance scoring, no AI call) and end-of-day consolidation with a hard cap. Pure C# for EditMode tests.
    /// </summary>
    public sealed class NpcMemoryStore
    {
        public const int MaxFactsPerNpc = 40;
        public const int MaxFactChars = 90;
        public const int DefaultRecallCount = 3;
        /// <summary>Hard cap for the rendered prompt block (~90 tokens at 4 chars per token).</summary>
        public const int MaxPromptBlockChars = 360;
        /// <summary>Two facts whose keyword sets overlap at least this much are the same memory.</summary>
        public const float DuplicateSimilarity = 0.7f;

        private readonly List<MemoryFact> _facts = new List<MemoryFact>();

        public IReadOnlyList<MemoryFact> All => _facts;

        public int Count(string holderId)
        {
            int n = 0;
            for (int i = 0; i < _facts.Count; i++) if (_facts[i].holderId == holderId) n++;
            return n;
        }

        public List<MemoryFact> For(string holderId)
        {
            var list = new List<MemoryFact>();
            for (int i = 0; i < _facts.Count; i++) if (_facts[i].holderId == holderId) list.Add(_facts[i]);
            return list;
        }

        public void Clear() => _facts.Clear();

        // ------------------------------------------------------------------ retain

        /// <summary>
        /// Stores a fact. Same holder + same <see cref="MemoryFact.key"/>: the newer fact replaces the older one.
        /// Near-duplicate text: the existing fact is reinforced (newest day, highest importance) instead.
        /// Returns the stored fact, or null if the input was empty.
        /// </summary>
        public MemoryFact Retain(MemoryFact fact)
        {
            if (fact == null || string.IsNullOrEmpty(fact.holderId)) return null;
            string text = CleanText(fact.text);
            if (text.Length == 0) return null;

            var f = fact.Clone();
            f.text = text;
            f.importance = Math.Clamp(f.importance, 1, 3);
            f.day = Math.Max(1, f.day);
            f.key = CleanKey(f.key);
            f.about = CleanAbout(f.about);

            if (!string.IsNullOrEmpty(f.key))
            {
                for (int i = 0; i < _facts.Count; i++)
                {
                    MemoryFact old = _facts[i];
                    if (old.holderId != f.holderId || old.key != f.key) continue;
                    if (old.day > f.day) return old;   // keep the newer one we already have
                    _facts[i] = f;                     // newest fact corrects the old one
                    return f;
                }
            }

            var words = Keywords(f.text);
            for (int i = 0; i < _facts.Count; i++)
            {
                MemoryFact old = _facts[i];
                if (old.holderId != f.holderId) continue;
                if (Similarity(words, Keywords(old.text)) < DuplicateSimilarity) continue;
                old.day = Math.Max(old.day, f.day);
                old.importance = Math.Max(old.importance, f.importance);
                return old;
            }

            _facts.Add(f);
            EnforceCap(f.holderId, f.day);
            return f;
        }

        /// <summary>True when the holder already has a fact with (nearly) this text.</summary>
        public bool Knows(string holderId, string text)
        {
            var words = Keywords(CleanText(text));
            for (int i = 0; i < _facts.Count; i++)
                if (_facts[i].holderId == holderId && Similarity(words, Keywords(_facts[i].text)) >= DuplicateSimilarity) return true;
            return false;
        }

        // ------------------------------------------------------------------ recall

        /// <summary>
        /// Top <paramref name="count"/> memories for this turn, best first. Score = 3 x keyword/entity hits with the
        /// player's message + importance + recency (2 today, 1 yesterday, ...) + 0.5 for first-hand player chat.
        /// Mirrors Hindsight's keyword + entity + recency ranking, all on device.
        /// </summary>
        public List<MemoryFact> Recall(string holderId, string query, int day, int count = DefaultRecallCount)
        {
            var scored = new List<(MemoryFact fact, float score)>();
            var q = Keywords(query);
            for (int i = 0; i < _facts.Count; i++)
            {
                MemoryFact f = _facts[i];
                if (f.holderId != holderId) continue;
                scored.Add((f, Score(f, q, day)));
            }
            scored.Sort((a, b) =>
            {
                int c = b.score.CompareTo(a.score);
                return c != 0 ? c : b.fact.day.CompareTo(a.fact.day);
            });
            var result = new List<MemoryFact>(Math.Min(count, scored.Count));
            for (int i = 0; i < scored.Count && result.Count < count; i++) result.Add(scored[i].fact);
            return result;
        }

        public static float Score(MemoryFact f, HashSet<string> queryWords, int day)
        {
            int hits = 0;
            if (queryWords != null && queryWords.Count > 0)
            {
                var words = Keywords(f.text);
                if (f.about != null) foreach (string a in f.about) words.Add(a);
                foreach (string w in queryWords) if (words.Contains(w)) hits++;
            }
            int age = Math.Max(0, day - f.day);
            float recency = 2f / (1f + age);
            float firstHand = f.source == (int)MemorySource.PlayerChat ? 0.5f : 0f;
            return 3f * hits + f.importance + recency + firstHand;
        }

        /// <summary>
        /// Prompt block for recalled memories, at most <see cref="MaxPromptBlockChars"/> (~90 tokens), e.g.
        /// "You remember (bring up only if it fits):\n- Day 1: The player's favourite crop is pumpkins.\n". Null if none.
        /// </summary>
        public static string BuildPromptBlock(IReadOnlyList<MemoryFact> facts, int day)
        {
            if (facts == null || facts.Count == 0) return null;
            const string header = "You remember (bring up only if it fits):\n";
            var sb = new StringBuilder(header, MaxPromptBlockChars);
            int added = 0;
            foreach (MemoryFact f in facts)
            {
                if (f == null || string.IsNullOrEmpty(f.text)) continue;
                string line = "- " + When(f.day, day) + ": " + f.text + "\n";
                if (sb.Length + line.Length > MaxPromptBlockChars) break;
                sb.Append(line);
                added++;
            }
            return added == 0 ? null : sb.ToString();
        }

        private static string When(int factDay, int day)
        {
            int age = day - factDay;
            if (age <= 0) return "today";
            if (age == 1) return "yesterday";
            return age + " days ago";
        }

        // ------------------------------------------------------------------ consolidate

        /// <summary>
        /// End-of-day pass (TECH_SPEC Layer 2): fades old low-importance facts, merges near-duplicates and keys,
        /// and enforces <see cref="MaxFactsPerNpc"/> per villager. Returns how many facts were dropped.
        /// Fade: kept while importance x 3 - age x 0.25 &gt;= 0, i.e. trivia ~12 days, normal ~24, important ~36.
        /// </summary>
        public int Consolidate(int day)
        {
            int before = _facts.Count;
            _facts.RemoveAll(f => Retention(f, day) < 0f);

            // Merge duplicates: re-retain everything through the dedupe rules, oldest first.
            var all = new List<MemoryFact>(_facts);
            all.Sort((a, b) => a.day.CompareTo(b.day));
            _facts.Clear();
            foreach (MemoryFact f in all) Retain(f);

            var holders = new HashSet<string>();
            foreach (MemoryFact f in _facts) holders.Add(f.holderId);
            foreach (string h in holders) EnforceCap(h, day);
            return before - _facts.Count;
        }

        public static float Retention(MemoryFact f, int day) => f.importance * 3f - Math.Max(0, day - f.day) * 0.25f;

        private void EnforceCap(string holderId, int day)
        {
            while (Count(holderId) > MaxFactsPerNpc)
            {
                int weakest = -1;
                for (int i = 0; i < _facts.Count; i++)
                {
                    if (_facts[i].holderId != holderId) continue;
                    if (weakest < 0 || Retention(_facts[i], day) < Retention(_facts[weakest], day)) weakest = i;
                }
                _facts.RemoveAt(weakest);
            }
        }

        // ------------------------------------------------------------------ persistence

        public List<MemoryFact> ToSaveList()
        {
            var list = new List<MemoryFact>(_facts.Count);
            foreach (MemoryFact f in _facts) list.Add(f.Clone());
            return list;
        }

        public void LoadFrom(IEnumerable<MemoryFact> facts)
        {
            _facts.Clear();
            if (facts == null) return;
            foreach (MemoryFact f in facts)
            {
                if (f == null || string.IsNullOrEmpty(f.holderId) || string.IsNullOrWhiteSpace(f.text)) continue;
                Retain(f);
            }
        }

        // ------------------------------------------------------------------ text helpers

        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "and", "or", "but", "to", "of", "in", "on", "at", "for", "with", "is", "are", "was", "were",
            "be", "been", "it", "its", "that", "this", "my", "your", "you", "me", "i", "he", "she", "they", "them", "his",
            "her", "their", "we", "us", "our", "do", "did", "does", "what", "who", "how", "why", "when", "where", "any",
            "have", "has", "had", "about", "told", "tell", "said", "say", "so", "if", "not", "no", "yes", "can", "will",
            "would", "just", "really", "there", "here", "from", "by", "as", "up", "out", "some", "all", "remember", "know"
        };

        /// <summary>Lowercase content words (len &gt;= 3, no stopwords), with a crude plural/possessive strip.</summary>
        public static HashSet<string> Keywords(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return set;
            var word = new StringBuilder(16);
            foreach (char raw in text + " ")
            {
                char c = char.ToLowerInvariant(raw);
                if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }
                if (c == '\'' || c == '\u2019') { continue; }   // "player's" -> "players" -> "player"
                AddWord(set, word.ToString());
                word.Clear();
            }
            return set;
        }

        private static void AddWord(HashSet<string> set, string w)
        {
            if (w.Length < 3 || Stopwords.Contains(w)) return;
            if (w.Length > 4 && w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal))
                w = w.Substring(0, w.Length - 1);
            if (Stopwords.Contains(w)) return;
            set.Add(w);
        }

        public static float Similarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0f;
            int inter = 0;
            foreach (string w in a) if (b.Contains(w)) inter++;
            return (float)inter / (a.Count + b.Count - inter);
        }

        /// <summary>One line, collapsed whitespace, capped at <see cref="MaxFactChars"/> on a word boundary.</summary>
        public static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c == '"' ? '\'' : c);
            }
            string s = sb.ToString();
            if (s.Length <= MaxFactChars) return s;
            int cut = s.LastIndexOf(' ', MaxFactChars - 1);
            return s.Substring(0, cut > 20 ? cut : MaxFactChars - 1).TrimEnd(',', ';', ' ') + "\u2026";
        }

        public static string CleanKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var sb = new StringBuilder(32);
            foreach (char raw in key.Trim().ToLowerInvariant())
            {
                char c = raw == ' ' || raw == '-' ? '_' : raw;
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == ':') sb.Append(c);
                if (sb.Length >= 40) break;
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string[] CleanAbout(string[] about)
        {
            if (about == null) return Array.Empty<string>();
            var list = new List<string>(4);
            foreach (string a in about)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                string v = a.Trim().ToLowerInvariant();
                if (v.Length > 24) v = v.Substring(0, 24);
                if (!list.Contains(v)) list.Add(v);
                if (list.Count == 4) break;
            }
            return list.ToArray();
        }
    }

    /// <summary>
    /// Zero-token NPC-to-NPC memory sharing (#42), run right after the evening rumor exchange:
    /// <list type="bullet">
    /// <item>every rumor transfer becomes a listener memory ("Mira told me the player praised Bram's smithing.");</item>
    /// <item>each speaker passes on their most important recent thing the player told them, to listeners who trust
    /// them at least <see cref="ShareTrust"/> (GossipTrustMatrix). One hop only: relayed facts are not relayed again.</item>
    /// </list>
    /// </summary>
    public static class NpcMemoryGossip
    {
        public const float ShareTrust = 0.5f;
        public const int ShareMaxAgeDays = 7;
        public const int ShareMinImportance = 2;

        public static List<MemoryFact> Share(NpcMemoryStore store, GossipTrustMatrix trust,
            IReadOnlyList<IReadOnlyList<string>> groups, IReadOnlyList<GossipTransfer> transfers, int day,
            Func<string, string> nameOf)
        {
            var created = new List<MemoryFact>();
            if (store == null) return created;

            if (transfers != null)
            {
                foreach (GossipTransfer t in transfers)
                {
                    string what = RumorCatalog.Describe(t.Tag, t.SpeakerId == t.OriginId);
                    var f = store.Retain(new MemoryFact
                    {
                        holderId = t.ListenerId, fromId = t.SpeakerId, source = (int)MemorySource.Gossip, day = day,
                        importance = 2, key = "rumor:" + t.Tag,
                        text = $"{Name(nameOf, t.SpeakerId)} told me the player {what}.",
                        about = new[] { "player", t.OriginId, t.SpeakerId }
                    });
                    if (f != null) created.Add(f);
                }
            }

            if (groups == null || trust == null) return created;
            foreach (IReadOnlyList<string> group in groups)
            {
                if (group == null || group.Count < 2) continue;
                // Snapshot what each speaker would share before anyone hears anything (order-independent).
                var told = new MemoryFact[group.Count];
                for (int s = 0; s < group.Count; s++) told[s] = BestToShare(store, group[s], day)?.Clone();

                for (int s = 0; s < group.Count; s++)
                {
                    MemoryFact fact = told[s];
                    if (fact == null) continue;
                    string speaker = group[s];
                    string relayed = $"{Name(nameOf, speaker)} told me: {fact.text}";
                    for (int l = 0; l < group.Count; l++)
                    {
                        string listener = group[l];
                        if (listener == speaker || trust.Get(listener, speaker) < ShareTrust) continue;
                        if (store.Knows(listener, fact.text) || store.Knows(listener, relayed)) continue;
                        var about = new List<string>(fact.about ?? Array.Empty<string>()) { speaker };
                        var f = store.Retain(new MemoryFact
                        {
                            holderId = listener, fromId = speaker, source = (int)MemorySource.Gossip, day = day,
                            importance = Math.Max(1, fact.importance - 1),
                            key = string.IsNullOrEmpty(fact.key) ? null : speaker + ":" + fact.key,
                            text = relayed, about = about.ToArray()
                        });
                        if (f != null) created.Add(f);
                    }
                }
            }
            return created;
        }

        /// <summary>Most important recent first-hand player fact this villager could pass on, or null.</summary>
        public static MemoryFact BestToShare(NpcMemoryStore store, string holderId, int day)
        {
            MemoryFact best = null;
            foreach (MemoryFact f in store.All)
            {
                if (f.holderId != holderId || f.source != (int)MemorySource.PlayerChat) continue;
                if (f.importance < ShareMinImportance || day - f.day > ShareMaxAgeDays) continue;
                if (best == null || f.importance > best.importance || (f.importance == best.importance && f.day > best.day)) best = f;
            }
            return best;
        }

        private static string Name(Func<string, string> nameOf, string id)
        {
            string n = nameOf?.Invoke(id);
            if (!string.IsNullOrEmpty(n)) return n;
            if (string.IsNullOrEmpty(id)) return "Someone";
            return char.ToUpperInvariant(id[0]) + id.Substring(1);
        }
    }
}
