using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier.World
{
    /// <summary>The five named Oakhaven schedule spots villagers move between across the day.</summary>
    public enum VillageSpot
    {
        Forge = 0,
        Square = 1,
        Tavern = 2,
        GuildHall = 3,
        HerbGarden = 4
    }

    /// <summary>
    /// Deterministic schedule table for Oakhaven's 5 vertical-slice villagers.
    /// Morning places every villager at their signature home spot; Midday sends them on errands;
    /// Evening creates two intentional co-location hubs (Tavern: Bram/Mira/Aldric, Square: Lyra/Pip)
    /// for the zero-token Gossip & Bias Engine (#16); Night sends them home (with Lyra stopping by the Tavern).
    /// </summary>
    public static class NpcScheduleTable
    {
        public static readonly string[] CastOrder = { "bram", "mira", "aldric", "lyra", "pip" };

        // Distinct standing sub-slots around each village landmark so co-located NPCs form a tidy group
        // rather than stacking on the same pixel. All coordinates sit clear of building colliders.
        private static readonly Vector2[][] SpotSlots =
        {
            // Forge (SW: Forge at (-6.5, -4.2), Anvil at (-4.5, -4.5))
            new[]
            {
                new Vector2(-4.3f, -3.3f), // Bram default
                new Vector2(-5.5f, -2.6f),
                new Vector2(-3.3f, -2.5f),
                new Vector2(-4.8f, -2.1f)
            },
            // Square (Center: Hearth at (0, 0.2))
            new[]
            {
                new Vector2(1.6f, 1.6f),   // Lyra default
                new Vector2(-1.6f, 1.6f),
                new Vector2(1.6f, -1.4f),
                new Vector2(-1.6f, -1.4f)
            },
            // Tavern (NW: Tavern at (-6.2, 3.8), Barrels at (-8.4, 2.9))
            new[]
            {
                new Vector2(-6.7f, 1.8f),  // Mira default
                new Vector2(-5.3f, 1.8f),
                new Vector2(-6.0f, 0.9f),
                new Vector2(-4.6f, 1.1f)
            },
            // GuildHall (NE: GuildHall at (6.2, 3.8), QuestBoard at (8.4, 2.9))
            new[]
            {
                new Vector2(5.7f, 1.8f),   // Aldric default
                new Vector2(7.0f, 1.8f),
                new Vector2(6.3f, 0.9f),
                new Vector2(4.7f, 1.2f)
            },
            // HerbGarden (SE: Cottage at (6.5, -4.2), HerbStall at (4.3, -4.5))
            new[]
            {
                new Vector2(2.9f, -4.1f),  // Pip default
                new Vector2(2.2f, -3.0f),
                new Vector2(3.6f, -3.0f),
                new Vector2(1.8f, -4.4f)
            }
        };

        public static VillageSpot GetScheduledSpot(string npcId, DayPhase phase, int day = 1)
        {
            string key = (npcId ?? string.Empty).Trim().ToLowerInvariant();
            switch (key)
            {
                case "bram":
                    return phase switch
                    {
                        DayPhase.Morning => VillageSpot.Forge,
                        DayPhase.Midday => VillageSpot.Square,
                        DayPhase.Evening => VillageSpot.Tavern,
                        DayPhase.Night => VillageSpot.Forge,
                        _ => VillageSpot.Forge
                    };
                case "mira":
                    return phase switch
                    {
                        DayPhase.Morning => VillageSpot.Tavern,
                        DayPhase.Midday => VillageSpot.HerbGarden,
                        DayPhase.Evening => VillageSpot.Tavern,
                        DayPhase.Night => VillageSpot.Tavern,
                        _ => VillageSpot.Tavern
                    };
                case "aldric":
                    return phase switch
                    {
                        DayPhase.Morning => VillageSpot.GuildHall,
                        DayPhase.Midday => VillageSpot.Forge,
                        DayPhase.Evening => VillageSpot.Tavern,
                        DayPhase.Night => VillageSpot.GuildHall,
                        _ => VillageSpot.GuildHall
                    };
                case "lyra":
                    return phase switch
                    {
                        DayPhase.Morning => VillageSpot.Square,
                        DayPhase.Midday => VillageSpot.GuildHall,
                        DayPhase.Evening => VillageSpot.Square,
                        DayPhase.Night => VillageSpot.Tavern,
                        _ => VillageSpot.Square
                    };
                case "pip":
                    return phase switch
                    {
                        DayPhase.Morning => VillageSpot.HerbGarden,
                        DayPhase.Midday => VillageSpot.HerbGarden,
                        DayPhase.Evening => VillageSpot.Square,
                        DayPhase.Night => VillageSpot.HerbGarden,
                        _ => VillageSpot.HerbGarden
                    };
                default:
                    return VillageSpot.Square;
            }
        }

        /// <summary>
        /// Deterministic sub-slot index for <paramref name="npcId"/> within its scheduled spot so co-located
        /// villagers stand side by side. The spot's home owner always claims slot 0 when present.
        /// </summary>
        public static int GetSlotIndex(string npcId, DayPhase phase, int day = 1)
        {
            string key = (npcId ?? string.Empty).Trim().ToLowerInvariant();
            VillageSpot spot = GetScheduledSpot(key, phase, day);
            string owner = GetSpotOwner(spot);
            if (key == owner) return 0;

            int slot = GetScheduledSpot(owner, phase, day) == spot ? 1 : 0;
            for (int i = 0; i < CastOrder.Length; i++)
            {
                string other = CastOrder[i];
                if (other == owner) continue;
                if (GetScheduledSpot(other, phase, day) != spot) continue;
                if (other == key) return slot;
                slot++;
            }
            return slot;
        }

        public static Vector2 GetSpotWorldPosition(VillageSpot spot, int slotIndex = 0)
        {
            int s = (int)spot;
            if (s < 0 || s >= SpotSlots.Length) s = (int)VillageSpot.Square;
            Vector2[] slots = SpotSlots[s];
            int idx = ((slotIndex % slots.Length) + slots.Length) % slots.Length;
            return slots[idx];
        }

        public static Vector2 GetTargetWorldPosition(string npcId, DayPhase phase, int day = 1)
        {
            VillageSpot spot = GetScheduledSpot(npcId, phase, day);
            int slot = GetSlotIndex(npcId, phase, day);
            return GetSpotWorldPosition(spot, slot);
        }

        /// <summary>
        /// Populates <paramref name="groups"/> with the NPC IDs sharing each spot for a given phase.
        /// Used by the Gossip & Bias Engine (#16) for deterministic evening co-location exchanges.
        /// </summary>
        public static void GetCoLocatedGroups(DayPhase phase, Dictionary<VillageSpot, List<string>> groups, int day = 1)
        {
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            groups.Clear();
            for (int i = 0; i < CastOrder.Length; i++)
            {
                string id = CastOrder[i];
                VillageSpot spot = GetScheduledSpot(id, phase, day);
                if (!groups.TryGetValue(spot, out List<string> list))
                {
                    list = new List<string>(4);
                    groups.Add(spot, list);
                }
                list.Add(id);
            }
        }

        public static string GetSpotOwner(VillageSpot spot) => spot switch
        {
            VillageSpot.Forge => "bram",
            VillageSpot.Square => "lyra",
            VillageSpot.Tavern => "mira",
            VillageSpot.GuildHall => "aldric",
            VillageSpot.HerbGarden => "pip",
            _ => "lyra"
        };

        public static string SpotDisplayName(VillageSpot spot) => spot switch
        {
            VillageSpot.Forge => "the Forge",
            VillageSpot.Square => "the Village Square",
            VillageSpot.Tavern => "Mira's Tavern",
            VillageSpot.GuildHall => "the Guild Hall",
            VillageSpot.HerbGarden => "the Herb Garden",
            _ => "Oakhaven"
        };
    }
}
