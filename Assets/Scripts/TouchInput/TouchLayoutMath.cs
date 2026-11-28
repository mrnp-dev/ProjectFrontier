using UnityEngine;

namespace ProjectFrontier.TouchInput
{
    /// <summary>
    /// Pure, allocation-free math for the touch HUD (floating joystick + safe area) so the rules are EditMode-testable.
    /// </summary>
    public static class TouchLayoutMath
    {
        /// <summary>
        /// Normalised safe-area anchors (0..1) for a full-screen RectTransform.
        /// Falls back to the full screen for an empty or degenerate safe area.
        /// </summary>
        public static void SafeAreaAnchors(Rect safeArea, float screenWidth, float screenHeight, out Vector2 anchorMin, out Vector2 anchorMax)
        {
            if (screenWidth <= 0f || screenHeight <= 0f || safeArea.width <= 0f || safeArea.height <= 0f)
            {
                anchorMin = Vector2.zero;
                anchorMax = Vector2.one;
                return;
            }

            anchorMin = new Vector2(Mathf.Clamp01(safeArea.xMin / screenWidth), Mathf.Clamp01(safeArea.yMin / screenHeight));
            anchorMax = new Vector2(Mathf.Clamp01(safeArea.xMax / screenWidth), Mathf.Clamp01(safeArea.yMax / screenHeight));
        }

        /// <summary>
        /// Where a floating joystick centre goes for a touch at <paramref name="touch"/> (zone-local, origin bottom-left):
        /// under the thumb, clamped so the whole ring stays inside the zone. If the zone is smaller than the ring on an
        /// axis, the ring is centred on that axis.
        /// </summary>
        public static Vector2 ClampRingCenter(Vector2 touch, Vector2 zoneSize, float ringRadius)
        {
            return new Vector2(ClampAxis(touch.x, zoneSize.x, ringRadius), ClampAxis(touch.y, zoneSize.y, ringRadius));
        }

        private static float ClampAxis(float v, float size, float r)
        {
            if (size <= 2f * r) return size * 0.5f;
            return Mathf.Clamp(v, r, size - r);
        }

        /// <summary>
        /// Knob offset (clamped to <paramref name="maxRadius"/>) and normalised stick value for a thumb offset from the ring centre.
        /// </summary>
        public static Vector2 StickValue(Vector2 offsetFromCenter, float maxRadius, out Vector2 knobOffset)
        {
            float r = Mathf.Max(1f, maxRadius);
            knobOffset = Vector2.ClampMagnitude(offsetFromCenter, r);
            return knobOffset / r;
        }
    }
}
