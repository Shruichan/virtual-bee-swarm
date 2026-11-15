using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Frame-time breakdown per beat, for development players only: on in a development Android build (the headset's
    /// logcat), or with VBS_PERF_PROBE=1 in a desktop development player. At every beat change (and every 20 s) it logs
    /// "[VBS][Perf] beat ...": average frame and PlayerLoop ms, the script and engine markers that cost the most,
    /// GC KB per frame, draw calls / triangles, and how many video players, particle systems and awake bodies run.
    /// Release builds have no per-script markers, so it stays off there.
    /// </summary>
    public sealed class PerfProbe : MonoBehaviour
    {
        sealed class Rec
        {
            public string name;
            public ProfilerRecorder r;
            public double sum, max;
            public bool parent;
        }

        static readonly string[] Parents =
        {
            "PlayerLoop", "BehaviourUpdate", "LateBehaviourUpdate", "FixedBehaviourUpdate", "Update.ScriptRunBehaviourUpdate",
            "PreLateUpdate.ScriptRunBehaviourLateUpdate", "FixedUpdate.ScriptRunBehaviourFixedUpdate", "Main Thread",
            "PostLateUpdate.FinishFrameRendering", "Gfx.WaitForPresentOnGfxThread", "Gfx.WaitForGfxCommandsFromMainThread",
            "Update.ScriptRunDelayedDynamicFrameRate", "CoroutinesDelayedCalls", "FixedUpdate.PhysicsFixedUpdate",
        };
        static readonly string[] Counters =
        {
            "GC Allocated In Frame", "Draw Calls Count", "Triangles Count", "SetPass Calls Count", "Batches Count",
        };

        readonly List<Rec> _recs = new List<Rec>(512);
        readonly HashSet<string> _have = new HashSet<string>();
        readonly Rec[] _ctr = new Rec[5];
        Rec _loop;
        int _frames;
        double _dtSum, _dtMax;
        float _since, _nextCount, _nextFind;
        string _beat = "boot";
        int _beatId = -1;
        int _videos, _particles, _bodies, _countSamples;
        double _videoSum, _particleSum, _bodySum;
        GameDirector _director;
        bool _countObjects;
        readonly bool _dump = Environment.GetEnvironmentVariable("VBS_PERF_PROBE_DUMP") == "1";
        readonly StringBuilder _dumpSb = new StringBuilder();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Debug.isDebugBuild || Application.isEditor) return;
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_PERF_PROBE"); } catch (Exception) { }
            bool on = Application.platform == RuntimePlatform.Android ? env != "0" : env == "1";
            if (!on) return;
            var go = new GameObject("[VBS PerfProbe]");
            DontDestroyOnLoad(go);
            go.AddComponent<PerfProbe>();
        }

        void Start()
        {
            bool android = Application.platform == RuntimePlatform.Android;
            _countObjects = !android; // FindObjectsByType every 2 s is a small hitch of its own on the headset
            string fps = null;
            try { fps = Environment.GetEnvironmentVariable("VBS_PERF_PROBE_FPS"); } catch (Exception) { }
            if (Environment.GetEnvironmentVariable("VBS_PERF_PROBE_PROFILER") == "1") UnityEngine.Profiling.Profiler.enabled = true;
            // per-script [Invoke] markers only report while the profiler collects (desktop test runs turn it on)
            _scripts = UnityEngine.Profiling.Profiler.enabled;
            if (int.TryParse(fps, out int f) && f > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = f; }
            for (int i = 0; i < Counters.Length; i++) _ctr[i] = Counter(i == 0 ? ProfilerCategory.Memory : ProfilerCategory.Render, Counters[i]);
            Scan();
            _since = Time.realtimeSinceStartup;
            Debug.Log("[VBS][Perf] probe on (" + _recs.Count + " markers, quest profile " + (QuestPerf.On ? "ON" : "off") + ")");
        }

        static Rec Counter(ProfilerCategory cat, string name)
        {
            var r = new Rec { name = name };
            try { r.r = ProfilerRecorder.StartNew(cat, name, 1); }
            catch (Exception) { return null; }
            return r;
        }

        Rec Track(ProfilerRecorderHandle h, string name)
        {
            var r = new Rec { name = name, parent = Array.IndexOf(Parents, name) >= 0 };
            try { r.r = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.Default); r.r.Start(); }
            catch (Exception) { return null; }
            _recs.Add(r);
            return r;
        }

        bool _scripts;

        bool Wanted(string n)
        {
            if (n.IndexOf("[Invoke]", StringComparison.Ordinal) >= 0 || n.IndexOf("[Coroutine]", StringComparison.Ordinal) >= 0) return _scripts;
            if (Array.IndexOf(Parents, n) >= 0) return true;
            return n.StartsWith("Update.", StringComparison.Ordinal) || n.StartsWith("PreUpdate.", StringComparison.Ordinal) ||
                   n.StartsWith("PreLateUpdate.", StringComparison.Ordinal) || n.StartsWith("PostLateUpdate.", StringComparison.Ordinal) ||
                   n.StartsWith("FixedUpdate.", StringComparison.Ordinal) || n.StartsWith("EarlyUpdate.", StringComparison.Ordinal) ||
                   n.StartsWith("Physics.", StringComparison.Ordinal) || n.StartsWith("Video", StringComparison.Ordinal) || n.StartsWith("VBS.", StringComparison.Ordinal) ||
                   n.StartsWith("ParticleSystem.", StringComparison.Ordinal) || n == "GC.Collect" || n == "Animators.Update";
        }

        void Scan()
        {
            var handles = new List<ProfilerRecorderHandle>(4096);
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                string n = d.Name;
                if (_dump && !string.IsNullOrEmpty(n) && (n.Contains("Update") || n.Contains("Beeswax"))) _dumpSb.Append(n).Append(" | ");
                if (string.IsNullOrEmpty(n) || d.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds || !Wanted(n) || !_have.Add(n)) continue;
                var r = Track(h, n);
                if (n == "PlayerLoop" && r != null) _loop = r;
            }
            if (_dump && _dumpSb.Length > 0) { Debug.Log("[VBS][Perf] markers: " + _dumpSb); _dumpSb.Clear(); }
        }

        void LateUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (_director == null && now >= _nextFind) { _nextFind = now + 1f; _director = FindAnyObjectByType<GameDirector>(); }
            int beat = _director != null ? (int)_director.Beat : -1;
            if (beat != _beatId || now - _since > 20f) { Report(); _beatId = beat; _beat = _director != null ? _director.Beat.ToString() : "boot"; }

            double dt = Time.unscaledDeltaTime * 1000.0;
            _frames++;
            _dtSum += dt;
            if (dt > _dtMax) _dtMax = dt;
            foreach (var r in _recs) Add(r, 1e-6);
            foreach (var r in _ctr) if (r != null) Add(r, 1.0);

            if (_countObjects && now >= _nextCount)
            {
                _nextCount = now + 2f;
                _videos = 0;
                foreach (var v in FindObjectsByType<UnityEngine.Video.VideoPlayer>(FindObjectsSortMode.None)) if (v.isPlaying) _videos++;
                _particles = 0;
                foreach (var p in FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)) if (p.IsAlive(false) && p.particleCount > 0) _particles++;
                _bodies = 0;
                foreach (var b in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)) if (!b.isKinematic && !b.IsSleeping()) _bodies++;
                _videoSum += _videos; _particleSum += _particles; _bodySum += _bodies; _countSamples++;
            }
        }

        static void Add(Rec r, double k)
        {
            if (!r.r.Valid) return;
            double v = r.r.LastValue * k;
            r.sum += v;
            if (v > r.max) r.max = v;
        }

        void Report()
        {
            if (_frames < 10) { _since = Time.realtimeSinceStartup; return; }
            var sb = new StringBuilder(1024);
            double f = _frames;
            sb.Append("[VBS][Perf] beat ").Append(_beat).Append(" frames ").Append(_frames)
              .Append(" dt ").Append((_dtSum / f).ToString("0.00")).Append(" ms (max ").Append(_dtMax.ToString("0.0")).Append(")");
            if (_loop != null) sb.Append(" loop ").Append((_loop.sum / f).ToString("0.00"));
            sb.Append(" gcKB ").Append(_ctr[0] != null ? (_ctr[0].sum / f / 1024.0).ToString("0.0") : "?");
            sb.Append(" draws ").Append(_ctr[1] != null ? (_ctr[1].sum / f).ToString("0") : "?");
            sb.Append(" tris ").Append(_ctr[2] != null ? (_ctr[2].sum / f / 1000.0).ToString("0") + "k" : "?");
            sb.Append(" setpass ").Append(_ctr[3] != null ? (_ctr[3].sum / f).ToString("0") : "?");
            if (_countSamples > 0)
                sb.Append(" videos ").Append((_videoSum / _countSamples).ToString("0.0"))
                  .Append(" particles ").Append((_particleSum / _countSamples).ToString("0"))
                  .Append(" bodies ").Append((_bodySum / _countSamples).ToString("0"));
            sb.Append(" | top");
            _recs.Sort((a, b) => b.sum.CompareTo(a.sum));
            int shown = 0;
            foreach (var r in _recs)
            {
                if (r.parent || r.sum <= 0) continue;
                sb.Append(' ').Append(r.name.Replace(" [Invoke]", "")).Append('=').Append((r.sum / f).ToString("0.000"))
                  .Append('/').Append(r.max.ToString("0.0"));
                if (++shown >= 20) break;
            }
            sb.Append(" | parents");
            foreach (var r in _recs)
                if (r.parent && r.sum > 0) sb.Append(' ').Append(r.name).Append('=').Append((r.sum / f).ToString("0.000"));
            Debug.Log(sb.ToString());
            foreach (var r in _recs) { r.sum = 0; r.max = 0; }
            foreach (var r in _ctr) if (r != null) { r.sum = 0; r.max = 0; }
            _frames = 0; _dtSum = 0; _dtMax = 0; _videoSum = _particleSum = _bodySum = 0; _countSamples = 0;
            _since = Time.realtimeSinceStartup;
            Scan(); // markers that appeared since (new scripts, new beats): only here, a scan walks every marker
        }

        void OnDestroy()
        {
            foreach (var r in _recs) r.r.Dispose();
            foreach (var r in _ctr) if (r != null) r.r.Dispose();
        }
    }
}
