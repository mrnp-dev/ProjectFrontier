# Product Requirements Document (PRD) - ProjectFrontier

- **Product:** ProjectFrontier *(working title; final commercial title TBD)*
- **Document Status:** Living Baseline (reflects Milestone 1 commit `c0f4cfb` and Milestone 2+ plan)
- **Engine / Platform:** Unity 6 (`6000.3.29f1`, URP 2D) - Android mobile (touch-first primary)
- **Genre:** Cozy High-Fantasy 2D Pixel-Art Life-Sim Action-RPG with LLM-Powered NPCs

---

## 1. Executive Summary & Vision

ProjectFrontier brings the cozy daily cadence of *Stardew Valley* (farming, town life, dungeon delving, pets, progression) together with **reactive, LLM-powered villagers** who remember how you treat them, talk about you behind your back via a **Gossip & Bias Engine**, and change how they act during quests, trade, and town raids.

Unlike desktop-heavy AI experiments, ProjectFrontier is architected from day one for **low-end Android phones** (2-4 GB RAM, Mali-G52 / Adreno 610 class GPUs), combining a crisp `480x270 @ 16 PPU` pixel-art world with native-resolution touch UI, strict LLM token budgets, and runtime **Low / Medium / High / Custom** graphics presets.

---

## 2. Target Audience & Hardware Baseline

### Target Players
- Mobile players who enjoy cozy life-sim RPGs (*Stardew Valley*, *Rune Factory*, *Harvest Moon*) and emergent social narratives.
- Players who want natural, expressive conversations with NPCs without slow walls of text or desktop-only hardware requirements.

### Target Hardware (Primary Constraint)
| Tier | Reference Hardware | Target Performance |
|---|---|---|
| **Minimum (Low)** | 2-3 GB RAM Android 7.1+ (API 25+), Mali-G52 / Adreno 610 / PowerVR class GPU | Stable **30 FPS**, no thermal runaway, crisp 16-PPU pixel art, legible native-res UI |
| **Recommended (Medium)** | 4-6 GB RAM mid-range Android | Stable **60 FPS**, simple 2D lighting, 65% particles |
| **High** | 6+ GB RAM modern Android | Stable **60 FPS**, full 2D lighting, HDR & post-processing, 100% particles |

---

## 3. Product Pillars

1. **Lively, Consequential NPCs (Depth Over Breadth):** Start with **5 deeply connected frontier villagers** rather than 30 shallow ones. Every conversation can shift multi-axis relationships (Neutral, Friend, Lover, Enemy) and seed rumors that ripple across town.
2. **Hybrid Touch-Friendly Conversation:** Mobile typing should never be mandatory for every turn. Players interact through a **hybrid chatbox**: free-text input paired with **2-3 contextual quick-reply chips** generated per turn.
3. **Token-Tight, Snappy Pacing:** NPC replies stay punchy (**1-2 short sentences, ~25 words**, capped at `max_tokens: 130`, ~500 input / ~100 output tokens per turn) so chats feel like a real RPG and keep cloud LLM costs sustainable.
4. **Interconnected Life-Sim & ARPG Loop:** Social choices, farming, dungeon combat, Adventurers Guild quests, pets, and town raids feed each other—e.g., farm herbs buff dungeon runs, dungeon loot upgrades gear, and NPC loyalty decides who fights beside you, hides, or betrays you during a raid.
5. **Low-End Android First:** Pixel-perfect low-res world (`480x270`) + native-res UI canvas + runtime graphics presets + zero-alloc gameplay loops.

---

## 4. Scope Breakdown: Implemented vs. Planned

### Phase 1 - Technical Floor Harness (Milestone 1 - **Implemented in `c0f4cfb`**)
Purpose: Prove the Unity 6 URP 2D rendering pipeline, touch ergonomics, graphics presets, UGS live-service wiring, and Android IL2CPP ARM64 build pipeline end-to-end before investing in LLM/RPG systems.

- Walkable Oakhaven frontier village square (`Assets/Scenes/SampleScene.unity`) built deterministically via `FrontierSceneBuilder`.
- Starter playable loop (`FrontierGameLoop`): gather 5 Lantern Shards within a 90-second dusk countdown, interact with 2 starter villagers (`Elder Rowan` and `Scout Mira`), win/lose/restart states.
- Touch-first controls (`VirtualJoystickUI` + context talk button) plus desktop keyboard support via Input System.
- Native-res overlay shell (`FrontierShellUI`): Title, HUD, Dialogue panel, Pause/Settings, End screen.
- Runtime **Low / Medium / High / Custom** graphics controller (`GraphicsSettingsManager`) with RAM-based first-boot auto-detection.
- Procedural pixel-art sprite baking (`PixelArtFactory`), character locomotion (`CharacterMotion2D`), feel/juice (`FeelManager`), and 22.05 kHz procedural audio (`SynthAudioManager`).
- UGS wiring (`FrontierLiveManager`): anonymous auth, cloud save + local JSON cache fallback, remote config kill switch, leaderboard submission with graceful fallback.
- Verified 44 MB Android ARM64 IL2CPP release APK (`Builds/Android/ProjectFrontier.apk`).

### Phase 2 - Core Social & LLM Vertical Slice (Milestone 2 - **Next Priority**)
Purpose: Replace the starter lantern-shard harness with the true core differentiator in Oakhaven.

