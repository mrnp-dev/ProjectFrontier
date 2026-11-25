using System.Collections.Generic;
using UnityEngine;

namespace ProjectFrontier
{
    /// <summary>
    /// Collectible Frontier Lantern Shard pickup with floating bob, trigger detection, and collector pop.
    /// </summary>
    [RequireComponent(typeof(CircleCollider2D))]
    public class LanternShardPickup : MonoBehaviour
    {
        public int shardValue = 1;
        private Vector3 _basePos;
        private bool _collected;
        private float _phase;

        private void Awake()
        {
            _basePos = transform.position;
            _phase = (transform.position.x + transform.position.y) * 0.9f;
            var col = GetComponent<CircleCollider2D>();
            col.isTrigger = true;
            col.radius = 0.55f;
        }

        private void Update()
        {
            if (_collected) return;
            float bob = Mathf.Sin(Time.time * 3.2f + _phase) * 0.08f;
            transform.position = _basePos + new Vector3(0f, bob, 0f);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_collected) return;
            var player = other.GetComponentInParent<PlayerController2D>();
            if (player == null) return;

            var loop = FrontierGameLoop.Instance;
            if (loop == null || loop.State != RunState.Running) return;

            _collected = true;
            player.Motion?.PlayPickupPop();
            loop.CollectLantern(transform.position, shardValue);
            gameObject.SetActive(false);
        }
    }
}
