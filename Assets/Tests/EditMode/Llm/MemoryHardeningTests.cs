using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ProjectFrontier.Llm.Tests
{
    /// <summary>#45: memory hardening (dev-only logs, early failures, timeout tokens, long chats, offline queue).</summary>
    public class MemoryHardeningTests
    {
        private sealed class StubProvider : ILLMProvider
        {
            public Func<LlmRequest, CancellationToken, Task<LlmCompletion>> Impl;
            public int Calls;
            public string Name => "stub";
            public bool RequiresNetwork => true;
            public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct) { Calls++; return Impl(request, ct); }
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

        private static LlmRequest Request() => MemoryExtractor.BuildRequest("mira", "Mira", Chat());

        // ------------------------------------------------------------------ 1. dev-only logs

        [Test]
        public void MemoryTextLogIsCompiledOutOfReleaseBuilds()
        {
            MethodInfo m = typeof(MemoryLog).GetMethod(nameof(MemoryLog.Text));
            var symbols = new List<string>();
            foreach (ConditionalAttribute a in m.GetCustomAttributes(typeof(ConditionalAttribute), false)) symbols.Add(a.ConditionString);
            CollectionAssert.AreEquivalent(new[] { "UNITY_EDITOR", "DEVELOPMENT_BUILD" }, symbols,
                "player text must only reach Editor/development logs, like #41's chat transcript line");
        }

        // ------------------------------------------------------------------ 2. early failures are logged

        [UnityTest]
        public IEnumerator RunSafelyLogsAThrowBeforeTheFirstAwait()
        {
            var logs = new List<string>();
            MemoryExtractionResult res = new MemoryExtractionResult();
            yield return Await(MemoryExtractionQueue.RunSafelyAsync(() => throw new NullReferenceException("Service not set up"), logs.Add), r => res = r);
            Assert.IsNull(res);
            Assert.AreEqual(1, logs.Count);
            StringAssert.Contains("[Memory] extraction failed: NullReferenceException: Service not set up", logs[0]);
        }

        [UnityTest]
        public IEnumerator RunSafelyLogsAFaultedTaskAndPassesResultsAndCancelThrough()
        {
            var logs = new List<string>();
            MemoryExtractionResult res = new MemoryExtractionResult();
            yield return Await(MemoryExtractionQueue.RunSafelyAsync(
                () => Task.FromException<MemoryExtractionResult>(new InvalidOperationException("boom")), logs.Add), r => res = r);
            Assert.IsNull(res);
            Assert.AreEqual(1, logs.Count);

            var ok = new MemoryExtractionResult();
            yield return Await(MemoryExtractionQueue.RunSafelyAsync(() => Task.FromResult(ok), logs.Add), r => res = r);
            Assert.AreSame(ok, res);

            yield return Await(MemoryExtractionQueue.RunSafelyAsync(
                () => Task.FromCanceled<MemoryExtractionResult>(new CancellationToken(true)), logs.Add), r => res = r);
            Assert.IsNull(res);
            Assert.AreEqual(1, logs.Count, "success and cancel are not errors");
        }

        // ------------------------------------------------------------------ 3. timeout token accounting

        [UnityTest]
        public IEnumerator TimedOutExtractionCountsEstimatedTokens()
        {
            var stub = new StubProvider
            {
                Impl = (r, ct) => Task.Run(async () => { await Task.Delay(Timeout.Infinite, ct); return (LlmCompletion)null; })
            };
            LlmRequest req = Request();
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, req, 1, 0.5f, CancellationToken.None), r => res = r);
            Assert.IsTrue(res.TimedOut);
            Assert.IsFalse(res.NoConnection);
            Assert.IsTrue(res.CountsTowardUsage);
            Assert.IsTrue(res.UsageEstimated);
            Assert.AreEqual(req.EstimatedInputTokens, res.PromptTokens);
            Assert.Greater(res.PromptTokens, 0);
            Assert.AreEqual(MemoryExtractor.MaxOutputTokens, res.CompletionTokens, "worst case: the provider may finish and bill the reply");
            Assert.IsFalse(MemoryExtractionQueue.ShouldQueue(res), "a timeout may have cost tokens, so it is not retried");

            var usage = new LlmUsageStats();
            if (res.CountsTowardUsage) usage.RecordFeature(MemoryExtractor.FeatureTag, res.PromptTokens, res.CompletionTokens, res.Error != null);
            var f = usage.GetFeature(MemoryExtractor.FeatureTag);
            Assert.AreEqual(1, f.Calls);
            Assert.AreEqual(1, f.Failures);
            Assert.AreEqual(req.EstimatedInputTokens, f.PromptTokens);
            Assert.AreEqual(MemoryExtractor.MaxOutputTokens, f.CompletionTokens);
        }

        [UnityTest]
        public IEnumerator NoServerAnswerCountsNoTokensAndIsQueueable()
        {
            var stub = new StubProvider
            {
                Impl = (r, ct) => Task.FromException<LlmCompletion>(new LlmProviderException("Cannot resolve destination host", isTransportFailure: true))
            };
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, Request(), 1, 5f, CancellationToken.None), r => res = r);
            Assert.AreEqual(1, stub.Calls, "one call per conversation, no hidden retry");
            Assert.IsTrue(res.NoConnection);
            Assert.IsFalse(res.CountsTowardUsage);
            Assert.AreEqual(0, res.PromptTokens);
            Assert.AreEqual(0, res.CompletionTokens);
            Assert.IsTrue(MemoryExtractionQueue.ShouldQueue(res));
        }

        [UnityTest]
        public IEnumerator HttpErrorCountsTokensAndIsNotQueued()
        {
            var stub = new StubProvider { Impl = (r, ct) => Task.FromException<LlmCompletion>(new LlmProviderException("HTTP 500 Internal Server Error")) };
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, Request(), 1, 5f, CancellationToken.None), r => res = r);
            Assert.IsFalse(res.NoConnection);
            Assert.IsTrue(res.CountsTowardUsage);
            Assert.IsFalse(MemoryExtractionQueue.ShouldQueue(res));
        }

        [UnityTest]
        public IEnumerator NetworkDownSkipsTheProviderAndIsQueueable()
        {
            var stub = new StubProvider { Impl = (r, ct) => Task.FromResult(new LlmCompletion { Text = "{\"facts\":[]}" }) };
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, Request(), 1, 5f, CancellationToken.None, networkAllowed: false), r => res = r);
            Assert.AreEqual(0, stub.Calls);
            Assert.IsFalse(res.CalledProvider);
            Assert.IsTrue(res.NoConnection);
            Assert.AreEqual("no connection", res.Error);
            Assert.IsFalse(res.CountsTowardUsage);
            Assert.IsTrue(MemoryExtractionQueue.ShouldQueue(res));
        }

        [UnityTest]
        public IEnumerator SuccessfulExtractionStillCountsReportedUsage()
        {
            var stub = new StubProvider { Impl = (r, ct) => Task.FromResult(new LlmCompletion { Text = "{\"facts\":[]}", PromptTokens = 300, CompletionTokens = 12 }) };
            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, Request(), 1, 5f, CancellationToken.None), r => res = r);
            Assert.IsTrue(res.CountsTowardUsage);
            Assert.AreEqual(300, res.PromptTokens);
            Assert.AreEqual(12, res.CompletionTokens);
            Assert.IsFalse(MemoryExtractionQueue.ShouldQueue(res));
        }

        // ------------------------------------------------------------------ 4. long chats

        private static List<ChatMessage> LongChat(int turns, int playerPad = 40, int npcPad = 110)
        {
            var chat = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "My sister Lena runs the mill in Brookvale and she hates geese."),
                new ChatMessage(ChatRole.Assistant, "Geese? Oh, I'd hate them too, love. " + new string('a', npcPad))
            };
            for (int i = 1; i < turns; i++)
            {
                chat.Add(new ChatMessage(ChatRole.User, $"player line {i} " + new string('p', playerPad)));
                chat.Add(new ChatMessage(ChatRole.Assistant, $"npc line {i} " + new string('n', npcPad)));
            }
            return chat;
        }

        private static int Size(List<string> lines)
        {
            int n = 0;
            foreach (string l in lines) n += l.Length + 1;
            return n;
        }

        [Test]
        public void ShortChatIsSentWhole()
        {
            List<string> lines = MemoryExtractor.BuildTranscript(Chat(), "Mira", MemoryExtractor.MaxTranscriptChars);
            Assert.AreEqual(4, lines.Count);
            Assert.AreEqual("Player: My favourite crop is pumpkins, I grow them back home.", lines[0]);
            Assert.AreEqual("Mira: I'll hold you to that.", lines[3]);
        }

        [Test]
        public void LongChatKeepsEveryPlayerLineWithinTheCap()
        {
            // 12 exchanges: ~1,960 chars in full, the first player line is far outside the old "last 10 messages" window.
            List<ChatMessage> chat = LongChat(12);
            List<string> lines = MemoryExtractor.BuildTranscript(chat, "Mira", MemoryExtractor.MaxTranscriptChars);
            Assert.LessOrEqual(Size(lines), MemoryExtractor.MaxTranscriptChars);
            StringAssert.StartsWith("Player: My sister Lena runs the mill", lines[0]);
            for (int i = 1; i < 12; i++) Assert.IsTrue(lines.Exists(l => l.StartsWith($"Player: player line {i} ")), $"player line {i} kept");
            Assert.IsTrue(lines.Exists(l => l.StartsWith("Mira: npc line 11")), "newest villager line kept for context");
            foreach (string l in lines) if (l.StartsWith("Mira:")) Assert.LessOrEqual(l.Length, MemoryExtractor.NpcLineChars);

            LlmRequest req = MemoryExtractor.BuildRequest("mira", "Mira", chat);
            StringAssert.Contains("My sister Lena runs the mill", req.Messages[1].Content);
            Assert.AreEqual(MemoryExtractor.MaxOutputTokens, req.MaxTokens);
        }

        [Test]
        public void VeryLongChatKeepsTheOpeningAndNewestPlayerLines()
        {
            List<ChatMessage> chat = LongChat(40, playerPad: 150);
            List<string> lines = MemoryExtractor.BuildTranscript(chat, "Mira", MemoryExtractor.MaxTranscriptChars);
            Assert.LessOrEqual(Size(lines), MemoryExtractor.MaxTranscriptChars);
            StringAssert.StartsWith("Player: My sister Lena runs the mill", lines[0]);
            Assert.IsTrue(lines.Exists(l => l.StartsWith("Player: player line 39 ")), "newest player line kept");
            Assert.IsTrue(lines.Exists(l => l.StartsWith("Mira:")), "some villager context kept");
            foreach (string l in lines) if (l.StartsWith("Player:")) Assert.LessOrEqual(l.Length, MemoryExtractor.PlayerLineChars);

            int last = -1;
            foreach (string l in lines)
            {
                int idx = chat.FindIndex(m => (m.Role == ChatRole.User ? "Player: " : "Mira: ") + m.Content.Trim() == l
                                              || l.EndsWith("...") && ((m.Role == ChatRole.User ? "Player: " : "Mira: ") + m.Content).StartsWith(l.Substring(0, l.Length - 3)));
                Assert.Greater(idx, last, "lines stay in chat order");
                last = idx;
            }
        }

        // ------------------------------------------------------------------ 5. queue until the connection comes back

        private static PendingExtraction Item(string npc) =>
            new PendingExtraction { NpcId = npc, Conversation = Chat(), Day = 3 };

        private static MemoryExtractionResult NoConnection(bool calledProvider) =>
            new MemoryExtractionResult { NoConnection = true, CalledProvider = calledProvider, Error = "no connection" };

        [Test]
        public void QueueIsBoundedAndDropsTheOldest()
        {
            var q = new MemoryExtractionQueue();
            Assert.AreEqual(6, q.Capacity);
            for (int i = 0; i < 8; i++) Assert.IsTrue(q.Enqueue(Item("npc" + i)));
            Assert.AreEqual(6, q.Count);
            Assert.AreEqual(2, q.Dropped);
            Assert.AreEqual("npc2", q.TakeNext().NpcId);
            Assert.IsFalse(q.Enqueue(null));
            Assert.IsFalse(q.Enqueue(new PendingExtraction { NpcId = "x", Conversation = new List<ChatMessage>() }));
        }

        [Test]
        public void FailedRetryGoesBackToTheFrontAndFreshChatsToTheBack()
        {
            var q = new MemoryExtractionQueue();
            q.Enqueue(Item("mira"));
            q.Enqueue(Item("pip"));
            PendingExtraction first = q.TakeNext();
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Requeued, q.Complete(first, NoConnection(true)));
            Assert.AreEqual(1, first.Attempts);
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Requeued, q.Complete(Item("bram"), NoConnection(false), fromQueue: false));
            Assert.AreEqual("mira", q.TakeNext().NpcId);
            Assert.AreEqual("pip", q.TakeNext().NpcId);
            Assert.AreEqual("bram", q.TakeNext().NpcId);
            Assert.IsNull(q.TakeNext());
        }

        [Test]
        public void GivesUpAfterThreeNoConnectionAttempts()
        {
            var q = new MemoryExtractionQueue();
            PendingExtraction item = Item("mira");
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Requeued, q.Complete(item, NoConnection(false), false), "skipped call: no attempt used");
            Assert.AreEqual(0, item.Attempts);
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Requeued, q.Complete(q.TakeNext(), NoConnection(true)));
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Requeued, q.Complete(q.TakeNext(), NoConnection(true)));
            Assert.AreEqual(MemoryExtractionQueue.Outcome.GaveUp, q.Complete(q.TakeNext(), NoConnection(true)));
            Assert.AreEqual(3, item.Attempts);
            Assert.AreEqual(0, q.Count);
            Assert.AreEqual(1, q.GaveUp);
        }

        [Test]
        public void OnlyNoConnectionFailuresAreQueued()
        {
            var q = new MemoryExtractionQueue();
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Done, q.Complete(Item("a"), new MemoryExtractionResult { CalledProvider = true }, false), "success");
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Done, q.Complete(Item("b"), new MemoryExtractionResult { TimedOut = true, Error = "timeout" }, false), "timeout");
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Done, q.Complete(Item("c"), new MemoryExtractionResult { CalledProvider = true, Error = "HTTP 429" }, false), "server reached");
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Done, q.Complete(Item("d"), null, false));
            Assert.AreEqual(0, q.Count);
        }

        [UnityTest]
        public IEnumerator QueuedChatIsExtractedOnceTheConnectionIsBack()
        {
            bool online = false;
            var stub = new StubProvider
            {
                Impl = (r, ct) => Task.FromResult(new LlmCompletion
                {
                    Text = "{\"facts\":[{\"text\":\"The player's favourite crop is pumpkins.\",\"about\":[\"pumpkins\"],\"key\":\"favorite_crop\",\"importance\":2}]}",
                    PromptTokens = 300, CompletionTokens = 40
                })
            };
            var q = new MemoryExtractionQueue();
            PendingExtraction item = Item("mira");

            MemoryExtractionResult res = null;
            yield return Await(MemoryExtractor.RunAsync(stub, MemoryExtractor.BuildRequest(item.NpcId, "Mira", item.Conversation), item.Day, 5f,
                CancellationToken.None, networkAllowed: online), r => res = r);
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Requeued, q.Complete(item, res, false));
            Assert.AreEqual(0, stub.Calls);

            online = true;
            PendingExtraction next = q.TakeNext();
            yield return Await(MemoryExtractor.RunAsync(stub, MemoryExtractor.BuildRequest(next.NpcId, "Mira", next.Conversation), next.Day, 5f,
                CancellationToken.None, networkAllowed: online), r => res = r);
            Assert.AreEqual(1, stub.Calls, "exactly one call for the queued conversation");
            Assert.AreEqual(1, res.Facts.Count);
            Assert.AreEqual(3, res.Facts[0].day, "facts keep the day the chat happened");
            Assert.AreEqual(MemoryExtractionQueue.Outcome.Done, q.Complete(next, res));
            Assert.AreEqual(0, q.Count);
        }
    }
}
