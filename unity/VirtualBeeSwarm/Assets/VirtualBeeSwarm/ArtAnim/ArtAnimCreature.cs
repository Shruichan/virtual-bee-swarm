using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Plays dartiros' baked insect clips (Legacy Animation on the same object, clips imported in place) on an art
    /// prefab that ArtSlots dresses a placeholder with. Gameplay code still moves the placeholder; this only animates
    /// the body: a flight loop (Hover), an optional idle when it stops moving, masked accents on a timer (Wag on the
    /// abdomen, Antenna Twitch on the head...) and, for the hornet, reactions to the swarm's ball (Startle when the
    /// ball closes, struggling inside it, Sad + wings still once the ball lets go = cooked).
    /// No prefab or no Animation = nothing happens (the temp model/primitive keeps working as before).
    /// </summary>
    [DisallowMultipleComponent]
    public class ArtAnimCreature : MonoBehaviour
    {
        [System.Serializable]
        public struct Accent
        {
            public string clip;
            [Tooltip("Bone (transform name) whose subtree the clip drives; empty = whole body.")]
            public string maskBone;
            public float minGap, maxGap;
        }

        public string loopClip = "Hover";
        [Tooltip("Played instead of the loop while the creature stays still (empty = always the loop).")]
        public string idleClip = "";
        public float idleBelowSpeed = 0.03f;
        public bool randomPhase = true;
        [Tooltip("Turn the art toward its flat travel direction (for placeholders that gameplay only moves).")]
        public bool faceVelocity;
        public Accent[] accents = new Accent[0];

        [Header("Hornet")]
        public bool hornetReactions;
        public string balledClip = "Startle";
        public string struggleClip = "Wag";
        public string cookedClip = "Sad";

        Animation _anim;
        SwarmSystem _swarm;
        Vector3 _lastPos;
        float _speed, _stillFor, _nextAccent;
        int _accentIdx;
        bool _idle, _balled, _cooked, _ready, _logged;
        string _base;

        void Awake()
        {
            _anim = GetComponent<Animation>();
            if (_anim == null) { enabled = false; return; }
            _anim.playAutomatically = false;
            if (!TryGetComponent<InsectBodySway>(out _)) gameObject.AddComponent<InsectBodySway>();
            foreach (var a in accents) SetupLayer(a.clip, a.maskBone, 1);
            if (hornetReactions)
            {
                SetupLayer(balledClip, null, 2);
                SetupLayer(struggleClip, "Abdomen", 1);
                var cooked = _anim[cookedClip];
                if (cooked != null) cooked.wrapMode = WrapMode.ClampForever;
            }
            var idle = string.IsNullOrEmpty(idleClip) ? null : _anim[idleClip];
            if (idle != null) idle.wrapMode = WrapMode.Loop;
            var loop = _anim[loopClip];
            if (loop != null) loop.wrapMode = WrapMode.Loop;
            _ready = loop != null;
            if (!_ready) Debug.LogWarning("[VBS][ArtAnim] " + name + ": no clip " + loopClip);
        }

        void SetupLayer(string clip, string bone, int layer)
        {
            var s = string.IsNullOrEmpty(clip) ? null : _anim[clip];
            if (s == null) return;
            s.layer = layer;
            s.wrapMode = WrapMode.Once;
            if (string.IsNullOrEmpty(bone)) return;
            var t = FindDeep(transform, bone);
            if (t != null) s.AddMixingTransform(t, true);
        }

        void OnEnable()
        {
            if (!_ready) return;
            _balled = _cooked = _idle = false;
            _stillFor = 0f;
            _lastPos = transform.position;
            _anim.Stop();
            PlayBase(loopClip, 0f);
            var s = _anim[loopClip];
            if (randomPhase && s != null) s.time = Random.value * s.length;
            _nextAccent = Time.time + Random.Range(1.5f, 4f);
            if (hornetReactions && _swarm == null) _swarm = FindFirstObjectByType<SwarmSystem>();
            if (!_logged)
            {
                _logged = true;
                Debug.Log("[VBS][ArtAnim] " + (transform.parent != null ? transform.parent.name : name) + " " + loopClip + " (shown)");
            }
        }

        void PlayBase(string clip, float fade)
        {
            if (string.IsNullOrEmpty(clip) || _anim[clip] == null || _base == clip && _anim.IsPlaying(clip)) return;
            _base = clip;
            if (fade <= 0f) _anim.Play(clip);
            else _anim.CrossFade(clip, fade);
        }

        void Update()
        {
            if (!_ready) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 p = transform.position;
            Vector3 v = (p - _lastPos) / dt;
            _lastPos = p;
            _speed = Mathf.Lerp(_speed, v.magnitude, 1f - Mathf.Exp(-dt * 10f));
            if (faceVelocity)
            {
                var flat = new Vector3(v.x, 0f, v.z);
                if (flat.sqrMagnitude > 0.0004f)
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(flat, Vector3.up), 1f - Mathf.Exp(-dt * 12f));
            }

            if (hornetReactions && HornetUpdate()) return;

            if (!string.IsNullOrEmpty(idleClip))
            {
                _stillFor = _speed < idleBelowSpeed ? _stillFor + dt : 0f;
                bool idle = _stillFor > 0.25f;
                if (idle != _idle)
                {
                    _idle = idle;
                    PlayBase(idle ? idleClip : loopClip, 0.2f);
                }
            }

            if (accents.Length > 0 && Time.time >= _nextAccent)
            {
                var a = accents[_accentIdx++ % accents.Length];
                if (_anim[a.clip] != null) _anim.CrossFade(a.clip, 0.1f);
                _nextAccent = Time.time + Random.Range(Mathf.Max(0.5f, a.minGap), Mathf.Max(a.minGap + 0.1f, a.maxGap));
            }
        }

        /// <summary>The swarm's ball around the hornet; true = it owns the animation this frame.</summary>
        bool HornetUpdate()
        {
            if (_swarm == null) return false;
            bool ball = _swarm.Mode == SwarmMode.Ball;
            if (ball && !_balled)
            {
                _balled = true;
                if (_anim[balledClip] != null) _anim.CrossFade(balledClip, 0.05f);
                _nextAccent = Time.time + 0.8f;
                Debug.Log("[VBS][ArtAnim] " + name + " " + balledClip + " (balled)");
            }
            if (_balled && !ball && !_cooked)
            {
                _cooked = true;
                if (_anim[struggleClip] != null) _anim.Stop(struggleClip);
                PlayBase(cookedClip, 0.3f);
                Debug.Log("[VBS][ArtAnim] " + name + " " + cookedClip + " (cooked)");
            }
            if (_cooked) return true;
            if (_balled && Time.time >= _nextAccent)
            {
                if (_anim[struggleClip] != null) _anim.CrossFade(struggleClip, 0.1f);
                _nextAccent = Time.time + Random.Range(0.6f, 1.2f);
                return true;
            }
            return _balled;
        }

        static Transform FindDeep(Transform root, string n)
        {
            if (root.name == n) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var t = FindDeep(root.GetChild(i), n);
                if (t != null) return t;
            }
            return null;
        }
    }
}
