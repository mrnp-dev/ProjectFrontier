using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier.World
{
    /// <summary>
    /// Zero-allocation flat-array A* pathfinder with a preallocated binary min-heap and generational
    /// stamp counters (O(1) reset per query). Supports both one-shot queries and incremental
    /// node-budgeted stepping so schedule transitions never spike frame times on mobile CPUs.
    /// </summary>
    public sealed class FlatAStarPathfinder
    {
        private const float StraightCost = 1.0f;
        private const float DiagonalCost = 1.41421356f;

        private static readonly int[] DirX = { 1, -1, 0, 0, 1, -1, 1, -1 };
        private static readonly int[] DirY = { 0, 0, 1, -1, 1, 1, -1, -1 };
        private static readonly float[] DirCost =
        {
            StraightCost, StraightCost, StraightCost, StraightCost,
            DiagonalCost, DiagonalCost, DiagonalCost, DiagonalCost
        };

        private readonly NavGrid2D _grid;
        private readonly float[] _gScore;
        private readonly int[] _parent;
        private readonly int[] _openStamp;
        private readonly int[] _closedStamp;

        // Indexed binary min-heap: supports O(log N) decrease-key with zero allocations.
        private readonly int[] _heapNodes;
        private readonly float[] _heapPriority;
        private readonly int[] _heapPos;
        private readonly int[] _reversePath;
        private int _heapCount;
        private int _searchId;

        private int _startIdx = -1;
        private int _goalIdx = -1;
        private int _goalX;
        private int _goalY;
        private Vector2 _requestedGoalWorld;
        private bool _active;
        private bool _finished;
        private bool _found;

        public bool IsActive => _active;
        public bool IsFinished => _finished;
        public bool PathFound => _found;
        public NavGrid2D Grid => _grid;

        public FlatAStarPathfinder(NavGrid2D grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            int n = grid.CellCount;
            _gScore = new float[n];
            _parent = new int[n];
            _openStamp = new int[n];
            _closedStamp = new int[n];
            _heapNodes = new int[n];
            _heapPriority = new float[n];
            _heapPos = new int[n];
            _reversePath = new int[n];
        }

        /// <summary>Starts a new incremental search from startWorld to goalWorld.</summary>
        public void Begin(Vector2 startWorld, Vector2 goalWorld)
        {
            AdvanceSearchStamp();
            _heapCount = 0;
            _requestedGoalWorld = goalWorld;
            _active = false;
            _finished = false;
            _found = false;

            _grid.WorldToCell(startWorld, out int sx, out int sy);
            _grid.WorldToCell(goalWorld, out int gx, out int gy);

            _startIdx = _grid.FindNearestWalkableCell(sx, sy);
            _goalIdx = _grid.FindNearestWalkableCell(gx, gy);

            if (_startIdx < 0 || _goalIdx < 0)
            {
                _finished = true;
                _found = false;
                return;
            }

            _grid.FromIndex(_goalIdx, out _goalX, out _goalY);

            if (_startIdx == _goalIdx)
            {
                _parent[_startIdx] = -1;
                _finished = true;
                _found = true;
                return;
            }

            _grid.FromIndex(_startIdx, out int startX, out int startY);
            _gScore[_startIdx] = 0f;
            _parent[_startIdx] = -1;
            _openStamp[_startIdx] = _searchId;
            HeapPush(_startIdx, Heuristic(startX, startY, _goalX, _goalY));
            _active = true;
        }

        /// <summary>
        /// Expands up to <paramref name="maxExpansions"/> nodes of the active search.
        /// Returns the exact number of nodes expanded in this step.
        /// </summary>
        public int Step(int maxExpansions)
        {
            if (!_active || _finished) return 0;
            int budget = Math.Max(1, maxExpansions);
            int expanded = 0;
            int width = _grid.Width;

            while (_heapCount > 0 && expanded < budget)
            {
                int current = HeapPop();
                expanded++;

                if (current == _goalIdx)
                {
                    _active = false;
                    _finished = true;
                    _found = true;
                    return expanded;
                }

                _closedStamp[current] = _searchId;
                int cy = current / width;
                int cx = current - cy * width;
                float currentG = _gScore[current];

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DirX[d];
                    int ny = cy + DirY[d];
                    if (!_grid.IsWalkable(nx, ny)) continue;

                    // Prevent cutting across blocked obstacle corners on diagonals.
                    if (d >= 4 && (!_grid.IsWalkable(cx + DirX[d], cy) || !_grid.IsWalkable(cx, cy + DirY[d])))
                        continue;

                    int next = ny * width + nx;
                    if (_closedStamp[next] == _searchId) continue;

                    float tentativeG = currentG + DirCost[d];
                    if (_openStamp[next] != _searchId)
                    {
                        _openStamp[next] = _searchId;
                        _gScore[next] = tentativeG;
                        _parent[next] = current;
                        HeapPush(next, tentativeG + Heuristic(nx, ny, _goalX, _goalY));
                    }
                    else if (tentativeG < _gScore[next])
                    {
                        _gScore[next] = tentativeG;
                        _parent[next] = current;
                        HeapDecreaseKey(next, tentativeG + Heuristic(nx, ny, _goalX, _goalY));
                    }
                }
            }

            if (_heapCount == 0)
            {
                _active = false;
                _finished = true;
                _found = false;
            }
            return expanded;
        }

        /// <summary>
        /// Writes the found path into <paramref name="outWaypoints"/> with collinear points collapsed
        /// so NPCs glide cleanly without grid stair-step jitter.
        /// </summary>
        public void WritePath(List<Vector2> outWaypoints)
        {
            if (outWaypoints == null) throw new ArgumentNullException(nameof(outWaypoints));
            outWaypoints.Clear();
            if (!_found || _goalIdx < 0) return;

            if (_startIdx == _goalIdx)
            {
                outWaypoints.Add(_requestedGoalWorld);
                return;
            }

            int count = 0;
            int cursor = _goalIdx;
            int safety = _grid.CellCount;
            while (cursor >= 0 && count < safety)
            {
                _reversePath[count++] = cursor;
                if (cursor == _startIdx) break;
                cursor = _parent[cursor];
            }

            // Walk from start (count - 1) down to goal (0), collapsing collinear steps.
            int prevDx = 0;
            int prevDy = 0;
            _grid.FromIndex(_reversePath[count - 1], out int prevX, out int prevY);

            for (int i = count - 2; i >= 0; i--)
            {
                _grid.FromIndex(_reversePath[i], out int cx, out int cy);
                int dx = cx - prevX;
                int dy = cy - prevY;

                if (i < count - 2 && (dx != prevDx || dy != prevDy))
                {
                    outWaypoints.Add(_grid.CellToWorld(prevX, prevY));
                }
                prevDx = dx;
                prevDy = dy;
                prevX = cx;
                prevY = cy;
            }

            // End cleanly on the exact requested world coordinate when its cell is walkable.
            _grid.WorldToCell(_requestedGoalWorld, out int reqX, out int reqY);
            outWaypoints.Add(_grid.IsWalkable(reqX, reqY)
                ? _requestedGoalWorld
                : _grid.CellToWorld(_goalX, _goalY));
        }

        /// <summary>One-shot convenience wrapper (primarily for unit tests).</summary>
        public bool FindPath(Vector2 startWorld, Vector2 goalWorld, List<Vector2> outWaypoints, out int expansions)
        {
            Begin(startWorld, goalWorld);
            expansions = 0;
            while (!_finished)
            {
                expansions += Step(_grid.CellCount);
            }
            WritePath(outWaypoints);
            return _found;
        }

        private void AdvanceSearchStamp()
        {
            _searchId++;
            if (_searchId == int.MaxValue)
            {
                Array.Clear(_openStamp, 0, _openStamp.Length);
                Array.Clear(_closedStamp, 0, _closedStamp.Length);
                _searchId = 1;
            }
        }

        private static float Heuristic(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int min = dx < dy ? dx : dy;
            int max = dx > dy ? dx : dy;
            return (max - min) * StraightCost + min * DiagonalCost;
        }

        private void HeapPush(int node, float priority)
        {
            int i = _heapCount++;
            _heapNodes[i] = node;
            _heapPriority[i] = priority;
            _heapPos[node] = i;
            SiftUp(i);
        }

        private int HeapPop()
        {
            int top = _heapNodes[0];
            _heapCount--;
            if (_heapCount > 0)
            {
                int last = _heapNodes[_heapCount];
                float lastPrio = _heapPriority[_heapCount];
                _heapNodes[0] = last;
                _heapPriority[0] = lastPrio;
                _heapPos[last] = 0;
                SiftDown(0);
            }
            return top;
        }

        private void HeapDecreaseKey(int node, float newPriority)
        {
            int i = _heapPos[node];
            if (i < 0 || i >= _heapCount || _heapNodes[i] != node) return;
            _heapPriority[i] = newPriority;
            SiftUp(i);
        }

        private void SiftUp(int i)
        {
            int node = _heapNodes[i];
            float prio = _heapPriority[i];
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (prio >= _heapPriority[parent]) break;
                int pNode = _heapNodes[parent];
                _heapNodes[i] = pNode;
                _heapPriority[i] = _heapPriority[parent];
                _heapPos[pNode] = i;
                i = parent;
            }
            _heapNodes[i] = node;
            _heapPriority[i] = prio;
            _heapPos[node] = i;
        }

        private void SiftDown(int i)
        {
            int node = _heapNodes[i];
            float prio = _heapPriority[i];
            int half = _heapCount >> 1;
            while (i < half)
            {
                int child = (i << 1) + 1;
                int right = child + 1;
                if (right < _heapCount && _heapPriority[right] < _heapPriority[child])
                    child = right;
                if (prio <= _heapPriority[child]) break;
                int cNode = _heapNodes[child];
                _heapNodes[i] = cNode;
                _heapPriority[i] = _heapPriority[child];
                _heapPos[cNode] = i;
                i = child;
            }
            _heapNodes[i] = node;
            _heapPriority[i] = prio;
            _heapPos[node] = i;
        }
    }
}
