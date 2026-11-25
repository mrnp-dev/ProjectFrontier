# Game Design Document (GDD) - ProjectFrontier

- **Working Title:** ProjectFrontier
- **Setting:** Cozy High-Fantasy Frontier (Oakhaven Village)
- **Genre:** 2D Pixel-Art Life-Sim Action-RPG with Reactive LLM NPCs
- **Perspective & Aesthetic:** Top-down 2D pixel art (`480x270 @ 16 PPU`), warm hearth-lit frontier timber & meadow palette

---

## 1. High Concept

You are a newcomer rebuilding a small homestead on the edge of **Oakhaven**, a cozy high-fantasy frontier settlement bordering monster-haunted ruins. Days flow between tending your farm, taking Adventurers Guild bounties, delving into nearby dungeons, and befriending—or antagonizing—five strong-willed villagers whose AI-driven conversations and behind-the-scenes gossip reshape the town's fate when raids strike.

---

## 2. World & Vertical Slice Map

The vertical slice compresses the full game fantasy into four walkable, interconnected zones:

1. **Homestead / Farmstead (West):** Small tillable crop plot, storage/crafting bench, and pet pen.
2. **Oakhaven Village Square (Center):** Central hearth brazier, four timber cottages, Bram's Forge, Mira's Tavern, and Pip's Herb Stall. *(Implemented: hearth square ringed by Mira's Tavern (NW), the Guild Hall (NE, standing in for the North/East hall until that zone exists), Bram's Forge (SW), and Pip's cottage + herb stall (SE); Lyra holds the square.)*
3. **Adventurers Guild Hall (North/East):** Quest board, rank ledger, and Captain Aldric's briefing desk.
4. **Frontier Ruins Entrance (East/South):** Portal into **1 Dungeon Biome (3 procedural floors + 1 mini-boss)**.

---

## 3. Core Gameplay Loops

### A. Milestone 1 Technical-Floor Harness *(Implemented in `c0f4cfb`)*
Used to validate controls, feel, UI, audio, and mobile rendering before layering full RPG systems:
- **Objective:** Collect **5 Lantern Shards** scattered around Oakhaven Square before the **90-second dusk timer** expires to light the village hearth.
- **Villagers:** Originally two placeholders (`Elder Rowan`, `Scout Mira`); replaced in #11 by the 5-NPC cast below, with proximity speech-bubble indicators and a scripted greeting chatbox (Mira awards a bonus shard).
- **State Flow:** `Title -> Running -> Paused / Settings -> Won or Lost -> Instant Restart`.

### B. Target Daily Loop *(Milestone 2 & 3)*
```text
Morning (Farm & Town) -> Midday (Guild Quests / Dungeon Delve) -> Evening (Tavern & Gossip Exchange) -> Night (Sleep & Day Summary)
```
- **Morning:** Water/harvest crops, feed pets, craft potions with Pip, upgrade gear with Bram, check Guild bounties with Aldric.
- **Midday:** Explore dungeon floors, fight enemies in snappy 2D real-time combat, collect ore/relics/monster drops.
- **Evening:** Visit the tavern or town square; chat with NPCs through the hybrid chatbox. NPCs sharing schedule spots exchange their top active rumors.
- **Day 5 Climax (Town Raid):** Frontier raiders assault Oakhaven. Your combat preparation, farm/potion supplies, and **social standing with all 5 NPCs** determine how the town defends itself.

---

## 4. The 5 Frontier NPCs (Vertical Slice Cast)

Depth over breadth: every villager is anchored to a gameplay system AND a web of social trust/rivalry.

| NPC | Role | Gameplay System Tie-In | Personality & Arc Hooks |
|---|---|---|---|
| **Bram** | Blacksmith | Weapon/armor forging & dungeon-ore upgrades | Proud, blunt craftsman. Deeply offended if his work is mocked; fiercely loyal once respected. |
| **Mira** | Tavern Keeper | Social hub, rumor broker, meals/morale buffs | Warm, observant gossip central. High trust from most villagers; what Mira believes spreads fast. |
| **Captain Aldric** | Guild Master | Adventurers Guild bounties, rank progression, raid defense | Disciplined veteran balancing frontier safety against scarce supplies. Values reliability and courage. |
| **Lyra** | Rival Adventurer | Competitive quests, dungeon co-op partner, romance/rivalry arc | Arrogant, sharp-tongued rival. Can evolve into your worst enemy, strongest dungeon ally, or lover. |
| **Pip** | Herbalist & Beast-Keeper | Farming seeds, alchemy/potions, tameable pets | Gentle, eccentric caretaker who notices kindness to creatures and brings sustainable buffs to the homestead. |

