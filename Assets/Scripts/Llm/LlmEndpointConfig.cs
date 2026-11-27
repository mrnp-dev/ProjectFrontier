using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProjectFrontier.Llm
{
    public enum LlmMode
    {
        /// <summary>Offline provider only. Default for release builds until the proxy ships.</summary>
        Offline,
        /// <summary>Call a model endpoint directly with a dev key. Editor and development builds only.</summary>
        DirectDev,
        /// <summary>Call the game's backend proxy with the player's UGS access token; the proxy holds the API key.</summary>
        Proxy
    }

    /// <summary>
    /// Endpoint settings. Never stored in an asset or in git. Sources, lowest to highest priority: a local JSON file
    /// (UserSettings/FrontierLlm.json in the Editor, persistentDataPath/frontier_llm.json on dev devices), the
    /// git-ignored project-root .env (Editor only), then real environment variables.
    /// See docs/LLM_SETUP.md.
    /// </summary>
    [Serializable]
    public class LlmEndpointConfig
    {
        public string mode = nameof(LlmMode.Offline);
        public string baseUrl = "";
        public string model = "";
        public string apiKey = "";
        public bool jsonMode = true;
        public float timeoutSeconds = 8f;
        public int maxTokens = TurnPromptBuilder.DefaultMaxOutputTokens;
        public float temperature = 0.8f;
        public int offlineLatencyMs = 400;
        /// <summary>Optional "low" / "medium" / "high" for reasoning models (gpt-oss, o-series). Empty = not sent.</summary>
        public string reasoningEffort = "";
        /// <summary>Extra completion tokens allowed when reasoningEffort is set: reasoning tokens count toward max_tokens.</summary>
        public int reasoningTokenAllowance = 120;

        public const string EnvMode = "FRONTIER_LLM_MODE";
        public const string EnvBaseUrl = "FRONTIER_LLM_BASE_URL";
        public const string EnvModel = "FRONTIER_LLM_MODEL";
        public const string EnvApiKey = "FRONTIER_LLM_API_KEY";
        public const string EnvReasoningEffort = "FRONTIER_LLM_REASONING_EFFORT";

        public bool HasReasoningEffort => !string.IsNullOrWhiteSpace(reasoningEffort);

        /// <summary>max_tokens actually sent: the turn's output cap, plus the reasoning allowance for reasoning models.</summary>
        public int EffectiveMaxTokens(int outputTokens) =>
            HasReasoningEffort ? outputTokens + Math.Max(0, reasoningTokenAllowance) : outputTokens;

        public LlmMode Mode => Enum.TryParse(mode, true, out LlmMode m) ? m : LlmMode.Offline;

        /// <summary>baseUrl may be ".../v1" or the full ".../chat/completions" URL.</summary>
        public string ChatCompletionsUrl
        {
            get
            {
                if (string.IsNullOrWhiteSpace(baseUrl)) return null;
                string b = baseUrl.Trim().TrimEnd('/');
                return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
            }
        }

        public bool HasEndpoint => !string.IsNullOrWhiteSpace(baseUrl);

        /// <summary>Loads the JSON file (if any), then the .env file (if any), then lets environment variables override.</summary>
        public static LlmEndpointConfig Load(string filePath, string dotEnvPath = null)
        {
            var cfg = new LlmEndpointConfig();
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(filePath), cfg);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LLM] Ignoring unreadable config {filePath}: {e.Message}");
            }

            Dictionary<string, string> dotEnv = null;
            try
            {
                if (!string.IsNullOrEmpty(dotEnvPath) && File.Exists(dotEnvPath))
                    dotEnv = ParseDotEnv(File.ReadAllText(dotEnvPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LLM] Ignoring unreadable .env: {e.Message}");
            }

            cfg.mode = Pick(EnvMode, dotEnv) ?? cfg.mode;
            cfg.baseUrl = Pick(EnvBaseUrl, dotEnv) ?? cfg.baseUrl;
            cfg.model = Pick(EnvModel, dotEnv) ?? cfg.model;
            cfg.apiKey = Pick(EnvApiKey, dotEnv) ?? cfg.apiKey;
            cfg.reasoningEffort = Pick(EnvReasoningEffort, dotEnv) ?? cfg.reasoningEffort;
            if (!cfg.HasReasoningEffort && IsDefaultReasoningModel(cfg.model))
                cfg.reasoningEffort = "low";
            return cfg;
        }

        public static bool IsDefaultReasoningModel(string modelName) =>
            !string.IsNullOrWhiteSpace(modelName) &&
            modelName.IndexOf("gpt-oss", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Pick(string name, Dictionary<string, string> dotEnv)
        {
            string v = Env(name);
            if (v != null) return v;
            return dotEnv != null && dotEnv.TryGetValue(name, out var d) && !string.IsNullOrWhiteSpace(d) ? d.Trim() : null;
        }

        /// <summary>Minimal KEY=VALUE parser: skips blanks and # comments, accepts "export ", strips matching quotes.</summary>
        public static Dictionary<string, string> ParseDotEnv(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return result;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line.Substring(7).TrimStart();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                    value = value.Substring(1, value.Length - 2);
                else
                {
                    int hash = value.IndexOf(" #", StringComparison.Ordinal);
                    if (hash >= 0) value = value.Substring(0, hash).TrimEnd();
                }
                result[key] = value;
            }
            return result;
        }

        /// <summary>True for endpoints on this machine (llama.cpp, LM Studio, Ollama), which usually need no key.</summary>
        public bool IsLocalEndpoint
        {
            get
            {
                if (!Uri.TryCreate(ChatCompletionsUrl ?? "", UriKind.Absolute, out var uri)) return false;
                return uri.IsLoopback || uri.Host == "10.0.2.2";
            }
        }

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// <summary>Safe for logs: never includes the key.</summary>
        public override string ToString() =>
            $"mode={Mode} url={ChatCompletionsUrl ?? "-"} model={(string.IsNullOrEmpty(model) ? "-" : model)} key={(string.IsNullOrEmpty(apiKey) ? "none" : "set")} json={jsonMode} reasoning={(HasReasoningEffort ? reasoningEffort.Trim() : "-")} timeout={timeoutSeconds}s";
    }
}
