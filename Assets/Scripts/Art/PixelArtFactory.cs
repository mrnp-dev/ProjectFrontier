using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier
{
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

        public static Sprite GetCottageSprite()
        {
            return GetOrBuild("prop_cottage", 48, 44, (w, h, px) =>
            {
                // Timber walls
                FillRect(px, w, h, 6, 2, 36, 22, FrontierPalette.CreamParchment);
                FillRect(px, w, h, 6, 2, 36, 4, FrontierPalette.CobbleStone); // stone foundation
                FillRect(px, w, h, 6, 2, 3, 22, FrontierPalette.TimberDark);  // left beam
                FillRect(px, w, h, 39, 2, 3, 22, FrontierPalette.TimberDark); // right beam
                FillRect(px, w, h, 22, 2, 3, 22, FrontierPalette.TimberDark); // center beam
                // Door
                FillRect(px, w, h, 12, 2, 8, 13, FrontierPalette.TimberDark);
                FillRect(px, w, h, 13, 3, 6, 11, FrontierPalette.TimberWarm);
                SetPx(px, w, h, 18, 8, FrontierPalette.HearthGold);
                // Warm lit window
                FillRect(px, w, h, 28, 10, 8, 7, FrontierPalette.TimberDark);
                FillRect(px, w, h, 29, 11, 6, 5, FrontierPalette.HearthGold);
                FillRect(px, w, h, 31, 11, 1, 5, FrontierPalette.TimberDark);
                FillRect(px, w, h, 29, 13, 6, 1, FrontierPalette.TimberDark);
                // Terracotta pitched roof
                for (int row = 0; row < 16; row++)
                {
                    int inset = row;
                    int x0 = 2 + inset;
                    int rw = w - 4 - inset * 2;
                    if (rw <= 2) break;
                    Color roofCol = (row % 3 == 0) ? FrontierPalette.TimberDark : FrontierPalette.Terracotta;
                    FillRect(px, w, h, x0, 23 + row, rw, 1, roofCol);
                }
                // Chimney
                FillRect(px, w, h, 33, 32, 5, 9, FrontierPalette.CobbleStone);
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