**Persona cards** live as `NpcPersona` ScriptableObjects in `Assets/Data/Personas/` (`Bram`, `Mira`, `Aldric`, `Lyra`, `Pip`): id, name, role, home spot, gameplay tie-in, personality, speech style, hooks, and 2 scripted greeting lines. `NpcPersona.ToPromptCard()` renders the ~90-token persona block for the LLM turn (#13). The scene builder seeds missing cards from `NpcPersonaSeeds` and never overwrites Inspector edits.

| NPC | Home spot | Signature look | Speech style |
|---|---|---|---|
| Bram | Forge (SW) | Bald, ember-ginger beard, leather apron | Short, gruff, forge metaphors |
| Mira | Tavern (NW) | Auburn bun, wine dress, cream apron | Warm, teasing, calls you "love" |
| Captain Aldric | Guild Hall (NE) | Crested steel helm, navy cape, breastplate | Clipped, military, "recruit" |
| Lyra | Village square | Platinum ponytail, violet jerkin, bandolier | Smug, turns everything into a bet |
| Pip | Herb stall (SE) | Leaf hat, olive robe, satchel | Soft, rambling, talks to herbs |

---

## 5. LLM Conversation & Relationship Design

### Hybrid Chatbox UX
To stay effortless on touchscreens while preserving open-ended expression:
- **2-3 Quick-Reply Chips** generated dynamically with each NPC turn (e.g., *Apologize sincerely*, *Tease about the blade*, *Ask about the ruins*).
- **Free-Text Input Bar** always available for custom player messages.
- **Brevity Rule:** Every NPC reply is strictly **1-2 short sentences (~25 words)** to keep reading snappy on mobile screens and minimize token usage.

### Multi-Axis Relationships
Instead of a single flat affection bar, C# tracks bounded relationship axes per NPC (e.g., **Trust**, **Affection**, **Respect/Rivalry**) that resolve into clear relational states:
- **Enemy:** Refuses discounts/quests, spreads hostile rumors, may hide or actively sabotage/betray during a raid.
- **Neutral:** Standard shop rates and guarded dialogue.
- **Friend:** Discounts, rare crafting/quest unlocks, fights alongside you during town raids.
- **Lover:** Deep personal arc, unique home/raid support moments, high mutual defense priority.

### C# Authority Over Game Rules
The LLM generates **personality, dialogue, mood, and suggested small deltas**, while **C# enforces all state rules, clamps per-turn deltas, checks inventory/gold/quest state, and triggers unlocks**. An LLM cannot hallucinate free legendary weapons or jump an NPC from Enemy to Lover in one turn.

---

## 6. Gossip & Bias Engine (Zero-Token Social Simulation)

NPC-to-NPC gossip runs deterministically in C# so the village feels alive between conversations at **zero LLM token cost**:

1. **Hidden Trust Matrix:** Every ordered pair `(Listener, Speaker)` has a trust weight in `[0.0, 1.0]`. Example: Bram trusts Mira (`0.8`), distrusts Lyra (`0.2`).
2. **Rumor Generation:** Notable player actions (insulting Bram's craft, saving Pip's beast, outscoring Lyra on a bounty, failing a Guild contract) instantiate a `Rumor`:
   ```json
   { "source": "Bram", "tag": "insulted_craft", "valence": -0.6, "strength": 0.8, "expiresDay": 6 }
   ```
3. **Daily Co-Location Exchange:** At the end of each day (or evening tavern overlap), NPCs sharing a schedule spot exchange their highest-strength active rumor.
4. **Deterministic Bias Shift:**
   ```text
   biasDelta = rumor.valence * listenerTrustInSpeaker * rumor.strength
   ```
   The listener's Trust/Affection toward the player shifts immediately in C#, and the rumor is stored in the listener's active rumor slot until `expiresDay`.
5. **Single-Line Prompt Injection:** The next time the player talks to that listener, one compact line is injected into their prompt:
   > `RUMOR: Bram says player mocked his smithing (you mostly believe Bram).`

Result: The player visibly experiences consequences across town—like Mira questioning why you picked a fight with Bram—within ~15 extra input tokens.

---

## 7. Interconnected Systems (Milestone 3)

| System | Feeds Into | Fed By |
|---|---|---|
| **Farming & Crafting** | Herbs, healing/buff potions, town raid supplies | Seeds & tips from Pip, gold from Guild quests |
| **Dungeon Combat** | Ore, monster drops, relics, combat XP | Potions from Farm, weapons from Bram, companions (Pets / Lyra) |
| **Adventurers Guild** | Gold, rank unlocks, raid intel, structured goals | Dungeon clears, town reputation with Aldric |
| **Pets** | Combat assist, gathering perks, cozy charm | Care & taming items via Pip, farm crops |
| **Town Raids (Day 5)** | Town survival, major reputation swings, endgame test | Gear (Bram), potions (Farm/Pip), NPC loyalty (Gossip & Chat) |

---

## 8. Art & Audio Direction

### Visual Style (`480x270 @ 16 PPU`)
- **Crisp Pixel World:** 16 pixels per unit, `FilterMode.Point`, Mipmaps OFF, MSAA OFF, pixel snapping enabled (`UnityEngine.Rendering.Universal.PixelPerfectCamera`, `CropFrame.None` so every Android aspect ratio fills cleanly without black bars).
- **Native-Resolution UI:** `Screen Space - Overlay` uGUI Canvas so dialogue text, quick-reply chips, and buttons stay razor-sharp on 1080p+ phones.
- **Cozy Frontier Palette (`FrontierPalette.cs`):** Warm Hearth Gold, Cream Parchment, Meadow Moss, Frontier Teal, Timber Dark, Terracotta roofs, Slate UI panels, and Brass borders.
- **Character Presentation:** Procedural squash/stretch, walk bob, foot stride, scarf sway, and idle breathing (`CharacterMotion2D`), plus crisp pixel-art talk bubbles rather than blurry low-res world text.

### Audio Direction
- Cozy acoustic/folk-inspired ambience and snappy tactile feedback (shard chimes, UI clicks, milestone triads, combat hits).
- Milestone 1 uses lightweight **22,050 Hz procedural PCM synthesis (`SynthAudioManager`)** with pause-menu volume ducking; production assets will follow mobile audio budgets (mono 22.05 kHz SFX, Vorbis-streamed ambience/music, <= 3 AudioMixer levels, no Master Reverb).
