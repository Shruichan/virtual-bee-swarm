using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// A small kinematic trigger sphere that follows one index fingertip (from GameHands, any SDK), so
    /// ChoiceButtons can be poked. Alternatively parent it under a fingertip bone and set followHandSource = false.
    /// Its collider turns off while the hand isn't tracked.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public class GameFingertip : MonoBehaviour
    {
        [SerializeField] GameHand hand = GameHand.Right;
        [SerializeField] bool followHandSource = true;
        [SerializeField] float radius = 0.009f;

        public GameHand Hand => hand;

        Rigidbody _rb;
        SphereCollider _col;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _col = GetComponent<SphereCollider>();
            _col.isTrigger = true;
            _col.radius = radius;
        }

        public void Configure(GameHand h) => hand = h;

        void Update()
        {
            if (!followHandSource) return;
            bool tracked = GameHands.TryGetIndexTip(hand, out var p);
            if (_col.enabled != tracked) _col.enabled = tracked;
            if (tracked) transform.position = p;
        }
    }
}
