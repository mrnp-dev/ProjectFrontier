using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Offline, deterministic provider: keyword sentiment drives mood and small deltas, and the reply
    /// is returned as the same compact JSON a real model sends, so the parser path is always exercised.
    /// Used for offline play, automated tests and as the fallback when the network provider fails.
    /// </summary>
    public sealed class MockLlmProvider : ILLMProvider
    {
        public string Name => "offline";
        public bool RequiresNetwork => false;

        /// <summary>Optional fake latency so the UI's waiting state is visible in offline play.</summary>
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

        [Serializable] private class WireDeltas { public int trust; public int affection; public int respect; }
        [Serializable] private class WireTurn { public string dialogue; public string mood; public WireDeltas deltas; public string[] quick_replies; }

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (SimulatedLatencyMs > 0) await Task.Delay(SimulatedLatencyMs, ct);

            string text = Respond(request?.PlayerMessage ?? string.Empty);
            return new LlmCompletion { Text = text, Model = Name };
        }

        /// <summary>Pure function of the player's message: same input, same JSON.</summary>
        public static string Respond(string playerMessage)
        {
            string m = (playerMessage ?? string.Empty).ToLowerInvariant();
            int pos = Count(m, Positive), neg = Count(m, Negative), cur = Count(m, Curious);
            int pick = StableHash(m);

            var turn = new WireTurn { deltas = new WireDeltas() };
            if (neg > pos)
            {
                turn.mood = neg >= 2 ? "angry" : "annoyed";
                turn.dialogue = NegativeLines[pick % NegativeLines.Length];
                turn.deltas.trust = -Math.Min(5, 2 + neg);
                turn.deltas.affection = -Math.Min(5, 1 + neg);
                turn.deltas.respect = -1;
                turn.quick_replies = new[] { "Sorry, I crossed a line.", "Just testing your temper.", "Fine, I'll go." };
            }
            else if (pos > 0)
            {
                turn.mood = "happy";
                turn.dialogue = PositiveLines[pick % PositiveLines.Length];
                turn.deltas.trust = Math.Min(5, 1 + pos);
                turn.deltas.affection = Math.Min(5, pos);
                turn.deltas.respect = cur > 0 ? 1 : 0;
                turn.quick_replies = new[] { "Glad to meet you.", "What do you do here?", "See you tomorrow!" };
            }
            else if (cur > 0)
            {
                turn.mood = "proud";
                turn.dialogue = CuriousLines[pick % CuriousLines.Length];
                turn.deltas.trust = 1;
                turn.deltas.respect = 1;
                turn.quick_replies = new[] { "Tell me more.", "That sounds hard.", "Can I help?" };
            }
            else
            {
                turn.mood = "neutral";
                turn.dialogue = NeutralLines[pick % NeutralLines.Length];
                turn.quick_replies = new[] { "Tell me about yourself.", "Any news in town?", "Goodbye for now." };
            }
            return JsonUtility.ToJson(turn);
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
