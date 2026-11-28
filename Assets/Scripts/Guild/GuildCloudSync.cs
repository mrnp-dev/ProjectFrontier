namespace ProjectFrontier.Guild
{
    public enum GuildReconcileAction
    {
        /// <summary>Cloud state is unknown (not signed in / load failed). Keep cloud writes blocked and retry later.</summary>
        Retry,
        /// <summary>Cloud is newer than the local save we booted from: load it locally (pre-reconcile actions are dropped).</summary>
        AdoptCloud,
        /// <summary>This device is ahead of (or equal to, with new progress) the cloud copy, or there is no cloud copy: push local up.</summary>
        PushLocal,
        /// <summary>Nothing to move; cloud writes may start.</summary>
        InSync
    }

    /// <summary>
    /// Pure newest-wins decision for the guild Cloud Save slot (#20). Lives outside the MonoBehaviour so the
    /// rules are EditMode-testable. Cloud writes must stay blocked until this returns anything but Retry.
    /// A cloud copy newer than the boot-time local save always wins, even if the player acted before the
    /// read succeeded: losing a few pre-reconcile actions beats overwriting a whole rank on a new/stale device.
    /// </summary>
    public static class GuildCloudSync
    {
        /// <param name="cloudLoadOk">False when the player is not signed in or the load threw.</param>
        /// <param name="hasCloud">A "guild" key exists in the cloud.</param>
        /// <param name="cloudSavedAt">Cloud snapshot timestamp (unix ms).</param>
        /// <param name="localSavedAt">Timestamp of the local save loaded at startup (0 = none / fresh install).</param>
        /// <param name="dirtySession">The player changed guild progress this session before reconcile finished.
        /// Only matters when the cloud is not newer (then that progress is pushed up).</param>
        public static GuildReconcileAction Decide(bool cloudLoadOk, bool hasCloud, long cloudSavedAt, long localSavedAt, bool dirtySession)
        {
            if (!cloudLoadOk) return GuildReconcileAction.Retry;

            if (!hasCloud)
                return localSavedAt > 0 || dirtySession ? GuildReconcileAction.PushLocal : GuildReconcileAction.InSync;

            // Cloud is newer than what this device booted from: adopt it regardless of dirtySession.
            if (cloudSavedAt > localSavedAt) return GuildReconcileAction.AdoptCloud;

            if (localSavedAt > cloudSavedAt || dirtySession) return GuildReconcileAction.PushLocal;
            return GuildReconcileAction.InSync;
        }
    }
}
