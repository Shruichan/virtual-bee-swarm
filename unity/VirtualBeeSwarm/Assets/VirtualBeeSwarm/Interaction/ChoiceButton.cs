using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// A selectable choice. Pressed by: a fingertip poke (trigger collider vs GameFingertip), a pointer click
    /// (GamePointer: mouse, controller ray, hand ray + pinch), or its number key (editor/desktop).
    /// Feedback: hover grows + brightens, press pushes in + flashes, locked = greyed out.
    /// The collider (trigger) should be on this object or a child; the visual is `face` (any renderer).
    /// </summary>
    public class ChoiceButton : MonoBehaviour, IGameTarget
    {
        [SerializeField] string choiceId = "";
        [SerializeField] string label = "";
        [Tooltip("1-9: number key that presses it (desktop). 0 = none.")]
        [SerializeField] int keyIndex;
        [SerializeField] Renderer face;
        [SerializeField] TextMesh labelText;
        [Tooltip("Pushed in along local -Z... i.e. away from the player when pressed.")]
        [SerializeField] Transform pressVisual;

        [Header("Look")]
        [SerializeField] Color idleColor = new Color(1f, 0.78f, 0.2f, 1f);
        [SerializeField] Color hoverColor = new Color(1f, 0.92f, 0.45f, 1f);
        [SerializeField] Color pressColor = new Color(1f, 1f, 1f, 1f);
        [SerializeField] Color lockedColor = new Color(0.35f, 0.35f, 0.35f, 1f);
        [SerializeField] Color wrongColor = new Color(0.75f, 0.2f, 0.15f, 1f);
        [SerializeField] float hoverScale = 1.08f;
        [SerializeField] float pressDepth = 0.008f;
        [SerializeField] float cooldownSeconds = 0.6f;

        /// <summary>Raised on a press while interactable.</summary>
        public event Action<ChoiceButton> Pressed;

        public string ChoiceId => choiceId;
        public string Label => label;
        public bool Interactable => _interactable && isActiveAndEnabled;
        public bool MarkedWrong { get; private set; }

        bool _interactable = true, _hovered;
        float _lastPressAt = -10f, _flashUntil = -1f;
        Vector3 _baseScale, _basePressPos;
        int _pokesInside;
        Color _shown = new Color(-1f, 0f, 0f, 0f);

        void Awake()
        {
            _baseScale = transform.localScale;
            if (pressVisual != null) _basePressPos = pressVisual.localPosition;
            if (labelText != null && !string.IsNullOrEmpty(label)) labelText.text = label;
        }

        void OnDisable()
        {
            _hovered = false;
            _pokesInside = 0;
            transform.localScale = _baseScale;
            if (pressVisual != null) pressVisual.localPosition = _basePressPos;
        }

        /// <summary>Scene builder / station setup.</summary>
        public void Configure(string id, string text, int key)
        {
            choiceId = id;
            label = text;
            keyIndex = key;
            if (labelText != null) labelText.text = text;
        }

        public void SetInteractable(bool on)
        {
            _interactable = on;
            if (!on) _hovered = false;
        }

        public void MarkWrong(bool wrong)
        {
            MarkedWrong = wrong;
        }

        public void SetHover(bool hovered) => _hovered = hovered && Interactable;

        public void Click() => Press();

        public void Press()
        {
            if (!Interactable || Time.time - _lastPressAt < cooldownSeconds) return;
            _lastPressAt = Time.time;
            _flashUntil = Time.time + 0.18f;
            Pressed?.Invoke(this);
        }

        void Update()
        {
            if (keyIndex > 0 && Interactable && GameInput.DigitDown(keyIndex)) Press();

            // feedback
            bool flashing = Time.time < _flashUntil;
            Color c = !_interactable ? (MarkedWrong ? wrongColor : lockedColor)
                : flashing ? pressColor
                : _hovered ? hoverColor
                : idleColor;
            if (c != _shown)
            {
                _shown = c;
                GameMaterials.Tint(face, c);
            }
            float s = _hovered && _interactable ? hoverScale : 1f;
            transform.localScale = Vector3.Lerp(transform.localScale, _baseScale * s, Time.deltaTime * 14f);
            if (pressVisual != null)
            {
                Vector3 target = _basePressPos + (flashing ? Vector3.forward * pressDepth : Vector3.zero);
                pressVisual.localPosition = Vector3.Lerp(pressVisual.localPosition, target, Time.deltaTime * 25f);
            }
        }

        // ------------------------------------------------------------ fingertip poke
        void OnTriggerEnter(Collider other)
        {
            if (other.GetComponent<GameFingertip>() == null) return;
            _pokesInside++;
            if (_pokesInside == 1) Press();
        }

        void OnTriggerExit(Collider other)
        {
            if (other.GetComponent<GameFingertip>() == null) return;
            _pokesInside = Mathf.Max(0, _pokesInside - 1);
        }
    }
}
