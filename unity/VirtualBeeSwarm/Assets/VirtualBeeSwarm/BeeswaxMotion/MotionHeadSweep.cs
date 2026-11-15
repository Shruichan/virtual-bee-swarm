#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Desktop test rig only (never in the Quest build): with env VBS_MOTION_HEADSWEEP=1 the autopilot's head stops being a
    /// statue. Every 24 s it leans in at Beeswax (to 0.3 m), crouches, leans over the hive, looks away and sways, at 72 fps,
    /// so the comfort governor's head-dependent rules run in a player. The game itself never moves the player's view.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class MotionHeadSweep : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Environment.GetEnvironmentVariable("VBS_MOTION_HEADSWEEP") != "1") return;
            var go = new GameObject("MotionHeadSweep (test rig)");
            DontDestroyOnLoad(go);
            go.AddComponent<MotionHeadSweep>();
        }

        BeeswaxController _b;
        HiveBody _hive;
        Transform _head;
        Vector3 _basePos, _leanAt;
        Quaternion _baseRot;
        float _t0;
        bool _leaning;

        void Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 72;
            Debug.Log("[VBS][Motion] head sweep ON (test rig): lean in, crouch, over the hive, look away, sway; 72 fps");
        }

        static float Bell(float t, float a, float b)
        {
            // 0 -> 1 -> 0 over [a, b]: 37% in, 26% held, 37% out
            float u = Mathf.InverseLerp(a, b, t);
            if (u <= 0f || u >= 1f) return 0f;
            return u < 0.37f ? Mathf.SmoothStep(0f, 1f, u / 0.37f) : u > 0.63f ? Mathf.SmoothStep(0f, 1f, (1f - u) / 0.37f) : 1f;
        }

        void Update()
        {
            if (_b == null) { _b = FindAnyObjectByType<BeeswaxController>(); _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include); }
            if (_b == null) return;
            var h = _b.HeadTransform;
            if (h == null) return;
            if (h != _head) { _head = h; _basePos = h.position; _baseRot = h.rotation; _t0 = Time.time; }
            float t = (Time.time - _t0) % 24f;
            Vector3 bee = _b.BodyTransform != null ? _b.BodyTransform.position : _b.transform.position;
            Vector3 p = _basePos;
            Quaternion r = _baseRot;
            // lean in at him: to 0.3 m from where he was when the lean began (he should back off, never jump); a real
            // lean is at most 0.6 m, and it does not chase him
            bool leaning = t >= 4f && t < 8f;
            if (leaning && !_leaning) _leanAt = bee;
            _leaning = leaning;
            Vector3 toBee = _leanAt - _basePos;
            float d = toBee.magnitude;
            if (leaning && d > 0.31f) p += toBee / d * Mathf.Min(0.6f, d - 0.30f) * Bell(t, 4f, 8f);
            p += Vector3.down * 0.45f * Bell(t, 8f, 12f);
            if (_hive != null)
            {
                Vector3 over = _hive.transform.position;
                over.y = _basePos.y;
                p = Vector3.Lerp(p, over, Bell(t, 12f, 16f));
                r = Quaternion.Slerp(r, Quaternion.LookRotation(Vector3.down, _baseRot * Vector3.forward), 0.8f * Bell(t, 12f, 16f));
            }
            r = Quaternion.AngleAxis(100f * Bell(t, 16f, 20f), Vector3.up) * r;
            if (t > 20f) p += _baseRot * Vector3.right * (0.05f * Mathf.Sin(2f * Mathf.PI * 0.5f * (t - 20f)));
            _head.SetPositionAndRotation(p, r);
        }
    }
}
#endif
