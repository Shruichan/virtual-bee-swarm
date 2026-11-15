using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using VirtualBeeSwarm.Title;

namespace VirtualBeeSwarm.Opening
{
    /// <summary>
    /// The slow opening (tuning opening.on, default on; env VBS_OPENING=0 turns it off). With deliveries on, pre starts
    /// calm: the title meadow grows in from the player's feet and the logo blooms open (Title/), then Beeswax buzzes out
    /// of the window with the SIGN HERE clipboard dangling under him, hovers beside the logo, looks around the room,
    /// waves, and floats over to hand it to the player (room_pre). The clipboard is ready about 9.5 s into pre.
    /// After signing he zips back to the window and into the hive, which the crew then carries in slower (6.5 s) with
    /// a pendulum wobble, so it drifts in instead of rushing at the player.
    /// Hooked into IntroSequence.BringClipboard; null (off, or no Beeswax) = one crew bee brings it as before.
    /// While the autopilot runs (smoke) the same path plays compressed (~3 s), so the start stays inside its 5 s wait.
    /// Logs "[VBS][Opening] ...".
    /// </summary>
    [DefaultExecutionOrder(10)] // after BeeDelivery (-20) has posed the flying hive this frame
    public class OpeningDirector : MonoBehaviour
    {
        public static OpeningDirector Instance { get; private set; }

        // full timings, seconds after IntroSequence hands over (~0.6 s into pre)
        const float AppearAt = 1.4f, FlyInMax = 2.6f, LookSeconds = 1.2f, WaveSeconds = 1.4f, FloatMax = 1.3f, GlideSeconds = 0.6f;
        const float HiveSeconds = 6.5f, WobbleDegrees = 5f, WobbleHz = 0.42f;

        BeeswaxController _bw;
        BeeswaxRig _rig;
        GameNet _net;
        GameDirector _director;
        HiveBody _hive;
        GameAutopilot _autopilot;
        Transform _clip, _waveBone;
        bool _carrying, _out, _subscribed;
        float _waveFrom = -1f, _waveUntil = -1f, _hiveFlyFrom = -1f;
        Vector3 _clipVel;
        Quaternion _swing = Quaternion.identity;
        Coroutine _exit;

        static bool? _on;
        public static bool On
        {
            get
            {
                if (_on == null) _on = ReadOn();
                return _on.Value;
            }
        }

