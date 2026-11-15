using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm.Dressing
{
    /// <summary>
    /// One of dartiros' small rigged insects on the table (solitary bee, bumblebee, tiny wasp): plays its baked clips
    /// (Legacy Animation on the art root: Hover / Walk loops, Antenna Twitch and Blink masked to the head, Shake on the
    /// whole body) and flies short plans in the local space of its parent (the dressing root on the TableAnchor, so
    /// everything follows the table). A plan is a coroutine on this component: <see cref="Run"/> replaces the current
    /// one, so a bee can drop what it's doing and dash home. No physics, no colliders, nothing allocated per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class DressingCritter : MonoBehaviour
    {
        public const string HoverClip = "Hover", WalkClip = "Walk", TwitchClip = "Antenna Twitch", BlinkClip = "Blink", ShakeClip = "Shake";

        Animation _anim;
        Renderer[] _renderers = new Renderer[0];
        string _base;
        float _scale = 1f, _wobble, _wobbleRate = 1f, _phase;
        Vector3 _vel;
        bool _visible;

        /// <summary>The player's head: no flight comes closer than <see cref="FaceClear"/> (pushed out radially).</summary>
        public static Transform Head;
        public const float FaceClear = 0.45f;

        public bool Busy { get; private set; }
        public bool Visible => _visible;
        public float Scale => _scale;
        public string Label { get; set; } = "critter";

        public void Init(float scale, float wobble, float wobbleRate)
        {
            _scale = scale;
            _wobble = wobble;
            _wobbleRate = wobbleRate;
            _phase = Random.value * 10f;
            _anim = GetComponentInChildren<Animation>(true); // the art sits one level down, turned to face +Z
            _renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var c in GetComponentsInChildren<Collider>(true)) Destroy(c);
            if (_anim != null)
            {
                _anim.playAutomatically = false;
                _anim.cullingType = AnimationCullingType.BasedOnRenderers;
                Layer(HoverClip, 0, WrapMode.Loop, null);
                Layer(WalkClip, 0, WrapMode.Loop, null);
                Layer(TwitchClip, 1, WrapMode.Once, "Head");
                Layer(BlinkClip, 1, WrapMode.Once, "Head");
                Layer(ShakeClip, 2, WrapMode.Once, null);
            }
            Hide();
        }

        void Layer(string clip, int layer, WrapMode wrap, string bone)
        {
            var s = _anim[clip];
            if (s == null) return;
            s.layer = layer;
            s.wrapMode = wrap;
            if (string.IsNullOrEmpty(bone)) return;
            var t = FindDeep(transform, bone);
            if (t != null) s.AddMixingTransform(t, true);
        }

        // ------------------------------------------------------------------ plans
        public void Run(IEnumerator plan)
        {
            StopAllCoroutines();
            Busy = plan != null && isActiveAndEnabled;
            if (Busy) StartCoroutine(Wrap(plan));
        }

        IEnumerator Wrap(IEnumerator plan)
        {
            yield return plan;
            Busy = false;
        }

        public void Stop(bool hide)
        {
            StopAllCoroutines();
            Busy = false;
            if (hide) Hide();
        }

        /// <summary>Renderers off (the object stays active so a plan keeps running; the Animation culls itself).</summary>
        public void Hide()
        {
            _visible = false;
            for (int i = 0; i < _renderers.Length; i++) if (_renderers[i] != null) _renderers[i].enabled = false;
        }

        public void Show(Vector3 localPos, Quaternion localRot, float scaleK)
        {
            transform.localPosition = localPos;
            transform.localRotation = localRot;
            transform.localScale = Vector3.one * (_scale * scaleK);
            _vel = Vector3.zero;
            for (int i = 0; i < _renderers.Length; i++) if (_renderers[i] != null) _renderers[i].enabled = true;
            _visible = true;
        }

        // ------------------------------------------------------------------ clips
        public void Fly() => Base(HoverClip, 1f);
        public void Perch() => Base(WalkClip, 0f);   // legs planted, wings still: the walk pose, frozen
        public void Walk() => Base(WalkClip, 1f);

        void Base(string clip, float speed)
        {
            if (_anim == null) return;
            var s = _anim[clip];
            if (s == null) return;
            s.speed = speed;
            if (_base != clip || !_anim.IsPlaying(clip))
            {
                _base = clip;
                _anim.CrossFade(clip, 0.12f);
                if (speed <= 0f) s.time = 0.2f;
            }
        }

        public void Accent(string clip)
        {
            if (_anim == null || _anim[clip] == null) return;
            _anim.CrossFade(clip, 0.08f);
        }

        // ------------------------------------------------------------------ motion helpers (local to the parent)
        /// <summary>Cubic Bezier from where it is to `to`, entering along `outDir` and arriving along `inDir`, eased,
        /// with a bumbling wobble that fades at both ends. Faces where it's going.</summary>
        public IEnumerator FlyTo(Vector3 to, float speed, Vector3 outDir, Vector3 inDir, float minSeconds = 0.4f, float bulge = 0.4f)
        {
            Fly();
            Vector3 a = transform.localPosition;
            float dist = Vector3.Distance(a, to);
            float dur = Mathf.Max(minSeconds, dist / Mathf.Max(0.01f, speed));
            float h = Mathf.Min(dist * bulge, 0.35f);
            Vector3 b = a + outDir.normalized * h, c = to - inDir.normalized * h;
            Vector3 prev = a;
            for (float t = 0f; t < dur; t += Time.deltaTime)
            {
                float u = Ease(t / dur);
                Vector3 p = Bez(a, b, c, to, u);
                float fade = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / dur));
                p += Wobble(fade);
                Step(prev, p);
                prev = p;
                yield return null;
            }
            Step(prev, to);
        }

        /// <summary>A lazy figure-8 around `center` (lobes `rx` along local X, `rz` along Z), `seconds` for one 8.</summary>
        public IEnumerator FigureEight(Vector3 center, float rx, float rz, float seconds)
        {
            Fly();
            Vector3 prev = transform.localPosition;
            Vector3 start = prev;
            const float blend = 0.6f;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float a = t / seconds * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Sin(a) * rx, Mathf.Sin(a * 2f) * 0.02f, Mathf.Sin(a) * Mathf.Cos(a) * rz * 2f);
                p += Wobble(1f);
                if (t < blend) p = Vector3.Lerp(start, p, Ease(t / blend));
                Step(prev, p);
                prev = p;
                yield return null;
            }
        }

        /// <summary>Drop onto `spot` from just above it (0.35 s), legs down.</summary>
        public IEnumerator Land(Vector3 spot, float yawDeg)
        {
            Vector3 from = transform.localPosition;
            Quaternion r0 = transform.localRotation, r1 = Quaternion.Euler(0f, yawDeg, 0f);
            const float dur = 0.35f;
            for (float t = 0f; t < dur; t += Time.deltaTime)
            {
                float u = Ease(t / dur);
                transform.localPosition = Vector3.Lerp(from, spot, u);
                transform.localRotation = Quaternion.Slerp(r0, r1, u);
                yield return null;
            }
            transform.localPosition = spot;
            transform.localRotation = r1;
            _vel = Vector3.zero;
            Perch();
        }

        /// <summary>Walk a few cm along its heading on the surface it's on.</summary>
        public IEnumerator WalkBy(float meters, float seconds)
        {
            Walk();
            Vector3 a = transform.localPosition, b = a + transform.localRotation * Vector3.forward * meters;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                transform.localPosition = Vector3.Lerp(a, b, t / seconds);
                yield return null;
            }
            transform.localPosition = b;
            Perch();
        }

        public IEnumerator ScaleTo(float k, float seconds)
        {
            float a = transform.localScale.x, b = _scale * k;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float u = t / seconds;
                float s = Mathf.LerpUnclamped(a, b, b > a ? Back(u) : u * u);
                transform.localScale = Vector3.one * Mathf.Max(0f, s);
                yield return null;
            }
            transform.localScale = Vector3.one * b;
        }

        Vector3 Wobble(float k)
        {
            if (_wobble <= 0f) return Vector3.zero;
            float t = Time.time * _wobbleRate + _phase;
            return new Vector3(Mathf.Sin(t * 10.7f) * 0.6f, Mathf.Sin(t * 14.3f), Mathf.Sin(t * 8.9f) * 0.5f) * (_wobble * k);
        }

        void Step(Vector3 prev, Vector3 p)
        {
            var parent = transform.parent;
            if (Head != null && parent != null)
            {
                Vector3 w = parent.TransformPoint(p), d = w - Head.position;
                float m = d.magnitude;
                if (m < FaceClear) p = parent.InverseTransformPoint(Head.position + (m > 1e-4f ? d / m : Vector3.down) * FaceClear);
            }
            transform.localPosition = p;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            _vel = Vector3.Lerp(_vel, (p - prev) / dt, 1f - Mathf.Exp(-dt * 8f));
            var flat = new Vector3(_vel.x, 0f, _vel.z);
            if (flat.sqrMagnitude > 0.0009f)
            {
                // nose a touch up when climbing, down when diving; banks a little into turns
                float pitch = Mathf.Clamp(-_vel.y * 40f, -18f, 18f);
                var want = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
                transform.localRotation = Quaternion.Slerp(transform.localRotation, want, 1f - Mathf.Exp(-dt * 7f));
            }
        }

        static Vector3 Bez(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u)
        {
            float v = 1f - u;
            return v * v * v * a + 3f * v * v * u * b + 3f * v * u * u * c + u * u * u * d;
        }

        static float Ease(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }
        static float Back(float u) { u = Mathf.Clamp01(u) - 1f; return 1f + u * u * (2.7f * u + 1.7f); }

        public static Transform FindDeep(Transform root, string n)
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
