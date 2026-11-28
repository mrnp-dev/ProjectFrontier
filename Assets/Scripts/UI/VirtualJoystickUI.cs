using ProjectFrontier.TouchInput;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectFrontier
{
    /// <summary>
    /// Floating uGUI virtual joystick (#35). Sits on a transparent movement zone in the lower-left of the safe area:
    /// touching anywhere in the zone moves the ring under the thumb, dragging steers, releasing returns the ring to
    /// its rest position. HUD buttons, the chatbox and modals are later siblings, so they win raycasts over the zone.
    /// Only the first finger in the zone drives the stick. No per-frame work; everything runs on pointer events.
    /// </summary>
    public class VirtualJoystickUI : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private const int NoPointer = int.MinValue;

        [Tooltip("Transparent raycast area that accepts touches. Defaults to this RectTransform.")]
        public RectTransform zone;
        [Tooltip("Ring; anchored to the zone's bottom-left with a centre pivot.")]
        public RectTransform background;
        public RectTransform handle;
        [Tooltip("Knob travel in canvas units. Keep the ratio to the ring size constant (46/150) to preserve feel.")]
        public float maxRadius = 46f;
        [Tooltip("Ring centre at rest, relative to the zone's bottom-left corner.")]
        public Vector2 restPosition = new Vector2(238f, 208f);
        public bool floating = true;

        private Vector2 _value;
        private int _pointerId = NoPointer;

        public Vector2 Value => _value;
        public bool IsHeld => _pointerId != NoPointer;

        private void Awake()
        {
            if (zone == null) zone = (RectTransform)transform;
            ReturnToRest();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_pointerId != NoPointer || background == null) return;
            _pointerId = eventData.pointerId;

            if (floating && zone != null &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(zone, eventData.position, eventData.pressEventCamera, out Vector2 local))
            {
                Rect r = zone.rect;
                Vector2 fromCorner = local - r.min;
                float ringRadius = background.rect.width * 0.5f;
                background.anchoredPosition = TouchLayoutMath.ClampRingCenter(fromCorner, r.size, ringRadius);
            }

            Steer(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.pointerId != _pointerId) return;
            Steer(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.pointerId != _pointerId) return;
            ResetStick();
        }

        private void OnDisable()
        {
            ResetStick();
        }

        private void Steer(PointerEventData eventData)
        {
            if (background == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(background, eventData.position, eventData.pressEventCamera, out Vector2 localPos))
                return;
            // Ring pivot is its centre, so localPos is the thumb offset from the centre.
            _value = TouchLayoutMath.StickValue(localPos, maxRadius, out Vector2 knob);
            if (handle != null) handle.anchoredPosition = knob;
            PlayerController2D.Instance?.SetTouchMoveInput(_value);
        }

        public void ResetStick()
        {
            _pointerId = NoPointer;
            _value = Vector2.zero;
            if (handle != null) handle.anchoredPosition = Vector2.zero;
            ReturnToRest();
            PlayerController2D.Instance?.SetTouchMoveInput(Vector2.zero);
        }

        private void ReturnToRest()
        {
            if (background != null && floating) background.anchoredPosition = restPosition;
        }
    }
}
