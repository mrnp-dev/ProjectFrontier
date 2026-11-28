using System;

namespace ProjectFrontier.Net
{
    /// <summary>Online-only connection state (#37).</summary>
    public enum ConnectionState
    {
        /// <summary>Startup gate running (internet, sign-in, AI endpoint). Gameplay can't start yet.</summary>
        Checking,
        /// <summary>Startup gate failed: "Connect to the internet to play" with Retry. Gameplay can't start.</summary>
        Blocked,
        /// <summary>Everything reachable.</summary>
        Online,
        /// <summary>Short drop: calls retry quietly and NPCs use built-in lines. The game keeps running.</summary>
        Degraded,
        /// <summary>Long drop: the game is paused behind "Reconnecting..." until a call or probe succeeds.</summary>
        Reconnecting
    }

    /// <summary>Who reported a connection outcome (for logs and the last-failure label).</summary>
    public enum ConnectionSource
    {
        Llm,
        CloudSave,
        Auth,
        RemoteConfig,
        Leaderboards,
        Probe,
        Reachability
    }

    /// <summary>Why the startup gate refused to enter gameplay.</summary>
    public enum StartupBlockReason
    {
        None,
        NoInternet,
        SignInFailed,
        AiUnreachable
    }

    /// <summary>Receives connection outcomes. Implemented by <see cref="ConnectionMonitor"/>.</summary>
    public interface IConnectionSink
    {
        void ReportSuccess(ConnectionSource source);
        void ReportFailure(ConnectionSource source, string reason);
    }

    /// <summary>
    /// Static entry point every LLM and UGS caller reports into. Forwards to the active monitor; a no-op when there is
    /// none (EditMode tests, tools). Allocation-free: reasons are passed through, never formatted here.
    /// </summary>
    public static class ConnectionReports
    {
        public static IConnectionSink Sink { get; set; }

        public static void Success(ConnectionSource source) => Sink?.ReportSuccess(source);

        public static void Failure(ConnectionSource source, string reason) => Sink?.ReportFailure(source, reason);

        /// <summary>Reports success when <paramref name="reachedServer"/> is true, otherwise a failure.</summary>
        public static void Outcome(ConnectionSource source, bool reachedServer, string reason = null)
        {
            if (reachedServer) Success(source);
            else Failure(source, reason);
        }
    }

    /// <summary>Tunables. Defaults are the values proposed in the #37 PR.</summary>
    public sealed class ConnectionSettings
    {
        /// <summary>Continuous trouble longer than this pauses the game behind "Reconnecting...".</summary>
        public double LongDropSeconds = 12.0;
        /// <summary>How often a cheap probe runs while degraded or reconnecting (and the network interface is up).</summary>
        public double ProbeIntervalSeconds = 3.0;
    }

    /// <summary>
    /// Pure state machine for the online-only rules (#37): online -> degraded on the first failed call or lost
    /// interface, degraded -> reconnecting after <see cref="ConnectionSettings.LongDropSeconds"/> of continuous trouble,
    /// any successful call or probe -> online. "Reachable" from the OS only means an interface is up, so it can start a
    /// drop but never ends one; only a real server response does.
    /// Time is passed in (seconds, any monotonic clock) so EditMode tests drive it directly. No allocations per call.
    /// Reports may arrive from any thread; state changes are serialised with a lock and read by the owner each frame.
    /// </summary>
    public sealed class ConnectionMonitor : IConnectionSink
    {
        private readonly object _gate = new object();
        private readonly Func<double> _clock;

        public ConnectionSettings Settings { get; }
        public ConnectionState State { get; private set; } = ConnectionState.Checking;
        public StartupBlockReason BlockReason { get; private set; } = StartupBlockReason.None;
        /// <summary>When the current drop started (degraded or reconnecting), or -1.</summary>
        public double TroubleSince { get; private set; } = -1;
        public double LastProbeAt { get; private set; } = double.NegativeInfinity;
        public bool InterfaceReachable { get; private set; } = true;
        public int ConsecutiveFailures { get; private set; }
        public ConnectionSource LastFailureSource { get; private set; }
        public string LastFailureReason { get; private set; }
        /// <summary>Bumped on every state change so the owner can react with a cheap int compare.</summary>
        public int Version { get; private set; }
        public int TotalFailures { get; private set; }
        public int ReconnectingEpisodes { get; private set; }

        public ConnectionMonitor(Func<double> clock, ConnectionSettings settings = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Settings = settings ?? new ConnectionSettings();
        }

        /// <summary>Gameplay may start (title -> run) only when the gate passed and the game isn't held.</summary>
        public bool CanStartGameplay => State == ConnectionState.Online || State == ConnectionState.Degraded;

        /// <summary>The game clock, timers and input are frozen while this is true.</summary>
        public bool IsHoldingGame => State == ConnectionState.Reconnecting;

        /// <summary>
        /// Whether callers should try the network at all. False while reconnecting or with no interface, so NPC turns
        /// answer instantly from built-in lines instead of waiting for a timeout.
        /// </summary>
        public bool AllowsNetworkCalls =>
            InterfaceReachable && State != ConnectionState.Reconnecting && State != ConnectionState.Blocked;

