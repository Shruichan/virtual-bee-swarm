using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The hive on the table: lift/tip it by hand (feed station), door width (wasps, escape), red heat glow (heat
    /// station + fx hive_glow_red/off), "sick" tint (store honey). Lifting is exaggerated on purpose: it's way too light.
    /// Modeler: replace the child meshes; keep liftRoot (moves when lifted), door (scale.x = width), doorPoint,
    /// swarmCenter, and the glow renderer.
    /// </summary>
    public class HiveBody : MonoBehaviour, IGameGrabTarget
    {
        [SerializeField] GameNet net;
        [Tooltip("What moves when the hive is lifted/tipped (the box, not the base plate).")]
        [SerializeField] Transform liftRoot;
        [SerializeField] Renderer[] bodyRenderers = new Renderer[0];
        [SerializeField] Color bodyColor = new Color(0.93f, 0.68f, 0.28f, 1f);
        [SerializeField] Color sickColor = new Color(0.55f, 0.6f, 0.45f, 1f);
        [Tooltip("Entrance slot. Its local scale.x is the door width.")]
        [SerializeField] Transform door;
        [SerializeField] Transform doorPoint;
        [SerializeField] Transform swarmCenter;
        [Tooltip("Red shell, hidden when cool.")]
        [SerializeField] Renderer glow;
        [SerializeField] Color glowColor = new Color(1f, 0.15f, 0.05f, 0.55f);

        [Header("Door widths (scale.x)")]
        [SerializeField] float doorNormal = 1f;
        [SerializeField] float doorSmall = 0.4f;
        [SerializeField] float doorBig = 2f;
        [SerializeField] float doorOpen = 2.8f;

        [Header("Lift (way too light)")]
        [SerializeField] float liftExaggeration = 2.8f;
        [SerializeField] float maxLift = 0.26f;
        [SerializeField] float maxTiltDegrees = 22f;
        [Tooltip("Lifted this high (m) = 'too light' detected.")]
        [SerializeField] float liftThreshold = 0.03f;
        [Tooltip("Desktop: hold this key to lift the hive (when lifting is allowed).")]
        [SerializeField] KeyCode liftKey = KeyCode.G;

        public enum DoorSize { Normal, Small, Big, Open }

        /// <summary>Raised once per lift when it passes liftThreshold (height in m).</summary>
        public event Action<float> Lifted;

        public bool LiftEnabled { get; set; }
        public bool Grabbable => LiftEnabled && isActiveAndEnabled;
        public bool Held { get; private set; }
        public float LiftHeight { get; private set; }
        internal bool LiftReported => _liftReported;
        public Transform DoorPoint => doorPoint != null ? doorPoint : transform;
        public Transform SwarmCenter => swarmCenter != null ? swarmCenter : transform;

        internal bool TryGetBodyBounds(out Transform frame, out Bounds bounds)
        {
            frame = liftRoot != null ? liftRoot : transform;
            bounds = default;
            bool found = false;
            Matrix4x4 worldToHive = frame.worldToLocalMatrix;
            foreach (var body in bodyRenderers)
            {
                if (body == null || !body.enabled || !body.gameObject.activeInHierarchy) continue;
                Matrix4x4 toHive = worldToHive * body.localToWorldMatrix;
                Bounds local = body.localBounds;
                Vector3 x = toHive.MultiplyVector(Vector3.right * local.extents.x);
                Vector3 y = toHive.MultiplyVector(Vector3.up * local.extents.y);
                Vector3 z = toHive.MultiplyVector(Vector3.forward * local.extents.z);
                Vector3 extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                                              Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                                              Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
                var piece = new Bounds(toHive.MultiplyPoint3x4(local.center), 2f * extents);
                if (found) bounds.Encapsulate(piece);
                else bounds = piece;
                found = true;
            }
            return found;
        }

        public float HeatGlow { get; private set; }
        /// <summary>0 = healthy .. 1 = sick (the body colour blend).</summary>
        public float Sick => _sick;

        Vector3 _restPos;
        Quaternion _restRot;
        Vector3 _grabStart;
        bool _liftReported, _keyLift;
        float _doorTarget, _heatTarget, _sick, _sickTarget;
        Color _lastBody = new Color(-1f, 0f, 0f, 0f);

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (liftRoot == null) liftRoot = transform;
            _restPos = liftRoot.localPosition;
            _restRot = liftRoot.localRotation;
            _doorTarget = doorNormal;
            if (glow != null) glow.enabled = false;
        }

        void OnEnable() { if (net != null) net.FxReceived += HandleFx; }
        void OnDisable() { if (net != null) net.FxReceived -= HandleFx; }

        void HandleFx(string name, int delayMs, int durationMs)
        {
            if (name == GameIds.Fx.HiveGlowRed) SetHeatGlow(1f);
            else if (name == GameIds.Fx.HiveGlowOff) SetHeatGlow(0f);
        }

        // ------------------------------------------------------------ API
        public void SetDoor(DoorSize size)
        {
            switch (size)
            {
                case DoorSize.Small: _doorTarget = doorSmall; break;
                case DoorSize.Big: _doorTarget = doorBig; break;
                case DoorSize.Open: _doorTarget = doorOpen; break;
                default: _doorTarget = doorNormal; break;
            }
        }

        /// <summary>0 = cool, 1 = glowing red (pulses).</summary>
        public void SetHeatGlow(float level) => _heatTarget = Mathf.Clamp01(level);

        public void SetSick(bool sick) => _sickTarget = sick ? 1f : 0f;

        public void ResetHive()
        {
            LiftEnabled = false;
            Held = false;
            _keyLift = false;
            LiftHeight = 0f;
            liftRoot.localPosition = _restPos;
            liftRoot.localRotation = _restRot;
            _doorTarget = doorNormal;
            if (door != null) { var s = door.localScale; s.x = doorNormal; door.localScale = s; }
            _heatTarget = HeatGlow = 0f;
            _sickTarget = _sick = 0f;
            if (glow != null) glow.enabled = false;
        }

        // ------------------------------------------------------------ grab (IGameGrabTarget)
        public void BeginGrab(Vector3 grabPoint)
        {
            Held = true;
            _grabStart = grabPoint;
            _liftReported = false;
        }

        public void UpdateGrab(Vector3 handPoint)
        {
            Vector3 d = handPoint - _grabStart;
            ApplyLift(d.y, Vector3.Dot(d, transform.right));
        }

        public void EndGrab() => Held = false;

        void ApplyLift(float up, float side)
        {
            float h = Mathf.Clamp(up * liftExaggeration, 0f, maxLift);
            float tilt = Mathf.Clamp(-side * 160f, -maxTiltDegrees, maxTiltDegrees) * Mathf.Clamp01(h / 0.05f);
            LiftHeight = h;
            liftRoot.localPosition = _restPos + Vector3.up * h;
            liftRoot.localRotation = _restRot * Quaternion.Euler(0f, 0f, tilt);
            if (!_liftReported && h >= liftThreshold)
            {
                _liftReported = true;
                Lifted?.Invoke(h);
            }
        }

        void Update()
        {
            // desktop lift key
            if (LiftEnabled && GameInput.KeyHeld(liftKey))
            {
                if (!_keyLift) { _keyLift = true; _liftReported = false; }
                Held = true;
                ApplyLift(Mathf.Min(LiftHeight / liftExaggeration + Time.deltaTime * 0.08f, 0.1f), Mathf.Sin(Time.time * 3f) * 0.03f);
            }
            else if (_keyLift)
            {
                _keyLift = false;
                Held = false;
            }

            // settle back down when released
            if (!Held && (LiftHeight > 0.0005f || liftRoot.localRotation != _restRot))
            {
                LiftHeight = Mathf.MoveTowards(LiftHeight, 0f, Time.deltaTime * 0.5f);
                liftRoot.localPosition = _restPos + Vector3.up * LiftHeight;
                liftRoot.localRotation = Quaternion.RotateTowards(liftRoot.localRotation, _restRot, Time.deltaTime * 120f);
            }

            if (door != null)
            {
                var s = door.localScale;
                if (Mathf.Abs(s.x - _doorTarget) > 0.001f)
                {
                    s.x = Mathf.MoveTowards(s.x, _doorTarget, Time.deltaTime * 3f);
                    door.localScale = s;
                }
            }

            HeatGlow = Mathf.MoveTowards(HeatGlow, _heatTarget, Time.deltaTime * 0.8f);
            if (glow != null)
            {
                bool on = HeatGlow > 0.01f;
                if (glow.enabled != on) glow.enabled = on;
                if (on)
                {
                    var c = glowColor;
                    c.a *= HeatGlow * (0.65f + 0.35f * Mathf.Sin(Time.time * (3f + 4f * HeatGlow)));
                    GameMaterials.Tint(glow, c);
                }
            }

            _sick = Mathf.MoveTowards(_sick, _sickTarget, Time.deltaTime * 0.6f);
            var body = Color.Lerp(bodyColor, sickColor, _sick);
            if (body != _lastBody)
            {
                _lastBody = body;
                GameMaterials.TintAll(bodyRenderers, body);
            }
        }

        // ------------------------------------------------------------ the lid (escape)
        // The art's LidOpen clip hinges the lid at the FRONT, so with the hive at arm's reach the open lid filled the
        // player's view. The lid now hangs from a LidHinge of its own (which also unbinds the clip from it): while the
        // clip "plays" (LidOpen, held at its end) the hinge swings the lid up and over the edge AWAY from the player by
        // lidSwing degrees, in the clip's time; LidClose (or a stop) swings it back.
        [Header("Lid (escape)")]
        [SerializeField] float lidSwing = 170f;
        Transform _lid, _lidHinge;
        Animation _lidAnim;
        bool _lidInit, _lidHinged, _lidLoggedOpen;
        float _lidFrac, _lidSeconds = 1.5f;
        Vector3 _lidAxisP;
        Bounds _lidBox;

        void LateUpdate()
        {
            if (!_lidInit) InitLid();
            if (_lidHinge == null || _lidAnim == null) return;
            bool open = _lidAnim.IsPlaying("LidOpen");
            if (open && !_lidHinged) PickLidHinge();
            _lidFrac = Mathf.MoveTowards(_lidFrac, open ? 1f : 0f, Time.deltaTime / _lidSeconds);
            if (!open && _lidFrac <= 0f) _lidHinged = false;
            float t = _lidFrac * _lidFrac * (3f - 2f * _lidFrac);
            _lidHinge.localRotation = Quaternion.AngleAxis(lidSwing * t, _lidAxisP);
            if ((_lidFrac > 0.5f) != _lidLoggedOpen)
            {
                _lidLoggedOpen = _lidFrac > 0.5f;
                Debug.Log("[VBS][Hive] lid " + (_lidLoggedOpen ? "open: swinging back over the far edge" : "closed"));
            }
        }

        void InitLid()
        {
            _lidInit = true;
            var lid = FindDeep(liftRoot, "Lid");
            if (lid == null || lid.parent == null) return;
            var anim = lid.GetComponentInParent<Animation>();
            var st = anim != null ? anim["LidOpen"] : null;
            if (st == null || st.clip == null) return;
            _lid = lid;
            _lidAnim = anim;
            _lidSeconds = Mathf.Max(0.3f, st.length / Mathf.Max(0.1f, Mathf.Abs(st.speed)));
            // the closed lid's box in its parent's space (the hinge side is picked when it opens: away from the player)
            var parent = lid.parent;
            bool any = false;
            Bounds b = default;
            var w2l = parent.worldToLocalMatrix;
            foreach (var mf in lid.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = w2l * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(c, Vector3.zero); any = true; } else b.Encapsulate(c);
                }
            }
            _lidBox = any ? b : new Bounds(lid.localPosition, Vector3.zero);
            _lidHinge = new GameObject("LidHinge").transform;
            _lidHinge.gameObject.layer = lid.gameObject.layer;
            _lidHinge.SetParent(parent, false);
            lid.SetParent(_lidHinge, true); // the clip's "Lid" path no longer binds: the hinge drives it
            _lidAxisP = Vector3.right;
            Debug.Log("[VBS][Hive] lid on its own hinge: LidOpen swings it " + lidSwing.ToString("0") + " deg back over the edge away from the player in " +
                      _lidSeconds.ToString("0.0") + " s");
        }

        /// <summary>The hinge: the closed lid's bottom edge on the side away from the player's eyes (else the rig's +Z;
        /// the hive's own axes are turned round under the art), in the lid parent's space.</summary>
        void PickLidHinge()
        {
            var parent = _lidHinge.parent;
            var cam = Camera.main;
            Vector3 away = cam != null ? transform.position - cam.transform.position : (transform.parent != null ? transform.parent.forward : transform.forward);
            away.y = 0f;
            if (away.sqrMagnitude < 1e-6f) away = transform.parent != null ? transform.parent.forward : transform.forward;
            Vector3 up = parent.InverseTransformDirection(Vector3.up).normalized;
            Vector3 back = Vector3.ProjectOnPlane(parent.InverseTransformDirection(away.normalized), up).normalized;
            Vector3 e = _lidBox.extents;
            float eBack = Mathf.Abs(back.x) * e.x + Mathf.Abs(back.y) * e.y + Mathf.Abs(back.z) * e.z;
            float eUp = Mathf.Abs(up.x) * e.x + Mathf.Abs(up.y) * e.y + Mathf.Abs(up.z) * e.z;
            _lid.SetParent(parent, true);
            _lidHinge.localPosition = _lidBox.center + back * eBack - up * eUp;
            _lidHinge.localRotation = Quaternion.identity;
            _lid.SetParent(_lidHinge, true);
            _lidAxisP = Vector3.Cross(up, back).normalized;
            _lidHinged = true;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
            foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
            return null;
        }
    }
}
