using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Plays one of dartiros' prop clips (Legacy Animation on the same object) when the gameplay event already
    /// happens:
    ///  - ParticleBurst: a named ParticleSystem (SmokePuff, WaterPuff) just emitted a burst -> smoker bellows squeeze,
    ///    spray trigger pull. The automatic station watches the particle burst. A manual pull calls PlayManualAction.
    ///  - ObjectActive: a named child switched on -> rocket Flame (T0Sequence turns it on at liftoff): ignition, then
    ///    the flicker loop; off = stopped (rest pose: flame scaled to nothing).
    ///  - Beat: GameDirector.BeatChanged reached a beat -> hive lid opens at the escape; back before it = closes
    ///    (a reset to pre snaps it shut).
    /// Logs "[VBS][ArtAnim] &lt;object&gt; &lt;clip&gt; (&lt;why&gt;)" when it plays.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArtAnimProp : MonoBehaviour
    {
        public enum Trigger { ParticleBurst, ObjectActive, Beat }

        public Trigger trigger;
        public string clip;
        [Tooltip("Looped after clip (ObjectActive) / played going back before the beat (Beat).")]
        public string followClip;
        public float speed = 1f;

        [Header("ParticleBurst")]
        [Tooltip("ParticleSystem object name: searched under this prop first, then in the scene.")]
        public string particleName;
        public int burstMin = 5;
        public float minInterval = 0.2f;
        [Tooltip("Plays per detected burst (the smoker puffs 3 times, 0.4 s apart, and the puff system caps its particles, so only the first burst is reliably visible).")]
        public int repeat = 1;
        public float repeatGap = 0.4f;
        [Tooltip("Rome (agent): clip speed for a manual action (a pump the player did), capped so one squeeze reads.")]
        public float manualSpeed = 1f;

        [Header("ObjectActive")]
        public string watchName;

        [Header("Beat")]
        public GameBeat beat = GameBeat.Escape;
        public float delay = 0.3f;

        Animation _anim;
        ParticleSystem _ps;
        Transform _watch;
        GameDirector _director;
        int _lastCount;
        float _lastFire = -10f, _pending = -1f, _seqEnd = -1f, _nextRepeat = -1f;
        int _repeatsLeft;
        bool _wasActive, _open, _searched;
        float _punchAt = -1f; // Rome (agent): squash punch on a manual action
        Vector3 _punchBase;

        void Awake()
        {
            _anim = GetComponent<Animation>();
            if (_anim == null || _anim[clip] == null)
            {
                Debug.LogWarning("[VBS][ArtAnim] " + name + ": no Animation/clip " + clip);
                enabled = false;
                return;
            }
            _anim.playAutomatically = false;
            // Rome (agent): BasedOnRenderers left the props frozen whenever Unity judged them off-screen; a few props, cheap
            _anim.cullingType = AnimationCullingType.AlwaysAnimate;
            _anim[clip].speed = speed;
            if (trigger == Trigger.ObjectActive && !string.IsNullOrEmpty(followClip) && _anim[followClip] != null)
                _anim[followClip].wrapMode = WrapMode.Loop;
            if (trigger == Trigger.Beat) _anim[clip].wrapMode = WrapMode.ClampForever;
            if (trigger == Trigger.ObjectActive) _watch = FindDeep(transform, watchName);
        }

        void OnEnable()
        {
            // the station hid this prop meanwhile (and cleared its puffs): count from what is there now
            if (_ps != null) _lastCount = _ps.particleCount;
            _repeatsLeft = 0;
            _seqEnd = -1f;
            _punchAt = -1f;
            if (trigger != Trigger.Beat) return;
            if (_director == null) _director = GameDirector.Instance != null ? GameDirector.Instance : FindFirstObjectByType<GameDirector>();
            if (_director == null) return;
            _director.BeatChanged += OnBeat;
            if (_director.Beat < beat) Snap(false);
            else if (!_open && _director.Beat != GameBeat.Field) _pending = Time.time + delay;
        }

        void OnDisable()
        {
            if (_punchAt >= 0f) { transform.localScale = _punchBase; _punchAt = -1f; }
            _pending = -1f;
            if (_director != null) _director.BeatChanged -= OnBeat;
        }

        void OnBeat(GameBeat from, GameBeat to)
        {
            if (to == GameBeat.Field) { _pending = -1f; return; }
            if (to >= beat && !_open) { _pending = Time.time + delay; return; }
            if (to < beat)
            {
                _pending = -1f;
                if (!_open) return;
                if (to == GameBeat.Pre) Snap(false);
                else Play(followClip, "beat " + to);
                _open = false;
            }
        }

        void Snap(bool open)
        {
            var s = _anim[clip];
            _anim.Play(clip);
            s.time = open ? s.length : 0f;
            _anim.Sample();
            if (!open) _anim.Stop();
            _open = open;
        }

        public void PlayManualAction()
        {
            if (!isActiveAndEnabled || _anim == null || trigger != Trigger.ParticleBurst) return;
            _repeatsLeft = 0;
            _lastFire = Time.time;
            // Rome (agent): one readable squeeze at manualSpeed (speed 2 = 0.38 s, too quick to see on a headset), the
            // particle watcher stays quiet for it, and the whole prop gives a squash so every press visibly lands
            var s = _anim[clip];
            s.speed = Mathf.Min(speed, manualSpeed);
            _seqEnd = Time.time + s.length / Mathf.Max(0.01f, s.speed) * 0.8f;
            if (_punchAt < 0f) _punchBase = transform.localScale;
            _punchAt = Time.time;
            _anim.Rewind(clip);
            Play(clip, "manual action");
        }

        void LateUpdate() // Rome (agent): the squash punch
        {
            if (_punchAt < 0f) return;
            float k = (Time.time - _punchAt) / 0.3f;
            if (k >= 1f) { transform.localScale = _punchBase; _punchAt = -1f; return; }
            float w = Mathf.Sin(k * Mathf.PI) * (1f - k);
            transform.localScale = Vector3.Scale(_punchBase, new Vector3(1f + 0.14f * w, 1f - 0.16f * w, 1f + 0.14f * w));
        }

        void Play(string c, string why)
        {
            if (string.IsNullOrEmpty(c) || _anim[c] == null) return;
            _anim.Stop();
            _anim.Play(c);
            Debug.Log("[VBS][ArtAnim] " + name + " " + c + " (" + why + ")");
        }

        void Update()
        {
            switch (trigger)
            {
                case Trigger.Beat:
                    if (_pending > 0f && Time.time >= _pending)
                    {
                        _pending = -1f;
                        _open = true;
                        Play(clip, "beat " + beat);
                    }
                    break;

                case Trigger.ObjectActive:
                    bool on = _watch != null && _watch.gameObject.activeSelf;
                    if (on && !_wasActive)
                    {
                        Play(clip, watchName + " on");
                        if (!string.IsNullOrEmpty(followClip) && _anim[followClip] != null) _anim.PlayQueued(followClip, QueueMode.CompleteOthers);
                    }
                    else if (!on && _wasActive) _anim.Stop();
                    _wasActive = on;
                    break;

                case Trigger.ParticleBurst:
                    if (_ps == null)
                    {
                        if (_searched) return;
                        _searched = true;
                        _ps = FindParticles();
                        if (_ps == null) { Debug.LogWarning("[VBS][ArtAnim] " + name + ": no ParticleSystem " + particleName); return; }
                        _lastCount = _ps.particleCount;
                    }
                    int n = _ps.particleCount;
                    if (n - _lastCount >= burstMin && Time.time - _lastFire >= minInterval && Time.time >= _seqEnd)
                    {
                        _lastFire = Time.time;
                        _seqEnd = Time.time + repeat * repeatGap - 0.05f;
                        _repeatsLeft = repeat - 1;
                        _nextRepeat = Time.time + repeatGap;
                        _anim[clip].speed = speed; // Rome (agent): a manual action may have slowed it
                        _anim.Rewind(clip);
                        Play(clip, particleName + " +" + (n - _lastCount) + (repeat > 1 ? ", x" + repeat : ""));
                    }
                    else if (_repeatsLeft > 0 && Time.time >= _nextRepeat)
                    {
                        _repeatsLeft--;
                        _nextRepeat += repeatGap;
                        _anim.Rewind(clip);
                        _anim.Play(clip);
                        ArtAnimLog.Clip(this, clip); // Rome (agent): VBS_ANIM_LOG=1 only
                    }
                    _lastCount = n;
                    break;
            }
        }

        ParticleSystem FindParticles()
        {
            var t = FindDeep(transform, particleName);
            if (t != null && t.TryGetComponent(out ParticleSystem own)) return own;
            // nearest by hierarchy: walk up and look below each ancestor
            for (var p = transform.parent; p != null; p = p.parent)
            {
                var hit = FindDeep(p, particleName);
                if (hit != null && hit.TryGetComponent(out ParticleSystem ps)) return ps;
            }
            foreach (var ps in FindObjectsByType<ParticleSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (ps.name == particleName) return ps;
            return null;
        }

        static Transform FindDeep(Transform root, string n)
        {
            if (root == null || string.IsNullOrEmpty(n)) return null;
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
