# LLM Setup (Layer 1 chat)

Villager chat turns go through `FrontierLlmManager` (scene: `GameSystems`) → `LlmTurnService` → an `ILLMProvider`.
The game is **online-only** (#37): it needs the internet to start and pauses behind "Reconnecting..." during long drops
(see [`TECH_SPEC.md`](TECH_SPEC.md#online-only-connection-model-37)). With no AI endpoint configured, villagers answer
with **built-in lines** (`MockLlmProvider`: fixed, keyword-matched replies). That is a dev/test setting, not an offline
play mode; built-in lines are otherwise only the cushion for a single failed turn during a short drop.

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

### Reasoning models (Groq `openai/gpt-oss-*`, OpenAI o-series)

These models "think" before replying, and the thinking tokens count toward `max_tokens`. At their default effort the
thinking can use the whole 130-token cap, leaving an empty reply (Groq returns `400 Failed to validate JSON`).
`gpt-oss` models automatically default to `reasoning_effort: "low"` when `FRONTIER_LLM_REASONING_EFFORT` is unset; for
other reasoning models (or to override), set:

```ini
FRONTIER_LLM_REASONING_EFFORT=low
```

This sends `reasoning_effort: "low"` and raises `max_tokens` by `reasoningTokenAllowance` (default 120, so 250 total).
Leave it blank for normal non-reasoning models: they reject the field.

Tested on Groq: `openai/gpt-oss-20b` + `low` → 7/7 valid JSON turns, ~350-410 input tokens, 70-136 output tokens
(thinking included), 0.5-0.8 s in the Editor. Free Groq tiers have per-model daily token limits; on HTTP 429 the game
falls back to built-in lines (logged as `tpd_cap`). A 429 still counts as "connected" for the connection monitor.

Any OpenAI-compatible `/v1/chat/completions` endpoint works (OpenAI, Groq, OpenRouter, DeepSeek, Together, or a local
llama.cpp / LM Studio / Ollama server). Local (`localhost` / `127.0.0.1`) endpoints need no key.

## Where settings come from

Lowest to highest priority:

| Source | Where | Notes |
|---|---|---|
| JSON file | Editor: `UserSettings/FrontierLlm.json` · device: `<persistentDataPath>/frontier_llm.json` | All fields (see below). `UserSettings/` is git-ignored. |
| `.env` | Project root | **Editor only.** `FRONTIER_LLM_*` keys. Blank values are ignored. |
| Environment variables | Process env of the Editor | `FRONTIER_LLM_MODE`, `FRONTIER_LLM_BASE_URL`, `FRONTIER_LLM_MODEL`, `FRONTIER_LLM_API_KEY`, `FRONTIER_LLM_REASONING_EFFORT` |

JSON fields and defaults:

```json
{
  "mode": "BuiltIn",
  "baseUrl": "",
  "model": "",
  "apiKey": "",
  "jsonMode": true,
  "timeoutSeconds": 8,
  "maxTokens": 130,
  "temperature": 0.8,
  "offlineLatencyMs": 400,
  "reasoningEffort": "",
  "reasoningTokenAllowance": 120
}
```

Set `jsonMode` to `false` for servers that reject `response_format: {"type":"json_object"}`; the prompt still asks for JSON.

## Modes

| Mode | Used for | Auth |
|---|---|---|
| `BuiltIn` | Default when nothing is configured: automated tests, dev machines without a key. Old configs saying `Offline` read as this. | none |
| `DirectDev` | Editor and development builds only (stripped from release builds) | `Authorization: Bearer <dev key>` |
| `Proxy` | Release builds, once the backend proxy exists | `Authorization: Bearer <UGS access token>`; the proxy holds the real key |

If `DirectDev` has no URL, or a remote URL without a key, the manager logs a warning and uses built-in lines. The startup
gate then passes on internet + UGS sign-in alone (there is no AI endpoint to check) until #36's build-time key or #38's proxy lands.

## Turn contract

- **Input (~340 tokens estimated; 350-410 measured by Groq's tokenizer; budget 500):** persona card, standing line, one rumor line, the last 4 chat messages, the player's line (capped at 200 characters).
- **Output (`max_tokens: 130`):** `{"dialogue","mood","deltas":{"trust","affection","respect"},"quick_replies":[3]}`.
- **Guardrails:** relationship changes go through `FrontierSocialManager.ApplyDelta(..., DeltaSource.LlmTurn)` and are clamped to ±5 per axis per turn. Fields outside the contract (gold, items, quest flags) are ignored. A plain-text or cut-off reply still shows the villager's line, with zero relationship change.
- **Short drops (#37):** a transport error (no HTTP response) gets one quiet retry after 400 ms inside the same `timeoutSeconds` budget. If that fails too, or the turn times out, or the provider returns an HTTP error, a built-in line answers that turn and the conversation continues. While the connection monitor says the network is down, turns skip the call and answer instantly from built-in lines.
- **Long drops:** after 12 s of continuous trouble the game pauses behind "Reconnecting..." and resumes on the first successful call or probe. The probe is an unauthenticated `GET <baseUrl>/models` (no tokens; any HTTP status, 401 included, means reachable).

## Token logging

Every turn logs one line:

```
[LLM] bram via gpt-4o-mini: in=265 out=56 924ms json=ok mood=proud d=(T+9 A+1 R-2) | turns=2 avgIn=281 avgOut=56 ...
```

`in`/`out` use the provider's reported `usage`. Built-in-line turns show an estimate, marked `(est)`. Running totals are in
`FrontierLlmManager.Instance.Service.Usage`.

## Code map

| File | Role |
|---|---|
| `Assets/Scripts/Llm/ILLMProvider.cs` | Provider interface |
| `Assets/Scripts/Llm/LlmContracts.cs` | Chat message, request, completion and turn-result types |
| `Assets/Scripts/Llm/OpenAiCompatibleProvider.cs` | `UnityWebRequest` client for `/v1/chat/completions` |
| `Assets/Scripts/Llm/MockLlmProvider.cs` | Built-in, deterministic, keyword-sentiment replies (short-drop cushion, tests) |
| `Assets/Scripts/Llm/TurnPromptBuilder.cs` | Prompt assembly and token budget |
| `Assets/Scripts/Llm/TokenEstimator.cs` | Local token estimate (~4 chars/token) |
| `Assets/Scripts/Llm/TurnResponseParser.cs` | Tolerant JSON parsing and plain-text fallback |
| `Assets/Scripts/Llm/LlmTurnService.cs` | Timeout, blip retry, built-in-line fallback, connection reports |
| `Assets/Scripts/Net/ConnectionMonitor.cs` | Online-only state machine and `ConnectionReports` (#37) |
| `Assets/Scripts/Gameplay/FrontierConnectionManager.cs` | Startup gate, probes, "Reconnecting..." overlay (#37) |
| `Assets/Scripts/Llm/LlmUsageStats.cs` | Running token averages |
| `Assets/Scripts/Llm/LlmEndpointConfig.cs` | Settings loading (JSON, `.env`, environment variables) |
| `Assets/Scripts/Gameplay/FrontierLlmManager.cs` | Scene manager: history, applies deltas, `SendPlayerMessageAsync` for the hybrid chatbox |
| `Assets/Scripts/UI/HybridChatboxUI.cs` | Hybrid chatbox UI: free text, 2-3 quick-reply chips, portrait, standing, mood, typing state, keyboard lift |
| `Assets/Scripts/Llm/ChatboxHelpers.cs` | Pure-C# chatbox helpers (mood mapping, chip selection, delta formatting, typing animation, keyboard lift) |
| `Assets/Tests/EditMode/Llm/LlmLayerTests.cs` | Edit Mode tests |

## Security

- Never commit a key. `.env`, `.env.*` (except `.env.example`) and `UserSettings/` are git-ignored.
- Never ship a key inside an APK. `DirectDev` is compiled out of release builds; use `Proxy`.
