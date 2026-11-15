using System;
using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// One order ticket on the ORDER UP rail: poke it (fingertip), ray-pinch it or click it. Same pattern as
    /// ChoicePropTarget: a trigger collider that counts GameFingertips, plus IGameTarget for the pointers.
    /// The band across the top is its status: "No. 2" (the desktop key), ON THE WING, or GRIEVANCE.
    /// </summary>
    public class OrderTicket : MonoBehaviour, IGameTarget
    {
        enum State { Open, OnTheWing, Waiting, Grievance }

        public event Action<OrderTicket> Poked;

        public string Choice { get; private set; } = "";
        public bool Crossed => _state == State.Grievance;
        public bool Interactable => this != null && isActiveAndEnabled && _state == State.Open;

        Renderer _card, _band;
        TextMesh _name, _status;
        State _state;
        string _number = "";
        bool _hover;
        readonly PokeContacts _contacts = new();
        float _wiggleUntil = -1f, _popAt = -1f;
        Quaternion _rest = Quaternion.identity;

        public static OrderTicket Build(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<OrderTicket>();
            t._card = HandsOnKit.Prim(PrimitiveType.Cube, "Card", go.transform, Vector3.zero, new Vector3(0.09f, 0.064f, 0.004f), HandsOnKit.Cream).GetComponent<Renderer>();
            t._band = HandsOnKit.Prim(PrimitiveType.Cube, "Band", go.transform, new Vector3(0f, 0.0235f, -0.0006f), new Vector3(0.09f, 0.017f, 0.004f), HandsOnKit.Honey).GetComponent<Renderer>();
            t._status = HandsOnKit.Text("Status", go.transform, new Vector3(0f, 0.0235f, -0.0035f), "", 0.0015f, HandsOnKit.Ink);
            t._name = HandsOnKit.Text("Name", go.transform, new Vector3(0f, -0.0085f, -0.0035f), "", 0.0021f, HandsOnKit.Ink);
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(0.1f, 0.074f, 0.05f);
            return t;
        }

        public void Set(string choice, string label, bool crossed, int number)
        {
            Choice = choice;
            _number = "No. " + number;
            _hover = false;
            _contacts.Clear();
            _popAt = -1f;
            _rest = transform.localRotation;
            // two short lines read better than one long one on a 9 cm card
            string text = label.ToUpperInvariant().Replace('\n', ' ');
            int sp = text.IndexOf(' ');
            if (sp > 0 && text.Length > 8) text = text.Substring(0, sp) + "\n" + text.Substring(sp + 1);
            int longest = 0;
            foreach (var line in text.Split('\n')) longest = Mathf.Max(longest, line.Length);
            _name.text = text;
            _name.characterSize = 0.0021f * Mathf.Min(1f, 9f / Mathf.Max(1, longest));
            Paint(crossed ? State.Grievance : State.Open);
        }

        public void StampOnTheWing() { _popAt = Time.time; Paint(State.OnTheWing); }
        public void StampGrievance() { _popAt = Time.time; Paint(State.Grievance); }
        /// <summary>Another ticket was ordered: this one waits, greyed.</summary>
        public void Dim() { if (_state == State.Open) Paint(State.Waiting); }
        public void Wiggle(float seconds) => _wiggleUntil = Time.time + seconds;

        public void SetHover(bool hovered) { _hover = hovered && Interactable; }
        public void Click() { if (Interactable) Poked?.Invoke(this); }

        void OnDisable() { _contacts.Clear(); _hover = false; }

        void OnTriggerEnter(Collider other)
        {
            if (isActiveAndEnabled && _contacts.Enter(other)) Click();
        }

        void OnTriggerExit(Collider other) => _contacts.Exit(other);

        void Update()
        {
            float s = _hover ? 1.08f : 1f;
            if (_popAt >= 0f)
            {
                float k = (Time.time - _popAt) / 0.2f;
                if (k >= 1f) _popAt = -1f; else s *= 1f + 0.18f * Mathf.Sin(k * Mathf.PI);
            }
            transform.localScale = Vector3.one * s;
            float w = Time.time < _wiggleUntil ? Mathf.Sin(Time.time * 18f) * 7f : 0f;
            transform.localRotation = _rest * Quaternion.Euler(0f, 0f, w);
        }

        void Paint(State state)
        {
            _state = state;
            bool bright = state == State.Open || state == State.OnTheWing;
            GameMaterials.Tint(_card, bright ? HandsOnKit.Cream : new Color(0.66f, 0.64f, 0.58f));
            _name.color = bright ? HandsOnKit.Ink : new Color(0.13f, 0.09f, 0.05f, 0.6f);
            switch (state)
            {
                case State.OnTheWing:
                    GameMaterials.Tint(_band, new Color(0.16f, 0.58f, 0.2f));
                    _status.text = "ON THE WING";
                    _status.color = Color.white;
                    break;
                case State.Grievance:
                    GameMaterials.Tint(_band, HandsOnKit.Bad);
                    _status.text = "GRIEVANCE";
                    _status.color = Color.white;
                    break;
                default:
                    GameMaterials.Tint(_band, state == State.Open ? HandsOnKit.Honey : HandsOnKit.HoneyDim);
                    _status.text = _number;
                    _status.color = HandsOnKit.Ink;
                    break;
            }
        }
    }
}