        public void BeginStartupCheck()
        {
            lock (_gate)
            {
                BlockReason = StartupBlockReason.None;
                SetState(ConnectionState.Checking);
            }
        }

        public void CompleteStartupCheck(StartupBlockReason result)
        {
            lock (_gate)
            {
                if (State != ConnectionState.Checking) return;
                BlockReason = result;
                if (result == StartupBlockReason.None)
                {
                    ClearTrouble();
                    SetState(ConnectionState.Online);
                }
                else
                {
                    SetState(ConnectionState.Blocked);
                }
            }
        }

        public void ReportSuccess(ConnectionSource source)
        {
            lock (_gate)
            {
                ConsecutiveFailures = 0;
                if (source != ConnectionSource.Reachability) InterfaceReachable = true;
                if (State == ConnectionState.Degraded || State == ConnectionState.Reconnecting)
                {
                    ClearTrouble();
                    SetState(ConnectionState.Online);
                }
            }
        }

        public void ReportFailure(ConnectionSource source, string reason)
        {
            lock (_gate)
            {
                ConsecutiveFailures++;
                TotalFailures++;
                LastFailureSource = source;
                LastFailureReason = reason;
                if (State == ConnectionState.Online) StartTrouble();
            }
        }

        /// <summary>OS interface state (Application.internetReachability), polled at low frequency.</summary>
        public void ReportInterfaceReachable(bool reachable)
        {
            lock (_gate)
            {
                if (InterfaceReachable == reachable) return;
                InterfaceReachable = reachable;
                if (!reachable)
                {
                    LastFailureSource = ConnectionSource.Reachability;
                    LastFailureReason = "no network interface";
                    if (State == ConnectionState.Online) StartTrouble();
                }
            }
        }

        /// <summary>Advances time-based rules. Call once per poll; returns true when a probe should run now.</summary>
        public bool Tick()
        {
            lock (_gate)
            {
                double now = _clock();
                if (State == ConnectionState.Degraded && TroubleSince >= 0 && now - TroubleSince >= Settings.LongDropSeconds)
                {
                    ReconnectingEpisodes++;
                    SetState(ConnectionState.Reconnecting);
                }

                bool troubled = State == ConnectionState.Degraded || State == ConnectionState.Reconnecting;
                if (!troubled || !InterfaceReachable) return false;
                if (now - LastProbeAt < Settings.ProbeIntervalSeconds) return false;
                LastProbeAt = now;
                return true;
            }
        }

        /// <summary>Seconds the current drop has lasted (0 when online).</summary>
        public double TroubleSeconds
        {
            get
            {
                lock (_gate) return TroubleSince < 0 ? 0 : Math.Max(0, _clock() - TroubleSince);
            }
        }

        private void StartTrouble()
        {
            TroubleSince = _clock();
            LastProbeAt = TroubleSince; // the failing call just told us; wait one interval before probing
            SetState(ConnectionState.Degraded);
        }

        private void ClearTrouble()
        {
            TroubleSince = -1;
            ConsecutiveFailures = 0;
        }

        private void SetState(ConnectionState next)
        {
            if (State == next) return;
            State = next;
            Version++;
        }

        /// <summary>
        /// Startup gate rule: internet first, then UGS sign-in, then the AI endpoint when one is configured. Builds with
        /// no AI endpoint (no key until #36's build-time key / #38's proxy) pass on internet + sign-in and use built-in lines.
        /// </summary>
        public static StartupBlockReason EvaluateStartup(bool internetReachable, bool signedIn, bool aiConfigured, bool aiReachable)
        {
            if (!internetReachable) return StartupBlockReason.NoInternet;
            if (!signedIn) return StartupBlockReason.SignInFailed;
            if (aiConfigured && !aiReachable) return StartupBlockReason.AiUnreachable;
            return StartupBlockReason.None;
        }

        /// <summary>Player-facing copy for the startup screen.</summary>
        public static string DescribeBlock(StartupBlockReason reason)
        {
            switch (reason)
            {
                case StartupBlockReason.NoInternet: return "Connect to the internet to play.";
                case StartupBlockReason.SignInFailed: return "Can't reach the game servers. Check your connection.";
                case StartupBlockReason.AiUnreachable: return "Can't reach the villagers right now. Check your connection.";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// True when an HTTP outcome means the server was reached: any status code counts (401, 404, 429 quota included),
        /// only "no response" (code 0: DNS, refused, airplane mode) or a timeout is a connection failure.
        /// </summary>
        public static bool IsServerReached(long httpStatusCode, bool timedOut) => !timedOut && httpStatusCode > 0;

        /// <summary>".../v1/chat/completions" -> ".../v1/models": a probe GET that costs no tokens (401 without a key is fine).</summary>
        public static string ModelsUrlFor(string chatCompletionsUrl)
        {
            if (string.IsNullOrWhiteSpace(chatCompletionsUrl)) return null;
            const string suffix = "/chat/completions";
            string b = chatCompletionsUrl.Trim().TrimEnd('/');
            if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) b = b.Substring(0, b.Length - suffix.Length);
            return b + "/models";
        }
    }
}
