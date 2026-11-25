#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectFrontier.Editor
{
    /// <summary>
    /// Seeds the five vertical-slice persona cards into Assets/Data/Personas/.
    /// Assets are created only when missing, so designer edits made in the Inspector are never overwritten
    /// by a scene rebuild. Delete an asset to re-seed it from these defaults.
    /// </summary>
    public static class NpcPersonaSeeds
    {
        public const string PersonaDir = "Assets/Data/Personas";

        public static NpcPersona Bram() => Ensure("Bram", p =>
        {
            p.npcId = "bram";
            p.displayName = "Bram";
            p.roleTitle = "Blacksmith";
            p.homeSpot = NpcHomeSpot.Forge;
            p.gameplayTieIn = "Weapon and armor forging; dungeon-ore upgrades.";
            p.personality = "Proud, blunt craftsman. Takes any slight to his work personally; fiercely loyal once you earn his respect.";
            p.speechStyle = "Short, gruff sentences. Forge and iron metaphors. Never says please.";
            p.hooks = new[]
            {
                "Mocking his craft becomes a grudge he spreads",
                "Wants rare dungeon ore for a masterpiece blade",
                "Trusts Mira; thinks Lyra is reckless"
            };
            p.greetingLines = new[]
            {
                "Hmph. Another newcomer. Mind the sparks.",
                "Lanterns dying already? Fetch the shards. Iron and fire wait for no one."
            };
        });

        public static NpcPersona Mira() => Ensure("Mira", p =>
        {
            p.npcId = "mira";
            p.displayName = "Mira";
            p.roleTitle = "Tavern Keeper";
            p.homeSpot = NpcHomeSpot.Tavern;
            p.gameplayTieIn = "Social hub and rumor broker; meals and morale buffs.";
            p.personality = "Warm, observant gossip central. Remembers everything; what Mira believes spreads through town fast.";
            p.speechStyle = "Friendly and teasing, calls you 'love'. Slips in hints of what others have said.";
            p.hooks = new[]
            {
                "Hears every rumor first and passes it on",
                "Trusted by nearly every villager",
                "Worries Aldric stretches the Guild too thin"
            };
            p.greetingLines = new[]
            {
                "Welcome in, love! You look like you could use a warm bowl.",
                "Dusk is coming. Take this spare spark from my hearth and go light the square."
            };
        });

        public static NpcPersona Aldric() => Ensure("Aldric", p =>
        {
            p.npcId = "aldric";
            p.displayName = "Captain Aldric";
            p.roleTitle = "Guild Master";
            p.homeSpot = NpcHomeSpot.GuildHall;
            p.gameplayTieIn = "Adventurers Guild bounties, rank progression, raid defense.";
            p.personality = "Disciplined veteran weighing frontier safety against scarce supplies. Values reliability and courage over talk.";
            p.speechStyle = "Clipped and military. Calls you 'recruit'. Never wastes a word.";
            p.hooks = new[]
            {
                "Hands out bounties and Guild ranks",
                "Readies Oakhaven for the coming raid",
                "Respects Lyra's skill, not her attitude"
            };
            p.greetingLines = new[]
            {
                "Recruit. The Guild ledger has no name for you yet.",
                "Earn one. Gather the lantern shards before dark. Reliably."
            };
        });

        public static NpcPersona Lyra() => Ensure("Lyra", p =>
        {
            p.npcId = "lyra";
            p.displayName = "Lyra";
            p.roleTitle = "Rival Adventurer";
            p.homeSpot = NpcHomeSpot.Square;
            p.gameplayTieIn = "Competitive quests, dungeon co-op partner, rivalry/romance arc.";
            p.personality = "Arrogant, sharp-tongued top adventurer. Hates losing; quietly respects anyone who keeps up.";
            p.speechStyle = "Smug and teasing. Turns everything into a contest or a bet.";
            p.hooks = new[]
            {
                "Races you for Guild bounties",
                "Can become worst enemy, dungeon partner, or love interest",
                "Bram calls her reckless; she calls him slow"
            };
            p.greetingLines = new[]
            {
                "Oh look, the new hero. Cute.",
                "Bet I could light this whole square before you find a single shard."
            };
        });

        public static NpcPersona Pip() => Ensure("Pip", p =>
        {
            p.npcId = "pip";
            p.displayName = "Pip";
            p.roleTitle = "Herbalist & Beast-Keeper";
            p.homeSpot = NpcHomeSpot.HerbStall;
            p.gameplayTieIn = "Farming seeds, potions, tameable pets.";
            p.personality = "Gentle, eccentric caretaker. Notices how you treat creatures and plants; rewards kindness quietly.";
            p.speechStyle = "Soft and rambling, with plant and animal asides. Talks to the herbs mid-sentence.";
            p.hooks = new[]
            {
                "Supplies seeds, potions, and taming treats",
                "Remembers anyone who harms a beast",
                "Shy around Aldric's soldiers"
            };
            p.greetingLines = new[]
            {
                "Oh! Hello. Careful, the moonmint bites. Only a little.",
                "Lantern glow keeps the night-moths calm. Could you gather the shards? They'd thank you."
            };
        });

        private static NpcPersona Ensure(string assetName, System.Action<NpcPersona> seed)
        {
            Directory.CreateDirectory(PersonaDir);
            string path = $"{PersonaDir}/{assetName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<NpcPersona>(path);
            if (existing != null) return existing;

            var persona = ScriptableObject.CreateInstance<NpcPersona>();
            seed(persona);
            AssetDatabase.CreateAsset(persona, path);
            AssetDatabase.SaveAssets();
            return persona;
        }
    }
}
#endif
