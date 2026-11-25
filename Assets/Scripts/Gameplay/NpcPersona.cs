using System.Text;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>Where a villager spends their default (non-scheduled) time in Oakhaven.</summary>
    public enum NpcHomeSpot
    {
        Forge,
        Tavern,
        GuildHall,
        HerbStall,
        Square
    }

    /// <summary>
    /// Persona card for one vertical-slice villager. Designer-editable asset under Assets/Data/Personas/.
    /// Feeds the scripted greeting today and the compact LLM persona block later (#13/#14),
    /// so keep every field short: the whole card should stay around ~100 prompt tokens.
    /// </summary>
    [CreateAssetMenu(fileName = "NpcPersona", menuName = "ProjectFrontier/NPC Persona")]
    public class NpcPersona : ScriptableObject
    {
        [Header("Identity")]
        public string npcId = "npc";
        public string displayName = "Villager";
        public string roleTitle = "Villager";
        public NpcHomeSpot homeSpot = NpcHomeSpot.Square;

        [Header("Card")]
        [Tooltip("Which gameplay system this villager anchors.")]
        [TextArea(1, 3)] public string gameplayTieIn;
        [TextArea(2, 4)] public string personality;
        [TextArea(1, 3)] public string speechStyle;
        [Tooltip("Story / relationship hooks the LLM and gossip engine can lean on.")]
        public string[] hooks = new string[0];

        [Header("Scripted Greeting (Layer 0, 0 tokens)")]
        [TextArea(1, 3)] public string[] greetingLines = new string[0];

        /// <summary>
        /// Compact single-block persona text for the LLM system prompt. Called once per conversation open,
        /// never per frame, so the StringBuilder allocation is acceptable.
        /// </summary>
        public string ToPromptCard()
        {
            var sb = new StringBuilder(256);
            sb.Append("You are ").Append(displayName).Append(", ").Append(roleTitle).Append(" of Oakhaven.\n");
            sb.Append("Personality: ").Append(personality).Append('\n');
            sb.Append("Speech: ").Append(speechStyle).Append('\n');
            if (hooks != null && hooks.Length > 0)
            {
                sb.Append("Hooks: ");
                for (int i = 0; i < hooks.Length; i++)
                {
                    if (i > 0) sb.Append("; ");
                    sb.Append(hooks[i]);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
