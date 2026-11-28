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
        public Vector2 worldBoundsMin = new Vector2(-23.5f, -7.2f);
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
        private HomesteadStation _nearestStation;
        private bool _hasTargetPlot;
        private int _targetPlotX = -1;
        private int _targetPlotY = -1;

        public VillageNpc NearestNpc => _nearestNpc;
        public HomesteadStation NearestStation => _nearestStation;
        public bool HasTargetPlot => _hasTargetPlot;
        public int TargetPlotX => _targetPlotX;
        public int TargetPlotY => _targetPlotY;
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

            if (FrontierFarmUI.IsAnyModalOpen || FrontierGuildUI.IsAnyModalOpen) return;

            if (loop.DialogueOpen)
            {
                loop.AdvanceDialogue();
                return;
            }

            if (_nearestNpc != null)
            {
                loop.OpenOrAdvanceDialogue(_nearestNpc);
                return;
            }

            if (_nearestStation != null)
            {
                switch (_nearestStation.stationKind)
                {
                    case HomesteadStationKind.CraftingBench:
                        FrontierFarmUI.Instance?.Open(FarmModalTab.Craft);
                        break;
                    case HomesteadStationKind.PipShop:
                        FrontierFarmUI.Instance?.Open(FarmModalTab.PipShop);
                        break;
                    case HomesteadStationKind.RestBed:
                        FrontierDayCycleManager.Instance?.RequestSleep(immediate: false);
                        break;
                    case HomesteadStationKind.GuildBoard:
                        FrontierGuildUI.Instance?.Open(GuildModalTab.Board);
                        break;
                    case HomesteadStationKind.GuildLedger:
                        FrontierGuildUI.Instance?.Open(GuildModalTab.Rank);
                        break;
                }
                return;
            }

            if (_hasTargetPlot && FrontierFarmManager.Instance != null)
            {
                FrontierFarmManager.Instance.InteractWithPlot(_targetPlotX, _targetPlotY);
            }
        }

        private void Update()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null) return;

            bool modalOpen = FrontierFarmUI.IsAnyModalOpen || FrontierGuildUI.IsAnyModalOpen;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.escapeKey.wasPressedThisFrame)
                {
                    if (FrontierGuildUI.IsAnyModalOpen)
                    {
                        FrontierGuildUI.Instance?.Close();
                    }
                    else if (FrontierFarmUI.IsAnyModalOpen)
                    {
                        FrontierFarmUI.Instance?.Close();
                    }
                    else if (loop.State == RunState.Running && loop.DialogueOpen)
                    {
                        loop.CloseDialogue();
                    }
                    else if (loop.State == RunState.Running || loop.State == RunState.Paused)
                    {
                        loop.TogglePause();
                    }
                }
                else if (loop.State == RunState.Running && !loop.DialogueOpen && (kb.bKey.wasPressedThisFrame || kb.iKey.wasPressedThisFrame))
                {
                    FrontierFarmUI.Instance?.ToggleBag();
                    modalOpen = FrontierFarmUI.IsAnyModalOpen || FrontierGuildUI.IsAnyModalOpen;
                }
                else if (loop.State == RunState.Running && !loop.DialogueOpen && kb.gKey.wasPressedThisFrame)
                {
                    FrontierGuildUI.Instance?.ToggleBoard();
                    modalOpen = FrontierFarmUI.IsAnyModalOpen || FrontierGuildUI.IsAnyModalOpen;
                }
                else if (loop.State == RunState.Running && !loop.DialogueOpen && !modalOpen && (kb.eKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame))
                {
                    TryInteract();
                }
            }

            if (loop.State != RunState.Running || loop.DialogueOpen || modalOpen)
            {
                _currentVelocity = Vector2.Lerp(_currentVelocity, Vector2.zero, 1f - Mathf.Exp(-14f * Time.deltaTime));
                _motion?.SetMotionState(_currentVelocity, moveSpeed);
                FrontierFarmManager.Instance?.UpdatePlotCursor(false, -1, -1);
                return;
            }

            float speedMult = FrontierFarmManager.Instance != null ? FrontierFarmManager.Instance.GetMovementSpeedMultiplier() : 1f;
            float effectiveSpeed = moveSpeed * speedMult;
            Vector2 input = ReadCombinedInput();
            Vector2 targetVel = input * effectiveSpeed;
            _currentVelocity = Vector2.Lerp(_currentVelocity, targetVel, 1f - Mathf.Exp(-acceleration * Time.deltaTime));

            _motion?.SetMotionState(_currentVelocity, effectiveSpeed);
            UpdateNearestNpc();
            UpdateNearestStationAndPlot();
        }

        private void FixedUpdate()
        {
            var loop = FrontierGameLoop.Instance;
            if (loop == null || loop.State != RunState.Running || loop.DialogueOpen || FrontierFarmUI.IsAnyModalOpen || FrontierGuildUI.IsAnyModalOpen) return;

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

        private void UpdateNearestStationAndPlot()
        {
            HomesteadStation bestStation = null;
            if (_nearestNpc == null)
            {
                float bestDistSq = float.MaxValue;
                for (int i = 0; i < HomesteadStation.AllStations.Count; i++)
                {
                    var st = HomesteadStation.AllStations[i];
                    if (st == null || !st.isActiveAndEnabled) continue;
                    float r = st.interactRadius;
                    float dSq = (st.transform.position - transform.position).sqrMagnitude;
                    if (dSq <= r * r && dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestStation = st;
                    }
                }
            }

            if (_nearestStation != bestStation)
            {
                if (_nearestStation != null) _nearestStation.SetPromptHighlighted(false);
                _nearestStation = bestStation;
                if (_nearestStation != null) _nearestStation.SetPromptHighlighted(true);
            }

            var farm = FrontierFarmManager.Instance;
            if (_nearestNpc == null && _nearestStation == null && farm != null
                && farm.TryGetTargetPlot(transform.position, out int gx, out int gy))
            {
                _hasTargetPlot = true;
                _targetPlotX = gx;
                _targetPlotY = gy;
                farm.UpdatePlotCursor(true, gx, gy);
            }
            else
            {
                _hasTargetPlot = false;
                _targetPlotX = -1;
                _targetPlotY = -1;
                farm?.UpdatePlotCursor(false, -1, -1);
            }
        }
    }
}
