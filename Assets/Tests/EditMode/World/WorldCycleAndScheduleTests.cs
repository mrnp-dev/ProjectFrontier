using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace ProjectFrontier.World.Tests
{
    public class DayClockTests
    {
        [Test]
        public void Phases_ProgressDeterministically_FromMorningToNight()
        {
            var clock = new DayClock(dayDurationSeconds: 180f, startDay: 1);
            var transitions = new List<(DayPhase from, DayPhase to)>();
            bool midnightFired = false;
            clock.PhaseChanged += (a, b) => transitions.Add((a, b));
            clock.MidnightReached += () => midnightFired = true;

            Assert.AreEqual(DayPhase.Morning, clock.CurrentPhase);
            clock.GetClockTime(out int h0, out int m0);
            Assert.AreEqual(6, h0);
            Assert.AreEqual(0, m0);

            // Step 1 second at a time through the whole 180s day.
            for (int i = 0; i < 180; i++) clock.Tick(1f);

            Assert.AreEqual(DayPhase.Night, clock.CurrentPhase);
            Assert.IsTrue(clock.MidnightPending);
            Assert.IsTrue(midnightFired);
            Assert.AreEqual(3, transitions.Count);
            Assert.AreEqual((DayPhase.Morning, DayPhase.Midday), transitions[0]);
            Assert.AreEqual((DayPhase.Midday, DayPhase.Evening), transitions[1]);
            Assert.AreEqual((DayPhase.Evening, DayPhase.Night), transitions[2]);
        }

        [Test]
        public void MidnightReached_FiresOnlyOnceUntilSleep()
        {
            var clock = new DayClock(dayDurationSeconds: 10f, startDay: 1);
            int midnightCount = 0;
            clock.MidnightReached += () => midnightCount++;

            clock.Tick(12f);
            clock.Tick(5f);
            clock.Tick(5f);
            Assert.AreEqual(1, midnightCount);

            clock.SleepToNextDay();
            Assert.AreEqual(2, clock.Day);
            Assert.AreEqual(DayPhase.Morning, clock.CurrentPhase);
            Assert.AreEqual(0f, clock.NormalizedTime, 0.0001f);
            Assert.IsFalse(clock.MidnightPending);

            clock.Tick(12f);
            Assert.AreEqual(2, midnightCount);
        }

        [Test]
        public void SleepToNextDay_CanSyncWithExternalSocialDay()
        {
            var clock = new DayClock(dayDurationSeconds: 60f, startDay: 1);
            int advancedDay = 0;
            clock.DayAdvanced += d => advancedDay = d;
            clock.SetPhase(DayPhase.Evening);

            clock.SleepToNextDay(syncedDay: 5);

            Assert.AreEqual(5, clock.Day);
            Assert.AreEqual(5, advancedDay);
            Assert.AreEqual(DayPhase.Morning, clock.CurrentPhase);
            Assert.AreEqual("DAY 5 · MORNING · 06:00", clock.FormatHudLabel());
        }

        [Test]
        public void ClockTime_SnapsToTenMinuteIncrements()
        {
            var clock = new DayClock(dayDurationSeconds: 180f, startDay: 1);
            clock.SetPhase(DayPhase.Midday);
            clock.GetClockTime(out int hm, out int mm);
            Assert.AreEqual(11, hm);
            Assert.AreEqual(0, mm);

            clock.SetPhase(DayPhase.Evening);
            clock.GetClockTime(out int he, out int me);
            Assert.AreEqual(17, he);
            Assert.AreEqual(0, me);

            clock.SetPhase(DayPhase.Night);
            clock.GetClockTime(out int hn, out int mn);
            Assert.AreEqual(21, hn);
            Assert.AreEqual(0, mn);
        }
    }

    public class DayLightingGradingTests
    {
        [Test]
        public void LightingCurve_StaysLegibleAtNight_AndBrightestAtMidday()
        {
            LightGrade morning = DayLightingGrading.EvaluateForPhase(DayPhase.Morning);
            LightGrade midday = DayLightingGrading.EvaluateForPhase(DayPhase.Midday);
            LightGrade evening = DayLightingGrading.EvaluateForPhase(DayPhase.Evening);
            LightGrade night = DayLightingGrading.EvaluateForPhase(DayPhase.Night);
            LightGrade midnight = DayLightingGrading.Evaluate(1f);

            Assert.Greater(midday.Intensity, morning.Intensity);
            Assert.Greater(midday.Intensity, evening.Intensity);
            Assert.Greater(evening.Intensity, night.Intensity);
            // Low graphics tier disables all point lights: night must stay >= 0.50 intensity.
            Assert.GreaterOrEqual(midnight.Intensity, 0.50f);
            // Evening should be warmer (R > B); night should be cooler (B > R).
            Assert.Greater(evening.Color.r, evening.Color.b);
            Assert.Greater(night.Color.b, night.Color.r);
        }

        [Test]
        public void LightingCurve_SmoothAcrossEntireRange()
        {
            LightGrade prev = DayLightingGrading.Evaluate(0f);
            for (int i = 1; i <= 100; i++)
            {
                LightGrade cur = DayLightingGrading.Evaluate(i / 100f);
                Assert.IsFalse(float.IsNaN(cur.Intensity));
                Assert.Less(Mathf.Abs(cur.Intensity - prev.Intensity), 0.1f);
                prev = cur;
            }
        }
    }

    public class NpcScheduleTableTests
    {
        [Test]
        public void EveryNpc_VisitsMultipleSpots_AndAllFiveSpotsAreUsed()
        {
            var allSpotsUsed = new HashSet<VillageSpot>();
            foreach (string id in NpcScheduleTable.CastOrder)
            {
                var npcSpots = new HashSet<VillageSpot>();
                foreach (DayPhase phase in System.Enum.GetValues(typeof(DayPhase)))
                {
                    VillageSpot spot = NpcScheduleTable.GetScheduledSpot(id, phase);
                    npcSpots.Add(spot);
                    allSpotsUsed.Add(spot);
                }
                Assert.GreaterOrEqual(npcSpots.Count, 2, $"{id} should move between at least 2 spots across the day");
            }

            Assert.AreEqual(5, allSpotsUsed.Count, "All five village spots should be visited across the schedule");
        }

        [Test]
        public void Evening_FormsDeterministicCoLocationHubs_WithDistinctStandingSlots()
        {
            var groups = new Dictionary<VillageSpot, List<string>>();
            NpcScheduleTable.GetCoLocatedGroups(DayPhase.Evening, groups);

            Assert.IsTrue(groups.ContainsKey(VillageSpot.Tavern));
            Assert.IsTrue(groups.ContainsKey(VillageSpot.Square));
            CollectionAssert.AreEquivalent(new[] { "bram", "mira", "aldric" }, groups[VillageSpot.Tavern]);
            CollectionAssert.AreEquivalent(new[] { "lyra", "pip" }, groups[VillageSpot.Square]);

            // Verify no two co-located NPCs share the same coordinates in any phase.
            foreach (DayPhase phase in System.Enum.GetValues(typeof(DayPhase)))
            {
                for (int i = 0; i < NpcScheduleTable.CastOrder.Length; i++)
                for (int j = i + 1; j < NpcScheduleTable.CastOrder.Length; j++)
                {
                    Vector2 a = NpcScheduleTable.GetTargetWorldPosition(NpcScheduleTable.CastOrder[i], phase);
                    Vector2 b = NpcScheduleTable.GetTargetWorldPosition(NpcScheduleTable.CastOrder[j], phase);
                    Assert.Greater(Vector2.Distance(a, b), 0.7f,
                        $"{NpcScheduleTable.CastOrder[i]} and {NpcScheduleTable.CastOrder[j]} overlap in {phase}");
                }
            }
        }

        [Test]
        public void Schedule_IsDeterministic()
        {
            foreach (DayPhase phase in System.Enum.GetValues(typeof(DayPhase)))
            foreach (string id in NpcScheduleTable.CastOrder)
            {
                Assert.AreEqual(
                    NpcScheduleTable.GetTargetWorldPosition(id, phase, day: 1),
                    NpcScheduleTable.GetTargetWorldPosition(id, phase, day: 1));
            }
        }
    }

    public class NavigationAndFrameBudgetTests
    {
        [Test]
        public void FlatAStar_RoutesAroundObstacleWithoutCuttingCorners()
        {
            var grid = NavGrid2D.CreateOakhavenDefault();
            // Vertical barrier across the center from y=-3 to y=3.
            grid.MarkBlockedRect(-0.5f, -3f, 0.5f, 3f);

            var astar = new FlatAStarPathfinder(grid);
            var path = new List<Vector2>(32);
            bool found = astar.FindPath(new Vector2(-4f, 0f), new Vector2(4f, 0f), path, out int expansions);

            Assert.IsTrue(found);
            Assert.Greater(path.Count, 1);
            Assert.Greater(expansions, 0);
            Assert.Less(Vector2.Distance(path[path.Count - 1], new Vector2(4f, 0f)), 0.01f);

            // Every intermediate waypoint must lie on a walkable cell.
            for (int i = 0; i < path.Count; i++)
            {
                grid.WorldToCell(path[i], out int cx, out int cy);
                Assert.IsTrue(grid.IsWalkable(cx, cy), $"Waypoint {i} at {path[i]} fell on a blocked cell");
            }
        }

        [Test]
        public void FlatAStar_HandlesBlockedGoalBySnappingToNearestWalkable()
        {
            var grid = NavGrid2D.CreateOakhavenDefault();
            grid.MarkBlockedRect(2f, 2f, 3.5f, 3.5f);

            var astar = new FlatAStarPathfinder(grid);
            var path = new List<Vector2>(32);
            bool found = astar.FindPath(new Vector2(-2f, 0f), new Vector2(2.75f, 2.75f), path, out _);

            Assert.IsTrue(found);
            Assert.GreaterOrEqual(path.Count, 1);
            grid.WorldToCell(path[path.Count - 1], out int gx, out int gy);
            Assert.IsTrue(grid.IsWalkable(gx, gy));
        }

        [Test]
        public void FrameBudgetedNavQueue_RespectsRequestAndNodeCaps_WhenAllFiveNpcsTransition()
        {
            var grid = NavGrid2D.CreateOakhavenDefault();
            // Block the central hearth and buildings roughly like Oakhaven.
            grid.MarkBlockedRect(-0.8f, -0.5f, 0.8f, 0.9f);
            grid.MarkBlockedRect(-8.0f, 2.7f, -4.4f, 4.9f);
            grid.MarkBlockedRect(4.4f, 2.7f, 8.0f, 4.9f);
            grid.MarkBlockedRect(-8.2f, -5.2f, -4.8f, -3.2f);
            grid.MarkBlockedRect(4.8f, -5.2f, 8.2f, -3.2f);

            const int reqCap = 2;
            const int nodeCap = 48;
            var queue = new FrameBudgetedNavQueue(new FlatAStarPathfinder(grid), reqCap, nodeCap);

            int completed = 0;
            var buffers = new List<Vector2>[NpcScheduleTable.CastOrder.Length];
            for (int i = 0; i < NpcScheduleTable.CastOrder.Length; i++)
            {
                string id = NpcScheduleTable.CastOrder[i];
                buffers[i] = new List<Vector2>(32);
                Vector2 start = NpcScheduleTable.GetTargetWorldPosition(id, DayPhase.Morning);
                Vector2 goal = NpcScheduleTable.GetTargetWorldPosition(id, DayPhase.Evening);
                queue.Enqueue(id, start, goal, buffers[i], ok => { if (ok) completed++; });
            }

            int frames = 0;
            while (!queue.IsIdle && frames < 100)
            {
                queue.ProcessFrameBudget();
                Assert.LessOrEqual(queue.LastFrameCompletedRequests, reqCap);
                Assert.LessOrEqual(queue.LastFrameExpansions, nodeCap);
                frames++;
            }

            Assert.IsTrue(queue.IsIdle);
            Assert.AreEqual(NpcScheduleTable.CastOrder.Length, completed);
            Assert.GreaterOrEqual(frames, 3, "5 simultaneous requests with reqCap=2 must spread across >= 3 frames");
            Assert.LessOrEqual(queue.PeakFrameExpansions, nodeCap);
        }

        [Test]
        public void FrameBudgetedNavQueue_CoalescesDuplicateRequesters()
        {
            var grid = NavGrid2D.CreateOakhavenDefault();
            var queue = new FrameBudgetedNavQueue(new FlatAStarPathfinder(grid), maxRequestsPerFrame: 1, maxNodeExpansionsPerFrame: 64);
            var buf = new List<Vector2>(16);
            int calls = 0;

            queue.Enqueue("bram", Vector2.zero, new Vector2(5f, 0f), buf, _ => calls++);
            queue.Enqueue("bram", Vector2.zero, new Vector2(-5f, 2f), buf, _ => calls++);
            Assert.AreEqual(1, queue.PendingCount);

            int guard = 0;
            while (!queue.IsIdle && guard++ < 20) queue.ProcessFrameBudget();

            Assert.AreEqual(1, calls);
            Assert.Less(Vector2.Distance(buf[buf.Count - 1], new Vector2(-5f, 2f)), 0.01f);
        }
    }
}
