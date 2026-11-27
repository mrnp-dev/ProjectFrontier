using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Interactive frontier villager NPC with dialogue lines and optional first-talk lantern gift.
    /// </summary>
    public class VillageNpc : MonoBehaviour
    {
        public static readonly List<VillageNpc> AllNpcs = new List<VillageNpc>();

        private static readonly string[] FallbackLines = { "..." };

        [Tooltip("Persona card asset (Assets/Data/Personas). Identity and greeting come from here.")]
        public NpcPersona persona;
        public bool grantsBonusShardOnFirstTalk = false;

        public string NpcId => persona != null ? persona.npcId : name;
        public string DisplayName => persona != null ? persona.displayName : name;
        public string RoleTitle => persona != null ? persona.roleTitle : string.Empty;
        public string[] DialogueLines => persona != null && persona.greetingLines != null && persona.greetingLines.Length > 0
            ? persona.greetingLines
            : FallbackLines;
        [Header("Portrait (Chatbox #14)")]
        public Sprite portraitHeadSprite;
        public Sprite portraitBodySprite;
        public Color accentColor = Color.clear;

        public Transform promptIndicator;

        public Sprite PortraitHeadSprite
        {
            get
            {
                if (portraitHeadSprite == null)
                    portraitHeadSprite = transform.Find("VisualRoot/Head")?.GetComponent<SpriteRenderer>()?.sprite;
                return portraitHeadSprite;
            }
        }

        public Sprite PortraitBodySprite
        {
            get
            {
                if (portraitBodySprite == null)
                    portraitBodySprite = transform.Find("VisualRoot/Body")?.GetComponent<SpriteRenderer>()?.sprite;
                return portraitBodySprite;
            }
        }

        public Color AccentColor
        {
            get
            {
                if (accentColor.a > 0.01f) return accentColor;
                return ResolveDefaultAccent(NpcId);
            }
        }

        public static Color ResolveDefaultAccent(string npcId) => (npcId ?? string.Empty).ToLowerInvariant() switch
        {
            "bram" => FrontierPalette.EmberOrange,
            "mira" => FrontierPalette.TavernWine,
            "aldric" => FrontierPalette.GuildNavy,
            "lyra" => FrontierPalette.RivalViolet,
            "pip" => FrontierPalette.HerbOlive,
            _ => FrontierPalette.HearthGold
        };

        private bool _grantedBonus;
        private Vector3 _promptBasePos;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            AllNpcs.Clear();
        }

        private void OnEnable()
        {
            if (!AllNpcs.Contains(this)) AllNpcs.Add(this);
        }

        private void OnDisable()
        {
            AllNpcs.Remove(this);
        }

        private void Awake()
        {
            if (promptIndicator != null)
            {
                _promptBasePos = promptIndicator.localPosition;
                promptIndicator.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (promptIndicator != null && promptIndicator.gameObject.activeSelf)
            {
                float bob = Mathf.Sin(Time.time * 4.5f) * 0.06f;
                promptIndicator.localPosition = _promptBasePos + new Vector3(0f, bob, 0f);
            }
        }

        public void SetPromptHighlighted(bool active)
        {
            if (promptIndicator != null)
            {
                promptIndicator.gameObject.SetActive(active);
            }
        }

        public void OnDialogueCompleted(FrontierGameLoop loop)
        {
            FrontierSocialManager.Instance?.RecordFirstMeeting(NpcId);
            if (grantsBonusShardOnFirstTalk && !_grantedBonus && loop != null)
            {
                _grantedBonus = true;
                PlayerController2D.Instance?.Motion?.PlayPickupPop();
                loop.CollectLantern(transform.position + Vector3.up * 0.6f, 1);
            }
        }
    }
}
