# ProjectFrontier

**ProjectFrontier** *(working title)* is a cozy high-fantasy, 2D pixel-art life-sim Action-RPG built in **Unity 6 (`6000.3.29f1`, URP 2D)** for **touch-first Android devices**, engineered from day one to run smoothly on low-end phones (2-4 GB RAM, Mali-G52 / Adreno 610 class GPUs).

Its core hook is a small, tightly connected frontier village powered by **LLM-driven NPCs** and a deterministic **Gossip & Bias Engine**, expanding into farming, dungeon runs, an Adventurers Guild, pets, and town raids where social relationships directly shape gameplay outcomes.

---

## Project Status

| Layer | Status | Details |
|---|---|---|
| **Foundation (`cdaaef1`)** | Done | Unity `6000.3.29f1` URP 2D baseline, `480x270 @ 16 PPU`, Low/Medium/High URP 2D assets, Cinemachine `3.1.8`, UGS packages, Android IL2CPP ARM64 config. |
| **Milestone 1 (`c0f4cfb`)** | Done | Playable Oakhaven village technical-floor harness: touch joystick + keyboard movement, 2 starter NPCs, 5 lantern shard pickups, 90s dusk loop, native-res uGUI shell, procedural audio & feel, runtime graphics presets, UGS fallback wiring, and verified 44 MB Android release APK (`Builds/Android/ProjectFrontier.apk`). |
| **Milestone 2 (Core Slice)** | Planned | 5 connected frontier NPCs (Bram, Mira, Aldric, Lyra, Pip), hybrid LLM chatbox (free-text + quick-reply chips), deterministic C# gossip/trust engine, and daily NPC schedules. |
| **Milestone 3+ (Full Loop)** | Planned | Farmstead & crafting, 3-floor dungeon biome + mini-boss, Adventurers Guild quests, pets, and Day 5 town raid climax. |

---

## Documentation

- **[Product Requirements Document (`docs/PRD.md`)](docs/PRD.md)** - Product vision, target low-end Android hardware, user journeys, Milestone 1 vs. Milestone 2+ scope, and success criteria.
- **[Game Design Document (`docs/GDD.md`)](docs/GDD.md)** - Cozy frontier setting, 5-NPC cast, hybrid LLM dialogue & gossip/bias mechanics, interconnected gameplay loops, and `480x270 @ 16 PPU` art/audio direction.
- **[Technical Specification (`docs/TECH_SPEC.md`)](docs/TECH_SPEC.md)** - Unity 6 URP 2D pipeline, graphics tier matrix, C# architecture map, UGS services, planned token-tight LLM/gossip architecture, low-end mobile algorithms, and Android build pipeline.

---

## Quickstart

### Prerequisites
- **Unity Editor:** `6000.3.29f1` (LTS) with **Android Build Support** (OpenJDK 17, Android SDK & NDK r27c, IL2CPP).
- **OS:** Linux (tested on Ubuntu 24.04) or Windows/macOS.

### Opening & Running in Unity
1. Open `/media/mrnp/New Volume/Repositories/ProjectFrontier` in Unity `6000.3.29f1`.
2. Open `Assets/Scenes/SampleScene.unity`.
3. *(Optional)* To deterministically regenerate the Milestone 1 sprites and scene hierarchy, run from the Unity menu:
   ```text
   ProjectFrontier -> Rebuild Milestone 1 Village Scene
   ```
4. Press **Play** to test the Oakhaven village loop.

### Controls (Milestone 1)
- **Touch / Mobile:** On-screen virtual thumbstick (`VirtualJoystickUI`) + context **TALK** button + top-right **GFX** / **PAUSE** buttons.
- **Desktop / Editor:** `WASD` or Arrow keys to move, `E` / `Space` to interact with nearby NPCs, `Esc` to pause.

---

## Runtime Graphics Presets

`GraphicsSettingsManager` auto-detects device RAM on first launch (`<= 3072 MB` -> `Low`, otherwise `Medium`) and persists user selection to `Application.persistentDataPath/frontier_graphics.json`.

| Preset | Target FPS | Dynamic 2D Point Lights | Post-Processing | HDR | Particle Density |
|---|---:|---|---|---|---:|
| **Low** | 30 | Off (Global Light2D only) | Off | Off | 25% |
| **Medium** | 60 | On | On | Off | 65% |
| **High** | 60 | On | On | On | 100% |
| **Custom** | 30-60 | Toggleable | Toggleable | Follows PostFX | 0-100% |

*All presets strictly enforce `FilterMode.Point`, Mipmaps OFF, and MSAA 0 to keep 16-PPU pixel art razor-sharp.*

---

## Repository Layout

```text
ProjectFrontier/
├── Assets/
│   ├── Art/Generated/            # Deterministically baked 16-PPU sprites & TMP outline materials
│   ├── LiveOps/                  # UGS Remote Config wrapper
│   ├── PlayerAuthentication/     # UGS Anonymous Authentication wrapper
│   ├── PlayerSave/               # UGS Cloud Save wrapper
│   ├── Scenes/SampleScene.unity  # Milestone 1 playable Oakhaven village scene
│   ├── Scripts/
│   │   ├── Art/                  # PixelArtFactory (procedural pixel-art sprite baking)
│   │   ├── Audio/                # SynthAudioManager (22.05 kHz procedural PCM audio)
│   │   ├── Camera/               # FrontierCameraRig (Cinemachine 3 2D rig)
│   │   ├── Core/                 # GraphicsSettingsManager, FeelManager, FrontierPalette, Ease
│   │   ├── Editor/               # FrontierSceneBuilder menu builder
│   │   ├── Gameplay/             # FrontierGameLoop, PlayerController2D, CharacterMotion2D, VillageNpc, LanternShardPickup
│   │   ├── Live/                 # FrontierLiveManager (Auth, Save, RemoteConfig, Leaderboards)
│   │   └── UI/                   # FrontierShellUI, VirtualJoystickUI
│   └── Settings/Graphics/        # URP2D_{Low,Medium,High} & Renderer2D_{Low,Medium,High} assets
├── docs/
│   ├── PRD.md                    # Product Requirements Document
│   ├── GDD.md                    # Game Design Document
│   └── TECH_SPEC.md              # Technical Specification
├── Builds/                       # Git-ignored local builds (e.g., Builds/Android/ProjectFrontier.apk)
├── Packages/manifest.json        # Unity 6 package manifest
└── ProjectSettings/              # Unity project & Android player configuration
```

---

## Known Milestone 1 Follow-Ups

- Sideload and profile `Builds/Android/ProjectFrontier.apk` on a physical low-end Android handset (`adb install -r Builds/Android/ProjectFrontier.apk`) once a device is attached.
- Create the `frontier_lanterns` leaderboard resource in Unity Dashboard (currently degrades gracefully to `Rank: unavailable`).
- *(Completed in Phase 1 Polish)* Production Android Application Identifier (`com.frontierstudio.projectfrontier`), `companyName` (`FrontierStudio`), Android `Medium` managed stripping, 2x2 Custom graphics controls (FPS, 2D Lights, Post-FX, Particles), and 1.45s end-screen reveal delay over the `VILLAGE LIT!` banner.
