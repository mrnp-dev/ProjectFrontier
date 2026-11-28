using System;
using System.Collections.Generic;
using ProjectFrontier.Social;

namespace ProjectFrontier.Farm
{
    [Serializable]
    public sealed class ItemStackData
    {
        public string itemId;
        public int count;
    }

    [Serializable]
    public sealed class ActiveBuffData
    {
        public int buffKind;
        public string label;
        public float magnitude;
        public long expiresAtUnixMs;
    }

    [Serializable]
    public sealed class InventorySaveData
    {
        public int version = 1;
        public int gold;
        public string selectedSeedId;
        public List<ItemStackData> items = new List<ItemStackData>();
        public List<string> unlockedRecipes = new List<string>();
        public List<ActiveBuffData> activeBuffs = new List<ActiveBuffData>();
    }

    /// <summary>
    /// Pure-C# player inventory, gold wallet, unlocked crafting recipes, and active potion buffs.
    /// Raises <see cref="Changed"/> only on mutations so HUD widgets do zero per-frame work.
    /// </summary>
    public sealed class InventoryState
    {
        public const int StarterGold = 50;
        public const int StarterMoonmintSeeds = 3;
        public const int StarterSunpetalSeeds = 1;

        private readonly Dictionary<string, int> _items = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _unlockedRecipes = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ActiveBuffData> _activeBuffs = new List<ActiveBuffData>(4);

        public int Gold { get; private set; }
        public string SelectedSeedId { get; private set; } = ItemCatalog.SeedMoonmint;
        public IReadOnlyList<ActiveBuffData> ActiveBuffs => _activeBuffs;

        public event Action Changed;

        public InventoryState(bool seedStarterKit = true)
        {
            Reset(seedStarterKit);
        }

        public void Reset(bool seedStarterKit = true)
        {
            _items.Clear();
            _unlockedRecipes.Clear();
            _activeBuffs.Clear();

            for (int i = 0; i < ItemCatalog.AllRecipes.Length; i++)
            {
                if (ItemCatalog.AllRecipes[i].UnlockedByDefault)
                    _unlockedRecipes.Add(ItemCatalog.AllRecipes[i].Id);
            }

            if (seedStarterKit)
            {
                Gold = StarterGold;
                _items[ItemCatalog.SeedMoonmint] = StarterMoonmintSeeds;
                _items[ItemCatalog.SeedSunpetal] = StarterSunpetalSeeds;
                SelectedSeedId = ItemCatalog.SeedMoonmint;
            }
            else
            {
                Gold = 0;
                SelectedSeedId = ItemCatalog.SeedMoonmint;
            }

            Changed?.Invoke();
        }

        public int GetCount(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            return _items.TryGetValue(itemId, out int c) ? c : 0;
        }

        public void AddItem(string itemId, int amount)
        {
            if (amount <= 0 || ItemCatalog.GetItem(itemId) == null) return;
            _items[itemId] = GetCount(itemId) + amount;
            if (string.IsNullOrEmpty(SelectedSeedId) && ItemCatalog.GetCropForSeed(itemId) != null)
                SelectedSeedId = itemId;
            Changed?.Invoke();
        }

        public bool TryConsumeItem(string itemId, int amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(itemId)) return false;
            int cur = GetCount(itemId);
            if (cur < amount) return false;
            int next = cur - amount;
            if (next == 0) _items.Remove(itemId);
            else _items[itemId] = next;
            Changed?.Invoke();
            return true;
        }

        public void AddGold(int amount)
        {
            if (amount <= 0) return;
            Gold += amount;
            Changed?.Invoke();
        }

        public bool TrySpendGold(int amount)
        {
            if (amount < 0) return false;
            if (amount == 0) return true;
            if (Gold < amount) return false;
            Gold -= amount;
            Changed?.Invoke();
            return true;
        }

        public bool SelectSeed(string seedItemId)
        {
            if (ItemCatalog.GetCropForSeed(seedItemId) == null) return false;
            if (SelectedSeedId == seedItemId) return true;
            SelectedSeedId = seedItemId;
            Changed?.Invoke();
            return true;
        }

        /// <summary>Returns the currently selected seed if owned, otherwise the first owned seed in catalog order.</summary>
        public string ResolvePlantableSeed(string preferredSeedId = null)
        {
            string candidate = !string.IsNullOrEmpty(preferredSeedId) ? preferredSeedId : SelectedSeedId;
            if (!string.IsNullOrEmpty(candidate) && ItemCatalog.GetCropForSeed(candidate) != null && GetCount(candidate) > 0)
                return candidate;

            for (int i = 0; i < ItemCatalog.AllCrops.Length; i++)
            {
                string sid = ItemCatalog.AllCrops[i].SeedItemId;
                if (GetCount(sid) > 0) return sid;
            }
            return null;
        }

