using System;
using System.Collections.Generic;
using ProjectFrontier.Farm;

namespace ProjectFrontier.Guild
{
    /// <summary>
    /// Adventurers Guild rank tiers overseen by Captain Aldric (#20).
    /// Higher ranks unlock higher-paying bounties and carry social weight across Oakhaven.
    /// </summary>
    public enum GuildRank
    {
        Bronze = 0,
        Silver = 1,
        Gold = 2,
        Vanguard = 3
    }

    public enum BountyObjectiveKind
    {
        DeliverItem = 0,
        HarvestCrops = 1,
        CraftPotions = 2,
        SpeakWithVillager = 3,
        CollectLanternShards = 4,
        DungeonClear = 5
    }

    /// <summary>
    /// Immutable C#-authoritative bounty contract. Objectives, rank requirements, rewards, and
    /// relationship impacts are strictly enforced in C#; Layer 2 LLM only supplies optional flavor prose.
    /// </summary>
    public sealed class BountyDef
    {
        public string Id { get; }
        public string Title { get; }
        public GuildRank MinRank { get; }
        public BountyObjectiveKind ObjectiveKind { get; }
        public string TargetId { get; }
        public int RequiredCount { get; }
        public int GoldReward { get; }
        public int GuildXpReward { get; }
        public int AldricTrustDelta { get; }
        public int AldricRespectDelta { get; }
        public string SponsorNpcId { get; }
        public int SponsorTrustDelta { get; }
        public int SponsorRespectDelta { get; }
        public string ObjectiveSummary { get; }
        public string StaticDescription { get; }

        public BountyDef(
            string id,
            string title,
            GuildRank minRank,
            BountyObjectiveKind objectiveKind,
            string targetId,
            int requiredCount,
            int goldReward,
            int guildXpReward,
            int aldricTrustDelta,
            int aldricRespectDelta,
            string sponsorNpcId,
            int sponsorTrustDelta,
            int sponsorRespectDelta,
            string objectiveSummary,
            string staticDescription)
        {
            Id = id;
            Title = title;
            MinRank = minRank;
            ObjectiveKind = objectiveKind;
            TargetId = targetId ?? string.Empty;
            RequiredCount = Math.Max(1, requiredCount);
            GoldReward = Math.Max(0, goldReward);
            GuildXpReward = Math.Max(1, guildXpReward);
            AldricTrustDelta = aldricTrustDelta;
            AldricRespectDelta = aldricRespectDelta;
            SponsorNpcId = sponsorNpcId ?? string.Empty;
            SponsorTrustDelta = sponsorTrustDelta;
            SponsorRespectDelta = sponsorRespectDelta;
            ObjectiveSummary = objectiveSummary ?? string.Empty;
            StaticDescription = staticDescription ?? string.Empty;
        }
    }

    /// <summary>
    /// Deterministic C# catalog of Guild ranks and bounty contracts for Phase 3 (#20).
    /// </summary>
    public static class GuildCatalog
    {
        public const string BountyMoonmintSupplies = "bounty_moonmint_supplies";
        public const string BountyHomesteadHarvest = "bounty_homestead_harvest";
        public const string BountyTavernCheck = "bounty_tavern_check";
        public const string BountyApothecaryBrew = "bounty_apothecary_brew";
        public const string BountySunpetalCourier = "bounty_sunpetal_courier";
        public const string BountyHearthShards = "bounty_hearth_shards";
        public const string BountyEmberReserve = "bounty_ember_reserve";
        public const string BountyRuinsRecon = "bounty_ruins_recon";

        public const int SilverXpThreshold = 100;
        public const int GoldXpThreshold = 250;
        public const int VanguardXpThreshold = 450;