        bool Fast => _autopilot != null && _autopilot.Running;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!On || FindAnyObjectByType<IntroSequence>() == null || FindAnyObjectByType<OpeningDirector>() != null) return;
            var gd = FindAnyObjectByType<GameDirector>();
            var go = new GameObject("Opening");
            if (gd != null) go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<OpeningDirector>();
        }

        void Awake()
        {
            Instance = this;
            _bw = FindAnyObjectByType<BeeswaxController>();
            _rig = _bw != null ? _bw.GetComponentInChildren<BeeswaxRig>(true) : null;
            _net = FindAnyObjectByType<GameNet>();
            _hive = FindAnyObjectByType<HiveBody>();
            _autopilot = FindAnyObjectByType<GameAutopilot>(FindObjectsInactive.Include);
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (_bw != null) IntroSequence.BringClipboard = Bring;
            SlowHive();
            Debug.Log("[VBS][Opening] installed (" + (_bw != null ? "Beeswax brings SIGN HERE" : "no Beeswax: crew brings it") + ")");
        }

        void OnEnable()
        {
            if (_director != null && !_subscribed) { _director.BeatChanged += HandleBeat; _subscribed = true; }
        }

        void OnDisable()
        {
            if (_subscribed && _director != null) _director.BeatChanged -= HandleBeat;
            _subscribed = false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            IntroSequence.BringClipboard = null;
        }

        /// <summary>The hive's festoon flight and IntroSequence's matching cue: 4.5 s -> 6.5 s (no edit in Delivery/).</summary>
        void SlowHive()
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var del = BeeDelivery.Instance != null ? BeeDelivery.Instance : FindAnyObjectByType<BeeDelivery>();
            var intro = FindAnyObjectByType<IntroSequence>();
            var a = typeof(BeeDelivery).GetField("festoonSeconds", F);
            var b = typeof(IntroSequence).GetField("hiveFlightSeconds", F);
            if (del != null && a != null) a.SetValue(del, HiveSeconds);
            if (intro != null && b != null) b.SetValue(intro, HiveSeconds);
            if (a == null || b == null) Debug.LogWarning("[VBS][Opening] could not slow the hive flight (field renamed?)");
        }

        // ------------------------------------------------------------------ pre: Beeswax brings SIGN HERE
        IEnumerator Bring(IntroSequence intro, SignHereClipboard clipboard)
        {
            var del = BeeDelivery.Instance;
            if (_bw == null || del == null || clipboard == null) yield break; // IntroSequence falls back to the crew
            bool fast = Fast;
            float t0 = Time.time;
            _clip = clipboard.transform;
            del.Release(_clip, hide: true);
            yield return Until(t0 + (fast ? 0.1f : AppearAt));

            // out of the window, the clipboard on a string under him
            Vector3 pane = del.PanePosition(), n = del.PaneNormal();
            _bw.EmergeNow(null);
            _bw.transform.position = pane + n * 0.06f;
            _bw.LookAtPlayer(true);
            _out = true;
            _clip.gameObject.SetActive(true);
            _clipVel = Vector3.zero;
            _swing = Quaternion.identity;
            _carrying = true;
            HangClip(0f, snap: true);
            Vector3 mark = MarkBesideLogo(clipboard);
            _bw.FlyTo(mark);
            Log("Beeswax buzzes out of the window with SIGN HERE" + (fast ? " (autopilot: short)" : ""));

            float until = Time.time + (fast ? 0.9f : FlyInMax);
            while (Time.time < until && !_bw.Arrived) yield return null;
            if (!fast)
            {
                yield return Seconds(0.2f);
                Rig("Look Around", "opening: sizing up the room");
                yield return Seconds(LookSeconds);
                Rig("Wag", "opening: wave");
                _waveFrom = Time.time;
                _waveUntil = Time.time + WaveSeconds;
                yield return Seconds(WaveSeconds);
            }

            // float over to the player and hand it across
            Pose park = intro.ClipboardParkPose();
            Transform head = del.Head;
            Vector3 eye = head != null ? head.position : park.position + Vector3.up * 0.2f;
            Vector3 to = park.position - eye;
            float r = Radius();
            Vector3 hold = eye + (to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward) * Mathf.Max(0.8f, 0.55f + 2.2f * r) + Vector3.up * (0.06f + r);
            _bw.FlyTo(hold);
            if (_net != null) _net.Say(GameLines.RoomPre);
            until = Time.time + (fast ? 0.6f : FloatMax);
            while (Time.time < until && !_bw.Arrived) yield return null;

            _carrying = false;
            park = intro.ClipboardParkPose();
            Vector3 fromP = _clip.position;
            Quaternion fromR = _clip.rotation;
            float glide = fast ? 0.3f : GlideSeconds, g0 = Time.time;
            while (Time.time - g0 < glide)
            {
                float k = Mathf.SmoothStep(0f, 1f, (Time.time - g0) / glide);
                _clip.SetPositionAndRotation(Vector3.Lerp(fromP, park.position, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.03f), Quaternion.Slerp(fromR, park.rotation, k));
                yield return null;
            }
            _clip.SetPositionAndRotation(park.position, park.rotation);
            var item = del.Register(_clip);
            if (item != null) item.parked = true;
            _bw.FlyTo(MarkBesideLogo(clipboard));
            Log("SIGN HERE handed over " + (Time.time - t0).ToString("0.0") + " s after the hand-over (" + (fast ? "short" : "full") + ")");
        }

        /// <summary>Beside the logo on the clipboard's side (the globe takes the other), else ahead-right of the player.</summary>
        Vector3 MarkBesideLogo(SignHereClipboard clipboard)
        {
            var del = BeeDelivery.Instance;
            Transform head = del != null ? del.Head : null;
            if (TitleDirector.LogoSpot(out var pos, out var right, out var away, out float width))
                return pos + right * (width * 0.5f + 0.16f) - away * 0.12f + Vector3.up * 0.04f;
            Vector3 eye = head != null ? head.position : transform.position + Vector3.up * 1.3f;
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            return eye + Quaternion.Euler(0f, 30f, 0f) * fwd * 1.0f - Vector3.up * 0.15f;
        }

        // ------------------------------------------------------------------ hook: back into the hive, then the slow hive
        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) { _carrying = false; _out = false; _waveUntil = -1f; return; }
            _carrying = false;
            _waveUntil = -1f;
            if (beat == GameBeat.Hook && prev == GameBeat.Pre && _out && _bw != null && !_bw.Inside)
            {
                if (_exit != null) StopCoroutine(_exit);
                _exit = StartCoroutine(BackIntoTheHive());
            }
            _out = false;
        }

        IEnumerator BackIntoTheHive()
        {
            var del = BeeDelivery.Instance;
            if (del == null || _hive == null) yield break;
            Vector3 pane = del.PanePosition(), n = del.PaneNormal();
            _bw.FlyTo(pane + n * 0.1f);
            Log("signed: Beeswax zips back to the window to ride in the hive");
            float until = Time.time + 1.2f;
            while (Time.time < until && _director != null && _director.Beat == GameBeat.Hook && Vector3.Distance(_bw.transform.position, pane) > 0.15f)
                yield return null;
            if (_director != null && _director.Beat == GameBeat.Hook && !_bw.Inside) _bw.HideInside(_hive.DoorPoint);
            _exit = null;
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (_carrying) HangClip(Time.deltaTime, snap: false);
            WobbleHive();
        }

        void LateUpdate()
        {
            // the wave: his right front leg, after the Animator has posed it
            if (_waveUntil < 0f || _bw == null) return;
            if (Time.time >= _waveUntil) { _waveUntil = -1f; return; }
            if (_waveBone == null) _waveBone = FindBone(_bw.transform, "Leg_Front_Upper.R");
            float u = (Time.time - _waveFrom) / (_waveUntil - _waveFrom);
            float env = Mathf.Clamp01(u / 0.15f) * Mathf.Clamp01((1f - u) / 0.15f);
            float a = env * (110f + 28f * Mathf.Sin((Time.time - _waveFrom) * Mathf.PI * 2f * 2.2f));
            if (_waveBone != null) _waveBone.rotation = Quaternion.AngleAxis(a, _bw.transform.forward) * _waveBone.rotation;
            else if (_bw.BodyTransform != null) _bw.BodyTransform.rotation = Quaternion.AngleAxis(env * 12f * Mathf.Sin(Time.time * 14f), _bw.transform.forward) * _bw.BodyTransform.rotation;
        }

        /// <summary>The clipboard dangles under Beeswax, facing the player, swinging a little with his flight.</summary>
        void HangClip(float dt, bool snap)
        {
            if (_clip == null || _bw == null) return;
            var del = BeeDelivery.Instance;
            Transform head = del != null ? del.Head : null;
            Vector3 b = _bw.transform.position;
            Vector3 face = head != null ? b - head.position : _bw.transform.forward;
            face.y = 0f;
            if (face.sqrMagnitude < 1e-4f) face = Vector3.forward;
            float h = SignHereClipboard.Height * Mathf.Abs(_clip.lossyScale.y);
            if (!snap && dt > 0f)
            {
                // a lagging pendulum: tilt against his acceleration
                Vector3 v = (b - _lastBw) / dt;
                Vector3 acc = (v - _clipVel) / dt;
                _clipVel = v;
                acc.y = 0f;
                Vector3 axis = Vector3.Cross(Vector3.up, acc);
                float ang = Mathf.Min(18f, acc.magnitude * 3f);
                Quaternion want = axis.sqrMagnitude > 1e-6f ? Quaternion.AngleAxis(-ang, axis.normalized) : Quaternion.identity;
                _swing = Quaternion.Slerp(_swing, want, 1f - Mathf.Exp(-5f * dt));
            }
            _lastBw = b;
            Vector3 top = b - Vector3.up * (Radius() * 0.8f + 0.02f);
            Quaternion rot = _swing * Quaternion.LookRotation(face.normalized, Vector3.up);
            _clip.SetPositionAndRotation(top - rot * Vector3.up * h, rot);
        }

        Vector3 _lastBw;

        /// <summary>His size (m, about half his length): BeeswaxBody measures the real mesh at any rig scale.</summary>
        float Radius()
        {
            var body = BeeswaxBody.Instance;
            float r = body != null && body.Active ? body.Reach : 0f;
            return r > 0.02f ? Mathf.Min(r, 0.4f) : 0.08f;
        }

        void WobbleHive()
        {
            var del = BeeDelivery.Instance;
            if (_hive == null || del == null || _director == null || _director.Beat != GameBeat.Hook) { _hiveFlyFrom = -1f; return; }
            var t = _hive.transform;
            if (!del.IsFlying(t)) { _hiveFlyFrom = -1f; return; }
            if (_hiveFlyFrom < 0f) _hiveFlyFrom = Time.time;
            float s = Time.time - _hiveFlyFrom;
            float env = Mathf.Clamp01(s / 0.8f) * Mathf.Clamp01((HiveSeconds - s) / 1.2f);
            if (env <= 0f) return;
            var item = del.Register(t);
            Vector3 top = t.position + Vector3.up * (item != null ? item.Top : 0.3f);
            float w = s * Mathf.PI * 2f * WobbleHz;
            Quaternion r = Quaternion.AngleAxis(WobbleDegrees * env * Mathf.Sin(w), t.forward) *
                           Quaternion.AngleAxis(WobbleDegrees * 0.6f * env * Mathf.Sin(w * 0.73f + 1.1f), t.right);
            t.SetPositionAndRotation(top + r * (t.position - top), r * t.rotation);
        }

        // ------------------------------------------------------------------ helpers
        void Rig(string clip, string why)
        {
            if (_rig != null && _rig.isActiveAndEnabled) _rig.Play(clip, why);
        }

        static Transform FindBone(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindBone(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        static IEnumerator Until(float t)
        {
            while (Time.time < t) yield return null;
        }

        static IEnumerator Seconds(float s) => Until(Time.time + s);

        static void Log(string msg) => Debug.Log("[VBS][Opening] " + msg);

        static bool ReadOn()
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_OPENING"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env)) return !(env == "0" || env == "off" || env == "false");
            bool on = true;
            var paths = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            paths.Add(Path.Combine(Application.streamingAssetsPath, "tuning.json"));
#endif
            paths.Add(Path.Combine(Application.persistentDataPath, "tuning.json"));
            var flat = new Dictionary<string, string>();
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    flat.Clear();
                    VirtualBeeSwarm.AR.ArTuning.FlatJson.Parse(File.ReadAllText(p), flat);
                    if (flat.TryGetValue("opening.on", out var v)) on = !(v == "0" || v == "off" || v == "false");
                }
                catch (Exception) { }
            }
            return on;
        }
    }
}
