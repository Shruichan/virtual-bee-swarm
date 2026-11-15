using System;
using UnityEngine;

namespace VirtualBeeSwarm.Ask
{
    /// <summary>What the card shows; each state looks different at a glance (border, hex meter, body).</summary>
    public enum AskCardLook { Idle, Listening, Thinking, Answer }

    /// <summary>
    /// The ASK BEESWAX card (ride script v2): a honey-yellow board (0.5 m wide) a crew flies in and parks at the player's
    /// right hand, 1 m away, so every line reads from the standing spot; the chips take a ray + pinch or a step and a poke.
    ///   ASK BEESWAX / [state line, the biggest text] / three hexes / 4 TRY chips or the answer / MINUTES: n QUESTIONS NOTED
    ///   idle       three quiet hexes, the chips
    ///   listening  red border that pulses, the hexes bounce like a mic meter
    ///   thinking   one hex lit at a time, sweeping
    ///   answer     the chips swap for Beeswax's answer in dark ink on the yellow board (kept a few seconds after he stops)
    /// Quest budget: one opaque unlit board, 4 chip boxes and 3 hexes (shared materials, no lights, no shadows), TextMeshes
    /// whose text changes only when the state does. Per frame it only writes a few scales. Built once; hidden until opened.
    /// </summary>
    public class AskCard : MonoBehaviour
    {
        public const float Width = 0.44f, Height = 0.32f, Scale = 1.15f;
        const float StatusBig = 0.0034f, StatusSmall = 0.0024f;

        public event Action<int> ChipPressed;
        public bool Visible => _root != null && _root.activeSelf;
        public Transform Root => _root != null ? _root.transform : null;
        public bool AnswerShown { get; private set; }
        public AskCardLook Look { get; private set; } = AskCardLook.Idle;
        public bool AnyChipHovered
        {
            get
            {
                if (_chips == null) return false;
                foreach (var c in _chips) if (c != null && c.Hovered) return true;
                return false;
            }
        }

        GameObject _root;
        Transform _edge;
        Renderer _edgeR;
        Vector3 _edgeScale;
        TextMesh _status, _minutes, _answer;
        AskChip[] _chips;
        GameObject[] _chipLabels;
        readonly Transform[] _hex = new Transform[3];
        readonly Renderer[] _hexR = new Renderer[3];
        bool _flownIn;
        float _punchAt = -9f;

        static Font _font;
        static Material _boardMat, _chipMat, _edgeMat, _hexMat;
        static Mesh _hexMesh;

        static readonly Color Board = new Color(1f, 0.83f, 0.33f, 1f);
        static readonly Color Edge = new Color(0.16f, 0.11f, 0.05f, 1f);
        static readonly Color EdgeListening = new Color(0.86f, 0.16f, 0.1f, 1f);
        static readonly Color Ink = new Color(0.14f, 0.09f, 0.03f, 1f);
        static readonly Color HexIdle = new Color(0.72f, 0.55f, 0.18f, 1f);
        static readonly Color ChipIdle = new Color(1f, 0.95f, 0.78f, 1f);
        static readonly Color ChipHover = new Color(1f, 1f, 1f, 1f);

        public void Build()
        {
            if (_root != null) return;
            _font = _font != null ? _font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _boardMat = _boardMat != null ? _boardMat : GameMaterials.Create("[VBS] Ask Board", Board);
            _chipMat = _chipMat != null ? _chipMat : GameMaterials.Create("[VBS] Ask Chip", ChipIdle);
            _edgeMat = _edgeMat != null ? _edgeMat : GameMaterials.Create("[VBS] Ask Edge", Edge);
            _hexMat = _hexMat != null ? _hexMat : GameMaterials.Create("[VBS] Ask Hex", HexIdle);
            _hexMesh = _hexMesh != null ? _hexMesh : GameMeshes.CreateHexPrism(0.016f, 0.004f, "VBS_AskHex");

            _root = new GameObject("[VBS] Ask Beeswax Card");
            _root.transform.SetParent(transform, false);
            _root.transform.localScale = Vector3.one * Scale;
            // Text reads for a player looking along +Z (like every builder text): the card's -Z face looks at the player.
            _edgeScale = new Vector3(Width + 0.02f, Height + 0.02f, 0.004f);
            var edge = Box("Edge", _root.transform, new Vector3(0f, 0f, 0.002f), _edgeScale, _edgeMat);
            _edge = edge.transform;
            _edgeR = edge.GetComponent<Renderer>();
            Box("Board", _root.transform, Vector3.zero, new Vector3(Width, Height, 0.006f), _boardMat);

            float top = Height * 0.5f;
            Text("Title", new Vector3(0f, top - 0.030f, -0.004f), "ASK BEESWAX", 0.0042f, FontStyle.Bold);
            _status = Text("Status", new Vector3(0f, top - 0.076f, -0.004f), AskFlow.StatusIdle, StatusBig, FontStyle.Bold);
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("Hex " + i);
                go.transform.SetParent(_root.transform, false);
                go.transform.localPosition = new Vector3((i - 1) * 0.044f, top - 0.118f, -0.006f);
                go.AddComponent<MeshFilter>().sharedMesh = _hexMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _hexMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _hex[i] = go.transform;
                _hexR[i] = r;
            }

