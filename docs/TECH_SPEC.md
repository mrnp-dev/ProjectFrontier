# Technical Specification - ProjectFrontier

- **Engine:** Unity `6000.3.29f1` (`96d2fe17529c`) - URP 2D (`com.unity.render-pipelines.universal: 17.3.0`)
- **Primary Target:** Android ARM64 (IL2CPP), API 25+ (`AndroidMinSdkVersion: 25`), OpenGLES3 primary
- **Baseline Commit:** `c0f4cfb` (Milestone 1 playable harness + verified 44 MB APK)

---

## 1. Installed Unity Packages (`Packages/manifest.json`)

| Package | Version | Role |
|---|---|---|
| `com.unity.render-pipelines.universal` | `17.3.0` | URP 2D Renderer + built-in `PixelPerfectCamera` |
| `com.unity.cinemachine` | `3.1.8` | 2D follow & title-pan camera rig |
| `com.unity.inputsystem` | `1.20.1` | Touch + keyboard/gamepad input |
| `com.unity.ugui` | `2.0.0` | Native-resolution `Screen Space - Overlay` UI + TextMeshPro |
| `com.unity.2d.tilemap` / `tilemap.extras` | `1.0.0` / `6.0.3` | Chunked 2D village/farm/dungeon tilemaps |
| `com.unity.2d.animation` / `aseprite` / `psdimporter` | `13.0.6` / `3.0.2` / `12.0.2` | 2D sprite authoring & animation import |
| `com.unity.services.authentication` | `3.9.0` | Anonymous player authentication |
| `com.unity.services.cloudsave` | `3.4.2` | Cross-session cloud save with local fallback |
| `com.unity.remote-config` | `4.3.0` | LiveOps feature flags / kill switches |
| `com.unity.services.leaderboards` | `2.4.0` | Leaderboard submission (`frontier_lanterns`) |
| `com.unity.pipeline` | `1.0.1` | Editor automation bridge |

> **Critical URP Rule:** Do **NOT** install legacy `com.unity.2d.pixel-perfect`. Always use `UnityEngine.Rendering.Universal.PixelPerfectCamera` bundled in URP.

---

## 2. Rendering & Pixel-Art Architecture

### Camera & Resolution Split
- **World Camera (`SampleScene.unity` / `FrontierSceneBuilder.cs`):**
  - `PixelPerfectCamera`: `assetsPPU = 16`, `refResolutionX = 480`, `refResolutionY = 270`, `cropFrame = CropFrame.None` (fills arbitrary phone aspect ratios with zero letter/pillar bars).
  - `orthographicSize = 8.4375f` (`270 / (2 * 16)`).
  - Avoid `UpscaleRenderTexture`; use `GridSnapping.PixelSnapping` to save mobile fill-rate.
  - `CinemachineBrain` + `CinemachineCamera` driven by `FrontierCameraRig` (title orbit pan vs. clamped player follow in `[-4.2, -2.2]` to `[4.2, 2.2]`).
- **UI Canvas (`FrontierShellUI.cs`):**
  - `Screen Space - Overlay` at **native device resolution** so chatbox text, buttons, and HUD stay crisp on 1080p+ screens while the pixel world renders cheaply at `480x270`.
  - Floating world prompts use crisp pixel-art sprites (e.g., `ui_talk_bubble`) or UI-layer popups rather than downsampled world-space text.

### Runtime Graphics Preset Matrix (`GraphicsSettingsManager.cs`)
URP 2D tier assets live under `Assets/Settings/Graphics/` (`URP2D_{Low,Medium,High}.asset` and `Renderer2D_{Low,Medium,High}.asset`), mapped to Unity `QualitySettings` indices `1=Low`, `2=Medium`, `3=High`.

