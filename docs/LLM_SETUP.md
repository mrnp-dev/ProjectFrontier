# LLM Setup (Layer 1 chat)

Villager chat turns go through `FrontierLlmManager` (scene: `GameSystems`) → `LlmTurnService` → an `ILLMProvider`.
With no configuration the game uses the **offline provider**: fixed, keyword-matched replies with no network. That is the
default for every build until the backend proxy ships.

## Quick start (Editor, real model)

1. Open `.env` in the project root. It is git-ignored; if it is missing, copy `.env.example` to `.env`.
2. Set the endpoint and model, then paste your key after `FRONTIER_LLM_API_KEY=`:
   ```ini
   FRONTIER_LLM_MODE=DirectDev
   FRONTIER_LLM_BASE_URL=https://api.openai.com/v1
   FRONTIER_LLM_MODEL=gpt-4o-mini
   FRONTIER_LLM_API_KEY=sk-...
   ```
3. Enter Play Mode. The Console prints `[LLM] Provider: <model> (mode=DirectDev ... key=set ...)`. The key itself is never logged.

Any OpenAI-compatible `/v1/chat/completions` endpoint works (OpenAI, Groq, OpenRouter, DeepSeek, Together, or a local
llama.cpp / LM Studio / Ollama server). Local (`localhost` / `127.0.0.1`) endpoints need no key.

## Where settings come from

Lowest to highest priority:

| Source | Where | Notes |
|---|---|---|
| JSON file | Editor: `UserSettings/FrontierLlm.json` · device: `<persistentDataPath>/frontier_llm.json` | All fields (see below). `UserSettings/` is git-ignored. |
| `.env` | Project root | **Editor only.** `FRONTIER_LLM_*` keys. Blank values are ignored. |
| Environment variables | Process env of the Editor | `FRONTIER_LLM_MODE`, `FRONTIER_LLM_BASE_URL`, `FRONTIER_LLM_MODEL`, `FRONTIER_LLM_API_KEY` |

JSON fields and defaults:

```json
{
  "mode": "Offline",
  "baseUrl": "",
  "model": "",
  "apiKey": "",
  "jsonMode": true,
  "timeoutSeconds": 8,
  "maxTokens": 130,
  "temperature": 0.8,
  "offlineLatencyMs": 400
}
```

Set `jsonMode` to `false` for servers that reject `response_format: {"type":"json_object"}`; the prompt still asks for JSON.

## Modes

| Mode | Used for | Auth |
|---|---|---|
| `Offline` | Default, automated tests, no network | none |
| `DirectDev` | Editor and development builds only (stripped from release builds) | `Authorization: Bearer <dev key>` |
| `Proxy` | Release builds, once the backend proxy exists | `Authorization: Bearer <UGS access token>`; the proxy holds the real key |

If `DirectDev` has no URL, or a remote URL without a key, the manager logs a warning and stays offline.

## Turn contract

- **Input (~340 tokens measured, budget 500):** persona card, standing line, one rumor line, the last 4 chat messages, the player's line (capped at 200 characters).
- **Output (`max_tokens: 130`):** `{"dialogue","mood","deltas":{"trust","affection","respect"},"quick_replies":[3]}`.
- **Guardrails:** relationship changes go through `FrontierSocialManager.ApplyDelta(..., DeltaSource.LlmTurn)` and are clamped to ±5 per axis per turn. Fields outside the contract (gold, items, quest flags) are ignored. A plain-text or cut-off reply still shows the villager's line, with zero relationship change.
- **Fallback:** no network, an HTTP error, or no answer within `timeoutSeconds` → the offline provider answers that turn and the game keeps going.

## Token logging

Every turn logs one line:

```
[LLM] bram via gpt-4o-mini: in=265 out=56 924ms json=ok mood=proud d=(T+9 A+1 R-2) | turns=2 avgIn=281 avgOut=56 ...
```

`in`/`out` use the provider's reported `usage`. Offline turns show an estimate, marked `(est)`. Running totals are in
`FrontierLlmManager.Instance.Service.Usage`.

## Code map

| File | Role |
|---|---|
| `Assets/Scripts/Llm/ILLMProvider.cs` | Provider interface |
| `Assets/Scripts/Llm/LlmContracts.cs` | Chat message, request, completion and turn-result types |
| `Assets/Scripts/Llm/OpenAiCompatibleProvider.cs` | `UnityWebRequest` client for `/v1/chat/completions` |
| `Assets/Scripts/Llm/MockLlmProvider.cs` | Offline, deterministic, keyword-sentiment replies |
| `Assets/Scripts/Llm/TurnPromptBuilder.cs` | Prompt assembly and token budget |
| `Assets/Scripts/Llm/TokenEstimator.cs` | Offline token estimate (~4 chars/token) |
| `Assets/Scripts/Llm/TurnResponseParser.cs` | Tolerant JSON parsing and plain-text fallback |
| `Assets/Scripts/Llm/LlmTurnService.cs` | Timeout and fallback |
| `Assets/Scripts/Llm/LlmUsageStats.cs` | Running token averages |
| `Assets/Scripts/Llm/LlmEndpointConfig.cs` | Settings loading (JSON, `.env`, environment variables) |
| `Assets/Scripts/Gameplay/FrontierLlmManager.cs` | Scene manager: history, applies deltas, `SendPlayerMessageAsync` for the #14 chat UI |
| `Assets/Tests/EditMode/Llm/LlmLayerTests.cs` | Edit Mode tests |

## Security

- Never commit a key. `.env`, `.env.*` (except `.env.example`) and `UserSettings/` are git-ignored.
- Never ship a key inside an APK. `DirectDev` is compiled out of release builds; use `Proxy`.
