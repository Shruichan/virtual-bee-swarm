using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// "The props are the buttons": a delivered prop forwards pointer hover/click and fingertip pokes to its
    /// ChoiceButton (now the price tag under it). Needs a trigger collider on the same GameObject (StationDelivery adds
    /// one, 16 cm max). The button keeps its own rules: interactable only while choosing, 0.6 s cooldown, so a poke
    /// that enters both the prop and the tag counts once. v3.2: and only once the prop is parked (StationDelivery.AcceptsPokeAt),
    /// never mid-flight.
    /// </summary>
    public class ChoicePropTarget : MonoBehaviour, IGameTarget
    {
        [SerializeField] ChoiceButton button;
        [SerializeField] StationDelivery owner;
        [SerializeField] int index = -1;
        readonly PokeContacts _contacts = new();

        public ChoiceButton Button => button;
        public void Bind(ChoiceButton b) => button = b;
        public void Bind(ChoiceButton b, StationDelivery d, int i) { button = b; owner = d; index = i; }

        public bool Interactable => enabled && button != null && button.Interactable && (owner == null || owner.AcceptsPokeAt(index));
        public void SetHover(bool hovered) { if (button != null) button.SetHover(hovered); }
        public void Click() { if (Interactable) button.Press(); }

        void OnDisable() => _contacts.Clear();

        void OnTriggerEnter(Collider other)
        {
            if (isActiveAndEnabled && _contacts.Enter(other)) Click();
        }

        void OnTriggerExit(Collider other) => _contacts.Exit(other);
    }
}
