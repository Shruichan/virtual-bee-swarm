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
        [SerializeField] float liftExaggeration = 1.7f;
        [SerializeField] float maxLift = 0.2f;
        [SerializeField] float maxTiltDegrees = 28f;
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
        public Transform DoorPoint => doorPoint != null ? doorPoint : transform;
        public Transform SwarmCenter => swarmCenter != null ? swarmCenter : transform;
        public float HeatGlow { get; private set; }

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
    }
}
