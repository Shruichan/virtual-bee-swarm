using System;
using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm.Pals
{
    /// <summary>
    /// The desktop path for Pals: a ghost right hand (PalHands) acts out each move so the real detectors run.
    /// Off the headset only. Keys: F5 swat, F6 wave, F7 high-five, F8 palm up/close, F10 waggle dance, F11 pet. Env
    /// VBS_PALS_DEMO=1 plays swat, pet, wave, high-five, palm bees, the dance once in pre after SIGN HERE is handed over (captures: set
    /// VBS_AUTOPILOT_PRE_IDLE so the autopilot waits ~20 s before signing). The autopilot itself never uses any of it.
    /// </summary>
    public class PalDemo : MonoBehaviour
    {
        Coroutine _act;
        bool _palmHeld, _demoDone, _honeyDone;
        BeeswaxController _bw;
        bool _honeyKey;

        /// <summary>Extra moves later items add (the honey frame): run after the built-in ones in the scripted demo.</summary>
        public static event Func<PalDemo, IEnumerator> DemoExtras;

        void Start() => _bw = FindAnyObjectByType<BeeswaxController>();

        void Update()
        {
            if (!PalHands.OffHeadset) return;
            if (GameInput.KeyDown(KeyCode.F5)) Run(Boop());
            if (GameInput.KeyDown(KeyCode.F11)) Run(Pet());
            if (GameInput.KeyDown(KeyCode.F6)) Run(Wave());
            if (GameInput.KeyDown(KeyCode.F7)) Run(HighFive());
            if (GameInput.KeyDown(KeyCode.F8)) Run(_palmHeld ? PalmClose() : PalmOpen());
            if (GameInput.KeyDown(KeyCode.F10) && PalBeeswax.Instance != null) PalBeeswax.Instance.AskDance();
            var hf = HoneyFrame.Instance;
            if (hf != null && hf.Ready && !_honeyDone && (Environment.GetEnvironmentVariable("VBS_HONEY_DEMO") == "1" || _honeyKey)) { _honeyDone = true; _honeyKey = false; Run(Honey()); }
            if (GameInput.KeyDown(KeyCode.F9)) { _honeyKey = true; _honeyDone = false; }
            if (!_demoDone && Environment.GetEnvironmentVariable("VBS_PALS_DEMO") == "1")
            {
                var intro = FindAnyObjectByType<IntroSequence>();
                if (intro != null && intro.SignHereReady) { _demoDone = true; Run(Demo()); }
            }
        }

        void Run(IEnumerator e)
        {
            if (_act != null) StopCoroutine(_act);
            _act = StartCoroutine(e);
        }

        IEnumerator Demo()
        {
            Debug.Log("[VBS][Pals] demo: swat, pet, wave, high-five, palm bees");
            yield return Boop();
            yield return Wait(1.6f);
            yield return Pet();
            yield return Wait(1.2f);
            yield return Wave();
            yield return Wait(1.0f);
            yield return HighFive();
            yield return Wait(0.8f);
            yield return PalmOpen();
            yield return Wait(5.5f);
            yield return PalmClose();
            yield return Wait(1.0f);
            if (PalBeeswax.Instance != null) PalBeeswax.Instance.AskDance();
            yield return Wait(7.2f);
            if (DemoExtras != null)
                foreach (Func<PalDemo, IEnumerator> f in DemoExtras.GetInvocationList()) yield return f(this);
            Debug.Log("[VBS][Pals] demo done");
        }

        // ------------------------------------------------------------------ moves
        Transform Head()
        {
            var del = BeeDelivery.Instance;
            if (del != null && del.Head != null) return del.Head;
            return Camera.main != null ? Camera.main.transform : null;
        }

        void Frame(out Vector3 eye, out Vector3 fwd, out Vector3 right)
        {
            var h = Head();
            eye = h != null ? h.position : Vector3.up * 1.5f;
            fwd = h != null ? h.forward : Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            right = Vector3.Cross(Vector3.up, fwd);
        }

        Vector3 Rest()
        {
            Frame(out var eye, out var fwd, out var right);
            return eye + fwd * 0.3f - Vector3.up * 0.55f + right * 0.2f;
        }

        public IEnumerator Boop()
        {
            var hands = PalHands.Instance;
            if (hands == null || _bw == null) yield break;
            Frame(out var eye, out _, out _);
            float r = PalBeeswax.Radius();
            Vector3 c = Centre();
            Vector3 dir = (c - eye).normalized;
            Vector3 normal = Vector3.ProjectOnPlane(Vector3.down, dir).normalized;
            Vector3 far = c - dir * (r + 0.22f), near = c - dir * (r * 0.4f);
            yield return Move(Rest(), far, dir, normal, 0.45f, tipAt: true);
            yield return Move(far, near, dir, normal, 0.22f, tipAt: true);
            yield return Wait(0.12f);
            yield return Move(near, far, dir, normal, 0.3f, tipAt: true);
            hands.GhostDown();
        }

        public IEnumerator Pet()
        {
            var hands = PalHands.Instance;
            if (hands == null || _bw == null) yield break;
            Frame(out var eye, out _, out var right);
            Vector3 inward = (Centre() - eye).normalized;
            Vector3 normal = Vector3.ProjectOnPlane(Vector3.down, inward).normalized;
            float radius = PalBeeswax.Radius() * 0.75f + 0.015f;
            Vector3 near = Centre() - inward * (radius * 0.65f);
            Vector3 far = Centre() - inward * (radius + 0.12f);
            yield return Move(Rest(), far, inward, normal, 0.45f, tipAt: true);
            yield return Move(far, near, inward, normal, 0.65f, tipAt: true);
            float start = Time.time;
            while (Time.time - start < 1f)
            {
                float phase = (Time.time - start) * Mathf.PI * 3f;
                float stroke = Mathf.Sin(phase) * Mathf.Min(radius * 0.4f, 0.05f);
                var tip = Centre() - inward * (radius * 0.65f) + right * stroke;
                hands.Ghost(tip - PalHands.GhostTip(Vector3.zero, normal, inward), normal, inward);
                yield return null;
            }
            hands.GhostDown();
        }

        public IEnumerator Wave()
        {
            var hands = PalHands.Instance;
            if (hands == null) yield break;
            Frame(out var eye, out var fwd, out var right);
            Vector3 at = eye + fwd * 0.45f + right * 0.2f + Vector3.up * 0.02f;
            yield return Move(Rest(), at, Vector3.up, fwd, 0.4f);
            float t0 = Time.time;
            while (Time.time - t0 < 2.3f)
            {
                float s = Mathf.Sin((Time.time - t0) * Mathf.PI * 2f * 1.7f);
                hands.Ghost(at + right * (0.075f * s), fwd, Quaternion.AngleAxis(-18f * s, fwd) * Vector3.up);
                yield return null;
            }
            yield return Move(at, Rest(), Vector3.up, fwd, 0.4f);
            hands.GhostDown();
        }

        public IEnumerator HighFive()
        {
            var hands = PalHands.Instance;
            if (hands == null) yield break;
            Frame(out var eye, out var fwd, out var right);
            Vector3 at = eye + fwd * 0.52f + right * 0.1f + Vector3.up * 0.03f;
            Vector3 facing = (at - eye).normalized;
            yield return Move(Rest(), at, Vector3.up, facing, 0.45f);
            float t0 = Time.time;
            var pb = PalBeeswax.Instance;
            int before = pb != null ? pb.HighFives : 0;
            while (Time.time - t0 < 3.2f && (pb == null || pb.HighFives == before)) { hands.Ghost(at, facing, Vector3.up); yield return null; }
            // the slap pushes the hand back a touch
            for (float k = 0f; k < 1f; k += Time.deltaTime / 0.25f) { hands.Ghost(at - facing * (0.03f * Mathf.Sin(k * Mathf.PI)), facing, Vector3.up); yield return null; }
            yield return Wait(0.6f, () => hands.Ghost(at, facing, Vector3.up));
            yield return Move(at, Rest(), Vector3.up, facing, 0.4f);
            hands.GhostDown();
        }

        public IEnumerator PalmOpen()
        {
            var hands = PalHands.Instance;
            if (hands == null) yield break;
            Frame(out var eye, out var fwd, out var right);
            Vector3 at = PalmSpot();
            yield return Move(Rest(), at, fwd, Vector3.up, 0.5f);
            _palmHeld = true;
            // held there (the coroutine ends; Update keeps it posed)
            StartCoroutine(HoldPalm(at, fwd));
        }

        Vector3 PalmSpot()
        {
            Frame(out var eye, out var fwd, out var right);
            return eye + fwd * 0.4f - Vector3.up * 0.3f + right * 0.06f;
        }

        IEnumerator HoldPalm(Vector3 at, Vector3 fwd)
        {
            var hands = PalHands.Instance;
            while (_palmHeld) { hands.Ghost(at + Vector3.up * (0.004f * Mathf.Sin(Time.time * 1.3f)), Vector3.up, fwd); yield return null; }
        }

        public IEnumerator PalmClose()
        {
            var hands = PalHands.Instance;
            if (hands == null) yield break;
            _palmHeld = false;
            Frame(out _, out var fwd, out _);
            Vector3 at = PalmSpot();
            yield return Wait(1.2f, () => hands.Ghost(at, Vector3.up, fwd, fist: true));
            yield return Move(at, Rest(), fwd, Vector3.up, 0.45f, fist: true);
            hands.GhostDown();
        }

        /// <summary>The honey frame with the ghost: pinch the top bar, hold it (Beeswax scrapes), then over the jar.</summary>
        public IEnumerator Honey()
        {
            var hf = HoneyFrame.Instance;
            var hands = PalHands.Instance;
            if (hf == null || hands == null) yield break;
            Debug.Log("[VBS][Pals] demo: honey frame");
            Frame(out _, out var fwd, out _);
            Vector3 n = Vector3.ProjectOnPlane(Vector3.down, fwd).normalized;
            Vector3 bar = hf.TopBar;
            yield return Move(Rest(), bar - fwd * 0.06f, fwd, n, 0.5f, tipAt: true);
            yield return Move(bar - fwd * 0.06f, bar, fwd, n, 0.25f, tipAt: true, pinch: true);
            yield return Wait(0.1f, () => Tip(bar, fwd, n));
            Vector3 grab = hf.Position - bar;
            float t0 = Time.time;
            while (Time.time - t0 < 12f && hf.Scraped < 0.7f && hf.Held) { Tip(bar, fwd, n); yield return null; }
            Vector3 over = hf.JarTop + Vector3.up * (0.12f + 0.095f) - grab;
            yield return Move(bar, over, fwd, n, 0.8f, tipAt: true, pinch: true);
            t0 = Time.time;
            while (Time.time - t0 < 5f && hf.Fill < 1f) { Tip(over, fwd, n); yield return null; }
            yield return Move(over, Rest(), fwd, n, 0.5f, tipAt: true);
            hands.GhostDown();
        }

        void Tip(Vector3 tip, Vector3 fingers, Vector3 normal) =>
            PalHands.Instance.Ghost(tip - PalHands.GhostTip(Vector3.zero, normal, fingers), normal, fingers, false, true);

        // ------------------------------------------------------------------ helpers
        Vector3 Centre() => _bw.BodyTransform != null ? _bw.BodyTransform.position : _bw.transform.position;

        /// <summary>Move the ghost from a to b (its palm centre, or its index tip when tipAt).</summary>
        public IEnumerator Move(Vector3 a, Vector3 b, Vector3 fingers, Vector3 normal, float seconds, bool tipAt = false, bool fist = false, bool pinch = false)
        {
            var hands = PalHands.Instance;
            float t0 = Time.time;
            while (true)
            {
                float k = Mathf.Clamp01((Time.time - t0) / Mathf.Max(0.01f, seconds));
                float e = k * k * (3f - 2f * k);
                Vector3 p = Vector3.Lerp(a, b, e);
                Vector3 n = normal.normalized, f = Vector3.ProjectOnPlane(fingers, n).normalized;
                if (tipAt) p -= PalHands.GhostTip(Vector3.zero, n, f);
                hands.Ghost(p, n, f, fist, pinch);
                if (k >= 1f) yield break;
                yield return null;
            }
        }

        public static IEnumerator Wait(float s, Action each = null)
        {
            float end = Time.time + s;
            while (Time.time < end) { each?.Invoke(); yield return null; }
        }
    }
}
