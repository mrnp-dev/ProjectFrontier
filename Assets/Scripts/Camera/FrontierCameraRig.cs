using Unity.Cinemachine;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Drives the Cinemachine 3 2D camera rig target:
    /// - On Title (Ready): slow 2D parallax pan (~0.5 units/s) across the live village.
    /// - In Gameplay: tracks the player smoothly within village bounds.
    /// </summary>
    public class FrontierCameraRig : MonoBehaviour
    {
        public Transform playerTarget;
        public Transform cameraAnchor;
        public CinemachineCamera virtualCamera;

        [Header("2D Title Pan")]
        public float titlePanSpeed = 0.5f;
        public float titlePanRadiusX = 2.0f;
        public float titlePanRadiusY = 0.8f;

        [Header("Gameplay Bounds")]
        public Vector2 clampMin = new Vector2(-4.2f, -2.2f);
        public Vector2 clampMax = new Vector2(4.2f, 2.2f);

        private float _panTime;

        private void Awake()
        {
            if (cameraAnchor == null)
            {
                var anchorGo = new GameObject("CameraTargetAnchor");
                cameraAnchor = anchorGo.transform;
            }
            if (virtualCamera != null)
            {
                virtualCamera.Target.TrackingTarget = cameraAnchor;
            }
        }

        private void LateUpdate()
        {
            if (cameraAnchor == null) return;
            var loop = FrontierGameLoop.Instance;
            bool isTitle = loop == null || loop.State == RunState.Ready;

            if (isTitle)
            {
                _panTime += Time.unscaledDeltaTime * titlePanSpeed;
                float x = Mathf.Sin(_panTime * 0.45f) * titlePanRadiusX;
                float y = Mathf.Cos(_panTime * 0.30f) * titlePanRadiusY;
                cameraAnchor.position = new Vector3(x, y, 0f);
            }
            else if (playerTarget != null)
            {
                Vector3 p = playerTarget.position;
                float cx = Mathf.Clamp(p.x, clampMin.x, clampMax.x);
                float cy = Mathf.Clamp(p.y, clampMin.y, clampMax.y);
                cameraAnchor.position = Vector3.Lerp(cameraAnchor.position, new Vector3(cx, cy, 0f), 1f - Mathf.Exp(-10f * Time.deltaTime));
            }
        }
    }
}
