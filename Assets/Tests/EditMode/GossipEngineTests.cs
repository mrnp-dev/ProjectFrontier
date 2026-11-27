using System.Collections.Generic;
using NUnit.Framework;
using ProjectFrontier.Social;

namespace ProjectFrontier.Social.Tests
{
    public class GossipEngineTests
    {
        private static readonly IReadOnlyList<IReadOnlyList<string>> EveningGroups = new List<IReadOnlyList<string>>
        {
            new List<string> { "bram", "mira", "aldric" },   // Tavern
            new List<string> { "lyra", "pip" }               // Square
        };

        private static SocialState NewState() => new SocialState();

        [Test]
        public void DefaultTrustMatrix_Is5x5_WithGddWeights()
        {
            var m = GossipTrustMatrix.CreateDefault();
            Assert.AreEqual(5, m.Count);
            Assert.AreEqual(0.8f, m.Get("bram", "mira"), 1e-4f);
            Assert.AreEqual(0.2f, m.Get("bram", "lyra"), 1e-4f);
            Assert.AreEqual(0.8f, m.Get("mira", "bram"), 1e-4f);
            Assert.AreEqual(1f, m.Get("pip", "pip"), 1e-4f);
            foreach (var l in m.Ids)
                foreach (var s in m.Ids)
                {
                    float w = m.Get(l, s);
                    Assert.That(w, Is.InRange(0f, 1f), $"{l}->{s}");
                }
            Assert.AreEqual(GossipTrustMatrix.DefaultTrust, m.Get("stranger", "bram"));
        }

        [Test]
        public void NotableChat_CreatesFirstHandRumor_SmallTalkDoesNot()
        {
            var s = NewState();
            Assert.IsNull(GossipEngine.RecordChatOutcome(s, "bram", new AxisDeltas(1, 1, 0)));
            Rumor r = GossipEngine.RecordChatOutcome(s, "bram", new AxisDeltas(-4, -4, -2));
            Assert.IsNotNull(r);
            Assert.AreEqual("mocked_bram_smithing", r.tag);
            Assert.AreEqual("bram", r.holderId);
            Assert.AreEqual("bram", r.Origin);
            Assert.IsTrue(r.IsFirstHand);
            Assert.Less(r.valence, 0f);
            Assert.AreEqual(1 + GossipEngine.RumorLifetimeDays, r.expiresDay);
            Assert.AreEqual(1, s.Rumors.Count);

            // Same kind of action again reinforces instead of duplicating.
            float before = r.strength;
            GossipEngine.RecordChatOutcome(s, "bram", new AxisDeltas(-5, -5, -5));
            Assert.AreEqual(1, s.Rumors.Count);
            Assert.Greater(s.Rumors[0].strength, before);

            Rumor kind = GossipEngine.RecordChatOutcome(s, "pip", new AxisDeltas(3, 4, 0));
            Assert.AreEqual("was_gentle_with_pip", kind.tag);
            Assert.Greater(kind.valence, 0f);
        }

        [Test]
        public void EveningExchange_AppliesBiasFormula_ToCoLocatedListenersOnly()
        {
            var s = NewState();
            var trust = GossipTrustMatrix.CreateDefault();
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);
            var transfers = new List<GossipTransfer>();

            int n = GossipEngine.RunExchange(s, trust, EveningGroups, transfers);

            Assert.AreEqual(2, n);   // mira + aldric hear it; lyra and pip are at the square
            GossipTransfer toMira = transfers.Find(t => t.ListenerId == "mira");
            Assert.AreEqual(-0.6f * 0.8f * 0.8f, toMira.BiasDelta, 1e-4f);   // valence * trust(mira<-bram) * strength
            Assert.AreEqual(-3, toMira.Applied.trust);    // round(-0.384 * 8)
            Assert.AreEqual(-3, toMira.Applied.affection);
            Assert.AreEqual(0, toMira.Applied.respect);
            Assert.AreEqual(-3, s.Get("mira").Trust);
            Assert.AreEqual(-3, s.Get("mira").Affection);

