using System.Collections;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Procedural 2D pixel-art character locomotion and idle breathing.
    /// Drives child Body, Head, LeftFoot, RightFoot, and Scarf parts from normalized velocity
    /// so characters walk with gait bob, foot alternation, lean, secondary scarf lag, and settle into idle breathing.
    /// </summary>
    public class CharacterMotion2D : MonoBehaviour
    {
        [Header("Part Transforms")]
        public Transform visualRoot;
        public Transform bodyTransform;
        public Transform headTransform;
        public Transform leftFootTransform;
        public Transform rightFootTransform;
        public Transform scarfTransform;

        [Header("Tunables (Live-Editable)")]
        public float stepFrequency = 2.6f;
        public float bobAmplitude = 0.085f;
        public float footStride = 0.11f;
        public float leanDegrees = 7f;
        public float idleBreathAmplitude = 0.035f;
        public float idleBreathPeriod = 2.0f;
        public float settleTime = 0.3f;

        private Vector2 _velocity;
        private float _maxSpeed = 4.5f;
        private float _gaitBlend;
        private float _gaitPhase;
        private float _idlePhase;
        private float _currentLean;
        private float _scarfAngle;
        private float _facingSign = 1f;
        private Vector3 _baseBodyLocalPos;
        private Vector3 _baseHeadLocalPos;
        private Vector3 _baseLeftFootPos;
        private Vector3 _baseRightFootPos;
        private Vector3 _baseVisualScale = Vector3.one;
        private Coroutine _popRoutine;

        private void Awake()
        {
            CacheBaselines();
        }

        public void CacheBaselines()
        {
            if (visualRoot == null) visualRoot = transform;
            _baseVisualScale = visualRoot.localScale;
            if (bodyTransform != null) _baseBodyLocalPos = bodyTransform.localPosition;
            if (headTransform != null) _baseHeadLocalPos = headTransform.localPosition;
            if (leftFootTransform != null) _baseLeftFootPos = leftFootTransform.localPosition;
            if (rightFootTransform != null) _baseRightFootPos = rightFootTransform.localPosition;
            // Desync NPCs subtly by world position
            _idlePhase = (transform.position.x * 0.7f + transform.position.y * 0.5f) % (Mathf.PI * 2f);
        }

        public void SetMotionState(Vector2 velocity, float maxSpeed)
        {
            _velocity = velocity;
            _maxSpeed = Mathf.Max(0.1f, maxSpeed);
            if (Mathf.Abs(velocity.x) > 0.08f)
            {
                _facingSign = Mathf.Sign(velocity.x);
            }
        }

        public void PlayPickupPop()
        {
            if (visualRoot == null) return;
            if (_popRoutine != null) StopCoroutine(_popRoutine);
            _popRoutine = StartCoroutine(PopCoroutine());
        }

        private IEnumerator PopCoroutine()
        {
            Vector3 peak = _baseVisualScale * 1.25f;
            yield return Ease.Tween(0.08f, t => visualRoot.localScale = Vector3.LerpUnclamped(_baseVisualScale, peak, t), Ease.OutBack, unscaled: false);
            yield return Ease.Tween(0.12f, t => visualRoot.localScale = Vector3.LerpUnclamped(peak, _baseVisualScale, t), Ease.OutCubic, unscaled: false);
            visualRoot.localScale = _baseVisualScale;
            _popRoutine = null;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            float speedN = Mathf.Clamp01(_velocity.magnitude / _maxSpeed);
            float blendRate = 1f / Mathf.Max(0.05f, settleTime);
            _gaitBlend = Mathf.MoveTowards(_gaitBlend, speedN, dt * blendRate);

            _gaitPhase += dt * stepFrequency * Mathf.PI * 2f * (0.4f + 0.6f * _gaitBlend);
            _idlePhase += dt * (Mathf.PI * 2f / Mathf.Max(0.4f, idleBreathPeriod));

            float walkBob = Mathf.Abs(Mathf.Sin(_gaitPhase)) * bobAmplitude * _gaitBlend;
            float idleBob = Mathf.Sin(_idlePhase) * idleBreathAmplitude * (1f - _gaitBlend * 0.6f);
            float bodyRoll = Mathf.Sin(_gaitPhase * 0.5f) * 3.0f * _gaitBlend;

            float targetLean = -(_velocity.x / _maxSpeed) * leanDegrees;
            _currentLean = Mathf.Lerp(_currentLean, targetLean, 1f - Mathf.Exp(-10f * dt));

            if (bodyTransform != null)
            {
                bodyTransform.localPosition = _baseBodyLocalPos + new Vector3(0f, walkBob + idleBob, 0f);
                bodyTransform.localRotation = Quaternion.Euler(0f, 0f, _currentLean + bodyRoll);
            }

            if (headTransform != null)
            {
                float headLagBob = Mathf.Sin(_idlePhase - 0.5f) * (idleBreathAmplitude * 0.7f);
                float headLookX = _facingSign * 0.03f;
                headTransform.localPosition = _baseHeadLocalPos + new Vector3(headLookX, walkBob * 1.15f + headLagBob, 0f);
                headTransform.localRotation = Quaternion.Euler(0f, 0f, _currentLean * 0.5f + Mathf.Sin(_idlePhase) * 1.8f);
            }

            if (leftFootTransform != null && rightFootTransform != null)
            {
                float stride = Mathf.Sin(_gaitPhase) * footStride * _gaitBlend;
                float liftL = Mathf.Max(0f, Mathf.Cos(_gaitPhase)) * 0.05f * _gaitBlend;
                float liftR = Mathf.Max(0f, -Mathf.Cos(_gaitPhase)) * 0.05f * _gaitBlend;
                leftFootTransform.localPosition = _baseLeftFootPos + new Vector3(stride, liftL, 0f);
                rightFootTransform.localPosition = _baseRightFootPos + new Vector3(-stride, liftR, 0f);
                leftFootTransform.localRotation = Quaternion.Euler(0f, 0f, stride * 120f);
                rightFootTransform.localRotation = Quaternion.Euler(0f, 0f, -stride * 120f);
            }

            if (scarfTransform != null)
            {
                float targetScarf = -_facingSign * (8f + 22f * _gaitBlend) + Mathf.Sin(_gaitPhase - 0.9f) * (10f * _gaitBlend + 3f);
                _scarfAngle = Mathf.Lerp(_scarfAngle, targetScarf, 1f - Mathf.Exp(-12f * dt));
                scarfTransform.localRotation = Quaternion.Euler(0f, 0f, _scarfAngle);
            }
        }
    }
}
