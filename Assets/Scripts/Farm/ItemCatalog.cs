using System;
using System.Collections.Generic;
using ProjectFrontier.Social;
using UnityEngine;

namespace ProjectFrontier.Farm
{
    public enum ItemCategory
    {
        Seed = 0,
        Herb = 1,
        Potion = 2,
        Material = 3
    }

    public enum BuffKind
    {
        None = 0,
        Heal = 1,
        Swiftness = 2,
        EmberMight = 3
    }

    public sealed class ItemDef
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public ItemCategory Category { get; }
        public int BuyPrice { get; }
        public int SellPrice { get; }
        public BuffKind Buff { get; }
        public float BuffMagnitude { get; }
        public float BuffDurationSeconds { get; }

        public ItemDef(
            string id,
            string displayName,
            string description,
            ItemCategory category,
            int buyPrice,
            int sellPrice,
            BuffKind buff = BuffKind.None,
            float buffMagnitude = 0f,
            float buffDurationSeconds = 0f)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Category = category;
            BuyPrice = Math.Max(0, buyPrice);
            SellPrice = Math.Max(0, sellPrice);
            Buff = buff;
            BuffMagnitude = buffMagnitude;
            BuffDurationSeconds = buffDurationSeconds;
        }
    }

    public sealed class CropDef
    {
        public string CropId { get; }
        public string SeedItemId { get; }
        public string HarvestItemId { get; }
        public string DisplayName { get; }
        public int DaysToGrow { get; }
        public float OfflineGrowSeconds { get; }
        public int HarvestYield { get; }

        public CropDef(
            string cropId,
            string seedItemId,
            string harvestItemId,
            string displayName,
            int daysToGrow,
            float offlineGrowSeconds,
            int harvestYield)
        {
            CropId = cropId;
            SeedItemId = seedItemId;
            HarvestItemId = harvestItemId;
            DisplayName = displayName;
            DaysToGrow = Math.Max(1, daysToGrow);
            OfflineGrowSeconds = Mathf.Max(10f, offlineGrowSeconds);
            HarvestYield = Math.Max(1, harvestYield);
        }
    }

    public readonly struct RecipeIngredient
    {
        public readonly string ItemId;
        public readonly int Count;

        public RecipeIngredient(string itemId, int count)
        {
            ItemId = itemId;
            Count = Math.Max(1, count);
        }
    }

    public sealed class RecipeDef
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string OutputItemId { get; }
        public int OutputCount { get; }
        public bool UnlockedByDefault { get; }
        public int PipUnlockPrice { get; }
        public RecipeIngredient[] Ingredients { get; }

        public RecipeDef(
            string id,
            string displayName,
            string outputItemId,
            int outputCount,
            bool unlockedByDefault,
            int pipUnlockPrice,
            params RecipeIngredient[] ingredients)
        {
            Id = id;
            DisplayName = displayName;
            OutputItemId = outputItemId;
            OutputCount = Math.Max(1, outputCount);
            UnlockedByDefault = unlockedByDefault;
            PipUnlockPrice = Math.Max(0, pipUnlockPrice);
            Ingredients = ingredients ?? Array.Empty<RecipeIngredient>();
        }
    }

    /// <summary>
    /// Static data catalog for Homestead crops, herbs, potions, crafting recipes, and Pip's shop pricing.
    /// Pure C# so C# rules remain authoritative over gold, unlocks, and crafting (GDD §5).
    /// </summary>
    public static class ItemCatalog
    {
        public const string SeedMoonmint = "seed_moonmint";
        public const string SeedSunpetal = "seed_sunpetal";
        public const string SeedEmberroot = "seed_emberroot";

        public const string HerbMoonmint = "herb_moonmint";
        public const string HerbSunpetal = "herb_sunpetal";
        public const string HerbEmberroot = "herb_emberroot";

        public const string PotionMoonmintSalve = "potion_moonmint_salve";
        public const string PotionSunpetalTea = "potion_sunpetal_tea";
        public const string PotionEmberTonic = "potion_ember_tonic";

        public const string CropMoonmint = "moonmint";
        public const string CropSunpetal = "sunpetal";
        public const string CropEmberroot = "emberroot";

        public const string RecipeMoonmintSalve = "recipe_moonmint_salve";
        public const string RecipeSunpetalTea = "recipe_sunpetal_tea";
        public const string RecipeEmberTonic = "recipe_ember_tonic";

        public static readonly ItemDef[] AllItems =
        {
            new ItemDef(SeedMoonmint, "Moonmint Seed", "Fast-growing cool herb seed (1 day).", ItemCategory.Seed, buyPrice: 10, sellPrice: 5),
            new ItemDef(SeedSunpetal, "Sunpetal Seed", "Warm golden blossom seed (1 day).", ItemCategory.Seed, buyPrice: 16, sellPrice: 8),
            new ItemDef(SeedEmberroot, "Emberroot Seed", "Hearty ember-warm root seed (2 days).", ItemCategory.Seed, buyPrice: 24, sellPrice: 12),

            new ItemDef(HerbMoonmint, "Moonmint", "Soothing silver-green leaf for salves.", ItemCategory.Herb, buyPrice: 0, sellPrice: 8),
            new ItemDef(HerbSunpetal, "Sunpetal", "Bright sunlit petals for swiftness tea.", ItemCategory.Herb, buyPrice: 0, sellPrice: 14),
            new ItemDef(HerbEmberroot, "Emberroot", "Spicy ember root prized for raid tonics.", ItemCategory.Herb, buyPrice: 0, sellPrice: 22),

            new ItemDef(PotionMoonmintSalve, "Moonmint Salve", "Restores +40 vitality.", ItemCategory.Potion, buyPrice: 0, sellPrice: 20, BuffKind.Heal, 40f, 0f),
            new ItemDef(PotionSunpetalTea, "Sunpetal Tea", "+20% movement speed for 60s.", ItemCategory.Potion, buyPrice: 0, sellPrice: 32, BuffKind.Swiftness, 0.20f, 60f),
            new ItemDef(PotionEmberTonic, "Ember Tonic", "+25% combat might for 90s.", ItemCategory.Potion, buyPrice: 0, sellPrice: 48, BuffKind.EmberMight, 0.25f, 90f)
        };

        public static readonly CropDef[] AllCrops =
        {
            new CropDef(CropMoonmint, SeedMoonmint, HerbMoonmint, "Moonmint", daysToGrow: 1, offlineGrowSeconds: 180f, harvestYield: 2),
            new CropDef(CropSunpetal, SeedSunpetal, HerbSunpetal, "Sunpetal", daysToGrow: 1, offlineGrowSeconds: 240f, harvestYield: 2),
            new CropDef(CropEmberroot, SeedEmberroot, HerbEmberroot, "Emberroot", daysToGrow: 2, offlineGrowSeconds: 420f, harvestYield: 2)
        };

        public static readonly RecipeDef[] AllRecipes =
        {
            new RecipeDef(
                RecipeMoonmintSalve, "Moonmint Salve", PotionMoonmintSalve, 1,
                unlockedByDefault: true, pipUnlockPrice: 0,
                new RecipeIngredient(HerbMoonmint, 2)),
            new RecipeDef(
                RecipeSunpetalTea, "Sunpetal Tea", PotionSunpetalTea, 1,
                unlockedByDefault: false, pipUnlockPrice: 25,
                new RecipeIngredient(HerbMoonmint, 1),
                new RecipeIngredient(HerbSunpetal, 2)),
            new RecipeDef(
                RecipeEmberTonic, "Ember Tonic", PotionEmberTonic, 1,
                unlockedByDefault: false, pipUnlockPrice: 40,
                new RecipeIngredient(HerbSunpetal, 1),
                new RecipeIngredient(HerbEmberroot, 2))
        };

        private static readonly Dictionary<string, ItemDef> ItemsById = BuildItemMap();
        private static readonly Dictionary<string, CropDef> CropsById = BuildCropMap();
        private static readonly Dictionary<string, CropDef> CropsBySeed = BuildSeedCropMap();
        private static readonly Dictionary<string, RecipeDef> RecipesById = BuildRecipeMap();

        public static ItemDef GetItem(string itemId) =>
            !string.IsNullOrEmpty(itemId) && ItemsById.TryGetValue(itemId, out var def) ? def : null;

        public static CropDef GetCrop(string cropId) =>
            !string.IsNullOrEmpty(cropId) && CropsById.TryGetValue(cropId, out var def) ? def : null;

        public static CropDef GetCropForSeed(string seedItemId) =>
            !string.IsNullOrEmpty(seedItemId) && CropsBySeed.TryGetValue(seedItemId, out var def) ? def : null;

        public static RecipeDef GetRecipe(string recipeId) =>
            !string.IsNullOrEmpty(recipeId) && RecipesById.TryGetValue(recipeId, out var def) ? def : null;

        /// <summary>
        /// Relationship-adjusted Pip shop price (GDD §5):
        /// Friend/Lover gets 20% off; Enemy pays +25% markup; Neutral pays standard rate.
        /// </summary>
        public static int GetPipPrice(int basePrice, RelationshipState pipStanding)
        {
            if (basePrice <= 0) return 0;
            float mult = pipStanding switch
            {
                RelationshipState.Friend => 0.80f,
                RelationshipState.Lover => 0.75f,
                RelationshipState.Enemy => 1.25f,
                _ => 1.0f
            };
            return Math.Max(1, Mathf.RoundToInt(basePrice * mult));
        }

        public static string GetStandingPriceNote(RelationshipState pipStanding) => pipStanding switch
        {
            RelationshipState.Friend => "FRIEND · 20% OFF",
            RelationshipState.Lover => "LOVER · 25% OFF",
            RelationshipState.Enemy => "ENEMY · +25% MARKUP",
            _ => "STANDARD RATES"
        };

        private static Dictionary<string, ItemDef> BuildItemMap()
        {
            var map = new Dictionary<string, ItemDef>(AllItems.Length, StringComparer.Ordinal);
            for (int i = 0; i < AllItems.Length; i++) map[AllItems[i].Id] = AllItems[i];
            return map;
        }

        private static Dictionary<string, CropDef> BuildCropMap()
        {
            var map = new Dictionary<string, CropDef>(AllCrops.Length, StringComparer.Ordinal);
            for (int i = 0; i < AllCrops.Length; i++) map[AllCrops[i].CropId] = AllCrops[i];
            return map;
        }

        private static Dictionary<string, CropDef> BuildSeedCropMap()
        {
            var map = new Dictionary<string, CropDef>(AllCrops.Length, StringComparer.Ordinal);
            for (int i = 0; i < AllCrops.Length; i++) map[AllCrops[i].SeedItemId] = AllCrops[i];
            return map;
        }

        private static Dictionary<string, RecipeDef> BuildRecipeMap()
        {
            var map = new Dictionary<string, RecipeDef>(AllRecipes.Length, StringComparer.Ordinal);
            for (int i = 0; i < AllRecipes.Length; i++) map[AllRecipes[i].Id] = AllRecipes[i];
            return map;
        }
    }
}
