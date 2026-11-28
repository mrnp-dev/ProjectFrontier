using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>Signature look for each vertical-slice villager sprite.</summary>
    public enum CastLook { Bram, Mira, Aldric, Lyra, Pip }

    /// <summary>Building variants sharing the 48x44 cottage frame.</summary>
    public enum BuildingStyle { Cottage, Forge, Tavern, GuildHall }

    /// <summary>
    /// Generates crisp 16-PPU pixel-art sprites at runtime (FilterMode.Point, MipMaps disabled).
    /// Enforces consistent pixel density and 1px dark (#1A1F24) outline discipline across gameplay sprites.
    /// </summary>
    public static class PixelArtFactory
    {
        public const float PPU = 16f;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCache()
        {
            Cache.Clear();
        }

        public static Sprite GetSolidUiSprite() => GetOrBuild("ui_solid", 4, 4, (w, h, px) =>
        {
            FillRect(px, w, h, 0, 0, w, h, Color.white);
        }, ppu: 16f);

        public static Sprite GetUiPanelSprite() => GetOrBuild("ui_panel", 16, 16, (w, h, px) =>
        {
            Color slateBody = Color.Lerp(FrontierPalette.SlatePanel, FrontierPalette.CreamParchment, 0.08f);
            FillRect(px, w, h, 0, 0, w, h, FrontierPalette.InkDark);
            FillRect(px, w, h, 1, 1, w - 2, h - 2, FrontierPalette.BrassBorder);
            FillRect(px, w, h, 2, 2, w - 4, h - 4, slateBody);
            SetPx(px, w, h, 2, h - 3, FrontierPalette.BrassBorder);
            SetPx(px, w, h, w - 3, h - 3, FrontierPalette.BrassBorder);
            SetPx(px, w, h, 2, 2, FrontierPalette.BrassBorder);
            SetPx(px, w, h, w - 3, 2, FrontierPalette.BrassBorder);
        }, ppu: 16f, border: new Vector4(4, 4, 4, 4));

        public static Sprite GetUiAccentButtonSprite() => GetOrBuild("ui_btn_accent", 16, 16, (w, h, px) =>
        {
            FillRect(px, w, h, 0, 0, w, h, new Color(0.28f, 0.28f, 0.28f, 1f));
            FillRect(px, w, h, 1, 1, w - 2, h - 2, FrontierPalette.CreamParchment);
            FillRect(px, w, h, 2, 2, w - 4, h - 4, Color.white);
            SetPx(px, w, h, 2, h - 3, new Color(0.85f, 0.85f, 0.85f, 1f));
            SetPx(px, w, h, w - 3, h - 3, new Color(0.85f, 0.85f, 0.85f, 1f));
        }, ppu: 16f, border: new Vector4(4, 4, 4, 4));

        public static Sprite GetCircleUiSprite(string key, int size, Color fill, Color ring)
        {
            return GetOrBuild(key, size, size, (w, h, px) =>
            {
                float cx = (w - 1) * 0.5f;
                float cy = (h - 1) * 0.5f;
                float rOuter = w * 0.48f;
                float rInner = rOuter - 2.2f;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                    if (d <= rInner) SetPx(px, w, h, x, y, fill);
                    else if (d <= rOuter) SetPx(px, w, h, x, y, ring);
                }
            }, ppu: 16f);
        }

        public static Sprite GetGrassTile(int variant)
        {
            return GetOrBuild("tile_grass_" + variant, 16, 16, (w, h, px) =>
            {
                FillRect(px, w, h, 0, 0, w, h, FrontierPalette.MeadowMoss);
                System.Random rng = new System.Random(101 + variant * 37);
                for (int i = 0; i < 18; i++)
                {
                    int x = rng.Next(0, 16);
                    int y = rng.Next(0, 16);
                    SetPx(px, w, h, x, y, (i % 3 == 0) ? FrontierPalette.DeepPine : FrontierPalette.MeadowLight);
                }
                if (variant % 3 == 1)
                {
                    // Tiny wildflower pixel cluster
                    SetPx(px, w, h, 5, 9, FrontierPalette.MeadowLight);
                    SetPx(px, w, h, 5, 10, FrontierPalette.HearthGold);
                    SetPx(px, w, h, 11, 4, FrontierPalette.CreamParchment);
                }
            });
        }

        public static Sprite GetDirtTile(int variant)
        {
            return GetOrBuild("tile_dirt_" + variant, 16, 16, (w, h, px) =>
            {
                FillRect(px, w, h, 0, 0, w, h, FrontierPalette.DirtPath);
                System.Random rng = new System.Random(303 + variant * 19);
                for (int i = 0; i < 14; i++)
                {
                    SetPx(px, w, h, rng.Next(0, 16), rng.Next(0, 16), FrontierPalette.DirtEdge);
                }
                SetPx(px, w, h, 3, 12, FrontierPalette.CreamParchment);
                SetPx(px, w, h, 12, 5, FrontierPalette.TimberWarm);
            });
        }

        public static Sprite GetCobbleTile()
        {
            return GetOrBuild("tile_cobble", 16, 16, (w, h, px) =>
            {
                FillRect(px, w, h, 0, 0, w, h, FrontierPalette.DirtEdge);
                FillRect(px, w, h, 1, 1, 6, 6, FrontierPalette.CobbleStone);
                FillRect(px, w, h, 9, 1, 6, 6, FrontierPalette.CobbleStone);
                FillRect(px, w, h, 1, 9, 6, 6, FrontierPalette.CobbleStone);
                FillRect(px, w, h, 9, 9, 6, 6, FrontierPalette.CobbleStone);
                FillRect(px, w, h, 2, 5, 4, 1, FrontierPalette.CreamParchment);
                FillRect(px, w, h, 10, 13, 4, 1, FrontierPalette.CreamParchment);
            });
        }

        public static Sprite GetCharacterBodySprite(string key, Color tunicColor, Color trimColor)
        {
            return GetOrBuild("body_" + key, 14, 12, (w, h, px) =>
            {
                FillRect(px, w, h, 3, 1, 8, 9, tunicColor);
                FillRect(px, w, h, 3, 2, 8, 2, trimColor);
                FillRect(px, w, h, 6, 2, 2, 2, FrontierPalette.HearthGold); // belt buckle
                FillRect(px, w, h, 2, 4, 1, 4, tunicColor); // sleeves
                FillRect(px, w, h, 11, 4, 1, 4, tunicColor);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetCharacterHeadSprite(string key, Color hairColor, Color hatColor)
        {
            return GetOrBuild("head_" + key, 14, 12, (w, h, px) =>
            {
                FillRect(px, w, h, 4, 2, 6, 5, FrontierPalette.SkinWarm);
                SetPx(px, w, h, 5, 4, FrontierPalette.InkDark); // left eye
                SetPx(px, w, h, 8, 4, FrontierPalette.InkDark); // right eye
                FillRect(px, w, h, 3, 6, 8, 2, hairColor);
                FillRect(px, w, h, 2, 7, 10, 2, hatColor);
                FillRect(px, w, h, 4, 9, 6, 2, hatColor);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        // ---------------------------------------------------------------------------------
        // Vertical-slice cast (Bram, Mira, Aldric, Lyra, Pip): same 14x12 head/body rig as the hero
        // so CharacterMotion2D animates them identically, but each has a unique silhouette cue
        // (beard, bun, helm crest, ponytail, leaf hat) that reads at 480x270.
        // ---------------------------------------------------------------------------------

        public static Sprite GetCastHeadSprite(CastLook look)
        {
            return GetOrBuild("head_cast_" + look, 14, 12, (w, h, px) =>
            {
                Color skin = FrontierPalette.SkinWarm;
                FillRect(px, w, h, 4, 2, 6, 5, skin);
                SetPx(px, w, h, 5, 4, FrontierPalette.InkDark);
                SetPx(px, w, h, 8, 4, FrontierPalette.InkDark);

                switch (look)
                {
                    case CastLook.Bram:
                        // Bald dome, soot headband, thick brows, big ember-ginger beard
                        FillRect(px, w, h, 4, 7, 6, 2, skin);
                        FillRect(px, w, h, 5, 9, 4, 1, skin);
                        FillRect(px, w, h, 3, 7, 8, 1, FrontierPalette.ForgeSoot);
                        FillRect(px, w, h, 3, 5, 1, 2, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 10, 5, 1, 2, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 4, 5, 2, 1, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 8, 5, 2, 1, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 3, 1, 8, 3, FrontierPalette.EmberOrange);
                        FillRect(px, w, h, 4, 0, 6, 1, FrontierPalette.EmberOrange);
                        SetPx(px, w, h, 6, 2, FrontierPalette.InkDark);
                        SetPx(px, w, h, 7, 2, FrontierPalette.InkDark);
                        break;

                    case CastLook.Mira:
                        // Auburn hair framing the face, tall bun with cream ribbon, rosy cheeks
                        SetPx(px, w, h, 4, 3, Color.Lerp(skin, FrontierPalette.Terracotta, 0.55f));
                        SetPx(px, w, h, 9, 3, Color.Lerp(skin, FrontierPalette.Terracotta, 0.55f));
                        SetPx(px, w, h, 6, 2, FrontierPalette.TimberDark);
                        SetPx(px, w, h, 7, 2, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 3, 6, 8, 2, FrontierPalette.AuburnHair);
                        FillRect(px, w, h, 4, 8, 6, 1, FrontierPalette.AuburnHair);
                        FillRect(px, w, h, 3, 3, 1, 3, FrontierPalette.AuburnHair);
                        FillRect(px, w, h, 10, 3, 1, 3, FrontierPalette.AuburnHair);
                        FillRect(px, w, h, 5, 9, 4, 2, FrontierPalette.AuburnHair);
                        FillRect(px, w, h, 5, 9, 4, 1, FrontierPalette.CreamParchment);
                        SetPx(px, w, h, 7, 10, Color.Lerp(FrontierPalette.AuburnHair, FrontierPalette.HearthGold, 0.4f));
                        break;

                    case CastLook.Aldric:
                        // Steel helm with nasal guard, cheek guards, gold crest, grey moustache
                        Color brim = Color.Lerp(FrontierPalette.SteelPlate, FrontierPalette.InkDark, 0.3f);
                        FillRect(px, w, h, 3, 6, 8, 3, FrontierPalette.SteelPlate);
                        FillRect(px, w, h, 3, 6, 8, 1, brim);
                        FillRect(px, w, h, 4, 9, 6, 1, FrontierPalette.SteelPlate);
                        FillRect(px, w, h, 6, 10, 2, 1, FrontierPalette.HearthGold);
                        FillRect(px, w, h, 3, 3, 1, 3, FrontierPalette.SteelPlate);
                        FillRect(px, w, h, 10, 3, 1, 3, FrontierPalette.SteelPlate);
                        FillRect(px, w, h, 6, 4, 2, 2, brim);
                        FillRect(px, w, h, 5, 2, 4, 1, FrontierPalette.CobbleStone);
                        SetPx(px, w, h, 5, 8, FrontierPalette.CreamParchment);
                        break;

                    case CastLook.Lyra:
                        // Platinum hair with asymmetric fringe, violet headband, ponytail, smirk
                        FillRect(px, w, h, 3, 6, 8, 3, FrontierPalette.PlatinumHair);
                        FillRect(px, w, h, 4, 9, 6, 1, FrontierPalette.PlatinumHair);
                        FillRect(px, w, h, 4, 5, 2, 1, FrontierPalette.PlatinumHair);
                        FillRect(px, w, h, 3, 4, 1, 2, FrontierPalette.PlatinumHair);
                        FillRect(px, w, h, 10, 4, 1, 3, FrontierPalette.PlatinumHair);
                        FillRect(px, w, h, 3, 7, 8, 1, FrontierPalette.RivalViolet);
                        FillRect(px, w, h, 11, 3, 1, 5, FrontierPalette.PlatinumHair);
                        FillRect(px, w, h, 12, 2, 1, 4, FrontierPalette.PlatinumHair);
                        SetPx(px, w, h, 11, 7, FrontierPalette.RivalViolet);
                        SetPx(px, w, h, 8, 5, FrontierPalette.InkDark);
                        SetPx(px, w, h, 7, 2, FrontierPalette.TimberDark);
                        SetPx(px, w, h, 8, 2, FrontierPalette.TimberDark);
                        break;

                    case CastLook.Pip:
                        // Big floppy leaf hat with a flower, messy sandy hair, freckles
                        SetPx(px, w, h, 4, 3, FrontierPalette.TimberWarm);
                        SetPx(px, w, h, 9, 3, FrontierPalette.TimberWarm);
                        SetPx(px, w, h, 6, 2, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 3, 6, 8, 1, FrontierPalette.SandyHair);
                        SetPx(px, w, h, 3, 5, FrontierPalette.SandyHair);
                        FillRect(px, w, h, 10, 4, 1, 2, FrontierPalette.SandyHair);
                        FillRect(px, w, h, 1, 7, 12, 1, FrontierPalette.HerbOlive);
                        FillRect(px, w, h, 3, 8, 8, 2, FrontierPalette.MeadowLight);
                        FillRect(px, w, h, 5, 10, 3, 1, FrontierPalette.MeadowLight);
                        FillRect(px, w, h, 3, 8, 8, 1, FrontierPalette.HerbOlive);
                        SetPx(px, w, h, 9, 10, FrontierPalette.HearthGold);
                        SetPx(px, w, h, 10, 9, FrontierPalette.CreamParchment);
                        break;
                }
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetCastBodySprite(CastLook look)
        {
            return GetOrBuild("body_cast_" + look, 14, 12, (w, h, px) =>
            {
                Color skin = FrontierPalette.SkinWarm;
                switch (look)
                {
                    case CastLook.Bram:
                        // Broad soot shirt, bare forearms, leather apron with straps
                        FillRect(px, w, h, 3, 1, 8, 9, FrontierPalette.ForgeSoot);
                        FillRect(px, w, h, 1, 4, 2, 4, skin);
                        FillRect(px, w, h, 11, 4, 2, 4, skin);
                        FillRect(px, w, h, 2, 7, 1, 2, FrontierPalette.ForgeSoot);
                        FillRect(px, w, h, 11, 7, 1, 2, FrontierPalette.ForgeSoot);
                        FillRect(px, w, h, 4, 1, 6, 7, FrontierPalette.TimberWarm);
                        FillRect(px, w, h, 4, 8, 1, 2, FrontierPalette.TimberWarm);
                        FillRect(px, w, h, 9, 8, 1, 2, FrontierPalette.TimberWarm);
                        FillRect(px, w, h, 6, 3, 2, 2, FrontierPalette.TimberDark);
                        SetPx(px, w, h, 5, 6, FrontierPalette.EmberOrange);
                        break;

                    case CastLook.Mira:
                        // Wine dress, cream apron and collar, cuffed sleeves
                        FillRect(px, w, h, 3, 1, 8, 9, FrontierPalette.TavernWine);
                        FillRect(px, w, h, 2, 4, 1, 4, FrontierPalette.TavernWine);
                        FillRect(px, w, h, 11, 4, 1, 4, FrontierPalette.TavernWine);
                        SetPx(px, w, h, 2, 4, FrontierPalette.CreamParchment);
                        SetPx(px, w, h, 11, 4, FrontierPalette.CreamParchment);
                        FillRect(px, w, h, 3, 6, 8, 1, FrontierPalette.CreamParchment);
                        FillRect(px, w, h, 5, 1, 4, 5, FrontierPalette.CreamParchment);
                        FillRect(px, w, h, 6, 9, 2, 1, FrontierPalette.CreamParchment);
                        SetPx(px, w, h, 6, 3, FrontierPalette.HearthGold);
                        break;

                    case CastLook.Aldric:
                        // Navy guild cape behind a steel breastplate, brass pauldrons, gold belt
                        FillRect(px, w, h, 2, 1, 10, 9, FrontierPalette.GuildNavy);
                        FillRect(px, w, h, 4, 2, 6, 7, FrontierPalette.SteelPlate);
                        FillRect(px, w, h, 5, 6, 1, 2, FrontierPalette.CreamParchment);
                        FillRect(px, w, h, 4, 3, 6, 1, FrontierPalette.BrassBorder);
                        FillRect(px, w, h, 6, 3, 2, 1, FrontierPalette.HearthGold);
                        FillRect(px, w, h, 1, 8, 3, 2, FrontierPalette.BrassBorder);
                        FillRect(px, w, h, 10, 8, 3, 2, FrontierPalette.BrassBorder);
                        SetPx(px, w, h, 7, 6, FrontierPalette.GuildNavy);
                        break;

                    case CastLook.Lyra:
                        // Violet jerkin, diagonal leather bandolier with gold buckle, bracers
                        FillRect(px, w, h, 3, 1, 8, 9, FrontierPalette.RivalViolet);
                        FillRect(px, w, h, 2, 4, 1, 4, FrontierPalette.RivalViolet);
                        FillRect(px, w, h, 11, 4, 1, 4, FrontierPalette.RivalViolet);
                        FillRect(px, w, h, 2, 4, 1, 2, FrontierPalette.TimberWarm);
                        FillRect(px, w, h, 11, 4, 1, 2, FrontierPalette.TimberWarm);
                        for (int i = 0; i < 8; i++)
                        {
                            SetPx(px, w, h, 3 + i, 9 - i, FrontierPalette.TimberWarm);
                            if (4 + i <= 10) SetPx(px, w, h, 4 + i, 9 - i, FrontierPalette.TimberWarm);
                        }
                        SetPx(px, w, h, 6, 6, FrontierPalette.HearthGold);
                        FillRect(px, w, h, 3, 2, 8, 1, FrontierPalette.TimberDark);
                        break;

                    case CastLook.Pip:
                        // Olive herbalist robe, rope belt, satchel, tucked sprig
                        FillRect(px, w, h, 3, 1, 8, 9, FrontierPalette.HerbOlive);
                        FillRect(px, w, h, 2, 4, 1, 4, FrontierPalette.HerbOlive);
                        FillRect(px, w, h, 11, 4, 1, 4, FrontierPalette.HerbOlive);
                        FillRect(px, w, h, 3, 4, 8, 1, FrontierPalette.CreamParchment);
                        for (int i = 0; i < 6; i++) SetPx(px, w, h, 9 - i, 9 - i, FrontierPalette.TimberDark);
                        FillRect(px, w, h, 9, 1, 3, 3, FrontierPalette.TimberWarm);
                        SetPx(px, w, h, 10, 3, FrontierPalette.BrassBorder);
                        SetPx(px, w, h, 4, 8, FrontierPalette.MeadowLight);
                        SetPx(px, w, h, 4, 9, FrontierPalette.HearthGold);
                        break;
                }
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetCharacterFootSprite()
        {
            return GetOrBuild("char_foot", 6, 5, (w, h, px) =>
            {
                FillRect(px, w, h, 1, 1, 4, 3, FrontierPalette.TimberDark);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetScarfSprite(Color scarfColor)
        {
            return GetOrBuild("char_scarf", 8, 6, (w, h, px) =>
            {
                FillRect(px, w, h, 1, 2, 6, 2, scarfColor);
                FillRect(px, w, h, 1, 1, 3, 1, FrontierPalette.HearthGold);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetGroundShadowSprite()
        {
            return GetOrBuild("char_shadow", 14, 6, (w, h, px) =>
            {
                Color sh = FrontierPalette.WithAlpha(FrontierPalette.InkDark, 0.42f);
                FillRect(px, w, h, 3, 1, 8, 4, sh);
                FillRect(px, w, h, 1, 2, 12, 2, sh);
            });
        }

        public static Sprite GetTalkBubbleSprite()
        {
            return GetOrBuild("ui_talk_bubble", 13, 12, (w, h, px) =>
            {
                FillRect(px, w, h, 1, 4, 11, 7, FrontierPalette.CreamParchment); // bubble body
                FillRect(px, w, h, 3, 2, 3, 2, FrontierPalette.CreamParchment);  // tail
                FillRect(px, w, h, 3, 1, 1, 1, FrontierPalette.CreamParchment);
                FillRect(px, w, h, 3, 7, 1, 1, FrontierPalette.TimberDark);      // "..." dots
                FillRect(px, w, h, 6, 7, 1, 1, FrontierPalette.TimberDark);
                FillRect(px, w, h, 9, 7, 1, 1, FrontierPalette.TimberDark);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetLanternShardSprite()
        {
            return GetOrBuild("pickup_lantern", 14, 16, (w, h, px) =>
            {
                FillRect(px, w, h, 5, 12, 4, 2, FrontierPalette.BrassBorder); // handle
                FillRect(px, w, h, 4, 10, 6, 2, FrontierPalette.BrassBorder); // cap
                FillRect(px, w, h, 4, 3, 6, 7, FrontierPalette.HearthGold);   // core glow
                FillRect(px, w, h, 6, 5, 2, 4, FrontierPalette.CreamParchment); // inner core
                FillRect(px, w, h, 4, 2, 6, 1, FrontierPalette.BrassBorder);  // base
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetCottageSprite() => GetBuildingSprite(BuildingStyle.Cottage);

        public static Sprite GetBuildingSprite(BuildingStyle style)
        {
            string key = style == BuildingStyle.Cottage ? "prop_cottage" : "prop_building_" + style;
            return GetOrBuild(key, 48, 44, (w, h, px) =>
            {
                Color wall = style == BuildingStyle.Forge
                    ? Color.Lerp(FrontierPalette.CobbleStone, FrontierPalette.ForgeSoot, 0.35f)
                    : FrontierPalette.CreamParchment;
                Color roof = style switch
                {
                    BuildingStyle.Forge => FrontierPalette.ForgeSoot,
                    BuildingStyle.Tavern => FrontierPalette.TavernWine,
                    BuildingStyle.GuildHall => FrontierPalette.GuildNavy,
                    _ => FrontierPalette.Terracotta
                };
                Color windowGlow = style == BuildingStyle.Forge ? FrontierPalette.EmberOrange : FrontierPalette.HearthGold;

                // Timber walls
                FillRect(px, w, h, 6, 2, 36, 22, wall);
                FillRect(px, w, h, 6, 2, 36, 4, FrontierPalette.CobbleStone); // stone foundation
                FillRect(px, w, h, 6, 2, 3, 22, FrontierPalette.TimberDark);  // left beam
                FillRect(px, w, h, 39, 2, 3, 22, FrontierPalette.TimberDark); // right beam
                FillRect(px, w, h, 22, 2, 3, 22, FrontierPalette.TimberDark); // center beam
                // Door
                FillRect(px, w, h, 12, 2, 8, 13, FrontierPalette.TimberDark);
                if (style == BuildingStyle.Forge)
                {
                    // Open forge mouth with ember glow
                    FillRect(px, w, h, 13, 3, 6, 11, FrontierPalette.InkDark);
                    FillRect(px, w, h, 14, 3, 4, 3, FrontierPalette.EmberOrange);
                    FillRect(px, w, h, 15, 4, 2, 1, FrontierPalette.HearthGold);
                }
                else
                {
                    FillRect(px, w, h, 13, 3, 6, 11, FrontierPalette.TimberWarm);
                    SetPx(px, w, h, 18, 8, FrontierPalette.HearthGold);
                }
                // Warm lit window
                FillRect(px, w, h, 28, 10, 8, 7, FrontierPalette.TimberDark);
                FillRect(px, w, h, 29, 11, 6, 5, windowGlow);
                FillRect(px, w, h, 31, 11, 1, 5, FrontierPalette.TimberDark);
                FillRect(px, w, h, 29, 13, 6, 1, FrontierPalette.TimberDark);
                // Pitched roof
                for (int row = 0; row < 16; row++)
                {
                    int inset = row;
                    int x0 = 2 + inset;
                    int rw = w - 4 - inset * 2;
                    if (rw <= 2) break;
                    Color roofCol = (row % 3 == 0) ? FrontierPalette.TimberDark : roof;
                    FillRect(px, w, h, x0, 23 + row, rw, 1, roofCol);
                }
                // Chimney
                FillRect(px, w, h, 33, 32, 5, 9, FrontierPalette.CobbleStone);

                switch (style)
                {
                    case BuildingStyle.Forge:
                        FillRect(px, w, h, 33, 41, 5, 1, FrontierPalette.EmberOrange); // hot chimney lip
                        break;
                    case BuildingStyle.Tavern:
                        // Hanging sign with a foaming mug over the door
                        FillRect(px, w, h, 10, 17, 12, 5, FrontierPalette.TimberWarm);
                        FillRect(px, w, h, 14, 18, 3, 2, FrontierPalette.HearthGold);
                        FillRect(px, w, h, 14, 20, 3, 1, FrontierPalette.CreamParchment);
                        SetPx(px, w, h, 17, 19, FrontierPalette.HearthGold);
                        break;
                    case BuildingStyle.GuildHall:
                        // Navy guild banner with gold emblem hanging from the center beam
                        FillRect(px, w, h, 21, 11, 5, 10, FrontierPalette.GuildNavy);
                        FillRect(px, w, h, 21, 20, 5, 1, FrontierPalette.BrassBorder);
                        FillRect(px, w, h, 23, 15, 1, 3, FrontierPalette.HearthGold);
                        FillRect(px, w, h, 22, 16, 3, 1, FrontierPalette.HearthGold);
                        SetPx(px, w, h, 23, 11, FrontierPalette.TimberDark);
                        break;
                }
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetAnvilSprite()
        {
            return GetOrBuild("prop_anvil", 16, 12, (w, h, px) =>
            {
                FillRect(px, w, h, 5, 1, 6, 4, FrontierPalette.TimberDark);   // stump
                FillRect(px, w, h, 6, 1, 1, 4, FrontierPalette.TimberWarm);
                FillRect(px, w, h, 6, 5, 4, 2, FrontierPalette.ForgeSoot);    // waist
                FillRect(px, w, h, 2, 7, 11, 2, FrontierPalette.ForgeSoot);   // face
                FillRect(px, w, h, 3, 8, 9, 1, FrontierPalette.SteelPlate);   // highlight
                FillRect(px, w, h, 1, 7, 1, 1, FrontierPalette.ForgeSoot);    // horn tip
                FillRect(px, w, h, 7, 9, 4, 1, FrontierPalette.EmberOrange);  // glowing ingot
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetBarrelSprite()
        {
            return GetOrBuild("prop_barrel", 12, 14, (w, h, px) =>
            {
                FillRect(px, w, h, 2, 1, 8, 11, FrontierPalette.TimberWarm);
                FillRect(px, w, h, 3, 1, 1, 11, Color.Lerp(FrontierPalette.TimberWarm, FrontierPalette.CreamParchment, 0.25f));
                FillRect(px, w, h, 2, 3, 8, 1, FrontierPalette.TimberDark);
                FillRect(px, w, h, 2, 9, 8, 1, FrontierPalette.TimberDark);
                FillRect(px, w, h, 3, 12, 6, 1, FrontierPalette.TimberDark);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetQuestBoardSprite()
        {
            return GetOrBuild("prop_questboard", 18, 20, (w, h, px) =>
            {
                FillRect(px, w, h, 2, 1, 2, 10, FrontierPalette.TimberDark);   // posts
                FillRect(px, w, h, 14, 1, 2, 10, FrontierPalette.TimberDark);
                FillRect(px, w, h, 1, 7, 16, 10, FrontierPalette.TimberWarm);  // board
                FillRect(px, w, h, 1, 17, 16, 2, FrontierPalette.GuildNavy);   // guild cap
                FillRect(px, w, h, 3, 9, 4, 5, FrontierPalette.CreamParchment); // notices
                FillRect(px, w, h, 8, 11, 3, 4, FrontierPalette.CreamParchment);
                FillRect(px, w, h, 12, 8, 3, 6, FrontierPalette.CreamParchment);
                SetPx(px, w, h, 4, 13, FrontierPalette.DuskAlert);
                SetPx(px, w, h, 9, 14, FrontierPalette.HearthGold);
                SetPx(px, w, h, 13, 13, FrontierPalette.DuskAlert);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetHerbStallSprite()
        {
            return GetOrBuild("prop_herbstall", 32, 26, (w, h, px) =>
            {
                FillRect(px, w, h, 3, 9, 1, 12, FrontierPalette.TimberDark);   // awning posts
                FillRect(px, w, h, 28, 9, 1, 12, FrontierPalette.TimberDark);
                FillRect(px, w, h, 2, 1, 28, 8, FrontierPalette.TimberWarm);   // counter front
                FillRect(px, w, h, 2, 8, 28, 1, FrontierPalette.TimberDark);   // counter top
                for (int x = 5; x < 28; x += 6) FillRect(px, w, h, x, 2, 1, 5, FrontierPalette.TimberDark);
                // Potted herbs on the counter
                int[] potX = { 5, 13, 21 };
                for (int i = 0; i < potX.Length; i++)
                {
                    FillRect(px, w, h, potX[i], 9, 5, 3, FrontierPalette.Terracotta);
                    FillRect(px, w, h, potX[i] + 1, 12, 3, 2, i == 1 ? FrontierPalette.HerbOlive : FrontierPalette.MeadowLight);
                    SetPx(px, w, h, potX[i] + 2, 14, i == 2 ? FrontierPalette.CreamParchment : FrontierPalette.HearthGold);
                }
                // Striped olive/cream awning with scalloped edge
                for (int x = 1; x < 31; x++)
                {
                    Color stripe = ((x - 1) / 4) % 2 == 0 ? FrontierPalette.HerbOlive : FrontierPalette.CreamParchment;
                    FillRect(px, w, h, x, 20, 1, 4, stripe);
                    if ((x - 1) % 4 == 1 || (x - 1) % 4 == 2) SetPx(px, w, h, x, 19, stripe);
                }
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetPineTreeSprite()
        {
            return GetOrBuild("prop_pine", 24, 32, (w, h, px) =>
            {
                FillRect(px, w, h, 10, 2, 4, 8, FrontierPalette.TimberDark);
                FillRect(px, w, h, 11, 2, 2, 8, FrontierPalette.TimberWarm);
                // Layered pine boughs
                FillRect(px, w, h, 3, 8, 18, 6, FrontierPalette.DeepPine);
                FillRect(px, w, h, 4, 11, 16, 3, FrontierPalette.MeadowMoss);
                FillRect(px, w, h, 5, 14, 14, 6, FrontierPalette.DeepPine);
                FillRect(px, w, h, 6, 17, 12, 3, FrontierPalette.MeadowMoss);
                FillRect(px, w, h, 8, 20, 8, 5, FrontierPalette.MeadowMoss);
                FillRect(px, w, h, 10, 25, 4, 4, FrontierPalette.MeadowLight);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetLanternPostSprite()
        {
            return GetOrBuild("prop_lanternpost", 14, 28, (w, h, px) =>
            {
                FillRect(px, w, h, 6, 2, 2, 20, FrontierPalette.TimberDark);
                FillRect(px, w, h, 6, 21, 5, 2, FrontierPalette.TimberDark);
                FillRect(px, w, h, 8, 15, 4, 6, FrontierPalette.HearthGold);
                FillRect(px, w, h, 9, 17, 2, 2, FrontierPalette.CreamParchment);
                FillRect(px, w, h, 7, 20, 6, 1, FrontierPalette.BrassBorder);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetHearthBrazierSprite()
        {
            return GetOrBuild("prop_hearth", 24, 24, (w, h, px) =>
            {
                FillRect(px, w, h, 4, 2, 16, 7, FrontierPalette.CobbleStone);
                FillRect(px, w, h, 6, 8, 12, 4, FrontierPalette.TimberDark);
                FillRect(px, w, h, 7, 11, 10, 6, FrontierPalette.Terracotta);
                FillRect(px, w, h, 9, 13, 6, 6, FrontierPalette.HearthGold);
                FillRect(px, w, h, 11, 15, 2, 3, FrontierPalette.CreamParchment);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetFenceSprite()
        {
            return GetOrBuild("prop_fence", 16, 12, (w, h, px) =>
            {
                FillRect(px, w, h, 2, 1, 3, 9, FrontierPalette.TimberDark);
                FillRect(px, w, h, 11, 1, 3, 9, FrontierPalette.TimberDark);
                FillRect(px, w, h, 1, 3, 14, 2, FrontierPalette.TimberWarm);
                FillRect(px, w, h, 1, 7, 14, 2, FrontierPalette.TimberWarm);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetPixelSparkSprite()
        {
            return GetOrBuild("fx_spark", 4, 4, (w, h, px) =>
            {
                FillRect(px, w, h, 1, 1, 2, 2, Color.white);
            });
        }

        // ---------------------------------------------------------------------------------
        // Homestead & Farming sprites (#19): 16-PPU soil tiles, crop stages, bench, rest post
        // ---------------------------------------------------------------------------------

        public static Sprite GetSoilTileSprite(int soilState)
        {
            string key = soilState switch
            {
                1 => "tile_soil_tilled",
                2 => "tile_soil_watered",
                _ => "tile_soil_untilled"
            };
            return GetOrBuild(key, 16, 16, (w, h, px) =>
            {
                if (soilState == 0)
                {
                    // Framed meadow plot pad ready to till
                    Color border = Color.Lerp(FrontierPalette.MeadowMoss, FrontierPalette.TimberDark, 0.35f);
                    Color body = Color.Lerp(FrontierPalette.MeadowMoss, FrontierPalette.DirtEdge, 0.28f);
                    FillRect(px, w, h, 0, 0, w, h, border);
                    FillRect(px, w, h, 1, 1, w - 2, h - 2, body);
                    SetPx(px, w, h, 3, 4, FrontierPalette.MeadowLight);
                    SetPx(px, w, h, 11, 10, FrontierPalette.MeadowLight);
                    SetPx(px, w, h, 7, 12, FrontierPalette.DirtPath);
                }
                else
                {
                    bool watered = soilState == 2;
                    Color edge = watered ? FrontierPalette.InkDark : FrontierPalette.TimberDark;
                    Color ridge = watered ? Color.Lerp(FrontierPalette.TimberDark, FrontierPalette.InkDark, 0.45f) : FrontierPalette.TimberWarm;
                    Color furrow = watered ? Color.Lerp(FrontierPalette.InkDark, FrontierPalette.FrontierTeal, 0.16f) : FrontierPalette.TimberDark;
                    FillRect(px, w, h, 0, 0, w, h, edge);
                    FillRect(px, w, h, 1, 1, w - 2, h - 2, ridge);
                    for (int y = 3; y <= 12; y += 3)
                    {
                        FillRect(px, w, h, 2, y, 12, 1, furrow);
                    }
                    if (watered)
                    {
                        // Subtle damp glint pixels
                        SetPx(px, w, h, 4, 5, Color.Lerp(FrontierPalette.FrontierTeal, FrontierPalette.CreamParchment, 0.35f));
                        SetPx(px, w, h, 11, 11, Color.Lerp(FrontierPalette.FrontierTeal, FrontierPalette.CreamParchment, 0.35f));
                    }
                    else
                    {
                        SetPx(px, w, h, 4, 8, FrontierPalette.DirtPath);
                        SetPx(px, w, h, 11, 5, FrontierPalette.DirtEdge);
                    }
                }
            });
        }

        public static Sprite GetCropStageSprite(int stage)
        {
            return GetOrBuild("crop_stage_" + stage, 16, 16, (w, h, px) =>
            {
                if (stage <= 1)
                {
                    // Seedling: tiny twin shoot
                    FillRect(px, w, h, 7, 3, 2, 3, FrontierPalette.MeadowLight);
                    FillRect(px, w, h, 5, 5, 2, 2, FrontierPalette.HerbOlive);
                    FillRect(px, w, h, 9, 5, 2, 2, FrontierPalette.MeadowLight);
                }
                else if (stage == 2)
                {
                    // Sprout: leafy rosette
                    FillRect(px, w, h, 6, 3, 4, 5, FrontierPalette.MeadowMoss);
                    FillRect(px, w, h, 4, 6, 3, 3, FrontierPalette.MeadowLight);
                    FillRect(px, w, h, 9, 6, 3, 3, FrontierPalette.HerbOlive);
                    FillRect(px, w, h, 7, 8, 2, 2, FrontierPalette.MeadowLight);
                }
                else
                {
                    // Growing: full leafy bush with small bud
                    FillRect(px, w, h, 5, 3, 6, 6, FrontierPalette.DeepPine);
                    FillRect(px, w, h, 3, 5, 10, 5, FrontierPalette.MeadowMoss);
                    FillRect(px, w, h, 5, 8, 6, 4, FrontierPalette.MeadowLight);
                    FillRect(px, w, h, 7, 11, 2, 2, FrontierPalette.CreamParchment);
                }
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetMatureCropSprite(string cropId)
        {
            string id = string.IsNullOrEmpty(cropId) ? "moonmint" : cropId.ToLowerInvariant();
            return GetOrBuild("crop_mature_" + id, 16, 16, (w, h, px) =>
            {
                // Lush foliage base
                FillRect(px, w, h, 4, 3, 8, 5, FrontierPalette.DeepPine);
                FillRect(px, w, h, 3, 5, 10, 5, FrontierPalette.MeadowMoss);
                if (id == "sunpetal")
                {
                    // Golden sunlit blossom
                    FillRect(px, w, h, 5, 8, 6, 6, FrontierPalette.HearthGold);
                    FillRect(px, w, h, 4, 9, 8, 4, FrontierPalette.HearthGold);
                    FillRect(px, w, h, 6, 10, 4, 2, FrontierPalette.EmberOrange);
                    SetPx(px, w, h, 7, 12, FrontierPalette.CreamParchment);
                    SetPx(px, w, h, 8, 12, FrontierPalette.CreamParchment);
                }
                else if (id == "emberroot")
                {
                    // Glowing ember bulb & crimson-gold leaves
                    FillRect(px, w, h, 5, 4, 6, 5, FrontierPalette.Terracotta);
                    FillRect(px, w, h, 6, 5, 4, 4, FrontierPalette.EmberOrange);
                    FillRect(px, w, h, 7, 6, 2, 2, FrontierPalette.HearthGold);
                    FillRect(px, w, h, 4, 9, 3, 4, FrontierPalette.HerbOlive);
                    FillRect(px, w, h, 9, 9, 3, 4, FrontierPalette.MeadowLight);
                    SetPx(px, w, h, 7, 11, FrontierPalette.HearthGold);
                }
                else
                {
                    // Moonmint: shimmering silver-teal herb leaves
                    FillRect(px, w, h, 4, 7, 8, 5, FrontierPalette.FrontierTeal);
                    FillRect(px, w, h, 6, 9, 4, 5, FrontierPalette.FrontierTeal);
                    SetPx(px, w, h, 5, 10, FrontierPalette.CreamParchment);
                    SetPx(px, w, h, 7, 12, FrontierPalette.CreamParchment);
                    SetPx(px, w, h, 10, 9, FrontierPalette.CreamParchment);
                }
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetPlotCursorSprite()
        {
            return GetOrBuild("ui_plot_cursor", 16, 16, (w, h, px) =>
            {
                Color hi = FrontierPalette.HearthGold;
                Color dark = FrontierPalette.InkDark;
                // Four crisp corner brackets
                FillRect(px, w, h, 0, 0, 5, 2, dark); FillRect(px, w, h, 0, 0, 2, 5, dark);
                FillRect(px, w, h, 11, 0, 5, 2, dark); FillRect(px, w, h, 14, 0, 2, 5, dark);
                FillRect(px, w, h, 0, 14, 5, 2, dark); FillRect(px, w, h, 0, 11, 2, 5, dark);
                FillRect(px, w, h, 11, 14, 5, 2, dark); FillRect(px, w, h, 14, 11, 2, 5, dark);

                FillRect(px, w, h, 1, 1, 3, 1, hi); FillRect(px, w, h, 1, 1, 1, 3, hi);
                FillRect(px, w, h, 12, 1, 3, 1, hi); FillRect(px, w, h, 14, 1, 1, 3, hi);
                FillRect(px, w, h, 1, 14, 3, 1, hi); FillRect(px, w, h, 1, 12, 1, 3, hi);
                FillRect(px, w, h, 12, 14, 3, 1, hi); FillRect(px, w, h, 14, 12, 1, 3, hi);
            });
        }

        public static Sprite GetCraftingBenchSprite()
        {
            return GetOrBuild("prop_craftingbench", 26, 20, (w, h, px) =>
            {
                // Legs
                FillRect(px, w, h, 3, 1, 3, 8, FrontierPalette.TimberDark);
                FillRect(px, w, h, 20, 1, 3, 8, FrontierPalette.TimberDark);
                // Lower shelf
                FillRect(px, w, h, 4, 3, 18, 2, FrontierPalette.TimberWarm);
                // Tabletop
                FillRect(px, w, h, 2, 8, 22, 4, FrontierPalette.TimberWarm);
                FillRect(px, w, h, 2, 11, 22, 1, FrontierPalette.DirtPath);
                // Mortar & pestle (left)
                FillRect(px, w, h, 5, 12, 5, 3, FrontierPalette.CobbleStone);
                FillRect(px, w, h, 6, 15, 2, 2, FrontierPalette.TimberDark);
                // Herb sprig (middle)
                FillRect(px, w, h, 12, 12, 3, 3, FrontierPalette.HerbOlive);
                SetPx(px, w, h, 13, 15, FrontierPalette.HearthGold);
                // Glowing teal potion flask (right)
                FillRect(px, w, h, 17, 12, 4, 4, FrontierPalette.FrontierTeal);
                FillRect(px, w, h, 18, 16, 2, 2, FrontierPalette.CreamParchment);
                SetPx(px, w, h, 18, 14, FrontierPalette.CreamParchment);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        public static Sprite GetRestPostSprite()
        {
            return GetOrBuild("prop_restpost", 16, 20, (w, h, px) =>
            {
                // Cozy homestead signpost / moon lantern for resting until dawn
                FillRect(px, w, h, 7, 1, 2, 14, FrontierPalette.TimberDark);
                FillRect(px, w, h, 3, 9, 10, 5, FrontierPalette.TimberWarm);
                FillRect(px, w, h, 4, 10, 8, 3, FrontierPalette.ElderIndigo);
                // Crescent moon glyph
                FillRect(px, w, h, 6, 11, 2, 2, FrontierPalette.HearthGold);
                SetPx(px, w, h, 8, 11, FrontierPalette.HearthGold);
                SetPx(px, w, h, 10, 12, FrontierPalette.CreamParchment);
                // Cap
                FillRect(px, w, h, 6, 15, 4, 2, FrontierPalette.BrassBorder);
                ApplyOutline(px, w, h, FrontierPalette.InkDark);
            });
        }

        private static Sprite GetOrBuild(string key, int width, int height, System.Action<int, int, Color[]> paint, float ppu = PPU, Vector4 border = default)
        {
            if (Cache.TryGetValue(key, out Sprite existing) && existing != null)
            {
                return existing;
            }

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = key,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] px = new Color[width * height];
            paint(width, height, px);
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            var sprite = Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect, border);
            sprite.name = key;
            Cache[key] = sprite;
            return sprite;
        }

        private static void FillRect(Color[] px, int w, int h, int x0, int y0, int rw, int rh, Color col)
        {
            for (int y = y0; y < y0 + rh; y++)
            for (int x = x0; x < x0 + rw; x++)
            {
                SetPx(px, w, h, x, y, col);
            }
        }

        private static void SetPx(Color[] px, int w, int h, int x, int y, Color col)
        {
            if (x >= 0 && x < w && y >= 0 && y < h)
            {
                px[y * w + x] = col;
            }
        }

        private static void ApplyOutline(Color[] px, int w, int h, Color outlineColor)
        {
            Color[] copy = (Color[])px.Clone();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (copy[y * w + x].a > 0.1f) continue;
                bool nearOpaque =
                    (x > 0 && copy[y * w + (x - 1)].a > 0.1f) ||
                    (x < w - 1 && copy[y * w + (x + 1)].a > 0.1f) ||
                    (y > 0 && copy[(y - 1) * w + x].a > 0.1f) ||
                    (y < h - 1 && copy[(y + 1) * w + x].a > 0.1f);
                if (nearOpaque)
                {
                    px[y * w + x] = outlineColor;
                }
            }
        }
    }
}
