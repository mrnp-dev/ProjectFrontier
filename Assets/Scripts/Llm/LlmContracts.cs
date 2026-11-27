using System;
using System.Collections.Generic;
using ProjectFrontier.Social;

namespace ProjectFrontier.Llm
{
    public enum ChatRole { System, User, Assistant }

    public readonly struct ChatMessage
    {
        public readonly ChatRole Role;
        public readonly string Content;

        public ChatMessage(ChatRole role, string content)
        {
            Role = role;
            Content = content ?? string.Empty;
        }

        public string RoleName => Role switch
        {
            ChatRole.System => "system",
            ChatRole.Assistant => "assistant",
            _ => "user"
        };
    }

    /// <summary>One single-call structured turn (TECH_SPEC section 4).</summary>
    public sealed class LlmRequest
    {
        public string NpcId;
        public string NpcDisplayName;
        public readonly List<ChatMessage> Messages = new List<ChatMessage>();
        public int MaxTokens = TurnPromptBuilder.DefaultMaxOutputTokens;
        public float Temperature = 0.8f;
        public int EstimatedInputTokens;

        /// <summary>The player's line for this turn (last user message), used by the offline provider.</summary>
        public string PlayerMessage
        {
            get
            {
                for (int i = Messages.Count - 1; i >= 0; i--)
                    if (Messages[i].Role == ChatRole.User) return Messages[i].Content;
                return string.Empty;
            }
        }
    }

    /// <summary>Raw provider output plus token usage. Usage is 0 when the provider didn't report it.</summary>
    public sealed class LlmCompletion
    {
        public string Text;
        public int PromptTokens;
        public int CompletionTokens;
        public string Model;
        /// <summary>True when the reply was recovered from a provider error body (usage is then estimated).</summary>
        public bool Salvaged;
    }

    public static class LlmMoods
    {
        public const string Neutral = "neutral";

        /// <summary>Moods the UI (#14) has an indicator for. Anything else maps to neutral.</summary>
        public static readonly string[] All =
            { "happy", "neutral", "annoyed", "angry", "sad", "shy", "flirty", "suspicious", "proud", "amused" };

        public static string Normalize(string mood)
        {
            if (string.IsNullOrWhiteSpace(mood)) return Neutral;
            string m = mood.Trim().ToLowerInvariant();
            foreach (var known in All)
                if (known == m) return known;
            return Neutral;
        }
    }

    /// <summary>A parsed, sanitized NPC turn. Deltas are as requested by the model; clamping happens in SocialState.</summary>
    public sealed class LlmTurnResult
    {
        public string NpcId;
        public string Dialogue;
        public string Mood = LlmMoods.Neutral;
        public AxisDeltas Deltas;
        public string[] QuickReplies = Array.Empty<string>();

        /// <summary>True when the reply was valid JSON; false for the salvage / plain-text fallback.</summary>
        public bool ParsedJson;
        public string ProviderName;
        public bool UsedFallbackProvider;
        public string FallbackReason;

        public int PromptTokens;
        public int CompletionTokens;
        public bool UsageEstimated;
        public long LatencyMs;

        /// <summary>Filled in by the game after SocialState clamping.</summary>
        public AxisDeltas AppliedDeltas;
    }

    public class LlmProviderException : Exception
    {
        /// <summary>
        /// True when the failure came from one bad sample (e.g. the model emitted a stray tool call or invalid JSON)
        /// rather than from config, auth, quota or the network, so trying the same request again is likely to work.
        /// </summary>
        public bool IsRetryable { get; }

        public LlmProviderException(string message, bool isRetryable = false) : base(message) { IsRetryable = isRetryable; }

        private static readonly string[] SamplingFailureMarkers =
        {
            "called a tool",        // Groq/OpenRouter: "Tool choice is none, but model called a tool"
            "tool_use_failed",
            "json_validate_failed", // json_object mode: model produced invalid JSON
            "failed to generate json",
            "output_parse_failed"
        };

        /// <summary>HTTP 400/422 bodies that describe a bad sample rather than a bad request.</summary>
        public static bool IsSamplingFailure(long httpStatus, string detail)
        {
            if ((httpStatus != 400 && httpStatus != 422) || string.IsNullOrEmpty(detail)) return false;
            foreach (string marker in SamplingFailureMarkers)
                if (detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
