using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ProjectFrontier.Net;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Runs one structured turn: primary provider with a hard timeout (one in-budget retry for a bad sample or a
    /// network blip), built-in lines on any other failure as the short-drop cushion (#37), then parse + usage logging.
    /// Every network outcome is reported to <see cref="ConnectionReports"/>. Always returns a playable result unless
    /// the caller cancels.
    /// Continuations stay on the caller's context (no ConfigureAwait(false)) because UnityWebRequest is main-thread only.
    /// </summary>
    public sealed class LlmTurnService
    {
        public ILLMProvider Primary { get; }
        /// <summary>Built-in lines (<see cref="MockLlmProvider"/>): the short-drop cushion (#37). Name kept for API stability.</summary>
        public ILLMProvider Offline { get; }
        public float TimeoutSeconds { get; }
        public LlmUsageStats Usage { get; } = new LlmUsageStats();

        private readonly Func<bool> _isNetworkAvailable;
        private readonly Action<string> _log;

        public event Action<LlmTurnResult> TurnCompleted;

        public LlmTurnService(ILLMProvider primary, ILLMProvider offline, float timeoutSeconds = 8f,
            Func<bool> isNetworkAvailable = null, Action<string> log = null)
        {
            Offline = offline ?? throw new ArgumentNullException(nameof(offline));
            Primary = primary ?? offline;
            TimeoutSeconds = Math.Max(0.1f, timeoutSeconds);
            _isNetworkAvailable = isNetworkAvailable ?? (() => true);
            _log = log ?? UnityEngine.Debug.Log;
        }

        public async Task<LlmTurnResult> RunTurnAsync(LlmRequest request, string[] fallbackQuickReplies = null,
            CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var sw = Stopwatch.StartNew();

            ILLMProvider used = Primary;
            string fallbackReason = null;
            LlmCompletion completion = null;

            if (Primary != Offline)
            {
                if (Primary.RequiresNetwork && !_isNetworkAvailable())
                {
                    // Online-only (#37): no connection right now -> answer instantly from built-in lines.
                    fallbackReason = NoConnectionReason;
                }
                else
                {
                    try
                    {
                        try
                        {
                            completion = await CompleteWithTimeoutAsync(Primary, request, TimeoutSeconds, ct);
                        }
                        catch (LlmProviderException e) when ((e.IsRetryable || e.IsTransportFailure) && RemainingSeconds(sw) >= MinRetrySeconds)
                        {
                            // One bad sample (stray tool call, invalid JSON) or a short network blip (#37): try once more
                            // inside the same time budget. A blip waits a moment first so a flapping link can come back.
                            RetriedTurns++;
                            if (e.IsTransportFailure)
                            {
                                TransportRetries++;
                                ConnectionReports.Failure(ConnectionSource.Llm, e.Message);
                                await Task.Delay(TransportRetryDelayMs, ct);
                            }
                            _log?.Invoke($"[LLM] {request.NpcId}: retrying after \"{e.Message}\"");
                            completion = await CompleteWithTimeoutAsync(Primary, request, Math.Max(0.1f, RemainingSeconds(sw)), ct);
                        }
                        ConnectionReports.Success(ConnectionSource.Llm);
                        if (completion == null || string.IsNullOrWhiteSpace(completion.Text)) fallbackReason = "empty response";
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        fallbackReason = $"timeout after {TimeoutSeconds:0.#}s";
                        ConnectionReports.Failure(ConnectionSource.Llm, "timeout");
                    }
                    catch (Exception e)
                    {
                        fallbackReason = ClassifyFailure(e.Message);
                        // Any HTTP status (auth, quota, server error) still means the network works.
                        ConnectionReports.Outcome(ConnectionSource.Llm, !IsTransport(e), e.Message);
                        if (IsTpdCap(fallbackReason))
                        {
                            TpdCapTurns++;
                            if (TpdCapTurns == 1)
                                UnityEngine.Debug.LogWarning("[LLM] Provider daily token cap (TPD) reached: NPC turns use built-in lines until it resets.");
                        }
                    }
                }
            }

            if (Primary == Offline || fallbackReason != null)
            {
                used = Offline;
                completion = await Offline.CompleteAsync(request, ct);
            }

            LlmTurnResult result = TurnResponseParser.Parse(completion?.Text, fallbackQuickReplies);
            result.NpcId = request.NpcId;
            result.ProviderName = used.Name;
            result.UsedFallbackProvider = fallbackReason != null;
            result.FallbackReason = fallbackReason;
            result.LatencyMs = sw.ElapsedMilliseconds;

            bool reported = completion != null && completion.PromptTokens > 0;
            result.UsageEstimated = !reported;
            result.PromptTokens = reported ? completion.PromptTokens : request.EstimatedInputTokens;
            result.CompletionTokens = reported && completion.CompletionTokens > 0
                ? completion.CompletionTokens
                : TokenEstimator.Estimate(completion?.Text);

            if (completion != null && completion.Salvaged) SalvagedTurns++;

            Usage.Record(result);
            _log?.Invoke(FormatLog(result) + (completion != null && completion.Salvaged ? " [salvaged from tool_use_failed]" : string.Empty));
            TurnCompleted?.Invoke(result);
            return result;
        }

        /// <summary>Prefix on fallback reasons caused by the provider's tokens-per-day cap (Groq free tier HTTP 429 TPD).</summary>
        public const string TpdCapTag = "tpd_cap";

        /// <summary>Turns that fell back to built-in lines because the provider's daily token cap was hit.</summary>
        public int TpdCapTurns { get; private set; }

        /// <summary>
        /// Tags a provider error so logs can tell the daily token cap apart from a real failure, e.g.
        /// <c>tpd_cap: HTTP 429 ... tokens per day (TPD) ...</c>. Other messages are returned unchanged.
        /// </summary>
        public static string ClassifyFailure(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            bool tpd = message.IndexOf("tokens per day", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       message.IndexOf("(TPD)", StringComparison.Ordinal) >= 0;
            return tpd ? TpdCapTag + ": " + message : message;
        }

        public static bool IsTpdCap(string reason) =>
            reason != null && reason.StartsWith(TpdCapTag, StringComparison.Ordinal);

        /// <summary>A retry is only attempted when at least this much of the turn budget is left.</summary>
        public const float MinRetrySeconds = 1.5f;

        /// <summary>Pause before retrying after a network blip (#37).</summary>
        public const int TransportRetryDelayMs = 400;

        /// <summary>Fallback reason when the connection monitor says the network is down (#37).</summary>
        public const string NoConnectionReason = "no connection";

        /// <summary>Turns that retried after a network blip (subset of <see cref="RetriedTurns"/>).</summary>
        public int TransportRetries { get; private set; }

        /// <summary>True for failures where no server answered (provider transport error or a timeout).</summary>
        public static bool IsTransport(Exception e) =>
            e is TimeoutException || (e is LlmProviderException p && p.IsTransportFailure);

        /// <summary>Turns that needed a second sample from the primary provider (debug/telemetry).</summary>
        public int RetriedTurns { get; private set; }

        /// <summary>Turns whose reply was recovered from a provider error body (gpt-oss stray tool call).</summary>
        public int SalvagedTurns { get; private set; }

        private float RemainingSeconds(Stopwatch sw) => TimeoutSeconds - (float)sw.Elapsed.TotalSeconds;

        private async Task<LlmCompletion> CompleteWithTimeoutAsync(ILLMProvider provider, LlmRequest request, float seconds, CancellationToken outer)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            Task<LlmCompletion> call = provider.CompleteAsync(request, cts.Token);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1f, seconds)), cts.Token);
            Task first = await Task.WhenAny(call, timeout);
            if (first != call)
            {
                // Cancel from this (caller/main) thread so providers may safely abort Unity web requests.
                cts.Cancel();
                outer.ThrowIfCancellationRequested();
                ObserveLater(call);
                throw new TimeoutException();
            }
            cts.Cancel(); // stop the delay timer
            return await call;
        }

        private static void ObserveLater(Task t) =>
            t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

        public string FormatLog(LlmTurnResult r)
        {
            string est = r.UsageEstimated ? " (est)" : string.Empty;
            string fb = r.UsedFallbackProvider ? $" fallback=\"{r.FallbackReason}\"" : string.Empty;
            return $"[LLM] {r.NpcId} via {r.ProviderName}: in={r.PromptTokens} out={r.CompletionTokens}{est} {r.LatencyMs}ms " +
                   $"json={(r.ParsedJson ? "ok" : "fallback")} mood={r.Mood} d={r.Deltas}{fb} | {Usage}";
        }
    }
}
