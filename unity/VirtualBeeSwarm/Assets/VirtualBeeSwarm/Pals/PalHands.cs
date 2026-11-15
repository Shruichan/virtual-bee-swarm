using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm.Pals
{
    /// <summary>Optional: a hand source that knows the palm (Meta/OvrHandPoseSource, from the hand skeleton).</summary>
    public interface IHandPalmSource
    {
        /// <summary>Palm centre, normal (out of the palm), finger direction, fingers straight. False: no skeleton (controller).</summary>
        bool TryGetPalm(GameHand hand, out Vector3 centre, out Vector3 normal, out Vector3 fingers, out bool open);
    }

    /// <summary>One hand this frame, as the pal interactions see it.</summary>
    public struct PalHand
    {
        public bool tracked, palm, open, fist, pinch, ghost;
        public Vector3 centre, normal, fingers, tip, vel;
    }

    /// <summary>
    /// Reads both hands once a frame for Pals/ (boop, wave, high-five, palm bees, the honey frame): index tip from GameHands,
    /// the palm from an IHandPalmSource when there is one (hand tracking), velocity smoothed. Controllers give a tip only
    /// (boop works, the palm moves don't). Off the headset a ghost hand (a few primitives, only drawn then) stands in for
    /// the right hand, driven by PalDemo (F5-F9, or env VBS_PALS_DEMO for captures), so every move has a desktop path.
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public class PalHands : MonoBehaviour
    {
        public static PalHands Instance { get; private set; }
        public readonly PalHand[] Hands = new PalHand[2];

        // the ghost (right hand slot while it is up)
        public bool GhostUp { get; private set; }
        Vector3 _gCentre, _gNormal = Vector3.up, _gFingers = Vector3.forward;
        bool _gFist, _gPinch;
        Transform _ghost, _gPalm;
        readonly Transform[] _gFinger = new Transform[5];
        Material _gMat;
        readonly Vector3[] _prev = new Vector3[2];
        readonly bool[] _had = new bool[2];

        public static bool OffHeadset => !XRSettings.isDeviceActive && !GameHands.IsTracked(GameHand.Left) && !GameHands.IsTracked(GameHand.Right);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<GameDirector>() == null || FindAnyObjectByType<PalHands>() != null) return;
            var go = new GameObject("Pals");
            go.AddComponent<PalHands>();
            go.AddComponent<PalBeeswax>();
            go.AddComponent<PalBees>();
            go.AddComponent<HoneyFrame>();
            go.AddComponent<PalDemo>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_gMat != null) Destroy(_gMat);
        }

        /// <summary>Pose the ghost hand (world). normal = out of the palm, fingers = where they point.</summary>
        public void Ghost(Vector3 centre, Vector3 normal, Vector3 fingers, bool fist = false, bool pinch = false)
        {
            GhostUp = true;
            _gPinch = pinch;
            _gCentre = centre;
            _gNormal = normal.sqrMagnitude > 1e-6f ? normal.normalized : Vector3.up;
            _gFingers = Vector3.ProjectOnPlane(fingers, _gNormal);
            if (_gFingers.sqrMagnitude < 1e-6f) _gFingers = Vector3.Cross(_gNormal, Vector3.right);
            _gFingers.Normalize();
            _gFist = fist;
        }

        public void GhostDown() => GhostUp = false;

        /// <summary>The ghost's index tip for a pose (where a poke lands).</summary>
        public static Vector3 GhostTip(Vector3 centre, Vector3 normal, Vector3 fingers) =>
            centre + fingers.normalized * 0.095f + Vector3.Cross(normal, fingers).normalized * 0.022f;

        void Update()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            for (int h = 0; h < 2; h++)
            {
                var hand = (GameHand)h;
                var ph = new PalHand();
                if (h == 1 && GhostUp)
                {
                    ph.tracked = ph.palm = ph.ghost = true;
                    ph.centre = _gCentre;
                    ph.normal = _gNormal;
                    ph.fingers = _gFingers;
                    ph.fist = _gFist;
                    ph.pinch = _gPinch;
                    ph.open = !_gFist && !_gPinch;
                    ph.tip = GhostTip(_gCentre, _gNormal, _gFingers);
                }
                else if (GameHands.TryGetIndexTip(hand, out var tip))
                {
                    ph.tracked = true;
                    ph.tip = tip;
                    ph.fist = GameHands.IsFist(hand);
                    ph.pinch = GameHands.IsPinching(hand);
                    ph.centre = tip;
                    if (GameHands.For(hand) is IHandPalmSource ps && ps.TryGetPalm(hand, out var c, out var n, out var f, out bool open))
                    {
                        ph.palm = true;
                        ph.centre = c;
                        ph.normal = n;
                        ph.fingers = f;
                        ph.open = open && !ph.fist;
                    }
                }
                if (ph.tracked)
                {
                    Vector3 v = _had[h] ? (ph.centre - _prev[h]) / dt : Vector3.zero;
                    ph.vel = Vector3.Lerp(Hands[h].vel, v, 1f - Mathf.Exp(-dt / 0.06f));
                    _prev[h] = ph.centre;
                }
                _had[h] = ph.tracked;
                Hands[h] = ph;
            }
            DrawGhost();
        }

        // ------------------------------------------------------------------ the ghost hand (off-headset only)
        void DrawGhost()
        {
            if (!GhostUp)
            {
                if (_ghost != null && _ghost.gameObject.activeSelf) _ghost.gameObject.SetActive(false);
                return;
            }
            if (_ghost == null) BuildGhost();
            if (!_ghost.gameObject.activeSelf) _ghost.gameObject.SetActive(true);
            Quaternion rot = Quaternion.LookRotation(_gFingers, _gNormal); // z = fingers, y = out of the palm
            _ghost.SetPositionAndRotation(_gCentre - _gNormal * 0.012f, rot);
            // fingers: straight, or curled into the palm for a fist
            float curl = _gFist ? 1f : 0f;
            for (int i = 0; i < 4; i++)
            {
                var f = _gFinger[i];
                float x = (i - 1.5f) * 0.021f;
                f.localPosition = new Vector3(x, 0.012f * curl, 0.045f - 0.02f * curl);
                f.localRotation = Quaternion.Euler(90f - 115f * curl, 0f, 0f); // curls toward the palm side (+y)
            }
        }

        void BuildGhost()
        {
            _gMat = GameMaterials.Create("VBS_PalGhostHand (runtime)", new Color(0.96f, 0.78f, 0.64f, 1f));
            _ghost = new GameObject("PalGhostHand").transform;
            _ghost.SetParent(transform, false);
            _gPalm = Part(PrimitiveType.Cube, _ghost, new Vector3(0f, 0f, 0f), Quaternion.identity, new Vector3(0.085f, 0.022f, 0.09f));
            float[] len = { 0.075f, 0.085f, 0.08f, 0.065f };
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject("Finger" + i).transform;
                pivot.SetParent(_ghost, false);
                var seg = Part(PrimitiveType.Capsule, pivot, new Vector3(0f, len[i] * 0.5f, 0f), Quaternion.identity, new Vector3(0.018f, len[i] * 0.5f, 0.018f));
                seg.name = "Segment";
                _gFinger[i] = pivot;
            }
            // thumb on the index side (+x when y is the palm normal and z the fingers, for a right hand: Cross(n, f))
            var thumb = new GameObject("Thumb").transform;
            thumb.SetParent(_ghost, false);
            thumb.localPosition = new Vector3(0.05f, 0f, -0.01f);
            thumb.localRotation = Quaternion.Euler(80f, 50f, 0f);
            Part(PrimitiveType.Capsule, thumb, new Vector3(0f, 0.03f, 0f), Quaternion.identity, new Vector3(0.02f, 0.03f, 0.02f));
            _gFinger[4] = thumb;
        }

        Transform Part(PrimitiveType type, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _gMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }
    }
}
