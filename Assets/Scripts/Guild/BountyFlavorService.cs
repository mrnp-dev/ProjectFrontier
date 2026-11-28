using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ProjectFrontier.Llm;
using UnityEngine;

namespace ProjectFrontier.Guild
{
    public readonly struct BountyFlavorResult
    {
        public readonly string Text;
        public readonly bool UsedFallback;
        public readonly string FallbackReason;
        public readonly string ProviderName;

        public BountyFlavorResult(string text, bool usedFallback, string fallbackReason, string providerName)
        {
            Text = text;
            UsedFallback = usedFallback;
            FallbackReason = fallbackReason;
            ProviderName = providerName;
        }
    }

    /// <summary>
    /// Layer 2 background LLM service for Adventurers Guild bounty flavor descriptions (#20, TECH_SPEC §4).
    /// Generates concise in-world prose in Captain Aldric's voice while keeping all gameplay numbers, rewards,
    /// and rank rules strictly in C#. Always degrades cleanly and non-blockingly to <see cref="BountyDef.StaticDescription"/>.
    /// </summary>
    public sealed class BountyFlavorService
    {
        public const int MaxFlavorChars = 160;
        public const int MaxFlavorTokens = 96;
        public const float DefaultTimeoutSeconds = 8f;

        private readonly ILLMProvider _provider;
        private readonly Func<bool> _isOnline;
        private readonly float _timeoutSeconds;

        [Serializable]
        private class FlavorWire
        {
            public string flavor;
            public string description;
            public string notice;
        }

        public BountyFlavorService(ILLMProvider provider, Func<bool> isOnline = null, float timeoutSeconds = DefaultTimeoutSeconds)
        {
            _provider = provider;
            _isOnline = isOnline ?? (() => true);
            _timeoutSeconds = Math.Max(0.5f, timeoutSeconds);
        }

        public static LlmRequest BuildRequest(BountyDef bounty, int day, GuildRank rank, string townHint = null)
        {
            if (bounty == null) throw new ArgumentNullException(nameof(bounty));
            var req = new LlmRequest
            {
                NpcId = "aldric",
                NpcDisplayName = "Captain Aldric",
                MaxTokens = MaxFlavorTokens,
                Temperature = 0.7f
            };
            req.Messages.Add(new ChatMessage(ChatRole.System,
                "You write terse, grounded 1-sentence Adventurers Guild board notices signed by Captain Aldric in Oakhaven. " +
                "Reply ONLY as JSON: {\"flavor\":\"...\"}. Under 140 characters. Never state gold, XP, counts, or rank requirements."));

            // Counts, days and rewards stay in C#: the model gets a number-free task so a notice can never
            // contradict the card. Output containing digits is rejected in ParseFlavor as a second guard.
            string sponsor = GuildCatalog.GetSponsorDisplayName(bounty.SponsorNpcId);
            string extra = string.IsNullOrWhiteSpace(townHint) ? string.Empty : $" | Context: {StripNumbers(townHint)}";
            req.Messages.Add(new ChatMessage(ChatRole.User,
                $"Rank: {GuildCatalog.GetRankShortLabel(rank)} | Title: {StripNumbers(bounty.Title)} | Task: {StripNumbers(bounty.ObjectiveSummary)} | Sponsor: {sponsor}{extra}. Write the board notice."));
            int est = 0;
            for (int i = 0; i < req.Messages.Count; i++) est += TokenEstimator.Estimate(req.Messages[i]);
            req.EstimatedInputTokens = est;
            return req;
        }

        public async Task<BountyFlavorResult> GenerateFlavorAsync(
            BountyDef bounty,
            int day,
            GuildRank rank,
            string townHint = null,
            CancellationToken ct = default)
        {
            if (bounty == null)
                return new BountyFlavorResult(string.Empty, true, "null bounty", "static");

            string fallback = bounty.StaticDescription;
            if (_provider == null || !_provider.RequiresNetwork)
                return new BountyFlavorResult(fallback, true, "offline provider", "static");

            if (!_isOnline())
                return new BountyFlavorResult(fallback, true, "no network", "static");

            LlmRequest req = BuildRequest(bounty, day, rank, townHint);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                LlmCompletion completion = await _provider.CompleteAsync(req, linkedCts.Token);
                string parsed = ParseFlavor(completion?.Text);
                if (!string.IsNullOrWhiteSpace(parsed))
                    return new BountyFlavorResult(parsed, false, null, _provider.Name);

                return new BountyFlavorResult(fallback, true, "empty or invalid flavor", _provider.Name);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                return new BountyFlavorResult(fallback, true, "timeout", _provider.Name);
            }
            catch (Exception e)
            {
                return new BountyFlavorResult(fallback, true, e.Message, _provider.Name);
            }
        }

        private static readonly Regex CountPattern = new Regex(@"\d+\s*[xX×]?\s*", RegexOptions.Compiled);

        /// <summary>Removes counts like "2x" / "3 " so the prompt carries no gameplay numbers.</summary>
        public static string StripNumbers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string s = CountPattern.Replace(text, string.Empty).Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        /// <summary>
        /// Extracts and sanitizes flavor copy from JSON (<c>{"flavor":"..."}</c>, raw or code-fenced).
        /// Rejects anything that is not that JSON shape, empty/short text, and any text containing digits
        /// (numbers are C#-only), so the caller falls back to the static description.
        /// </summary>
        public static string ParseFlavor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = StripCodeFences(raw.Trim());

            int braceStart = s.IndexOf('{');
            int braceEnd = s.LastIndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart)
            {
                string slice = s.Substring(braceStart, braceEnd - braceStart + 1);
                try
                {
                    FlavorWire wire = JsonUtility.FromJson<FlavorWire>(slice);
                    string extracted = !string.IsNullOrWhiteSpace(wire?.flavor) ? wire.flavor
                        : (!string.IsNullOrWhiteSpace(wire?.description) ? wire.description : wire?.notice);
                    if (!string.IsNullOrWhiteSpace(extracted))
                        return Sanitize(extracted);
                }
                catch (Exception)
                {
                    // Fall through to plain-text guard below
                }
                return null;
            }

            return null;   // no JSON object: refusals / meta chatter fall back to static copy
        }

        private static string StripCodeFences(string text)
        {
            if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
            int firstNl = text.IndexOf('\n');
            if (firstNl < 0) return text.Trim('`', ' ');
            string body = text.Substring(firstNl + 1);
            int close = body.LastIndexOf("```", StringComparison.Ordinal);
            return close >= 0 ? body.Substring(0, close).Trim() : body.Trim();
        }

        private static string Sanitize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string cleaned = input.Replace((char)13, ' ').Replace((char)10, ' ').Trim().Trim((char)34, (char)39);
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            if (cleaned.Length < 12) return null;
            for (int i = 0; i < cleaned.Length; i++)
                if (char.IsDigit(cleaned[i])) return null;
            if (cleaned.Length > MaxFlavorChars)
            {
                int maxBody = MaxFlavorChars - 3;
                int cut = cleaned.LastIndexOf(' ', maxBody);
                if (cut < maxBody / 2) cut = maxBody;
                cleaned = cleaned.Substring(0, cut).TrimEnd('.', ',', ';', ' ') + "...";
            }
            return cleaned;
        }
    }
}
