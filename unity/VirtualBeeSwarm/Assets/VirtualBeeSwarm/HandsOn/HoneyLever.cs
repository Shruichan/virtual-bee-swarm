using System;
using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// The HIVE-DRAULIC lever: a honey dipper on a hex base. Pinch the head (or ray-grab, or mouse-drag) and pull it
    /// toward you. No physics: the hand's travel along the pull direction turns the arm (0-70 deg), a ratchet tooth
    /// lights every 14 deg, and at 62 deg it snaps and fires Pulled. It re-arms below 15 deg, so it can be pumped
    /// without letting go; released, it springs back. Not Live: it only wobbles and says "not yet".
    /// Desktop: key Y (HandsOnDirector) = SimulatePull.
    /// </summary>
    public class HoneyLever : MonoBehaviour, IGameGrabTarget
    {
        public const float MaxAngle = 70f, SnapAngle = 62f, RearmAngle = 15f, ToothStep = 14f, RestLean = 20f;
        const float ArmLength = 0.17f, NotLiveAngle = 10f;
        static readonly Vector3 PivotLocal = new Vector3(0f, 0.04f, 0f);
        static readonly Vector3 PullDir = new Vector3(0f, -0.3f, -0.954f); // toward the player and a little down (local)

        public event Action Pulled;
        /// <summary>A grab or a key on a lever that is not live.</summary>
        public event Action Refused;

        public bool Grabbable => this != null && isActiveAndEnabled && _live;
        public bool Held { get; private set; }
        public float Angle => _angle;
        public int Pulls { get; private set; }
        public Transform PromptPoint => transform;
        /// <summary>Where a prop is docked next to the lever (the smoker, nozzle at the hive).</summary>
        public Pose CradlePose => new Pose(_pad.position + transform.up * 0.012f, transform.rotation);
        public Vector3 HeadPosition => _head.position;

        void RefreshVisibility() => gameObject.SetActive(_live || (_pad != null && _pad.gameObject.activeSelf));

        public bool Live
        {
            get => _live;
            set
            {
                if (_live == value) return;
                _live = value;
                _armed = true;
                _painted = false;
                if (_capsule != null) _capsule.enabled = value;
                if (_ball != null) _ball.enabled = value;
                RefreshVisibility();
                Debug.Log("[VBS][HandsOn] lever " + (value ? "live" : "locked"));
            }
        }

        Transform _arm, _head, _pad;
        Renderer[] _discs, _teeth;
        Renderer _flash;
        CapsuleCollider _capsule;
        SphereCollider _ball;
        bool _live, _armed = true, _painted;
        float _angle, _vel, _target, _grabAngle, _pop, _flashAt = -1f;
        Vector3 _grabPoint;
        int _tooth;
        Coroutine _sim;

        public static HoneyLever Build(Transform parent, Vector3 localPos, bool left)
        {
            var root = new GameObject("[VBS] HoneyLever").transform;
            root.SetParent(parent, false);
            root.localPosition = localPos;
            var lever = root.gameObject.AddComponent<HoneyLever>();

            HandsOnKit.Hexagon("Base", root, Vector3.zero, 0.16f, 0.04f, HandsOnKit.Wood);
            HandsOnKit.Hexagon("Rim", root, new Vector3(0f, 0.04f, 0f), 0.145f, 0.006f, HandsOnKit.Honey);
            HandsOnKit.Prim(PrimitiveType.Cube, "Plate", root, new Vector3(0f, 0.02f, -0.0695f), new Vector3(0.078f, 0.024f, 0.004f), HandsOnKit.Brass);
            HandsOnKit.Text("PlateText", root, new Vector3(0f, 0.02f, -0.0722f), "HIVE-DRAULIC", 0.0014f, HandsOnKit.Ink);

            // ratchet quadrant on the outer side: a disc in the pull plane with five teeth around its rim
            float side = left ? -0.05f : 0.05f;
            var quad = HandsOnKit.Prim(PrimitiveType.Cylinder, "Quadrant", root, PivotLocal + new Vector3(side, 0f, 0f), new Vector3(0.15f, 0.004f, 0.15f), HandsOnKit.Brass);
            quad.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            lever._teeth = new Renderer[5];
            for (int k = 0; k < 5; k++)
            {
                Vector3 dir = Quaternion.Euler(RestLean - (k + 1) * ToothStep, 0f, 0f) * Vector3.up;
                var tooth = HandsOnKit.Prim(PrimitiveType.Cube, "Tooth" + k, root, PivotLocal + new Vector3(side, 0f, 0f) + dir * 0.083f, new Vector3(0.012f, 0.016f, 0.012f), HandsOnKit.Honey);
                tooth.transform.localRotation = Quaternion.Euler(RestLean - (k + 1) * ToothStep, 0f, 0f);
                lever._teeth[k] = tooth.GetComponent<Renderer>();
            }

            lever._arm = new GameObject("Arm").transform;
            lever._arm.SetParent(root, false);
            lever._arm.localPosition = PivotLocal;
            HandsOnKit.Prim(PrimitiveType.Sphere, "Hub", lever._arm, Vector3.zero, Vector3.one * 0.034f, HandsOnKit.Brass);
            HandsOnKit.Prim(PrimitiveType.Cylinder, "Shaft", lever._arm, new Vector3(0f, ArmLength * 0.5f, 0f), new Vector3(0.018f, ArmLength * 0.5f, 0.018f), HandsOnKit.Brass);
            lever._head = new GameObject("Dipper").transform;
            lever._head.SetParent(lever._arm, false);
            lever._head.localPosition = new Vector3(0f, ArmLength + 0.03f, 0f);
            lever._discs = new Renderer[5];
            for (int i = 0; i < 5; i++)
            {
                float d = i % 2 == 0 ? 0.056f : 0.04f;
                var disc = HandsOnKit.Prim(PrimitiveType.Cylinder, "Disc" + i, lever._head, new Vector3(0f, (i - 2) * 0.014f, 0f), new Vector3(d, 0.0065f, d), HandsOnKit.Honey);
                lever._discs[i] = disc.GetComponent<Renderer>();
            }

            // grab volume: generous, so a pinch near the dipper catches it (direct grab radius is 7 cm)
            var grab = new GameObject("Grab");
            grab.transform.SetParent(lever._arm, false);
            var capsule = grab.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.direction = 1;
            capsule.center = new Vector3(0f, ArmLength * 0.5f + 0.03f, 0f);
            capsule.height = ArmLength + 0.1f;
            capsule.radius = 0.045f;
            capsule.enabled = false; // only while live: a locked lever must not eat rays aimed past it
            lever._capsule = capsule;
            var ball = grab.AddComponent<SphereCollider>();
            ball.isTrigger = true;
            ball.center = lever._head.localPosition;
            ball.radius = 0.04f;
            ball.enabled = false;
            lever._ball = ball;

            lever._flash = HandsOnKit.Glow(PrimitiveType.Cylinder, "Flash", root, new Vector3(0f, 0.05f, 0f), new Vector3(0.16f, 0.002f, 0.16f), Color.clear);
            lever._flash.gameObject.SetActive(false);

            // the dock for a prop the crew parks at the lever (toward the middle of the table)
            lever._pad = HandsOnKit.Hexagon("Cradle", root, new Vector3(left ? 0.16f : -0.16f, 0f, 0.02f), 0.13f, 0.008f, HandsOnKit.Honey);
            lever._pad.gameObject.SetActive(false);

            lever.Apply();
            lever.Paint();
            lever.RefreshVisibility();
            return lever;
        }

        public void ShowCradle(bool on)
        {
            if (_pad != null && _pad.gameObject.activeSelf != on) _pad.gameObject.SetActive(on);
            RefreshVisibility();
        }

        // ------------------------------------------------------------ grab (IGameGrabTarget)
        public void BeginGrab(Vector3 grabPoint)
        {
            StopSim();
            Held = true;
            _grabPoint = grabPoint;
            _grabAngle = _angle;
            _target = _angle;
            if (!_live) Refused?.Invoke();
        }

        public void UpdateGrab(Vector3 handPoint)
        {
            // travel along the pull direction, as an angle on the arm's circle
            float travel = Vector3.Dot(transform.InverseTransformVector(handPoint - _grabPoint), PullDir);
            float a = _grabAngle + travel / (ArmLength + 0.03f) * Mathf.Rad2Deg;
            _target = Mathf.Clamp(a, 0f, _live ? MaxAngle : NotLiveAngle);
        }

        public void EndGrab() => Held = false;

        /// <summary>Desktop key / autopilot: one full pull, animated (0.35 s), through the same detents and snap.</summary>
        public void SimulatePull()
        {
            if (Held || _sim != null || !isActiveAndEnabled) return;
            if (!_live) Refused?.Invoke();
            _sim = StartCoroutine(SimRoutine());
        }

        IEnumerator SimRoutine()
        {
            float top = _live ? MaxAngle : NotLiveAngle, t = 0f;
            while (t < 0.35f && !Held)
            {
                t += Time.deltaTime;
                _target = top * HandsOnKit.Smooth(t / 0.35f);
                yield return null;
            }
            _target = 0f;
            _sim = null;
        }

        void StopSim()
        {
            if (_sim != null) StopCoroutine(_sim);
            _sim = null;
        }

        void OnDisable()
        {
            StopSim();
            Held = false;
            _angle = _target = _vel = 0f;
        }

        // ------------------------------------------------------------ motion
        void Update()
        {
            float dt = Time.deltaTime;
            if (Held || _sim != null)
            {
                _angle = Mathf.Lerp(_angle, _target, 1f - Mathf.Exp(-30f * dt));
                if (Mathf.Abs(_angle - _target) < 0.3f) _angle = _target;
                _vel = 0f;
            }
            else _angle = Mathf.SmoothDamp(_angle, 0f, ref _vel, 0.08f, Mathf.Infinity, dt); // springs back in ~0.25 s

            int tooth = Mathf.Clamp(Mathf.FloorToInt(_angle / ToothStep), 0, 5);
            if (tooth != _tooth)
            {
                if (tooth > _tooth && _live)
                {
                    _pop = 1f;
                    GameSound.Cue("lever_tick", _head);
                }
                _tooth = tooth;
            }
            if (_live && _armed && _angle >= SnapAngle)
            {
                _armed = false;
                _angle = MaxAngle;
                _pop = 1f;
                _flashAt = Time.time;
                Pulls++;
                GameSound.Cue("lever_snap", _head);
                Debug.Log("[VBS][HandsOn] lever pulled (" + Pulls + ")");
                Pulled?.Invoke();
            }
            else if (!_armed && _angle < RearmAngle) _armed = true;

            _pop = Mathf.Max(0f, _pop - dt / 0.18f);
            Apply();
            Paint();
        }

        void Apply()
        {
            if (_arm == null) return;
            _arm.localRotation = Quaternion.Euler(RestLean - _angle, 0f, 0f);
            _head.localScale = Vector3.one * (1f + 0.08f * _pop);
        }

        void Paint()
        {
            if (_discs == null) return;
            if (!_live && _painted && _pop <= 0f && _flashAt < 0f) return; // locked and still: nothing to repaint
            _painted = !_live;
            float pulse = _live ? 0.82f + 0.18f * Mathf.Sin(Time.time * 6f) : 0f;
            Color head = _live ? Color.Lerp(HandsOnKit.Honey, Color.white, 0.25f * pulse + 0.35f * _pop) : HandsOnKit.HoneyDim;
            for (int i = 0; i < _discs.Length; i++) GameMaterials.Tint(_discs[i], head);
            for (int k = 0; k < _teeth.Length; k++)
                GameMaterials.Tint(_teeth[k], _live && k < _tooth ? Color.Lerp(HandsOnKit.Honey, Color.white, 0.4f) : HandsOnKit.HoneyDim);
            if (_flashAt >= 0f)
            {
                float k = (Time.time - _flashAt) / 0.4f;
                if (k >= 1f) { _flashAt = -1f; _flash.gameObject.SetActive(false); }
                else
                {
                    if (!_flash.gameObject.activeSelf) _flash.gameObject.SetActive(true);
                    float s = Mathf.Lerp(0.16f, 0.5f, k);
                    _flash.transform.localScale = new Vector3(s, 0.002f, s);
                    GameMaterials.Tint(_flash, new Color(1f, 0.85f, 0.3f, 0.7f * (1f - k)));
                }
            }
        }
    }
}