- **First-Boot Auto-Detection:** `SystemInfo.systemMemorySize <= 3072` -> `Low`; otherwise `Medium`.
- **Persistence:** JSON saved at `Application.persistentDataPath/frontier_graphics.json`.
- **Hard Pixel-Art Invariants (All Tiers):** `QualitySettings.antiAliasing = 0`, `QualitySettings.vSyncCount = 0`, `cam.allowMSAA = false`, `cam.allowDynamicResolution = false`, `FilterMode.Point`, Mipmaps OFF. Never expose MSAA or Mipmap/Texture Quality sliders in menus.

| Setting | Low (`idx 1`) | Medium (`idx 2`) | High (`idx 3`) | Custom |
|---|---:|---:|---:|---|
| `targetFps` (`Application.targetFrameRate`) | `30` | `60` | `60` | `30-60` |
| Local `Light2D` Point Lights *(Global Light2D always on)* | `false` | `true` | `true` (cap <= 3 active) | User toggle |
| `renderPostProcessing` | `false` | `true` *(planned: off on Med for ultra-low GPUs if needed)* | `true` | User toggle |
| `cam.allowHDR` | `false` | `false` | `true` | Follows `postProcessing` |
| `particleDensity` | `0.25` | `0.65` | `1.0` | `0.0-1.0` |

> **Lighting Budget Rule:** Use a single Global `Light2D` for day/night color grading and fake additive glow sprites for torches/lanterns/spells. Real-time point `Light2D` adds full-screen render passes that bottleneck Mali-G52/Adreno 610 GPUs—keep them off on Low and <= 2-3 active on High.

---

## 3. Implemented Milestone 1 Codebase Map (`19` C# Files, `3,472` Lines)

All game scripts live in namespace `ProjectFrontier` under `Assets/Scripts/`, plus three UGS service wrappers at `Assets/` root. Every singleton implements `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` static resets for Domain Reload safety.

| Module | File(s) | Responsibility |
|---|---|---|
| **Game Loop** | `Gameplay/FrontierGameLoop.cs` (238 L) | Single owner of `RunState` (`Ready`, `Running`, `Paused`, `Won`, `Lost`), lantern shard count (`goalLanterns=5`), dusk timer (`90s`), met NPCs, and event bus (`StateChanged`, `ProgressChanged`). |
| **Player & Touch** | `Gameplay/PlayerController2D.cs` (165 L), `UI/VirtualJoystickUI.cs` (52 L) | Touch thumbstick (`maxRadius=46f`) + Input System movement (`moveSpeed=4.2`, `accel=18`), world bounds clamping `(-12.5,-7.2)`..`(12.5,7.2)`, NPC proximity detection (`interactRadius=2.1`). |
| **Procedural Motion** | `Gameplay/CharacterMotion2D.cs` (140 L), `Core/Ease.cs` (59 L) | Drives body, head, feet, and scarf transforms for walk stride, bob, lean, idle breathing, and `PlayPickupPop()`. |
| **Interactables** | `Gameplay/VillageNpc.cs` (79 L), `Gameplay/LanternShardPickup.cs` (48 L) | Registry of active NPCs (`AllNpcs`), bobbing speech-bubble indicator, dialogue completion hook, and trigger-based shard pickups (`CircleCollider2D`, `r=0.55`). |
| **Graphics & Feel** | `Core/GraphicsSettingsManager.cs` (227 L), `Core/FeelManager.cs` (226 L), `Core/FrontierPalette.cs` (38 L) | Tier switching, hitstop/camera shake/pop/flashes, centralized color bible. |
| **Camera** | `Camera/FrontierCameraRig.cs` (63 L) | Switches Cinemachine target between Title orbit pan and clamped gameplay follow. |
| **Audio** | `Audio/SynthAudioManager.cs` (247 L) | Procedural 22,050 Hz PCM clip synthesis (pickup blip, milestone/lose triads, UI click, ambient bed) + pause volume ducking (`0.5x`). |
| **UI Shell** | `UI/FrontierShellUI.cs` (406 L) | Manages Title, HUD, Dialogue panel, Pause/Graphics settings, End screen, and milestone banner/flash. |
| **Live Services** | `Live/FrontierLiveManager.cs` (223 L), `PlayerAuthentication/`, `PlayerSave/`, `LiveOps/` | Anonymous UGS auth, Cloud Save + local JSON cache (`FrontierProgressData`), Remote Config kill switch (120s refetch), Leaderboards (`frontier_lanterns` graceful fallback). |
| **Procedural Art & Scene Builder** | `Art/PixelArtFactory.cs` (358 L), `Editor/FrontierSceneBuilder.cs` (750 L) | Bakes 16-PPU sprites & TMP outline materials to `Assets/Art/Generated/` and deterministically rebuilds `SampleScene.unity`. |

