using System;
using System.Collections;
using UnityEngine;
using VirtualBeeSwarm.Pals;

namespace VirtualBeeSwarm.SwarmTouch
{
    /// <summary>
    /// The desktop path for petting: the Pals ghost hand (palm down, fingers forward) sweeps through the hive's swarm:
    /// two quick passes, a slow stroke, then it rests in among the bees. Off the headset only. F11 runs it; env
    /// VBS_SWARMTOUCH_DEMO=1 runs a quick pass the first time the swarm is angry (the smoker: a buzz-up) and the
    /// whole stroke 1.5 s after it first turns calm. The autopilot never uses it.
    /// </summary>
    [DefaultExecutionOrder(200)] // LateUpdate after Pals/PalCam (100): the capture close-up below wins while a demo runs
    public class SwarmTouchDemo : MonoBehaviour
    {
        // captures only (env demo + a "pal" view): PalCamera is pulled in close on the hand while the demo runs
        bool _camOn;
        float _camOffAt = -1f;
        Vector3 _camFocus, _camBack, _camSide;
        Transform _palCam;
        Coroutine _act;
        bool _calmDone, _angryDone;
        float _calmSince = -1f;
        bool _env;
        SwarmSystem _swarm;
        HiveBody _hive;

        void Start()
        {
            _env = Environment.GetEnvironmentVariable("VBS_SWARMTOUCH_DEMO") == "1";
            _swarm = FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
        }

        void Update()
        {
            if (!PalHands.OffHeadset || PalHands.Instance == null || _swarm == null || _hive == null) return;
            if (GameInput.KeyDown(KeyCode.F11)) Run(Pet("F11"));
            if (!_env) return;
            // the hive only lands in the hook (delivery on), so: the smoker swarm while its angry, then once its calmed
            if (!_angryDone && _swarm.Mode == SwarmMode.Angry) { _angryDone = true; Run(Pet("demo angry", quick: true)); }
            if (_calmSince < 0f && _swarm.Mode == SwarmMode.Calm) _calmSince = Time.time;
            if (!_calmDone && _calmSince >= 0f && Time.time - _calmSince > 1.5f) { _calmDone = true; _nextTry = Time.time + 12f; Run(Pet("demo calm")); }
            // the line needs Beeswax free: stroke the calm swarm again (up to 3 more times) once he is, so a capture hears it
            var dir = SwarmTouchDirector.Instance;
            if (_calmDone && _tries < 3 && Time.time >= _nextTry && dir != null && !dir.Said && _swarm.Mode == SwarmMode.Calm &&
                GameDirector.Instance != null && dir.Free(GameDirector.Instance.Beat))
            {
                _tries++;
                _nextTry = Time.time + 12f;
                Run(Pet("demo calm again (" + _tries + ")"));
            }
        }

        int _tries;
        float _nextTry;

        void OnDisable()
        {
            // a stopped sweep must not leave the probe on (Begin would never early-out) or the ghost hand up
            SwarmTouchField.Probe = false;
            if (_act != null && PalHands.Instance != null) PalHands.Instance.GhostDown();
            _act = null;
            _camOn = false;
        }

        void LateUpdate()
        {
            if (!_env || !_camOn) return;
            if (_camOffAt >= 0f && Time.time > _camOffAt) { _camOn = false; _camOffAt = -1f; return; }
            if (_palCam == null) { var go = GameObject.Find("PalCamera"); if (go == null) return; _palCam = go.transform; }
            // 0.5 m back toward the player, a little to the side and above, looking at the sweep's middle
            Vector3 pos = _camFocus + _camBack * 0.5f + _camSide * 0.18f + Vector3.up * 0.12f;
            _palCam.SetPositionAndRotation(pos, Quaternion.LookRotation(_camFocus - pos, Vector3.up));
        }

        void Run(IEnumerator e)
        {
            if (_act != null) StopCoroutine(_act);
            _act = StartCoroutine(e);
        }

        void Frame(out Vector3 eye, out Vector3 fwd, out Vector3 right)
        {
            var cam = Camera.main;
            eye = cam != null ? cam.transform.position : Vector3.up * 1.5f;
            fwd = cam != null ? cam.transform.forward : Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            right = Vector3.Cross(Vector3.up, fwd);
        }

