using System;
using System.Collections.Generic;
using ProjectFrontier.Farm;
using ProjectFrontier.Guild;
using ProjectFrontier.Social;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectFrontier
{
    public enum GuildModalTab
    {
        Board = 0,
        Active = 1,
        Rank = 2
    }

    /// <summary>
    /// Native-resolution uGUI for the Adventurers Guild (#20): Captain Aldric's Quest Board,
    /// Active Contracts tracker, and Guild Rank Ledger, plus a compact top-left HUD Rank strip.
    /// Refreshes strictly on state/inventory/flavor events (zero per-frame allocations).
    /// </summary>
    public class FrontierGuildUI : MonoBehaviour
    {
        public static FrontierGuildUI Instance { get; private set; }
        public static bool IsAnyModalOpen => Instance != null && Instance.IsOpen;

        [Header("HUD Strip")]
        public TextMeshProUGUI hudRankSummaryText;
        public Button hudGuildButton;

        [Header("Modal Shell")]
        public GameObject modalRoot;
        public TextMeshProUGUI modalTitleText;
        public TextMeshProUGUI modalRankBadgeText;
        public TextMeshProUGUI modalSubheaderText;
        public TextMeshProUGUI feedbackToastText;
        public Button closeButton;
        public Button tabBoardButton;
        public Button tabActiveButton;
        public Button tabRankButton;
        public RectTransform contentContainer;

        [Header("Styling Assets")]
        public Sprite panelSprite;
        public Sprite solidSprite;
        public Material tmpOutlineMaterial;

        public bool IsOpen { get; private set; }
        public GuildModalTab ActiveTab { get; private set; } = GuildModalTab.Board;

        private readonly List<GameObject> _spawnedRows = new List<GameObject>(16);
        private float _toastTimer;
        private bool _wired;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

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
            if (FrontierGuildManager.Instance != null)
            {
                FrontierGuildManager.Instance.State.Changed += HandleGuildChanged;
                FrontierGuildManager.Instance.StatusFeedback += ShowFeedbackToast;
                FrontierGuildManager.Instance.FlavorUpdated += HandleFlavorUpdated;
            }
            if (FrontierFarmManager.Instance != null && FrontierFarmManager.Instance.Inventory != null)
            {
                FrontierFarmManager.Instance.Inventory.Changed += HandleGuildChanged;
            }
            if (modalRoot != null) modalRoot.SetActive(false);
            IsOpen = false;
            RefreshHudStrip();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (FrontierGuildManager.Instance != null)
            {
                if (FrontierGuildManager.Instance.State != null)
                    FrontierGuildManager.Instance.State.Changed -= HandleGuildChanged;
                FrontierGuildManager.Instance.StatusFeedback -= ShowFeedbackToast;
                FrontierGuildManager.Instance.FlavorUpdated -= HandleFlavorUpdated;
            }
            if (FrontierFarmManager.Instance != null && FrontierFarmManager.Instance.Inventory != null)
            {
                FrontierFarmManager.Instance.Inventory.Changed -= HandleGuildChanged;
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
        }

        private void EnsureWired()
        {
            if (_wired) return;
            _wired = true;

            if (panelSprite == null) panelSprite = PixelArtFactory.GetUiPanelSprite();
            if (solidSprite == null) solidSprite = PixelArtFactory.GetSolidUiSprite();

            if (hudGuildButton != null) hudGuildButton.onClick.AddListener(ToggleBoard);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (tabBoardButton != null) tabBoardButton.onClick.AddListener(() => SwitchTab(GuildModalTab.Board));
            if (tabActiveButton != null) tabActiveButton.onClick.AddListener(() => SwitchTab(GuildModalTab.Active));
            if (tabRankButton != null) tabRankButton.onClick.AddListener(() => SwitchTab(GuildModalTab.Rank));
        }

        public void ToggleBoard()
        {
            if (IsOpen && ActiveTab == GuildModalTab.Board) Close();
            else Open(GuildModalTab.Board);
        }

        public void Open(GuildModalTab tab)
        {
            EnsureWired();
            var loop = FrontierGameLoop.Instance;
            if (loop != null && loop.State != RunState.Running) return;
            if (loop != null && loop.DialogueOpen) loop.CloseDialogue(completeInteraction: true);
            FrontierFarmUI.Instance?.Close();

            ActiveTab = tab;
            IsOpen = true;
            if (modalRoot != null) modalRoot.SetActive(true);
            SynthAudioManager.Instance?.PlayUiClick();
            RefreshModal();
            if (FrontierGuildManager.Instance != null)
                _ = FrontierGuildManager.Instance.RefreshBoardFlavorAsync();
        }

        public void SwitchTab(GuildModalTab tab)
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
            _toastTimer = 3.2f;
        }

        private void HandleGuildChanged()
        {
            RefreshHudStrip();
            if (IsOpen) RefreshModal();
        }

        private void HandleFlavorUpdated()
        {
            if (IsOpen && (ActiveTab == GuildModalTab.Board || ActiveTab == GuildModalTab.Active))
                RefreshModal();
        }

        public void RefreshHudStrip()
        {
            var mgr = FrontierGuildManager.Instance;
            if (hudRankSummaryText == null || mgr == null || mgr.State == null) return;
            var state = mgr.State;
            var inv = FrontierFarmManager.Instance?.Inventory;

            string rankShort = GuildCatalog.GetRankShortLabel(state.Rank);
            int ready = state.CountReadyToTurnIn(inv);
            if (ready > 0)
            {
                hudRankSummaryText.text = $"{rankShort} · {ready} READY TO TURN IN!";
                hudRankSummaryText.color = FrontierPalette.HearthGold;
            }
            else if (state.ActiveBounties.Count > 0)
            {
                hudRankSummaryText.text = $"{rankShort} · {state.ActiveBounties.Count}/{GuildState.MaxActiveBounties} ACTIVE · {state.GuildXp} XP";
                hudRankSummaryText.color = FrontierPalette.CreamParchment;
            }
            else
            {
                int nextXp = GuildCatalog.GetNextRankXpThreshold(state.Rank);
                string xpPart = state.Rank == GuildRank.Vanguard ? $"{state.GuildXp} XP (MAX)" : $"{state.GuildXp}/{nextXp} XP";
                hudRankSummaryText.text = $"RANK: {rankShort} · {xpPart}";
                hudRankSummaryText.color = FrontierPalette.CreamParchment;
            }
        }

        public void RefreshModal()
        {
            var mgr = FrontierGuildManager.Instance;
            if (mgr == null || mgr.State == null) return;
            var state = mgr.State;
            var inv = FrontierFarmManager.Instance?.Inventory;

            TintTab(tabBoardButton, ActiveTab == GuildModalTab.Board);
            TintTab(tabActiveButton, ActiveTab == GuildModalTab.Active);
            TintTab(tabRankButton, ActiveTab == GuildModalTab.Rank);

            if (modalRankBadgeText != null)
            {
                modalRankBadgeText.text = $"{GuildCatalog.GetRankShortLabel(state.Rank)} · {state.GuildXp} XP";
            }

            ClearRows();
            if (contentContainer != null) contentContainer.anchoredPosition = Vector2.zero;

            switch (ActiveTab)
            {
                case GuildModalTab.Board:
                    BuildBoardTab(mgr, state, inv);
                    break;
                case GuildModalTab.Active:
                    BuildActiveTab(mgr, state, inv);
                    break;
                case GuildModalTab.Rank:
                    BuildRankTab(mgr, state);
                    break;
            }
        }

        private void BuildBoardTab(FrontierGuildManager mgr, GuildState state, InventoryState inv)
        {
            if (modalTitleText != null) modalTitleText.text = "ADVENTURERS GUILD · QUEST BOARD";
            if (modalSubheaderText != null)
            {
                modalSubheaderText.text = $"DAY {mgr.CurrentDay} BOUNTIES · ACTIVE {state.ActiveBounties.Count}/{GuildState.MaxActiveBounties} · NOTICES: {mgr.LastFlavorSource}";
            }

            var boardIds = state.BoardBountyIds;
            if (boardIds.Count == 0)
            {
                AddInfoRow("NO BOUNTIES POSTED TODAY. CHECK BACK TOMORROW.");
                return;
            }

            for (int i = 0; i < boardIds.Count; i++)
            {
                BountyDef def = GuildCatalog.GetBounty(boardIds[i]);
                if (def == null) continue;

                bool isActive = state.IsActive(def.Id);
                bool isDoneToday = state.IsCompletedToday(def.Id);
                bool rankLocked = state.Rank < def.MinRank;
                bool ready = isActive && state.IsReadyToTurnIn(def.Id, inv);
                int progress = isActive ? state.GetEffectiveProgress(state.FindActive(def.Id), inv) : 0;

                string rankTag = GuildCatalog.GetRankShortLabel(def.MinRank);
                string header = $"[{rankTag}] {def.Title.ToUpperInvariant()}";
                string flavor = mgr.GetBountyDescription(def);
                string progStr = isActive ? $" ({progress}/{def.RequiredCount})" : string.Empty;
                string meta = $"{def.ObjectiveSummary}{progStr}  |  +{def.GoldReward}G  +{def.GuildXpReward} XP  |  Aldric +{def.AldricTrustDelta}T/+{def.AldricRespectDelta}R";

                string btnLabel;
                bool interactable;
                Color btnColor;
                Action onClick = null;
                string capturedId = def.Id;

                if (ready)
                {
                    btnLabel = "TURN IN";
                    interactable = true;
                    btnColor = FrontierPalette.HearthGold;
                    onClick = () => mgr.TurnInBounty(capturedId, out _);
                }
                else if (isActive)
                {
                    btnLabel = $"{progress}/{def.RequiredCount}";
                    interactable = false;
                    btnColor = FrontierPalette.GuildNavy;
                }
                else if (isDoneToday)
                {
                    btnLabel = "DONE";
                    interactable = false;
                    btnColor = FrontierPalette.DeepPine;
                }
                else if (rankLocked)
                {
                    btnLabel = $"LOCKED ({rankTag})";
                    interactable = false;
                    btnColor = FrontierPalette.ForgeSoot;
                }
                else
                {
                    bool canAccept = state.CanAccept(def.Id, out _);
                    btnLabel = canAccept ? "ACCEPT" : "FULL (2/2)";
                    interactable = canAccept;
                    btnColor = canAccept ? FrontierPalette.FrontierTeal : FrontierPalette.ForgeSoot;
                    onClick = () => mgr.AcceptBounty(capturedId, out _);
                }

                AddBountyCard(header, flavor, meta, btnLabel, interactable, btnColor, onClick);
            }
        }

        private void BuildActiveTab(FrontierGuildManager mgr, GuildState state, InventoryState inv)
        {
            if (modalTitleText != null) modalTitleText.text = "ACTIVE GUILD CONTRACTS";
            if (modalSubheaderText != null)
            {
                modalSubheaderText.text = $"ACTIVE {state.ActiveBounties.Count}/{GuildState.MaxActiveBounties} · ABANDONING A CONTRACT COSTS ALDRIC STANDING";
            }

            if (state.ActiveBounties.Count == 0)
            {
                AddInfoRow("NO ACTIVE CONTRACTS. ACCEPT A BOUNTY ON THE QUEST BOARD.");
                return;
            }

            for (int i = 0; i < state.ActiveBounties.Count; i++)
            {
                ActiveBountyRecord rec = state.ActiveBounties[i];
                BountyDef def = GuildCatalog.GetBounty(rec.bountyId);
                if (def == null) continue;

                int prog = state.GetEffectiveProgress(rec, inv);
                bool ready = prog >= def.RequiredCount;
                string header = $"{def.Title.ToUpperInvariant()} ({prog}/{def.RequiredCount})";
                string flavor = mgr.GetBountyDescription(def);
                string meta = $"{def.ObjectiveSummary}  |  Reward: +{def.GoldReward}G, +{def.GuildXpReward} XP  |  Accepted Day {rec.acceptedDay}";

                string capturedId = def.Id;
                if (ready)
                {
                    AddBountyCard(header, flavor, meta, "TURN IN", true, FrontierPalette.HearthGold, () => mgr.TurnInBounty(capturedId, out _));
                }
                else
                {
                    AddBountyCard(header, flavor, meta, "ABANDON", true, FrontierPalette.WithAlpha(FrontierPalette.DuskAlert, 0.88f), () => mgr.AbandonBounty(capturedId, out _));
                }
            }
        }

        private void BuildRankTab(FrontierGuildManager mgr, GuildState state)
        {
            if (modalTitleText != null) modalTitleText.text = "GUILD RANK LEDGER · CAPTAIN ALDRIC";
            Relationship aldric = FrontierSocialManager.Instance != null
                ? FrontierSocialManager.Instance.Get("aldric")
                : new Relationship("aldric");
            if (modalSubheaderText != null)
            {
                modalSubheaderText.text = $"ALDRIC STANDING: {aldric.State.ToString().ToUpperInvariant()} (T{aldric.Trust:+0;-0;0} R{aldric.Respect:+0;-0;0}) · COMPLETED: {state.CompletedCount} · ABANDONED: {state.AbandonedCount}";
            }

            int nextXp = GuildCatalog.GetNextRankXpThreshold(state.Rank);
            string summaryRight = state.Rank == GuildRank.Vanguard
                ? "MAX RANK"
                : $"{state.GuildXp}/{nextXp} XP";
            AddRankRow(
                $"CURRENT: {GuildCatalog.GetRankTitle(state.Rank)}",
                GuildCatalog.GetRankPerkSummary(state.Rank),
                summaryRight,
                FrontierPalette.HearthGold);

            for (int r = (int)GuildRank.Bronze; r <= (int)GuildRank.Vanguard; r++)
            {
                var tier = (GuildRank)r;
                int reqXp = GuildCatalog.GetXpThreshold(tier);
                bool unlocked = state.Rank >= tier;
                bool isCurrent = state.Rank == tier;
                string badge = isCurrent ? "CURRENT" : (unlocked ? "UNLOCKED" : $"{reqXp} XP");
                Color badgeColor = isCurrent
                    ? FrontierPalette.FrontierTeal
                    : (unlocked ? FrontierPalette.HerbOlive : FrontierPalette.ForgeSoot);
                AddRankRow(GuildCatalog.GetRankTitle(tier), GuildCatalog.GetRankPerkSummary(tier), badge, badgeColor);
            }
        }

        // ------------------------------------------------------------------ row builders

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
            le.preferredHeight = 52f;
            le.flexibleWidth = 1f;
            var tmp = CreateTmp(row.transform, "InfoText", text, 15f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            Stretch(tmp.rectTransform, 10f, 4f);
            _spawnedRows.Add(row);
        }

        private void AddBountyCard(string title, string flavor, string meta, string buttonLabel, bool interactable, Color buttonColor, Action onClick)
        {
            var row = new GameObject("BountyRow_" + _spawnedRows.Count, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            row.transform.SetParent(contentContainer, false);
            var bg = row.GetComponent<Image>();
            bg.sprite = solidSprite;
            bg.color = FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.78f);
            bg.raycastTarget = false;

            var le = row.GetComponent<LayoutElement>();
            le.preferredHeight = 82f;
            le.minHeight = 76f;
            le.flexibleWidth = 1f;

            var titleTmp = CreateTmp(row.transform, "Title", title, 15f, FrontierPalette.HearthGold, TextAlignmentOptions.Left);
            titleTmp.textWrappingMode = TextWrappingModes.NoWrap;
            titleTmp.overflowMode = TextOverflowModes.Truncate;
            var tRt = titleTmp.rectTransform;
            tRt.anchorMin = new Vector2(0f, 1f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.pivot = new Vector2(0.5f, 1f);
            tRt.offsetMin = new Vector2(12f, -24f);
            tRt.offsetMax = new Vector2(-168f, -4f);

            var flavorTmp = CreateTmp(row.transform, "Flavor", flavor, 12f, FrontierPalette.WithAlpha(FrontierPalette.CreamParchment, 0.90f), TextAlignmentOptions.TopLeft);
            flavorTmp.fontStyle = FontStyles.Italic;
            flavorTmp.characterSpacing = 0.6f;
            flavorTmp.textWrappingMode = TextWrappingModes.Normal;
            flavorTmp.overflowMode = TextOverflowModes.Truncate;
            var fRt = flavorTmp.rectTransform;
            fRt.anchorMin = new Vector2(0f, 0f);
            fRt.anchorMax = new Vector2(1f, 1f);
            fRt.offsetMin = new Vector2(12f, 22f);
            fRt.offsetMax = new Vector2(-168f, -25f);

            var metaTmp = CreateTmp(row.transform, "Meta", meta, 11f, FrontierPalette.BrassBorder, TextAlignmentOptions.Left);
            metaTmp.fontStyle = FontStyles.Bold;
            metaTmp.characterSpacing = 0.8f;
            metaTmp.textWrappingMode = TextWrappingModes.NoWrap;
            metaTmp.overflowMode = TextOverflowModes.Truncate;
            var mRt = metaTmp.rectTransform;
            mRt.anchorMin = new Vector2(0f, 0f);
            mRt.anchorMax = new Vector2(1f, 0f);
            mRt.pivot = new Vector2(0.5f, 0f);
            mRt.offsetMin = new Vector2(12f, 4f);
            mRt.offsetMax = new Vector2(-168f, 22f);

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
            btnRt.sizeDelta = new Vector2(150f, 44f);

            var btn = btnGo.GetComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.interactable = interactable && onClick != null;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var lbl = CreateTmp(btnGo.transform, "Label", buttonLabel, 13f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            lbl.textWrappingMode = TextWrappingModes.NoWrap;
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = 9f;
            lbl.fontSizeMax = 13f;
            Stretch(lbl.rectTransform, 6f, 4f);

            _spawnedRows.Add(row);
        }

        private void AddRankRow(string title, string subtitle, string badgeLabel, Color badgeColor)
        {
            var row = new GameObject("RankRow_" + _spawnedRows.Count, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            row.transform.SetParent(contentContainer, false);
            var bg = row.GetComponent<Image>();
            bg.sprite = solidSprite;
            bg.color = FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.72f);
            bg.raycastTarget = false;

            var le = row.GetComponent<LayoutElement>();
            le.preferredHeight = 52f;
            le.minHeight = 48f;
            le.flexibleWidth = 1f;

            var titleTmp = CreateTmp(row.transform, "Title", title, 15f, FrontierPalette.HearthGold, TextAlignmentOptions.Left);
            titleTmp.textWrappingMode = TextWrappingModes.NoWrap;
            titleTmp.overflowMode = TextOverflowModes.Truncate;
            var tRt = titleTmp.rectTransform;
            tRt.anchorMin = new Vector2(0f, 0.5f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = new Vector2(12f, 0f);
            tRt.offsetMax = new Vector2(-168f, -2f);

            var subTmp = CreateTmp(row.transform, "Subtitle", subtitle, 12f, FrontierPalette.WithAlpha(FrontierPalette.CreamParchment, 0.85f), TextAlignmentOptions.Left);
            subTmp.fontStyle = FontStyles.Normal;
            subTmp.characterSpacing = 0.7f;
            subTmp.textWrappingMode = TextWrappingModes.NoWrap;
            subTmp.overflowMode = TextOverflowModes.Truncate;
            var sRt = subTmp.rectTransform;
            sRt.anchorMin = new Vector2(0f, 0f);
            sRt.anchorMax = new Vector2(1f, 0.5f);
            sRt.offsetMin = new Vector2(12f, 2f);
            sRt.offsetMax = new Vector2(-168f, 0f);

            var badgeBg = new GameObject("RankBadge", typeof(RectTransform), typeof(Image));
            badgeBg.transform.SetParent(row.transform, false);
            var bImg = badgeBg.GetComponent<Image>();
            bImg.sprite = panelSprite;
            bImg.type = Image.Type.Sliced;
            bImg.color = badgeColor;
            bImg.raycastTarget = false;
            var bRt = badgeBg.GetComponent<RectTransform>();
            bRt.anchorMin = new Vector2(1f, 0.5f);
            bRt.anchorMax = new Vector2(1f, 0.5f);
            bRt.pivot = new Vector2(1f, 0.5f);
            bRt.anchoredPosition = new Vector2(-8f, 0f);
            bRt.sizeDelta = new Vector2(148f, 34f);

            var bLbl = CreateTmp(badgeBg.transform, "Label", badgeLabel, 13f, FrontierPalette.CreamParchment, TextAlignmentOptions.Center);
            bLbl.textWrappingMode = TextWrappingModes.NoWrap;
            bLbl.enableAutoSizing = true;
            bLbl.fontSizeMin = 9f;
            bLbl.fontSizeMax = 13f;
            Stretch(bLbl.rectTransform, 6f, 2f);

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
            tmp.characterSpacing = 1.4f;
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
            btn.targetGraphic.color = active ? FrontierPalette.GuildNavy : FrontierPalette.SlatePanel;
        }
    }
}
