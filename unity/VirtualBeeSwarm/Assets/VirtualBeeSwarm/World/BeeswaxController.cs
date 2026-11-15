using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Beeswax, the union-rep drone (placeholder: sphere body + stripes + wings). Flies to points, faces the
    /// player, bobs, flaps, pulses his mouth/body with his voice (GameNet.VoiceAmplitude), shows his caption
    /// above him, rides the rocket at T-0 and comes back dusty. v3.1 (V14): waits hidden inside the delivered hive
    /// (HideInside), then climbs out of the door by code (ClimbOut: squeeze through the slot, pop, shake off, fly up to
    /// his mark); EmergeNow snaps that end state for a Board jump.
    /// Modeler: replace the children of `body`; keep `mouth` (scaled on Y), the wing transforms (rotated on Z),
    /// and list renderers to grey out in `dustyRenderers`.
    /// </summary>
    public class BeeswaxController : MonoBehaviour
    {
        [SerializeField] GameNet net;
        [Tooltip("Visual root; moves with bob/pulse. The modeler's Beeswax goes under it, facing +Z.")]
        [SerializeField] Transform body;
        [SerializeField] Transform mouth;
        [SerializeField] Vector3 mouthClosedScale = new Vector3(0.03f, 0.004f, 0.012f);
        [SerializeField] Vector3 mouthOpenScale = new Vector3(0.035f, 0.022f, 0.014f);
        [SerializeField] Transform leftWing;
        [SerializeField] Transform rightWing;
        [SerializeField] Renderer[] dustyRenderers = new Renderer[0];
        [SerializeField] Color dustyColor = new Color(0.62f, 0.6f, 0.58f, 1f);

        [Header("Caption")]
        [SerializeField] TextMesh caption;
        [SerializeField] Transform captionRoot;
        [SerializeField] int captionWrapChars = 34;

        [Header("Places")]
        [SerializeField] Transform homePoint;
        [SerializeField] Transform presenterPoint;
        [Tooltip("The player's head (empty = main camera).")]
        [SerializeField] Transform head;

        [Header("Motion")]
        [SerializeField] float flySpeed = 0.9f;
        [SerializeField] float bobAmplitude = 0.012f;
        [SerializeField] float bobHz = 1.4f;
        [SerializeField] float turnDegreesPerSecond = 240f;
        [SerializeField] float talkScalePulse = 0.07f;
        [SerializeField] float wingFlapHz = 18f;
        [SerializeField] float wingFlapDegrees = 35f;

        [Header("Climb out of the hive (V14)")]
        [SerializeField] float squeezeSeconds = 0.55f;
        [SerializeField] float popSeconds = 0.14f;
        [SerializeField] float shakeSeconds = 0.5f;
        [Tooltip("How far inside the door he starts, and how far out he pops (m).")]
        [SerializeField] float squeezeFrom = 0.035f;
        [SerializeField] float squeezeTo = 0.035f;
        [Tooltip("Body scale while squeezing through the slot (y = flattened).")]
        [SerializeField] Vector3 squeezeScale = new Vector3(1.12f, 0.42f, 1.1f);
        [SerializeField] float shakeDegrees = 32f;
        [SerializeField] float shakeHz = 11f;
        [Tooltip("Flight speed up to his mark after the shake (m/s).")]
        [SerializeField] float climbFlySpeed = 1.1f;

        public bool Arrived { get; private set; } = true;
        /// <summary>Set by BeeswaxRig (Daniel's rigged model): his Animator flaps the wings and moves the mouth, so the
        /// procedural wing flap and mouth scale below stop. Root motion (fly, face, bob, voice pulse) stays here.</summary>
        public bool RigDriven { get; set; }
        /// <summary>Where he waits (the hook mark after he climbs out of the hive).</summary>
        public Transform HomePoint => homePoint;
        public bool Visible => body == null || body.gameObject.activeSelf;
        /// <summary>Hidden inside the hive (HideInside), not climbing yet.</summary>
        public bool Inside { get; private set; }
        /// <summary>Squeezing out / shaking off (the fly-up after it is a normal flight: watch Arrived).</summary>
        public bool Climbing { get; private set; }

        Vector3 _target;
        Transform _targetT, _attached;
        bool _lookAtPlayer = true;
        float _whipUntil = -1f;
        string _shownCaption;
        Vector3 _bodyBaseScale = Vector3.one;
        Quaternion _bodyBaseRot = Quaternion.identity;
        Vector3 _captionBaseLocal;
        Transform _captionAnchor;
        float _captionAnchorUp;
        Transform _door, _climbMark;
        Vector3 _climbOut = Vector3.back;
        float _climbStart, _flySpeedOverride = -1f;
        Vector3 _climbScale = Vector3.one;
        Quaternion _climbRot = Quaternion.identity;
        bool _wingsFolded;
        System.Action _climbDone;
        Vector3 _restPos;
        Quaternion _restRot;
        Quaternion _leftWingRest, _rightWingRest;
        bool _dusty;

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (body != null) { _bodyBaseScale = body.localScale; _bodyBaseRot = body.localRotation; }
            if (captionRoot != null) _captionBaseLocal = captionRoot.localPosition;
            if (leftWing != null) _leftWingRest = leftWing.localRotation;
            if (rightWing != null) _rightWingRest = rightWing.localRotation;
            _restPos = transform.position;
            _restRot = transform.rotation;
            _target = homePoint != null ? homePoint.position : transform.position;
            if (caption != null) caption.text = "";
        }

        // ---- [BeeswaxMotion hook] ----
        // BeeswaxMotion (BeeswaxMotion/) drives the root while he is free (not climbing, not attached): how he flies,
        // faces and holds a mark. Null RootDriver and neutral Body* = exactly the code below.
        public interface IRootDriver { bool Drive(BeeswaxController b, Vector3 goal, float dt, out bool arrived); }
        public IRootDriver RootDriver { get; set; }
        public Vector3 Goal => _targetT != null ? _targetT.position : _target;
        public Transform GoalTransform => _targetT;
        public Transform PresenterPoint => presenterPoint;
        public Transform HeadTransform => Head();
        public Transform BodyTransform => body;
        public bool IsAttached => _attached != null;
        public Transform AttachedTransform => _attached;
        public bool LooksAtPlayer => _lookAtPlayer;
        public float BobAmplitude => bobAmplitude;
        public float BobHz => bobHz;
        /// <summary>fly present home snap attach detach climb emerge hide reset whip (after the state change).</summary>
        public event System.Action<string> Ordered;
        public Vector3 BodyOffset { get; set; }
        public Quaternion BodyTilt { get; set; } = Quaternion.identity;
        public Vector3 BodySquash { get; set; } = Vector3.one;
        public float BobScale { get; set; } = 1f;
        void RaiseOrdered(string what)
        {
            try { Ordered?.Invoke(what); } catch (System.Exception e) { Debug.LogException(e); }
        }
        // ---- end hook ----

        // ---- [BeeswaxBody hook] ----
        // BeeswaxBody (BeeswaxBody/) keeps him out of the hive, the rocket, the props and the player's head: it moves his
        // goal to a free spot, steers the stock flight around things, sets how far the climb pops him out of the door
        // and where he sits on a rocket seat. Null GoalFilter, ClimbScale 1, AttachOffset zero = exactly the code below.
        public interface IGoalFilter { Vector3 FilterGoal(Vector3 goal); Vector3 Steer(Vector3 from, Vector3 goal); }
        public IGoalFilter GoalFilter { get; set; }
        public float ClimbScale { get; set; } = 1f;
        /// <summary>Where he sits on what he is attached to, in its rotation frame (world metres).</summary>
        public Vector3 AttachOffset { get; set; }
        public Transform DoorTransform => _door;
        public Vector3 ClimbOutward => _climbOut;
        public float ClimbExitDistance => squeezeTo + 0.012f;
        /// <summary>How deep inside the door the squeeze starts (the rig's scale only, not the bigger pop).</summary>
        public float ClimbInScale { get; set; } = 1f;
        public Vector3 BodyBaseScale => _bodyBaseScale;
        public Quaternion BodyBaseRot => _bodyBaseRot;
        // ---- end hook ----

        // ------------------------------------------------------------ API
        public void FlyTo(Transform t)
        {
            EndClimbState();
            _attached = null;
            _targetT = t;
            Arrived = false;
            RaiseOrdered(t != null && t == presenterPoint ? "present" : t != null && t == homePoint ? "home" : "fly"); // [BeeswaxMotion hook]
        }

        public void FlyTo(Vector3 worldPoint)
        {
            EndClimbState();
            _attached = null;
            _targetT = null;
            _target = worldPoint;
            Arrived = false;
            RaiseOrdered("fly"); // [BeeswaxMotion hook]
        }

        public void GoHome() { if (homePoint != null) FlyTo(homePoint); }
        public void GoPresent() { if (presenterPoint != null) FlyTo(presenterPoint); }

        /// <summary>Ride something exactly (the rocket seat).</summary>
        public void AttachTo(Transform seat)
        {
            _attached = seat;
            _targetT = null;
            Arrived = true;
            RaiseOrdered("attach"); // [BeeswaxMotion hook]
        }

        public void Detach()
        {
            if (_attached != null) _target = _attached.position + _attached.rotation * AttachOffset; // [BeeswaxBody hook] AttachOffset
            _attached = null;
            RaiseOrdered("detach"); // [BeeswaxMotion hook]
        }

        public void SnapTo(Transform t)
        {
            if (t == null) return;
            EndClimbState();
            _attached = null;
            _targetT = t;
            transform.position = t.position;
            Arrived = true;
            RaiseOrdered("snap"); // [BeeswaxMotion hook]
        }

        public void SetVisible(bool on)
        {
            if (body != null && body.gameObject.activeSelf != on) body.gameObject.SetActive(on);
            if (captionRoot != null && captionRoot.gameObject.activeSelf != on) captionRoot.gameObject.SetActive(on);
        }

        /// <summary>Body only: his caption stays (he talks from inside the hive while it is carried in).</summary>
        public void SetBodyVisible(bool on)
        {
            if (body != null && body.gameObject.activeSelf != on) body.gameObject.SetActive(on);
        }

        /// <summary>Show his caption at another object (the SIGN HERE clipboard in pre) instead of over him; null = back over him.</summary>
        public void SetCaptionAnchor(Transform t, float up = 0.12f)
        {
            _captionAnchor = t;
            _captionAnchorUp = up;
            if (t == null && captionRoot != null) captionRoot.localPosition = _captionBaseLocal;
        }

        /// <summary>Where his caption is parked right now (null: over him). [polish hook: LevelMap keeps it off the map / end card]</summary>
        public Transform CaptionAnchor => _captionAnchor;

        // ------------------------------------------------------------ hive door (V14)
        /// <summary>v3.1 pre: inside the hive (riding its door point, body hidden) until ClimbOut.</summary>
        public void HideInside(Transform door)
        {
            EndClimbState();
            _door = door;
            if (door != null) AttachTo(door);
            SetBodyVisible(false);
            Inside = true;
            RaiseOrdered("hide"); // [BeeswaxMotion hook]
        }

        /// <summary>
        /// Climb out of the hive door: squeeze through the slot (flattened, wriggling), pop to full size, shake off like a
        /// wet dog, then fly up to `mark`. outward = out of the door, into the room. onOut runs when the shake ends.
        /// </summary>
        public void ClimbOut(Vector3 outward, Transform mark, System.Action onOut = null)
        {
            if (_door == null) _door = _attached;
            outward.y = 0f;
            _climbOut = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.back;
            _climbMark = mark;
            _climbDone = onOut;
            _attached = null;
            _targetT = null;
            Inside = false;
            Climbing = true;
            _climbStart = Time.time;
            SetBodyVisible(true);
            Arrived = true;
            RaiseOrdered("climb"); // [BeeswaxMotion hook]
        }

        /// <summary>A jump past the climb: out now, at `mark`. A flight someone already ordered keeps its target.</summary>
        public void EmergeNow(Transform mark)
        {
            if (!Inside && !Climbing) return;
            bool ordered = Inside && _attached == null; // FlyTo/SnapTo while inside: keep that target
            EndClimbState();
            Inside = false;
            SetBodyVisible(true);
            if (mark != null) transform.position = mark.position;
            if (!ordered)
            {
                _attached = null;
                _targetT = mark;
                if (mark == null) _target = transform.position;
                Arrived = true;
            }
            RaiseOrdered("emerge"); // [BeeswaxMotion hook]
        }

        void EndClimbState()
        {
            var done = Climbing ? _climbDone : null;
            Climbing = false;
            _climbDone = null;
            _climbScale = Vector3.one;
            _climbRot = Quaternion.identity;
            _wingsFolded = false;
            done?.Invoke();
        }

        bool UpdateClimb(float t)
        {
            if (!Climbing) return false;
            float ct = t - _climbStart;
            Vector3 door = _door != null ? _door.position : transform.position;
            Vector3 right = Vector3.Cross(Vector3.up, _climbOut);
            Quaternion face = Quaternion.LookRotation(_climbOut, Vector3.up);
            float sq = Mathf.Max(0.05f, squeezeSeconds), pop = Mathf.Max(0.02f, popSeconds), sh = Mathf.Max(0.05f, shakeSeconds);
            float cs = Mathf.Max(1f, ClimbScale); // [BeeswaxBody hook] a bigger body pops out farther
            float ci = Mathf.Clamp(ClimbInScale, 1f, cs); // [BeeswaxBody hook] ...but starts no deeper than the rig's scale
            if (ct < sq)
            {
                // squeeze: flattened through the slot, wriggling side to side, wings folded
                float k = ct / sq, e = Mathf.SmoothStep(0f, 1f, k);
                float wrig = Mathf.Sin(ct * Mathf.PI * 2f * 9f);
                transform.position = door + _climbOut * Mathf.Lerp(-squeezeFrom * ci, squeezeTo * cs, e) + right * (wrig * 0.006f * cs * (1f - k));
                transform.rotation = face;
                _climbScale = Vector3.Lerp(squeezeScale, Vector3.Lerp(squeezeScale, Vector3.one, 0.6f), e);
                _climbRot = Quaternion.Euler(0f, 0f, wrig * 9f);
                _wingsFolded = true;
                return true;
            }
            if (ct < sq + pop)
            {
                // pop: springs to full size with an overshoot, a little hop out
                float k = (ct - sq) / pop;
                float over = Mathf.Sin(k * Mathf.PI) * 0.18f;
                Vector3 from = Vector3.Lerp(squeezeScale, Vector3.one, 0.6f);
                _climbScale = Vector3.Lerp(from, Vector3.one, k) + new Vector3(-over * 0.3f, over, -over * 0.3f);
                _climbRot = Quaternion.identity;
                transform.position = door + _climbOut * ((squeezeTo + 0.012f * k) * cs) + Vector3.up * (0.012f * k * cs);
                transform.rotation = face;
                _wingsFolded = false;
                return true;
            }
            if (ct < sq + pop + sh)
            {
                // shake off: a fast decaying roll (and a smaller yaw), wings buzzing
                float u = (ct - sq - pop) / sh;
                float a = Mathf.Sin(u * sh * Mathf.PI * 2f * shakeHz) * shakeDegrees * (1f - u) * (1f - u);
                _climbScale = Vector3.one;
                _climbRot = Quaternion.Euler(0f, a * 0.25f, a);
                transform.position = door + _climbOut * ((squeezeTo + 0.012f) * cs) + Vector3.up * ((0.012f + 0.01f * Mathf.Sin(u * Mathf.PI)) * cs);
                transform.rotation = face;
                return true;
            }
            // up to the mark: a normal flight from here
            var done = _climbDone;
            _climbDone = null;
            Climbing = false;
            _climbScale = Vector3.one;
            _climbRot = Quaternion.identity;
            _targetT = _climbMark;
            if (_climbMark == null) _target = transform.position + Vector3.up * 0.25f;
            _flySpeedOverride = climbFlySpeed;
            Arrived = false;
            done?.Invoke();
            return false;
        }

        public void SetDusty(bool on)
        {
            _dusty = on;
            if (on) GameMaterials.TintAll(dustyRenderers, dustyColor);
            else GameMaterials.ClearAll(dustyRenderers);
        }

        public void LookAtPlayer(bool on) => _lookAtPlayer = on;

        /// <summary>A quick spin (surprise).</summary>
        public void WhipAround() { _whipUntil = Time.time + 0.5f; RaiseOrdered("whip"); } // [BeeswaxMotion hook]

        public void ResetBeeswax()
        {
            _climbDone = null;
            EndClimbState();
            Inside = false;
            _door = null;
            _flySpeedOverride = -1f;
            SetCaptionAnchor(null);
            if (body != null) body.localRotation = _bodyBaseRot;
            _attached = null;
            _targetT = homePoint;
            transform.position = homePoint != null ? homePoint.position : _restPos;
            transform.rotation = _restRot;
            Arrived = true;
            _lookAtPlayer = true;
            SetDusty(false);
            SetVisible(true);
            RaiseOrdered("reset"); // [BeeswaxMotion hook]
        }

        Vector3 FilteredGoal() // [BeeswaxBody hook]
        {
            Vector3 g = _targetT != null ? _targetT.position : _target;
            return GoalFilter != null ? GoalFilter.FilterGoal(g) : g;
        }

        Transform Head()
        {
            if (head != null) return head;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            float dt = Time.deltaTime;
            float t = Time.time;
            Transform headT = Head();

            // position
            if (UpdateClimb(t)) { }
            else if (_attached != null)
            {
                transform.position = _attached.position + _attached.rotation * AttachOffset; // [BeeswaxBody hook] AttachOffset
                transform.rotation = _attached.rotation;
            }
            else if (RootDriver != null && RootDriver.Drive(this, FilteredGoal(), dt, out bool driverArrived)) // [BeeswaxMotion hook] [BeeswaxBody hook] FilteredGoal
            {
                if (!Arrived && driverArrived) { Arrived = true; _flySpeedOverride = -1f; }
            }
            else
            {
                Vector3 goal = FilteredGoal(); // [BeeswaxBody hook]
                Vector3 p = transform.position;
                float dist = Vector3.Distance(p, goal);
                float speed = (_flySpeedOverride > 0f ? _flySpeedOverride : flySpeed) * Mathf.Clamp(dist * 4f, 0.25f, 1f);
                Vector3 steer = GoalFilter != null ? GoalFilter.Steer(p, goal) : goal; // [BeeswaxBody hook] around what is in the way
                transform.position = Vector3.MoveTowards(p, steer, speed * dt);
                if (!Arrived && dist < 0.01f) { Arrived = true; _flySpeedOverride = -1f; }

                // rotation: face the flight direction on long hops, otherwise the player
                Quaternion want = transform.rotation;
                if (!Arrived && dist > 0.12f && (steer - p).sqrMagnitude > 1e-8f) want = Quaternion.LookRotation(steer - p, Vector3.up);
                else if (_lookAtPlayer && headT != null)
                {
                    Vector3 to = headT.position - transform.position;
                    if (to.sqrMagnitude > 1e-4f) want = Quaternion.LookRotation(to, Vector3.up);
                }
                if (t < _whipUntil) want = transform.rotation * Quaternion.Euler(0f, 180f, 0f);
                float turn = t < _whipUntil ? turnDegreesPerSecond * 4f : turnDegreesPerSecond;
                transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turn * dt);
            }

            // bob + talk pulse + wings
            float amp = net != null ? net.VoiceAmplitude : 0f;
            if (body != null)
            {
                // [BeeswaxMotion hook] BobScale, BodyOffset, BodySquash, BodyTilt (neutral = unchanged)
                body.localPosition = new Vector3(0f, Climbing ? 0f : Mathf.Sin(t * bobHz * Mathf.PI * 2f) * bobAmplitude * BobScale, 0f) + BodyOffset;
                body.localScale = Vector3.Scale(Vector3.Scale(_bodyBaseScale, _climbScale), BodySquash) * (1f + amp * talkScalePulse);
                body.localRotation = _bodyBaseRot * _climbRot * BodyTilt;
            }
            if (!RigDriven)
            {
                if (mouth != null) mouth.localScale = Vector3.Lerp(mouthClosedScale, mouthOpenScale, amp);
                float flap = _wingsFolded ? 0f : Mathf.Sin(t * wingFlapHz * Mathf.PI * 2f) * wingFlapDegrees * (_dusty ? 0.3f : 1f);
                if (leftWing != null) leftWing.localRotation = _leftWingRest * Quaternion.Euler(0f, 0f, flap);
                if (rightWing != null) rightWing.localRotation = _rightWingRest * Quaternion.Euler(0f, 0f, -flap);
            }

            // caption (text changes only when the line changes: no per-frame allocation)
            if (caption != null && net != null)
            {
                string c = net.CurrentCaption;
                if (!ReferenceEquals(c, _shownCaption))
                {
                    _shownCaption = c;
                    caption.text = GameMaterials.Wrap(c, captionWrapChars);
                }
            }
            if (captionRoot != null && _captionAnchor != null)
                captionRoot.position = _captionAnchor.position + Vector3.up * _captionAnchorUp;
            if (captionRoot != null && headT != null)
            {
                Vector3 away = captionRoot.position - headT.position;
                away.y = 0f;
                if (away.sqrMagnitude > 1e-4f) captionRoot.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }
    }
}
