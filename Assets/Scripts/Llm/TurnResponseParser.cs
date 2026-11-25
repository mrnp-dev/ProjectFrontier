using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Parses <c>{dialogue, mood, deltas, quick_replies}</c>. Never throws: malformed or truncated JSON is salvaged
    /// (dialogue regex) and anything else falls back to plain text with zero deltas.
    /// Only the three relationship axes are read; any other field (gold, items, flags...) is ignored.
    /// </summary>
    public static class TurnResponseParser
    {
        public const int MaxDialogueChars = 280;
        public const int MaxQuickReplies = 3;
        public const int MaxQuickReplyChars = 60;
        public const string SilentLine = "...";

        public static readonly string[] DefaultQuickReplies = { "Tell me about yourself.", "How's business?", "See you around." };

        [Serializable] private class WireDeltas { public int trust; public int affection; public int respect; }

        [Serializable]
        private class WireTurn
        {
            public string dialogue;
            public string mood;
            public WireDeltas deltas;
            public string[] quick_replies;
        }

        private static readonly Regex DialogueField =
            new Regex("\"dialogue\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        public static LlmTurnResult Parse(string raw, string[] fallbackQuickReplies = null)
        {
            var result = new LlmTurnResult();
            string text = StripCodeFence(raw ?? string.Empty).Trim();
            int open = text.IndexOf('{');
            int close = text.LastIndexOf('}');

            WireTurn wire = null;
            if (open >= 0 && close > open)
            {
                try { wire = JsonUtility.FromJson<WireTurn>(text.Substring(open, close - open + 1)); }
                catch (Exception) { wire = null; }
            }

            if (wire != null && !string.IsNullOrWhiteSpace(wire.dialogue))
            {
                result.ParsedJson = true;
                result.Dialogue = CleanDialogue(wire.dialogue);
                result.Mood = LlmMoods.Normalize(wire.mood);
                if (wire.deltas != null) result.Deltas = new AxisDeltas(wire.deltas.trust, wire.deltas.affection, wire.deltas.respect);
                result.QuickReplies = CleanQuickReplies(wire.quick_replies, fallbackQuickReplies);
                return result;
            }

            // Salvage: JSON-ish but invalid or cut off by max_tokens. Keep the line, drop the rest.
            if (open >= 0)
            {
                Match m = DialogueField.Match(text);
                string salvaged = m.Success ? Unescape(m.Groups[1].Value) : null;
                result.Dialogue = string.IsNullOrWhiteSpace(salvaged) ? SilentLine : CleanDialogue(salvaged);
            }
            else
            {
                result.Dialogue = string.IsNullOrWhiteSpace(text) ? SilentLine : CleanDialogue(text);
            }
            result.QuickReplies = CleanQuickReplies(null, fallbackQuickReplies);
            return result;
        }

        private static string StripCodeFence(string s)
        {
            s = s.Trim();
            if (!s.StartsWith("```")) return s;
            int firstNewline = s.IndexOf('\n');
            s = firstNewline >= 0 ? s.Substring(firstNewline + 1) : s.Substring(3);
            int end = s.LastIndexOf("```", StringComparison.Ordinal);
            return end >= 0 ? s.Substring(0, end) : s;
        }

        private static string Unescape(string s)
        {
            try { return Regex.Unescape(s); }
            catch (Exception) { return s; }
        }

        /// <summary>Collapses whitespace, strips wrapping quotes and caps length at a sentence boundary when possible.</summary>
        public static string CleanDialogue(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return SilentLine;
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s.Trim())
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            string t = sb.ToString();
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"') t = t.Substring(1, t.Length - 2).Trim();
            if (t.Length <= MaxDialogueChars) return t.Length == 0 ? SilentLine : t;

            string cut = t.Substring(0, MaxDialogueChars);
            int sentenceEnd = Math.Max(cut.LastIndexOf(". ", StringComparison.Ordinal),
                              Math.Max(cut.LastIndexOf("! ", StringComparison.Ordinal), cut.LastIndexOf("? ", StringComparison.Ordinal)));
            return sentenceEnd > MaxDialogueChars / 2 ? cut.Substring(0, sentenceEnd + 1) : cut.TrimEnd() + "...";
        }

        public static string[] CleanQuickReplies(string[] replies, string[] fallback)
        {
            var list = new List<string>(MaxQuickReplies);
            if (replies != null)
            {
                foreach (var r in replies)
                {
                    if (string.IsNullOrWhiteSpace(r)) continue;
                    string t = r.Trim().Trim('"').Trim();
                    if (t.Length > MaxQuickReplyChars) t = t.Substring(0, MaxQuickReplyChars).TrimEnd() + "...";
                    bool dup = false;
                    foreach (var e in list) if (string.Equals(e, t, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup && t.Length > 0) list.Add(t);
                    if (list.Count == MaxQuickReplies) break;
                }
            }
            if (list.Count == 0) return (string[])(fallback ?? DefaultQuickReplies).Clone();
            return list.ToArray();
        }
    }
}
