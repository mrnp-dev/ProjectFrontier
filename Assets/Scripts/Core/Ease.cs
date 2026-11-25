using System;
using System.Collections;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Lightweight easing curves and coroutine tween helper (unscaled by default for UI/pause safety).
    /// </summary>
    public static class Ease
    {
        public static float Linear(float t) => Mathf.Clamp01(t);

        public static float OutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            float f = 1f - t;
            return 1f - f * f * f;
        }

        public static float OutBack(float t)
        {
            t = Mathf.Clamp01(t);
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float f = t - 1f;
            return 1f + c3 * f * f * f + c1 * f * f;
        }

        public static float OutElastic(float t)
        {
            t = Mathf.Clamp01(t);
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            const float c4 = (2f * Mathf.PI) / 3f;
            return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
        }

        public static IEnumerator Tween(float duration, Action<float> apply, Func<float, float> ease, bool unscaled = true)
        {
            if (duration <= 0.0001f)
            {
                apply?.Invoke(1f);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                float u = Mathf.Clamp01(elapsed / duration);
                apply?.Invoke(ease != null ? ease(u) : u);
                yield return null;
                elapsed += unscaled ? Time.unscaledDeltaTime : Time.deltaTime;
            }

            apply?.Invoke(1f);
        }
    }
}