        public bool IsRecipeUnlocked(string recipeId) =>
            !string.IsNullOrEmpty(recipeId) && _unlockedRecipes.Contains(recipeId);

        public bool UnlockRecipe(string recipeId)
        {
            if (ItemCatalog.GetRecipe(recipeId) == null) return false;
            if (!_unlockedRecipes.Add(recipeId)) return false;
            Changed?.Invoke();
            return true;
        }

        public bool CanCraft(string recipeId)
        {
            RecipeDef recipe = ItemCatalog.GetRecipe(recipeId);
            if (recipe == null || !IsRecipeUnlocked(recipe.Id)) return false;
            for (int i = 0; i < recipe.Ingredients.Length; i++)
            {
                if (GetCount(recipe.Ingredients[i].ItemId) < recipe.Ingredients[i].Count) return false;
            }
            return true;
        }

        public bool TryCraft(string recipeId, out ItemDef craftedItem)
        {
            craftedItem = null;
            RecipeDef recipe = ItemCatalog.GetRecipe(recipeId);
            if (recipe == null || !CanCraft(recipe.Id)) return false;

            for (int i = 0; i < recipe.Ingredients.Length; i++)
            {
                var ing = recipe.Ingredients[i];
                int next = GetCount(ing.ItemId) - ing.Count;
                if (next <= 0) _items.Remove(ing.ItemId);
                else _items[ing.ItemId] = next;
            }

            _items[recipe.OutputItemId] = GetCount(recipe.OutputItemId) + recipe.OutputCount;
            craftedItem = ItemCatalog.GetItem(recipe.OutputItemId);
            Changed?.Invoke();
            return true;
        }

        public bool TryBuySeed(string seedItemId, int quantity, RelationshipState pipStanding, out int totalCost)
        {
            totalCost = 0;
            if (quantity <= 0) return false;
            ItemDef item = ItemCatalog.GetItem(seedItemId);
            if (item == null || item.Category != ItemCategory.Seed || item.BuyPrice <= 0) return false;
            int unitPrice = ItemCatalog.GetPipPrice(item.BuyPrice, pipStanding);
            totalCost = unitPrice * quantity;
            if (Gold < totalCost) return false;

            Gold -= totalCost;
            _items[seedItemId] = GetCount(seedItemId) + quantity;
            if (GetCount(SelectedSeedId) <= 0) SelectedSeedId = seedItemId;
            Changed?.Invoke();
            return true;
        }

        public bool TryBuyRecipe(string recipeId, RelationshipState pipStanding, out int cost)
        {
            cost = 0;
            RecipeDef recipe = ItemCatalog.GetRecipe(recipeId);
            if (recipe == null || IsRecipeUnlocked(recipe.Id)) return false;
            cost = ItemCatalog.GetPipPrice(recipe.PipUnlockPrice, pipStanding);
            if (Gold < cost) return false;

            Gold -= cost;
            _unlockedRecipes.Add(recipe.Id);
            Changed?.Invoke();
            return true;
        }