- Full **5-NPC cast** in Oakhaven: **Bram** (Blacksmith), **Mira** (Tavern Keeper), **Captain Aldric** (Guild Master), **Lyra** (Rival Adventurer), **Pip** (Herbalist & Beast-Keeper).
- **Hybrid Chatbox UI**: free-text input + 2-3 clickable quick-reply chips + speaker mood indicator.
- **Swappable LLM Layer (`ILLMProvider`)**: OpenAI-compatible cloud API integration (built-in lines as the cushion for one failed turn during a short connection drop, and for tests), single-call structured JSON output (dialogue + bounded relationship deltas + mood + quick replies).
- **Deterministic C# Gossip & Bias Engine**: hidden NPC-to-NPC trust matrix, `Rumor` creation on notable player actions, end-of-day co-location rumor exchange, and single-line rumor injection into NPC prompts.
- **Day/Night Cycle & Deterministic NPC Schedules**: NPCs move between daily spots (forge, square, tavern, guild hall, herb garden) where gossip exchanges trigger.
- **Pause Menu & Custom Preset Completion**: expose granular Custom toggles in UI and fix win-screen timing over milestone banner.

### Phase 3 - Life-Sim, Dungeon & Raid Vertical Slice (Milestone 3 - **Planned**)
Purpose: Connect social state to the full cozy ARPG gameplay loop inside a compact slice map.

- **Farmstead & Crafting:** Chunked crop plots, timestamp-delta growth while the app is closed, plus day-skip growth, herbs/potions tied to Pip.
- **Adventurers Guild:** Quest board managed by Captain Aldric with async LLM-flavored bounty descriptions.
- **Dungeon Biome (1 biome, 3 floors + mini-boss):** Procedural BSP/room-graph + drunkard-walk generation, real-time 2D ARPG combat, loot fueling Bram's smithing upgrades.
- **Pets:** Tameable/upgradeable beast companions tied to Pip.
- **Day 5 Town Raid Event:** Flow-field enemy waves attacking Oakhaven where each NPC's relationship score determines whether they fight alongside the player, barricade/hide, or sabotage/betray.

### Out of Scope (Until Post-Vertical-Slice Validation)
- Monetization (IAP, ads, battle passes) — explicitly deferred until core loop retention is proven.
- Multiplayer / co-op / guild social servers.
- Expanding beyond 5 NPCs or 1 dungeon biome before the slice feels fun.
- Voice-to-text input (may be evaluated post-slice as an accessibility add-on).

---

## 5. Key User Journeys

1. **First Launch on Low-End Android:** App boots cleanly, auto-selects `Low` (<= 3 GB RAM) or `Medium`, checks the internet connection, signs in anonymously via UGS and checks the AI endpoint (or shows "Connect to the internet to play" with Retry), and drops the player onto the Title screen with live Oakhaven preview in under 5 seconds.
2. **Hybrid NPC Conversation (Milestone 2):** Player walks up to Bram with the virtual joystick, taps Talk, reads a crisp 1-2 sentence greeting, and either taps a suggested reply chip or types a custom insult/compliment. Within ~1-2s, Bram responds in character, his relationship axes update within C#-clamped bounds, and a `Rumor` is queued if the interaction was notable.
3. **Gossip Ripple (Milestone 2):** At evening tavern time, Bram and Mira share a schedule spot. The deterministic C# Gossip Engine propagates Bram's rumor to Mira weighted by Mira's trust in Bram (`0.8`). Next morning, Mira greets the player coldly referencing what Bram told her—without spending extra LLM tokens on background simulation.
4. **Cozy Day -> Dungeon -> Raid Loop (Milestone 3):** Tend crops in the morning, accept a Guild bounty from Aldric, craft/upgrade with Pip and Bram, delve into the 3-floor dungeon with a pet or allied Lyra, and defend Oakhaven on Day 5 with allies earned through social play.

---

## 6. Non-Functional Requirements

- **Frame Rate & Thermals:** >= 30 FPS sustained on Low preset (30 FPS cap to avoid thermal throttling); >= 60 FPS on Medium/High on mid-tier devices.
- **Memory & APK Size:** Keep release APK lean (< 100 MB for vertical slice; Milestone 1 baseline is 44 MB) and avoid GC spikes during gameplay (`Update`/`FixedUpdate` zero-alloc discipline).
- **Online-only (#37, user decision 2026-11-28):** Villagers depend on the AI, so the game requires internet. Startup gate: no gameplay until internet, UGS sign-in and the AI endpoint are reachable ("Connect to the internet to play" + Retry). Short drops are absorbed quietly (one retry, then built-in lines). After ~12 s of continuous trouble the game pauses behind "Reconnecting..." (day clock, timers and input frozen) and resumes automatically. The local save stays as a cache and crash safety; Cloud Save is the cross-device source (#33). There is no offline play mode.
- **Security & Privacy:** Never ship LLM API keys in client builds or git commits; route production cloud LLM requests through a thin authenticated backend proxy or user-configured endpoint for dev builds.

---

## 7. Success Metrics & Exit Gates

| Milestone | Exit Gate |
|---|---|
| **Milestone 1 (Done)** | Zero console errors/warnings in Play Mode; full Title -> Run -> Win/Lose -> Restart loop verified; runtime graphics presets switch cleanly; Android ARM64 IL2CPP APK compiles cleanly (`44 MB`). |
| **Milestone 2** | Player can converse with all 5 NPCs via free-text and quick-reply chips; end-of-day gossip visibly alters another NPC's greeting and trust score on Day 2+; avg LLM turn stays within ~500 in / <= 130 out tokens; sideloaded APK holds >= 30 FPS on low-end Android. |
| **Milestone 3** | Full 5-day playable slice (Farm -> Talk/Gossip -> Guild Quest -> 3-Floor Dungeon -> Day 5 Raid) completes end-to-end with at least two distinct raid outcomes driven by NPC relationships. |
