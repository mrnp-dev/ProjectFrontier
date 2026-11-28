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
    /// Feeds the scripted greeting and the compact LLM persona block (#13/#14/#41).
    /// Keep every field short: the whole card should stay around ~200 estimated prompt tokens.
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
        [Tooltip("Concrete things this villager knows and likes (job, wares, tastes). Gives the LLM something specific to " +
                 "answer with instead of a signature line (#41). Short clauses, second person.")]
        public string[] knowledge = new string[0];
        [Tooltip("One short opinion per other villager, e.g. \"Lyra reckless, chips every blade\".")]
        public string[] opinions = new string[0];
        [Tooltip("Story / relationship hooks the LLM and gossip engine can lean on.")]
        public string[] hooks = new string[0];

        [Header("Scripted Greeting (Layer 0, 0 tokens)")]
        [TextArea(1, 3)] public string[] greetingLines = new string[0];

        [Header("Starter Quick Replies (Before First LLM Turn)")]
        public string[] starterQuickReplies = new string[0];

        private static readonly string[] DefaultStarterReplies =
        {
            "Tell me about yourself.",
            "Any news around Oakhaven?",
            "Need any help?"
        };

        public string[] GetStarterQuickReplies()
        {
            if (starterQuickReplies != null && starterQuickReplies.Length >= 2)
                return starterQuickReplies;
            return DefaultStarterReplies;
        }

        /// <summary>
        /// Compact single-block persona text for the LLM system prompt. Called once per conversation open,
        /// never per frame, so the StringBuilder allocation is acceptable.
        /// </summary>
        public string ToPromptCard()
        {
            var sb = new StringBuilder(512);
            sb.Append("You are ").Append(displayName).Append(", ").Append(roleTitle).Append(" of Oakhaven.");
            if (!string.IsNullOrWhiteSpace(personality)) sb.Append(' ').Append(personality.Trim());
            sb.Append('\n');
            sb.Append("Speech: ").Append(speechStyle).Append('\n');
            AppendList(sb, "Knows: ", knowledge);
            AppendList(sb, "Thinks: ", opinions);
            AppendList(sb, "Story: ", hooks);
            return sb.ToString();
        }

        private static void AppendList(StringBuilder sb, string label, string[] items)
        {
            if (items == null) return;
            bool any = false;
            foreach (string item in items)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                sb.Append(any ? "; " : label).Append(item.Trim());
                any = true;
            }
            if (any) sb.Append('\n');
        }
    }
}
