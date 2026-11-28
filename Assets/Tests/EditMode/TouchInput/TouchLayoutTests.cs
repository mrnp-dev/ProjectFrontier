using NUnit.Framework;
using ProjectFrontier.TouchInput;
using UnityEngine;

namespace ProjectFrontier.TouchInput.Tests
{
    public class TouchLayoutTests
    {
        private const float Eps = 1e-4f;

        [Test]
        public void SafeArea_FullScreen_GivesUnitAnchors()
        {
            TouchLayoutMath.SafeAreaAnchors(new Rect(0, 0, 2400, 1080), 2400, 1080, out Vector2 min, out Vector2 max);
            Assert.AreEqual(Vector2.zero, min);
            Assert.AreEqual(Vector2.one, max);
        }

        [Test]
        public void SafeArea_LandscapeNotchAndGestureBar_InsetsAnchors()
        {
            // 20:9 landscape, camera cutout on the left (100 px), gesture bar at the bottom (40 px).
            TouchLayoutMath.SafeAreaAnchors(new Rect(100, 40, 2300, 1040), 2400, 1080, out Vector2 min, out Vector2 max);
            Assert.AreEqual(100f / 2400f, min.x, Eps);
            Assert.AreEqual(40f / 1080f, min.y, Eps);
            Assert.AreEqual(1f, max.x, Eps);
            Assert.AreEqual(1f, max.y, Eps);
        }

        [Test]
        public void SafeArea_Degenerate_FallsBackToFullScreen()
        {
            TouchLayoutMath.SafeAreaAnchors(new Rect(0, 0, 0, 0), 2400, 1080, out Vector2 min, out Vector2 max);
            Assert.AreEqual(Vector2.zero, min);
            Assert.AreEqual(Vector2.one, max);
            TouchLayoutMath.SafeAreaAnchors(new Rect(0, 0, 100, 100), 0, 0, out min, out max);
            Assert.AreEqual(Vector2.one, max);
        }

        [Test]
        public void SafeAreaFitter_AppliesAnchors_ToRectTransform()
        {
            var go = new GameObject("SafeArea", typeof(RectTransform));
            try
            {
                var fitter = go.AddComponent<SafeAreaFitter>();
                fitter.Apply(new Rect(120, 0, 2160, 1080), 2400, 1080);
                var rt = (RectTransform)go.transform;
                Assert.AreEqual(0.05f, rt.anchorMin.x, Eps);
                Assert.AreEqual(0.95f, rt.anchorMax.x, Eps);
                Assert.AreEqual(Vector2.zero, rt.offsetMin);
                Assert.AreEqual(Vector2.zero, rt.offsetMax);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void FloatingRing_FollowsThumb_InsideZone()
        {
            Vector2 zone = new Vector2(512, 432);
            Assert.AreEqual(new Vector2(300, 200), TouchLayoutMath.ClampRingCenter(new Vector2(300, 200), zone, 88));
        }

        [Test]
        public void FloatingRing_ClampedSoWholeRingStaysInZone()
        {
            Vector2 zone = new Vector2(512, 432);
            Assert.AreEqual(new Vector2(88, 88), TouchLayoutMath.ClampRingCenter(new Vector2(5, 10), zone, 88));
            Assert.AreEqual(new Vector2(424, 344), TouchLayoutMath.ClampRingCenter(new Vector2(600, 500), zone, 88));
            // Zone narrower than the ring: centre on that axis.
            Assert.AreEqual(new Vector2(60, 88), TouchLayoutMath.ClampRingCenter(new Vector2(10, 10), new Vector2(120, 432), 88));
        }

        [Test]
        public void StickValue_KeepsFeel_ClampsToRadius()
        {
            const float radius = 54f;
            Vector2 v = TouchLayoutMath.StickValue(new Vector2(27, 0), radius, out Vector2 knob);
            Assert.AreEqual(0.5f, v.x, Eps);
            Assert.AreEqual(new Vector2(27, 0), knob);

            v = TouchLayoutMath.StickValue(new Vector2(0, -300), radius, out knob);
            Assert.AreEqual(-1f, v.y, Eps);
            Assert.AreEqual(1f, v.magnitude, Eps);
            Assert.AreEqual(radius, knob.magnitude, Eps);

            Assert.AreEqual(Vector2.zero, TouchLayoutMath.StickValue(Vector2.zero, radius, out _));
        }
    }
}
