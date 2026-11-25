using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Single source of truth for ProjectFrontier's 2D pixel-art color bible.
    /// Every world sprite, particle, light, and uGUI element traces to these swatches.
    /// </summary>
    public static class FrontierPalette
    {
        public static readonly Color InkDark        = Hex("#1A1F24"); // 1px outlines, text shadow, deep scrim
        public static readonly Color DeepPine       = Hex("#2D3E32"); // Camera bg, pine shadow
        public static readonly Color MeadowMoss     = Hex("#4E7A4A"); // Primary grass tile
        public static readonly Color MeadowLight    = Hex("#5F8F56"); // Sunlit grass highlight
        public static readonly Color DirtPath       = Hex("#C8A878"); // Village path center
        public static readonly Color DirtEdge       = Hex("#B38F5E"); // Village path border
        public static readonly Color TimberDark     = Hex("#6E4B35"); // Cottage walls, trunks, fences
        public static readonly Color TimberWarm     = Hex("#8C6244"); // Timber highlight
        public static readonly Color Terracotta     = Hex("#C95F42"); // Cottage roofs, scout cloak
        public static readonly Color HearthGold     = Hex("#F0B94D"); // Primary accent, lanterns, title
        public static readonly Color FrontierTeal   = Hex("#3E9B95"); // Hero tunic, interactive highlight
        public static readonly Color CreamParchment = Hex("#F4ECD8"); // Light text, skin/stone highlights
        public static readonly Color SlatePanel     = Hex("#263038"); // UI panel fill
        public static readonly Color BrassBorder    = Hex("#9E8254"); // UI border, lantern frame
        public static readonly Color DuskAlert      = Hex("#D9534F"); // Low timer warning, lose accent
        public static readonly Color ElderIndigo    = Hex("#4B5D82"); // Village Elder robe
        public static readonly Color SkinWarm       = Hex("#E6C29F"); // Character faces
        public static readonly Color CobbleStone    = Hex("#8D9499"); // Plaza cobbles

        // Vertical-slice cast accents (one signature hue per villager for silhouette read at 480x270)
        public static readonly Color ForgeSoot      = Hex("#4A4F55"); // Bram's work shirt, forge roof
        public static readonly Color EmberOrange    = Hex("#E07A2F"); // Forge glow, Bram's beard
        public static readonly Color TavernWine     = Hex("#8E3B4A"); // Mira's dress, tavern roof
        public static readonly Color AuburnHair     = Hex("#A0472E"); // Mira's hair
        public static readonly Color SteelPlate     = Hex("#A3ADB5"); // Aldric's helm & breastplate
        public static readonly Color GuildNavy      = Hex("#2E4A7A"); // Aldric's cape, guild hall roof
        public static readonly Color RivalViolet    = Hex("#5B3F7A"); // Lyra's jerkin & headband
        public static readonly Color PlatinumHair   = Hex("#E3D9B8"); // Lyra's hair
        public static readonly Color HerbOlive      = Hex("#7A8F3A"); // Pip's robe, stall awning
        public static readonly Color SandyHair      = Hex("#C9A35A"); // Pip's hair

        public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }
    }
}
