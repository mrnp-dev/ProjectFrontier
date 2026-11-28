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
        /// <summary>The call gave up waiting. The provider may still generate (and bill) the reply, so usage is estimated (#45).</summary>
        public bool TimedOut;
        /// <summary>No server answered (transport error), or the network was down so the call was skipped. Safe to retry later (#45).</summary>
        public bool NoConnection;
        /// <summary>True when the call may have used provider tokens and belongs in <c>memory_extract</c> usage (#45).</summary>
        public bool CountsTowardUsage => TimedOut || (CalledProvider && !NoConnection);
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
        /// <summary>Villager lines are context only: cut to this many characters when the chat is over the cap (#45).</summary>
        public const int NpcLineChars = 90;
        /// <summary>Player lines are cut to this length only if all of them still don't fit (#45).</summary>
        public const int PlayerLineChars = 120;

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
            List<string> lines = BuildTranscript(conversation, name, MaxTranscriptChars);
            if (lines.Count == 0) return null;

            var request = new LlmRequest { NpcId = npcId, NpcDisplayName = name, MaxTokens = MaxOutputTokens, Temperature = 0.2f };
            var system = new ChatMessage(ChatRole.System, SystemPrompt);
            var user = new ChatMessage(ChatRole.User, $"Chat between the player and {name}:\n" + string.Join("\n", lines));
            request.Messages.Add(system);
            request.Messages.Add(user);
            request.EstimatedInputTokens = TokenEstimator.Estimate(system) + TokenEstimator.Estimate(user);
            return request;
        }

        /// <summary>
        /// Picks the transcript lines for extraction within <paramref name="maxChars"/> (#45). The whole chat when it fits.
        /// Otherwise the player's lines come first, since the facts are about the player: villager lines are cut to
        /// <see cref="NpcLineChars"/> and get at most a third of the budget (newest first, plus any space the player
        /// lines leave). If the player's lines still don't fit, each is cut to <see cref="PlayerLineChars"/>, then the
        /// opening line and the newest lines are kept. Output is in chat order; each line counts its newline.
        /// </summary>
        public static List<string> BuildTranscript(IReadOnlyList<ChatMessage> conversation, string npcName, int maxChars)
        {
            var result = new List<string>();
            if (conversation == null || maxChars <= 0) return result;

            var full = new List<string>(conversation.Count);
            var isPlayer = new List<bool>(conversation.Count);
            int total = 0;
            foreach (var m in conversation)
            {
                if (m.Role == ChatRole.System || string.IsNullOrWhiteSpace(m.Content)) continue;
                bool player = m.Role == ChatRole.User;
                string line = (player ? "Player: " : npcName + ": ") + m.Content.Trim();
                full.Add(line);
                isPlayer.Add(player);
                total += line.Length + 1;
            }
            if (total <= maxChars) return full;

            int n = full.Count;
            var lines = new string[n];
            int playerTotal = 0, npcTotal = 0;
            for (int i = 0; i < n; i++)
            {
                lines[i] = isPlayer[i] ? full[i] : Cut(full[i], NpcLineChars);
                if (isPlayer[i]) playerTotal += lines[i].Length + 1; else npcTotal += lines[i].Length + 1;
            }

            var keep = new bool[n];
            int npcReserve = Math.Min(npcTotal, maxChars / 3);
            int playerBudget = maxChars - npcReserve;
            int used = 0;
            if (playerTotal > playerBudget)
            {
                playerTotal = 0;
                for (int i = 0; i < n; i++)
                    if (isPlayer[i]) { lines[i] = Cut(lines[i], PlayerLineChars); playerTotal += lines[i].Length + 1; }
            }
            if (playerTotal <= playerBudget)
            {
                for (int i = 0; i < n; i++) if (isPlayer[i]) { keep[i] = true; used += lines[i].Length + 1; }
            }
            else
            {
                int first = isPlayer.IndexOf(true);
                if (first >= 0 && lines[first].Length + 1 <= playerBudget) { keep[first] = true; used += lines[first].Length + 1; }
                for (int i = n - 1; i > first; i--)
                {
                    if (!isPlayer[i]) continue;
                    if (used + lines[i].Length + 1 > playerBudget) break;
                    keep[i] = true;
                    used += lines[i].Length + 1;
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                if (isPlayer[i] || used + lines[i].Length + 1 > maxChars) continue;
                keep[i] = true;
                used += lines[i].Length + 1;
            }
            for (int i = 0; i < n; i++) if (keep[i]) result.Add(lines[i]);
            return result;
        }

        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 3)).TrimEnd() + "...";

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
        /// no AI provider, no connection, transport errors, timeouts and bad JSON all return an empty result with
        /// <see cref="MemoryExtractionResult.Error"/>. With <paramref name="networkAllowed"/> false the provider isn't called
        /// and the result is flagged <see cref="MemoryExtractionResult.NoConnection"/> so the caller can queue it (#45).
        /// </summary>
        public static async Task<MemoryExtractionResult> RunAsync(ILLMProvider provider, LlmRequest request, int day,
            float timeoutSeconds, CancellationToken ct, string mood = null, bool networkAllowed = true)
        {
            var result = new MemoryExtractionResult();
            if (request == null) { result.Error = "nothing to extract"; return result; }
            if (provider == null || !provider.RequiresNetwork) { result.Error = "no AI provider"; return result; }
            if (!networkAllowed) { result.Error = "no connection"; result.NoConnection = true; return result; }

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
                    result.TimedOut = true;
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
                result.NoConnection = LlmTurnService.IsTransport(e);
                ConnectionReports.Outcome(ConnectionSource.Llm, !result.NoConnection, e.Message);
            }
            result.LatencyMs = sw.ElapsedMilliseconds;

            bool reported = completion != null && completion.PromptTokens > 0;
            result.UsageEstimated = !reported;
            result.PromptTokens = reported ? completion.PromptTokens : request.EstimatedInputTokens;
            result.CompletionTokens = reported && completion.CompletionTokens > 0
                ? completion.CompletionTokens : TokenEstimator.Estimate(completion?.Text);
            // A timed-out request may still be generated and billed by the provider: count the worst case (#45).
            if (result.TimedOut) result.CompletionTokens = request.MaxTokens > 0 ? request.MaxTokens : MaxOutputTokens;
            if (!result.CountsTowardUsage) { result.PromptTokens = 0; result.CompletionTokens = 0; }

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
            if (r.CountsTowardUsage) sb.Append($" in={r.PromptTokens} out={r.CompletionTokens}{(r.UsageEstimated ? " (est)" : "")} {r.LatencyMs}ms");
            if (r.Error != null) sb.Append(" error=\"").Append(r.Error).Append('"');
            return sb.ToString();
        }
    }
}
