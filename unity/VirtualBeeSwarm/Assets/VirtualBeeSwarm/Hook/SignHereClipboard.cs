using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The SIGN HERE clipboard (V14): START when deliveries are on. One worker bee parks it, held still, in front of the
    /// player in pre; a fingertip poke, a pointer click (mouse, controller or hand ray + pinch) or N / Space signs it.
    /// IntroSequence places it, reacts to <see cref="Signed"/> and sends it back out through the window.
    /// Pivot = bottom edge centre, local +Z = away from the player (art slot SignHere, yaw 180, faces the player).
    /// Without the scene builder's placeholder, <see cref="Build"/> makes one at runtime (primitives + text).
    /// </summary>
    public class SignHereClipboard : MonoBehaviour, IGameTarget
    {
        public const float Width = 0.12f, Height = 0.16f;

        [SerializeField] float cooldownSeconds = 0.6f;

        /// <summary>(clipboard, how: "poke", "pointer", "key", ...)</summary>
        public event Action<SignHereClipboard, string> Signed;

        public bool Interactable => _armed && isActiveAndEnabled;
        public bool Hovered { get; private set; }

        bool _armed;
        float _lastAt = -10f;
        readonly PokeContacts _contacts = new();

        static readonly string[] PartNames = { "Board", "Paper", "Line" };
        static readonly Color[] PartColors = { new Color(0.62f, 0.43f, 0.24f), new Color(0.97f, 0.96f, 0.92f), new Color(0.15f, 0.15f, 0.15f) };

        void Awake()
        {
            // The art pass sweeps the placeholder board: then the art carries the words, and our label goes.
            var label = transform.Find("Label");
            if (label != null && transform.Find("Board") == null) label.gameObject.SetActive(false);
            // placeholder colours (property blocks aren't saved in the scene, so tint here)
            for (int i = 0; i < PartNames.Length; i++)
            {
                var part = transform.Find(PartNames[i]);
                if (part != null) GameMaterials.Tint(part.GetComponent<Renderer>(), PartColors[i]);
            }
        }

        void OnDisable()
        {
            Hovered = false;
            _contacts.Clear();
        }

        /// <summary>Accept a signature (only while it is parked in front of the player).</summary>
        public void Arm(bool on)
        {
            _armed = on;
            if (!on) Hovered = false;
        }

        public void SetHover(bool hovered) => Hovered = hovered && Interactable;
        public void Click() => Sign("pointer");

        public bool Sign(string how)
        {
            if (!Interactable || Time.time - _lastAt < cooldownSeconds) return false;
            _lastAt = Time.time;
            Signed?.Invoke(this, how);
            return true;
        }

        void OnTriggerEnter(Collider other)
        {
            if (isActiveAndEnabled && _contacts.Enter(other)) Sign("poke");
        }

        void OnTriggerExit(Collider other) => _contacts.Exit(other);

        /// <summary>
        /// The placeholder: a board, a paper sheet, the words and a trigger collider over the whole board. Named
        /// "SignHere" so ArtSlots dresses it with Art/SignHere. Starts hidden.
        /// </summary>
        public static SignHereClipboard Build(Transform parent, Material material = null)
        {
            if (material == null) material = GameMaterials.Create("VBS_SignHere", Color.white);
            var root = new GameObject("SignHere");
            root.transform.SetParent(parent, false);
            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.center = new Vector3(0f, Height * 0.5f, 0f);
            col.size = new Vector3(Width + 0.02f, Height + 0.02f, 0.04f);

            Prim(PartNames[0], root.transform, new Vector3(0f, Height * 0.5f, 0f), new Vector3(Width, Height, 0.006f), material);
            Prim(PartNames[1], root.transform, new Vector3(0f, Height * 0.47f, -0.0035f), new Vector3(Width - 0.014f, Height - 0.03f, 0.001f), material);
            Prim(PartNames[2], root.transform, new Vector3(0f, Height * 0.5f - 0.04f, -0.0045f), new Vector3(Width * 0.7f, 0.0016f, 0.001f), material);

            var label = new GameObject("Label");
            label.transform.SetParent(root.transform, false);
            label.transform.localPosition = new Vector3(0f, Height * 0.5f + 0.02f, -0.005f);
            label.transform.localRotation = Quaternion.identity; // read by a player looking along +Z, like every builder text
            var tm = label.AddComponent<TextMesh>();
            tm.text = "SIGN\nHERE";
            tm.characterSize = 0.0045f;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = new Color(0.12f, 0.1f, 0.08f);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                tm.font = font;
                label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }

            var clip = root.AddComponent<SignHereClipboard>();
            root.SetActive(false);
            return clip;
        }

        static void Prim(string name, Transform parent, Vector3 pos, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            var c = go.GetComponent<Collider>();
            if (c != null) DestroyImmediate(c);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }
    }
}
