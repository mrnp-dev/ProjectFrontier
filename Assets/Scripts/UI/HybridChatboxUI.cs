using System;
using System.Threading.Tasks;
using ProjectFrontier.Llm;
using ProjectFrontier.Social;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectFrontier
{
    /// <summary>
    /// Touch-first hybrid NPC conversation UI (#14, PRD pillar 2):
    /// speaker name, pixel-art portrait, color-coded mood badge, standing and delta indicator,
    /// 2-3 contextual quick-reply chips per NPC turn, free-text input bar with mobile soft-keyboard
    /// lift handling, and an animated waiting/typing state during LLM calls.
    /// </summary>
    public class HybridChatboxUI : MonoBehaviour
    {
        [Header("Panel & Keyboard Lift")]
        public RectTransform panelRect;
        public float restingBottomY = 18f;
        [Tooltip("Editor/PlayMode test hook: simulates mobile soft-keyboard height in screen pixels when > 0.")]
        public float simulatedKeyboardPixelHeight;

        [Header("Header & Speaker")]
        public TextMeshProUGUI speakerNameText;
        public TextMeshProUGUI standingBadgeText;
        public TextMeshProUGUI deltaToastText;
        public Image moodBadgeBackground;
        public TextMeshProUGUI moodBadgeText;
        public Button shopButton;
        public Button closeButton;

        [Header("Portrait")]
        public RectTransform portraitRoot;
        public Image portraitFrameImage;
        public Image portraitBodyImage;
        public Image portraitHeadImage;
        public Image portraitMoodBarImage;

        [Header("Dialogue Body & Status")]
        public TextMeshProUGUI playerEchoText;
        public TextMeshProUGUI dialogueBodyText;
        public TextMeshProUGUI statusHintText;

        [Header("Quick-Reply Chips (2-3)")]
        public Button[] quickReplyButtons = Array.Empty<Button>();
        public TextMeshProUGUI[] quickReplyLabels = Array.Empty<TextMeshProUGUI>();

        [Header("Free-Text Input Bar")]
        public TMP_InputField messageInputField;
        public TextMeshProUGUI inputPlaceholderText;
        public Button sendButton;
        public TextMeshProUGUI sendButtonLabel;

        public bool IsOpen { get; private set; }
        public bool IsWaitingForReply { get; private set; }
        public VillageNpc ActiveNpc => _activeNpc;
        public string CurrentMood { get; private set; } = LlmMoods.Neutral;
        public string[] CurrentQuickReplies { get; private set; } = Array.Empty<string>();
        public LlmTurnResult LastTurnResult { get; private set; }

        public event Action<LlmTurnResult> ReplyRendered;

        private VillageNpc _activeNpc;
        private Canvas _parentCanvas;
        private RectTransform _canvasRect;
        private int _conversationEpoch;
        private float _waitingStartedAt;
        private float _portraitPulseTimer;
        private float _deltaToastTimer;
        private Color _deltaToastBaseColor = FrontierPalette.HearthGold;
        private bool _wired;

        private void Awake()
        {
            EnsureWired();
        }

        private void EnsureWired()
        {
            if (_wired) return;
            _wired = true;

            if (panelRect == null) panelRect = GetComponent<RectTransform>();
            _parentCanvas = GetComponentInParent<Canvas>();
            if (_parentCanvas != null) _canvasRect = _parentCanvas.GetComponent<RectTransform>();

            if (closeButton != null)
                closeButton.onClick.AddListener(HandleCloseClicked);

            if (shopButton != null)
                shopButton.onClick.AddListener(HandleShopClicked);

            if (sendButton != null)
                sendButton.onClick.AddListener(HandleSendClicked);

            if (quickReplyButtons != null)
            {
                for (int i = 0; i < quickReplyButtons.Length; i++)
                {
                    int slot = i;
                    if (quickReplyButtons[slot] != null)
                        quickReplyButtons[slot].onClick.AddListener(() => SelectQuickReply(slot));
                }
            }

            if (messageInputField != null)
            {
                messageInputField.characterLimit = TurnPromptBuilder.MaxPlayerMessageChars;
                messageInputField.lineType = TMP_InputField.LineType.SingleLine;
                messageInputField.shouldHideMobileInput = true;
                messageInputField.resetOnDeActivation = false;
                messageInputField.restoreOriginalTextOnEscape = false;
                messageInputField.onValueChanged.AddListener(_ => RefreshInputControls());
                messageInputField.onSubmit.AddListener(HandleInputSubmitted);
            }
        }

        public void OpenForNpc(VillageNpc npc)
        {
            EnsureWired();
            if (npc == null) return;

            bool sameNpcAlreadyOpen = IsOpen && _activeNpc == npc;
            if (sameNpcAlreadyOpen) return;

            _conversationEpoch++;
            _activeNpc = npc;
            IsOpen = true;
            IsWaitingForReply = false;
            LastTurnResult = null;
            _portraitPulseTimer = 0.25f;
            _deltaToastTimer = 0f;

            if (!gameObject.activeSelf) gameObject.SetActive(true);

            if (speakerNameText != null)
            {
                string role = string.IsNullOrWhiteSpace(npc.RoleTitle) ? string.Empty : $" · {npc.RoleTitle.ToUpperInvariant()}";
                speakerNameText.text = npc.DisplayName.ToUpperInvariant() + role;
            }
            RefreshStandingBadge();
            if (shopButton != null)
                shopButton.gameObject.SetActive(string.Equals(npc.NpcId, "pip", StringComparison.OrdinalIgnoreCase));
            if (deltaToastText != null) deltaToastText.text = string.Empty;

            ApplyPortrait(npc);
            ApplyMood(LlmMoods.Neutral);

            if (playerEchoText != null) playerEchoText.text = string.Empty;
            if (dialogueBodyText != null)
            {
                dialogueBodyText.text = BuildGreeting(npc);
                dialogueBodyText.color = FrontierPalette.CreamParchment;
            }
            UpdateStatusHint(usedFallback: false);
            ShowHeardRumorToast(npc);

            string[] starters = npc.persona != null ? npc.persona.GetStarterQuickReplies() : TurnResponseParser.DefaultQuickReplies;
            int slotCount = quickReplyButtons != null && quickReplyButtons.Length > 0 ? quickReplyButtons.Length : ChatboxHelpers.MaxVisibleChips;
            ApplyQuickReplies(ChatboxHelpers.SelectChips(starters, TurnResponseParser.DefaultQuickReplies, slotCount));
            SetChipsInteractable(true);

            if (messageInputField != null)
            {
                messageInputField.SetTextWithoutNotify(string.Empty);
                messageInputField.interactable = true;
            }
            if (inputPlaceholderText != null)
            {
                inputPlaceholderText.text = $"Say something to {npc.DisplayName}...";
            }
            RefreshInputControls();
            SnapPanelToRestingPosition();
        }

        public void Close()
        {
            if (!IsOpen && !gameObject.activeSelf) return;
            _conversationEpoch++;
            IsOpen = false;
            IsWaitingForReply = false;
            _activeNpc = null;
            simulatedKeyboardPixelHeight = 0f;

            if (messageInputField != null)
            {
                messageInputField.DeactivateInputField();
                messageInputField.SetTextWithoutNotify(string.Empty);
            }
            SnapPanelToRestingPosition();
            gameObject.SetActive(false);
        }

        public void SelectQuickReply(int index)
        {
            if (!IsOpen || IsWaitingForReply || CurrentQuickReplies == null) return;
            if (index < 0 || index >= CurrentQuickReplies.Length) return;
            SubmitPlayerMessage(CurrentQuickReplies[index]);
        }

        public void SubmitPlayerMessage(string rawText)
        {
            if (!IsOpen || IsWaitingForReply || _activeNpc == null) return;
            if (string.IsNullOrWhiteSpace(rawText)) return;
            string trimmed = rawText.Trim();
            if (trimmed.Length == 0) return;

            if (messageInputField != null)
            {
                messageInputField.SetTextWithoutNotify(string.Empty);
                messageInputField.DeactivateInputField();
            }

            SynthAudioManager.Instance?.PlayUiClick();
            _ = SendTurnAsync(trimmed, _conversationEpoch);
        }

        private async Task SendTurnAsync(string playerText, int epoch)
        {
            VillageNpc npc = _activeNpc;
            if (npc == null) return;

            IsWaitingForReply = true;
            _waitingStartedAt = Time.unscaledTime;

            if (playerEchoText != null)
                playerEchoText.text = ChatboxHelpers.FormatPlayerEcho(playerText);

            if (dialogueBodyText != null)
            {
                dialogueBodyText.text = ChatboxHelpers.FormatTypingIndicator(npc.DisplayName, 0f);
                dialogueBodyText.color = FrontierPalette.WithAlpha(FrontierPalette.HearthGold, 0.92f);
            }

            if (statusHintText != null)
                statusHintText.text = "WAITING FOR REPLY...";

            SetChipsInteractable(false);
            RefreshInputControls();

            LlmTurnResult result = null;
            var llm = FrontierLlmManager.Instance;
            if (llm != null && npc.persona != null)
            {
                try
                {
                    result = await llm.SendPlayerMessageAsync(npc.persona, playerText);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Chatbox] Turn failed: {e.Message}");
                }
            }
            else
            {
                string fallbackJson = MockLlmProvider.Respond(playerText);
                result = TurnResponseParser.Parse(fallbackJson, npc.persona?.GetStarterQuickReplies());
                result.NpcId = npc.NpcId;
                result.ProviderName = "offline";
            }

            if (this == null || !IsOpen || epoch != _conversationEpoch) return;

            IsWaitingForReply = false;
            if (result == null)
            {
                if (dialogueBodyText != null)
                {
                    dialogueBodyText.text = TurnResponseParser.SilentLine;
                    dialogueBodyText.color = FrontierPalette.CreamParchment;
                }
                SetChipsInteractable(true);
                RefreshInputControls();
                UpdateStatusHint(usedFallback: true);
                return;
            }

            LastTurnResult = result;
            if (dialogueBodyText != null)
            {
                dialogueBodyText.text = result.Dialogue;
                dialogueBodyText.color = FrontierPalette.CreamParchment;
            }

            ApplyMood(result.Mood);
            _portraitPulseTimer = 0.28f;
            RefreshStandingBadge();
            ShowDeltaToast(result.AppliedDeltas.IsZero ? result.Deltas : result.AppliedDeltas);

            string[] starters = npc.persona != null ? npc.persona.GetStarterQuickReplies() : TurnResponseParser.DefaultQuickReplies;
            int slotCount = quickReplyButtons != null && quickReplyButtons.Length > 0 ? quickReplyButtons.Length : ChatboxHelpers.MaxVisibleChips;
            ApplyQuickReplies(ChatboxHelpers.SelectChips(result.QuickReplies, starters, slotCount));
            SetChipsInteractable(true);
            RefreshInputControls();
            UpdateStatusHint(result.UsedFallbackProvider);
            ReplyRendered?.Invoke(result);
        }

        private void Update()
        {
            if (!IsOpen) return;

            if (IsWaitingForReply && dialogueBodyText != null && _activeNpc != null)
            {
                float elapsed = Time.unscaledTime - _waitingStartedAt;
                dialogueBodyText.text = ChatboxHelpers.FormatTypingIndicator(_activeNpc.DisplayName, elapsed);
            }

            UpdatePortraitPulse();
            UpdateDeltaToastFade();
            UpdateMobileKeyboardLift();
        }

        private void UpdatePortraitPulse()
        {
            if (portraitRoot == null) return;
            if (_portraitPulseTimer > 0f)
            {
                _portraitPulseTimer = Mathf.Max(0f, _portraitPulseTimer - Time.unscaledDeltaTime);
                float t = _portraitPulseTimer / 0.28f;
                float scale = 1f + Mathf.Sin(t * Mathf.PI) * 0.08f;
                portraitRoot.localScale = new Vector3(scale, scale, 1f);
            }
            else if (IsWaitingForReply)
            {
                float pulse = 1f + 0.025f * Mathf.Sin(Time.unscaledTime * 6f);
                portraitRoot.localScale = new Vector3(pulse, pulse, 1f);
            }
            else if (portraitRoot.localScale != Vector3.one)
            {
                portraitRoot.localScale = Vector3.one;
            }
        }

        private void UpdateDeltaToastFade()
        {
            if (deltaToastText == null || _deltaToastTimer <= 0f) return;
            _deltaToastTimer = Mathf.Max(0f, _deltaToastTimer - Time.unscaledDeltaTime);
            float alpha = _deltaToastTimer < 0.8f ? Mathf.Clamp01(_deltaToastTimer / 0.8f) : 1f;
            deltaToastText.color = FrontierPalette.WithAlpha(_deltaToastBaseColor, alpha);
            if (_deltaToastTimer <= 0f) deltaToastText.text = string.Empty;
        }

        private void UpdateMobileKeyboardLift()
        {
            if (panelRect == null) return;
            if (_parentCanvas == null) _parentCanvas = GetComponentInParent<Canvas>();
            if (_canvasRect == null && _parentCanvas != null) _canvasRect = _parentCanvas.GetComponent<RectTransform>();

            bool nativeKeyboardVisible = TouchScreenKeyboard.visible ||
                (messageInputField != null && messageInputField.touchScreenKeyboard != null && messageInputField.touchScreenKeyboard.active);
            bool keyboardVisible = nativeKeyboardVisible || simulatedKeyboardPixelHeight > 1f;
            float kbPixels = simulatedKeyboardPixelHeight > 1f ? simulatedKeyboardPixelHeight : TouchScreenKeyboard.area.height;

            float canvasHeight = _canvasRect != null && _canvasRect.rect.height > 1f ? _canvasRect.rect.height : 720f;
            float scaleFactor = _parentCanvas != null && _parentCanvas.scaleFactor > 0.01f ? _parentCanvas.scaleFactor : 1f;
            float panelHeight = panelRect.rect.height > 1f ? panelRect.rect.height : panelRect.sizeDelta.y;

            float targetY = ChatboxHelpers.ComputeKeyboardLiftY(
                restingBottomY,
                panelHeight,
                canvasHeight,
                kbPixels,
                scaleFactor,
                keyboardVisible);

            Vector2 pos = panelRect.anchoredPosition;
            float nextY = Mathf.Lerp(pos.y, targetY, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
            if (Mathf.Abs(nextY - targetY) < 0.25f) nextY = targetY;
            panelRect.anchoredPosition = new Vector2(pos.x, nextY);
        }

        private void SnapPanelToRestingPosition()
        {
            if (panelRect == null) return;
            Vector2 pos = panelRect.anchoredPosition;
            panelRect.anchoredPosition = new Vector2(pos.x, restingBottomY);
        }

        private void ApplyPortrait(VillageNpc npc)
        {
            if (portraitFrameImage != null)
                portraitFrameImage.color = npc.AccentColor;

            if (portraitBodyImage != null)
            {
                portraitBodyImage.sprite = npc.PortraitBodySprite;
                portraitBodyImage.enabled = portraitBodyImage.sprite != null;
            }
            if (portraitHeadImage != null)
            {
                portraitHeadImage.sprite = npc.PortraitHeadSprite;
                portraitHeadImage.enabled = portraitHeadImage.sprite != null;
            }
        }

        private void ApplyMood(string rawMood)
        {
            ChatboxHelpers.MoodVisual mv = ChatboxHelpers.GetMoodVisual(rawMood);
            CurrentMood = mv.MoodKey;
            Color badgeColor = ChatboxHelpers.ParseHex(mv.BadgeHex, FrontierPalette.ForgeSoot);
            Color labelColor = ChatboxHelpers.ParseHex(mv.TextHex, FrontierPalette.CreamParchment);

            if (moodBadgeBackground != null) moodBadgeBackground.color = badgeColor;
            if (moodBadgeText != null)
            {
                moodBadgeText.text = mv.Label;
                moodBadgeText.color = labelColor;
            }
            if (portraitMoodBarImage != null) portraitMoodBarImage.color = badgeColor;
        }

        private void RefreshStandingBadge()
        {
            if (standingBadgeText == null || _activeNpc == null) return;
            var social = FrontierSocialManager.Instance;
            RelationshipState state = social != null ? social.Get(_activeNpc.NpcId).State : RelationshipState.Neutral;
            standingBadgeText.text = state.ToString().ToUpperInvariant();
        }

        /// <summary>Scripted greeting, opened by a 0-token gossip line when this villager heard a rumor (#16).</summary>
        private static string BuildGreeting(VillageNpc npc)
        {
            string greeting = ChatboxHelpers.FormatGreeting(npc.DialogueLines);
            var social = FrontierSocialManager.Instance;
            string gossip = social != null ? social.GetGossipGreeting(npc.NpcId, DisplayNameOf) : null;
            return string.IsNullOrEmpty(gossip) ? greeting : ChatboxHelpers.FormatGreeting(new[] { gossip, greeting });
        }

        /// <summary>Shows who the villager heard about you from and how it moved their view of you.</summary>
        private void ShowHeardRumorToast(VillageNpc npc)
        {
            var social = FrontierSocialManager.Instance;
            Rumor heard = social != null ? social.GetTopHeardRumor(npc.NpcId) : null;
            if (heard == null || deltaToastText == null) return;
            string source = DisplayNameOf(heard.sourceId).ToUpperInvariant();
            _deltaToastBaseColor = heard.valence < 0f ? FrontierPalette.DuskAlert : FrontierPalette.HearthGold;
            // Compact so it fits the toast slot beside the badges: "VIA BRAM · T-2 A-2".
            string summary = string.Empty;
            if (heard.appliedTrust != 0) summary += " T" + heard.appliedTrust.ToString("+0;-0");
            if (heard.appliedAffection != 0) summary += " A" + heard.appliedAffection.ToString("+0;-0");
            deltaToastText.text = summary.Length == 0 ? "VIA " + source : "VIA " + source + " ·" + summary;
            deltaToastText.color = _deltaToastBaseColor;
            _deltaToastTimer = 4.5f;
        }

        private static string DisplayNameOf(string npcId)
        {
            foreach (var other in VillageNpc.AllNpcs)
                if (other != null && other.NpcId == npcId) return other.DisplayName;
            return npcId;
        }

        private void ShowDeltaToast(AxisDeltas deltas)
        {
            if (deltaToastText == null) return;
            string summary = ChatboxHelpers.FormatDeltaSummary(deltas);
            if (string.IsNullOrEmpty(summary)) return;
            int net = deltas.trust + deltas.affection + deltas.respect;
            _deltaToastBaseColor = net < 0 ? FrontierPalette.DuskAlert : FrontierPalette.HearthGold;
            deltaToastText.text = summary;
            deltaToastText.color = _deltaToastBaseColor;
            _deltaToastTimer = 3.5f;
        }

        private void ApplyQuickReplies(string[] chips)
        {
            CurrentQuickReplies = chips ?? Array.Empty<string>();
            if (quickReplyButtons == null) return;
            for (int i = 0; i < quickReplyButtons.Length; i++)
            {
                Button btn = quickReplyButtons[i];
                if (btn == null) continue;
                bool hasChip = i < CurrentQuickReplies.Length && !string.IsNullOrWhiteSpace(CurrentQuickReplies[i]);
                btn.gameObject.SetActive(hasChip);
                if (hasChip && quickReplyLabels != null && i < quickReplyLabels.Length && quickReplyLabels[i] != null)
                {
                    quickReplyLabels[i].text = CurrentQuickReplies[i];
                }
            }
        }

        private void SetChipsInteractable(bool interactable)
        {
            if (quickReplyButtons == null) return;
            for (int i = 0; i < quickReplyButtons.Length; i++)
            {
                Button btn = quickReplyButtons[i];
                if (btn == null) continue;
                btn.interactable = interactable;
                if (btn.targetGraphic != null)
                {
                    btn.targetGraphic.color = interactable
                        ? FrontierPalette.WithAlpha(FrontierPalette.ElderIndigo, 0.92f)
                        : FrontierPalette.WithAlpha(FrontierPalette.ForgeSoot, 0.55f);
                }
            }
        }

        private void RefreshInputControls()
        {
            bool hasText = messageInputField != null && !string.IsNullOrWhiteSpace(messageInputField.text);
            if (messageInputField != null) messageInputField.interactable = !IsWaitingForReply;
            if (sendButton != null)
            {
                bool canSend = !IsWaitingForReply && hasText;
                sendButton.interactable = canSend;
                if (sendButton.targetGraphic != null)
                {
                    sendButton.targetGraphic.color = canSend
                        ? FrontierPalette.FrontierTeal
                        : FrontierPalette.WithAlpha(FrontierPalette.ForgeSoot, 0.7f);
                }
            }
            if (sendButtonLabel != null)
            {
                sendButtonLabel.text = IsWaitingForReply ? "..." : "SEND";
            }
        }

        private void UpdateStatusHint(bool usedFallback)
        {
            if (statusHintText == null) return;
            var llm = FrontierLlmManager.Instance;
            if (usedFallback || llm == null || !llm.IsOnlineProvider)
            {
                statusHintText.text = "OFFLINE MODE";
                statusHintText.color = FrontierPalette.WithAlpha(FrontierPalette.BrassBorder, 0.85f);
            }
            else
            {
                statusHintText.text = string.Empty;
            }
        }

        private void HandleSendClicked()
        {
            if (messageInputField == null) return;
            SubmitPlayerMessage(messageInputField.text);
        }

        private void HandleInputSubmitted(string submittedText)
        {
            SubmitPlayerMessage(submittedText);
        }

        private void HandleShopClicked()
        {
            if (FrontierFarmUI.Instance != null)
            {
                FrontierFarmUI.Instance.Open(FarmModalTab.PipShop);
            }
            else
            {
                HandleCloseClicked();
            }
        }

        private void HandleCloseClicked()
        {
            SynthAudioManager.Instance?.PlayUiClick();
            if (FrontierGameLoop.Instance != null)
                FrontierGameLoop.Instance.CloseDialogue();
            else
                Close();
        }
    }
}
