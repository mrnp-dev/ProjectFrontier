using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProjectFrontier.Net;

namespace ProjectFrontier.Net.Tests
{
    /// <summary>
    /// #48: Farm and Social must read their Cloud Save slot before writing it. Scenario under test: signed out at
    /// startup (read fails), player keeps playing (local saves), then signs in via the startup gate's Retry.
    /// </summary>
    public class CloudSyncGateTests
    {
        // Fakes complete inline; a null context keeps awaits in the gate from posting to the Editor's sync context.
        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_savedContext);

        private sealed class Snap
        {
            public long savedAt;
            public string tag;
        }

        /// <summary>Fake Cloud Save + clock. Fakes complete synchronously unless a TaskCompletionSource is queued.</summary>
        private sealed class Harness
        {
            public bool SignedIn;
            public bool LiveOps = true;
            public Snap Cloud;
            public bool SaveSucceeds = true;
            public double Now;
            public long LocalSavedAt;
            public string LocalTag = "local";
            public readonly List<Snap> Writes = new List<Snap>();
            public readonly List<string> Log = new List<string>();
            public Snap Adopted;
            public int Reads;
            public TaskCompletionSource<bool> PendingSave;
            public TaskCompletionSource<(bool, bool, Snap)> PendingLoad;

            public CloudSyncGate<Snap> Build(long startupLocalSavedAt, double interval = 30)
            {
                LocalSavedAt = startupLocalSavedAt;
                return new CloudSyncGate<Snap>("Test",
                    Load,
                    Save,
                    () => new Snap { savedAt = LocalSavedAt, tag = LocalTag },
                    s => s.savedAt,
                    s => { Adopted = s; LocalSavedAt = s.savedAt; LocalTag = s.tag; Log.Add("adopt"); },
                    () => LiveOps,
                    () => Now,
                    startupLocalSavedAt,
                    interval);
            }

            private Task<(bool ok, bool found, Snap value)> Load()
            {
                Reads++;
                Log.Add("read");
                if (PendingLoad != null) return PendingLoad.Task.ContinueWith(t => t.Result, TaskContinuationOptions.ExecuteSynchronously);
                if (!SignedIn) return Task.FromResult((false, false, (Snap)null));
                return Task.FromResult((true, Cloud != null, Cloud));
            }