        IEnumerator Pet(string why, bool quick = false)
        {
            var hands = PalHands.Instance;
            Frame(out var eye, out var fwd, out var right);
            Vector3 c = _hive.SwarmCenter.position;
            float spread = Bloom.MeadowLayout.SwarmSpread;
            Vector3 toEye = eye - c;
            toEye.y = 0f;
            toEye = toEye.sqrMagnitude > 1e-4f ? toEye.normalized : -fwd;
            // where the bees on the right of the swarm, outside the hive, actually fly (averaged over 0.3 s)
            Vector3 side = Vector3.Cross(Vector3.up, -toEye).normalized;
            SwarmTouchField.ProbeFrom = c;
            SwarmTouchField.ProbeToward = side; // the right-hand side, outside the hive: in view, where they fly
            SwarmTouchField.ProbeSum = Vector3.zero;
            SwarmTouchField.ProbeN = 0;
            SwarmTouchField.ProbeRadius = 0f;
            SwarmTouchField.Probe = true;
            float probeEnd = Time.time + 0.3f;
            while (Time.time < probeEnd) yield return null;
            SwarmTouchField.Probe = false;
            int pn = SwarmTouchField.ProbeN;
            Vector3 mid = c + toEye * (0.27f * spread) + Vector3.up * 0.1f;
            if (pn > 0) // the ring itself: the samples' average direction and height, at their average distance
            {
                Vector3 avg = SwarmTouchField.ProbeSum / pn, flat = avg - c;
                flat.y = 0f;
                flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : side;
                mid = c + flat * (SwarmTouchField.ProbeRadius / pn);
                mid.y = avg.y;
            }
            var dir = SwarmTouchDirector.Instance;
            string box = "no hive box";
            if (dir != null && dir.TryHiveBox(out var hb))
            {
                box = "hive box " + hb.min.ToString("F2") + "-" + hb.max.ToString("F2");
            }
            box += ", centre " + c.ToString("F2") + ", " + pn + " bee samples";
            float half = 0.3f * spread;
            Vector3 sweep = toEye; // front to back along the ring
            _camOn = true;
            _camOffAt = -1f;
            _camFocus = mid;
            _camBack = toEye;
            _camSide = side;
            Vector3 down = Vector3.down, fingers = -toEye;
            Debug.Log("[VBS][SwarmTouch] " + why + ": ghost hand through the swarm at " + mid.ToString("F2") + " (spread " + spread.ToString("0.00") + ", " + box + ")");
            Vector3 rest = eye + fwd * 0.3f - Vector3.up * 0.55f + right * 0.2f;

            yield return Move(rest, mid - sweep * half, down, fingers, 0.6f);
            yield return Move(mid - sweep * half, mid + sweep * half, down, fingers, 0.55f); // a quick pass
            yield return Hold(mid + sweep * half, down, fingers, 0.5f);
            yield return Move(mid + sweep * half, mid - sweep * half, down, fingers, 0.55f); // and back
            if (!quick)
            {
                yield return Hold(mid - sweep * half, down, fingers, 0.7f);
                // a slow stroke: an arc through the middle, like running a hand through
                float t0 = Time.time, dur = 2.4f;
                while (Time.time - t0 < dur)
                {
                    float k = (Time.time - t0) / dur;
                    float a = Mathf.Lerp(-1f, 1f, k);
                    Vector3 p = mid + sweep * (half * a) + Vector3.up * (0.05f * Mathf.Sin(k * Mathf.PI * 2f)) + side * (0.06f * Mathf.Sin(k * Mathf.PI));
                    hands.Ghost(p, down, fingers);
                    yield return null;
                }
                yield return Move(mid + sweep * half, mid, down, fingers, 0.8f);
                yield return Hold(mid, down, fingers, 2.2f); // a still hand: they part round it and settle
            }
            yield return Move(hands.Hands[1].centre, rest, down, fingers, 0.5f);
            hands.GhostDown();
            _camOffAt = Time.time + 2f; // the settle, on camera
            Debug.Log("[VBS][SwarmTouch] " + why + " done");
        }

        IEnumerator Move(Vector3 a, Vector3 b, Vector3 normal, Vector3 fingers, float seconds)
        {
            var hands = PalHands.Instance;
            float t0 = Time.time;
            while (true)
            {
                float k = Mathf.Clamp01((Time.time - t0) / Mathf.Max(0.01f, seconds));
                float e = k * k * (3f - 2f * k);
                hands.Ghost(Vector3.Lerp(a, b, e), normal, fingers);
                if (k >= 1f) yield break;
                yield return null;
            }
        }

        IEnumerator Hold(Vector3 at, Vector3 normal, Vector3 fingers, float seconds)
        {
            var hands = PalHands.Instance;
            float end = Time.time + seconds;
            while (Time.time < end) { hands.Ghost(at, normal, fingers); yield return null; }
        }
    }
}
