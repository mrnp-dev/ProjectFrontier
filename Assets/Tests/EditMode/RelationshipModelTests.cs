using NUnit.Framework;
using UnityEngine;

namespace ProjectFrontier.Social.Tests
{
    public class RelationshipClampingTests
    {
        [Test]
        public void LlmTurn_DeltasClampedToFive()
        {
            var s = new SocialState();
            var change = s.ApplyDelta("bram", new AxisDeltas(40, -40, 99), DeltaSource.LlmTurn);

            Assert.AreEqual(new AxisDeltas(5, -5, 5), change.Applied);
            Assert.AreEqual(new AxisDeltas(40, -40, 99), change.Requested);
            var bram = s.Get("bram");
            Assert.AreEqual(5, bram.Trust);
            Assert.AreEqual(-5, bram.Affection);
            Assert.AreEqual(5, bram.Respect);
        }

        [Test]
        public void InRangeDeltas_PassThroughUnchanged()
        {
            var s = new SocialState();
            var change = s.ApplyDelta("mira", new AxisDeltas(3, -2, 0), DeltaSource.LlmTurn);
            Assert.AreEqual(new AxisDeltas(3, -2, 0), change.Applied);
        }

        [TestCase(DeltaSource.LlmTurn, 5)]
        [TestCase(DeltaSource.Gossip, 5)]
        [TestCase(DeltaSource.Scripted, 25)]
        public void EachSource_HasItsOwnCap(DeltaSource source, int cap)
        {
            var s = new SocialState();
            var change = s.ApplyDelta("pip", new AxisDeltas(1000, -1000, 1000), source);
            Assert.AreEqual(new AxisDeltas(cap, -cap, cap), change.Applied);
        }

        [Test]
        public void Axes_ClampToBounds_AndReportLandedDelta()
        {
            var s = new SocialState();
            for (int i = 0; i < 10; i++) s.ApplyDelta("lyra", new AxisDeltas(25, 25, -25), DeltaSource.Scripted);

            var lyra = s.Get("lyra");
            Assert.AreEqual(RelationshipRules.AxisMax, lyra.Trust);
            Assert.AreEqual(RelationshipRules.AxisMax, lyra.Affection);
            Assert.AreEqual(RelationshipRules.AxisMin, lyra.Respect);

            var atCap = s.ApplyDelta("lyra", new AxisDeltas(5, 0, 0), DeltaSource.LlmTurn);
            Assert.AreEqual(AxisDeltas.Zero, atCap.Applied);
        }

        [Test]
        public void LlmPayload_CannotCarryGoldItemsOrQuestFlags()
        {
            // Whatever extra keys the model emits, the delta type only has the three axes.
            const string json = "{\"trust\":40,\"affection\":2,\"respect\":-1,\"gold\":9999,\"items\":[\"legendary_sword\"],\"quest_flags\":{\"raid_won\":true}}";
            var parsed = JsonUtility.FromJson<AxisDeltas>(json);
            var fields = typeof(AxisDeltas).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            Assert.AreEqual(3, fields.Length, "AxisDeltas must only expose trust/affection/respect");

            var s = new SocialState();
            var change = s.ApplyDelta("aldric", parsed, DeltaSource.LlmTurn);
            Assert.AreEqual(new AxisDeltas(5, 2, -1), change.Applied);
        }

        [Test]
        public void ChangedEvent_FiresOnlyWhenSomethingLands()
        {
            var s = new SocialState();
            int fired = 0;
            s.RelationshipChanged += _ => fired++;
            s.ApplyDelta("bram", AxisDeltas.Zero, DeltaSource.LlmTurn);
            Assert.AreEqual(0, fired);
            s.ApplyDelta("bram", new AxisDeltas(1, 0, 0), DeltaSource.LlmTurn);
            Assert.AreEqual(1, fired);
        }
    }

    public class RelationshipStateTests
    {
        [TestCase(0, 0, 0, RelationshipState.Neutral)]
        [TestCase(35, 25, 0, RelationshipState.Friend)]
        [TestCase(34, 25, 0, RelationshipState.Neutral)]
        [TestCase(35, 25, -31, RelationshipState.Neutral)] // no friendship without respect
        [TestCase(60, 75, 0, RelationshipState.Lover)]
        [TestCase(-40, 0, 0, RelationshipState.Enemy)]
        [TestCase(0, -50, 0, RelationshipState.Enemy)]
        [TestCase(80, -60, 0, RelationshipState.Enemy)]    // hostility dominates
        public void Evaluate_MapsAxesToState(int t, int a, int r, RelationshipState expected)
        {
            Assert.AreEqual(expected, RelationshipRules.Evaluate(t, a, r));
        }