            private Task<bool> Save(Snap s)
            {
                Log.Add("write");
                if (!SignedIn) return Task.FromResult(false);
                if (PendingSave != null)
                {
                    var tcs = PendingSave;
                    return tcs.Task.ContinueWith(t =>
                    {
                        if (t.Result) { Writes.Add(s); Cloud = s; }
                        return t.Result;
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
                if (!SaveSucceeds) return Task.FromResult(false);
                Writes.Add(s);
                Cloud = s;
                return Task.FromResult(true);
            }

            /// <summary>Simulates a local save in the manager (SaveNow): stamp + request a cloud write.</summary>
            public void LocalSave(CloudSyncGate<Snap> gate, long at, string tag, bool force)
            {
                LocalSavedAt = at;
                LocalTag = tag;
                gate.RequestWrite(force);
            }
        }

        // ------------------------------------------------------------------ Decide (pure rule, mirrors GuildCloudSync)

        [Test]
        public void Decide_FailedRead_Retries()
        {
            Assert.AreEqual(CloudReconcileAction.Retry, CloudReconcile.Decide(false, true, 500, 100, true));
        }

        [Test]
        public void Decide_NewerCloud_AdoptedEvenAfterPreReconcileActions()
        {
            Assert.AreEqual(CloudReconcileAction.AdoptCloud, CloudReconcile.Decide(true, true, 500, 100, false));
            Assert.AreEqual(CloudReconcileAction.AdoptCloud, CloudReconcile.Decide(true, true, 500, 100, true));
            Assert.AreEqual(CloudReconcileAction.AdoptCloud, CloudReconcile.Decide(true, true, 500, 0, true), "fresh install, cloud exists");
        }

        [Test]
        public void Decide_LocalAheadOrNoCloud_Pushes()
        {
            Assert.AreEqual(CloudReconcileAction.PushLocal, CloudReconcile.Decide(true, true, 100, 500, false));
            Assert.AreEqual(CloudReconcileAction.PushLocal, CloudReconcile.Decide(true, false, 0, 500, false));
            Assert.AreEqual(CloudReconcileAction.PushLocal, CloudReconcile.Decide(true, false, 0, 0, true));
            Assert.AreEqual(CloudReconcileAction.PushLocal, CloudReconcile.Decide(true, true, 100, 100, true));
        }

        [Test]
        public void Decide_EqualOrEmpty_InSync()
        {
            Assert.AreEqual(CloudReconcileAction.InSync, CloudReconcile.Decide(true, true, 100, 100, false));
            Assert.AreEqual(CloudReconcileAction.InSync, CloudReconcile.Decide(true, false, 0, 0, false));
        }

        // ------------------------------------------------------------------ the #48 scenario

        [Test]
        public void SignedOutAtStart_SaveBeforeRead_DoesNotWrite()
        {
            var h = new Harness { SignedIn = false, Cloud = new Snap { savedAt = 900, tag = "cloud" } };
            var gate = h.Build(startupLocalSavedAt: 100);

            gate.ReconcileAsync().GetAwaiter().GetResult();
            Assert.IsFalse(gate.IsReconciled);
            Assert.AreEqual(CloudReconcileAction.Retry, gate.LastAction);

            h.LocalSave(gate, 200, "played", force: true);   // e.g. pause/quit
            h.LocalSave(gate, 210, "played2", force: false);

            CollectionAssert.DoesNotContain(h.Log, "write", "no cloud write may happen before a successful read");
            Assert.IsTrue(gate.HasPendingWrite);
            Assert.IsTrue(gate.ActedBeforeReconcile);
        }

        [Test]
        public void RetrySignIn_ReadsBeforeWriting_AndAdoptsNewerCloud()
        {
            var h = new Harness { SignedIn = false, Cloud = new Snap { savedAt = 900, tag = "cloud" } };
            var gate = h.Build(startupLocalSavedAt: 100);
            gate.ReconcileRetrySeconds = 10;

            gate.ReconcileAsync().GetAwaiter().GetResult();          // signed out at start
            h.LocalSave(gate, 200, "played", force: true);

            h.SignedIn = true;                    // startup gate's Retry succeeds
            h.Now = 5;
            gate.Tick();                          // too early: throttled
            Assert.AreEqual(1, h.Reads);
            h.Now = 11;
            gate.Tick();                          // retry runs (fake completes synchronously)

            Assert.IsTrue(gate.IsReconciled);
            Assert.AreEqual(CloudReconcileAction.AdoptCloud, gate.LastAction);
            Assert.AreEqual("cloud", h.Adopted?.tag);
            CollectionAssert.IsEmpty(h.Writes, "stale local state must not overwrite newer cloud progress");
            Assert.IsFalse(gate.HasPendingWrite);
        }

        [Test]
        public void RetrySignIn_LocalAhead_PushesOnlyAfterRead()
        {
            var h = new Harness { SignedIn = false, Cloud = new Snap { savedAt = 50, tag = "old-cloud" } };
            var gate = h.Build(startupLocalSavedAt: 100);
            gate.ReconcileRetrySeconds = 10;

            gate.ReconcileAsync().GetAwaiter().GetResult();
            h.LocalSave(gate, 300, "played", force: false);
            h.SignedIn = true;
            h.Now = 10;
            gate.Tick();

            Assert.IsTrue(gate.IsReconciled);
            Assert.AreEqual(CloudReconcileAction.PushLocal, gate.LastAction);
            CollectionAssert.AreEqual(new[] { "read", "read", "write" }, h.Log);
            Assert.AreEqual(1, h.Writes.Count);
            Assert.AreEqual("played", h.Writes[0].tag);
            Assert.AreEqual(300, h.Writes[0].savedAt);
        }

        [Test]
        public void RetrySignIn_NoCloudYet_PushesSessionProgressAfterRead()
        {
            var h = new Harness { SignedIn = false, Cloud = null };
            var gate = h.Build(startupLocalSavedAt: 0);   // fresh install

            gate.ReconcileAsync().GetAwaiter().GetResult();
            h.LocalSave(gate, 300, "first-harvest", force: false);
            h.SignedIn = true;
            gate.ReconcileAsync().GetAwaiter().GetResult();

            Assert.AreEqual(CloudReconcileAction.PushLocal, gate.LastAction);
            Assert.AreEqual(1, h.Writes.Count);
            Assert.AreEqual("first-harvest", h.Writes[0].tag);
            Assert.AreEqual(0, h.Log.IndexOf("read"), "the read comes first");
        }

        [Test]
        public void WriteRequestDuringInFlightRead_WaitsForRead()
        {
            var h = new Harness { SignedIn = true, Cloud = new Snap { savedAt = 900, tag = "cloud" } };
            var gate = h.Build(startupLocalSavedAt: 100);
            h.PendingLoad = new TaskCompletionSource<(bool, bool, Snap)>();

            Task reconcile = gate.ReconcileAsync();
            Assert.IsTrue(gate.IsReconcileInFlight);
            h.LocalSave(gate, 200, "played", force: true);
            gate.Tick();
            CollectionAssert.DoesNotContain(h.Log, "write");

            h.PendingLoad.SetResult((true, true, h.Cloud));
            Assert.IsTrue(reconcile.IsCompleted);
            reconcile.GetAwaiter().GetResult();

            Assert.AreEqual(CloudReconcileAction.AdoptCloud, gate.LastAction);
            CollectionAssert.IsEmpty(h.Writes);
        }

        [Test]
        public void LiveOpsOff_NoReadNoWrite_UntilEnabled()
        {
            var h = new Harness { SignedIn = true, LiveOps = false, Cloud = null };
            var gate = h.Build(startupLocalSavedAt: 100);

            gate.ReconcileAsync().GetAwaiter().GetResult();
            h.LocalSave(gate, 200, "played", force: true);
            Assert.AreEqual(0, h.Reads);
            CollectionAssert.IsEmpty(h.Writes);

            h.LiveOps = true;
            h.Now = 100;
            gate.Tick();
            Assert.IsTrue(gate.IsReconciled);
            Assert.AreEqual(1, h.Writes.Count);
        }

        [Test]
        public void ReadThatThrows_IsRetryNotWrite()
        {
            int calls = 0;
            var writes = 0;
            var gate = new CloudSyncGate<Snap>("Test",
                () => { calls++; throw new InvalidOperationException("boom"); },
                s => { writes++; return Task.FromResult(true); },
                () => new Snap(), s => s.savedAt, s => { }, () => true, () => 0, 100, 0);

            gate.ReconcileAsync().GetAwaiter().GetResult();
            gate.RequestWrite(true);

            Assert.AreEqual(1, calls);
            Assert.IsFalse(gate.IsReconciled);
            Assert.AreEqual(0, writes);
        }

        // ------------------------------------------------------------------ after reconcile: normal saves still work (#33)

        [Test]
        public void AfterReconcile_ForcedWritesImmediately_UnforcedThrottled()
        {
            var h = new Harness { SignedIn = true, Cloud = new Snap { savedAt = 100, tag = "same" } };
            var gate = h.Build(startupLocalSavedAt: 100, interval: 30);
            gate.ReconcileAsync().GetAwaiter().GetResult();
            Assert.AreEqual(CloudReconcileAction.InSync, gate.LastAction);
            CollectionAssert.IsEmpty(h.Writes);

            h.Now = 1;
            h.LocalSave(gate, 200, "a", force: false);   // first write after reconcile is due (never written yet)
            Assert.AreEqual(1, h.Writes.Count);

            h.Now = 5;
            h.LocalSave(gate, 210, "b", force: false);   // throttled
            Assert.AreEqual(1, h.Writes.Count);

            h.LocalSave(gate, 220, "c", force: true);    // pause/quit
            Assert.AreEqual(2, h.Writes.Count);
            Assert.AreEqual("c", h.Writes[1].tag);
        }

        [Test]
        public void FailedWrite_StaysPending_AndRetriesOnInterval()
        {
            var h = new Harness { SignedIn = true, Cloud = null, SaveSucceeds = false };
            var gate = h.Build(startupLocalSavedAt: 100, interval: 30);
            gate.ReconcileAsync().GetAwaiter().GetResult();   // push attempted, fails
            Assert.IsTrue(gate.HasPendingWrite);
            CollectionAssert.IsEmpty(h.Writes);

            h.SaveSucceeds = true;
            h.Now = 10;
            gate.Tick();
            CollectionAssert.IsEmpty(h.Writes, "retry waits for the interval");
            h.Now = 31;
            gate.Tick();
            Assert.AreEqual(1, h.Writes.Count);
            Assert.IsFalse(gate.HasPendingWrite);
        }

        [Test]
        public void RequestDuringInFlightWrite_RunsOneTrailingWriteOfLatestState()
        {
            var h = new Harness { SignedIn = true, Cloud = new Snap { savedAt = 100 } };
            var gate = h.Build(startupLocalSavedAt: 100, interval: 0);
            gate.ReconcileAsync().GetAwaiter().GetResult();

            h.PendingSave = new TaskCompletionSource<bool>();
            h.LocalSave(gate, 200, "first", force: true);
            Assert.IsTrue(gate.IsWriteInFlight);
            h.LocalSave(gate, 210, "second", force: true);
            h.LocalSave(gate, 220, "third", force: true);

            var first = h.PendingSave;
            h.PendingSave = null;
            first.SetResult(true);   // null sync context: the pump's continuation runs inline here

            Assert.AreEqual(2, h.Writes.Count, "one in-flight write + one trailing write");
            Assert.AreEqual("third", h.Writes[1].tag);
            Assert.IsFalse(gate.HasPendingWrite);
        }

        [Test]
        public void Tick_WhenReconciledAndIdle_DoesNothing()
        {
            var h = new Harness { SignedIn = true, Cloud = null };
            var gate = h.Build(startupLocalSavedAt: 0);
            gate.ReconcileAsync().GetAwaiter().GetResult();
            Assert.AreEqual(CloudReconcileAction.InSync, gate.LastAction);
            int reads = h.Reads;
            h.Now = 1000;
            gate.Tick();
            Assert.AreEqual(reads, h.Reads);
            CollectionAssert.IsEmpty(h.Writes);
        }
    }
}
