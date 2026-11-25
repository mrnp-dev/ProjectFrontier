using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectFrontier
{
    /// <summary>
    /// Touch-first 2D pixel-art player controller supporting Virtual Joystick, New Input System keyboard/gamepad,
    /// and programmatic input override for verification.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class PlayerController2D : MonoBehaviour
    {
        public static PlayerController2D Instance { get; private set; }

        [Header("Movement")]
        public float moveSpeed = 4.2f;
        public float acceleration = 18f;
        public Vector2 worldBoundsMin = new Vector2(-12.5f, -7.2f);
        public Vector2 worldBoundsMax = new Vector2(12.5f, 7.2f);

        [Header("Interaction")]
        public float interactRadius = 2.1f;

        private Rigidbody2D _rb;
        private CharacterMotion2D _motion;
        private Vector2 _touchInput;
        private Vector2 _debugInput;
        private bool _useDebugInput;
        private Vector2 _currentVelocity;
        private VillageNpc _nearestNpc;

        public VillageNpc NearestNpc => _nearestNpc;
        public CharacterMotion2D Motion => _motion;
        public Vector2 CurrentVelocity => _currentVelocity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.gravityScale = 0f;
            _rb.freezeRotation = true;
            _motion = GetComponent<CharacterMotion2D>();
        }

        public void SetTouchMoveInput(Vector2 input)
        {
            _touchInput = Vector2.ClampMagnitude(input, 1f);
        }

        public void SetDebugMoveInput(Vector2 input, bool enabled = true)
        {
            _useDebugInput = enabled;
            _debugInput = Vector2.ClampMagnitude(input, 1f);
        }

        public void TryInteract()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null || loop.State != RunState.Running) return;

            if (loop.DialogueOpen)
            {
                loop.AdvanceDialogue();
                return;
            }

            if (_nearestNpc != null)
            {
                loop.OpenOrAdvanceDialogue(_nearestNpc);
            }
        }

        private void Update()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.escapeKey.wasPressedThisFrame && (loop.State == RunState.Running || loop.State == RunState.Paused))
                {
                    loop.TogglePause();
                }
                else if (loop.State == RunState.Running && (kb.eKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame))
                {
                    TryInteract();
                }
            }

            if (loop.State != RunState.Running || loop.DialogueOpen)
            {
                _currentVelocity = Vector2.Lerp(_currentVelocity, Vector2.zero, 1f - Mathf.Exp(-14f * Time.deltaTime));
                _motion?.SetMotionState(_currentVelocity, moveSpeed);
                return;
            }

            Vector2 input = ReadCombinedInput();
            Vector2 targetVel = input * moveSpeed;
            _currentVelocity = Vector2.Lerp(_currentVelocity, targetVel, 1f - Mathf.Exp(-acceleration * Time.deltaTime));

            _motion?.SetMotionState(_currentVelocity, moveSpeed);
            UpdateNearestNpc();
        }

        private void FixedUpdate()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null || loop.State != RunState.Running || loop.DialogueOpen) return;

            Vector2 nextPos = _rb.position + _currentVelocity * Time.fixedDeltaTime;
            nextPos.x = Mathf.Clamp(nextPos.x, worldBoundsMin.x, worldBoundsMax.x);
            nextPos.y = Mathf.Clamp(nextPos.y, worldBoundsMin.y, worldBoundsMax.y);
            _rb.MovePosition(nextPos);
        }

        private Vector2 ReadCombinedInput()
        {
            if (_useDebugInput) return _debugInput;
            if (_touchInput.sqrMagnitude > 0.01f) return _touchInput;

            Vector2 kbVec = Vector2.zero;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) kbVec.x -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) kbVec.x += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) kbVec.y -= 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) kbVec.y += 1f;
            }
            if (kbVec.sqrMagnitude > 1f) kbVec.Normalize();
            return kbVec;
        }

        private void UpdateNearestNpc()
        {
            VillageNpc best = null;
            float bestDistSq = interactRadius * interactRadius;
            foreach (var npc in VillageNpc.AllNpcs)
            {
                if (npc == null || !npc.isActiveAndEnabled) continue;
                float dSq = (npc.transform.position - transform.position).sqrMagnitude;
                if (dSq <= bestDistSq)
                {
                    bestDistSq = dSq;
                    best = npc;
                }
            }

            if (_nearestNpc != best)
            {
                if (_nearestNpc != null) _nearestNpc.SetPromptHighlighted(false);
                _nearestNpc = best;
                if (_nearestNpc != null) _nearestNpc.SetPromptHighlighted(true);
            }
        }
    }
}
