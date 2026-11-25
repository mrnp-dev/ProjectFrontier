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
        public Transform promptIndicator;

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
