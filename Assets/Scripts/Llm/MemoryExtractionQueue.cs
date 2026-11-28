using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ProjectFrontier.Llm
{
    /// <summary>One finished conversation waiting for its memory extraction (#45).</summary>
    public sealed class PendingExtraction
    {
        public string NpcId;
        /// <summary>Snapshot of the whole conversation (the live history is cleared when the dialogue closes).</summary>
        public List<ChatMessage> Conversation;
        /// <summary>Game day of the conversation, so queued facts keep the day they were said.</summary>
        public int Day;
        public string Mood;
        /// <summary>Calls that reached the provider code and failed without a server answer.</summary>
        public int Attempts;
    }

    /// <summary>
    /// In-memory queue of extractions that couldn't run because the connection was down (#45). Pure C#, no engine calls.
    /// Bounded at <see cref="Capacity"/> conversations (oldest dropped first) and <see cref="MaxAttempts"/> tries each.
    /// Only no-connection failures are queued: a timeout may already have cost tokens, so it isn't retried.
    /// Not saved: anything still waiting is lost if the game quits during an outage (accepted by product).
    /// </summary>
    public sealed class MemoryExtractionQueue
    {
        public const int DefaultCapacity = 6;
        public const int DefaultMaxAttempts = 3;

        public enum Outcome { Done, Requeued, GaveUp }

        public int Capacity { get; }
        public int MaxAttempts { get; }
        public int Count => _items.Count;
        /// <summary>Conversations dropped because the queue was full.</summary>
        public int Dropped { get; private set; }
        /// <summary>Conversations abandoned after <see cref="MaxAttempts"/> no-connection failures.</summary>
        public int GaveUp { get; private set; }

        private readonly LinkedList<PendingExtraction> _items = new LinkedList<PendingExtraction>();

        public MemoryExtractionQueue(int capacity = DefaultCapacity, int maxAttempts = DefaultMaxAttempts)
        {
            Capacity = Math.Max(1, capacity);
            MaxAttempts = Math.Max(1, maxAttempts);
        }

        /// <summary>True when <paramref name="result"/> failed only because no server could be reached (worth retrying later).</summary>
        public static bool ShouldQueue(MemoryExtractionResult result) => result != null && result.NoConnection && !result.TimedOut;

        /// <summary>Adds to the back. When full, the oldest waiting conversation is dropped. Returns false for null/empty input.</summary>
        public bool Enqueue(PendingExtraction item)
        {
            if (item == null || string.IsNullOrEmpty(item.NpcId) || item.Conversation == null || item.Conversation.Count == 0) return false;
            while (_items.Count >= Capacity)
            {
                _items.RemoveFirst();
                Dropped++;
            }
            _items.AddLast(item);
            return true;
        }

        /// <summary>Removes and returns the oldest waiting conversation, or null.</summary>
        public PendingExtraction TakeNext()
        {
            if (_items.Count == 0) return null;
            PendingExtraction first = _items.First.Value;
            _items.RemoveFirst();
            return first;
        }

        /// <summary>
        /// Records the result of a run of <paramref name="item"/>. A no-connection failure is queued again until
        /// <see cref="MaxAttempts"/> provider attempts; anything else is done. An item taken with <see cref="TakeNext"/>
        /// (<paramref name="fromQueue"/>) goes back to the front to keep order; a fresh conversation goes to the back.
        /// A skipped call (network not allowed, provider never reached) doesn't use up an attempt.
        /// </summary>
        public Outcome Complete(PendingExtraction item, MemoryExtractionResult result, bool fromQueue = true)
        {
            if (item == null || !ShouldQueue(result)) return Outcome.Done;
            if (result.CalledProvider) item.Attempts++;
            if (item.Attempts >= MaxAttempts)
            {
                GaveUp++;
                return Outcome.GaveUp;
            }
            if (!fromQueue) return Enqueue(item) ? Outcome.Requeued : Outcome.Done;
            if (_items.Count >= Capacity)
            {
                Dropped++;
                return Outcome.GaveUp;
            }
            _items.AddFirst(item);
            return Outcome.Requeued;
        }

        public void Clear() => _items.Clear();

        /// <summary>
        /// Runs fire-and-forget extraction work so an early failure (a throw before the first await, a faulted task)
        /// is logged instead of vanishing (#45). Returns null on failure. Cancellation is passed through silently.
        /// </summary>
        public static async Task<MemoryExtractionResult> RunSafelyAsync(Func<Task<MemoryExtractionResult>> work, Action<string> logError)
        {
            try
            {
                if (work == null) return null;
                Task<MemoryExtractionResult> task = work();
                return task == null ? null : await task;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                logError?.Invoke($"[Memory] extraction failed: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Memory log lines that contain what the player or villagers said (#45). Compiled out of release builds, so the
    /// text (and the string building) never reaches a release log, like #41's chat transcript line.
    /// </summary>
    public static class MemoryLog
    {
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Text(string message) => UnityEngine.Debug.Log(message);
    }
}
