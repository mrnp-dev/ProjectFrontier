using System;
using System.Collections.Generic;
using System.Text;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Pure presentation helpers for the hybrid chatbox UI (#14): mood styling, quick-reply slot
    /// selection, typing animation text, relationship delta badges, and mobile keyboard lift math.
    /// Lives in ProjectFrontier.Llm so EditMode unit tests can exercise it without a scene.
    /// </summary>
    public static class ChatboxHelpers
    {
        public const int MinVisibleChips = 2;
        public const int MaxVisibleChips = 3;
        public const int MaxGreetingChars = 220;
        public const float DefaultFallbackKeyboardFraction = 0.38f;
        public const float TopViewportMargin = 16f;

        public readonly struct MoodVisual
        {
            public readonly string MoodKey;
            public readonly string Label;
            public readonly string BadgeHex;
            public readonly string TextHex;

            public MoodVisual(string moodKey, string label, string badgeHex, string textHex)
            {
                MoodKey = moodKey;
                Label = label;
                BadgeHex = badgeHex;
                TextHex = textHex;
            }
        }

        public static MoodVisual GetMoodVisual(string rawMood)
        {
            string mood = LlmMoods.Normalize(rawMood);
            return mood switch
            {
                "happy" => new MoodVisual("happy", "HAPPY", "#4E7A4A", "#F4ECD8"),
                "proud" => new MoodVisual("proud", "PROUD", "#F0B94D", "#F4ECD8"),
                "amused" => new MoodVisual("amused", "AMUSED", "#3E9B95", "#F4ECD8"),
                "annoyed" => new MoodVisual("annoyed", "ANNOYED", "#E07A2F", "#F4ECD8"),
                "angry" => new MoodVisual("angry", "ANGRY", "#D9534F", "#F4ECD8"),
                "sad" => new MoodVisual("sad", "SAD", "#4B5D82", "#F4ECD8"),
                "shy" => new MoodVisual("shy", "SHY", "#8E3B4A", "#F4ECD8"),
                "flirty" => new MoodVisual("flirty", "FLIRTY", "#C95F42", "#F4ECD8"),
                "suspicious" => new MoodVisual("suspicious", "SUSPICIOUS", "#6E4B35", "#F4ECD8"),
                _ => new MoodVisual(LlmMoods.Neutral, "NEUTRAL", "#4A4F55", "#F4ECD8")
            };
        }

        public static Color ParseHex(string hex, Color fallback)
        {
            return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : fallback;
        }

        /// <summary>
        /// Joins a villager's scripted greeting lines into a single compact 1-2 sentence block
        /// that stays readable on mobile screens.
        /// </summary>
        public static string FormatGreeting(string[] greetingLines)
        {
            if (greetingLines == null || greetingLines.Length == 0) return TurnResponseParser.SilentLine;
            var sb = new StringBuilder(160);
            foreach (var raw in greetingLines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(raw.Trim());
            }
            if (sb.Length == 0) return TurnResponseParser.SilentLine;
            string joined = sb.ToString();
            if (joined.Length <= MaxGreetingChars) return joined;
            return TurnResponseParser.CleanDialogue(joined);
        }

        /// <summary>
        /// Guarantees 2-3 non-empty, deduplicated quick-reply chips per NPC turn.
        /// Pads from fallback chips if the model returned fewer than 2.
        /// </summary>
        public static string[] SelectChips(string[] replies, string[] fallback = null, int maxSlots = MaxVisibleChips)
        {
            int cap = Math.Clamp(maxSlots, MinVisibleChips, MaxVisibleChips);
            string[] cleaned = TurnResponseParser.CleanQuickReplies(replies, fallback);
            var list = new List<string>(cap);
            AddUnique(list, cleaned, cap);
            if (list.Count < MinVisibleChips)
            {
                AddUnique(list, fallback, cap);
                AddUnique(list, TurnResponseParser.DefaultQuickReplies, cap);
            }
            return list.ToArray();
        }

        private static void AddUnique(List<string> dest, string[] source, int cap)
        {
            if (source == null) return;
            foreach (var raw in source)
            {
                if (dest.Count >= cap) break;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string t = raw.Trim();
                if (t.Length > TurnResponseParser.MaxQuickReplyChars)
                    t = t.Substring(0, TurnResponseParser.MaxQuickReplyChars).TrimEnd() + "...";
                bool exists = false;
                foreach (var existing in dest)
                {
                    if (string.Equals(existing, t, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) dest.Add(t);
            }
        }

        public static string FormatTypingIndicator(string speakerName, float elapsedSeconds)
        {
            string name = string.IsNullOrWhiteSpace(speakerName) ? "Villager" : speakerName.Trim();
            int dots = ((int)Math.Floor(Math.Max(0f, elapsedSeconds) / 0.35f) % 3) + 1;
            return name + " is thinking" + new string('.', dots);
        }

        public static string FormatPlayerEcho(string playerText, int maxChars = 64)
        {
            string clean = TurnPromptBuilder.SanitizePlayerMessage(playerText);
            if (clean == "...") return string.Empty;
            int limit = Math.Max(16, maxChars);
            if (clean.Length > limit) clean = clean.Substring(0, limit - 3).TrimEnd() + "...";
            return $"YOU: \"{clean}\"";
        }

        public static string FormatDeltaSummary(AxisDeltas d)
        {
            if (d.IsZero) return string.Empty;
            var parts = new List<string>(3);
            if (d.trust != 0) parts.Add(FormatSigned(d.trust, "TRUST"));
            if (d.affection != 0) parts.Add(FormatSigned(d.affection, "AFFECTION"));
            if (d.respect != 0) parts.Add(FormatSigned(d.respect, "RESPECT"));
            return string.Join(" · ", parts);
        }

        private static string FormatSigned(int value, string label) =>
            value > 0 ? $"+{value} {label}" : $"{value} {label}";

        /// <summary>
        /// Computes the bottom-anchored Y position for the chatbox panel when a mobile soft keyboard
        /// is visible, converting keyboard pixels into Canvas units and clamping so the panel never
        /// clips off the top edge of the screen.
        /// </summary>
        public static float ComputeKeyboardLiftY(
            float restingBottomY,
            float panelHeight,
            float canvasHeight,
            float keyboardPixelHeight,
            float canvasScaleFactor,
            bool keyboardVisible)
        {
            if (!keyboardVisible) return restingBottomY;

            float safeScale = canvasScaleFactor > 0.01f ? canvasScaleFactor : 1f;
            float keyboardCanvasHeight = keyboardPixelHeight > 1f
                ? keyboardPixelHeight / safeScale
                : canvasHeight * DefaultFallbackKeyboardFraction;

            float desiredY = Math.Max(restingBottomY, keyboardCanvasHeight + 10f);
            float maxY = Math.Max(restingBottomY, canvasHeight - panelHeight - TopViewportMargin);
            return Math.Min(desiredY, maxY);
        }
    }
}
