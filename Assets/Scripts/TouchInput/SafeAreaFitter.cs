using UnityEngine;

namespace ProjectFrontier.TouchInput
{
    /// <summary>
    /// Fits this RectTransform to <see cref="Screen.safeArea"/> (notches, rounded corners, gesture bar).
    /// Children anchored to its edges stay reachable and visible. Re-applies only when the safe area or
    /// resolution changes (rotation, Device Simulator device swap); no per-frame allocations.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public class SafeAreaFitter : MonoBehaviour
    {
        private RectTransform _rect;
        private Rect _lastSafeArea;
        private int _lastWidth;
        private int _lastHeight;

        public Rect AppliedSafeArea => _lastSafeArea;

        private void Awake()
        {
            _rect = (RectTransform)transform;
        }

        private void OnEnable()
        {
            Refresh(force: true);
        }

        private void Update()
        {
            Refresh(force: false);
        }

        public void Refresh(bool force)
        {
            Rect safe = Screen.safeArea;
            int w = Screen.width;
            int h = Screen.height;
            if (!force && safe == _lastSafeArea && w == _lastWidth && h == _lastHeight) return;
            Apply(safe, w, h);
        }

        /// <summary>Applies an explicit safe area (used by Refresh and by tests).</summary>
        public void Apply(Rect safeArea, int screenWidth, int screenHeight)
        {
            if (_rect == null) _rect = (RectTransform)transform;
            _lastSafeArea = safeArea;
            _lastWidth = screenWidth;
            _lastHeight = screenHeight;

            TouchLayoutMath.SafeAreaAnchors(safeArea, screenWidth, screenHeight, out Vector2 min, out Vector2 max);
            _rect.anchorMin = min;
            _rect.anchorMax = max;
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
        }
    }
}
