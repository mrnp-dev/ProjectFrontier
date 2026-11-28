using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ProjectFrontier.Llm.Tests
{
    /// <summary>#41: grounded prompt, anti-repetition line, history window and repetition penalties.</summary>
    public class AntiRepetitionPromptTests
    {
        // Same shape and size as NpcPersona.ToPromptCard() for the shipped Bram asset.
        private const string BramCard =
            "You are Bram, Blacksmith of Oakhaven. Proud, blunt craftsman. Takes any slight to his work personally; fiercely loyal once you earn his respect.\n" +
            "Speech: Short, gruff sentences. Forge and iron metaphors. Never says please.\n" +
            "Knows: you forge and mend weapons and armor; fine blades need dungeon ore; likes Mira's mutton stew, a clean quench; hates bragging, flimsy steel\n" +
            "Thinks: Mira honest; Lyra reckless, chips every blade; Aldric solid, orders iron he can't pay for; Pip odd, but his salve heals forge burns\n" +
            "Story: Mocking your craft becomes a grudge you spread; Wants rare dungeon ore for a masterpiece blade\n";

        private static List<ChatMessage> Pairs(int pairs, int words = 6)
        {
            var list = new List<ChatMessage>();
            for (int i = 0; i < pairs; i++)
            {
                list.Add(new ChatMessage(ChatRole.User, $"question {i} " + string.Join(" ", Enumerable.Repeat("q", words))));
                list.Add(new ChatMessage(ChatRole.Assistant, $"answer{i} " + string.Join(" ", Enumerable.Repeat("a", words))));
            }
            return list;
        }

        private static TurnPromptBuilder.Context Ctx(IReadOnlyList<ChatMessage> history, string card = BramCard) => new TurnPromptBuilder.Context
        {
            NpcId = "bram",
            NpcDisplayName = "Bram",
            PersonaCard = card,
            NowLine = TurnPromptBuilder.BuildNowLine(2, "Evening", "Mira's Tavern", 3, 5),
            StandingLine = "Standing: Neutral (trust 0, affection 0, respect 0)",
            History = history,
            PlayerMessage = "What's your favorite food?"
        };

        [Test]
        public void NoHistoryMeansNoAvoidLine()
        {
            string sys = new TurnPromptBuilder().Build(Ctx(null)).Messages[0].Content;
            StringAssert.DoesNotContain(TurnPromptBuilder.AvoidHeader, sys);
            StringAssert.Contains("Never reuse wording from your earlier lines", sys);
        }

        [Test]
        public void LinesStillInTheWindowAreNotListedAsAvoid()
        {
            // 2 pairs = 4 messages: everything fits the window, so nothing is "older".
            string sys = new TurnPromptBuilder().Build(Ctx(Pairs(2))).Messages[0].Content;
            StringAssert.DoesNotContain(TurnPromptBuilder.AvoidHeader, sys);
        }

        [Test]
        public void OlderNpcLinesAreListedCutToTheirOpeningWords()
        {
            var req = new TurnPromptBuilder().Build(Ctx(Pairs(5)));
            string sys = req.Messages[0].Content;
            // Window = last 4 messages (pairs 3 and 4). Older NPC lines: answer0..answer2, newest last 3, oldest first.
            StringAssert.Contains(TurnPromptBuilder.AvoidHeader + "\"answer0 a a a a a...\" | \"answer1 a a a a a...\" | \"answer2 a a a a a...\"", sys);
            StringAssert.DoesNotContain("\"answer3", sys);
            var history = req.Messages.Skip(1).Take(req.Messages.Count - 2).ToList();
            Assert.AreEqual(4, history.Count);
            StringAssert.StartsWith("question 3", history[0].Content);
            StringAssert.StartsWith("answer4", history[3].Content);
        }

        [Test]
        public void AvoidListIsCappedAndDeduplicated()
        {
            var h = new List<ChatMessage>();
            for (int i = 0; i < 6; i++)
            {
                h.Add(new ChatMessage(ChatRole.User, "q" + i));
                h.Add(new ChatMessage(ChatRole.Assistant, i < 4 ? "Iron and fire wait for no one, recruit." : "fresh " + i));
            }
            List<string> avoid = TurnPromptBuilder.OlderNpcLines(h, TurnPromptBuilder.WindowStart(h, 4), 3);
            CollectionAssert.AreEqual(new[] { "Iron and fire wait for no..." }, avoid);
            Assert.AreEqual(0, TurnPromptBuilder.OlderNpcLines(h, 12, 0).Count);
            Assert.AreEqual(0, TurnPromptBuilder.OlderNpcLines(null, 0, 3).Count);
        }

        [Test]
        public void AvoidLinesCannotBreakOutOfTheirQuotes()
        {
            string sys = TurnPromptBuilder.BuildSystemPrompt(Ctx(null), new[] { "He said \"hi\"" });
            StringAssert.Contains("\"He said 'hi'\"", sys);
        }

        [Test]
        public void FirstWordsCutsAndMarksLongLines()
        {
            Assert.AreEqual("one two three", TurnPromptBuilder.FirstWords("  one   two three ", 6));
            Assert.AreEqual("a b...", TurnPromptBuilder.FirstWords("a b c", 2));
            Assert.AreEqual(string.Empty, TurnPromptBuilder.FirstWords(" ", 3));
        }

        [Test]
        public void SystemPromptHasCardTownNowAndRulesInOrder()
        {
            string sys = TurnPromptBuilder.BuildSystemPrompt(Ctx(null));
            int card = sys.IndexOf("You are Bram", System.StringComparison.Ordinal);
            int town = sys.IndexOf("Oakhaven: frontier village", System.StringComparison.Ordinal);
            int now = sys.IndexOf("Now: day 2, evening, at Mira's Tavern. Lanterns lit 3/5.", System.StringComparison.Ordinal);
            int rules = sys.IndexOf("Reply to what the player just said", System.StringComparison.Ordinal);
            Assert.IsTrue(card == 0 && town > card && now > town && rules > now, sys);
            StringAssert.DoesNotContain("Day 2.", sys);

            var noTown = Ctx(null);
            noTown.TownLine = "";
            noTown.NowLine = null;
            noTown.Day = 4;
            string bare = TurnPromptBuilder.BuildSystemPrompt(noTown);
            StringAssert.DoesNotContain("Oakhaven: frontier village", bare);
            StringAssert.Contains("Day 4.", bare);
        }

        [Test]
        public void NowLineFormatsAndClamps()
        {
            Assert.AreEqual("Now: day 1, morning, at the Forge. Lanterns lit 0/5.", TurnPromptBuilder.BuildNowLine(0, "Morning", "the Forge", -2, 5));
            Assert.AreEqual("Now: day 3, night. Lanterns lit 5/5.", TurnPromptBuilder.BuildNowLine(3, "Night", null, 9, 5));
            Assert.AreEqual("Now: day 2.", TurnPromptBuilder.BuildNowLine(2, null, "", 0, 0));
        }

        [Test]
        public void FullGroundedTurnStaysWithinBudgetAndKeepsFourHistoryMessages()
        {
            // Realistic worst-ish case: real card, rumor, 3 avoid lines, 4 history lines of ~25 words each.
            var ctx = Ctx(Pairs(7, 24));
            ctx.RumorLine = "RUMOR: Mira says the player mocked Bram's smithing (you mostly believe Mira).";
            var req = new TurnPromptBuilder().Build(ctx);
            Assert.LessOrEqual(req.EstimatedInputTokens, TurnPromptBuilder.DefaultInputBudget);
            Assert.AreEqual(req.Messages.Sum(TokenEstimator.Estimate), req.EstimatedInputTokens);
            Assert.AreEqual(6, req.Messages.Count, "system + 4 history + player");
            StringAssert.Contains(TurnPromptBuilder.AvoidHeader, req.Messages[0].Content);
        }

        [Test]
        public void PenaltiesAreSentWhenSetAndOmittedAtZero()
        {
            var cfg = new LlmEndpointConfig { model = "m", baseUrl = "http://x/v1" };
            var req = new LlmRequest();
            req.Messages.Add(new ChatMessage(ChatRole.User, "hi"));

            string body = new OpenAiCompatibleProvider(cfg, null).BuildBody(req);
            StringAssert.Contains(",\"frequency_penalty\":0.3,\"presence_penalty\":0.2}", body);
            StringAssert.Contains("\"temperature\":0.85", body);

            cfg.frequencyPenalty = 0f;
            cfg.presencePenalty = 0f;
            string none = new OpenAiCompatibleProvider(cfg, null).BuildBody(req);
            StringAssert.DoesNotContain("penalty", none);

            cfg.frequencyPenalty = 9f; // clamped to the OpenAI range
            cfg.reasoningEffort = "low";
            string both = new OpenAiCompatibleProvider(cfg, null).BuildBody(req);
            StringAssert.Contains("\"frequency_penalty\":2,", both);
            StringAssert.EndsWith(",\"reasoning_effort\":\"low\"}", both);
        }

        [Test]
        public void DeviceConfigWithoutPenaltyFieldsKeepsDefaults()
        {
            var cfg = new LlmEndpointConfig();
            JsonUtility.FromJsonOverwrite("{\"mode\":\"DirectDev\",\"temperature\":0.7}", cfg);
            Assert.AreEqual(0.3f, cfg.frequencyPenalty, 1e-6f);
            Assert.AreEqual(0.2f, cfg.presencePenalty, 1e-6f);
            Assert.AreEqual(0.7f, cfg.temperature, 1e-6f);
        }

        private static readonly string[] TenQuestions =
        {
            "Tell me about yourself.", "What do you think of the other villagers?",
            "What time of day is it, and what are you up to?", "Where can I find lantern shards?",
            "What's your favorite food?", "Heard any rumors lately?", "What should I do before nightfall?",
            "Is the dungeon dangerous?", "Can you teach me something useful?", "Why should I trust you?"
        };

        [Test]
        public void OfflineFallbackAnswersTheTenEvalQuestionsWithTenDifferentLines()
        {
            // When the API is down or rate-limited (Groq TPD 429), every turn lands here; the old
            // provider had 3 "curious" lines, so different questions got the same answer.
            var lines = TenQuestions.Select(q => TurnResponseParser.Parse(MockLlmProvider.Respond(q)).Dialogue).ToList();
            Assert.AreEqual(10, lines.Distinct().Count(), string.Join("\n", lines));
            StringAssert.Contains("shard", TurnResponseParser.Parse(MockLlmProvider.Respond("Where can I find lantern shards?")).Dialogue.ToLowerInvariant());
            StringAssert.Contains("dungeon", TurnResponseParser.Parse(MockLlmProvider.Respond("Is the dungeon dangerous?")).Dialogue.ToLowerInvariant());
        }

        [Test]
        public void OfflineFallbackSkipsLinesTheNpcAlreadySaid()
        {
            const string q = "Heard any rumors lately?";
            string first = TurnResponseParser.Parse(MockLlmProvider.Respond(q)).Dialogue;

            var req = new LlmRequest();
            req.Messages.Add(new ChatMessage(ChatRole.User, q));
            req.Messages.Add(new ChatMessage(ChatRole.Assistant, "{\"dialogue\":\"" + first + "\",\"mood\":\"neutral\"}"));
            req.Messages.Add(new ChatMessage(ChatRole.User, q));
            var said = MockLlmProvider.RecentNpcLines(req);
            CollectionAssert.AreEqual(new[] { first }, said);

            string second = TurnResponseParser.Parse(MockLlmProvider.Respond(q, said)).Dialogue;
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(second, TurnResponseParser.Parse(MockLlmProvider.Respond(q, said)).Dialogue, "still deterministic");
        }
    }
}