        private static readonly BountyDef[] AllBountiesList =
        {
            // Rank 0 — Bronze (immediate life-sim & village integration)
            new BountyDef(
                id: BountyMoonmintSupplies,
                title: "Moonmint Watch Poultices",
                minRank: GuildRank.Bronze,
                objectiveKind: BountyObjectiveKind.DeliverItem,
                targetId: ItemCatalog.HerbMoonmint,
                requiredCount: 2,
                goldReward: 35,
                guildXpReward: 55,
                aldricTrustDelta: 3,
                aldricRespectDelta: 4,
                sponsorNpcId: "pip",
                sponsorTrustDelta: 2,
                sponsorRespectDelta: 1,
                objectiveSummary: "Deliver 2x Moonmint",
                staticDescription: "Night-watch scouts need fresh moonmint bundles before the mist thickens along the east road."),
            new BountyDef(
                id: BountyHomesteadHarvest,
                title: "Frontier Harvest Ledger",
                minRank: GuildRank.Bronze,
                objectiveKind: BountyObjectiveKind.HarvestCrops,
                targetId: string.Empty,
                requiredCount: 2,
                goldReward: 30,
                guildXpReward: 50,
                aldricTrustDelta: 2,
                aldricRespectDelta: 3,
                sponsorNpcId: string.Empty,
                sponsorTrustDelta: 0,
                sponsorRespectDelta: 0,
                objectiveSummary: "Harvest 2 mature crops",
                staticDescription: "A settlement holds only as long as its stores do. Tend your plots and log two clean harvests."),
            new BountyDef(
                id: BountyTavernCheck,
                title: "Oakhaven Watch Check-In",
                minRank: GuildRank.Bronze,
                objectiveKind: BountyObjectiveKind.SpeakWithVillager,
                targetId: string.Empty,
                requiredCount: 2,
                goldReward: 25,
                guildXpReward: 45,
                aldricTrustDelta: 2,
                aldricRespectDelta: 2,
                sponsorNpcId: "mira",
                sponsorTrustDelta: 2,
                sponsorRespectDelta: 1,
                objectiveSummary: "Speak with 2 villagers",
                staticDescription: "Make the rounds through Oakhaven and report back on village morale before sundown."),

            // Rank 1 — Silver (crafting & patrol contracts)
            new BountyDef(
                id: BountyApothecaryBrew,
                title: "Watchman's Salve Stock",
                minRank: GuildRank.Silver,
                objectiveKind: BountyObjectiveKind.DeliverItem,
                targetId: ItemCatalog.PotionMoonmintSalve,
                requiredCount: 1,
                goldReward: 55,
                guildXpReward: 75,
                aldricTrustDelta: 4,
                aldricRespectDelta: 5,
                sponsorNpcId: "bram",
                sponsorTrustDelta: 1,
                sponsorRespectDelta: 2,
                objectiveSummary: "Deliver 1x Moonmint Salve",
                staticDescription: "Bram's apprentices keep scorching knuckles at the forge. Brew a proper salve for the Guild kit."),
            new BountyDef(
                id: BountySunpetalCourier,
                title: "Sunpetal Swiftness Brew",
                minRank: GuildRank.Silver,
                objectiveKind: BountyObjectiveKind.DeliverItem,
                targetId: ItemCatalog.PotionSunpetalTea,
                requiredCount: 1,
                goldReward: 75,
                guildXpReward: 90,
                aldricTrustDelta: 4,
                aldricRespectDelta: 6,
                sponsorNpcId: "lyra",
                sponsorTrustDelta: 1,
                sponsorRespectDelta: 3,
                objectiveSummary: "Deliver 1x Sunpetal Tea",
                staticDescription: "Outriders running the ruin border requested a swiftness tonic. Lyra doubts a newcomer can brew it cleanly."),
            new BountyDef(
                id: BountyHearthShards,
                title: "Perimeter Lantern Sweep",
                minRank: GuildRank.Silver,
                objectiveKind: BountyObjectiveKind.CollectLanternShards,
                targetId: string.Empty,
                requiredCount: 3,
                goldReward: 60,
                guildXpReward: 80,
                aldricTrustDelta: 3,
                aldricRespectDelta: 5,
                sponsorNpcId: string.Empty,
                sponsorTrustDelta: 0,
                sponsorRespectDelta: 0,
                objectiveSummary: "Gather 3 Lantern Shards",
                staticDescription: "Stray hearth shards keep drifting toward the treeline. Recover three to steady the watch beacons."),

            // Rank 2 — Gold (high-tier alchemy & dungeon readiness)
            new BountyDef(
                id: BountyEmberReserve,
                title: "Ember Tonic Readiness",
                minRank: GuildRank.Gold,
                objectiveKind: BountyObjectiveKind.DeliverItem,
                targetId: ItemCatalog.PotionEmberTonic,
                requiredCount: 1,
                goldReward: 110,
                guildXpReward: 120,
                aldricTrustDelta: 6,
                aldricRespectDelta: 8,
                sponsorNpcId: "bram",
                sponsorTrustDelta: 2,
                sponsorRespectDelta: 4,
                objectiveSummary: "Deliver 1x Ember Tonic",
                staticDescription: "Scouts report heavy tracks past the ridge. Stock the Guild armory with an Ember Tonic for the vanguard."),
            new BountyDef(
                id: BountyRuinsRecon,
                title: "Ruins Perimeter Recon",
                minRank: GuildRank.Gold,
                objectiveKind: BountyObjectiveKind.DungeonClear,
                targetId: string.Empty,
                requiredCount: 1,
                goldReward: 130,
                guildXpReward: 150,
                aldricTrustDelta: 6,
                aldricRespectDelta: 9,
                sponsorNpcId: "lyra",
                sponsorTrustDelta: 2,
                sponsorRespectDelta: 4,
                objectiveSummary: "Clear 1 dungeon floor",
                staticDescription: "Push into the frontier ruins, clear a sector, and return with an accurate count of what lurks below.")
        };

