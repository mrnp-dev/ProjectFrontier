using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier.World
{
    /// <summary>
    /// Frame-budgeted A* path request queue so simultaneous NPC schedule transitions
    /// (e.g., all 5 villagers switching spots at Evening) never spike mobile frame times.
    /// Caps both completed requests and total A* node expansions per frame, resuming
    /// unfinished searches across subsequent frames and coalescing duplicate requester IDs.
    /// </summary>
    public sealed class FrameBudgetedNavQueue
    {
        public const int DefaultMaxRequestsPerFrame = 2;
        public const int DefaultMaxNodeExpansionsPerFrame = 128;

        private struct NavRequest
        {
            public string RequesterId;
            public Vector2 Start;
            public Vector2 Goal;
            public List<Vector2> Buffer;
            public Action<bool> OnComplete;
        }

        private readonly FlatAStarPathfinder _pathfinder;
        private readonly List<NavRequest> _pending = new List<NavRequest>(16);
        private bool _hasActive;
        private NavRequest _active;

        public int MaxRequestsPerFrame { get; set; }
        public int MaxNodeExpansionsPerFrame { get; set; }

        public int PendingCount => _pending.Count;
        public bool HasActiveSearch => _hasActive;
        public bool IsIdle => !_hasActive && _pending.Count == 0;
        public int LastFrameExpansions { get; private set; }
        public int LastFrameCompletedRequests { get; private set; }
        public int TotalCompletedRequests { get; private set; }
        public int PeakFrameExpansions { get; private set; }

        public FrameBudgetedNavQueue(
            FlatAStarPathfinder pathfinder,
            int maxRequestsPerFrame = DefaultMaxRequestsPerFrame,
            int maxNodeExpansionsPerFrame = DefaultMaxNodeExpansionsPerFrame)
        {
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            MaxRequestsPerFrame = Math.Max(1, maxRequestsPerFrame);
            MaxNodeExpansionsPerFrame = Math.Max(1, maxNodeExpansionsPerFrame);
        }

        /// <summary>
        /// Enqueues a path query. If <paramref name="requesterId"/> already has a queued request,
        /// its start/goal/callback are updated in place instead of creating duplicate work.
        /// </summary>
        public void Enqueue(string requesterId, Vector2 start, Vector2 goal, List<Vector2> outBuffer, Action<bool> onComplete)
        {
            if (string.IsNullOrEmpty(requesterId)) throw new ArgumentException("requesterId is required", nameof(requesterId));
            if (outBuffer == null) throw new ArgumentNullException(nameof(outBuffer));

            var req = new NavRequest
            {
                RequesterId = requesterId,
                Start = start,
                Goal = goal,
                Buffer = outBuffer,
                OnComplete = onComplete
            };

            // If this requester is currently being searched, cancel the stale active search.
            if (_hasActive && _active.RequesterId == requesterId)
            {
                _hasActive = false;
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].RequesterId == requesterId)
                {
                    _pending[i] = req;
                    return;
                }
            }

            _pending.Add(req);
        }

        public void Cancel(string requesterId)
        {
            if (string.IsNullOrEmpty(requesterId)) return;
            if (_hasActive && _active.RequesterId == requesterId) _hasActive = false;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].RequesterId == requesterId) _pending.RemoveAt(i);
            }
        }

        public void Clear()
        {
            _hasActive = false;
            _pending.Clear();
        }

        /// <summary>
        /// Advances queued navigation work within this frame's request and node-expansion budgets.
        /// Call once per frame from the scene's day/schedule manager.
        /// </summary>
        public void ProcessFrameBudget()
        {
            LastFrameExpansions = 0;
            LastFrameCompletedRequests = 0;

            int reqBudget = Math.Max(1, MaxRequestsPerFrame);
            int nodeBudget = Math.Max(1, MaxNodeExpansionsPerFrame);

            while (LastFrameCompletedRequests < reqBudget && LastFrameExpansions < nodeBudget)
            {
                if (!_hasActive)
                {
                    if (_pending.Count == 0) break;
                    _active = _pending[0];
                    _pending.RemoveAt(0);
                    _pathfinder.Begin(_active.Start, _active.Goal);
                    _hasActive = true;
                }

                int remainingNodes = nodeBudget - LastFrameExpansions;
                if (!_pathfinder.IsFinished)
                {
                    LastFrameExpansions += _pathfinder.Step(remainingNodes);
                }

                if (_pathfinder.IsFinished)
                {
                    _pathfinder.WritePath(_active.Buffer);
                    bool found = _pathfinder.PathFound;
                    Action<bool> cb = _active.OnComplete;
                    _hasActive = false;
                    LastFrameCompletedRequests++;
                    TotalCompletedRequests++;
                    cb?.Invoke(found);
                }
                else
                {
                    // Exhausted node budget for this frame; resume next frame.
                    break;
                }
            }

            if (LastFrameExpansions > PeakFrameExpansions)
                PeakFrameExpansions = LastFrameExpansions;
        }
    }
}
