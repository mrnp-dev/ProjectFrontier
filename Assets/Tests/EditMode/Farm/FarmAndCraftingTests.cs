using NUnit.Framework;
using ProjectFrontier.Farm;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier.Farm.Tests
{
    public class FarmAndCraftingTests
    {
        private const long BaseTimeMs = 1_795_800_000_000L;

        [Test]
        public void ChunkedGrid_AllocatesChunksLazilyAcrossBoundaries()
        {
            var grid = new ChunkedFarmGrid(chunksX: 2, chunksY: 1);
            Assert.AreEqual(8, grid.Width);
            Assert.AreEqual(4, grid.Height);
            Assert.AreEqual(0, grid.ActiveChunkCount);

            Assert.IsTrue(grid.Till(1, 2, 1, BaseTimeMs));
            Assert.AreEqual(1, grid.ActiveChunkCount);
            Assert.AreEqual(SoilState.Tilled, grid.GetCell(1, 2).Soil);

            Assert.IsTrue(grid.Till(5, 1, 1, BaseTimeMs));
            Assert.AreEqual(2, grid.ActiveChunkCount);
            Assert.AreEqual(SoilState.Tilled, grid.GetCell(5, 1).Soil);
            Assert.AreEqual(SoilState.Untilled, grid.GetCell(4, 0).Soil);
        }

        [Test]
        public void FullCropLifecycle_TillPlantWaterDaySkipHarvest()
        {
            var grid = new ChunkedFarmGrid();
            var inv = new InventoryState(seedStarterKit: true);
            int startSeeds = inv.GetCount(ItemCatalog.SeedMoonmint);

            var r1 = grid.SmartInteract(0, 0, inv, 1, BaseTimeMs);
            Assert.AreEqual(PlotActionKind.Till, r1.Action);
            Assert.IsTrue(r1.Succeeded);

            var r2 = grid.SmartInteract(0, 0, inv, 1, BaseTimeMs);
            Assert.AreEqual(PlotActionKind.Plant, r2.Action);
            Assert.IsTrue(r2.Succeeded);
            Assert.AreEqual(startSeeds - 1, inv.GetCount(ItemCatalog.SeedMoonmint));

            var r3 = grid.SmartInteract(0, 0, inv, 1, BaseTimeMs);
            Assert.AreEqual(PlotActionKind.Water, r3.Action);
            Assert.IsTrue(r3.Succeeded);
            Assert.AreEqual(SoilState.Watered, grid.GetCell(0, 0).Soil);

            // Day skip catch-up (Day 1 -> Day 2): 1-day Moonmint becomes Mature and soil dries to Tilled.
            int changed = grid.CatchUpAll(2, BaseTimeMs + 1000L);
            Assert.AreEqual(1, changed);
            PlotCell cell = grid.GetCell(0, 0);
            Assert.IsTrue(cell.IsMature);
            Assert.AreEqual(CropGrowthStage.Mature, cell.Stage);
            Assert.AreEqual(SoilState.Tilled, cell.Soil);

            // Repeated catch-up at same timestamp produces zero changes.
            Assert.AreEqual(0, grid.CatchUpAll(2, BaseTimeMs + 1000L));

            // Harvest yields 2 Moonmint and leaves soil Tilled.
            var r4 = grid.SmartInteract(0, 0, inv, 2, BaseTimeMs + 2000L);
            Assert.AreEqual(PlotActionKind.Harvest, r4.Action);
            Assert.IsTrue(r4.Succeeded);
            Assert.AreEqual(2, inv.GetCount(ItemCatalog.HerbMoonmint));
            Assert.IsFalse(grid.GetCell(0, 0).HasCrop);
            Assert.AreEqual(SoilState.Tilled, grid.GetCell(0, 0).Soil);
        }

        [Test]
        public void TwoDayCrop_RequiresWateringOnSecondDay()
        {
            var grid = new ChunkedFarmGrid();
            var inv = new InventoryState(seedStarterKit: false);
            inv.AddItem(ItemCatalog.SeedEmberroot, 1);

            Assert.IsTrue(grid.Till(2, 1, 1, BaseTimeMs));
            Assert.IsTrue(grid.Plant(2, 1, ItemCatalog.SeedEmberroot, inv, 1, BaseTimeMs));
            Assert.IsTrue(grid.Water(2, 1, 1, BaseTimeMs));

            // Day 1 -> Day 2: 50% grown, dried back to Tilled.
            grid.CatchUpAll(2, BaseTimeMs + 1000L);
            PlotCell day2 = grid.GetCell(2, 1);
            Assert.IsFalse(day2.IsMature);
            Assert.AreEqual(0.5f, day2.GrowthProgress, 0.01f);
            Assert.AreEqual(SoilState.Tilled, day2.Soil);

            // Skipping Day 2 -> Day 3 without watering does NOT grow further.
            grid.CatchUpAll(3, BaseTimeMs + 2000L);
            Assert.AreEqual(0.5f, grid.GetCell(2, 1).GrowthProgress, 0.01f);

            // Watering on Day 3 and advancing to Day 4 completes growth.
            Assert.IsTrue(grid.Water(2, 1, 3, BaseTimeMs + 3000L));
            grid.CatchUpAll(4, BaseTimeMs + 4000L);
            Assert.IsTrue(grid.GetCell(2, 1).IsMature);
        }

        [Test]
        public void OfflineTimestampDelta_MaturesWateredCropWithoutDaySkip()
        {
            var grid = new ChunkedFarmGrid();
            var inv = new InventoryState(seedStarterKit: true);
            Assert.IsTrue(grid.Till(3, 0, 1, BaseTimeMs));
            Assert.IsTrue(grid.Plant(3, 0, ItemCatalog.SeedMoonmint, inv, 1, BaseTimeMs));
            Assert.IsTrue(grid.Water(3, 0, 1, BaseTimeMs));

            // 90 seconds offline (half of Moonmint's 180s offline growth).
            grid.CatchUpAll(1, BaseTimeMs + 90_000L);
            Assert.AreEqual(0.5f, grid.GetCell(3, 0).GrowthProgress, 0.02f);
            Assert.IsFalse(grid.GetCell(3, 0).IsMature);

            // Another 95 seconds offline -> Mature on the same day.
            grid.CatchUpAll(1, BaseTimeMs + 185_000L);
            Assert.IsTrue(grid.GetCell(3, 0).IsMature);
        }

        [Test]
        public void CraftingAndPipShop_EnforceRecipesGoldAndRelationshipPricing()
        {
            var inv = new InventoryState(seedStarterKit: true);
            Assert.AreEqual(50, inv.Gold);
            Assert.IsTrue(inv.IsRecipeUnlocked(ItemCatalog.RecipeMoonmintSalve));
            Assert.IsFalse(inv.IsRecipeUnlocked(ItemCatalog.RecipeSunpetalTea));

            // Default recipe crafts Moonmint Salve from 2 Moonmint.
            inv.AddItem(ItemCatalog.HerbMoonmint, 2);
            Assert.IsTrue(inv.TryCraft(ItemCatalog.RecipeMoonmintSalve, out ItemDef salve));
            Assert.AreEqual(ItemCatalog.PotionMoonmintSalve, salve.Id);
            Assert.AreEqual(0, inv.GetCount(ItemCatalog.HerbMoonmint));
            Assert.AreEqual(1, inv.GetCount(ItemCatalog.PotionMoonmintSalve));

            // Locked recipe cannot be crafted before buying from Pip.
            inv.AddItem(ItemCatalog.HerbMoonmint, 1);
            inv.AddItem(ItemCatalog.HerbSunpetal, 2);
            Assert.IsFalse(inv.TryCraft(ItemCatalog.RecipeSunpetalTea, out _));

            // Friend discount with Pip: Sunpetal Tea recipe base 25g -> 20g.
            Assert.AreEqual(25, ItemCatalog.GetPipPrice(25, RelationshipState.Neutral));
            Assert.AreEqual(20, ItemCatalog.GetPipPrice(25, RelationshipState.Friend));
            Assert.AreEqual(31, ItemCatalog.GetPipPrice(25, RelationshipState.Enemy));

            Assert.IsTrue(inv.TryBuyRecipe(ItemCatalog.RecipeSunpetalTea, RelationshipState.Friend, out int recipeCost));
            Assert.AreEqual(20, recipeCost);
            Assert.AreEqual(30, inv.Gold);
            Assert.IsTrue(inv.IsRecipeUnlocked(ItemCatalog.RecipeSunpetalTea));

            // Now craft Sunpetal Tea and drink it -> active Swiftness buff.
            Assert.IsTrue(inv.TryCraft(ItemCatalog.RecipeSunpetalTea, out ItemDef tea));
            Assert.AreEqual(ItemCatalog.PotionSunpetalTea, tea.Id);
            Assert.IsTrue(inv.TryUsePotion(ItemCatalog.PotionSunpetalTea, BaseTimeMs, out _));
            Assert.AreEqual(0.20f, inv.GetBuffBonus(BuffKind.Swiftness, BaseTimeMs + 10_000L), 0.001f);
            Assert.IsTrue(inv.PruneExpiredBuffs(BaseTimeMs + 65_000L));
            Assert.AreEqual(0f, inv.GetBuffBonus(BuffKind.Swiftness, BaseTimeMs + 65_000L), 0.001f);

            // Buy seed & sell herb.
            Assert.IsTrue(inv.TryBuySeed(ItemCatalog.SeedMoonmint, 1, RelationshipState.Friend, out int seedCost));
            Assert.AreEqual(8, seedCost);
            inv.AddItem(ItemCatalog.HerbEmberroot, 1);
            Assert.IsTrue(inv.TrySellItem(ItemCatalog.HerbEmberroot, 1, out int earned));
            Assert.AreEqual(22, earned);
            Assert.AreEqual(30 - 8 + 22, inv.Gold);
        }

        [Test]
        public void SaveLoadRoundTrip_PreservesPlotsInventoryAndOfflineCatchUp()
        {
            var grid = new ChunkedFarmGrid();
            var inv = new InventoryState(seedStarterKit: true);
            inv.AddGold(25);
            grid.Till(6, 2, 1, BaseTimeMs);
            grid.Plant(6, 2, ItemCatalog.SeedSunpetal, inv, 1, BaseTimeMs);
            grid.Water(6, 2, 1, BaseTimeMs);

            var save = new FarmSaveData
            {
                savedAtUnixMs = BaseTimeMs,
                lastDay = 1,
                inventory = inv.ToSaveData()
            };
            grid.WriteToSaveData(save.plots);
            string json = JsonUtility.ToJson(save);

            var restoredSave = JsonUtility.FromJson<FarmSaveData>(json);
            var restoredInv = new InventoryState(seedStarterKit: false);
            restoredInv.LoadFrom(restoredSave.inventory);
            var restoredGrid = new ChunkedFarmGrid();
            restoredGrid.LoadFromSaveData(restoredSave.plots);

            Assert.AreEqual(75, restoredInv.Gold);
            Assert.AreEqual(0, restoredInv.GetCount(ItemCatalog.SeedSunpetal));

            // Simulate returning 5 minutes later offline -> Sunpetal (240s) is Mature!
            restoredGrid.CatchUpAll(1, BaseTimeMs + 300_000L);
            Assert.IsTrue(restoredGrid.GetCell(6, 2).IsMature);
        }
    }
}
