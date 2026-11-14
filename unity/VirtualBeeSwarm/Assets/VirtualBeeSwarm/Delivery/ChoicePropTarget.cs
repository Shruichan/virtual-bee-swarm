using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// "The props are the buttons": a delivered prop forwards pointer hover/click and fingertip pokes to its
    /// ChoiceButton (now the price tag under it). Needs a trigger collider on the same GameObject (StationDelivery adds
    /// one, 16 cm max). The button keeps its own rules: interactable only while choosing, 0.6 s cooldown, so a poke
    /// that enters both the prop and the tag counts once.
    /// </summary>
    public class ChoicePropTarget : MonoBehaviour, IGameTarget
    {
        [SerializeField] ChoiceButton button;
        int _pokesInside;

        public ChoiceButton Button => button;
        public void Bind(ChoiceButton b) => button = b;

        public bool Interactable => enabled && button != null && button.Interactable;
        public void SetHover(bool hovered) { if (button != null) button.SetHover(hovered); }
        public void Click() { if (Interactable) button.Press(); }

        void OnDisable() => _pokesInside = 0;

        void OnTriggerEnter(Collider other)
        {
            if (!enabled || other.GetComponent<GameFingertip>() == null) return;
            _pokesInside++;
            if (_pokesInside == 1 && Interactable) button.Press();
        }

        void OnTriggerExit(Collider other)
        {
            if (other.GetComponent<GameFingertip>() == null) return;
            _pokesInside = Mathf.Max(0, _pokesInside - 1);
        }
    }
}