---

## 4. Planned Milestone 2 Architecture: Token-Tight LLM + Gossip Engine

### Layered LLM Execution Model
- **Layer 0 (0 tokens, deterministic C#):** Ambient barks, shop menus, schedules, inventory/gold math, relationship clamping, and NPC-to-NPC gossip propagation.
- **Layer 1 (Streaming / Single-Call LLM):** Active player-NPC chatbox turns via a swappable C# `ILLMProvider` interface (OpenAI-compatible cloud API in production; local/mock provider for offline dev & automated tests).
- **Layer 2 (Async Background LLM):** End-of-day memory compression and Adventurers Guild quest flavor generation.

### Single-Call Structured Turn Contract
One API request per player message avoids double-call latency and cost:
- **Budget:** ~500 input tokens (persona card + compact relationship state + 1-line active rumor + recent 3-4 turns), `max_tokens: 130` output (~100 tokens).
- **Response Schema (Compact JSON):**
```json
{
  "dialogue": "Keep your distance if you think ironwork is a joke.",
  "mood": "annoyed",
  "deltas": { "trust": -3, "affection": -2, "respect": -1 },
  "quick_replies": [
    "I crossed a line. Sorry, Bram.",
    "Just testing your temper.",
    "Fine, I will buy elsewhere."
  ]
}
```
- **C# Guardrails:** Clamp `deltas` per turn (e.g., `[-5, +5]`), validate JSON with graceful plain-text fallback, and never allow the LLM to mutate gold, items, or quest flags directly.

### Deterministic Gossip & Trust Engine (C#)
- `float[,] npcTrustMatrix` (5x5 for Bram, Mira, Aldric, Lyra, Pip).
- `struct Rumor { string source; string tag; float valence; float strength; int expiresDay; }`.
- On evening schedule co-location (same zone/spot), NPCs exchange their top active rumor: `biasDelta = rumor.valence * listenerTrustInSpeaker * rumor.strength`.
- Next player chat injects one line: `RUMOR: <source> says <summary> (<belief_phrase>).`

---

## 5. Low-End Android Performance & Algorithm Blueprint

Engineered specifically to avoid GPU fill-rate stalls, thermal throttling, and GC hitches on Mali-G52 / Adreno 610 devices:

### Rendering & Assets
- **URP 2D Renderer:** Keep Depth Texture, Opaque Texture, HDR (except High), MSAA, and Anisotropic Filtering OFF; set Store Actions to Auto/Discard for tile-based mobile GPUs.
- **Sprite Packing:** Enable `SpriteAtlasV2` (`EditorSettings.spritePackerMode`) via `IPreprocessBuildWithReport`. Pixel-art tileset atlases use `Point`, MipMaps OFF, Tight Packing OFF, `Padding >= 4`, Compression `None (RGBA32)` (a 1024x1024 RGBA32 atlas is only 4 MB with zero seams). Use **ASTC 6x6** for large UI panels, portraits, and backgrounds (~6-8x VRAM savings).
- **Overdraw Control:** Non-tile sprites (cottages, trees, characters) use **Tight Mesh** import; tilemaps use `TilemapRenderer.mode = Chunk`, <= 3 visible transparent tile layers per pixel, one `CompositeCollider2D` per collision layer, and Custom Transparency Sort Axis `(0, 1, 0)` for zero-cost Y-sorting.
- **TextMeshPro:** Static baked primary font atlas (scale 1, Latin sampling 70-90, 10% padding), `AutoSize` OFF on live text, numeric `TMP_Text.SetText` overloads to prevent string allocations.

### Core Algorithms (Milestone 2 & 3)
| Domain | Chosen Algorithm / Pattern | Why for Low-End Mobile |
|---|---|---|
| **Town Raids & Mob Hordes** | Dijkstra **Flow Field** | Single grid pass steers dozens of raiders simultaneously (`O(1)` lookup per unit). |
| **NPC & Pet Navigation** | Flat-array **A*** + binary heap + frame-budgeted request queue | Zero per-node heap allocations; prevents multi-NPC spikes on schedule transitions. |
| **Crowd Avoidance** | **Context Steering** | Smooth separation without expensive Physics2D body shoving. |
| **Spatial Queries** | **Uniform Spatial Hash Grid** | `O(1)` nearby entity/interactable queries instead of scene scans. |
| **Farm & Machines** | Chunked data arrays + **timestamp-delta catch-up** | Zero per-frame tick cost for off-screen crops/machines. |
| **Dungeon Generation** | **BSP / Room-Graph + Drunkard Walk + Flood-Fill** | Fast deterministic seed generation with guaranteed connectivity. |
| **NPC AI** | Deterministic schedules + **staggered Utility AI** | Spreads AI evaluation across frames; predictable gossip co-location. |

### Memory, Physics & Audio Budgets
- **GC Discipline:** Enable **Incremental GC**; zero LINQ, lambdas, or string concatenation in `Update`/`FixedUpdate`; use `ObjectPool<T>` and preallocated arrays for non-allocating `Physics2D` queries.
- **Physics2D:** `Time.fixedDeltaTime` at `1/60s` or `1/50s`, `Rigidbody2D.interpolation = Interpolate`, aggressively disable unused pairs in the Physics 2D Layer Collision Matrix (e.g., Crops vs. Projectiles, NPC vs. NPC shoving).
- **Audio:** Mono SFX @ `22,050 Hz`; decompress only tiny frequent SFX `< 200 KB`; stream music/ambience in background via Vorbis; keep `AudioMixer <= 3` levels with **no Master Reverb**.

---

## 6. Build, Verification & Pre-Release Checklist

### Verified Android Build Baseline
- **Artifact:** `Builds/Android/ProjectFrontier.apk` (**44 MB**, built Nov 25, 2026)
- **Configuration:** IL2CPP, ARM64 (`AndroidTargetArchitectures: 2`), Release build, Min SDK 25.
- **Git Hygiene:** `.gitignore` excludes `/Builds/`, `*.apk`, `*.aab`, `/.utmp/`, `/Screenshots/`, `/AgentScripts/`, and `/Assets/Captures/`.

### Immediate Pre-Milestone-2 Technical Tasks
1. Sideload `Builds/Android/ProjectFrontier.apk` onto a physical Android device via `adb install -r` and capture baseline frame-time / RAM telemetry across Low, Medium, and High *(pending attached device)*.
2. ~~Update Android Application Identifier from `com.DefaultCompany.urp_2d` and `companyName` from `DefaultCompany`~~ -> **Done:** `com.frontierstudio.projectfrontier` / `FrontierStudio` in `ProjectSettings/ProjectSettings.asset`.
3. ~~Set Android `Managed Stripping Level` explicitly to `Medium` or `High`~~ -> **Done:** Android `managedStrippingLevel: 2` (`Medium`) in `ProjectSettings/ProjectSettings.asset`.
4. Create the `frontier_lanterns` leaderboard in Unity Dashboard (or replace with Milestone 2/3 progression metrics).
5. ~~Complete the Pause Menu **Custom** graphics controls panel and add a brief delay before the end-screen panel appears over the `VILLAGE LIT!` banner~~ -> **Done:** 2x2 Custom GFX controls (FPS, 2D Lights, Post-FX, Particles) and `1.45s` `RevealEndScreenAfterBanner` delay in `FrontierShellUI.cs` + `FrontierSceneBuilder.cs`.
