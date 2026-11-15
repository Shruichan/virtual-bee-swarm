using System;
using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// The ORDER UP rail on the table in front of the hive: one ticket per choice. Poke a ticket and Beeswax's crew
    /// flies that one item in through the window. Wrong orders stay on the rail, stamped GRIEVANCE.
    /// </summary>
    public class OrderBoard : MonoBehaviour
    {
        public const float Spacing = 0.12f;
        internal const float TicketRise = 0.049f;

        public event Action<string> Ordered;

        readonly OrderTicket[] _tickets = new OrderTicket[3];
        Transform _rail;
        int _count;
        float _shownAt = -10f;
        const float ShowGuardSeconds = 0.3f;

        public bool Visible => gameObject.activeSelf;
        public int Count => _count;
        public Vector3 Top => transform.position + transform.up * 0.13f;

        public static OrderBoard Build(Transform parent, Vector3 localPos)
        {
            var go = new GameObject("[VBS] OrderBoard");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var b = go.AddComponent<OrderBoard>();
            b._rail = HandsOnKit.Prim(PrimitiveType.Cube, "Rail", go.transform, new Vector3(0f, 0.007f, 0f), new Vector3(0.38f, 0.014f, 0.03f), HandsOnKit.Wood).transform;
            HandsOnKit.Prim(PrimitiveType.Cube, "RailTrim", b._rail, new Vector3(0f, 0.55f, 0f), new Vector3(1f, 0.2f, 1.05f), HandsOnKit.Honey);
            for (int i = 0; i < b._tickets.Length; i++)
            {
                var t = OrderTicket.Build(go.transform, "Ticket" + i);
                t.transform.localRotation = Quaternion.Euler(35f, 0f, 0f); // leaning back, face up to the player
                t.Poked += b.HandlePoked;
                b._tickets[i] = t;
            }
            go.SetActive(false);
            return b;
        }

        public void Show(string[] choices, string[] labels, Predicate<string> crossed)
        {
            _count = Mathf.Min(choices.Length, _tickets.Length);
            _rail.localScale = new Vector3(Spacing * _count + 0.03f, 0.014f, 0.03f);
            for (int i = 0; i < _tickets.Length; i++)
            {
                var t = _tickets[i];
                bool on = i < _count;
                if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
                if (!on) continue;
                t.transform.localPosition = new Vector3((i - (_count - 1) * 0.5f) * Spacing, TicketRise, 0.012f);
                t.transform.localRotation = Quaternion.Euler(35f, 0f, 0f);
                t.Set(choices[i], labels[i], crossed(choices[i]), i + 1);
            }
            if (!gameObject.activeSelf) { _shownAt = Time.time; gameObject.SetActive(true); }
        }

        public void Hide()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        public OrderTicket Ticket(string choice)
        {
            for (int i = 0; i < _count; i++)
                if (_tickets[i].Choice == choice) return _tickets[i];
            return null;
        }

        /// <summary>The ordered ticket is stamped ON THE WING, the others wait.</summary>
        public void MarkOrdered(string choice)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_tickets[i].Choice == choice) _tickets[i].StampOnTheWing();
                else if (!_tickets[i].Crossed) _tickets[i].Dim();
            }
        }

        // A fingertip already resting where a ticket appears fires OnTriggerEnter at once: ignore pokes for a moment.
        void HandlePoked(OrderTicket t)
        {
            if (Time.time - _shownAt < ShowGuardSeconds) return;
            Ordered?.Invoke(t.Choice);
        }
    }
}
