using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectFrontier.Net;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier.Llm
{
    /// <summary>Outcome of one after-conversation extraction call (#42).</summary>
    public sealed class MemoryExtractionResult
    {
        public readonly List<MemoryFact> Facts = new List<MemoryFact>();
        public int PromptTokens;
        public int CompletionTokens;
        public bool UsageEstimated;
        public long LatencyMs;
        /// <summary>Why nothing was remembered (skipped, provider error, bad JSON), or null on success.</summary>
        public string Error;
        public bool CalledProvider;
    }

    /// <summary>
    /// After a conversation closes, one small batched call extracts 0-3 facts worth remembering (#42).
    /// Bounded: transcript capped at <see cref="MaxTranscriptChars"/>, output at <see cref="MaxOutputTokens"/>.
    /// JSON-validated; any failure means "remember nothing" (the game never depends on it).
    /// </summary>
    public static class MemoryExtractor
    {
        public const string FeatureTag = "memory_extract";
        public const int MaxFacts = 3;
        public const int MaxOutputTokens = 160;
        public const int MaxTranscriptChars = 1200;
        /// <summary>Skip the call when the player said less than this in total (greetings, "ok", chip taps).</summary>
        public const int MinPlayerChars = 20;

        public const string SystemPrompt =
            "You extract long-term memories for a game villager from one chat with the player. " +
            "Keep only facts worth remembering for days: what the player said about themselves (likes, plans, family, goals), " +
            "promises or requests between them, and notable events. Skip greetings, small talk and anything the villager said about themselves. " +
            "Never invent anything. Return each fact that qualifies (at most 3), or none.\n" +
            "Each fact: one short sentence starting with \"The player\", max 15 words; \"about\": 1-3 lowercase keywords; " +
            "\"key\": short snake_case topic (e.g. favorite_crop); \"importance\": 1 trivia, 2 personal detail, 3 promise or strong feeling.\n" +
            "JSON only: {\"facts\":[{\"text\":\"...\",\"about\":[\"...\"],\"key\":\"...\",\"importance\":2}]} or {\"facts\":[]}";

        [Serializable] private class WireFact { public string text; public string[] about; public string key; public int importance; }
        [Serializable] private class WireFacts { public WireFact[] facts; }

        /// <summary>Null when the conversation has nothing worth a call (too little player text).</summary>
        public static LlmRequest BuildRequest(string npcId, string npcName, IReadOnlyList<ChatMessage> conversation)
        {
            if (conversation == null || conversation.Count == 0) return null;
            int playerChars = 0;
            foreach (var m in conversation) if (m.Role == ChatRole.User) playerChars += m.Content?.Length ?? 0;
            if (playerChars < MinPlayerChars) return null;

            string name = string.IsNullOrEmpty(npcName) ? npcId : npcName;
            // Newest lines first until the cap, then restore order.
            var lines = new List<string>();
            int used = 0;
            for (int i = conversation.Count - 1; i >= 0; i--)
            {
                var m = conversation[i];
                if (m.Role == ChatRole.System || string.IsNullOrWhiteSpace(m.Content)) continue;
                string line = (m.Role == ChatRole.User ? "Player: " : name + ": ") + m.Content.Trim();
                if (used + line.Length + 1 > MaxTranscriptChars) break;
                lines.Add(line);
                used += line.Length + 1;
            }
            if (lines.Count == 0) return null;
            lines.Reverse();

            var request = new LlmRequest { NpcId = npcId, NpcDisplayName = name, MaxTokens = MaxOutputTokens, Temperature = 0.2f };
            var system = new ChatMessage(ChatRole.System, SystemPrompt);
            var user = new ChatMessage(ChatRole.User, $"Chat between the player and {name}:\n" + string.Join("\n", lines));
            request.Messages.Add(system);
            request.Messages.Add(user);
            request.EstimatedInputTokens = TokenEstimator.Estimate(system) + TokenEstimator.Estimate(user);
            return request;
        }

        /// <summary>Validates the model's JSON into facts held by <paramref name="npcId"/>. Invalid input -> empty list.</summary>
        public static List<MemoryFact> Parse(string raw, string npcId, int day, string mood = null)
        {
            var facts = new List<MemoryFact>();
            string json = ExtractJsonObject(raw);
            if (json == null) return facts;
            WireFacts wire;
            try { wire = JsonUtility.FromJson<WireFacts>(json); }
            catch (ArgumentException) { return facts; }
            if (wire?.facts == null) return facts;

            foreach (WireFact w in wire.facts)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.text)) continue;
                string text = NpcMemoryStore.CleanText(w.text);
                if (text.Length < 8) continue;
                var about = new List<string> { "player" };
                if (w.about != null) foreach (string a in w.about) if (!string.IsNullOrWhiteSpace(a)) about.Add(a);
                facts.Add(new MemoryFact
                {
                    holderId = npcId, fromId = npcId, source = (int)MemorySource.PlayerChat, day = Math.Max(1, day),
                    text = text, about = about.ToArray(), key = NpcMemoryStore.CleanKey(w.key),
                    importance = Math.Clamp(w.importance <= 0 ? 1 : w.importance, 1, 3), mood = mood
                });
                if (facts.Count == MaxFacts) break;
            }
            return facts;
        }

        /// <summary>
        /// Runs the extraction on <paramref name="provider"/> with a hard timeout. Never throws (except caller cancel):
        /// no AI provider, transport errors, timeouts and bad JSON all return an empty result with <see cref="MemoryExtractionResult.Error"/>.
        /// </summary>
        public static async Task<MemoryExtractionResult> RunAsync(ILLMProvider provider, LlmRequest request, int day,
            float timeoutSeconds, CancellationToken ct, string mood = null)
        {
            var result = new MemoryExtractionResult();
            if (request == null) { result.Error = "nothing to extract"; return result; }
            if (provider == null || !provider.RequiresNetwork) { result.Error = "no AI provider"; return result; }

            var sw = Stopwatch.StartNew();
            LlmCompletion completion = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task<LlmCompletion> call = provider.CompleteAsync(request, cts.Token);
                Task first = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5f, timeoutSeconds)), cts.Token));
                if (first != call)
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    _ = call.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    result.Error = $"timeout after {timeoutSeconds:0.#}s";
                    ConnectionReports.Failure(ConnectionSource.Llm, "timeout");
                }
                else
                {
                    cts.Cancel();
                    completion = await call;
                    result.CalledProvider = true;
                    ConnectionReports.Success(ConnectionSource.Llm);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                result.CalledProvider = true;
                result.Error = LlmTurnService.ClassifyFailure(e.Message);
                ConnectionReports.Outcome(ConnectionSource.Llm, !LlmTurnService.IsTransport(e), e.Message);
            }
            result.LatencyMs = sw.ElapsedMilliseconds;

            bool reported = completion != null && completion.PromptTokens > 0;
            result.UsageEstimated = !reported;
            result.PromptTokens = reported ? completion.PromptTokens : request.EstimatedInputTokens;
            result.CompletionTokens = reported && completion.CompletionTokens > 0
                ? completion.CompletionTokens : TokenEstimator.Estimate(completion?.Text);

            if (completion != null)
            {
                result.Facts.AddRange(Parse(completion.Text, request.NpcId, day, mood));
                if (result.Facts.Count == 0 && ExtractJsonObject(completion.Text) == null) result.Error = "non-JSON reply";
            }
            return result;
        }

        private static string ExtractJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            return start >= 0 && end > start ? raw.Substring(start, end - start + 1) : null;
        }

        public static string Describe(MemoryExtractionResult r)
        {
            var sb = new StringBuilder();
            sb.Append(r.Facts.Count).Append(" fact(s)");
            if (r.CalledProvider) sb.Append($" in={r.PromptTokens} out={r.CompletionTokens}{(r.UsageEstimated ? " (est)" : "")} {r.LatencyMs}ms");
            if (r.Error != null) sb.Append(" error=\"").Append(r.Error).Append('"');
            return sb.ToString();
        }
    }
}
