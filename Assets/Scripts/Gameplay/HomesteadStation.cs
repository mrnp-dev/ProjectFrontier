using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier
{
    public enum HomesteadStationKind
    {
        CraftingBench = 0,
        PipShop = 1,
        RestBed = 2,
        GuildBoard = 3,
        GuildLedger = 4
    }

    /// <summary>
    /// World-space interactable station (Homestead Crafting Bench, Pip's Herb Stall, Homestead Rest Post).
    /// Mirrors VillageNpc's lightweight static registry so PlayerController2D detects proximity with zero allocations.
    /// </summary>
    public class HomesteadStation : MonoBehaviour
    {
        public static readonly List<HomesteadStation> AllStations = new List<HomesteadStation>(8);

        public HomesteadStationKind stationKind = HomesteadStationKind.CraftingBench;
        public string actionLabel = "CRAFT";
        public float interactRadius = 2.0f;
        public Transform promptIndicator;

        private Vector3 _promptBasePos;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            AllStations.Clear();
        }

        private void OnEnable()
        {
            if (!AllStations.Contains(this)) AllStations.Add(this);
        }

        private void OnDisable()
        {
            AllStations.Remove(this);
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
            if (promptIndicator != null && promptIndicator.gameObject.activeSelf != active)
            {
                promptIndicator.gameObject.SetActive(active);
            }
        }

        public string GetDefaultLabel() => !string.IsNullOrEmpty(actionLabel) ? actionLabel : stationKind switch
        {
            HomesteadStationKind.CraftingBench => "CRAFT",
            HomesteadStationKind.PipShop => "SHOP",
            HomesteadStationKind.RestBed => "REST",
            HomesteadStationKind.GuildBoard => "BOUNTIES",
            HomesteadStationKind.GuildLedger => "RANK",
            _ => "USE"
        };

        public Color GetButtonAccentColor() => stationKind switch
        {
            HomesteadStationKind.CraftingBench => FrontierPalette.HearthGold,
            HomesteadStationKind.PipShop => FrontierPalette.HerbOlive,
            HomesteadStationKind.RestBed => FrontierPalette.ElderIndigo,
            HomesteadStationKind.GuildBoard => FrontierPalette.GuildNavy,
            HomesteadStationKind.GuildLedger => FrontierPalette.HearthGold,
            _ => FrontierPalette.FrontierTeal
        };
    }
}
