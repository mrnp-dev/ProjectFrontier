using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier.Farm
{
    public enum SoilState : byte
    {
        Untilled = 0,
        Tilled = 1,
        Watered = 2
    }

    public enum CropGrowthStage : byte
    {
        Empty = 0,
        Seedling = 1,
        Sprout = 2,
        Growing = 3,
        Mature = 4
    }

    public enum PlotActionKind
    {
        None = 0,
        Till = 1,
        Plant = 2,
        Water = 3,
        Harvest = 4,
        GrowingInfo = 5,
        NeedSeeds = 6
    }

    public struct PlotCell
    {
        public SoilState Soil;
        public string CropId;
        public float GrowthProgress;
        public int LastEvaluatedDay;
        public long LastEvaluatedUnixMs;

        public bool HasCrop => !string.IsNullOrEmpty(CropId);
        public bool IsMature => HasCrop && GrowthProgress >= 1f;

        public CropGrowthStage Stage
        {
            get
            {
                if (!HasCrop) return CropGrowthStage.Empty;
                if (GrowthProgress >= 1f) return CropGrowthStage.Mature;
                if (GrowthProgress >= 0.70f) return CropGrowthStage.Growing;
                if (GrowthProgress >= 0.30f) return CropGrowthStage.Sprout;
                return CropGrowthStage.Seedling;
            }
        }
    }

    public readonly struct PlotActionResult
    {
        public readonly PlotActionKind Action;
        public readonly bool Succeeded;
        public readonly string Message;
        public readonly string ItemId;
        public readonly int Quantity;

        public PlotActionResult(PlotActionKind action, bool succeeded, string message, string itemId = null, int quantity = 0)
        {
            Action = action;
            Succeeded = succeeded;
            Message = message;
            ItemId = itemId;
            Quantity = quantity;
        }
    }

    [Serializable]
    public sealed class PlotSaveEntry
    {
        public int x;
        public int y;
        public int soil;
        public string cropId;
        public float growth;
        public int lastDay;
        public long lastUnixMs;
    }

    [Serializable]
    public sealed class FarmSaveData
    {
        public int version = 1;
        public long savedAtUnixMs;
        public int lastDay;
        public InventorySaveData inventory = new InventorySaveData();
        public List<PlotSaveEntry> plots = new List<PlotSaveEntry>();
    }

    public readonly struct ChunkKey : IEquatable<ChunkKey>
    {
        public readonly int Cx;
        public readonly int Cy;

        public ChunkKey(int cx, int cy)
        {
            Cx = cx;
            Cy = cy;
        }

        public bool Equals(ChunkKey other) => Cx == other.Cx && Cy == other.Cy;
        public override bool Equals(object obj) => obj is ChunkKey other && Equals(other);
        public override int GetHashCode() => (Cx * 397) ^ Cy;
    }

    /// <summary>
    /// Fixed 4x4 flat-array chunk of Homestead crop plots. Avoids per-plot MonoBehaviours and per-frame ticks.
    /// </summary>
    public sealed class FarmChunk
    {
        public const int Size = 4;
        public const int CellCount = Size * Size;

        public ChunkKey Key { get; }
        public readonly PlotCell[] Cells = new PlotCell[CellCount];

        public FarmChunk(ChunkKey key)
        {
            Key = key;
        }

        public static int LocalIndex(int lx, int ly) => ly * Size + lx;
    }

    /// <summary>
    /// Chunked Homestead crop plot grid with timestamp-delta growth catch-up (TECH_SPEC §5).
    /// Zero per-frame tick cost: growth is evaluated only on day rollover, phase change, load, or direct interaction.
    /// </summary>
    public sealed class ChunkedFarmGrid
    {
        public const int DefaultChunksX = 2;
        public const int DefaultChunksY = 1;

        public int ChunksX { get; }
        public int ChunksY { get; }
        public int Width => ChunksX * FarmChunk.Size;
        public int Height => ChunksY * FarmChunk.Size;
        public int ActiveChunkCount => _chunks.Count;

        public event Action<int, int, PlotCell> PlotChanged;

        private readonly Dictionary<ChunkKey, FarmChunk> _chunks = new Dictionary<ChunkKey, FarmChunk>(4);

        public ChunkedFarmGrid(int chunksX = DefaultChunksX, int chunksY = DefaultChunksY)
        {
            ChunksX = Math.Max(1, chunksX);
            ChunksY = Math.Max(1, chunksY);
        }

        public bool InBounds(int gx, int gy) => gx >= 0 && gx < Width && gy >= 0 && gy < Height;

        public static ChunkKey ToChunkKey(int gx, int gy) => new ChunkKey(gx / FarmChunk.Size, gy / FarmChunk.Size);

        public PlotCell GetCell(int gx, int gy)
        {
            if (!InBounds(gx, gy)) return default;
            ChunkKey key = ToChunkKey(gx, gy);
            if (!_chunks.TryGetValue(key, out FarmChunk chunk)) return default;
            return chunk.Cells[FarmChunk.LocalIndex(gx % FarmChunk.Size, gy % FarmChunk.Size)];
        }

        public void ClearAll()
        {
            _chunks.Clear();
        }

        public bool Till(int gx, int gy, int currentDay, long nowUnixMs)
        {
            if (!InBounds(gx, gy)) return false;
            FarmChunk chunk = GetOrCreateChunk(gx, gy);
            int idx = FarmChunk.LocalIndex(gx % FarmChunk.Size, gy % FarmChunk.Size);
            ref PlotCell cell = ref chunk.Cells[idx];
            if (cell.Soil != SoilState.Untilled) return false;

            cell.Soil = SoilState.Tilled;
            cell.LastEvaluatedDay = Math.Max(1, currentDay);
            cell.LastEvaluatedUnixMs = nowUnixMs;
            PlotChanged?.Invoke(gx, gy, cell);
            return true;
        }

        public bool Plant(int gx, int gy, string seedItemId, InventoryState inventory, int currentDay, long nowUnixMs)
        {
            if (!InBounds(gx, gy) || inventory == null) return false;
            CropDef crop = ItemCatalog.GetCropForSeed(seedItemId);
            if (crop == null) return false;

            FarmChunk chunk = GetOrCreateChunk(gx, gy);
            int idx = FarmChunk.LocalIndex(gx % FarmChunk.Size, gy % FarmChunk.Size);
            ref PlotCell cell = ref chunk.Cells[idx];
            if (cell.Soil == SoilState.Untilled || cell.HasCrop) return false;
            if (!inventory.TryConsumeItem(seedItemId, 1)) return false;

            cell.CropId = crop.CropId;
            cell.GrowthProgress = 0f;
            cell.LastEvaluatedDay = Math.Max(1, currentDay);
            cell.LastEvaluatedUnixMs = nowUnixMs;
            PlotChanged?.Invoke(gx, gy, cell);
            return true;
        }

        public bool Water(int gx, int gy, int currentDay, long nowUnixMs)
        {
            if (!InBounds(gx, gy)) return false;
            FarmChunk chunk = GetOrCreateChunk(gx, gy);
            int idx = FarmChunk.LocalIndex(gx % FarmChunk.Size, gy % FarmChunk.Size);
            ref PlotCell cell = ref chunk.Cells[idx];
            if (cell.Soil != SoilState.Tilled) return false;

            cell.Soil = SoilState.Watered;
            cell.LastEvaluatedDay = Math.Max(1, currentDay);
            cell.LastEvaluatedUnixMs = nowUnixMs;
            PlotChanged?.Invoke(gx, gy, cell);
            return true;
        }

        public bool Harvest(int gx, int gy, InventoryState inventory, int currentDay, long nowUnixMs, out string harvestedItemId, out int yield)
        {
            harvestedItemId = null;
            yield = 0;
            if (!InBounds(gx, gy) || inventory == null) return false;
            CatchUpPlot(gx, gy, currentDay, nowUnixMs);

            ChunkKey key = ToChunkKey(gx, gy);
            if (!_chunks.TryGetValue(key, out FarmChunk chunk)) return false;
            int idx = FarmChunk.LocalIndex(gx % FarmChunk.Size, gy % FarmChunk.Size);
            ref PlotCell cell = ref chunk.Cells[idx];
            if (!cell.IsMature) return false;

            CropDef crop = ItemCatalog.GetCrop(cell.CropId);
            if (crop == null) return false;

            harvestedItemId = crop.HarvestItemId;
            yield = crop.HarvestYield;
            inventory.AddItem(harvestedItemId, yield);

            cell.CropId = null;
            cell.GrowthProgress = 0f;
            cell.Soil = SoilState.Tilled;
            cell.LastEvaluatedDay = Math.Max(1, currentDay);
            cell.LastEvaluatedUnixMs = nowUnixMs;
            PlotChanged?.Invoke(gx, gy, cell);
            return true;
        }

        /// <summary>
        /// Touch-first context action for a single plot: Untilled -> Till -> Plant -> Water -> Harvest.
        /// </summary>
        public PlotActionResult SmartInteract(int gx, int gy, InventoryState inventory, int currentDay, long nowUnixMs, string preferredSeedId = null)
        {
            if (!InBounds(gx, gy) || inventory == null)
                return new PlotActionResult(PlotActionKind.None, false, string.Empty);

            CatchUpPlot(gx, gy, currentDay, nowUnixMs);
            PlotCell cell = GetCell(gx, gy);

            if (cell.Soil == SoilState.Untilled)
            {
                bool ok = Till(gx, gy, currentDay, nowUnixMs);
                return new PlotActionResult(PlotActionKind.Till, ok, ok ? "TILLED SOIL" : string.Empty);
            }

            if (cell.IsMature)
            {
                if (Harvest(gx, gy, inventory, currentDay, nowUnixMs, out string itemId, out int qty))
                {
                    ItemDef def = ItemCatalog.GetItem(itemId);
                    string name = def != null ? def.DisplayName.ToUpperInvariant() : itemId;
                    return new PlotActionResult(PlotActionKind.Harvest, true, $"+{qty} {name}", itemId, qty);
                }
                return new PlotActionResult(PlotActionKind.Harvest, false, string.Empty);
            }

            if (!cell.HasCrop)
            {
                string seedId = inventory.ResolvePlantableSeed(preferredSeedId);
                if (string.IsNullOrEmpty(seedId))
                    return new PlotActionResult(PlotActionKind.NeedSeeds, false, "NEED SEEDS (PIP)");

                bool ok = Plant(gx, gy, seedId, inventory, currentDay, nowUnixMs);
                CropDef crop = ItemCatalog.GetCropForSeed(seedId);
                string label = crop != null ? crop.DisplayName.ToUpperInvariant() : "SEED";
                return new PlotActionResult(PlotActionKind.Plant, ok, ok ? $"PLANTED {label}" : string.Empty, seedId, 1);
            }

            if (cell.Soil == SoilState.Tilled)
            {
                bool ok = Water(gx, gy, currentDay, nowUnixMs);
                return new PlotActionResult(PlotActionKind.Water, ok, ok ? "WATERED" : string.Empty);
            }

            int pct = Mathf.Clamp(Mathf.RoundToInt(cell.GrowthProgress * 100f), 0, 99);
            return new PlotActionResult(PlotActionKind.GrowingInfo, false, $"GROWING {pct}%");
        }

        public PlotActionKind PeekAction(int gx, int gy, InventoryState inventory, int currentDay, long nowUnixMs, string preferredSeedId = null)
        {
            if (!InBounds(gx, gy)) return PlotActionKind.None;
            PlotCell cell = GetCell(gx, gy);
            if (cell.Soil == SoilState.Untilled) return PlotActionKind.Till;
            if (cell.IsMature) return PlotActionKind.Harvest;
            if (!cell.HasCrop)
            {
                if (inventory == null || string.IsNullOrEmpty(inventory.ResolvePlantableSeed(preferredSeedId)))
                    return PlotActionKind.NeedSeeds;
                return PlotActionKind.Plant;
            }
            if (cell.Soil == SoilState.Tilled) return PlotActionKind.Water;
            return PlotActionKind.GrowingInfo;
        }

        /// <summary>
        /// Evaluates timestamp-delta and day-skip growth across all allocated chunks.
        /// Returns the number of plots whose visible state changed.
        /// </summary>
        public int CatchUpAll(int currentDay, long nowUnixMs)
        {
            int changed = 0;
            foreach (var kv in _chunks)
            {
                FarmChunk chunk = kv.Value;
                int baseX = chunk.Key.Cx * FarmChunk.Size;
                int baseY = chunk.Key.Cy * FarmChunk.Size;
                for (int ly = 0; ly < FarmChunk.Size; ly++)
                for (int lx = 0; lx < FarmChunk.Size; lx++)
                {
                    int idx = FarmChunk.LocalIndex(lx, ly);
                    if (EvaluateCellCatchUp(ref chunk.Cells[idx], currentDay, nowUnixMs))
                    {
                        changed++;
                        PlotChanged?.Invoke(baseX + lx, baseY + ly, chunk.Cells[idx]);
                    }
                }
            }
            return changed;
        }

        public bool CatchUpPlot(int gx, int gy, int currentDay, long nowUnixMs)
        {
            if (!InBounds(gx, gy)) return false;
            ChunkKey key = ToChunkKey(gx, gy);
            if (!_chunks.TryGetValue(key, out FarmChunk chunk)) return false;
            int idx = FarmChunk.LocalIndex(gx % FarmChunk.Size, gy % FarmChunk.Size);
            if (EvaluateCellCatchUp(ref chunk.Cells[idx], currentDay, nowUnixMs))
            {
                PlotChanged?.Invoke(gx, gy, chunk.Cells[idx]);
                return true;
            }
            return false;
        }

        private static bool EvaluateCellCatchUp(ref PlotCell cell, int currentDay, long nowUnixMs)
        {
            if (cell.Soil == SoilState.Untilled) return false;

            int clampedDay = Math.Max(1, currentDay);
            int prevDay = cell.LastEvaluatedDay > 0 ? cell.LastEvaluatedDay : clampedDay;
            int dayDelta = Math.Max(0, clampedDay - prevDay);
            long prevMs = cell.LastEvaluatedUnixMs > 0 ? cell.LastEvaluatedUnixMs : nowUnixMs;
            double elapsedSec = nowUnixMs > prevMs ? (nowUnixMs - prevMs) / 1000.0 : 0.0;

            if (dayDelta == 0 && elapsedSec <= 0.001) return false;

            SoilState prevSoil = cell.Soil;
            CropGrowthStage prevStage = cell.Stage;
            float prevProgress = cell.GrowthProgress;

            if (cell.Soil == SoilState.Watered)
            {
                if (cell.HasCrop && cell.GrowthProgress < 1f)
                {
                    CropDef crop = ItemCatalog.GetCrop(cell.CropId);
                    if (crop != null)
                    {
                        // Watered soil fuels at most 1 day-step of overnight growth per watering,
                        // OR real-world offline elapsed time, whichever is larger.
                        float dayGain = dayDelta > 0 ? (1f / crop.DaysToGrow) : 0f;
                        float timeGain = elapsedSec > 0.0 ? (float)(elapsedSec / crop.OfflineGrowSeconds) : 0f;
                        // Cap single-watering offline gain to one day-step for multi-day crops so
                        // a 2-day crop still benefits from watering again unless fully grown.
                        float maxWateredGain = crop.DaysToGrow > 1 ? Math.Max(dayGain, Math.Min(timeGain, 1f / crop.DaysToGrow)) : Math.Max(dayGain, timeGain);
                        cell.GrowthProgress = Mathf.Clamp01(cell.GrowthProgress + maxWateredGain);
                    }
                }

                // Overnight rollover dries watered soil back to Tilled for the next morning.
                if (dayDelta > 0)
                {
                    cell.Soil = SoilState.Tilled;
                }
            }

            cell.LastEvaluatedDay = clampedDay;
            cell.LastEvaluatedUnixMs = nowUnixMs;

            return cell.Soil != prevSoil
                || cell.Stage != prevStage
                || Mathf.Abs(cell.GrowthProgress - prevProgress) >= 0.01f;
        }

        public void WriteToSaveData(List<PlotSaveEntry> target)
        {
            if (target == null) return;
            target.Clear();
            foreach (var kv in _chunks)
            {
                FarmChunk chunk = kv.Value;
                int baseX = chunk.Key.Cx * FarmChunk.Size;
                int baseY = chunk.Key.Cy * FarmChunk.Size;
                for (int ly = 0; ly < FarmChunk.Size; ly++)
                for (int lx = 0; lx < FarmChunk.Size; lx++)
                {
                    PlotCell cell = chunk.Cells[FarmChunk.LocalIndex(lx, ly)];
                    if (cell.Soil == SoilState.Untilled && !cell.HasCrop) continue;
                    target.Add(new PlotSaveEntry
                    {
                        x = baseX + lx,
                        y = baseY + ly,
                        soil = (int)cell.Soil,
                        cropId = cell.CropId,
                        growth = cell.GrowthProgress,
                        lastDay = cell.LastEvaluatedDay,
                        lastUnixMs = cell.LastEvaluatedUnixMs
                    });
                }
            }
        }

        public void LoadFromSaveData(List<PlotSaveEntry> entries)
        {
            _chunks.Clear();
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                PlotSaveEntry e = entries[i];
                if (e == null || !InBounds(e.x, e.y)) continue;
                FarmChunk chunk = GetOrCreateChunk(e.x, e.y);
                int idx = FarmChunk.LocalIndex(e.x % FarmChunk.Size, e.y % FarmChunk.Size);
                chunk.Cells[idx] = new PlotCell
                {
                    Soil = (SoilState)Mathf.Clamp(e.soil, 0, 2),
                    CropId = !string.IsNullOrEmpty(e.cropId) && ItemCatalog.GetCrop(e.cropId) != null ? e.cropId : null,
                    GrowthProgress = Mathf.Clamp01(e.growth),
                    LastEvaluatedDay = Math.Max(1, e.lastDay),
                    LastEvaluatedUnixMs = Math.Max(0L, e.lastUnixMs)
                };
            }
        }

        private FarmChunk GetOrCreateChunk(int gx, int gy)
        {
            ChunkKey key = ToChunkKey(gx, gy);
            if (!_chunks.TryGetValue(key, out FarmChunk chunk))
            {
                chunk = new FarmChunk(key);
                _chunks.Add(key, chunk);
            }
            return chunk;
        }
    }
}
