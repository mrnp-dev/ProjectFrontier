using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// POSTs to any OpenAI-compatible <c>/chat/completions</c> endpoint (OpenAI, OpenRouter, Groq, llama.cpp,
    /// Ollama, LM Studio, or the game's own proxy). Must be called from the Unity main thread.
    /// </summary>
    public sealed class OpenAiCompatibleProvider : ILLMProvider
    {
        private readonly LlmEndpointConfig _config;
        private readonly Func<string> _bearerToken;

        /// <param name="bearerToken">Returns the Authorization bearer: the dev API key (Editor/dev builds) or the
        /// player's Unity Authentication access token (proxy mode). May return null for keyless local servers.</param>
        public OpenAiCompatibleProvider(LlmEndpointConfig config, Func<string> bearerToken)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _bearerToken = bearerToken;
        }

        public string Name => string.IsNullOrEmpty(_config.model) ? "remote" : _config.model;
        public bool RequiresNetwork => true;

        [Serializable] private class WireMessage { public string role; public string content; }
        [Serializable] private class WireFormat { public string type = "json_object"; }

        [Serializable]
        private class WireBody
        {
            public string model;
            public WireMessage[] messages;
            public int max_tokens;
            public float temperature;
        }

        [Serializable] private class WireBodyJsonMode : WireBody { public WireFormat response_format = new WireFormat(); }

        [Serializable] private class WireChoice { public WireMessage message; public string finish_reason; }
        [Serializable] private class WireUsage { public int prompt_tokens; public int completion_tokens; }
        [Serializable] private class WireError { public string message; public string code; public string failed_generation; }

        [Serializable]
        private class WireResponse
        {
            public string model;
            public WireChoice[] choices;
            public WireUsage usage;
            public WireError error;
        }

        public string BuildBody(LlmRequest request)
        {
            WireBody body = _config.jsonMode ? new WireBodyJsonMode() : new WireBody();
            body.model = _config.model;
            body.max_tokens = _config.EffectiveMaxTokens(request.MaxTokens);
            body.temperature = request.Temperature;
            body.messages = new WireMessage[request.Messages.Count];
            for (int i = 0; i < request.Messages.Count; i++)
                body.messages[i] = new WireMessage { role = request.Messages[i].RoleName, content = request.Messages[i].Content };
            string json = JsonUtility.ToJson(body);
            if (_config.HasReasoningEffort)
            {
                // JsonUtility can't omit fields, so append the optional one by hand. Wrapping in a wire object escapes it.
                string effort = JsonUtility.ToJson(new WireFormat { type = _config.reasoningEffort.Trim() });
                json = json.Substring(0, json.Length - 1) + ",\"reasoning_effort\":" + effort.Substring(8, effort.Length - 9) + "}";
            }
            return json;
        }

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string url = _config.ChatCompletionsUrl;
            if (string.IsNullOrEmpty(url)) throw new LlmProviderException("no endpoint configured");

            using var uwr = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(BuildBody(request))),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.CeilToInt(Mathf.Max(1f, _config.timeoutSeconds)) + 1 // backstop; the service enforces the real timeout
            };
            uwr.SetRequestHeader("Content-Type", "application/json");
            string token = _bearerToken?.Invoke();
            if (!string.IsNullOrEmpty(token)) uwr.SetRequestHeader("Authorization", "Bearer " + token);

            var done = new TaskCompletionSource<bool>();
            using (ct.Register(() => { if (!uwr.isDone) uwr.Abort(); done.TrySetCanceled(); }))
            {
                uwr.SendWebRequest().completed += _ => done.TrySetResult(true);
                await done.Task;
            }
            ct.ThrowIfCancellationRequested();

            string payload = uwr.downloadHandler?.text;
            if (uwr.result != UnityWebRequest.Result.Success)
            {
                string salvaged = TrySalvageFailedGeneration(payload);
                if (salvaged != null)
                    return new LlmCompletion { Text = salvaged, Model = _config.model, Salvaged = true };

                string detail = TryReadError(payload);
                throw new LlmProviderException($"HTTP {uwr.responseCode} {uwr.error}{(detail != null ? ": " + detail : string.Empty)}",
                    LlmProviderException.IsSamplingFailure(uwr.responseCode, payload));
            }

            WireResponse response;
            try { response = JsonUtility.FromJson<WireResponse>(payload); }
            catch (Exception e) { throw new LlmProviderException("unreadable response: " + e.Message); }

            if (response?.choices == null || response.choices.Length == 0 || response.choices[0].message == null)
                throw new LlmProviderException("no choices in response" + (response?.error?.message != null ? ": " + response.error.message : string.Empty));

            return new LlmCompletion
            {
                Text = response.choices[0].message.content,
                PromptTokens = response.usage?.prompt_tokens ?? 0,
                CompletionTokens = response.usage?.completion_tokens ?? 0,
                Model = string.IsNullOrEmpty(response.model) ? _config.model : response.model
            };
        }

        /// <summary>
        /// gpt-oss on Groq sometimes wraps its final answer in a bogus tool call (name
        /// <c>assistant&lt;|channel|&gt;final</c>), which the server rejects with <c>tool_use_failed</c> even though
        /// <c>failed_generation</c> holds a complete reply. Returns that reply's JSON object, or null if there is
        /// nothing usable (the caller then treats the error normally).
        /// </summary>
        public static string TrySalvageFailedGeneration(string errorPayload)
        {
            if (string.IsNullOrEmpty(errorPayload) || errorPayload.IndexOf("failed_generation", StringComparison.Ordinal) < 0) return null;
            string generation;
            try { generation = JsonUtility.FromJson<WireResponse>(errorPayload)?.error?.failed_generation; }
            catch (Exception) { return null; }
            if (string.IsNullOrEmpty(generation)) return null;

            int at = generation.IndexOf("\"arguments\"", StringComparison.Ordinal);
            string candidate = at >= 0 ? ExtractValueAfterKey(generation, at + 11) : generation;
            if (candidate == null) return null;
            candidate = candidate.Trim();
            int start = candidate.IndexOf('{');
            if (start < 0) return null;
            string obj = MatchBraces(candidate, start);
            return obj != null && obj.IndexOf("\"dialogue\"", StringComparison.Ordinal) >= 0 ? obj : null;
        }

        /// <summary>The raw value after <c>"key":</c> — an object as-is, or a JSON-encoded string unescaped.</summary>
        private static string ExtractValueAfterKey(string s, int afterKey)
        {
            int colon = s.IndexOf(':', afterKey);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;
            if (s[i] == '{') return MatchBraces(s, i);
            if (s[i] != '"') return null;
            // Arguments encoded as a JSON string: let JsonUtility unescape it.
            int end = i + 1;
            while (end < s.Length && !(s[end] == '"' && s[end - 1] != '\\')) end++;
            if (end >= s.Length) return null;
            try { return JsonUtility.FromJson<WireMessage>("{\"content\":" + s.Substring(i, end - i + 1) + "}")?.content; }
            catch (Exception) { return null; }
        }

        /// <summary>Substring from the '{' at <paramref name="start"/> to its matching '}' (string-aware), or null.</summary>
        private static string MatchBraces(string s, int start)
        {
            int depth = 0;
            bool inString = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return s.Substring(start, i - start + 1);
            }
            return null;
        }

        private static string TryReadError(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return null;
            try
            {
                var r = JsonUtility.FromJson<WireResponse>(payload);
                if (!string.IsNullOrEmpty(r?.error?.message)) return r.error.message;
            }
            catch (Exception) { }
            return payload.Length > 160 ? payload.Substring(0, 160) : payload;
        }
    }
}
