using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier.Social.Tests
{
    /// <summary>#42: long-term NPC memory store, recall scoring, consolidation, gossip sharing and save round-trip.</summary>
    public class NpcMemoryTests
    {
        private static MemoryFact Fact(string holder, string text, int day, int importance = 2, string key = null,
            MemorySource source = MemorySource.PlayerChat, params string[] about) => new MemoryFact
        {
            holderId = holder, text = text, day = day, importance = importance, key = key, source = (int)source,
            fromId = holder, about = about.Length == 0 ? new[] { "player" } : about
        };

        [Test]
        public void RecallRanksKeywordMatchAboveRecencyAndReturnsAtMostThree()
        {
            var store = new NpcMemoryStore();
            store.Retain(Fact("mira", "The player's favourite crop is pumpkins.", 1, 2, "favorite_crop", MemorySource.PlayerChat, "player", "pumpkins"));
            store.Retain(Fact("mira", "The player wants a fishing rod.", 3));
            store.Retain(Fact("mira", "The player dislikes rain.", 3, 1));
            store.Retain(Fact("mira", "The player has a sister in the capital.", 2));
            store.Retain(Fact("bram", "The player loves pumpkins too.", 3));

            var top = store.Recall("mira", "Do you remember which crop I like?", 3);
            Assert.AreEqual(3, top.Count);
            Assert.AreEqual("The player's favourite crop is pumpkins.", top[0].text, "keyword hit beats newer facts");
            Assert.IsTrue(top.All(f => f.holderId == "mira"));

            var noQuery = store.Recall("mira", "Hello!", 3);
            Assert.AreEqual(3, noQuery.Count);
            Assert.AreEqual(3, noQuery[0].day, "without a keyword hit, newer/more important wins");
        }

        [Test]
        public void SameKeyNewestFactCorrectsTheOldOne()
        {
            var store = new NpcMemoryStore();
            store.Retain(Fact("mira", "The player's favourite crop is pumpkins.", 1, 2, "favorite_crop"));
            store.Retain(Fact("mira", "The player's favourite crop is now turnips.", 3, 2, "favorite_crop"));
            store.Retain(Fact("mira", "The player's favourite crop is beets.", 2, 2, "favorite_crop"));   // older than what we have
            var facts = store.For("mira");
            Assert.AreEqual(1, facts.Count);
            StringAssert.Contains("turnips", facts[0].text);
        }

        [Test]
        public void NearDuplicateTextReinforcesInsteadOfAdding()
        {
            var store = new NpcMemoryStore();
            store.Retain(Fact("pip", "The player promised to bring Pip moonmint.", 1, 2));
            store.Retain(Fact("pip", "The player promised to bring Pip some moonmint!", 2, 3));
            var facts = store.For("pip");
            Assert.AreEqual(1, facts.Count);
            Assert.AreEqual(2, facts[0].day);
            Assert.AreEqual(3, facts[0].importance);
        }

        [Test]
        public void CapDropsTheWeakestFactPerVillager()
        {
            var store = new NpcMemoryStore();
            store.Retain(Fact("bram", "The player promised Bram rare dungeon ore.", 1, 3));
            for (int i = 0; i < NpcMemoryStore.MaxFactsPerNpc + 5; i++)
                store.Retain(Fact("bram", $"Trivia number {i} zebra{i} quokka{i} wombat{i}.", 2 + i % 3, 1));
            store.Retain(Fact("mira", "The player tips well.", 1, 1));

            Assert.AreEqual(NpcMemoryStore.MaxFactsPerNpc, store.Count("bram"));
            Assert.IsTrue(store.For("bram").Any(f => f.importance == 3), "important promise survives the cap");
            Assert.AreEqual(1, store.Count("mira"), "cap is per villager");
        }

        [Test]
        public void ConsolidateFadesOldTriviaButKeepsImportantFacts()
        {
            var store = new NpcMemoryStore();
            store.Retain(Fact("aldric", "The player asked about the weather.", 1, 1));
            store.Retain(Fact("aldric", "The player swore an oath to the Guild.", 1, 3));
            store.Retain(Fact("aldric", "The player mentioned a limp.", 10, 1));

            int dropped = store.Consolidate(14);   // trivia lasts ~12 days
            Assert.AreEqual(1, dropped);
            var texts = store.For("aldric").Select(f => f.text).ToList();
            CollectionAssert.DoesNotContain(texts, "The player asked about the weather.");
            CollectionAssert.Contains(texts, "The player swore an oath to the Guild.");
            CollectionAssert.Contains(texts, "The player mentioned a limp.");
        }

        [Test]
        public void PromptBlockIsCappedAroundNinetyTokens()
        {
            var facts = new List<MemoryFact>();
            for (int i = 0; i < 3; i++)
                facts.Add(Fact("mira", $"Fact {i} " + string.Join(" ", Enumerable.Repeat("word" + i, 40)), 1, 2));
            var store = new NpcMemoryStore();
            foreach (var f in facts) store.Retain(f);
            string block = NpcMemoryStore.BuildPromptBlock(store.Recall("mira", "x", 3), 3);
            Assert.IsNotNull(block);
            Assert.LessOrEqual(block.Length, NpcMemoryStore.MaxPromptBlockChars);
            Assert.LessOrEqual((block.Length + 3) / 4, 90);
            StringAssert.Contains("2 days ago", block);
            Assert.IsNull(NpcMemoryStore.BuildPromptBlock(new List<MemoryFact>(), 1));
        }

        [Test]
        public void SaveRoundTripKeepsMemoriesThroughJsonUtility()
        {
            var state = new SocialState();
            state.Memory.Retain(Fact("mira", "The player's favourite crop is pumpkins.", 1, 2, "favorite_crop", MemorySource.PlayerChat, "player", "pumpkins"));
            state.Memory.Retain(Fact("bram", "Mira told me: The player's favourite crop is pumpkins.", 1, 1, null, MemorySource.Gossip));

            string json = JsonUtility.ToJson(state.ToSaveData(123));
            var loaded = new SocialState();
            loaded.LoadFrom(JsonUtility.FromJson<SocialSaveData>(json));

            Assert.AreEqual(2, loaded.Memory.All.Count);
            var mira = loaded.Memory.For("mira").Single();
            Assert.AreEqual("favorite_crop", mira.key);
            CollectionAssert.AreEqual(new[] { "player", "pumpkins" }, mira.about);
            Assert.AreEqual(MemorySource.Gossip, loaded.Memory.For("bram").Single().Source);
        }

        [Test]
        public void OldSaveWithoutMemoriesLoadsEmpty()
        {
            var loaded = new SocialState();
            loaded.LoadFrom(JsonUtility.FromJson<SocialSaveData>("{\"version\":1,\"day\":4,\"relationships\":[],\"rumors\":[]}"));
            Assert.AreEqual(4, loaded.Day);
            Assert.AreEqual(0, loaded.Memory.All.Count);
        }

        [Test]
        public void MemoriesSurviveDayAdvanceToDayThree()
        {
            var state = new SocialState();
            state.Memory.Retain(Fact("mira", "The player's favourite crop is pumpkins.", state.Day, 2, "favorite_crop"));
            state.AdvanceDay();
            state.AdvanceDay();
            Assert.AreEqual(3, state.Day);
            var top = state.Memory.Recall("mira", "What crop do I like?", state.Day);
            Assert.AreEqual(1, top.Count);
            StringAssert.Contains("2 days ago", NpcMemoryStore.BuildPromptBlock(top, state.Day));
        }

        [Test]
        public void GossipPassesWhatThePlayerToldMiraOnToBramAtTheTavern()
        {
            var state = new SocialState();
            var trust = GossipTrustMatrix.CreateDefault();
            state.Memory.Retain(Fact("mira", "The player's favourite crop is pumpkins.", 1, 2, "favorite_crop", MemorySource.PlayerChat, "player", "pumpkins"));
            state.Memory.Retain(Fact("mira", "The player sneezed.", 1, 1));   // trivia is not passed on

            var tavern = new List<IReadOnlyList<string>> { new List<string> { "bram", "mira", "aldric" } };
            var created = NpcMemoryGossip.Share(state.Memory, trust, tavern, null, 1, id => char.ToUpper(id[0]) + id.Substring(1));

            // Bram trusts Mira 0.8 and Aldric 0.7: both learn it; nobody learns the sneeze.
            Assert.AreEqual(2, created.Count);
            var bram = state.Memory.For("bram").Single();
            Assert.AreEqual("Mira told me: The player's favourite crop is pumpkins.", bram.text);
            Assert.AreEqual(MemorySource.Gossip, bram.Source);
            Assert.AreEqual("mira", bram.fromId);
            Assert.AreEqual(1, bram.importance);
            Assert.AreEqual(bram.text, state.Memory.Recall("bram", "Heard anything about my crops?", 3)[0].text);

            // Second evening: nothing new, and relayed facts are not relayed again (one hop).
            Assert.AreEqual(0, NpcMemoryGossip.Share(state.Memory, trust, tavern, null, 1, null).Count);
            var square = new List<IReadOnlyList<string>> { new List<string> { "bram", "lyra" } };
            Assert.AreEqual(0, NpcMemoryGossip.Share(state.Memory, trust, square, null, 1, null).Count);
        }

        [Test]
        public void LowTrustListenerDoesNotPickUpTheFact()
        {
            var state = new SocialState();
            var trust = GossipTrustMatrix.CreateDefault();   // Bram trusts Lyra 0.2
            state.Memory.Retain(Fact("lyra", "The player beat Lyra in an archery bet.", 1, 3));
            var group = new List<IReadOnlyList<string>> { new List<string> { "lyra", "bram" } };
            Assert.AreEqual(0, NpcMemoryGossip.Share(state.Memory, trust, group, null, 1, null).Count);
            Assert.AreEqual(0, state.Memory.Count("bram"));
        }

        [Test]
        public void RumorTransfersBecomeListenerMemories()
        {
            var state = new SocialState();
            var trust = GossipTrustMatrix.CreateDefault();
            GossipEngine.RecordChatOutcome(state, "bram", new AxisDeltas(3, 2, 3));
            var transfers = new List<GossipTransfer>();
            var tavern = new List<IReadOnlyList<string>> { new List<string> { "bram", "mira" } };
            GossipEngine.RunExchange(state, trust, tavern, transfers);
            Assert.AreEqual(1, transfers.Count);

            NpcMemoryGossip.Share(state.Memory, trust, tavern, transfers, state.Day, null);
            var mira = state.Memory.For("mira").Single();
            Assert.AreEqual("Bram told me the player praised his smithing.", mira.text);
            Assert.AreEqual("rumor:praised_bram_smithing", mira.key);
        }
    }
}
