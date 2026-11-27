using System;
using System.Collections.Generic;
using System.Text;
using ProjectFrontier.Social;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Builds the ~500-token single-call prompt: persona card + standing + 1 rumor line + recent turns.
    /// History is stored as plain dialogue (not the JSON envelope) to save tokens.
    /// </summary>
    public sealed class TurnPromptBuilder
    {
        public const int DefaultMaxOutputTokens = 130;
        public const int DefaultInputBudget = 500;
        public const int DefaultMaxHistoryMessages = 4;
        public const int MaxPlayerMessageChars = 200;

        public int InputTokenBudget = DefaultInputBudget;
        public int MaxHistoryMessages = DefaultMaxHistoryMessages;
        public int MaxOutputTokens = DefaultMaxOutputTokens;

        public const string OutputRules =
            "Stay strictly in character; use your speech style and never sound like an AI assistant. Reply in 1-2 short sentences (max 25 words).\n" +
            "Respond ONLY with compact JSON: {\"dialogue\":\"...\",\"mood\":\"happy|neutral|annoyed|angry|sad|shy|flirty|suspicious|proud|amused\"," +
            "\"deltas\":{\"trust\":0,\"affection\":0,\"respect\":0},\"quick_replies\":[\"...\",\"...\",\"...\"]}\n" +
            "deltas: integers -5..5 reflecting how the player's line made you feel (use +/-1..3 for praise, warmth, help, or insults). " +
            "quick_replies: 3 distinct short lines the player might say next (max 7 words each).";

        public sealed class Context
        {
            public string NpcId;
            public string NpcDisplayName;
            public string PersonaCard;
            public string StandingLine;
            public string RumorLine;
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

            var system = new ChatMessage(ChatRole.System, BuildSystemPrompt(ctx));
            var player = new ChatMessage(ChatRole.User, SanitizePlayerMessage(ctx.PlayerMessage));

            int used = TokenEstimator.Estimate(system) + TokenEstimator.Estimate(player);

            // Newest history first until the message cap or the token budget is hit.
            var kept = new List<ChatMessage>();
            if (ctx.History != null)
            {
                for (int i = ctx.History.Count - 1; i >= 0 && kept.Count < MaxHistoryMessages; i--)
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

        public static string BuildSystemPrompt(Context ctx)
        {
            var sb = new StringBuilder(768);
            sb.Append(string.IsNullOrWhiteSpace(ctx.PersonaCard)
                ? $"You are {ctx.NpcDisplayName}, a villager of Oakhaven.\n"
                : ctx.PersonaCard.TrimEnd() + "\n");
            sb.Append("Day ").Append(Math.Max(1, ctx.Day)).Append(". The player is a newcomer adventurer in Oakhaven.\n");
            if (!string.IsNullOrWhiteSpace(ctx.StandingLine)) sb.Append(ctx.StandingLine.Trim()).Append(" toward the player.\n");
            if (!string.IsNullOrWhiteSpace(ctx.RumorLine)) sb.Append(ctx.RumorLine.Trim()).Append('\n');
            sb.Append(OutputRules);
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
