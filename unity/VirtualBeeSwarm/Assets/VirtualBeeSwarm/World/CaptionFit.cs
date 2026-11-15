using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// v3.3: keeps world-space captions inside a comfortable field of view at their distance (Beeswax's caption, also
    /// when it sits at the SIGN HERE clipboard, and the pre instructions). Each frame, after everyone else has posed
    /// them: a caption wider than maxWidthDeg is scaled down to it, and one whose edge would sit more than maxHalfDeg
    /// to the side of the stage slides into view. Dialogue follows the headset's view and points back to Beeswax
    /// when the caption has moved away from its anchor. The offsets are removed again on the next frame.
    /// Installs itself into any scene with a GameDirector; no allocations per frame (it never reads TextMesh.text, which
    /// allocates; it logs only when the fit changes noticeably).
    /// </summary>
    [DefaultExecutionOrder(20000)]
    public class CaptionFit : MonoBehaviour
    {
        public static bool AutoInstall = true;

        [Tooltip("Widest a caption may be, seen from the head (degrees).")]
        [SerializeField] float maxWidthDeg = 30f;
        [Tooltip("No caption edge further than this to either side of the stage direction (degrees).")]
        [SerializeField] float maxHalfDeg = 26f;
        [Tooltip("Vertical angle available for the dialogue caption, measured from the headset's view.")]
        [SerializeField] float speechHalfPitchDeg = 18f;
        [Tooltip("Never shrink below this (a caption read from very close stays legible).")]
        [SerializeField] float minScale = 0.45f;

        sealed class Entry
        {
            public string what;
            public TextMesh text;
            public Renderer renderer;
            public Transform root;     // what moves and scales (Beeswax's Caption, or the text itself)
            public Transform stage;    // the direction it centres on (null: its parent)
            public bool speech;
            public bool applied;
            public Vector3 naturalLocalPos, naturalLocalScale, appliedLocalPos, appliedLocalScale;
            public Quaternion naturalLocalRot, appliedLocalRot;
            public float loggedK = 1f, loggedYaw;
        }

        readonly List<Entry> _entries = new List<Entry>(4);
        HiveBody _hive;
        BeeswaxController _beeswax;
        LineRenderer _leader;
        Material _leaderMaterial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<CaptionFit>() != null) return;
            var go = new GameObject("CaptionFit");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<CaptionFit>();
        }

        void Start()
        {
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            var bw = _beeswax = FindAnyObjectByType<BeeswaxController>(FindObjectsInactive.Include);
            if (bw != null)
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var tm = typeof(BeeswaxController).GetField("caption", F)?.GetValue(bw) as TextMesh;
                var root = typeof(BeeswaxController).GetField("captionRoot", F)?.GetValue(bw) as Transform;
                if (tm == null)
                    foreach (var t in bw.GetComponentsInChildren<TextMesh>(true))
                        if (t.transform.parent != null && t.transform.parent.name.StartsWith("Caption")) { tm = t; break; }
                if (tm != null)
                {
                    Add("beeswax caption", tm, root != null ? root : tm.transform.parent, null, speech: true);
                    BuildSpeechLeader();
                }
            }
            foreach (var tm in FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (tm.name == "Instructions") Add("pre instructions", tm, tm.transform, tm.transform.parent);
            Debug.Log("[VBS][Caption] fit " + _entries.Count + " caption(s) to " + maxWidthDeg.ToString("0") + " deg wide, +-" +
                      maxHalfDeg.ToString("0") + " deg of the stage");
        }

        void Add(string what, TextMesh tm, Transform root, Transform stage, bool speech = false)
        {
            if (tm == null || root == null) return;
            foreach (var e in _entries) if (e.text == tm) return;
            _entries.Add(new Entry
            {
                what = what, text = tm, renderer = tm.GetComponent<Renderer>(), root = root, stage = stage, speech = speech,
                naturalLocalPos = root.localPosition, naturalLocalScale = root.localScale, naturalLocalRot = root.localRotation,
            });
        }

        void OnDisable()
        {
            foreach (var e in _entries) Restore(e);
            if (_leader != null) _leader.enabled = false;
        }

        void OnDestroy()
        {
            if (_leaderMaterial != null) Destroy(_leaderMaterial);
        }

        void BuildSpeechLeader()
        {
            var go = new GameObject("Beeswax Caption Leader");
            go.transform.SetParent(transform, false);
            _leader = go.AddComponent<LineRenderer>();
            _leaderMaterial = GameMaterials.Create("VBS_CaptionLeader", new Color(1f, 0.77f, 0.28f, 0.8f),
                GameMaterials.Blend.Alpha, doubleSided: true);
            AR.ArMaterials.MarkNeverOccluded(_leaderMaterial);
            _leader.sharedMaterial = _leaderMaterial;
            _leader.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _leader.receiveShadows = false;
            _leader.positionCount = 2;
            _leader.useWorldSpace = true;
            _leader.startWidth = 0.005f;
            _leader.endWidth = 0.002f;
            _leader.enabled = false;
        }

        /// <summary>Take last frame's fit off again, unless an owner moved the caption since (then that is the new natural pose).</summary>
        static void Restore(Entry e)
        {
            if (!e.applied || e.root == null) return;
            e.applied = false;
            var r = e.root;
            if (r.localPosition == e.appliedLocalPos) r.localPosition = e.naturalLocalPos;
            if (r.localScale == e.appliedLocalScale) r.localScale = e.naturalLocalScale;
            if (r.localRotation == e.appliedLocalRot) r.localRotation = e.naturalLocalRot;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                if (_leader != null) _leader.enabled = false;
                return;
            }
            Transform head = cam.transform;
            bool leader = false;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.root == null || e.text == null) continue;
                // the natural pose this frame: what the owner left (BeeswaxController poses the root every frame when
                // it follows the clipboard, and turns it to the head every frame)
                if (e.applied)
                {
                    var r0 = e.root;
                    if (r0.localPosition == e.appliedLocalPos) r0.localPosition = e.naturalLocalPos;
                    if (r0.localScale == e.appliedLocalScale) r0.localScale = e.naturalLocalScale;
                    if (r0.localRotation == e.appliedLocalRot) r0.localRotation = e.naturalLocalRot;
                    e.applied = false;
                }
                e.naturalLocalPos = e.root.localPosition;
                e.naturalLocalScale = e.root.localScale;
                e.naturalLocalRot = e.root.localRotation;
                if (!e.text.gameObject.activeInHierarchy || e.renderer == null) continue; // empty text: zero bounds (Fit skips)
                if (e.speech) leader = FitSpeech(e, head);
                else Fit(e, head.position);
            }
            if (_leader != null && _leader.enabled != leader) _leader.enabled = leader;
        }

        bool FitSpeech(Entry e, Transform head)
        {
            Bounds bounds = e.renderer.localBounds;
            if (bounds.size.x < 1e-4f) return false;
            // BeeswaxController turns his caption to the head in Update, but his body (banking, turning, the rig's
            // animation) still moves after that, which rolled the card or turned it edge-on: face the head again,
            // upright (yaw only), before fitting
            bool faced = false;
            Vector3 away = e.root.position - head.position;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-4f)
            {
                var want = Quaternion.LookRotation(away, Vector3.up);
                if (Quaternion.Angle(want, e.root.rotation) > 0.5f) { e.root.rotation = want; faced = true; }
            }
            Transform text = e.text.transform;
            Vector3 center = text.TransformPoint(bounds.center);
            Vector3 offset = head.InverseTransformDirection(center - head.position);
            float naturalDistance = offset.magnitude;
            float distance = Mathf.Max(0.65f, naturalDistance);
            float halfWidth = Mathf.Max(1e-4f, bounds.extents.x * Mathf.Abs(text.lossyScale.x));
            float halfHeight = bounds.extents.y * Mathf.Abs(text.lossyScale.y);
            float yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(offset.y, Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z)) * Mathf.Rad2Deg;
            if (Mathf.Abs(yaw) > maxHalfDeg * 0.5f || Mathf.Abs(pitch) > speechHalfPitchDeg * 0.5f)
                distance = Mathf.Min(distance, 1.5f);
            float scale = Mathf.Clamp(Mathf.Tan(maxWidthDeg * 0.5f * Mathf.Deg2Rad) * distance / halfWidth, minScale, 1f);
            float widthAngle = Mathf.Atan2(halfWidth * scale, distance) * Mathf.Rad2Deg;
            float heightAngle = Mathf.Atan2(halfHeight * scale, distance) * Mathf.Rad2Deg;
            float yawLimit = Mathf.Max(0f, maxHalfDeg - widthAngle);
            float pitchLimit = Mathf.Max(0f, speechHalfPitchDeg - heightAngle);
            float fittedYaw = Mathf.Clamp(yaw, -yawLimit, yawLimit);
            float fittedPitch = Mathf.Clamp(pitch, -pitchLimit, pitchLimit);
            bool moved = Mathf.Abs(yaw - fittedYaw) >= 0.05f || Mathf.Abs(pitch - fittedPitch) >= 0.05f ||
                         Mathf.Abs(distance - naturalDistance) >= 0.001f;
            if (moved || scale < 0.999f)
            {
                Transform root = e.root;
                root.localScale = e.naturalLocalScale * scale;
                if (moved)
                {
                    Vector3 direction = head.TransformDirection(Quaternion.Euler(-fittedPitch, fittedYaw, 0f) * Vector3.forward);
                    root.rotation = Quaternion.LookRotation(direction, head.up);
                    root.position += head.position + direction * distance - text.TransformPoint(bounds.center);
                }
                e.applied = true;
                e.appliedLocalPos = root.localPosition;
                e.appliedLocalScale = root.localScale;
                e.appliedLocalRot = root.localRotation;
            }
            else if (faced)
            {
                e.applied = true;
                e.appliedLocalPos = e.root.localPosition;
                e.appliedLocalScale = e.root.localScale;
                e.appliedLocalRot = e.root.localRotation;
            }
            return moved && UpdateSpeechLeader(text, bounds, head);
        }

        bool UpdateSpeechLeader(Transform text, Bounds bounds, Transform head)
        {
            if (_leader == null || _beeswax == null) return false;
            Vector3 speaker = _beeswax.transform.position + Vector3.up * 0.06f;
            Vector3 towards = text.InverseTransformPoint(speaker) - bounds.center;
            towards.z = 0f;
            if (towards.sqrMagnitude < 1e-6f) towards = Vector3.down;
            float edge = Mathf.Max(Mathf.Abs(towards.x) / Mathf.Max(0.001f, bounds.extents.x),
                Mathf.Abs(towards.y) / Mathf.Max(0.001f, bounds.extents.y));
            Vector3 from = text.TransformPoint(bounds.center + 1.05f * towards / edge);
            if (Vector3.Dot(speaker - head.position, head.forward) < 0.18f)
                speaker = from + text.TransformDirection(towards.normalized) * 0.16f;
            if ((speaker - from).sqrMagnitude < 0.01f) return false;
            _leader.SetPosition(0, from);
            _leader.SetPosition(1, speaker);
            return true;
        }

        void Fit(Entry e, Vector3 head)
        {
            Bounds lb = e.renderer.localBounds; // the text mesh, in the TextMesh's own space
            if (lb.size.x < 1e-4f) return;
            Transform tt = e.text.transform;
            Vector3 center = tt.TransformPoint(lb.center);
            float halfW = lb.extents.x * Mathf.Abs(tt.lossyScale.x);
            Vector3 toC = center - head;
            toC.y = 0f;
            float d = toC.magnitude;
            if (d < 0.05f) return;

            // 1) width: no wider than maxWidthDeg at this distance
            float a = Mathf.Atan2(halfW, d) * Mathf.Rad2Deg;
            float k = 1f;
            if (a * 2f > maxWidthDeg) k = Mathf.Max(minScale, Mathf.Tan(maxWidthDeg * 0.5f * Mathf.Deg2Rad) * d / halfW);
            float aFit = Mathf.Atan2(halfW * k, d) * Mathf.Rad2Deg;

            // 2) side: both edges within maxHalfDeg of the stage direction
            Vector3 stageP = _hive != null && _hive.gameObject.activeInHierarchy && e.stage == null ? _hive.transform.position
                : e.stage != null ? e.stage.position : center;
            Vector3 toS = stageP - head;
            toS.y = 0f;
            float yaw = 0f;
            if (toS.sqrMagnitude > 1e-4f)
            {
                float c = Vector3.SignedAngle(toS, toC, Vector3.up); // + = right of the stage
                float lim = Mathf.Max(0f, maxHalfDeg - aFit);
                if (c > lim) yaw = lim - c;
                else if (c < -lim) yaw = -lim - c;
            }
            if (k >= 0.999f && Mathf.Abs(yaw) < 0.05f) { Log(e, a * 2f, 0f, 1f); return; } // fits as it is

            var root = e.root;
            if (k < 0.999f) root.localScale = e.naturalLocalScale * k;
            if (Mathf.Abs(yaw) >= 0.05f)
            {
                Quaternion q = Quaternion.AngleAxis(yaw, Vector3.up);
                root.position = head + q * (root.position - head);
                root.rotation = q * root.rotation; // still faces the head
            }
            e.applied = true;
            e.appliedLocalPos = root.localPosition;
            e.appliedLocalScale = root.localScale;
            e.appliedLocalRot = root.localRotation;
            Log(e, a * 2f, yaw, k);
        }

        void Log(Entry e, float widthDeg, float yaw, float k)
        {
            if (Mathf.Abs(k - e.loggedK) < 0.05f && Mathf.Abs(yaw - e.loggedYaw) < 3f) return;
            e.loggedK = k;
            e.loggedYaw = yaw;
            Debug.Log("[VBS][Caption] " + e.what + ": " + widthDeg.ToString("0") + " deg wide -> scale " + k.ToString("0.00") +
                      ", moved " + yaw.ToString("0.0") + " deg toward the stage");
        }
    }
}
