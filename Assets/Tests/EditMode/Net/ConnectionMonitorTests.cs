using System.Collections.Generic;
using NUnit.Framework;

namespace ProjectFrontier.Net.Tests
{
    /// <summary>State machine for the online-only rules (#37): online -> degraded -> reconnecting -> online.</summary>
    public class ConnectionMonitorTests
    {
        private double _now;
        private ConnectionMonitor _m;

        [SetUp]
        public void SetUp()
        {
            _now = 100.0;
            _m = new ConnectionMonitor(() => _now, new ConnectionSettings { LongDropSeconds = 12.0, ProbeIntervalSeconds = 3.0 });
        }

        [TearDown]
        public void TearDown() => ConnectionReports.Sink = null;

        private void GoOnline()
        {
            _m.BeginStartupCheck();
            _m.CompleteStartupCheck(StartupBlockReason.None);
            Assert.AreEqual(ConnectionState.Online, _m.State);
        }

        [Test]
        public void StartsCheckingAndBlocksGameplay()
        {
            Assert.AreEqual(ConnectionState.Checking, _m.State);
            Assert.IsFalse(_m.CanStartGameplay);
            Assert.IsFalse(_m.IsHoldingGame);
        }

        [Test]
        public void StartupGatePassGoesOnline()
        {
            GoOnline();
            Assert.IsTrue(_m.CanStartGameplay);
            Assert.IsTrue(_m.AllowsNetworkCalls);
            Assert.AreEqual(StartupBlockReason.None, _m.BlockReason);
        }

        [Test]
        public void StartupGateFailBlocksUntilRetryPasses()
        {
            _m.CompleteStartupCheck(StartupBlockReason.NoInternet);
            Assert.AreEqual(ConnectionState.Blocked, _m.State);
            Assert.AreEqual(StartupBlockReason.NoInternet, _m.BlockReason);
            Assert.IsFalse(_m.CanStartGameplay);
            Assert.IsFalse(_m.AllowsNetworkCalls);

            _m.BeginStartupCheck(); // Retry
            Assert.AreEqual(ConnectionState.Checking, _m.State);
            Assert.AreEqual(StartupBlockReason.None, _m.BlockReason);
            _m.CompleteStartupCheck(StartupBlockReason.None);
            Assert.AreEqual(ConnectionState.Online, _m.State);
        }

        [Test]
        public void ReportsBeforeTheGateDoNotChangeState()
        {
            _m.ReportFailure(ConnectionSource.Auth, "sign-in failed");
            _m.ReportSuccess(ConnectionSource.CloudSave);
            Assert.AreEqual(ConnectionState.Checking, _m.State);

            _m.CompleteStartupCheck(StartupBlockReason.SignInFailed);
            _m.ReportSuccess(ConnectionSource.Llm);
            _now += 60;
            _m.Tick();
            Assert.AreEqual(ConnectionState.Blocked, _m.State, "only Retry leaves the blocked screen");
        }

        [Test]
        public void CompleteIsIgnoredWhenNotChecking()
        {
            GoOnline();
            _m.CompleteStartupCheck(StartupBlockReason.NoInternet);
            Assert.AreEqual(ConnectionState.Online, _m.State);
        }

        [Test]
        public void ShortBlipDegradesThenRecoversWithoutPausing()
        {
            GoOnline();
            _m.ReportFailure(ConnectionSource.Llm, "HTTP 0 Cannot resolve destination host");
            Assert.AreEqual(ConnectionState.Degraded, _m.State);
            Assert.IsTrue(_m.CanStartGameplay, "a blip doesn't block anything");
            Assert.IsFalse(_m.IsHoldingGame);
            Assert.IsTrue(_m.AllowsNetworkCalls, "calls keep retrying quietly while degraded");
            Assert.AreEqual(ConnectionSource.Llm, _m.LastFailureSource);

            _now += 4;
            _m.Tick();
            _m.ReportSuccess(ConnectionSource.Llm);
            Assert.AreEqual(ConnectionState.Online, _m.State);
            Assert.AreEqual(0, _m.ConsecutiveFailures);
            Assert.AreEqual(0, _m.ReconnectingEpisodes);
        }