        [Test]
        public void LlmTurnsAlone_CannotFlipNeutralToFriendQuickly()
        {
            var s = new SocialState();
            // Friend needs trust 35 -> at least 7 maxed LLM turns.
            for (int i = 0; i < 6; i++) s.ApplyDelta("mira", new AxisDeltas(99, 99, 99), DeltaSource.LlmTurn);
            Assert.AreEqual(RelationshipState.Neutral, s.Get("mira").State);
            s.ApplyDelta("mira", new AxisDeltas(99, 99, 99), DeltaSource.LlmTurn);
            Assert.AreEqual(RelationshipState.Friend, s.Get("mira").State);
        }

        [Test]
        public void Transitions_MoveOneRankPerUpdate_EnemyNeverJumpsToLover()
        {
            var s = new SocialState();
            for (int i = 0; i < 3; i++) s.ApplyDelta("lyra", new AxisDeltas(-25, -25, 0), DeltaSource.Scripted);
            Assert.AreEqual(RelationshipState.Enemy, s.Get("lyra").State);

            // Jump far into Lover territory with big scripted gifts: still climbs one rank per update.
            var steps = new System.Collections.Generic.List<RelationshipState>();
            for (int i = 0; i < 8; i++)
            {
                s.ApplyDelta("lyra", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
                steps.Add(s.Get("lyra").State);
            }
            CollectionAssert.IsOrdered(steps);
            for (int i = 1; i < steps.Count; i++)
                Assert.LessOrEqual((int)steps[i] - (int)steps[i - 1], 1, "skipped a rank");
            Assert.AreEqual(RelationshipState.Lover, steps[steps.Count - 1]);
        }

        [Test]
        public void FriendToEnemy_PassesThroughNeutral()
        {
            var s = new SocialState();
            s.ApplyDelta("bram", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            s.ApplyDelta("bram", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            Assert.AreEqual(RelationshipState.Friend, s.Get("bram").State);

            for (int i = 0; i < 4; i++) s.ApplyDelta("bram", new AxisDeltas(-25, -25, 0), DeltaSource.Scripted);
            Assert.AreEqual(-50, s.Get("bram").Trust);
            // 4 updates: Friend -> Neutral -> Enemy; the first drop lands on Neutral, never straight to Enemy.
            Assert.AreEqual(RelationshipState.Enemy, s.Get("bram").State);

            var s2 = new SocialState();
            s2.ApplyDelta("bram", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            s2.ApplyDelta("bram", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            s2.ApplyDelta("bram", new AxisDeltas(-25, -25, 0), DeltaSource.Scripted);   // 25/25 -> Neutral
            Assert.AreEqual(RelationshipState.Neutral, s2.Get("bram").State);
        }

        [Test]
        public void Hysteresis_KeepsFriendNearTheLine()
        {
            var s = new SocialState();
            s.ApplyDelta("pip", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            s.ApplyDelta("pip", new AxisDeltas(11, 0, 0), DeltaSource.Scripted);   // trust 36, affection 25
            Assert.AreEqual(RelationshipState.Friend, s.Get("pip").State);

            s.ApplyDelta("pip", new AxisDeltas(-5, -5, 0), DeltaSource.LlmTurn);   // 31/20: below entry, within slack
            Assert.AreEqual(RelationshipState.Friend, s.Get("pip").State);

            s.ApplyDelta("pip", new AxisDeltas(-5, -5, 0), DeltaSource.LlmTurn);   // 26/15: beyond slack
            Assert.AreEqual(RelationshipState.Neutral, s.Get("pip").State);

            s.ApplyDelta("pip", new AxisDeltas(5, 5, 0), DeltaSource.LlmTurn);     // 31/20: must re-earn the strict line
            Assert.AreEqual(RelationshipState.Neutral, s.Get("pip").State);
        }

        [Test]
        public void Hysteresis_EnemyMustRecoverPastSlack()
        {
            var s = new SocialState();
            s.ApplyDelta("aldric", new AxisDeltas(-25, 0, 0), DeltaSource.Scripted);
            s.ApplyDelta("aldric", new AxisDeltas(-20, 0, 0), DeltaSource.Scripted);   // trust -45
            Assert.AreEqual(RelationshipState.Enemy, s.Get("aldric").State);

            s.ApplyDelta("aldric", new AxisDeltas(10, 0, 0), DeltaSource.Scripted);    // -35: within slack (-32)
            Assert.AreEqual(RelationshipState.Enemy, s.Get("aldric").State);
            s.ApplyDelta("aldric", new AxisDeltas(5, 0, 0), DeltaSource.Scripted);     // -30: out
            Assert.AreEqual(RelationshipState.Neutral, s.Get("aldric").State);
        }
    }

    public class SocialPersistenceTests
    {
        private static SocialState Populated()
        {
            var s = new SocialState();
            s.ApplyDelta("bram", new AxisDeltas(25, 25, 10), DeltaSource.Scripted);
            s.ApplyDelta("bram", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            s.ApplyDelta("lyra", new AxisDeltas(-3, 2, -5), DeltaSource.LlmTurn);
            s.MarkMet("bram");
            s.AdvanceDay();
            s.AddRumor(new Rumor { holderId = "mira", sourceId = "bram", tag = "insulted_craft", valence = -0.6f, strength = 0.8f, expiresDay = 6 });
            return s;
        }

        [Test]
        public void RoundTrip_ThroughJson_PreservesEverything()
        {
            var original = Populated();
            string json = JsonUtility.ToJson(original.ToSaveData(1234));
            var restored = new SocialState();
            restored.LoadFrom(JsonUtility.FromJson<SocialSaveData>(json));

            Assert.AreEqual(2, restored.Day);
            var bram = restored.Get("bram");
            Assert.AreEqual(50, bram.Trust);
            Assert.AreEqual(50, bram.Affection);
            Assert.AreEqual(10, bram.Respect);
            Assert.AreEqual(RelationshipState.Friend, bram.State);
            Assert.AreEqual(-3, restored.Get("lyra").Trust);
            Assert.AreEqual(1, restored.Rumors.Count);
            Assert.AreEqual("insulted_craft", restored.Rumors[0].tag);
            Assert.AreEqual(-0.6f, restored.Rumors[0].valence, 1e-4f);
            Assert.IsTrue(restored.HasMet("bram"));
            Assert.IsFalse(restored.HasMet("lyra"));
        }

        [Test]
        public void SavedState_PreservesHysteresisState()
        {
            // Friend kept by hysteresis at 31/20 must still be Friend after reload, not re-evaluated to Neutral.
            var s = new SocialState();
            s.ApplyDelta("pip", new AxisDeltas(25, 25, 0), DeltaSource.Scripted);
            s.ApplyDelta("pip", new AxisDeltas(11, 0, 0), DeltaSource.Scripted);
            s.ApplyDelta("pip", new AxisDeltas(-5, -5, 0), DeltaSource.LlmTurn);
            var restored = new SocialState();
            restored.LoadFrom(s.ToSaveData(1));
            Assert.AreEqual(RelationshipState.Friend, restored.Get("pip").State);
        }

        [Test]
        public void Load_ClampsCorruptValues_AndSkipsBadRecords()
        {
            var data = new SocialSaveData { day = -4 };
            data.relationships.Add(new RelationshipRecord { npcId = "bram", trust = 900, affection = -900, respect = 0, state = 42 });
            data.relationships.Add(null);
            data.relationships.Add(new RelationshipRecord { npcId = "" });
            data.rumors.Add(new Rumor { holderId = "mira", valence = -7f, strength = 3f, expiresDay = 9 });
            data.rumors.Add(null);

            var s = new SocialState();
            s.LoadFrom(data);
            Assert.AreEqual(1, s.Day);
            var bram = s.Get("bram");
            Assert.AreEqual(100, bram.Trust);
            Assert.AreEqual(-100, bram.Affection);
            Assert.AreEqual(RelationshipState.Enemy, bram.State, "invalid state is re-evaluated from axes");
            Assert.AreEqual(1, s.Rumors.Count);
            Assert.AreEqual(-1f, s.Rumors[0].valence);
            Assert.AreEqual(1f, s.Rumors[0].strength);
        }

        [Test]
        public void Load_NullResetsToFreshState()
        {
            var s = Populated();
            s.LoadFrom(null);
            Assert.AreEqual(1, s.Day);
            Assert.AreEqual(0, s.Rumors.Count);
            Assert.AreEqual(0, s.Get("bram").Trust);
            Assert.IsFalse(s.HasMet("bram"));
        }

        [Test]
        public void AdvanceDay_ExpiresOldRumors()
        {
            var s = new SocialState();
            s.AddRumor(new Rumor { holderId = "mira", tag = "a", expiresDay = 1 });
            s.AddRumor(new Rumor { holderId = "mira", tag = "b", expiresDay = 2 });
            s.AdvanceDay();   // day 2
            Assert.AreEqual(1, s.Rumors.Count);
            Assert.AreEqual("b", s.Rumors[0].tag);
        }

        [Test]
        public void PickNewest_PrefersLatestTimestamp_ThenDay()
        {
            var older = new SocialSaveData { savedAtUnixMs = 100, day = 5 };
            var newer = new SocialSaveData { savedAtUnixMs = 200, day = 2 };
            Assert.AreSame(newer, SocialSaveData.PickNewest(older, newer));
            Assert.AreSame(newer, SocialSaveData.PickNewest(newer, older));
            Assert.AreSame(older, SocialSaveData.PickNewest(older, null));
            Assert.AreSame(newer, SocialSaveData.PickNewest(null, newer));

            var tieA = new SocialSaveData { savedAtUnixMs = 100, day = 3 };
            var tieB = new SocialSaveData { savedAtUnixMs = 100, day = 4 };
            Assert.AreSame(tieB, SocialSaveData.PickNewest(tieA, tieB));
        }
    }
}