            GossipTransfer toAldric = transfers.Find(t => t.ListenerId == "aldric");
            Assert.AreEqual(-0.6f * 0.6f * 0.8f, toAldric.BiasDelta, 1e-4f);
            Assert.AreEqual(-2, s.Get("aldric").Trust);

            Assert.AreEqual(0, s.Get("lyra").Trust);
            Assert.AreEqual(0, s.Get("pip").Trust);
            Assert.AreEqual(0, s.Get("bram").Trust, "the origin is never biased by its own rumor");

            Rumor heard = s.FindRumor("mira", "bram", "mocked_bram_smithing");
            Assert.IsNotNull(heard);
            Assert.AreEqual("bram", heard.sourceId);
            Assert.AreEqual(0.64f, heard.strength, 1e-4f);   // belief = strength * trust
            Assert.AreEqual(-3, heard.appliedTrust);
            Assert.AreEqual(1, s.LastGossipDay);
        }

        [Test]
        public void GossipBias_IsClampedToGossipCap()
        {
            var s = NewState();
            var trust = GossipTrustMatrix.CreateDefault();
            trust.Set("mira", "bram", 1f);
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -1f, 1f);   // raw = -8 points
            GossipEngine.RunExchange(s, trust, EveningGroups);
            Assert.AreEqual(-RelationshipRules.GossipCap, s.Get("mira").Trust);
            Assert.AreEqual(-RelationshipRules.GossipCap, s.Get("mira").Affection);
        }

        [Test]
        public void Exchange_IsIdempotent_ForAlreadyKnownRumors()
        {
            var s = NewState();
            var trust = GossipTrustMatrix.CreateDefault();
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);
            GossipEngine.RunExchange(s, trust, EveningGroups);
            int miraTrust = s.Get("mira").Trust;
            int rumorCount = s.Rumors.Count;

            int again = GossipEngine.RunExchange(s, trust, EveningGroups);
            Assert.AreEqual(0, again);
            Assert.AreEqual(miraTrust, s.Get("mira").Trust);
            Assert.AreEqual(rumorCount, s.Rumors.Count);
        }

        [Test]
        public void Rumor_PropagatesOneHopPerEvening_WithDecayingBelief()
        {
            var s = NewState();
            var trust = GossipTrustMatrix.CreateDefault();
            var tavern = new List<IReadOnlyList<string>> { new List<string> { "bram", "mira" } };
            var square = new List<IReadOnlyList<string>> { new List<string> { "mira", "pip" } };
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);

            GossipEngine.RunExchange(s, trust, square);   // Mira knows nothing yet
            Assert.IsNull(s.FindRumor("pip", "bram", "mocked_bram_smithing"));

            GossipEngine.RunExchange(s, trust, tavern);
            Rumor mira = s.FindRumor("mira", "bram", "mocked_bram_smithing");
            Assert.IsNotNull(mira);

            s.AdvanceDay();
            GossipEngine.RunExchange(s, trust, square);
            Rumor pip = s.FindRumor("pip", "bram", "mocked_bram_smithing");
            Assert.IsNotNull(pip, "Mira relays Bram's rumor to Pip the next evening");
            Assert.AreEqual("mira", pip.sourceId);
            Assert.AreEqual("bram", pip.Origin);
            Assert.AreEqual(mira.strength * trust.Get("pip", "mira"), pip.strength, 1e-4f);
            Assert.Less(pip.strength, mira.strength);
        }

        [Test]
        public void Rumors_ExpireAndStopSpreading()
        {
            var s = NewState();
            var trust = GossipTrustMatrix.CreateDefault();
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);   // expires day 5
            for (int i = 0; i < GossipEngine.RumorLifetimeDays + 1; i++) s.AdvanceDay();   // day 6
            Assert.AreEqual(0, s.Rumors.Count);
            Assert.AreEqual(0, GossipEngine.RunExchange(s, trust, EveningGroups));
            Assert.AreEqual(0, s.Get("mira").Trust);
        }

        [Test]
        public void HeardRumor_ExpiresWithOriginalRumor()
        {
            var s = NewState();
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);
            GossipEngine.RunExchange(s, GossipTrustMatrix.CreateDefault(), EveningGroups);
            Assert.IsNotNull(GossipEngine.TopHeardRumor(s.Rumors, "mira", s.Day));
            while (s.Day <= 1 + GossipEngine.RumorLifetimeDays) s.AdvanceDay();
            Assert.IsNull(GossipEngine.TopHeardRumor(s.Rumors, "mira", s.Day));
            Assert.IsNull(GossipEngine.BuildPromptLine(s.Rumors, "mira", s.Day, null));
        }

        [Test]
        public void PromptLine_IsSingleLine_SkipsFirstHand_AndNamesSource()
        {
            var s = NewState();
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);
            Assert.IsNull(GossipEngine.BuildPromptLine(s.Rumors, "bram", s.Day, null), "Bram isn't told his own story");

            GossipEngine.RunExchange(s, GossipTrustMatrix.CreateDefault(), EveningGroups);
            string line = GossipEngine.BuildPromptLine(s.Rumors, "mira", s.Day, null);
            Assert.AreEqual("RUMOR: Bram says the player mocked his smithing (you mostly believe Bram).", line);
            Assert.IsFalse(line.Contains("\n"));
            Assert.LessOrEqual(line.Length, 100, "~15-20 tokens");

            string greeting = GossipEngine.BuildGreetingLine(s.Rumors, "mira", s.Day, null);
            StringAssert.StartsWith("Bram told me you mocked his smithing.", greeting);
            Assert.IsNull(GossipEngine.BuildGreetingLine(s.Rumors, "lyra", s.Day, null));
        }

        [Test]
        public void RelayedPromptLine_NamesTheOriginalVillager()
        {
            var s = NewState();
            s.AddRumor(new Rumor { holderId = "pip", sourceId = "mira", originId = "bram", tag = "mocked_bram_smithing", valence = -0.6f, strength = 0.8f, expiresDay = 5 });
            Assert.AreEqual("RUMOR: Mira says the player mocked Bram's smithing (you believe Mira).",
                GossipEngine.BuildPromptLine(s.Rumors, "pip", 1, null));
        }

        [Test]
        public void GossipState_SurvivesSaveRoundTrip()
        {
            var s = NewState();
            GossipEngine.RecordAction(s, "bram", "mocked_bram_smithing", -0.6f, 0.8f);
            GossipEngine.RunExchange(s, GossipTrustMatrix.CreateDefault(), EveningGroups);
            string json = UnityEngine.JsonUtility.ToJson(s.ToSaveData(42));
            var restored = new SocialState();
            restored.LoadFrom(UnityEngine.JsonUtility.FromJson<SocialSaveData>(json));
            Assert.AreEqual(1, restored.LastGossipDay);
            Rumor heard = restored.FindRumor("mira", "bram", "mocked_bram_smithing");
            Assert.IsNotNull(heard);
            Assert.AreEqual("bram", heard.originId);
            Assert.AreEqual(-3, heard.appliedTrust);
            Assert.AreEqual(0, GossipEngine.RunExchange(restored, GossipTrustMatrix.CreateDefault(), EveningGroups));
        }

        [Test]
        public void HolderRumorCap_DropsWeakest()
        {
            var s = NewState();
            for (int i = 0; i < SocialState.MaxRumorsPerHolder + 2; i++)
                GossipEngine.RecordAction(s, "lyra", "tag_" + i, -0.5f, 0.1f * (i + 1));
            Assert.AreEqual(SocialState.MaxRumorsPerHolder, s.CountRumors("lyra"));
            Assert.IsNull(s.FindRumor("lyra", "lyra", "tag_0"));
            Assert.IsNotNull(s.FindRumor("lyra", "lyra", "tag_5"));
        }
    }
}
