using System;
using System.Collections.Generic;
using System.Text;
using ProjectFrontier.Social;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Builds the single-call prompt: persona card (with knowledge and opinions) + town facts + "now" line + standing +
    /// 1 rumor line + anti-repeat line + recent turns. History is stored as plain dialogue (not the JSON envelope) to save tokens.
    /// </summary>
    public sealed class TurnPromptBuilder
    {
        public const int DefaultMaxOutputTokens = 130;
        /// <summary>
        /// Estimated (chars/4) input budget. Raised from 500 for #41: the grounded persona card + town facts cost ~110
        /// estimated tokens, and without them NPCs had nothing specific to say and fell back on their signature lines.
        /// </summary>
        public const int DefaultInputBudget = 600;
        public const int DefaultMaxHistoryMessages = 4;
        public const int MaxPlayerMessageChars = 200;
        /// <summary>How many of the NPC's older lines (outside the history window) are listed as "don't repeat".</summary>
        public const int DefaultAvoidLines = 3;
        /// <summary>Each avoided line is cut to its first few words: repetition shows up in openings and catchphrases.</summary>
        public const int AvoidLineWords = 6;

        public int InputTokenBudget = DefaultInputBudget;
        public int MaxHistoryMessages = DefaultMaxHistoryMessages;
        public int MaxOutputTokens = DefaultMaxOutputTokens;
        public int AvoidLines = DefaultAvoidLines;

        public const string DefaultTownLine =
            "Oakhaven: frontier village with a Forge, Tavern, Guild Hall, Herb Garden, Square and the player's farm. " +
            "Five lantern shards, scattered on village paths, relight the Square before dark. Dungeon past the ridge; a raid is feared.";

        public const string AvoidHeader = "Earlier you said (don't repeat): ";

        public const string OutputRules =
            "Reply to what the player just said with a concrete answer from the facts above. If you don't know, say so in character; " +
            "never invent people or places. Never reuse wording from your earlier lines. Stay in character, never like an AI. " +
            "1-2 short sentences, max 25 words.\n" +
            "JSON only: {\"dialogue\":\"...\",\"mood\":\"happy|neutral|annoyed|angry|sad|shy|flirty|suspicious|proud|amused\"," +
            "\"deltas\":{\"trust\":0,\"affection\":0,\"respect\":0},\"quick_replies\":[\"...\",\"...\",\"...\"]}\n" +
            "deltas: -5..5, how the player's line made you feel (0 if neutral). " +
            "quick_replies: 3 things the player could say next, each a different topic, max 7 words.";

        public sealed class Context
        {
            public string NpcId;
            public string NpcDisplayName;
            public string PersonaCard;
            /// <summary>Shared world facts. Null uses <see cref="DefaultTownLine"/>; empty omits the line.</summary>
            public string TownLine;
            /// <summary>Live situation, e.g. "Now: day 2, evening, at Mira's Tavern. Lanterns lit 3/5." Null falls back to "Day N.".</summary>
            public string NowLine;
            public string StandingLine;
            public string RumorLine;
            /// <summary>Top recalled long-term memories (#42), already rendered and capped at ~90 tokens. Optional.</summary>
            public string MemoryBlock;
            public int Day = 1;
            public IReadOnlyList<ChatMessage> History;
            public string PlayerMessage;
        }

        public LlmRequest Build(Context ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            var request = new LlmRequest
            {
                NpcId = ctx.NpcId,
                NpcDisplayName = ctx.NpcDisplayName,
                MaxTokens = MaxOutputTokens
            };

            int windowStart = WindowStart(ctx.History, MaxHistoryMessages);
            var avoid = OlderNpcLines(ctx.History, windowStart, AvoidLines);
            var system = new ChatMessage(ChatRole.System, BuildSystemPrompt(ctx, avoid));
            var player = new ChatMessage(ChatRole.User, SanitizePlayerMessage(ctx.PlayerMessage));

            int used = TokenEstimator.Estimate(system) + TokenEstimator.Estimate(player);

            // Newest history first until the message cap or the token budget is hit.
            var kept = new List<ChatMessage>();
            if (ctx.History != null)
            {
                for (int i = ctx.History.Count - 1; i >= windowStart; i--)
                {
                    int cost = TokenEstimator.Estimate(ctx.History[i]);
                    if (used + cost > InputTokenBudget) break;
                    kept.Add(ctx.History[i]);
                    used += cost;
                }
            }
            kept.Reverse();
            // Never start the window on an assistant line with no player line before it.
            while (kept.Count > 0 && kept[0].Role == ChatRole.Assistant) { used -= TokenEstimator.Estimate(kept[0]); kept.RemoveAt(0); }

            request.Messages.Add(system);
            request.Messages.AddRange(kept);
            request.Messages.Add(player);
            request.EstimatedInputTokens = used;
            return request;
        }

        /// <summary>Index of the first history message inside the (message-count) window.</summary>
        public static int WindowStart(IReadOnlyList<ChatMessage> history, int maxMessages) =>
            history == null ? 0 : Math.Max(0, history.Count - Math.Max(0, maxMessages));

        /// <summary>
        /// The NPC's most recent lines that fell out of the history window (oldest first), cut to their opening words.
        /// Lines still inside the window are visible to the model already and covered by the "never reuse" rule.
        /// </summary>
        public static List<string> OlderNpcLines(IReadOnlyList<ChatMessage> history, int windowStart, int max)
        {
            var lines = new List<string>(Math.Max(0, max));
            if (history == null || max <= 0) return lines;
            for (int i = Math.Min(windowStart, history.Count) - 1; i >= 0 && lines.Count < max; i--)
            {
                if (history[i].Role != ChatRole.Assistant || string.IsNullOrWhiteSpace(history[i].Content)) continue;
                string head = FirstWords(history[i].Content, AvoidLineWords);
                if (!lines.Contains(head)) lines.Add(head);
            }
            lines.Reverse();
            return lines;
        }

        /// <summary>First <paramref name="count"/> words, with "..." when the text was longer.</summary>
        public static string FirstWords(string text, int count)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string[] words = text.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= count) return string.Join(" ", words);
            return string.Join(" ", words, 0, count) + "...";
        }

        public static string BuildSystemPrompt(Context ctx) => BuildSystemPrompt(ctx, null);

        public static string BuildSystemPrompt(Context ctx, IReadOnlyList<string> avoidLines)
        {
            var sb = new StringBuilder(1024);
            sb.Append(string.IsNullOrWhiteSpace(ctx.PersonaCard)
                ? $"You are {ctx.NpcDisplayName}, a villager of Oakhaven.\n"
                : ctx.PersonaCard.TrimEnd() + "\n");
            string town = ctx.TownLine ?? DefaultTownLine;
            if (!string.IsNullOrWhiteSpace(town)) sb.Append(town.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(ctx.NowLine)) sb.Append(ctx.NowLine.Trim()).Append('\n');
            else sb.Append("Day ").Append(Math.Max(1, ctx.Day)).Append(".\n");
            if (!string.IsNullOrWhiteSpace(ctx.StandingLine)) sb.Append(ctx.StandingLine.Trim()).Append(" toward the player.\n");
            if (!string.IsNullOrWhiteSpace(ctx.RumorLine)) sb.Append(ctx.RumorLine.Trim()).Append('\n');
            if (avoidLines != null && avoidLines.Count > 0)
            {
                sb.Append(AvoidHeader);
                for (int i = 0; i < avoidLines.Count; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append('"').Append(avoidLines[i].Replace('"', '\'')).Append('"');
                }
                sb.Append('\n');
            }
            if (!string.IsNullOrWhiteSpace(ctx.MemoryBlock)) sb.Append(ctx.MemoryBlock.TrimEnd()).Append('\n');
            sb.Append(OutputRules);
            return sb.ToString();
        }

        /// <summary>"Now: day 2, evening, at Mira's Tavern. Lanterns lit 3/5." Built by the scene from live game state.</summary>
        public static string BuildNowLine(int day, string phase, string place, int lanternsLit, int lanternGoal)
        {
            var sb = new StringBuilder(80);
            sb.Append("Now: day ").Append(Math.Max(1, day));
            if (!string.IsNullOrWhiteSpace(phase)) sb.Append(", ").Append(phase.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(place)) sb.Append(", at ").Append(place.Trim());
            sb.Append('.');
            if (lanternGoal > 0) sb.Append(" Lanterns lit ").Append(Math.Max(0, Math.Min(lanternsLit, lanternGoal))).Append('/').Append(lanternGoal).Append('.');
            return sb.ToString();
        }

        /// <summary>Trims, collapses whitespace and caps the player's free text.</summary>
        public static string SanitizePlayerMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "...";
            var sb = new StringBuilder(Math.Min(text.Length, MaxPlayerMessageChars));
            bool space = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
                if (sb.Length >= MaxPlayerMessageChars) break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Picks the strongest live rumor this villager <b>heard</b> from someone else and renders the one-line prompt hint
        /// (GDD §6), e.g. "RUMOR: Bram says the player mocked his smithing (you mostly believe Bram)." Null when there is none.
        /// </summary>
        public static string BuildRumorLine(IReadOnlyList<Rumor> rumors, string holderId, int day, Func<string, string> nameOf) =>
            GossipEngine.BuildPromptLine(rumors, holderId, day, nameOf);
    }
}