            _chips = new AskChip[AskLines.Chips.Length];
            _chipLabels = new GameObject[_chips.Length];
            for (int i = 0; i < _chips.Length; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * 0.106f;
                float y = top - 0.170f - (i / 2) * 0.058f;
                _chips[i] = Chip(i, new Vector3(x, y, -0.006f), AskLines.ChipLabels[i]);
            }
            _answer = Text("Answer", new Vector3(0f, top - 0.198f, -0.004f), "", 0.0030f, FontStyle.Bold);
            _answer.gameObject.SetActive(false);
            _minutes = Text("Minutes", new Vector3(0f, -top + 0.022f, -0.004f), AskFlow.MinutesText(0), 0.0022f, FontStyle.Bold);
            _root.SetActive(false);
        }

        // ------------------------------------------------------------ show / hide
        /// <summary>Fly the card in with a bee crew (BeeDelivery, any mode; off = it just appears) and park it at `pose`.</summary>
        public void Show(Pose pose)
        {
            Build();
            ShowChips();
            SetLook(AskCardLook.Idle);
            var bd = BeeDelivery.Instance;
            if (bd != null)
            {
                bd.Deliver(_root.transform, pose, DeliveryStyle.Crown, 4);
                _flownIn = true;
            }
            else
            {
                _root.transform.SetPositionAndRotation(pose.position, pose.rotation);
                _root.SetActive(true);
                _flownIn = false;
            }
        }

        /// <summary>fly: the crew carries it back out through the window; else it vanishes at once (reset).</summary>
        public void Hide(bool fly)
        {
            if (_root == null) return;
            var bd = BeeDelivery.Instance;
            if (_flownIn && bd != null)
            {
                if (fly && _root.activeInHierarchy) bd.Return(_root.transform);
                else bd.Release(_root.transform, hide: true);
            }
            if (!fly || bd == null) _root.SetActive(false);
            _flownIn = false;
            foreach (var c in _chips) if (c != null) c.SetHover(false);
        }

        /// <summary>Called only when the status changes (wrapping allocates). Short lines are the card's biggest text.</summary>
        public void SetStatus(string s)
        {
            if (_status == null) return;
            s = s ?? "";
            bool big = s.Length <= 30; // the animated thinking dots are short: they never wrap (no allocation)
            float size = big ? StatusBig : StatusSmall;
            if (_status.characterSize != size) _status.characterSize = size;
            if (!big) s = GameMaterials.Wrap(s, 42);
            if (_status.text != s) _status.text = s;
        }

        public void SetMinutes(string s)
        {
            if (_minutes != null && _minutes.text != s) _minutes.text = s;
        }

        /// <summary>Beeswax's answer takes the chips' place, in dark ink on the board (wrapped once, here).</summary>
        public void ShowAnswer(string text)
        {
            if (_answer == null) return;
            text = text ?? "";
            _answer.characterSize = text.Length > 120 ? 0.0025f : 0.0030f;
            _answer.text = GameMaterials.Wrap(text, text.Length > 120 ? 40 : 33);
            _answer.gameObject.SetActive(true);
            SetChipsActive(false);
            AnswerShown = true;
        }

        public void ShowChips()
        {
            if (_answer != null) _answer.gameObject.SetActive(false);
            SetChipsActive(true);
            AnswerShown = false;
        }

        void SetChipsActive(bool on)
        {
            if (_chips == null) return;
            for (int i = 0; i < _chips.Length; i++)
            {
                if (_chips[i] != null && _chips[i].gameObject.activeSelf != on) _chips[i].gameObject.SetActive(on);
                if (_chipLabels[i] != null && _chipLabels[i].activeSelf != on) _chipLabels[i].SetActive(on);
            }
        }

        /// <summary>Colours change here (once per state change); Animate moves the hexes and the border every frame.</summary>
        public void SetLook(AskCardLook look)
        {
            if (_root == null) return;
            Look = look;
            GameMaterials.Tint(_edgeR, look == AskCardLook.Listening ? EdgeListening : Edge);
            Color hex = look == AskCardLook.Listening ? EdgeListening : look == AskCardLook.Idle ? HexIdle : Ink;
            for (int i = 0; i < 3; i++) GameMaterials.Tint(_hexR[i], hex);
        }

        /// <summary>A stamp landed on the card (a question went into the minutes): one quick squash.</summary>
        public void Punch() => _punchAt = Time.time;

