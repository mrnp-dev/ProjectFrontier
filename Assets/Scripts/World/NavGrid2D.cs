using System;
using UnityEngine;

namespace ProjectFrontier.World
{
    /// <summary>
    /// Flat-array 2D walkability grid for Oakhaven NPC navigation.
    /// Uses 0.5-unit cells (~8 px @ 16 PPU) across [-13, 13] x [-8, 8] (52 x 32 = 1,664 cells).
    /// </summary>
    public sealed class NavGrid2D
    {
        public const float DefaultCellSize = 0.5f;
        public static readonly Vector2 DefaultOrigin = new Vector2(-13f, -8f);
        public const int DefaultWidth = 52;
        public const int DefaultHeight = 32;

        private readonly byte[] _blocked;

        public Vector2 Origin { get; }
        public int Width { get; }
        public int Height { get; }
        public float CellSize { get; }
        public int CellCount => Width * Height;

        public NavGrid2D(Vector2 origin, int width, int height, float cellSize = DefaultCellSize)
        {
            Origin = origin;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            CellSize = Mathf.Max(0.1f, cellSize);
            _blocked = new byte[Width * Height];
        }

        public static NavGrid2D CreateOakhavenDefault() =>
            new NavGrid2D(DefaultOrigin, DefaultWidth, DefaultHeight, DefaultCellSize);

        public void ClearAll() => Array.Clear(_blocked, 0, _blocked.Length);

        public bool InBounds(int cx, int cy) => cx >= 0 && cx < Width && cy >= 0 && cy < Height;

        public int ToIndex(int cx, int cy) => cy * Width + cx;

        public void FromIndex(int idx, out int cx, out int cy)
        {
            cy = idx / Width;
            cx = idx - cy * Width;
        }

        public bool IsWalkable(int cx, int cy) => InBounds(cx, cy) && _blocked[cy * Width + cx] == 0;

        public bool IsWalkableIndex(int idx) => idx >= 0 && idx < _blocked.Length && _blocked[idx] == 0;

        public void SetBlocked(int cx, int cy, bool blocked)
        {
            if (InBounds(cx, cy)) _blocked[cy * Width + cx] = blocked ? (byte)1 : (byte)0;
        }

        public void WorldToCell(Vector2 worldPos, out int cx, out int cy)
        {
            cx = Mathf.Clamp(Mathf.FloorToInt((worldPos.x - Origin.x) / CellSize), 0, Width - 1);
            cy = Mathf.Clamp(Mathf.FloorToInt((worldPos.y - Origin.y) / CellSize), 0, Height - 1);
        }

        public Vector2 CellToWorld(int cx, int cy)
        {
            return new Vector2(
                Origin.x + (cx + 0.5f) * CellSize,
                Origin.y + (cy + 0.5f) * CellSize);
        }

        /// <summary>Marks all cells intersecting the padded world-space AABB as blocked.</summary>
        public void MarkBlockedRect(float minX, float minY, float maxX, float maxY, float padding = 0f)
        {
            float x0 = minX - padding;
            float y0 = minY - padding;
            float x1 = maxX + padding;
            float y1 = maxY + padding;

            int cx0 = Mathf.Clamp(Mathf.FloorToInt((x0 - Origin.x) / CellSize), 0, Width - 1);
            int cy0 = Mathf.Clamp(Mathf.FloorToInt((y0 - Origin.y) / CellSize), 0, Height - 1);
            int cx1 = Mathf.Clamp(Mathf.FloorToInt((x1 - Origin.x) / CellSize), 0, Width - 1);
            int cy1 = Mathf.Clamp(Mathf.FloorToInt((y1 - Origin.y) / CellSize), 0, Height - 1);

            for (int y = cy0; y <= cy1; y++)
            for (int x = cx0; x <= cx1; x++)
            {
                _blocked[y * Width + x] = 1;
            }
        }

        /// <summary>
        /// Finds the nearest walkable cell to (cx, cy) within <paramref name="maxRadius"/> using concentric rings.
        /// Returns -1 if no walkable cell exists in range.
        /// </summary>
        public int FindNearestWalkableCell(int cx, int cy, int maxRadius = 8)
        {
            cx = Mathf.Clamp(cx, 0, Width - 1);
            cy = Mathf.Clamp(cy, 0, Height - 1);
            if (IsWalkable(cx, cy)) return ToIndex(cx, cy);

            int bestIdx = -1;
            int bestDistSq = int.MaxValue;
            for (int r = 1; r <= maxRadius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (!IsWalkable(nx, ny)) continue;
                    int dSq = dx * dx + dy * dy;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = ToIndex(nx, ny);
                    }
                }
                if (bestIdx >= 0) return bestIdx;
            }
            return -1;
        }
    }
}
