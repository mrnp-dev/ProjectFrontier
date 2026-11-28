using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProjectFrontier.Farm;
using ProjectFrontier.Guild;
using ProjectFrontier.Llm;
using ProjectFrontier.Social;

namespace ProjectFrontier.Guild.Tests
{
    public class GuildAndBountyTests
    {
        private sealed class FakeOnlineProvider : ILLMProvider
        {
            private readonly Func<LlmRequest, CancellationToken, Task<LlmCompletion>> _handler;
            public string Name => "fake-layer2";
            public bool RequiresNetwork => true;

            public FakeOnlineProvider(Func<LlmRequest, CancellationToken, Task<LlmCompletion>> handler)
            {
                _handler = handler;
            }

            public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct) => _handler(request, ct);
        }

        [Test]
        public void RankGatingAndActiveCap_AreStrictlyEnforcedInCSharp()
        {
            var guild = new GuildState(initialDay: 1);
            Assert.AreEqual(GuildRank.Bronze, guild.Rank);

            // Silver and Gold bounties must be rejected at Bronze rank
            Assert.IsFalse(guild.TryAccept(GuildCatalog.BountyApothecaryBrew, 1, out string silverReason));
            StringAssert.Contains("SILVER", silverReason);
            Assert.IsFalse(guild.TryAccept(GuildCatalog.BountyEmberReserve, 1, out string goldReason));
            StringAssert.Contains("GOLD", goldReason);

            // Accept up to MaxActiveBounties (2)
            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyMoonmintSupplies, 1, out _));
            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyHomesteadHarvest, 1, out _));
            Assert.AreEqual(2, guild.ActiveBounties.Count);

            // Third concurrent bounty rejected
            Assert.IsFalse(guild.TryAccept(GuildCatalog.BountyTavernCheck, 1, out string capReason));
            StringAssert.Contains("MAX", capReason);
        }

        [Test]
        public void TurnIn_PaysGoldAndXp_PromotesRank_AndHooksRelationshipsAndGossip()
        {
            var guild = new GuildState(initialDay: 1);
            var inv = new InventoryState(seedStarterKit: true);
            var social = new SocialState();
            var trust = GossipTrustMatrix.CreateDefault();

            int startGold = inv.Gold;
            inv.AddItem(ItemCatalog.HerbMoonmint, 2);

            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyMoonmintSupplies, 1, out _));
            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyHomesteadHarvest, 1, out _));

            // Turn in Moonmint supply delivery (55 XP, 35G, Aldric +3T/+4R, Pip +2T/+1R)
            Assert.IsTrue(guild.TryTurnIn(GuildCatalog.BountyMoonmintSupplies, inv, social, out BountyTurnInResult r1));
            Assert.IsTrue(r1.Succeeded);
            Assert.AreEqual(35, r1.GoldAwarded);
            Assert.AreEqual(55, r1.XpAwarded);
            Assert.AreEqual(startGold + 35, inv.Gold);
            Assert.AreEqual(0, inv.GetCount(ItemCatalog.HerbMoonmint), "Delivered items must be consumed");
            Assert.AreEqual(GuildRank.Bronze, guild.Rank);
            Assert.AreEqual(3, social.Get("aldric").Trust);
            Assert.AreEqual(4, social.Get("aldric").Respect);
            Assert.AreEqual(2, social.Get("pip").Trust);

            // Same bounty cannot be re-accepted on the same day
            Assert.IsFalse(guild.TryAccept(GuildCatalog.BountyMoonmintSupplies, 1, out _));

            // Progress & turn in Harvest bounty (50 XP -> total 105 XP -> Silver Rank!)
            Assert.IsFalse(guild.TryTurnIn(GuildCatalog.BountyHomesteadHarvest, inv, social, out _));
            Assert.AreEqual(1, guild.RecordProgress(BountyObjectiveKind.HarvestCrops, null, 2));
            Assert.IsTrue(guild.TryTurnIn(GuildCatalog.BountyHomesteadHarvest, inv, social, out BountyTurnInResult r2));
            Assert.IsTrue(r2.RankedUp);
            Assert.AreEqual(GuildRank.Bronze, r2.PreviousRank);
            Assert.AreEqual(GuildRank.Silver, r2.NewRank);
            Assert.AreEqual(GuildRank.Silver, guild.Rank);
            Assert.Greater(social.Get("lyra").Respect, 0, "Rank promotion should earn rival Lyra's respect");

            // Silver bounty is now unlocked!
            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyApothecaryBrew, 1, out _));

            // Evening gossip: Aldric shares the promotion rumor with Mira & Bram at the Tavern
            var transfers = new List<GossipTransfer>();
            int gossipCount = GossipEngine.RunExchange(social, trust,
                new IReadOnlyList<string>[] { new[] { "bram", "mira", "aldric" } }, transfers);
            Assert.Greater(gossipCount, 0);
            Assert.Greater(social.Get("mira").Trust, 0, "Mira should gain trust after hearing Aldric's promotion rumor");
            string miraGreeting = GossipEngine.BuildGreetingLine(social.Rumors, "mira", social.Day, id => id == "aldric" ? "Aldric" : id);
            StringAssert.Contains("rank promotion", miraGreeting);
        }

        [Test]
        public void AbandonContract_AppliesAldricPenalty_AndSeedsNegativeGossipRumor()
        {
            var guild = new GuildState(initialDay: 1);
            var social = new SocialState();
            var trust = GossipTrustMatrix.CreateDefault();

            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyTavernCheck, 1, out _));
            Assert.IsTrue(guild.TryAbandon(GuildCatalog.BountyTavernCheck, social, out _));
            Assert.AreEqual(0, guild.ActiveBounties.Count);
            Assert.AreEqual(1, guild.AbandonedCount);
            Assert.Less(social.Get("aldric").Trust, 0);
            Assert.Less(social.Get("aldric").Respect, 0);

            // Spreads negatively via evening gossip
            GossipEngine.RunExchange(social, trust, new IReadOnlyList<string>[] { new[] { "bram", "mira", "aldric" } });
            Assert.Less(social.Get("bram").Trust, 0);
            string bramGreeting = GossipEngine.BuildGreetingLine(social.Rumors, "bram", social.Day, id => id == "aldric" ? "Aldric" : id);
            StringAssert.Contains("signed Guild contract", bramGreeting);
        }

        [Test]
        public void SpeakWithVillagerObjective_CountsDistinctVillagersOnly()
        {
            var guild = new GuildState(initialDay: 1);
            Assert.IsTrue(guild.TryAccept(GuildCatalog.BountyTavernCheck, 1, out _));

            Assert.AreEqual(1, guild.RecordProgress(BountyObjectiveKind.SpeakWithVillager, "mira", 1));
            Assert.AreEqual(0, guild.RecordProgress(BountyObjectiveKind.SpeakWithVillager, "mira", 1), "Same villager must not count twice");
            Assert.AreEqual(1, guild.RecordProgress(BountyObjectiveKind.SpeakWithVillager, "bram", 1));
            Assert.IsTrue(guild.IsReadyToTurnIn(GuildCatalog.BountyTavernCheck, null));
        }

        [Test]
        public void BountyFlavorService_ParsesJson_AndFallsBackCleanlyOfflineOrOnError()
        {
            BountyDef def = GuildCatalog.GetBounty(GuildCatalog.BountyMoonmintSupplies);

            // 1. Valid JSON from online provider
            var online = new FakeOnlineProvider((req, ct) => Task.FromResult(new LlmCompletion
            {
                Text = "```json\n{\"flavor\":\"Fog rolled down the ridge before dawn; scouts need moonmint poultices at the gate.\"}\n```"
            }));
            var svcOk = new BountyFlavorService(online, () => true, 2f);
            BountyFlavorResult okRes = svcOk.GenerateFlavorAsync(def, 2, GuildRank.Bronze).GetAwaiter().GetResult();
            Assert.IsFalse(okRes.UsedFallback);
            StringAssert.Contains("Fog rolled down", okRes.Text);

            // 2. Offline / mock provider degrades immediately to static description
            var svcOffline = new BountyFlavorService(new MockLlmProvider(), () => true, 2f);
            BountyFlavorResult offRes = svcOffline.GenerateFlavorAsync(def, 2, GuildRank.Bronze).GetAwaiter().GetResult();
            Assert.IsTrue(offRes.UsedFallback);
            Assert.AreEqual(def.StaticDescription, offRes.Text);

            // 3. Throwing online provider degrades to static description
            var throwing = new FakeOnlineProvider((req, ct) => throw new LlmProviderException("HTTP 503"));
            var svcErr = new BountyFlavorService(throwing, () => true, 2f);
            BountyFlavorResult errRes = svcErr.GenerateFlavorAsync(def, 2, GuildRank.Bronze).GetAwaiter().GetResult();
            Assert.IsTrue(errRes.UsedFallback);
            Assert.AreEqual(def.StaticDescription, errRes.Text);

            // 4. Overlong text is sanitized within MaxFlavorChars
            string longText = new string('A', 250);
            string parsedLong = BountyFlavorService.ParseFlavor("{\"flavor\":\"" + longText + "\"}");
            Assert.LessOrEqual(parsedLong.Length, BountyFlavorService.MaxFlavorChars);
        }

        [Test]
        public void SaveLoadRoundTrip_PreservesRankActiveProgressAndCachedFlavor()
        {
            var guild = new GuildState(initialDay: 2);
            var inv = new InventoryState(seedStarterKit: true);
            var social = new SocialState();
            inv.AddItem(ItemCatalog.HerbMoonmint, 2);

            guild.TryAccept(GuildCatalog.BountyMoonmintSupplies, 2, out _);
            guild.TryTurnIn(GuildCatalog.BountyMoonmintSupplies, inv, social, out _);
            guild.TryAccept(GuildCatalog.BountyHomesteadHarvest, 2, out _);
            guild.RecordProgress(BountyObjectiveKind.HarvestCrops, null, 1);
            guild.SetCachedFlavor(2, GuildCatalog.BountyHomesteadHarvest, "Aldric wants the west rows logged before rain.");

            GuildSaveData saved = guild.ToSaveData(123456789L);
            var restored = new GuildState(initialDay: 1);
            restored.LoadFrom(saved);

            Assert.AreEqual(guild.GuildXp, restored.GuildXp);
            Assert.AreEqual(guild.CompletedCount, restored.CompletedCount);
            Assert.AreEqual(2, restored.BoardDay);
            Assert.IsTrue(restored.IsCompletedToday(GuildCatalog.BountyMoonmintSupplies));
            Assert.IsTrue(restored.IsActive(GuildCatalog.BountyHomesteadHarvest));
            Assert.AreEqual(1, restored.FindActive(GuildCatalog.BountyHomesteadHarvest).progress);
            Assert.AreEqual("Aldric wants the west rows logged before rain.",
                restored.GetDisplayDescription(GuildCatalog.GetBounty(GuildCatalog.BountyHomesteadHarvest), 2));
        }

        [Test]
        public void FlavorCache_DropsPreviousDays_SoSavePayloadStaysBounded()
        {
            var guild = new GuildState(initialDay: 1);
            guild.SetCachedFlavor(1, GuildCatalog.BountyHomesteadHarvest, "Day one notice for the west rows.");
            guild.SetCachedFlavor(1, GuildCatalog.BountyMoonmintSupplies, "Day one notice for moonmint stock.");
            Assert.AreEqual(2, guild.CachedFlavorCount);

            guild.RefreshBoardForDay(2);
            Assert.AreEqual(0, guild.CachedFlavorCount);
            Assert.AreEqual(GuildCatalog.GetBounty(GuildCatalog.BountyHomesteadHarvest).StaticDescription,
                guild.GetDisplayDescription(GuildCatalog.GetBounty(GuildCatalog.BountyHomesteadHarvest), 2));

            guild.SetCachedFlavor(2, GuildCatalog.BountyHomesteadHarvest, "Day two notice for the west rows.");
            GuildSaveData saved = guild.ToSaveData(1L);
            saved.flavorCache.Add(new CachedFlavorRecord { key = "1:stale", text = "An old notice nobody reads." });

            var restored = new GuildState(initialDay: 2);
            restored.LoadFrom(saved, 2);
            Assert.AreEqual(1, restored.CachedFlavorCount);
            Assert.IsTrue(restored.TryGetCachedFlavor(2, GuildCatalog.BountyHomesteadHarvest, out _));
        }
    }
}
