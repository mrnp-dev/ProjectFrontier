using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectFrontier
{
    /// <summary>
    /// Touch-first uGUI virtual joystick that drives PlayerController2D smoothly on Android touchscreens and mouse.
    /// </summary>
    public class VirtualJoystickUI : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public RectTransform background;
        public RectTransform handle;
        public float maxRadius = 46f;

        private Vector2 _value;
        public Vector2 Value => _value;

        public void OnPointerDown(PointerEventData eventData)
        {
            OnDrag(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (background == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(background, eventData.position, eventData.pressEventCamera, out Vector2 localPos))
            {
                Vector2 clamped = Vector2.ClampMagnitude(localPos, maxRadius);
                _value = clamped / Mathf.Max(1f, maxRadius);
                if (handle != null) handle.anchoredPosition = clamped;
                PlayerController2D.Instance?.SetTouchMoveInput(_value);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            ResetStick();
        }

        private void OnDisable()
        {
            ResetStick();
        }

        public void ResetStick()
        {
            _value = Vector2.zero;
            if (handle != null) handle.anchoredPosition = Vector2.zero;
            PlayerController2D.Instance?.SetTouchMoveInput(Vector2.zero);
        }
    }
}