        /// <summary>Every frame while the card is up: scales only, no allocation. mic = 0..1 input level (0 = unknown).</summary>
        public void Animate(float now, float mic)
        {
            if (_root == null || !_root.activeSelf) return;
            float edge = 1f;
            for (int i = 0; i < 3; i++)
            {
                float s;
                switch (Look)
                {
                    case AskCardLook.Listening:
                        // a mic meter: each hex bounces on its own beat, taller with the voice when a level is known
                        s = 0.75f + (0.35f + 0.5f * mic) * Mathf.Abs(Mathf.Sin(now * (7f + i * 1.9f) + i * 1.3f));
                        break;
                    case AskCardLook.Thinking:
                        s = ((int)(now * 3.5f)) % 3 == i ? 1.3f : 0.65f;
                        break;
                    case AskCardLook.Answer:
                        s = 0.8f;
                        break;
                    default:
                        s = 0.7f;
                        break;
                }
                _hex[i].localScale = new Vector3(s, s, 1f);
            }
            if (Look == AskCardLook.Listening) edge = 1f + 0.018f * (0.5f + 0.5f * Mathf.Sin(now * 6f));
            _edge.localScale = new Vector3(_edgeScale.x * edge, _edgeScale.y * (1f + (edge - 1f) * Width / Height), _edgeScale.z);
            float p = now - _punchAt;
            float k = p >= 0f && p < 0.25f ? 1f + 0.06f * Mathf.Sin(p / 0.25f * Mathf.PI) : 1f;
            _root.transform.localScale = new Vector3(Scale * k, Scale * (2f - k), Scale);
        }

        /// <summary>Selftest / desktop: press chip i as if poked.</summary>
        public void Press(int i)
        {
            if (_chips != null && i >= 0 && i < _chips.Length && _chips[i] != null) _chips[i].Click();
        }

        // ------------------------------------------------------------ build helpers
        AskChip Chip(int i, Vector3 pos, string label)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Chip " + i;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(0.200f, 0.050f, 0.008f);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _chipMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var col = go.GetComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(1f, 1f, 4f); // a little depth in front, so a poke registers before the finger hits the board
            var chip = go.AddComponent<AskChip>();
            chip.Init(i, r, ChipIdle, ChipHover, this);
            _chipLabels[i] = Text("ChipLabel " + i, pos + new Vector3(0f, 0f, -0.006f), label, 0.0022f, FontStyle.Bold).gameObject;
            return chip;
        }

        internal void RaiseChip(int i) => ChipPressed?.Invoke(i);

        TextMesh Text(string name, Vector3 pos, string text, float size, FontStyle style, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = size;
            tm.fontSize = 64;
            tm.fontStyle = style;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.MiddleLeft ? TextAlignment.Left : TextAlignment.Center;
            tm.color = Ink;
            if (_font != null)
            {
                tm.font = _font;
                go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
            }
            return tm;
        }

        static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            BeeDelivery.DestroyCollider(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        /// <summary>
        /// Where the card parks: at the player's right hand, 1 m away and 30 degrees right of the window (so the hive,
        /// the level map over it and the window stay clear), a little below eye height, facing them. No window: the same
        /// spot measured from where the head looks.
        /// </summary>
        public static Pose PoseFor(Transform head, Transform window)
        {
            Vector3 h = head != null ? head.position : new Vector3(0f, 1.5f, 0f);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            if (window != null)
            {
                Vector3 toWin = window.position - h;
                toWin.y = 0f;
                if (toWin.sqrMagnitude > 1e-3f) fwd = toWin;
            }
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            const float deg = 30f * Mathf.Deg2Rad;
            Vector3 pos = h + (fwd * Mathf.Cos(deg) + right * Mathf.Sin(deg)) * 1.0f;
            pos.y = Mathf.Clamp(h.y - 0.20f, 0.85f, 1.65f);
            Vector3 look = pos - h;
            look.y = 0f;
            if (look.sqrMagnitude < 1e-4f) look = fwd;
            return new Pose(pos, Quaternion.LookRotation(look.normalized, Vector3.up));
        }
    }

    /// <summary>One TRY chip: poked by a fingertip (trigger), or clicked by GamePointer (mouse, controller or hand ray + pinch).</summary>
    public class AskChip : MonoBehaviour, IGameTarget
    {
        int _index;
        Renderer _face;
        Color _idle, _hover;
        AskCard _card;
        bool _hovered;
        float _lastPressAt = -10f;
        int _pokes;

        public bool Hovered => _hovered;
        public bool Interactable => isActiveAndEnabled;

        public void Init(int index, Renderer face, Color idle, Color hover, AskCard card)
        {
            _index = index;
            _face = face;
            _idle = idle;
            _hover = hover;
            _card = card;
        }

        public void SetHover(bool hovered)
        {
            if (hovered == _hovered) return;
            _hovered = hovered;
            GameMaterials.Tint(_face, hovered ? _hover : _idle);
        }

        public void Click()
        {
            if (!Interactable || Time.time - _lastPressAt < 0.8f) return;
            _lastPressAt = Time.time;
            if (_card != null) _card.RaiseChip(_index);
        }

        void OnDisable()
        {
            _pokes = 0;
            if (_hovered) SetHover(false);
        }

        void OnTriggerEnter(Collider other)
        {
            if (other.GetComponent<GameFingertip>() == null) return;
            if (++_pokes == 1) Click();
        }

        void OnTriggerExit(Collider other)
        {
            if (other.GetComponent<GameFingertip>() == null) return;
            _pokes = Mathf.Max(0, _pokes - 1);
        }
    }
}
