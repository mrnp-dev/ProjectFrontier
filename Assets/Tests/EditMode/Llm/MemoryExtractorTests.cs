using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProjectFrontier.Social;
using UnityEngine.TestTools;

namespace ProjectFrontier.Llm.Tests
{
    /// <summary>#42: after-conversation memory extraction, memory prompt block, feature usage and the TPD cap tag.</summary>
    public class MemoryExtractorTests
    {
        private sealed class StubProvider : ILLMProvider
        {
            public Func<LlmRequest, Task<LlmCompletion>> Impl;
            public LlmRequest Last;
            public int Calls;
            public string Name => "stub";
            public bool RequiresNetwork => true;
            public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct) { Calls++; Last = request; return Impl(request); }
        }

        private static IEnumerator Await<T>(Task<T> task, Action<T> done)
        {
            while (!task.IsCompleted) yield return null;
            done(task.GetAwaiter().GetResult());
        }

        private static List<ChatMessage> Chat() => new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "My favourite crop is pumpkins, I grow them back home."),
            new ChatMessage(ChatRole.Assistant, "Pumpkins, love? I'll bake you a pie with them."),
            new ChatMessage(ChatRole.User, "I promise to bring you some next week."),
            new ChatMessage(ChatRole.Assistant, "I'll hold you to that.")
        };

        [Test]
        public void RequestIsBoundedAndSkipsChatsWithNothingSaid()
        {
            var req = MemoryExtractor.BuildRequest("mira", "Mira", Chat());
            Assert.IsNotNull(req);
            Assert.AreEqual(2, req.Messages.Count);
            Assert.AreEqual(MemoryExtractor.MaxOutputTokens, req.MaxTokens);
            StringAssert.Contains("Player: My favourite crop is pumpkins", req.Messages[1].Content);
            StringAssert.Contains("Mira: I'll hold you to that.", req.Messages[1].Content);

            var longChat = new List<ChatMessage>();
            for (int i = 0; i < 60; i++) longChat.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"line {i} " + new string('z', 60)));
            var big = MemoryExtractor.BuildRequest("mira", "Mira", longChat);
            Assert.LessOrEqual(big.Messages[1].Content.Length, MemoryExtractor.MaxTranscriptChars + 64);
            StringAssert.Contains("line 59", big.Messages[1].Content, "keeps the newest lines");
            Assert.LessOrEqual(big.EstimatedInputTokens, 520);

            var hello = new List<ChatMessage> { new ChatMessage(ChatRole.User, "Hi!"), new ChatMessage(ChatRole.Assistant, "Hello.") };
            Assert.IsNull(MemoryExtractor.BuildRequest("mira", "Mira", hello));
            Assert.IsNull(MemoryExtractor.BuildRequest("mira", "Mira", null));
        }

        [Test]
        public void ParseValidatesClampsAndCapsAtThree()
        {
            string raw = "Here: {\"facts\":[" +
                         "{\"text\":\"The player's favourite crop is pumpkins.\",\"about\":[\"Pumpkins\"],\"key\":\"Favorite Crop\",\"importance\":2}," +
                         "{\"text\":\"The player promised to bring Mira pumpkins.\",\"about\":[\"mira\"],\"importance\":9}," +
                         "{\"text\":\"\",\"importance\":2}," +
                         "{\"text\":\"The player grows crops back home.\",\"importance\":0}," +
                         "{\"text\":\"The player has a fourth fact that must be dropped.\",\"importance\":1}]}";
            var facts = MemoryExtractor.Parse(raw, "mira", 2, "happy");
            Assert.AreEqual(3, facts.Count);
            Assert.AreEqual("favorite_crop", facts[0].key);
            CollectionAssert.AreEqual(new[] { "player", "Pumpkins" }, facts[0].about);
            Assert.AreEqual(3, facts[1].importance, "clamped");
            Assert.AreEqual(1, facts[2].importance, "missing importance defaults to 1");
            foreach (var f in facts)
            {
                Assert.AreEqual("mira", f.holderId);
                Assert.AreEqual(MemorySource.PlayerChat, f.Source);
                Assert.AreEqual(2, f.day);
                Assert.AreEqual("happy", f.mood);
            }
        }

        [Test]
        public void BadOrEmptyJsonMeansRememberNothing()
        {
            Assert.AreEqual(0, MemoryExtractor.Parse("sure! the player likes pumpkins", "mira", 1).Count);
            Assert.AreEqual(0, MemoryExtractor.Parse("{\"facts\":[]}", "mira", 1).Count);
            Assert.AreEqual(0, MemoryExtractor.Parse("{\"facts\":\"oops\"", "mira", 1).Count);
            Assert.AreEqual(0, MemoryExtractor.Parse(null, "mira", 1).Count);
        }

        [UnityTest]
        public IEnumerator RunAsyncReturnsFactsAndReportedUsage()
        {
            var stub = new StubProvider
            {
                Impl = r => Task.FromResult(new LlmCompletion
                {
                    Text = "{\"facts\":[{\"text\":\"The player's favourite crop is pumpkins.\",\"about\":[\"pumpkins\"],\"key\":\"favorite_crop\",\"importance\":2}]}",
                    PromptTokens = 310, CompletionTokens = 45
                })
            };
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, MemoryExtractor.BuildRequest("mira", "Mira", Chat()), 1, 5f, CancellationToken.None), r => res = r);
            Assert.IsNull(res.Error);
            Assert.AreEqual(1, res.Facts.Count);
            Assert.AreEqual(310, res.PromptTokens);
            Assert.AreEqual(45, res.CompletionTokens);
            Assert.AreEqual(0.2f, stub.Last.Temperature, 1e-6f);
        }

        [UnityTest]
        public IEnumerator RunAsyncSwallowsProviderErrorsAndTagsTheDailyCap()
        {
            var stub = new StubProvider
            {
                Impl = r => Task.FromException<LlmCompletion>(new LlmProviderException(
                    "HTTP 429: Rate limit reached for model on tokens per day (TPD): Limit 200000, Used 199344"))
            };
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, MemoryExtractor.BuildRequest("mira", "Mira", Chat()), 1, 5f, CancellationToken.None), r => res = r);
            Assert.AreEqual(0, res.Facts.Count);
            Assert.IsTrue(LlmTurnService.IsTpdCap(res.Error), res.Error);
        }

        [UnityTest]
        public IEnumerator OfflineProviderIsNeverAskedToExtract()
        {
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(new MockLlmProvider(), MemoryExtractor.BuildRequest("mira", "Mira", Chat()), 1, 5f, CancellationToken.None), r => res = r);
            Assert.AreEqual(0, res.Facts.Count);
            Assert.IsFalse(res.CalledProvider);
            Assert.AreEqual("offline provider", res.Error);
        }

        [UnityTest]
        public IEnumerator ChatTurnFallbackReasonIsTaggedTpdCap()
        {
            var stub = new StubProvider
            {
                Impl = r => Task.FromException<LlmCompletion>(new LlmProviderException("HTTP 429 ... on tokens per day (TPD): Limit 200000"))
            };
            var svc = new LlmTurnService(stub, new MockLlmProvider(), 5f, () => true, _ => { });
            var req = new LlmRequest { NpcId = "mira" };
            req.Messages.Add(new ChatMessage(ChatRole.User, "Hello"));
            LlmTurnResult res = null;
            yield return Await(svc.RunTurnAsync(req), r => res = r);
            Assert.IsTrue(res.UsedFallbackProvider);
            StringAssert.StartsWith("tpd_cap: ", res.FallbackReason);
            Assert.AreEqual(1, svc.TpdCapTurns);
            Assert.AreEqual("HTTP 401 Unauthorized", LlmTurnService.ClassifyFailure("HTTP 401 Unauthorized"), "other errors unchanged");
        }

        [Test]
        public void FeatureUsageIsCountedSeparatelyFromChatTurns()
        {
            var u = new LlmUsageStats();
            u.RecordFeature(MemoryExtractor.FeatureTag, 300, 40, false);
            u.RecordFeature(MemoryExtractor.FeatureTag, 280, 0, true);
            Assert.AreEqual(0, u.Turns);
            var f = u.GetFeature(MemoryExtractor.FeatureTag);
            Assert.AreEqual(2, f.Calls);
            Assert.AreEqual(1, f.Failures);
            Assert.AreEqual(580, f.PromptTokens);
            Assert.AreEqual(40, f.CompletionTokens);
        }

        [Test]
        public void MemoryBlockLandsInTheSystemPromptWithinBudget()
        {
            var store = new NpcMemoryStore();
            store.Retain(new MemoryFact { holderId = "mira", text = "The player's favourite crop is pumpkins.", day = 1, importance = 2, about = new[] { "player", "pumpkins" } });
            store.Retain(new MemoryFact { holderId = "mira", text = "The player promised to bring Mira pumpkins next week.", day = 1, importance = 3 });
            string block = NpcMemoryStore.BuildPromptBlock(store.Recall("mira", "Do you remember my favourite crop?", 3), 3);

            var ctx = new TurnPromptBuilder.Context
            {
                NpcId = "mira", NpcDisplayName = "Mira", PersonaCard = "You are Mira, tavern keeper of Oakhaven.\n", Day = 3,
                MemoryBlock = block, PlayerMessage = "Do you remember my favourite crop?"
            };
            var withMemory = new TurnPromptBuilder().Build(ctx);
            ctx.MemoryBlock = null;
            var without = new TurnPromptBuilder().Build(ctx);

            string system = withMemory.Messages[0].Content;
            StringAssert.Contains("You remember (bring up only if it fits):", system);
            StringAssert.Contains("- 2 days ago: The player's favourite crop is pumpkins.", system);
            Assert.Less(system.IndexOf("You remember", StringComparison.Ordinal), system.IndexOf("Stay strictly", StringComparison.Ordinal));
            int extra = withMemory.EstimatedInputTokens - without.EstimatedInputTokens;
            Assert.Greater(extra, 0);
            Assert.LessOrEqual(extra, 90, "recalled memories stay within ~90 tokens");
        }
    }
}