        [Test]
        public void LongDropPausesAfterExactlyTheThreshold()
        {
            GoOnline();
            _m.ReportFailure(ConnectionSource.CloudSave, "transport");
            _now += 11.9;
            _m.Tick();
            Assert.AreEqual(ConnectionState.Degraded, _m.State);

            _now += 0.1;
            _m.Tick();
            Assert.AreEqual(ConnectionState.Reconnecting, _m.State);
            Assert.IsTrue(_m.IsHoldingGame);
            Assert.IsFalse(_m.AllowsNetworkCalls, "NPC turns answer instantly from built-in lines while paused");
            Assert.IsFalse(_m.CanStartGameplay);
            Assert.AreEqual(1, _m.ReconnectingEpisodes);
        }

        [Test]
        public void ReconnectingResumesOnFirstSuccess()
        {
            GoOnline();
            _m.ReportFailure(ConnectionSource.Llm, "timeout");
            _now += 13;
            _m.Tick();
            Assert.AreEqual(ConnectionState.Reconnecting, _m.State);

            _m.ReportSuccess(ConnectionSource.Probe);
            Assert.AreEqual(ConnectionState.Online, _m.State);
            Assert.IsFalse(_m.IsHoldingGame);
            Assert.AreEqual(0, _m.TroubleSeconds);
        }

        [Test]
        public void FullCycleOnlineDegradedReconnectingOnline()
        {
            GoOnline();
            var seen = new List<ConnectionState> { _m.State };
            int version = _m.Version;
            void Record() { if (_m.Version != version) { version = _m.Version; seen.Add(_m.State); } }

            _m.ReportInterfaceReachable(false); Record();
            for (int i = 0; i < 60; i++) { _now += 0.25; _m.Tick(); Record(); }   // 15 s of outage
            _m.ReportInterfaceReachable(true); Record();
            _m.ReportSuccess(ConnectionSource.Probe); Record();

            CollectionAssert.AreEqual(new[]
            {
                ConnectionState.Online, ConnectionState.Degraded, ConnectionState.Reconnecting, ConnectionState.Online
            }, seen);
        }

        [Test]
        public void ContinuousFailuresDoNotResetTheDropTimer()
        {
            GoOnline();
            _m.ReportFailure(ConnectionSource.Llm, "a");
            _now += 5; _m.ReportFailure(ConnectionSource.Llm, "b");
            _now += 5; _m.ReportFailure(ConnectionSource.CloudSave, "c");
            _now += 2; _m.Tick();
            Assert.AreEqual(ConnectionState.Reconnecting, _m.State, "12 s of continuous trouble");
            Assert.AreEqual(3, _m.ConsecutiveFailures);
            Assert.AreEqual("c", _m.LastFailureReason);
        }

        [Test]
        public void SuccessInBetweenStartsANewDrop()
        {
            GoOnline();
            _m.ReportFailure(ConnectionSource.Llm, "a");
            _now += 5; _m.ReportSuccess(ConnectionSource.Llm);
            _now += 5; _m.ReportFailure(ConnectionSource.Llm, "b");
            _now += 10; _m.Tick();
            Assert.AreEqual(ConnectionState.Degraded, _m.State, "only 10 s since the new drop began");
            _now += 2; _m.Tick();
            Assert.AreEqual(ConnectionState.Reconnecting, _m.State);
        }

        [Test]
        public void InterfaceComingBackAloneDoesNotEndADrop()
        {
            GoOnline();
            _m.ReportInterfaceReachable(false);
            Assert.AreEqual(ConnectionState.Degraded, _m.State);
            Assert.IsFalse(_m.AllowsNetworkCalls, "no interface: skip the network");
            Assert.AreEqual(ConnectionSource.Reachability, _m.LastFailureSource);

            _m.ReportInterfaceReachable(true);
            Assert.AreEqual(ConnectionState.Degraded, _m.State, "Wi-Fi up isn't proof the internet works");
            Assert.IsTrue(_m.AllowsNetworkCalls);
        }