        public bool TrySellItem(string itemId, int quantity, out int goldEarned)
        {
            goldEarned = 0;
            if (quantity <= 0) return false;
            ItemDef item = ItemCatalog.GetItem(itemId);
            if (item == null || item.SellPrice <= 0) return false;
            if (GetCount(itemId) < quantity) return false;

            int next = GetCount(itemId) - quantity;
            if (next <= 0) _items.Remove(itemId);
            else _items[itemId] = next;

            goldEarned = item.SellPrice * quantity;
            Gold += goldEarned;
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// Consumes a crafted potion and applies its heal or timed buff.
        /// </summary>
        public bool TryUsePotion(string potionItemId, long nowUnixMs, out ItemDef usedItem)
        {
            usedItem = null;
            ItemDef item = ItemCatalog.GetItem(potionItemId);
            if (item == null || item.Category != ItemCategory.Potion) return false;
            if (GetCount(potionItemId) <= 0) return false;

            int next = GetCount(potionItemId) - 1;
            if (next <= 0) _items.Remove(potionItemId);
            else _items[potionItemId] = next;

            if (item.Buff != BuffKind.None && item.BuffDurationSeconds > 0f)
            {
                long expires = nowUnixMs + (long)(item.BuffDurationSeconds * 1000.0);
                bool updated = false;
                for (int i = 0; i < _activeBuffs.Count; i++)
                {
                    if (_activeBuffs[i].buffKind == (int)item.Buff)
                    {
                        _activeBuffs[i].expiresAtUnixMs = Math.Max(_activeBuffs[i].expiresAtUnixMs, expires);
                        _activeBuffs[i].magnitude = Math.Max(_activeBuffs[i].magnitude, item.BuffMagnitude);
                        updated = true;
                        break;
                    }
                }
                if (!updated)
                {
                    _activeBuffs.Add(new ActiveBuffData
                    {
                        buffKind = (int)item.Buff,
                        label = item.DisplayName,
                        magnitude = item.BuffMagnitude,
                        expiresAtUnixMs = expires
                    });
                }
            }

            usedItem = item;
            Changed?.Invoke();
            return true;
        }

        /// <summary>Removes expired buffs using timestamp comparison. Returns true if any expired.</summary>
        public bool PruneExpiredBuffs(long nowUnixMs)
        {
            bool removed = false;
            for (int i = _activeBuffs.Count - 1; i >= 0; i--)
            {
                if (_activeBuffs[i].expiresAtUnixMs <= nowUnixMs)
                {
                    _activeBuffs.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) Changed?.Invoke();
            return removed;
        }

        public float GetBuffBonus(BuffKind kind, long nowUnixMs)
        {
            float bonus = 0f;
            for (int i = 0; i < _activeBuffs.Count; i++)
            {
                if (_activeBuffs[i].buffKind == (int)kind && _activeBuffs[i].expiresAtUnixMs > nowUnixMs)
                    bonus += _activeBuffs[i].magnitude;
            }
            return bonus;
        }

        public InventorySaveData ToSaveData()
        {
            var data = new InventorySaveData
            {
                version = 1,
                gold = Gold,
                selectedSeedId = SelectedSeedId
            };
            for (int i = 0; i < ItemCatalog.AllItems.Length; i++)
            {
                string id = ItemCatalog.AllItems[i].Id;
                int count = GetCount(id);
                if (count > 0) data.items.Add(new ItemStackData { itemId = id, count = count });
            }
            foreach (string r in _unlockedRecipes) data.unlockedRecipes.Add(r);
            data.unlockedRecipes.Sort(StringComparer.Ordinal);
            for (int i = 0; i < _activeBuffs.Count; i++)
            {
                var b = _activeBuffs[i];
                data.activeBuffs.Add(new ActiveBuffData
                {
                    buffKind = b.buffKind,
                    label = b.label,
                    magnitude = b.magnitude,
                    expiresAtUnixMs = b.expiresAtUnixMs
                });
            }
            return data;
        }

        public void LoadFrom(InventorySaveData data)
        {
            if (data == null)
            {
                Reset(seedStarterKit: true);
                return;
            }

            _items.Clear();
            _unlockedRecipes.Clear();
            _activeBuffs.Clear();

            for (int i = 0; i < ItemCatalog.AllRecipes.Length; i++)
            {
                if (ItemCatalog.AllRecipes[i].UnlockedByDefault)
                    _unlockedRecipes.Add(ItemCatalog.AllRecipes[i].Id);
            }

            Gold = Math.Max(0, data.gold);
            SelectedSeedId = ItemCatalog.GetCropForSeed(data.selectedSeedId) != null
                ? data.selectedSeedId
                : ItemCatalog.SeedMoonmint;

            if (data.items != null)
            {
                for (int i = 0; i < data.items.Count; i++)
                {
                    var s = data.items[i];
                    if (s != null && s.count > 0 && ItemCatalog.GetItem(s.itemId) != null)
                        _items[s.itemId] = s.count;
                }
            }

            if (data.unlockedRecipes != null)
            {
                for (int i = 0; i < data.unlockedRecipes.Count; i++)
                {
                    string r = data.unlockedRecipes[i];
                    if (ItemCatalog.GetRecipe(r) != null) _unlockedRecipes.Add(r);
                }
            }

            if (data.activeBuffs != null)
            {
                for (int i = 0; i < data.activeBuffs.Count; i++)
                {
                    var b = data.activeBuffs[i];
                    if (b != null && b.buffKind > 0 && b.expiresAtUnixMs > 0)
                        _activeBuffs.Add(b);
                }
            }

            Changed?.Invoke();
        }
    }
}
