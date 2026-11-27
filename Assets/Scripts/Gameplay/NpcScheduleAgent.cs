using System;
using System.Collections.Generic;
using ProjectFrontier.World;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Deterministic schedule-driven locomotion for a single Oakhaven villager.
    /// Receives frame-budgeted A* waypoints from <see cref="FrontierDayCycleManager"/>,
    /// walks via <see cref="CharacterMotion2D"/> with soft separation (no NPC-vs-NPC physics shoving),
    /// and pauses smoothly while the player is talking or the game is paused.
    /// </summary>
    [RequireComponent(typeof(VillageNpc))]
    public class NpcScheduleAgent : MonoBehaviour
    {
        [Header("Locomotion")]
        public float moveSpeed = 2.15f;
        public float waypointTolerance = 0.14f;
        public float arrivalTolerance = 0.10f;
        public float separationRadius = 0.72f;
        public float separationWeight = 0.55f;

        public VillageSpot CurrentSpot { get; private set; } = VillageSpot.Square;
        public Vector2 TargetPosition { get; private set; }
        public bool IsMoving => _followingPath && _waypointIndex < _waypoints.Count;
        public bool HasArrived { get; private set; } = true;
        public bool PathPending { get; private set; }
        public int RemainingWaypoints => _followingPath ? Mathf.Max(0, _waypoints.Count - _waypointIndex) : 0;

        private VillageNpc _npc;
        private CharacterMotion2D _motion;
        private readonly List<Vector2> _waypoints = new List<Vector2>(32);
        private Action<bool> _onPathReady;
        private int _waypointIndex;
        private bool _followingPath;
        private Vector2 _currentVelocity;

        private void Awake()
        {
            _npc = GetComponent<VillageNpc>();
            _motion = GetComponent<CharacterMotion2D>();
            _onPathReady = HandlePathReady;
            Vector3 p = transform.position;
            TargetPosition = new Vector2(p.x, p.y);
        }

        private void OnDisable()
        {
            if (FrontierDayCycleManager.Instance != null && _npc != null)
            {
                FrontierDayCycleManager.Instance.CancelNpcPath(_npc.NpcId);
            }
            PathPending = false;
            _followingPath = false;
            _currentVelocity = Vector2.zero;
            _motion?.SetMotionState(Vector2.zero, moveSpeed);
        }

        public void AssignSchedule(DayPhase phase, int day, bool snapImmediate = false)
        {
            if (_npc == null) _npc = GetComponent<VillageNpc>();
            string id = _npc != null ? _npc.NpcId : name;
            CurrentSpot = NpcScheduleTable.GetScheduledSpot(id, phase, day);
            TargetPosition = NpcScheduleTable.GetTargetWorldPosition(id, phase, day);

            if (snapImmediate)
            {
                FrontierDayCycleManager.Instance?.CancelNpcPath(id);
                PathPending = false;
                _followingPath = false;
                _waypoints.Clear();
                _waypointIndex = 0;
                HasArrived = true;
                _currentVelocity = Vector2.zero;
                transform.position = new Vector3(TargetPosition.x, TargetPosition.y, transform.position.z);
                _motion?.SetMotionState(Vector2.zero, moveSpeed);
                return;
            }

            Vector2 pos = new Vector2(transform.position.x, transform.position.y);
            if ((TargetPosition - pos).sqrMagnitude <= arrivalTolerance * arrivalTolerance)
            {
                FrontierDayCycleManager.Instance?.CancelNpcPath(id);
                PathPending = false;
                _followingPath = false;
                _waypoints.Clear();
                _waypointIndex = 0;
                HasArrived = true;
                _currentVelocity = Vector2.zero;
                _motion?.SetMotionState(Vector2.zero, moveSpeed);
                return;
            }

            HasArrived = false;
            var mgr = FrontierDayCycleManager.Instance;
            if (mgr != null)
            {
                PathPending = true;
                mgr.RequestNpcPath(id, pos, TargetPosition, _waypoints, _onPathReady);
            }
            else
            {
                _waypoints.Clear();
                _waypoints.Add(TargetPosition);
                _waypointIndex = 0;
                _followingPath = true;
                PathPending = false;
            }
        }

        private void HandlePathReady(bool found)
        {
            PathPending = false;
            if (!found || _waypoints.Count == 0)
            {
                _waypoints.Clear();
                _waypoints.Add(TargetPosition);
            }
            _waypointIndex = 0;
            _followingPath = true;
        }

        private void Update()
        {
            var loop = FrontierGameLoop.Instance;
            var dayMgr = FrontierDayCycleManager.Instance;
            bool paused = (loop != null && (loop.State != RunState.Running || loop.DialogueOpen))
                || (dayMgr != null && dayMgr.IsSleeping);

            if (paused || !_followingPath || _waypointIndex >= _waypoints.Count)
            {
                if (_currentVelocity.sqrMagnitude > 0.0001f)
                {
                    _currentVelocity = Vector2.zero;
                    _motion?.SetMotionState(Vector2.zero, moveSpeed);
                }
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector2 pos = new Vector2(transform.position.x, transform.position.y);
            Vector2 waypoint = _waypoints[_waypointIndex];
            bool isFinal = _waypointIndex == _waypoints.Count - 1;
            float tol = isFinal ? arrivalTolerance : waypointTolerance;

            Vector2 toTarget = waypoint - pos;
            float dist = toTarget.magnitude;
            if (dist <= tol)
            {
                if (isFinal)
                {
                    transform.position = new Vector3(TargetPosition.x, TargetPosition.y, transform.position.z);
                    _followingPath = false;
                    HasArrived = true;
                    _currentVelocity = Vector2.zero;
                    _motion?.SetMotionState(Vector2.zero, moveSpeed);
                    return;
                }
                _waypointIndex++;
                waypoint = _waypoints[_waypointIndex];
                toTarget = waypoint - pos;
                dist = toTarget.magnitude;
            }

            if (dist <= 0.0001f) return;

            Vector2 dir = toTarget / dist;
            // Soft separation while in transit so villagers glide past each other without physics shoving.
            if (!isFinal || dist > 0.65f)
            {
                Vector2 sep = ComputeSoftSeparation(pos);
                if (sep.sqrMagnitude > 0.0001f)
                {
                    dir = (dir + sep * separationWeight).normalized;
                }
            }

            float step = Mathf.Min(moveSpeed * dt, dist);
            Vector2 next = pos + dir * step;
            transform.position = new Vector3(next.x, next.y, transform.position.z);

            _currentVelocity = dir * moveSpeed;
            _motion?.SetMotionState(_currentVelocity, moveSpeed);
        }

        private Vector2 ComputeSoftSeparation(Vector2 myPos)
        {
            Vector2 push = Vector2.zero;
            float radiusSq = separationRadius * separationRadius;
            var npcs = VillageNpc.AllNpcs;
            for (int i = 0; i < npcs.Count; i++)
            {
                VillageNpc other = npcs[i];
                if (other == null || other == _npc || !other.isActiveAndEnabled) continue;
                Vector3 op = other.transform.position;
                float dx = myPos.x - op.x;
                float dy = myPos.y - op.y;
                float dSq = dx * dx + dy * dy;
                if (dSq > 0.0001f && dSq < radiusSq)
                {
                    float d = Mathf.Sqrt(dSq);
                    float w = (separationRadius - d) / separationRadius;
                    push.x += (dx / d) * w;
                    push.y += (dy / d) * w;
                }
            }
            return push;
        }
    }
}
