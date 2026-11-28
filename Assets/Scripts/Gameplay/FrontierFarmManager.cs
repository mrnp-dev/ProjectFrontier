using System;
using System.IO;
using System.Threading.Tasks;
using ProjectFrontier.Farm;
using ProjectFrontier.Social;
using ProjectFrontier.World;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Scene owner of the West Homestead chunked crop plots, player inventory/gold/recipes/buffs,
    /// zero-per-frame-cost timestamp-delta catch-up, event-driven plot sprites, and local+Cloud persistence (#19).
    /// </summary>
    public class FrontierFarmManager : MonoBehaviour
    {
        public static FrontierFarmManager Instance { get; private set; }

        public const string CloudKey = "farm";
        public const string LocalFileName = "frontier_farm.json";

        [Header("Homestead Grid Layout")]
        public Vector2 plotGridOrigin = new Vector2(-21.5f, -3.5f);
        public float plotCellSize = 1.0f;
        public int chunksX = ChunkedFarmGrid.DefaultChunksX;
        public int chunksY = ChunkedFarmGrid.DefaultChunksY;
        public Transform plotsRoot;
        public SpriteRenderer plotCursorRenderer;
        public Material spriteLitMaterial;
        public Material spriteUnlitMaterial;

        [Header("Baked 16-PPU Sprites (optional; falls back to PixelArtFactory)")]
        public Sprite soilUntilledSprite;
        public Sprite soilTilledSprite;
        public Sprite soilWateredSprite;
        public Sprite cropStage1Sprite;
        public Sprite cropStage2Sprite;
        public Sprite cropStage3Sprite;
        public Sprite cropMoonmintMatureSprite;
        public Sprite cropSunpetalMatureSprite;
        public Sprite cropEmberrootMatureSprite;
        public Sprite plotCursorSprite;

        [Header("Persistence")]
        public float cloudSaveIntervalSeconds = 30f;

        public ChunkedFarmGrid Grid { get; private set; }
        public InventoryState Inventory { get; private set; }
        public string LastSaveStatus { get; private set; } = "Local cache";

        public long NowUnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _debugClockOffsetMs;
        public int CurrentDay => FrontierDayCycleManager.Instance != null
            ? FrontierDayCycleManager.Instance.CurrentDay
            : (FrontierSocialManager.Instance != null ? FrontierSocialManager.Instance.State.Day : 1);

        public event Action<string> StatusFeedback;
        public event Action<string, int> CropHarvested;
        public event Action<string, int> ItemCrafted;

        private SpriteRenderer[] _soilRenderers;
        private SpriteRenderer[] _cropRenderers;
        private FrontierDayCycleManager _dayCycle;
        private string _localPath;
        private bool _dirty;
        private long _localSavedAt;
        private float _lastCloudSaveTime = -999f;
        private bool _cloudSaveInFlight;
        private long _debugClockOffsetMs;
        private float _buffPruneTimer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            EnsureSprites();
            Grid = new ChunkedFarmGrid(Mathf.Max(1, chunksX), Mathf.Max(1, chunksY));
            Inventory = new InventoryState(seedStarterKit: true);

            _localPath = Path.Combine(Application.persistentDataPath, LocalFileName);
            FarmSaveData local = ReadLocal();
            if (local != null)
            {
                Inventory.LoadFrom(local.inventory);
                Grid.LoadFromSaveData(local.plots);
                _localSavedAt = local.savedAtUnixMs;
            }

            Grid.PlotChanged += HandlePlotChanged;
            Inventory.Changed += HandleInventoryChanged;

            BindOrCreatePlotVisuals();
            // Evaluate offline timestamp-delta catch-up once on load (zero per-frame cost).
            int caughtUp = Grid.CatchUpAll(CurrentDay, NowUnixMs);
            Inventory.PruneExpiredBuffs(NowUnixMs);
            RefreshAllPlotVisuals();
            if (caughtUp > 0) _dirty = true;
        }

        private async void Start()
        {
            _dayCycle = FrontierDayCycleManager.Instance;
            if (_dayCycle != null)
            {
                _dayCycle.DayAdvanced += HandleDayAdvanced;
                _dayCycle.PhaseChanged += HandlePhaseChanged;
                // Re-sync against synced day count in case DayClock synced after Awake.
                if (Grid.CatchUpAll(CurrentDay, NowUnixMs) > 0) _dirty = true;
            }
            await ReconcileWithCloudAsync();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (Grid != null) Grid.PlotChanged -= HandlePlotChanged;
            if (Inventory != null) Inventory.Changed -= HandleInventoryChanged;
            if (_dayCycle != null)
            {
                _dayCycle.DayAdvanced -= HandleDayAdvanced;
                _dayCycle.PhaseChanged -= HandlePhaseChanged;
            }
            Instance = null;
        }

        private void Update()
        {
            // Zero per-frame crop ticks! Only prune active potion buffs at 1 Hz when a buff is running.
            if (Inventory == null || Inventory.ActiveBuffs.Count == 0) return;
            _buffPruneTimer += Time.unscaledDeltaTime;
            if (_buffPruneTimer >= 1f)
            {
                _buffPruneTimer = 0f;
                Inventory.PruneExpiredBuffs(NowUnixMs);
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) SaveNow(forceCloud: true);
        }

        private void OnApplicationQuit() => SaveNow(forceCloud: true);

        // ------------------------------------------------------------------ world targeting

        public Vector3 PlotToWorld(int gx, int gy)
        {
            return new Vector3(
                plotGridOrigin.x + gx * plotCellSize,
                plotGridOrigin.y + gy * plotCellSize,
                0f);
        }

        public bool TryGetTargetPlot(Vector2 playerPos, out int gx, out int gy)
        {
            gx = -1;
            gy = -1;
            if (Grid == null) return false;

            float minX = plotGridOrigin.x - plotCellSize * 1.05f;
            float maxX = plotGridOrigin.x + (Grid.Width - 1) * plotCellSize + plotCellSize * 1.05f;
            float minY = plotGridOrigin.y - plotCellSize * 1.05f;
            float maxY = plotGridOrigin.y + (Grid.Height - 1) * plotCellSize + plotCellSize * 1.05f;
            if (playerPos.x < minX || playerPos.x > maxX || playerPos.y < minY || playerPos.y > maxY)
                return false;

            int cx = Mathf.Clamp(Mathf.RoundToInt((playerPos.x - plotGridOrigin.x) / plotCellSize), 0, Grid.Width - 1);
            int cy = Mathf.Clamp(Mathf.RoundToInt((playerPos.y - plotGridOrigin.y) / plotCellSize), 0, Grid.Height - 1);
            Vector3 cellPos = PlotToWorld(cx, cy);
            float distSq = ((Vector2)cellPos - playerPos).sqrMagnitude;
            if (distSq > (plotCellSize * 1.35f) * (plotCellSize * 1.35f))
                return false;

            gx = cx;
            gy = cy;
            return true;
        }

        public void UpdatePlotCursor(bool visible, int gx, int gy)
        {
            if (plotCursorRenderer == null) return;
            if (!visible || Grid == null || !Grid.InBounds(gx, gy))
            {
                if (plotCursorRenderer.enabled) plotCursorRenderer.enabled = false;
                return;
            }
            plotCursorRenderer.transform.position = PlotToWorld(gx, gy);
            if (!plotCursorRenderer.enabled) plotCursorRenderer.enabled = true;
        }

        public PlotActionKind PeekPlotAction(int gx, int gy)
        {
            return Grid != null ? Grid.PeekAction(gx, gy, Inventory, CurrentDay, NowUnixMs) : PlotActionKind.None;
        }

        public static string GetPlotActionLabel(PlotActionKind kind) => kind switch
        {
            PlotActionKind.Till => "TILL",
            PlotActionKind.Plant => "PLANT",
            PlotActionKind.Water => "WATER",
            PlotActionKind.Harvest => "HARVEST",
            PlotActionKind.NeedSeeds => "SEEDS?",
            PlotActionKind.GrowingInfo => "CHECK",
            _ => "FARM"
        };

        // ------------------------------------------------------------------ gameplay actions

        public PlotActionResult InteractWithPlot(int gx, int gy)
        {
            if (Grid == null || Inventory == null)
                return new PlotActionResult(PlotActionKind.None, false, string.Empty);

            PlotActionResult result = Grid.SmartInteract(gx, gy, Inventory, CurrentDay, NowUnixMs);
            Vector3 worldPos = PlotToWorld(gx, gy);

            if (result.Succeeded)
            {
                _dirty = true;
                PlayerController2D.Instance?.Motion?.PlayPickupPop();
                if (result.Action == PlotActionKind.Harvest)
                {
                    SynthAudioManager.Instance?.PlayPickup();
                    FeelManager.Instance?.PlayPickupFeedback(worldPos + Vector3.up * 0.25f, result.Message);
                    CropHarvested?.Invoke(result.ItemId, result.Quantity);
                }
                else
                {
                    SynthAudioManager.Instance?.PlayUiClick();
                    FeelManager.Instance?.PlayPickupFeedback(worldPos + Vector3.up * 0.15f, result.Message);
                }
                SaveNow(forceCloud: false);
            }
            else if (!string.IsNullOrEmpty(result.Message))
            {
                SynthAudioManager.Instance?.PlayUiClick();
                FeelManager.Instance?.PlayPickupFeedback(worldPos + Vector3.up * 0.2f, result.Message);
            }

            if (!string.IsNullOrEmpty(result.Message))
                StatusFeedback?.Invoke(result.Message);

            return result;
        }

        public RelationshipState GetPipStanding()
        {
            var social = FrontierSocialManager.Instance;
            return social != null ? social.Get("pip").State : RelationshipState.Neutral;
        }

        public bool CraftRecipe(string recipeId, out string message)
        {
            message = string.Empty;
            if (Inventory == null) return false;

            if (!Inventory.TryCraft(recipeId, out ItemDef crafted))
            {
                RecipeDef def = ItemCatalog.GetRecipe(recipeId);
                message = def != null && !Inventory.IsRecipeUnlocked(def.Id)
                    ? "RECIPE LOCKED · BUY AT PIP'S"
                    : "NOT ENOUGH HERBS";
                StatusFeedback?.Invoke(message);
                return false;
            }

            _dirty = true;
            SynthAudioManager.Instance?.PlayPickup();
            message = $"CRAFTED {crafted.DisplayName.ToUpperInvariant()}";
            if (PlayerController2D.Instance != null)
            {
                PlayerController2D.Instance.Motion?.PlayPickupPop();
                FeelManager.Instance?.PlayPickupFeedback(PlayerController2D.Instance.transform.position + Vector3.up * 0.5f, message);
            }
            SaveNow(forceCloud: false);
            ItemCrafted?.Invoke(crafted.Id, 1);
            StatusFeedback?.Invoke(message);
            return true;
        }

        public bool BuySeedFromPip(string seedItemId, int quantity, out string message)
        {
            message = string.Empty;
            if (Inventory == null) return false;

            RelationshipState standing = GetPipStanding();
            if (!Inventory.TryBuySeed(seedItemId, quantity, standing, out int cost))
            {
                message = "NOT ENOUGH GOLD";
                StatusFeedback?.Invoke(message);
                return false;
            }

            _dirty = true;
            SynthAudioManager.Instance?.PlayPickup();
            ItemDef item = ItemCatalog.GetItem(seedItemId);
            string name = item != null ? item.DisplayName.ToUpperInvariant() : seedItemId;
            message = $"BOUGHT {quantity}X {name} (-{cost}G)";
            SaveNow(forceCloud: false);
            StatusFeedback?.Invoke(message);
            return true;
        }

        public bool BuyRecipeFromPip(string recipeId, out string message)
        {
            message = string.Empty;
            if (Inventory == null) return false;

            RelationshipState standing = GetPipStanding();
            if (!Inventory.TryBuyRecipe(recipeId, standing, out int cost))
            {
                message = Inventory.IsRecipeUnlocked(recipeId) ? "ALREADY UNLOCKED" : "NOT ENOUGH GOLD";
                StatusFeedback?.Invoke(message);
                return false;
            }

            _dirty = true;
            SynthAudioManager.Instance?.PlayMilestone();
            RecipeDef r = ItemCatalog.GetRecipe(recipeId);
            message = $"UNLOCKED {(r != null ? r.DisplayName.ToUpperInvariant() : recipeId)} (-{cost}G)";
            SaveNow(forceCloud: false);
            StatusFeedback?.Invoke(message);
            return true;
        }

        public bool SellItemToPip(string itemId, int quantity, out string message)
        {
            message = string.Empty;
            if (Inventory == null) return false;

            if (!Inventory.TrySellItem(itemId, quantity, out int earned))
            {
                message = "NONE TO SELL";
                StatusFeedback?.Invoke(message);
                return false;
            }

            _dirty = true;
            SynthAudioManager.Instance?.PlayPickup();
            ItemDef item = ItemCatalog.GetItem(itemId);
            string name = item != null ? item.DisplayName.ToUpperInvariant() : itemId;
            message = $"SOLD {quantity}X {name} (+{earned}G)";
            SaveNow(forceCloud: false);
            StatusFeedback?.Invoke(message);
            return true;
        }

        public bool UsePotion(string potionItemId, out string message)
        {
            message = string.Empty;
            if (Inventory == null) return false;

            if (!Inventory.TryUsePotion(potionItemId, NowUnixMs, out ItemDef used))
            {
                message = "NO POTION IN BAG";
                StatusFeedback?.Invoke(message);
                return false;
            }

            _dirty = true;
            SynthAudioManager.Instance?.PlayPickup();
            message = used.Buff switch
            {
                BuffKind.Heal => $"HEALED +{Mathf.RoundToInt(used.BuffMagnitude)} HP",
                BuffKind.Swiftness => $"SWIFTNESS +{Mathf.RoundToInt(used.BuffMagnitude * 100f)}% ({Mathf.RoundToInt(used.BuffDurationSeconds)}S)",
                BuffKind.EmberMight => $"EMBER MIGHT +{Mathf.RoundToInt(used.BuffMagnitude * 100f)}% ({Mathf.RoundToInt(used.BuffDurationSeconds)}S)",
                _ => $"USED {used.DisplayName.ToUpperInvariant()}"
            };
            if (PlayerController2D.Instance != null)
            {
                PlayerController2D.Instance.Motion?.PlayPickupPop();
                FeelManager.Instance?.PlayPickupFeedback(PlayerController2D.Instance.transform.position + Vector3.up * 0.5f, message);
            }
            SaveNow(forceCloud: false);
            StatusFeedback?.Invoke(message);
            return true;
        }

        public float GetMovementSpeedMultiplier()
        {
            if (Inventory == null || Inventory.ActiveBuffs.Count == 0) return 1f;
            return 1f + Inventory.GetBuffBonus(BuffKind.Swiftness, NowUnixMs);
        }

        public float GetCombatMightMultiplier()
        {
            if (Inventory == null || Inventory.ActiveBuffs.Count == 0) return 1f;
            return 1f + Inventory.GetBuffBonus(BuffKind.EmberMight, NowUnixMs);
        }

        /// <summary>Advances the offline timestamp clock and runs timestamp-delta catch-up immediately (for verification).</summary>
        public int ForceAdvanceOfflineSeconds(double seconds)
        {
            if (seconds <= 0.0 || Grid == null) return 0;
            _debugClockOffsetMs += (long)(seconds * 1000.0);
            int changed = Grid.CatchUpAll(CurrentDay, NowUnixMs);
            Inventory?.PruneExpiredBuffs(NowUnixMs);
            if (changed > 0)
            {
                _dirty = true;
                SaveNow(forceCloud: false);
            }
            return changed;
        }

        /// <summary>Resets Homestead plots and inventory to a clean starter kit.</summary>
        public void ResetToStarterState()
        {
            _debugClockOffsetMs = 0;
            Grid?.ClearAll();
            Inventory?.Reset(seedStarterKit: true);
            RefreshAllPlotVisuals();
            _dirty = true;
            SaveNow(forceCloud: false);
        }

        // ------------------------------------------------------------------ day/phase catch-up & visuals

        private void HandleDayAdvanced(int newDay)
        {
            if (Grid == null) return;
            int changed = Grid.CatchUpAll(newDay, NowUnixMs);
            if (changed > 0)
            {
                _dirty = true;
                SaveNow(forceCloud: true);
            }
        }

        private void HandlePhaseChanged(DayPhase prev, DayPhase next)
        {
            if (Grid == null) return;
            if (Grid.CatchUpAll(CurrentDay, NowUnixMs) > 0)
            {
                _dirty = true;
                SaveNow(forceCloud: false);
            }
        }

        private void HandlePlotChanged(int gx, int gy, PlotCell cell)
        {
            _dirty = true;
            ApplyPlotVisual(gx, gy, cell);
        }

        private void HandleInventoryChanged() => _dirty = true;

        private void EnsureSprites()
        {
            if (soilUntilledSprite == null) soilUntilledSprite = PixelArtFactory.GetSoilTileSprite(0);
            if (soilTilledSprite == null) soilTilledSprite = PixelArtFactory.GetSoilTileSprite(1);
            if (soilWateredSprite == null) soilWateredSprite = PixelArtFactory.GetSoilTileSprite(2);
            if (cropStage1Sprite == null) cropStage1Sprite = PixelArtFactory.GetCropStageSprite(1);
            if (cropStage2Sprite == null) cropStage2Sprite = PixelArtFactory.GetCropStageSprite(2);
            if (cropStage3Sprite == null) cropStage3Sprite = PixelArtFactory.GetCropStageSprite(3);
            if (cropMoonmintMatureSprite == null) cropMoonmintMatureSprite = PixelArtFactory.GetMatureCropSprite(ItemCatalog.CropMoonmint);
            if (cropSunpetalMatureSprite == null) cropSunpetalMatureSprite = PixelArtFactory.GetMatureCropSprite(ItemCatalog.CropSunpetal);
            if (cropEmberrootMatureSprite == null) cropEmberrootMatureSprite = PixelArtFactory.GetMatureCropSprite(ItemCatalog.CropEmberroot);
            if (plotCursorSprite == null) plotCursorSprite = PixelArtFactory.GetPlotCursorSprite();
        }

        private void BindOrCreatePlotVisuals()
        {
            int total = Grid.Width * Grid.Height;
            _soilRenderers = new SpriteRenderer[total];
            _cropRenderers = new SpriteRenderer[total];

            if (plotsRoot == null)
            {
                var existing = GameObject.Find("FarmPlots");
                if (existing != null) plotsRoot = existing.transform;
                else
                {
                    var go = new GameObject("FarmPlots");
                    plotsRoot = go.transform;
                }
            }

            Material lit = spriteLitMaterial;
            if (lit == null)
            {
                var any = FindFirstObjectByType<SpriteRenderer>();
                lit = any != null ? any.sharedMaterial : null;
            }

            for (int gy = 0; gy < Grid.Height; gy++)
            for (int gx = 0; gx < Grid.Width; gx++)
            {
                int idx = gy * Grid.Width + gx;
                string slotName = $"Plot_{gx}_{gy}";
                Transform slot = plotsRoot.Find(slotName);
                if (slot == null)
                {
                    var slotGo = new GameObject(slotName);
                    slotGo.transform.SetParent(plotsRoot, false);
                    slotGo.transform.position = PlotToWorld(gx, gy);
                    var soilSr = slotGo.AddComponent<SpriteRenderer>();
                    soilSr.sortingOrder = -90;
                    if (lit != null) soilSr.sharedMaterial = lit;

                    var cropGo = new GameObject("Crop");
                    cropGo.transform.SetParent(slotGo.transform, false);
                    cropGo.transform.localPosition = new Vector3(0f, 0.14f, 0f);
                    var cropSr = cropGo.AddComponent<SpriteRenderer>();
                    cropSr.sortingOrder = 4;
                    if (lit != null) cropSr.sharedMaterial = lit;

                    _soilRenderers[idx] = soilSr;
                    _cropRenderers[idx] = cropSr;
                }
                else
                {
                    _soilRenderers[idx] = slot.GetComponent<SpriteRenderer>();
                    Transform cropTr = slot.Find("Crop");
                    _cropRenderers[idx] = cropTr != null ? cropTr.GetComponent<SpriteRenderer>() : null;
                }
            }

            if (plotCursorRenderer == null)
            {
                Transform existingCursor = plotsRoot.Find("PlotCursor");
                if (existingCursor != null)
                {
                    plotCursorRenderer = existingCursor.GetComponent<SpriteRenderer>();
                }
                else
                {
                    var cursorGo = new GameObject("PlotCursor");
                    cursorGo.transform.SetParent(plotsRoot, false);
                    plotCursorRenderer = cursorGo.AddComponent<SpriteRenderer>();
                    plotCursorRenderer.sprite = plotCursorSprite;
                    plotCursorRenderer.sortingOrder = 15;
                    if (spriteUnlitMaterial != null) plotCursorRenderer.sharedMaterial = spriteUnlitMaterial;
                }
            }
            if (plotCursorRenderer != null)
            {
                if (plotCursorRenderer.sprite == null) plotCursorRenderer.sprite = plotCursorSprite;
                plotCursorRenderer.enabled = false;
            }
        }

        public void RefreshAllPlotVisuals()
        {
            if (Grid == null || _soilRenderers == null) return;
            for (int gy = 0; gy < Grid.Height; gy++)
            for (int gx = 0; gx < Grid.Width; gx++)
            {
                ApplyPlotVisual(gx, gy, Grid.GetCell(gx, gy));
            }
        }

        private void ApplyPlotVisual(int gx, int gy, PlotCell cell)
        {
            if (Grid == null || !Grid.InBounds(gx, gy) || _soilRenderers == null) return;
            int idx = gy * Grid.Width + gx;
            SpriteRenderer soilSr = _soilRenderers[idx];
            if (soilSr != null)
            {
                soilSr.sprite = cell.Soil switch
                {
                    SoilState.Watered => soilWateredSprite,
                    SoilState.Tilled => soilTilledSprite,
                    _ => soilUntilledSprite
                };
            }

            SpriteRenderer cropSr = _cropRenderers[idx];
            if (cropSr != null)
            {
                if (!cell.HasCrop)
                {
                    cropSr.enabled = false;
                    cropSr.sprite = null;
                }
                else
                {
                    cropSr.enabled = true;
                    cropSr.sprite = ResolveCropSprite(cell);
                }
            }
        }

        private Sprite ResolveCropSprite(PlotCell cell)
        {
            switch (cell.Stage)
            {
                case CropGrowthStage.Seedling: return cropStage1Sprite;
                case CropGrowthStage.Sprout: return cropStage2Sprite;
                case CropGrowthStage.Growing: return cropStage3Sprite;
                case CropGrowthStage.Mature:
                    if (cell.CropId == ItemCatalog.CropSunpetal) return cropSunpetalMatureSprite;
                    if (cell.CropId == ItemCatalog.CropEmberroot) return cropEmberrootMatureSprite;
                    return cropMoonmintMatureSprite;
                default: return null;
            }
        }

        // ------------------------------------------------------------------ persistence

        public void SaveNow(bool forceCloud)
        {
            if (Grid == null || Inventory == null) return;
            var snapshot = new FarmSaveData
            {
                version = 1,
                savedAtUnixMs = NowUnixMs,
                lastDay = CurrentDay,
                inventory = Inventory.ToSaveData()
            };
            Grid.WriteToSaveData(snapshot.plots);
            WriteLocal(snapshot);
            _localSavedAt = snapshot.savedAtUnixMs;
            _dirty = false;

            bool due = forceCloud || Time.realtimeSinceStartup - _lastCloudSaveTime >= cloudSaveIntervalSeconds;
            if (due) _ = SaveCloudAsync(snapshot);
        }

        private async Task SaveCloudAsync(FarmSaveData snapshot)
        {
            if (_cloudSaveInFlight || !LiveOpsAllowsWrites()) return;
            _cloudSaveInFlight = true;
            _lastCloudSaveTime = Time.realtimeSinceStartup;
            try
            {
                bool ok = await PlayerSave.SaveAsync(CloudKey, snapshot);
                LastSaveStatus = ok ? "Cloud synced" : "Local cache";
            }
            finally
            {
                _cloudSaveInFlight = false;
            }
        }

        private async Task ReconcileWithCloudAsync()
        {
            if (!LiveOpsAllowsWrites()) return;
            FarmSaveData cloud = await PlayerSave.LoadAsync<FarmSaveData>(CloudKey, null);
            if (this == null) return;

            if (cloud != null && !_dirty && cloud.savedAtUnixMs > _localSavedAt)
            {
                Inventory.LoadFrom(cloud.inventory);
                Grid.LoadFromSaveData(cloud.plots);
                Grid.CatchUpAll(CurrentDay, NowUnixMs);
                RefreshAllPlotVisuals();
                WriteLocal(cloud);
                _localSavedAt = cloud.savedAtUnixMs;
                LastSaveStatus = "Cloud synced";
            }
            else if (_localSavedAt > 0 && (cloud == null || _localSavedAt > cloud.savedAtUnixMs))
            {
                SaveNow(forceCloud: true);
            }
        }

        private static bool LiveOpsAllowsWrites() =>
            FrontierLiveManager.Instance == null || FrontierLiveManager.Instance.LiveOpsEnabled;

        private FarmSaveData ReadLocal()
        {
            try
            {
                if (File.Exists(_localPath))
                    return JsonUtility.FromJson<FarmSaveData>(File.ReadAllText(_localPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Farm] Local save unreadable, starting fresh: {e.Message}");
            }
            return null;
        }

        private void WriteLocal(FarmSaveData data)
        {
            try
            {
                string tmp = _localPath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(data));
                if (File.Exists(_localPath)) File.Delete(_localPath);
                File.Move(tmp, _localPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Farm] Local save failed: {e.Message}");
            }
        }
    }
}
