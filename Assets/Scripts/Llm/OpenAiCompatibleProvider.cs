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
        [Serializable] private class WireError { public string message; }

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
                string detail = TryReadError(payload);
                throw new LlmProviderException($"HTTP {uwr.responseCode} {uwr.error}{(detail != null ? ": " + detail : string.Empty)}");
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
