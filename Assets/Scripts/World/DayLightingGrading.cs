using UnityEngine;

namespace ProjectFrontier.World
{
    /// <summary>Evaluated Global Light2D grade and optional point-light scale for a time of day.</summary>
    public readonly struct LightGrade
    {
        public readonly Color Color;
        public readonly float Intensity;
        public readonly float PointLightScale;

        public LightGrade(Color color, float intensity, float pointLightScale)
        {
            Color = color;
            Intensity = intensity;
            PointLightScale = pointLightScale;
        }
    }

    /// <summary>
    /// Pure-C# colour-grading curve for the single Global Light2D across Morning / Midday / Evening / Night.
    /// Keeps Night legible on Low graphics tier (where all local Point Light2Ds remain disabled).
    /// </summary>
    public static class DayLightingGrading
    {
        private readonly struct Keyframe
        {
            public readonly float Time;
            public readonly Color Color;
            public readonly float Intensity;
            public readonly float PointLightScale;

            public Keyframe(float time, string hex, float intensity, float pointLightScale)
            {
                Time = time;
                ColorUtility.TryParseHtmlString(hex, out Color);
                Intensity = intensity;
                PointLightScale = pointLightScale;
            }
        }

        private static readonly Keyframe[] Curve =
        {
            new Keyframe(0.00f, "#FFE5BF", 0.94f, 0.55f), // 06:00 soft golden dawn
            new Keyframe(0.20f, "#FFF8EC", 1.05f, 0.30f), // 09:30 crisp morning
            new Keyframe(0.45f, "#FFFFFF", 1.10f, 0.25f), // 14:00 bright midday
            new Keyframe(0.62f, "#FFC285", 0.92f, 0.70f), // 17:10 warm amber evening
            new Keyframe(0.78f, "#B98BB8", 0.74f, 0.95f), // 20:00 dusk twilight
            new Keyframe(0.88f, "#6E84BA", 0.58f, 1.15f), // 21:50 cozy indigo night
            new Keyframe(1.00f, "#5A70A6", 0.52f, 1.20f)  // 24:00 midnight (legible without point lights)
        };

        public static LightGrade Evaluate(float normalizedTime)
        {
            float t = Mathf.Clamp01(normalizedTime);
            if (t <= Curve[0].Time)
                return new LightGrade(Curve[0].Color, Curve[0].Intensity, Curve[0].PointLightScale);

            for (int i = 0; i < Curve.Length - 1; i++)
            {
                Keyframe a = Curve[i];
                Keyframe b = Curve[i + 1];
                if (t <= b.Time)
                {
                    float span = Mathf.Max(0.0001f, b.Time - a.Time);
                    float u = (t - a.Time) / span;
                    // Smoothstep for gentle sunrise/sunset transitions without harsh kinks.
                    float s = u * u * (3f - 2f * u);
                    return new LightGrade(
                        Color.Lerp(a.Color, b.Color, s),
                        Mathf.Lerp(a.Intensity, b.Intensity, s),
                        Mathf.Lerp(a.PointLightScale, b.PointLightScale, s));
                }
            }

            Keyframe last = Curve[Curve.Length - 1];
            return new LightGrade(last.Color, last.Intensity, last.PointLightScale);
        }

        public static LightGrade EvaluateForPhase(DayPhase phase) =>
            Evaluate(DayClock.GetPhaseMidpoint(phase));

        private static readonly Color MorningAccent = ParseHex("#F4ECD8"); // CreamParchment
        private static readonly Color MiddayAccent = ParseHex("#F0B94D");  // HearthGold
        private static readonly Color EveningAccent = ParseHex("#E07A2F"); // EmberOrange
        private static readonly Color NightAccent = ParseHex("#A7C0E8");   // Moonlit periwinkle

        public static Color GetHudAccentColor(DayPhase phase) => phase switch
        {
            DayPhase.Morning => MorningAccent,
            DayPhase.Midday => MiddayAccent,
            DayPhase.Evening => EveningAccent,
            DayPhase.Night => NightAccent,
            _ => MorningAccent
        };

        private static Color ParseHex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }
    }
}
