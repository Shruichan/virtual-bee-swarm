using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Beeswax, the union-rep drone (placeholder: sphere body + stripes + wings). Flies to points, faces the
    /// player, bobs, flaps, pulses his mouth/body with his voice (GameNet.VoiceAmplitude), shows his caption
    /// above him, rides the rocket at T-0 and comes back dusty.
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

        public bool Arrived { get; private set; } = true;
        public bool Visible => body == null || body.gameObject.activeSelf;

        Vector3 _target;
        Transform _targetT, _attached;
        bool _lookAtPlayer = true;
        float _whipUntil = -1f;
        string _shownCaption;
        Vector3 _bodyBaseScale = Vector3.one;
        Vector3 _restPos;
        Quaternion _restRot;
        Quaternion _leftWingRest, _rightWingRest;
        bool _dusty;

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (body != null) _bodyBaseScale = body.localScale;
            if (leftWing != null) _leftWingRest = leftWing.localRotation;
            if (rightWing != null) _rightWingRest = rightWing.localRotation;
            _restPos = transform.position;
            _restRot = transform.rotation;
            _target = homePoint != null ? homePoint.position : transform.position;
            if (caption != null) caption.text = "";
        }

        // ------------------------------------------------------------ API
        public void FlyTo(Transform t)
        {
            _attached = null;
            _targetT = t;
            Arrived = false;
        }

        public void FlyTo(Vector3 worldPoint)
        {
            _attached = null;
            _targetT = null;
            _target = worldPoint;
            Arrived = false;
        }

        public void GoHome() { if (homePoint != null) FlyTo(homePoint); }
        public void GoPresent() { if (presenterPoint != null) FlyTo(presenterPoint); }

        /// <summary>Ride something exactly (the rocket seat).</summary>
        public void AttachTo(Transform seat)
        {
            _attached = seat;
            _targetT = null;
            Arrived = true;
        }

        public void Detach()
        {
            if (_attached != null) _target = _attached.position;
            _attached = null;
        }

        public void SnapTo(Transform t)
        {
            if (t == null) return;
            _attached = null;
            _targetT = t;
            transform.position = t.position;
            Arrived = true;
        }

        public void SetVisible(bool on)
        {
            if (body != null && body.gameObject.activeSelf != on) body.gameObject.SetActive(on);
            if (captionRoot != null && captionRoot.gameObject.activeSelf != on) captionRoot.gameObject.SetActive(on);
        }

        public void SetDusty(bool on)
        {
            _dusty = on;
            if (on) GameMaterials.TintAll(dustyRenderers, dustyColor);
            else GameMaterials.ClearAll(dustyRenderers);
        }

        public void LookAtPlayer(bool on) => _lookAtPlayer = on;

        /// <summary>A quick spin (surprise).</summary>
        public void WhipAround() => _whipUntil = Time.time + 0.5f;

        public void ResetBeeswax()
        {
            _attached = null;
            _targetT = homePoint;
            transform.position = homePoint != null ? homePoint.position : _restPos;
            transform.rotation = _restRot;
            Arrived = true;
            _lookAtPlayer = true;
            SetDusty(false);
            SetVisible(true);
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
            if (_attached != null)
            {
                transform.position = _attached.position;
                transform.rotation = _attached.rotation;
            }
            else
            {
                Vector3 goal = _targetT != null ? _targetT.position : _target;
                Vector3 p = transform.position;
                float dist = Vector3.Distance(p, goal);
                float speed = flySpeed * Mathf.Clamp(dist * 4f, 0.25f, 1f);
                transform.position = Vector3.MoveTowards(p, goal, speed * dt);
                if (!Arrived && dist < 0.01f) Arrived = true;

                // rotation: face the flight direction on long hops, otherwise the player
                Quaternion want = transform.rotation;
                if (!Arrived && dist > 0.12f) want = Quaternion.LookRotation(goal - p, Vector3.up);
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
                body.localPosition = new Vector3(0f, Mathf.Sin(t * bobHz * Mathf.PI * 2f) * bobAmplitude, 0f);
                body.localScale = _bodyBaseScale * (1f + amp * talkScalePulse);
            }
            if (mouth != null) mouth.localScale = Vector3.Lerp(mouthClosedScale, mouthOpenScale, amp);
            float flap = Mathf.Sin(t * wingFlapHz * Mathf.PI * 2f) * wingFlapDegrees * (_dusty ? 0.3f : 1f);
            if (leftWing != null) leftWing.localRotation = _leftWingRest * Quaternion.Euler(0f, 0f, flap);
            if (rightWing != null) rightWing.localRotation = _rightWingRest * Quaternion.Euler(0f, 0f, -flap);

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
            if (captionRoot != null && headT != null)
            {
                Vector3 away = captionRoot.position - headT.position;
                away.y = 0f;
                if (away.sqrMagnitude > 1e-4f) captionRoot.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }
    }
}
