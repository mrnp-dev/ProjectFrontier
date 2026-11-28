using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Built-in, deterministic lines: keyword sentiment drives mood and small deltas, and the reply
    /// is returned as the same compact JSON a real model sends, so the parser path is always exercised.
    /// The game is online-only (#37): this is the short-drop cushion when a call fails, and the provider for
    /// automated tests and dev machines without a key. It is not an offline play mode.
    /// </summary>
    public sealed class MockLlmProvider : ILLMProvider
    {
        public const string ProviderName = "built-in";
        public string Name => ProviderName;
        public bool RequiresNetwork => false;

        /// <summary>Optional fake latency so the UI's waiting state is visible when no AI key is configured.</summary>
        public int SimulatedLatencyMs;

        private static readonly string[] Positive =
        {
            "thank", "sorry", "apolog", "love", "beautiful", "great", "amazing", "help", "gift", "please", "admire",
            "friend", "nice", "kind", "wonderful", "brave", "impressive", "best", "glad", "appreciate"
        };

        private static readonly string[] Negative =
        {
            "stupid", "hate", "ugly", "joke", "useless", "liar", "idiot", "shut up", "boring", "cheat", "steal",
            "pathetic", "worst", "dumb", "weak", "scam", "trash", "loser"
        };

        private static readonly string[] Curious = { "how", "what", "why", "teach", "tell me", "your work", "your craft", "story" };

        private static readonly string[] PositiveLines =
        {
            "Ha! Kind words. Oakhaven could use more folk like you.",
            "That's good to hear. Come by again soon.",
            "You've a decent heart, newcomer. I'll remember that."
        };

        private static readonly string[] NegativeLines =
        {
            "Watch your tongue, newcomer.",
            "I've no time for that kind of talk.",
            "Say that again and we're done here."
        };

        private static readonly string[] CuriousLines =
        {
            "Now that's a fair question. Sit a while and I'll tell you.",
            "Few bother to ask. It's honest work, and it keeps the village going.",
            "Curious one, aren't you? I like that."
        };

        private static readonly string[] NeutralLines =
        {
            "Hm. What brings you by?",
            "Busy day in Oakhaven. Need something?",
            "Mm-hm. Go on."
        };

        /// <summary>
        /// Topic buckets (#41): when the network provider is down or rate-limited, a question about food,
        /// rumors or the dungeon gets an answer about that topic instead of one of three generic lines.
        /// Checked in order; the first bucket with a keyword hit wins.
        /// </summary>
        private static readonly (string[] keys, string[] lines)[] Topics =
        {
            (new[] { "villager", "other folk", "neighbo", "bram", "mira", "aldric", "lyra", "pip", "everyone" }, new[]
            {
                "Folk here mean well, mostly. Bram's gruff, Mira hears everything, Pip talks to his plants.",
                "Small village, long memories. Treat people fair and they'll do the same.",
                "Ask Mira at the tavern. She knows everyone's business better than I do."
            }),
            (new[] { "time", "what are you up to", "doing", "busy", "day is it" }, new[]
            {
                "Same as every day: work first, talk after. Look at the sky if you want the hour.",
                "Getting through my chores before the light turns. You?",
                "Busy enough. The day doesn't wait for anyone in Oakhaven."
            }),
            (new[] { "shard", "lantern", "relight", "square" }, new[]
            {
                "The lantern shards are scattered along the village paths. Bring them to the Square.",
                "Look along the paths. Five shards, and the Square lantern burns again.",
                "Shards glint at dusk if you know where to look. Try the paths out of the Square."
            }),
            (new[] { "food", "eat", "hungry", "drink", "stew", "favorite" }, new[]
            {
                "A warm bowl from Mira's tavern beats anything else in Oakhaven.",
                "Bread, stew, whatever's hot. I'm not fussy.",
                "Ask Mira. She'll feed you whether you want it or not."
            }),
            (new[] { "rumor", "rumour", "gossip", "news", "heard" }, new[]
            {
                "Word is the Guild is planning a raid beyond the ridge.",
                "Nothing I'd swear to. Mira's tavern is where the talk flows.",
                "People say strange lights moved near the ridge last night."
            }),
            (new[] { "night", "dark", "dusk", "evening", "before" }, new[]
            {
                "Get your errands done and stay near the lanterns once it's dark.",
                "Gather what you need while it's light. Nights are long out here.",
                "Find those lantern shards before dusk, then get somewhere warm."
            }),
            (new[] { "dungeon", "danger", "raid", "monster", "ridge" }, new[]
            {
                "The dungeon past the ridge is no place for the careless. Go armed.",
                "Dangerous enough. Talk to Aldric at the Guild before you go.",
                "Folk come back from that dungeon with scars, or not at all."
            }),
            (new[] { "teach", "learn", "advice", "tip", "useful", "show me" }, new[]
            {
                "Watch more than you talk. That's the first lesson.",
                "Keep your tools clean and your word good. That gets you far here.",
                "Learn the paths by day so you don't lose them by night."
            }),
            (new[] { "trust", "believe", "honest" }, new[]
            {
                "Trust is earned. Keep coming back and we'll see.",
                "You don't have to. Watch what I do, not what I say.",
                "I've lived in Oakhaven a long time. Ask anyone about me."
            }),
            (new[] { "yourself", "who are you", "your work", "your craft", "your job" }, new[]
            {
                "I'm one of Oakhaven's own. Ask me about my work sometime.",
                "Not much to tell. I do my job and keep the village going.",
                "Born and raised here. The village is my whole life."
            }),
        };

        [Serializable] private class WireDeltas { public int trust; public int affection; public int respect; }
        [Serializable] private class WireTurn { public string dialogue; public string mood; public WireDeltas deltas; public string[] quick_replies; }

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (SimulatedLatencyMs > 0) await Task.Delay(SimulatedLatencyMs, ct);

            string text = Respond(request?.PlayerMessage ?? string.Empty, RecentNpcLines(request));
            return new LlmCompletion { Text = text, Model = Name };
        }

        /// <summary>The NPC's own earlier lines in this request's history (assistant JSON or plain text).</summary>
        public static List<string> RecentNpcLines(LlmRequest request)
        {
            var said = new List<string>();
            if (request == null) return said;
            foreach (var msg in request.Messages)
            {
                if (msg.Role != ChatRole.Assistant || string.IsNullOrEmpty(msg.Content)) continue;
                string line = msg.Content;
                if (line.TrimStart().StartsWith("{"))
                {
                    try { line = JsonUtility.FromJson<WireTurn>(line)?.dialogue ?? line; }
                    catch (ArgumentException) { /* not our JSON; compare raw */ }
                }
                said.Add(line);
            }
            return said;
        }

        /// <summary>Pure function of the player's message: same input, same JSON.</summary>
        public static string Respond(string playerMessage) => Respond(playerMessage, null);

        /// <summary>
        /// Pure function of the player's message and the NPC's earlier lines: same input, same JSON.
        /// Lines the NPC already said are skipped while an unused one is left in the chosen bucket.
        /// </summary>
        public static string Respond(string playerMessage, IReadOnlyCollection<string> alreadySaid)
        {
            string m = (playerMessage ?? string.Empty).ToLowerInvariant();
            int pos = Count(m, Positive), neg = Count(m, Negative), cur = Count(m, Curious);
            int pick = StableHash(m);
            string[] topic = MatchTopic(m);

            var turn = new WireTurn { deltas = new WireDeltas() };
            if (neg > pos)
            {
                turn.mood = neg >= 2 ? "angry" : "annoyed";
                turn.dialogue = Pick(NegativeLines, pick, alreadySaid);
                turn.deltas.trust = -Math.Min(5, 2 + neg);
                turn.deltas.affection = -Math.Min(5, 1 + neg);
                turn.deltas.respect = -1;
                turn.quick_replies = new[] { "Sorry, I crossed a line.", "Just testing your temper.", "Fine, I'll go." };
            }
            else if (pos > 0)
            {
                turn.mood = "happy";
                turn.dialogue = Pick(PositiveLines, pick, alreadySaid);
                turn.deltas.trust = Math.Min(5, 1 + pos);
                turn.deltas.affection = Math.Min(5, pos);
                turn.deltas.respect = cur > 0 ? 1 : 0;
                turn.quick_replies = new[] { "Glad to meet you.", "What do you do here?", "See you tomorrow!" };
            }
            else if (topic != null)
            {
                turn.mood = "neutral";
                turn.dialogue = Pick(topic, pick, alreadySaid);
                turn.deltas.trust = cur > 0 ? 1 : 0;
                turn.quick_replies = new[] { "Tell me more.", "Any news in town?", "Thanks, see you." };
            }
            else if (cur > 0)
            {
                turn.mood = "proud";
                turn.dialogue = Pick(CuriousLines, pick, alreadySaid);
                turn.deltas.trust = 1;
                turn.deltas.respect = 1;
                turn.quick_replies = new[] { "Tell me more.", "That sounds hard.", "Can I help?" };
            }
            else
            {
                turn.mood = "neutral";
                turn.dialogue = Pick(NeutralLines, pick, alreadySaid);
                turn.quick_replies = new[] { "Tell me about yourself.", "Any news in town?", "Goodbye for now." };
            }
            return JsonUtility.ToJson(turn);
        }

        private static string[] MatchTopic(string lowered)
        {
            foreach (var (keys, lines) in Topics)
                foreach (string k in keys)
                    if (lowered.Contains(k)) return lines;
            return null;
        }

        /// <summary>Hash-picked line, stepping past ones already said; repeats only when the bucket is used up.</summary>
        private static string Pick(string[] lines, int pick, IReadOnlyCollection<string> alreadySaid)
        {
            int start = pick % lines.Length;
            if (alreadySaid == null || alreadySaid.Count == 0) return lines[start];
            for (int i = 0; i < lines.Length; i++)
            {
                string candidate = lines[(start + i) % lines.Length];
                bool used = false;
                foreach (string s in alreadySaid)
                    if (string.Equals(s?.Trim(), candidate, StringComparison.Ordinal)) { used = true; break; }
                if (!used) return candidate;
            }
            return lines[start];
        }

        private static int Count(string text, string[] words)
        {
            int n = 0;
            foreach (var w in words) if (text.Contains(w)) n++;
            return n;
        }

        // string.GetHashCode is randomized per process on some runtimes; keep replies reproducible.
        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return h & 0x7fffffff;
            }
        }
    }
}
