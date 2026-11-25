using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Runs one structured turn: primary provider with a hard timeout, offline provider on any failure,
    /// then parse + usage logging. Always returns a playable result unless the caller cancels.
    /// Continuations stay on the caller's context (no ConfigureAwait(false)) because UnityWebRequest is main-thread only.
    /// </summary>
    public sealed class LlmTurnService
    {
        public ILLMProvider Primary { get; }
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
                    fallbackReason = "offline";
                }
                else
                {
                    try
                    {
                        completion = await CompleteWithTimeoutAsync(Primary, request, ct);
                        if (completion == null || string.IsNullOrWhiteSpace(completion.Text)) fallbackReason = "empty response";
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        fallbackReason = $"timeout after {TimeoutSeconds:0.#}s";
                    }
                    catch (Exception e)
                    {
                        fallbackReason = e.Message;
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

            Usage.Record(result);
            _log?.Invoke(FormatLog(result));
            TurnCompleted?.Invoke(result);
            return result;
        }

        private async Task<LlmCompletion> CompleteWithTimeoutAsync(ILLMProvider provider, LlmRequest request, CancellationToken outer)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            Task<LlmCompletion> call = provider.CompleteAsync(request, cts.Token);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cts.Token);
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