        private static readonly Dictionary<string, BountyDef> BountiesById = BuildMap();

        private static Dictionary<string, BountyDef> BuildMap()
        {
            var map = new Dictionary<string, BountyDef>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < AllBountiesList.Length; i++)
                map[AllBountiesList[i].Id] = AllBountiesList[i];
            return map;
        }

        public static IReadOnlyList<BountyDef> AllBounties => AllBountiesList;

        public static BountyDef GetBounty(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return BountiesById.TryGetValue(id, out var def) ? def : null;
        }

        public static GuildRank GetRankForXp(int xp)
        {
            if (xp >= VanguardXpThreshold) return GuildRank.Vanguard;
            if (xp >= GoldXpThreshold) return GuildRank.Gold;
            if (xp >= SilverXpThreshold) return GuildRank.Silver;
            return GuildRank.Bronze;
        }

        public static int GetXpThreshold(GuildRank rank) => rank switch
        {
            GuildRank.Silver => SilverXpThreshold,
            GuildRank.Gold => GoldXpThreshold,
            GuildRank.Vanguard => VanguardXpThreshold,
            _ => 0
        };

        public static int GetNextRankXpThreshold(GuildRank rank) => rank switch
        {
            GuildRank.Bronze => SilverXpThreshold,
            GuildRank.Silver => GoldXpThreshold,
            GuildRank.Gold => VanguardXpThreshold,
            _ => VanguardXpThreshold
        };

        public static string GetRankTitle(GuildRank rank) => rank switch
        {
            GuildRank.Bronze => "BRONZE RECRUIT",
            GuildRank.Silver => "SILVER RUNNER",
            GuildRank.Gold => "GOLD WARDEN",
            GuildRank.Vanguard => "FRONTIER VANGUARD",
            _ => "BRONZE RECRUIT"
        };

        public static string GetRankShortLabel(GuildRank rank) => rank switch
        {
            GuildRank.Bronze => "BRONZE",
            GuildRank.Silver => "SILVER",
            GuildRank.Gold => "GOLD",
            GuildRank.Vanguard => "VANGUARD",
            _ => "BRONZE"
        };

        public static string GetRankPerkSummary(GuildRank rank) => rank switch
        {
            GuildRank.Bronze => "Access to Bronze supply & patrol bounties.",
            GuildRank.Silver => "Unlocks Silver alchemy & patrol contracts + Aldric trust boost.",
            GuildRank.Gold => "Unlocks Gold dungeon & vanguard contracts + raid priority.",
            GuildRank.Vanguard => "Full Guild commendation; maximum standing bonus across Oakhaven.",
            _ => string.Empty
        };

        public static string GetSponsorDisplayName(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return "Captain Aldric";
            return npcId.ToLowerInvariant() switch
            {
                "bram" => "Bram",
                "mira" => "Mira",
                "aldric" => "Captain Aldric",
                "lyra" => "Lyra",
                "pip" => "Pip",
                _ => npcId
            };
        }
    }
}
