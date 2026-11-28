using System;
using System.Threading.Tasks;

namespace ProjectFrontier.Net
{
    /// <summary>What to do once the cloud copy of a save slot has been read (or failed to read).</summary>
    public enum CloudReconcileAction
    {
        /// <summary>The read failed (signed out, offline, LiveOps off): keep cloud writes blocked and retry later.</summary>
        Retry,
        /// <summary>The cloud copy is newer than the local save this device booted from: load it.</summary>
        AdoptCloud,
        /// <summary>This device is ahead (or the cloud has nothing): push local up.</summary>
        PushLocal,
        InSync
    }

    /// <summary>Pure reconcile rule shared by Farm and Social (#48); same rule as <c>GuildCloudSync.Decide</c> (#28).</summary>
    public static class CloudReconcile
    {
        /// <param name="cloudLoadOk">False when the player is not signed in, LiveOps is off, or the load threw.</param>
        /// <param name="hasCloud">The key exists in the cloud.</param>
        /// <param name="cloudSavedAt">Cloud snapshot timestamp (unix ms).</param>
        /// <param name="localSavedAt">Timestamp of the local save loaded at startup (0 = none / fresh install).</param>
        /// <param name="actedBeforeReconcile">Progress was saved this session before the read finished. Only decides
        /// whether to push when the cloud is not newer; a newer cloud copy is adopted regardless.</param>
        public static CloudReconcileAction Decide(bool cloudLoadOk, bool hasCloud, long cloudSavedAt, long localSavedAt, bool actedBeforeReconcile)
        {
            if (!cloudLoadOk) return CloudReconcileAction.Retry;
            if (!hasCloud)
                return localSavedAt > 0 || actedBeforeReconcile ? CloudReconcileAction.PushLocal : CloudReconcileAction.InSync;
            if (cloudSavedAt > localSavedAt) return CloudReconcileAction.AdoptCloud;
            if (localSavedAt > cloudSavedAt || actedBeforeReconcile) return CloudReconcileAction.PushLocal;
            return CloudReconcileAction.InSync;
        }
    }

    /// <summary>
    /// Read-before-write gate for one Cloud Save slot (#48). Pure C# (delegates for load/save/clock) so EditMode tests
    /// can drive "signed out at start, sign in via Retry, then no cloud write before a successful read".
    /// <list type="bullet">
    /// <item>Cloud writes are blocked until a read of the slot succeeds (<see cref="IsReconciled"/>).</item>
    /// <item>A failed read (signed out, offline, LiveOps off) is retried on <see cref="ReconcileRetrySeconds"/> from <see cref="Tick"/>.</item>
    /// <item>Write requests are never dropped: requested while blocked, failed, or mid-flight, they stay pending and run
    /// as one trailing write of the current state.</item>
    /// </list>
    /// </summary>
    public sealed class CloudSyncGate<T> where T : class
    {
        public const double DefaultReconcileRetrySeconds = 10.0;

        private readonly string _tag;
        private readonly Func<Task<(bool ok, bool found, T value)>> _load;
        private readonly Func<T, Task<bool>> _save;
        private readonly Func<T> _snapshot;
        private readonly Func<T, long> _savedAt;
        private readonly Action<T> _adopt;
        private readonly Func<bool> _writesAllowed;
        private readonly Func<double> _clock;
        private readonly Func<bool> _alive;
        private readonly Action<string> _warn;
        private readonly long _startupLocalSavedAt;

        private double _lastWriteAt = double.NegativeInfinity;
        private double _lastReconcileAttempt = double.NegativeInfinity;

        public double WriteIntervalSeconds { get; set; }
        public double ReconcileRetrySeconds { get; set; } = DefaultReconcileRetrySeconds;

        public bool IsReconciled { get; private set; }
        public bool IsReconcileInFlight { get; private set; }
        public bool IsWriteInFlight { get; private set; }
        public bool HasPendingWrite { get; private set; }
        public bool ActedBeforeReconcile { get; private set; }
        public CloudReconcileAction LastAction { get; private set; } = CloudReconcileAction.Retry;
        public string LastStatus { get; private set; } = "Local cache";
        public int ReadAttempts { get; private set; }
        public int WriteAttempts { get; private set; }

