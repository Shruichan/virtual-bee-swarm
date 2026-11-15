using System.Collections.Generic;
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
        internal uint ContactVersion { get; private set; }

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

        void OnEnable()
        {
            if (!followHandSource) _col.enabled = true;
        }

        void OnDisable() => LoseContact();

        void LoseContact()
        {
            ++ContactVersion;
            _col.enabled = false;
        }

        public void Configure(GameHand h) => hand = h;

        void Update()
        {
            if (!followHandSource) return;
            bool tracked = GameHands.TryGetIndexTip(hand, out var p);
            if (tracked)
            {
                transform.position = p;
                if (!_col.enabled) _col.enabled = true;
            }
            else if (_col.enabled) LoseContact();
        }
    }

    /// <summary>One poke until all fingers leave, including fingers whose tracking disappeared inside the trigger.</summary>
    internal sealed class PokeContacts
    {
        readonly List<(Collider collider, GameFingertip finger, uint version)> _inside = new(2);

        public bool Enter(Collider collider)
        {
            if (collider == null || !collider.enabled ||
                !collider.TryGetComponent<GameFingertip>(out var finger) || !finger.isActiveAndEnabled)
                return false;

            // Unity skips OnTriggerExit when a collider is disabled or destroyed inside a trigger.
            bool alreadyInside = false;
            for (int i = _inside.Count - 1; i >= 0; --i)
            {
                var contact = _inside[i];
                if (contact.collider == null || contact.finger == null || contact.version != contact.finger.ContactVersion)
                    _inside.RemoveAt(i);
                else if (contact.collider == collider) alreadyInside = true;
            }

            if (alreadyInside) return false;
            bool first = _inside.Count == 0;
            _inside.Add((collider, finger, finger.ContactVersion));
            return first;
        }

        public void Exit(Collider collider)
        {
            for (int i = _inside.Count - 1; i >= 0; --i)
                if (_inside[i].collider == collider) _inside.RemoveAt(i);
        }

        public void Clear() => _inside.Clear();
    }
}
