using System;
using System.Collections.Generic;
using ProjectFrontier.Farm;
using ProjectFrontier.Social;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectFrontier
{
    public enum FarmModalTab
    {
        Bag = 0,
        Craft = 1,
        PipShop = 2
    }

    /// <summary>
    /// Native-resolution uGUI for Homestead Inventory/Bag, Crafting Bench, and Pip's Seed &amp; Recipe Shop (#19),
    /// plus the compact HUD Gold/Seed/Buff strip. Refreshes strictly on mutations and 1 Hz buff countdown.
    /// </summary>
    public class FrontierFarmUI : MonoBehaviour
    {
        public static FrontierFarmUI Instance { get; private set; }
        public static bool IsAnyModalOpen => Instance != null && Instance.IsOpen;

        [Header("HUD Strip")]
        public TextMeshProUGUI hudGoldSeedText;
        public Button hudBagButton;
        public GameObject hudBuffBadgeRoot;
        public TextMeshProUGUI hudBuffBadgeText;

        [Header("Modal Shell")]
        public GameObject modalRoot;
        public TextMeshProUGUI modalTitleText;
        public TextMeshProUGUI modalGoldText;
        public TextMeshProUGUI modalStandingText;
        public TextMeshProUGUI feedbackToastText;
        public Button closeButton;
        public Button tabBagButton;
        public Button tabCraftButton;
        public Button tabShopButton;
        public RectTransform contentContainer;

        [Header("Styling Assets")]
        public Sprite panelSprite;
        public Sprite solidSprite;
        public Material tmpOutlineMaterial;

        public bool IsOpen { get; private set; }
        public FarmModalTab ActiveTab { get; private set; } = FarmModalTab.Bag;

        private readonly List<GameObject> _spawnedRows = new List<GameObject>(16);
        private float _buffRefreshTimer;
        private float _toastTimer;
        private bool _wired;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            EnsureWired();
        }

        private void Start()
        {
            EnsureWired();
            if (FrontierFarmManager.Instance != null)
            {
                FrontierFarmManager.Instance.Inventory.Changed += HandleInventoryChanged;
                FrontierFarmManager.Instance.StatusFeedback += ShowFeedbackToast;
            }
            if (modalRoot != null) modalRoot.SetActive(false);
            IsOpen = false;
            RefreshHudStrip();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (FrontierFarmManager.Instance != null)
            {
                if (FrontierFarmManager.Instance.Inventory != null)
                    FrontierFarmManager.Instance.Inventory.Changed -= HandleInventoryChanged;
                FrontierFarmManager.Instance.StatusFeedback -= ShowFeedbackToast;
            }
            Instance = null;
        }

        private void Update()
        {
            if (_toastTimer > 0f)
            {
                _toastTimer = Mathf.Max(0f, _toastTimer - Time.unscaledDeltaTime);
                if (_toastTimer <= 0f && feedbackToastText != null) feedbackToastText.text = string.Empty;
            }

            var mgr = FrontierFarmManager.Instance;
            if (mgr != null && mgr.Inventory != null && mgr.Inventory.ActiveBuffs.Count > 0)
            {
                _buffRefreshTimer += Time.unscaledDeltaTime;
                if (_buffRefreshTimer >= 1f)
                {
                    _buffRefreshTimer = 0f;
                    RefreshBuffBadge();
                }
            }
        }

        private void EnsureWired()
        {
            if (_wired) return;
            _wired = true;

            if (panelSprite == null) panelSprite = PixelArtFactory.GetUiPanelSprite();
            if (solidSprite == null) solidSprite = PixelArtFactory.GetSolidUiSprite();

            if (hudBagButton != null) hudBagButton.onClick.AddListener(ToggleBag);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (tabBagButton != null) tabBagButton.onClick.AddListener(() => SwitchTab(FarmModalTab.Bag));
            if (tabCraftButton != null) tabCraftButton.onClick.AddListener(() => SwitchTab(FarmModalTab.Craft));
            if (tabShopButton != null) tabShopButton.onClick.AddListener(() => SwitchTab(FarmModalTab.PipShop));
        }

        public void ToggleBag()
        {
            if (IsOpen && ActiveTab == FarmModalTab.Bag) Close();
            else Open(FarmModalTab.Bag);
        }

        public void Open(FarmModalTab tab)
        {
            EnsureWired();
            var loop = FrontierGameLoop.Instance;
            if (loop != null && loop.State != RunState.Running) return;
            if (loop != null && loop.DialogueOpen) loop.CloseDialogue(completeInteraction: true);

            ActiveTab = tab;
            IsOpen = true;
            if (modalRoot != null) modalRoot.SetActive(true);
            SynthAudioManager.Instance?.PlayUiClick();
            RefreshModal();
        }

        public void SwitchTab(FarmModalTab tab)
        {
            if (ActiveTab == tab && IsOpen) return;
            ActiveTab = tab;
            SynthAudioManager.Instance?.PlayUiClick();
            RefreshModal();
        }

        public void Close()
        {
            if (!IsOpen && (modalRoot == null || !modalRoot.activeSelf)) return;
            IsOpen = false;
            if (modalRoot != null) modalRoot.SetActive(false);
            SynthAudioManager.Instance?.PlayUiClick();
        }

        public void ShowFeedbackToast(string message)
        {
            if (feedbackToastText == null || string.IsNullOrEmpty(message)) return;
            feedbackToastText.text = message;
            _toastTimer = 3.0f;
        }

        private void HandleInventoryChanged()
        {
            RefreshHudStrip();
            if (IsOpen) RefreshModal();
        }

        public void RefreshHudStrip()
        {
            var mgr = FrontierFarmManager.Instance;
            if (mgr == null || mgr.Inventory == null) return;
            var inv = mgr.Inventory;

            if (hudGoldSeedText != null)
            {
                string seedId = inv.ResolvePlantableSeed();
                if (!string.IsNullOrEmpty(seedId))
                {
                    CropDef crop = ItemCatalog.GetCropForSeed(seedId);
                    string cropName = crop != null ? crop.DisplayName.ToUpperInvariant() : "SEED";
                    int count = inv.GetCount(seedId);
                    hudGoldSeedText.text = $"{inv.Gold}G · {cropName} ({count})";
                }
                else
                {
                    hudGoldSeedText.text = $"{inv.Gold}G · NO SEEDS";
                }
            }

            RefreshBuffBadge();
        }

        private void RefreshBuffBadge()
        {
            if (hudBuffBadgeRoot == null || hudBuffBadgeText == null) return;
            var mgr = FrontierFarmManager.Instance;
            if (mgr == null || mgr.Inventory == null || mgr.Inventory.ActiveBuffs.Count == 0)
            {
                if (hudBuffBadgeRoot.activeSelf) hudBuffBadgeRoot.SetActive(false);
                return;
            }

            long now = mgr.NowUnixMs;
            string summary = string.Empty;
            for (int i = 0; i < mgr.Inventory.ActiveBuffs.Count; i++)
            {
                var b = mgr.Inventory.ActiveBuffs[i];
                int secs = Mathf.CeilToInt((b.expiresAtUnixMs - now) / 1000f);
                if (secs <= 0) continue;
                string tag = (BuffKind)b.buffKind switch
                {
                    BuffKind.Swiftness => $"SWIFT +{Mathf.RoundToInt(b.magnitude * 100f)}% {secs}S",
                    BuffKind.EmberMight => $"MIGHT +{Mathf.RoundToInt(b.magnitude * 100f)}% {secs}S",
                    _ => $"{b.label.ToUpperInvariant()} {secs}S"
                };
                summary = summary.Length == 0 ? tag : summary + " · " + tag;
            }

            bool hasActive = summary.Length > 0;
            if (hudBuffBadgeRoot.activeSelf != hasActive) hudBuffBadgeRoot.SetActive(hasActive);
            if (hasActive) hudBuffBadgeText.text = summary;
        }

        public void RefreshModal()
        {
            var mgr = FrontierFarmManager.Instance;
            if (mgr == null || mgr.Inventory == null) return;
            var inv = mgr.Inventory;

            TintTab(tabBagButton, ActiveTab == FarmModalTab.Bag);
            TintTab(tabCraftButton, ActiveTab == FarmModalTab.Craft);
            TintTab(tabShopButton, ActiveTab == FarmModalTab.PipShop);

            if (modalGoldText != null) modalGoldText.text = $"GOLD: {inv.Gold}G";

            if (modalTitleText != null)
            {
                modalTitleText.text = ActiveTab switch
                {
                    FarmModalTab.Craft => "CRAFTING BENCH · POTIONS & SALVES",
                    FarmModalTab.PipShop => "PIP'S HERB STALL · SEEDS & RECIPES",
                    _ => "HOMESTEAD SATCHEL · SEEDS, HERBS & POTIONS"
                };
            }

            if (modalStandingText != null)
            {
                if (ActiveTab == FarmModalTab.PipShop)
                {
                    RelationshipState standing = mgr.GetPipStanding();
                    modalStandingText.text = "PIP: " + ItemCatalog.GetStandingPriceNote(standing);
                }
                else if (ActiveTab == FarmModalTab.Craft)
                {
                    modalStandingText.text = "BREW HARVESTED HERBS INTO POTIONS";
                }
                else
                {
                    string sel = inv.ResolvePlantableSeed();
                    CropDef crop = ItemCatalog.GetCropForSeed(sel);
                    modalStandingText.text = crop != null
                        ? $"ACTIVE SEED: {crop.DisplayName.ToUpperInvariant()}"
                        : "VISIT PIP TO BUY SEEDS";
                }
            }

            ClearRows();
            if (contentContainer == null) return;
            contentContainer.anchoredPosition = Vector2.zero;

            switch (ActiveTab)
            {
                case FarmModalTab.Bag:
                    BuildBagRows(mgr, inv);
                    break;
                case FarmModalTab.Craft:
                    BuildCraftRows(mgr, inv);
                    break;
                case FarmModalTab.PipShop:
                    BuildShopRows(mgr, inv);
                    break;
            }
        }

        private void BuildBagRows(FrontierFarmManager mgr, InventoryState inv)
        {
            bool anyItem = false;
            for (int i = 0; i < ItemCatalog.AllItems.Length; i++)
            {
                ItemDef item = ItemCatalog.AllItems[i];
                int count = inv.GetCount(item.Id);
                if (count <= 0) continue;
                anyItem = true;

                string left = $"{item.DisplayName.ToUpperInvariant()} x{count}";
                string sub = item.Description;
                if (item.Category == ItemCategory.Seed)
                {
                    bool equipped = inv.SelectedSeedId == item.Id;
                    AddActionRow(left, sub, equipped ? "EQUIPPED" : "EQUIP", !equipped,
                        equipped ? FrontierPalette.FrontierTeal : FrontierPalette.ElderIndigo,
                        () => { inv.SelectSeed(item.Id); SynthAudioManager.Instance?.PlayUiClick(); });
                }
                else if (item.Category == ItemCategory.Potion)
                {
                    string id = item.Id;
                    AddActionRow(left, sub, "DRINK", true, FrontierPalette.HearthGold,
                        () => { mgr.UsePotion(id, out string msg); ShowFeedbackToast(msg); });
                }
                else
                {
                    AddActionRow(left, $"{sub} (SELL {item.SellPrice}G AT PIP'S)", "HERB", false, FrontierPalette.ForgeSoot, null);
                }
            }

            if (!anyItem)
            {
                AddInfoRow("Your satchel is empty. Visit Pip's Herb Stall in the SE square to buy seeds!");
            }
        }

        private void BuildCraftRows(FrontierFarmManager mgr, InventoryState inv)
        {
            for (int i = 0; i < ItemCatalog.AllRecipes.Length; i++)
            {
                RecipeDef recipe = ItemCatalog.AllRecipes[i];
                ItemDef output = ItemCatalog.GetItem(recipe.OutputItemId);
                bool unlocked = inv.IsRecipeUnlocked(recipe.Id);
                bool canCraft = inv.CanCraft(recipe.Id);

                string ingText = FormatIngredients(recipe, inv);
                string desc = output != null ? $"{ingText}  |  {output.Description}" : ingText;
                int owned = inv.GetCount(recipe.OutputItemId);
                string title = owned > 0
                    ? $"{recipe.DisplayName.ToUpperInvariant()} (OWN {owned})"
                    : recipe.DisplayName.ToUpperInvariant();

                if (!unlocked)
                {
                    AddActionRow(title, desc, "LOCKED (PIP)", false, FrontierPalette.ForgeSoot, null);
                }
                else
                {
                    string rid = recipe.Id;
                    AddActionRow(title, desc, canCraft ? "CRAFT" : "NEED HERBS", canCraft,
                        canCraft ? FrontierPalette.FrontierTeal : FrontierPalette.ForgeSoot,
                        () => { mgr.CraftRecipe(rid, out string msg); ShowFeedbackToast(msg); });
                }
            }
        }

        private void BuildShopRows(FrontierFarmManager mgr, InventoryState inv)
        {
            RelationshipState standing = mgr.GetPipStanding();

            // 1. Buy Seeds
            for (int i = 0; i < ItemCatalog.AllItems.Length; i++)
            {
                ItemDef item = ItemCatalog.AllItems[i];
                if (item.Category != ItemCategory.Seed) continue;
                int price = ItemCatalog.GetPipPrice(item.BuyPrice, standing);
                bool canAfford = inv.Gold >= price;
                int owned = inv.GetCount(item.Id);
                string id = item.Id;
                AddActionRow(
                    $"{item.DisplayName.ToUpperInvariant()} (OWN {owned})",
                    item.Description,
                    $"BUY {price}G",
                    canAfford,
                    canAfford ? FrontierPalette.HerbOlive : FrontierPalette.ForgeSoot,
                    () => { mgr.BuySeedFromPip(id, 1, out string msg); ShowFeedbackToast(msg); });
            }

            // 2. Unlock Recipes
            for (int i = 0; i < ItemCatalog.AllRecipes.Length; i++)
            {
                RecipeDef recipe = ItemCatalog.AllRecipes[i];
                if (recipe.UnlockedByDefault) continue;
                bool unlocked = inv.IsRecipeUnlocked(recipe.Id);
                int price = ItemCatalog.GetPipPrice(recipe.PipUnlockPrice, standing);
                bool canAfford = !unlocked && inv.Gold >= price;
                string rid = recipe.Id;
                AddActionRow(
                    $"RECIPE: {recipe.DisplayName.ToUpperInvariant()}",
                    FormatIngredients(recipe, inv),
                    unlocked ? "OWNED" : $"BUY {price}G",
                    canAfford,
                    unlocked ? FrontierPalette.SlatePanel : (canAfford ? FrontierPalette.HearthGold : FrontierPalette.ForgeSoot),
                    () => { mgr.BuyRecipeFromPip(rid, out string msg); ShowFeedbackToast(msg); });
            }

            // 3. Sell Harvested Herbs
            for (int i = 0; i < ItemCatalog.AllItems.Length; i++)
            {
                ItemDef item = ItemCatalog.AllItems[i];
                if (item.Category != ItemCategory.Herb) continue;
                int owned = inv.GetCount(item.Id);
                if (owned <= 0) continue;
                string id = item.Id;
                AddActionRow(
                    $"SELL {item.DisplayName.ToUpperInvariant()} (HAVE {owned})",
                    item.Description,
                    $"SELL +{item.SellPrice}G",
                    true,
                    FrontierPalette.FrontierTeal,
                    () => { mgr.SellItemToPip(id, 1, out string msg); ShowFeedbackToast(msg); });
            }
        }

        private static string FormatIngredients(RecipeDef recipe, InventoryState inv)
        {
            string s = string.Empty;
            for (int i = 0; i < recipe.Ingredients.Length; i++)
            {
                var ing = recipe.Ingredients[i];
                ItemDef item = ItemCatalog.GetItem(ing.ItemId);
                string name = item != null ? item.DisplayName : ing.ItemId;
                int have = inv.GetCount(ing.ItemId);
                string part = $"{ing.Count}x {name} ({have})";
                s = s.Length == 0 ? part : s + ", " + part;
            }
            return s;
        }

        private void ClearRows()
        {
            for (int i = 0; i < _spawnedRows.Count; i++)
            {
                if (_spawnedRows[i] != null) Destroy(_spawnedRows[i]);
            }
            _spawnedRows.Clear();
        }

        private void AddInfoRow(string text)
        {
            var row = new GameObject("InfoRow", typeof(RectTransform), typeof(LayoutElement));
            row.transform.SetParent(contentContainer, false);
            var le = row.GetComponent<LayoutElement>();
            le.preferredHeight = 46f;
            le.flexibleWidth = 1f;
            var tmp = CreateTmp(row.transform, "InfoText", text, 16f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            Stretch(tmp.rectTransform, 10f, 4f);
            _spawnedRows.Add(row);
        }

        private void AddActionRow(string title, string subtitle, string buttonLabel, bool interactable, Color buttonColor, Action onClick)
        {
            var row = new GameObject("Row_" + _spawnedRows.Count, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            row.transform.SetParent(contentContainer, false);
            var bg = row.GetComponent<Image>();
            bg.sprite = solidSprite;
            bg.color = FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.72f);
            bg.raycastTarget = false;

            var le = row.GetComponent<LayoutElement>();
            le.preferredHeight = 52f;
            le.minHeight = 48f;
            le.flexibleWidth = 1f;

            var titleTmp = CreateTmp(row.transform, "Title", title, 16f, FrontierPalette.HearthGold, TextAlignmentOptions.Left);
            titleTmp.textWrappingMode = TextWrappingModes.NoWrap;
            titleTmp.overflowMode = TextOverflowModes.Truncate;
            var tRt = titleTmp.rectTransform;
            tRt.anchorMin = new Vector2(0f, 0.5f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = new Vector2(12f, 0f);
            tRt.offsetMax = new Vector2(-174f, -2f);

            var subTmp = CreateTmp(row.transform, "Subtitle", subtitle, 12f, FrontierPalette.WithAlpha(FrontierPalette.CreamParchment, 0.85f), TextAlignmentOptions.Left);
            subTmp.fontStyle = FontStyles.Normal;
            subTmp.characterSpacing = 0.8f;
            subTmp.textWrappingMode = TextWrappingModes.NoWrap;
            subTmp.overflowMode = TextOverflowModes.Truncate;
            var sRt = subTmp.rectTransform;
            sRt.anchorMin = new Vector2(0f, 0f);
            sRt.anchorMax = new Vector2(1f, 0.5f);
            sRt.offsetMin = new Vector2(12f, 2f);
            sRt.offsetMax = new Vector2(-174f, 0f);

            var btnGo = new GameObject("ActionButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(row.transform, false);
            var btnImg = btnGo.GetComponent<Image>();
            btnImg.sprite = panelSprite;
            btnImg.type = Image.Type.Sliced;
            btnImg.color = buttonColor;
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(1f, 0.5f);
            btnRt.anchorMax = new Vector2(1f, 0.5f);
            btnRt.pivot = new Vector2(1f, 0.5f);
            btnRt.anchoredPosition = new Vector2(-8f, 0f);
            btnRt.sizeDelta = new Vector2(156f, 40f);

            var btn = btnGo.GetComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.interactable = interactable && onClick != null;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var lbl = CreateTmp(btnGo.transform, "Label", buttonLabel, 14f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            lbl.textWrappingMode = TextWrappingModes.NoWrap;
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = 10f;
            lbl.fontSizeMax = 14f;
            Stretch(lbl.rectTransform, 6f, 4f);

            _spawnedRows.Add(row);
        }

        private TextMeshProUGUI CreateTmp(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = FontStyles.Bold;
            tmp.characterSpacing = 1.6f;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            if (tmpOutlineMaterial != null) tmp.fontSharedMaterial = tmpOutlineMaterial;
            return tmp;
        }

        private static void Stretch(RectTransform rt, float padX, float padY)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        private static void TintTab(Button btn, bool active)
        {
            if (btn == null || btn.targetGraphic == null) return;
            btn.targetGraphic.color = active ? FrontierPalette.FrontierTeal : FrontierPalette.SlatePanel;
        }
    }
}