        /// <param name="tag">Log prefix, e.g. "Farm".</param>
        /// <param name="load">Reads the slot: ok=false when signed out / offline / threw.</param>
        /// <param name="save">Writes the slot: false on failure.</param>
        /// <param name="snapshot">Current state to push (stamped with the last local save time).</param>
        /// <param name="savedAt">Timestamp of a cloud snapshot.</param>
        /// <param name="adopt">Applies a newer cloud snapshot to the live state and the local file.</param>
        /// <param name="writesAllowed">LiveOps gate; when false nothing is read or written (stays pending).</param>
        /// <param name="clock">Monotonic seconds (realtimeSinceStartup in the game).</param>
        /// <param name="startupLocalSavedAt">Timestamp of the local save the device booted from (0 = none).</param>
        /// <param name="writeIntervalSeconds">Throttle between non-forced cloud writes.</param>
        /// <param name="alive">False once the owner is destroyed; results arriving after that are ignored.</param>
        public CloudSyncGate(string tag, Func<Task<(bool ok, bool found, T value)>> load, Func<T, Task<bool>> save,
            Func<T> snapshot, Func<T, long> savedAt, Action<T> adopt, Func<bool> writesAllowed, Func<double> clock,
            long startupLocalSavedAt, double writeIntervalSeconds, Func<bool> alive = null, Action<string> warn = null)
        {
            _tag = tag ?? "Save";
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _save = save ?? throw new ArgumentNullException(nameof(save));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _savedAt = savedAt ?? throw new ArgumentNullException(nameof(savedAt));
            _adopt = adopt ?? throw new ArgumentNullException(nameof(adopt));
            _writesAllowed = writesAllowed ?? (() => true);
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _alive = alive ?? (() => true);
            _warn = warn;
            _startupLocalSavedAt = Math.Max(0, startupLocalSavedAt);
            WriteIntervalSeconds = Math.Max(0, writeIntervalSeconds);
        }

        /// <summary>
        /// Call after each local save. Queues a cloud write when <paramref name="force"/>d or the interval has passed.
        /// Before reconcile the write only stays pending (and is pushed or discarded by the reconcile decision).
        /// </summary>
        public void RequestWrite(bool force)
        {
            if (!IsReconciled) ActedBeforeReconcile = true;
            bool due = force || HasPendingWrite || _clock() - _lastWriteAt >= WriteIntervalSeconds;
            if (!due) return;
            HasPendingWrite = true;
            if (IsReconciled && !IsWriteInFlight) _ = PumpAsync();
        }

        /// <summary>Cheap per-frame check: retries an unfinished reconcile, then a pending write, on their intervals.</summary>
        public void Tick()
        {
            if (IsReconciled && !HasPendingWrite) return;
            double now = _clock();
            if (!IsReconciled)
            {
                if (!IsReconcileInFlight && now - _lastReconcileAttempt >= ReconcileRetrySeconds) _ = ReconcileAsync();
                return;
            }
            if (!IsWriteInFlight && now - _lastWriteAt >= WriteIntervalSeconds) _ = PumpAsync();
        }

        /// <summary>Reads the slot and applies <see cref="CloudReconcile.Decide"/>. No-op once reconciled or while a read runs.</summary>
        public async Task ReconcileAsync()
        {
            if (IsReconciled || IsReconcileInFlight) return;
            _lastReconcileAttempt = _clock();
            if (!_writesAllowed()) return;
            IsReconcileInFlight = true;
            try
            {
                ReadAttempts++;
                (bool ok, bool found, T value) r;
                try
                {
                    r = await _load();
                }
                catch (Exception e)
                {
                    _warn?.Invoke($"[{_tag}] Cloud read threw: {e.Message}");
                    r = (false, false, null);
                }
                if (!_alive()) return;
                bool hasCloud = r.ok && r.found && r.value != null;
                CloudReconcileAction action = CloudReconcile.Decide(r.ok, hasCloud, hasCloud ? _savedAt(r.value) : 0,
                    _startupLocalSavedAt, ActedBeforeReconcile);
                LastAction = action;
                switch (action)
                {
                    case CloudReconcileAction.Retry:
                        return;   // _lastReconcileAttempt throttles the Tick retry
                    case CloudReconcileAction.AdoptCloud:
                        _adopt(r.value);
                        HasPendingWrite = false;   // the stale local state must not overwrite the newer cloud copy
                        LastStatus = "Cloud loaded";
                        break;
                    case CloudReconcileAction.PushLocal:
                        HasPendingWrite = true;
                        break;
                    case CloudReconcileAction.InSync:
                        LastStatus = "Cloud synced";
                        break;
                }
                ActedBeforeReconcile = false;
                IsReconciled = true;
                if (HasPendingWrite) await PumpAsync();
            }
            catch (Exception e)
            {
                _warn?.Invoke($"[{_tag}] Cloud reconcile failed: {e.Message}");
            }
            finally
            {
                IsReconcileInFlight = false;
            }
        }

        /// <summary>Writes pending state, looping so a request made mid-flight runs as a trailing write. Never runs before reconcile.</summary>
        public async Task PumpAsync()
        {
            if (IsWriteInFlight || !IsReconciled) return;
            IsWriteInFlight = true;
            try
            {
                while (HasPendingWrite && _alive())
                {
                    if (!_writesAllowed())
                    {
                        _lastWriteAt = _clock();   // stays pending; retry on the interval
                        break;
                    }
                    HasPendingWrite = false;
                    _lastWriteAt = _clock();
                    WriteAttempts++;
                    bool ok;
                    try
                    {
                        ok = await _save(_snapshot());
                    }
                    catch (Exception e)
                    {
                        _warn?.Invoke($"[{_tag}] Cloud save threw: {e.Message}");
                        ok = false;
                    }
                    if (!_alive()) return;
                    if (!ok)
                    {
                        HasPendingWrite = true;
                        LastStatus = "Local cache (cloud retry pending)";
                        break;
                    }
                    LastStatus = "Cloud synced";
                }
            }
            finally
            {
                IsWriteInFlight = false;
            }
        }
    }
}