        [Test]
        public void ProbesRunOnlyWhileTroubledEveryIntervalWithAnInterface()
        {
            GoOnline();
            _now += 10;
            Assert.IsFalse(_m.Tick(), "no probes while online");

            _m.ReportFailure(ConnectionSource.Llm, "x");
            Assert.IsFalse(_m.Tick(), "the failing call just told us; wait one interval");
            _now += 2.9; Assert.IsFalse(_m.Tick());
            _now += 0.1; Assert.IsTrue(_m.Tick());
            Assert.IsFalse(_m.Tick(), "once per interval");
            _now += 3; Assert.IsTrue(_m.Tick());

            _m.ReportInterfaceReachable(false);
            _now += 30; Assert.IsFalse(_m.Tick(), "no probes without an interface (also while reconnecting)");
            Assert.AreEqual(ConnectionState.Reconnecting, _m.State);
            _m.ReportInterfaceReachable(true);
            Assert.IsTrue(_m.Tick(), "probe as soon as the interface is back");
        }

        [Test]
        public void SuccessFromAServerMarksTheInterfaceUp()
        {
            GoOnline();
            _m.ReportInterfaceReachable(false);
            _m.ReportSuccess(ConnectionSource.Llm);
            Assert.IsTrue(_m.InterfaceReachable);
            Assert.AreEqual(ConnectionState.Online, _m.State);
        }

        [TestCase(true, true, true, true, StartupBlockReason.None)]
        [TestCase(true, true, false, false, StartupBlockReason.None)]
        [TestCase(false, true, true, true, StartupBlockReason.NoInternet)]
        [TestCase(false, false, true, false, StartupBlockReason.NoInternet)]
        [TestCase(true, false, true, true, StartupBlockReason.SignInFailed)]
        [TestCase(true, false, false, false, StartupBlockReason.SignInFailed)]
        [TestCase(true, true, true, false, StartupBlockReason.AiUnreachable)]
        public void StartupGateTable(bool internet, bool signedIn, bool aiConfigured, bool aiReachable, StartupBlockReason expected)
        {
            Assert.AreEqual(expected, ConnectionMonitor.EvaluateStartup(internet, signedIn, aiConfigured, aiReachable));
        }

        [Test]
        public void BlockReasonsHavePlayerCopy()
        {
            StringAssert.Contains("internet", ConnectionMonitor.DescribeBlock(StartupBlockReason.NoInternet));
            Assert.IsNotEmpty(ConnectionMonitor.DescribeBlock(StartupBlockReason.SignInFailed));
            Assert.IsNotEmpty(ConnectionMonitor.DescribeBlock(StartupBlockReason.AiUnreachable));
            Assert.IsEmpty(ConnectionMonitor.DescribeBlock(StartupBlockReason.None));
        }

        [TestCase(200L, false, true)]
        [TestCase(401L, false, true)]
        [TestCase(404L, false, true)]
        [TestCase(429L, false, true)]
        [TestCase(503L, false, true)]
        [TestCase(0L, false, false)]
        [TestCase(200L, true, false)]
        public void ServerReachedTable(long code, bool timedOut, bool expected)
        {
            Assert.AreEqual(expected, ConnectionMonitor.IsServerReached(code, timedOut));
        }

        [TestCase("https://api.groq.com/openai/v1/chat/completions", "https://api.groq.com/openai/v1/models")]
        [TestCase("https://api.groq.com/openai/v1/chat/completions/", "https://api.groq.com/openai/v1/models")]
        [TestCase("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1/models")]
        public void ProbeUrlCostsNoTokens(string chatUrl, string expected)
        {
            Assert.AreEqual(expected, ConnectionMonitor.ModelsUrlFor(chatUrl));
            Assert.IsNull(ConnectionMonitor.ModelsUrlFor(" "));
        }

        [Test]
        public void ReportsRouteToTheSinkAndAreSafeWithout()
        {
            ConnectionReports.Sink = null;
            ConnectionReports.Failure(ConnectionSource.Llm, "x"); // no sink: no-op

            GoOnline();
            ConnectionReports.Sink = _m;
            ConnectionReports.Outcome(ConnectionSource.CloudSave, reachedServer: false, "transport");
            Assert.AreEqual(ConnectionState.Degraded, _m.State);
            ConnectionReports.Outcome(ConnectionSource.CloudSave, reachedServer: true);
            Assert.AreEqual(ConnectionState.Online, _m.State);
            Assert.AreEqual(1, _m.TotalFailures);
        }
    }
}
