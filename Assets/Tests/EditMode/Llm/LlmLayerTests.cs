using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProjectFrontier.Social;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectFrontier.Llm.Tests
{
    public class TurnResponseParserTests
    {
        [Test]
        public void ParsesCompactJson()
        {
            var r = TurnResponseParser.Parse(
                "{\"dialogue\":\"Keep your distance.\",\"mood\":\"annoyed\",\"deltas\":{\"trust\":-3,\"affection\":-2,\"respect\":-1}," +
                "\"quick_replies\":[\"Sorry, Bram.\",\"Just testing.\",\"Fine.\"]}");
            Assert.IsTrue(r.ParsedJson);
            Assert.AreEqual("Keep your distance.", r.Dialogue);
            Assert.AreEqual("annoyed", r.Mood);
            Assert.AreEqual(new AxisDeltas(-3, -2, -1), r.Deltas);
            CollectionAssert.AreEqual(new[] { "Sorry, Bram.", "Just testing.", "Fine." }, r.QuickReplies);
        }

        [Test]
        public void AcceptsCodeFencesAndSurroundingProse()
        {
            var r = TurnResponseParser.Parse("Sure! Here you go:\n```json\n{\"dialogue\":\"Hello there.\",\"mood\":\"happy\"}\n```");
            Assert.IsTrue(r.ParsedJson);
            Assert.AreEqual("Hello there.", r.Dialogue);
            Assert.AreEqual("happy", r.Mood);
            Assert.IsTrue(r.Deltas.IsZero);
        }

        [Test]
        public void IgnoresFieldsOutsideTheContract()
        {
            var r = TurnResponseParser.Parse(
                "{\"dialogue\":\"Take it.\",\"gold\":9999,\"items\":[\"sword\"],\"quest_flags\":{\"done\":true},\"deltas\":{\"trust\":2,\"gold\":500}}");
            Assert.IsTrue(r.ParsedJson);
            Assert.AreEqual(new AxisDeltas(2, 0, 0), r.Deltas);
            var fields = typeof(LlmTurnResult).GetFields().Select(f => f.Name.ToLowerInvariant()).ToArray();
            Assert.IsFalse(fields.Any(f => f.Contains("gold") || f.Contains("item") || f.Contains("quest")));
        }

        [Test]
        public void PlainTextFallsBackWithZeroDeltas()
        {
            var r = TurnResponseParser.Parse("  \"Well met, traveler.  Mind the forge.\"  ");
            Assert.IsFalse(r.ParsedJson);
            Assert.AreEqual("Well met, traveler. Mind the forge.", r.Dialogue);
            Assert.AreEqual(LlmMoods.Neutral, r.Mood);
            Assert.IsTrue(r.Deltas.IsZero);
            CollectionAssert.AreEqual(TurnResponseParser.DefaultQuickReplies, r.QuickReplies);
        }

        [Test]
        public void SalvagesDialogueFromTruncatedJson()
        {
            // max_tokens can cut the reply mid-object.
            var r = TurnResponseParser.Parse("{\"dialogue\":\"He said \\\"no\\\" twice.\",\"mood\":\"sad\",\"deltas\":{\"trust\":-2,\"affe");
            Assert.IsFalse(r.ParsedJson);
            Assert.AreEqual("He said \"no\" twice.", r.Dialogue);
            Assert.IsTrue(r.Deltas.IsZero, "a broken reply must not move relationships");
        }

        [Test]
        public void EmptyOrUnsalvageableGivesSilentLine()
        {
            Assert.AreEqual(TurnResponseParser.SilentLine, TurnResponseParser.Parse(null).Dialogue);
            Assert.AreEqual(TurnResponseParser.SilentLine, TurnResponseParser.Parse("   ").Dialogue);
            Assert.AreEqual(TurnResponseParser.SilentLine, TurnResponseParser.Parse("{\"mood\":\"happy\",\"dia").Dialogue);
        }

        [Test]
        public void NormalizesUnknownMood()
        {
            Assert.AreEqual("neutral", TurnResponseParser.Parse("{\"dialogue\":\"Hi.\",\"mood\":\"ecstatic\"}").Mood);
            Assert.AreEqual("happy", TurnResponseParser.Parse("{\"dialogue\":\"Hi.\",\"mood\":\" HAPPY \"}").Mood);
        }

        [Test]
        public void CleansQuickReplies()
        {
            string longReply = new string('a', 100);
            var r = TurnResponseParser.Parse(
                "{\"dialogue\":\"Hi.\",\"quick_replies\":[\"  One \",\"\",\"one\",\"" + longReply + "\",\"Three\",\"Four\"]}");
            Assert.AreEqual(3, r.QuickReplies.Length);
            Assert.AreEqual("One", r.QuickReplies[0]);
            Assert.LessOrEqual(r.QuickReplies[1].Length, TurnResponseParser.MaxQuickReplyChars + 3);
            Assert.AreEqual("Three", r.QuickReplies[2]);
        }

        [Test]
        public void CapsLongDialogue()
        {
            string longLine = string.Join(" ", Enumerable.Repeat("The forge burns hot today.", 30));
            var r = TurnResponseParser.Parse("{\"dialogue\":\"" + longLine + "\"}");
            Assert.LessOrEqual(r.Dialogue.Length, TurnResponseParser.MaxDialogueChars + 3);
            StringAssert.EndsWith(".", r.Dialogue);
        }
    }

    public class TurnPromptBuilderTests
    {
        private const string Persona =
            "You are Bram, Blacksmith of Oakhaven.\nPersonality: Gruff, proud of his craft, secretly soft on kids.\n" +
            "Speech: Short, blunt sentences; forge metaphors.\nHooks: Lost his apprentice to the mines; rivalry with Aldric\n";

        private static List<ChatMessage> History(int pairs, int wordsPerLine = 12)
        {
            var list = new List<ChatMessage>();
            string line = string.Join(" ", Enumerable.Repeat("word", wordsPerLine));
            for (int i = 0; i < pairs; i++)
            {
                list.Add(new ChatMessage(ChatRole.User, $"p{i} {line}"));
                list.Add(new ChatMessage(ChatRole.Assistant, $"n{i} {line}"));
            }
            return list;
        }

        private static TurnPromptBuilder.Context Ctx(List<ChatMessage> history, string rumor = null) => new TurnPromptBuilder.Context
        {
            NpcId = "bram",
            NpcDisplayName = "Bram",
            PersonaCard = Persona,
            StandingLine = "Standing: Friend (trust 40, affection 28, respect 5)",
            RumorLine = rumor,
            Day = 3,
            History = history,
            PlayerMessage = "  Nice   hammer.\nCan I see it? "
        };

        [Test]
        public void LayoutIsSystemHistoryThenPlayer()
        {
            var req = new TurnPromptBuilder().Build(Ctx(History(1), "RUMOR: Mira says the player stole apples (you doubt it)."));
            Assert.AreEqual(ChatRole.System, req.Messages[0].Role);
            StringAssert.Contains("You are Bram", req.Messages[0].Content);
            StringAssert.Contains("Standing: Friend", req.Messages[0].Content);
            StringAssert.Contains("RUMOR: Mira", req.Messages[0].Content);
            StringAssert.Contains("Day 3", req.Messages[0].Content);
            StringAssert.Contains("quick_replies", req.Messages[0].Content);
            Assert.AreEqual(ChatRole.User, req.Messages[req.Messages.Count - 1].Role);
            Assert.AreEqual("Nice hammer. Can I see it?", req.PlayerMessage);
            Assert.AreEqual(130, req.MaxTokens);
        }

        [Test]
        public void KeepsAtMostFourRecentHistoryMessages()
        {
            var req = new TurnPromptBuilder().Build(Ctx(History(5, 3)));
            var hist = req.Messages.Skip(1).Take(req.Messages.Count - 2).ToList();
            Assert.AreEqual(4, hist.Count);
            StringAssert.StartsWith("p3", hist[0].Content);
            StringAssert.StartsWith("n4", hist[3].Content);
        }

        [Test]
        public void TypicalTurnStaysWithinInputBudget()
        {
            var req = new TurnPromptBuilder().Build(Ctx(History(4), "RUMOR: Mira says the player stole apples (you doubt it)."));
            int estimate = req.Messages.Sum(TokenEstimator.Estimate);
            Assert.AreEqual(estimate, req.EstimatedInputTokens);
            Assert.LessOrEqual(req.EstimatedInputTokens, TurnPromptBuilder.DefaultInputBudget);
            Assert.GreaterOrEqual(req.Messages.Count, 4, "budget should still leave room for some history");
        }

        [Test]
        public void DropsOldestHistoryWhenOverBudget()
        {
            var builder = new TurnPromptBuilder { InputTokenBudget = 330 };
            var req = builder.Build(Ctx(History(4, 40)));
            Assert.LessOrEqual(req.EstimatedInputTokens, 330);
            Assert.Less(req.Messages.Count, 6);
            Assert.AreEqual(ChatRole.User, req.Messages[req.Messages.Count - 1].Role);
            if (req.Messages.Count > 2) Assert.AreEqual(ChatRole.User, req.Messages[1].Role, "window must not start on an NPC line");
        }

        [Test]
        public void CapsPlayerMessage()
        {
            Assert.AreEqual(TurnPromptBuilder.MaxPlayerMessageChars, TurnPromptBuilder.SanitizePlayerMessage(new string('x', 1000)).Length);
            Assert.AreEqual("...", TurnPromptBuilder.SanitizePlayerMessage("   "));
        }

        [Test]
        public void RumorLinePicksStrongestLiveRumorForHolder()
        {
            var rumors = new List<Rumor>
            {
                new Rumor { holderId = "bram", sourceId = "mira", tag = "stole_apples", valence = -0.3f, strength = 0.9f, expiresDay = 5 },
                new Rumor { holderId = "bram", sourceId = "pip", tag = "saved_a_cat", valence = 0.9f, strength = 0.5f, expiresDay = 5 },
                new Rumor { holderId = "bram", sourceId = "lyra", tag = "old_news", valence = -1f, strength = 1f, expiresDay = 1 },
                new Rumor { holderId = "mira", sourceId = "aldric", tag = "other", valence = -1f, strength = 1f, expiresDay = 9 }
            };
            string line = TurnPromptBuilder.BuildRumorLine(rumors, "bram", 3, id => id == "pip" ? "Pip" : id);
            Assert.AreEqual("RUMOR: Pip says the player saved a cat (you half-believe it).", line);
            Assert.IsNull(TurnPromptBuilder.BuildRumorLine(rumors, "lyra", 3, null));
        }
    }

    public class MockProviderTests
    {
        [Test]
        public void IsDeterministicAndParsesAsJson()
        {
            string a = MockLlmProvider.Respond("Tell me about your craft");
            Assert.AreEqual(a, MockLlmProvider.Respond("Tell me about your craft"));
            var r = TurnResponseParser.Parse(a);
            Assert.IsTrue(r.ParsedJson);
            Assert.AreEqual(3, r.QuickReplies.Length);
        }

        [Test]
        public void SentimentDrivesDeltas()
        {
            var nice = TurnResponseParser.Parse(MockLlmProvider.Respond("Thank you, that's kind"));
            var rude = TurnResponseParser.Parse(MockLlmProvider.Respond("Your ironwork is a joke, idiot"));
            Assert.Greater(nice.Deltas.trust, 0);
            Assert.AreEqual("happy", nice.Mood);
            Assert.Less(rude.Deltas.trust, 0);
            Assert.Less(rude.Deltas.affection, 0);
            Assert.AreEqual("angry", rude.Mood);
            foreach (var d in new[] { nice.Deltas, rude.Deltas })
                Assert.IsTrue(Math.Abs(d.trust) <= 5 && Math.Abs(d.affection) <= 5 && Math.Abs(d.respect) <= 5);
        }
    }

    public class LlmTurnServiceTests
    {
        private sealed class FakeProvider : ILLMProvider
        {
            public Func<LlmRequest, CancellationToken, Task<LlmCompletion>> Impl;
            public int Calls;
            public bool SawCancel;
            public string Name => "fake";
            public bool RequiresNetwork => true;

            public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
            {
                Calls++;
                ct.Register(() => SawCancel = true);
                return Impl(request, ct);
            }
        }

        private static LlmRequest Request(string text = "hello")
        {
            var req = new LlmRequest { NpcId = "bram", EstimatedInputTokens = 321 };
            req.Messages.Add(new ChatMessage(ChatRole.System, "sys"));
            req.Messages.Add(new ChatMessage(ChatRole.User, text));
            return req;
        }

        private static IEnumerator Await<T>(Task<T> task, Action<T> done)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("task did not complete");
                yield return null;
            }
            done(task.GetAwaiter().GetResult());
        }

        [UnityTest]
        public IEnumerator UsesPrimaryAndReportedUsage()
        {
            var logs = new List<string>();
            var fake = new FakeProvider
            {
                Impl = (r, ct) => Task.FromResult(new LlmCompletion
                    { Text = "{\"dialogue\":\"Aye.\",\"mood\":\"proud\",\"deltas\":{\"respect\":2}}", PromptTokens = 480, CompletionTokens = 40 })
            };
            var svc = new LlmTurnService(fake, new MockLlmProvider(), 5f, () => true, logs.Add);
            LlmTurnResult res = null;
            yield return Await(svc.RunTurnAsync(Request()), r => res = r);

            Assert.AreEqual("Aye.", res.Dialogue);
            Assert.AreEqual("fake", res.ProviderName);
            Assert.IsFalse(res.UsedFallbackProvider);
            Assert.AreEqual(480, res.PromptTokens);
            Assert.AreEqual(40, res.CompletionTokens);
            Assert.IsFalse(res.UsageEstimated);
            Assert.AreEqual(1, svc.Usage.Turns);
            Assert.AreEqual(480f, svc.Usage.AvgPromptTokens);
            Assert.AreEqual(1, logs.Count);
            StringAssert.Contains("in=480 out=40", logs[0]);
        }

        [UnityTest]
        public IEnumerator FallsBackOnProviderError()
        {
            var fake = new FakeProvider { Impl = (r, ct) => Task.FromException<LlmCompletion>(new LlmProviderException("HTTP 401 Unauthorized")) };
            var svc = new LlmTurnService(fake, new MockLlmProvider(), 5f, () => true, _ => { });
            LlmTurnResult res = null;
            yield return Await(svc.RunTurnAsync(Request("thank you")), r => res = r);

            Assert.IsTrue(res.UsedFallbackProvider);
            Assert.AreEqual("offline", res.ProviderName);
            StringAssert.Contains("401", res.FallbackReason);
            Assert.IsTrue(res.ParsedJson);
            Assert.IsTrue(res.UsageEstimated);
            Assert.AreEqual(321, res.PromptTokens);
            Assert.Greater(res.CompletionTokens, 0);
        }

        [UnityTest]
        public IEnumerator TimesOutAndCancelsSlowProvider()
        {
            var fake = new FakeProvider { Impl = (r, ct) => new TaskCompletionSource<LlmCompletion>().Task }; // never completes
            var svc = new LlmTurnService(fake, new MockLlmProvider(), 0.3f, () => true, _ => { });
            float start = Time.realtimeSinceStartup;
            LlmTurnResult res = null;
            yield return Await(svc.RunTurnAsync(Request()), r => res = r);

            Assert.Less(Time.realtimeSinceStartup - start, 3f);
            Assert.IsTrue(res.UsedFallbackProvider);
            StringAssert.StartsWith("timeout", res.FallbackReason);
            Assert.IsTrue(fake.SawCancel, "the timed-out request must be cancelled");
            Assert.IsFalse(string.IsNullOrEmpty(res.Dialogue));
        }

        [UnityTest]
        public IEnumerator SkipsNetworkProviderWhenOffline()
        {
            var fake = new FakeProvider { Impl = (r, ct) => Task.FromResult(new LlmCompletion { Text = "{\"dialogue\":\"x\"}" }) };
            var svc = new LlmTurnService(fake, new MockLlmProvider(), 5f, () => false, _ => { });
            LlmTurnResult res = null;
            yield return Await(svc.RunTurnAsync(Request()), r => res = r);

            Assert.AreEqual(0, fake.Calls);
            Assert.AreEqual("offline", res.FallbackReason);
            Assert.AreEqual("offline", res.ProviderName);
        }

        [UnityTest]
        public IEnumerator PlainTextFromPrimaryStillPlays()
        {
            var fake = new FakeProvider { Impl = (r, ct) => Task.FromResult(new LlmCompletion { Text = "Hmph. Fine weather.", PromptTokens = 400, CompletionTokens = 6 }) };
            var svc = new LlmTurnService(fake, new MockLlmProvider(), 5f, () => true, _ => { });
            LlmTurnResult res = null;
            yield return Await(svc.RunTurnAsync(Request()), r => res = r);

            Assert.IsFalse(res.UsedFallbackProvider);
            Assert.IsFalse(res.ParsedJson);
            Assert.AreEqual("Hmph. Fine weather.", res.Dialogue);
            Assert.AreEqual(1, svc.Usage.NonJsonTurns);
        }
    }

    public class EndpointConfigTests
    {
        [Test]
        public void BuildsChatCompletionsUrl()
        {
            Assert.AreEqual("http://localhost:8080/v1/chat/completions", new LlmEndpointConfig { baseUrl = "http://localhost:8080/v1/" }.ChatCompletionsUrl);
            Assert.AreEqual("https://x.ai/v1/chat/completions", new LlmEndpointConfig { baseUrl = "https://x.ai/v1/chat/completions" }.ChatCompletionsUrl);
            Assert.IsNull(new LlmEndpointConfig().ChatCompletionsUrl);
        }

        [Test]
        public void DefaultsToOfflineAndNeverLogsKey()
        {
            Assert.AreEqual(LlmMode.Offline, new LlmEndpointConfig().Mode);
            Assert.AreEqual(LlmMode.Offline, new LlmEndpointConfig { mode = "nonsense" }.Mode);
            var cfg = new LlmEndpointConfig { mode = "directdev", apiKey = "sk-secret-123" };
            Assert.AreEqual(LlmMode.DirectDev, cfg.Mode);
            StringAssert.DoesNotContain("sk-secret", cfg.ToString());
        }

        [Test]
        public void ParsesDotEnv()
        {
            var env = LlmEndpointConfig.ParseDotEnv(
                "# comment\n\nFRONTIER_LLM_MODE=DirectDev\r\nexport FRONTIER_LLM_MODEL=\"gpt-x\"\n" +
                "FRONTIER_LLM_BASE_URL=https://api.example.com/v1  # trailing note\nFRONTIER_LLM_API_KEY=\nBROKEN LINE\n=nokey\n");
            Assert.AreEqual("DirectDev", env["FRONTIER_LLM_MODE"]);
            Assert.AreEqual("gpt-x", env["FRONTIER_LLM_MODEL"]);
            Assert.AreEqual("https://api.example.com/v1", env["FRONTIER_LLM_BASE_URL"]);
            Assert.AreEqual("", env["FRONTIER_LLM_API_KEY"]);
            Assert.AreEqual(4, env.Count);
        }

        [Test]
        public void DotEnvOverridesJsonAndBlankPlaceholderIsIgnored()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frontier_llm_cfg_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string json = System.IO.Path.Combine(dir, "cfg.json");
                string dotEnv = System.IO.Path.Combine(dir, ".env");
                System.IO.File.WriteAllText(json, "{\"mode\":\"Offline\",\"model\":\"from-json\",\"apiKey\":\"json-key\",\"timeoutSeconds\":5}");
                System.IO.File.WriteAllText(dotEnv, "FRONTIER_LLM_MODE=DirectDev\nFRONTIER_LLM_MODEL=from-env-file\nFRONTIER_LLM_API_KEY=\n");
                var cfg = LlmEndpointConfig.Load(json, dotEnv);
                if (Environment.GetEnvironmentVariable(LlmEndpointConfig.EnvMode) == null) Assert.AreEqual(LlmMode.DirectDev, cfg.Mode);
                if (Environment.GetEnvironmentVariable(LlmEndpointConfig.EnvModel) == null) Assert.AreEqual("from-env-file", cfg.model);
                if (Environment.GetEnvironmentVariable(LlmEndpointConfig.EnvApiKey) == null) Assert.AreEqual("json-key", cfg.apiKey);
                Assert.AreEqual(5f, cfg.timeoutSeconds);
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        [Test]
        public void DetectsLocalEndpoints()
        {
            Assert.IsTrue(new LlmEndpointConfig { baseUrl = "http://127.0.0.1:8080/v1" }.IsLocalEndpoint);
            Assert.IsTrue(new LlmEndpointConfig { baseUrl = "http://localhost:11434/v1" }.IsLocalEndpoint);
            Assert.IsFalse(new LlmEndpointConfig { baseUrl = "https://api.openai.com/v1" }.IsLocalEndpoint);
        }

        [Test]
        public void RequestBodyMatchesOpenAiShape()
        {
            var cfg = new LlmEndpointConfig { model = "m1", jsonMode = true, baseUrl = "http://x/v1" };
            var req = new LlmRequest();
            req.Messages.Add(new ChatMessage(ChatRole.System, "sys \"quoted\""));
            req.Messages.Add(new ChatMessage(ChatRole.User, "hi"));
            string body = new OpenAiCompatibleProvider(cfg, null).BuildBody(req);
            StringAssert.Contains("\"model\":\"m1\"", body);
            StringAssert.Contains("\"max_tokens\":130", body);
            StringAssert.Contains("{\"role\":\"system\",\"content\":\"sys \\\"quoted\\\"\"}", body);
            StringAssert.Contains("\"response_format\":{\"type\":\"json_object\"}", body);

            cfg.jsonMode = false;
            StringAssert.DoesNotContain("response_format", new OpenAiCompatibleProvider(cfg, null).BuildBody(req));
        }
    }
}
