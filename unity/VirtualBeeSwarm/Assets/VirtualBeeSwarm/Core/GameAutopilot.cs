using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Plays the whole show by itself: START, every station (optionally a wrong choice first), lifts the hive, balls +
    /// heats the hornet, "what if a fire starts?" / "put it out!", escape, T-0, map, then resets. F2 starts/stops it.
    /// Uses: rehearsing without a player, filming, and the smoke test (runOnStart + quitWhenDone in a standalone
    /// player, exit code 0 = pass). Logs "[VBSAutopilot] PASS/FAIL". Off by default.
    /// Voice (S8.2/S8.3): waits for GameNet.PreloadVoice before START, counts every line Beeswax starts (pre-rendered clip,
    /// caption only, or live stream) and logs "[VBSAutopilot] voice: ..." before the result; requireClips makes a
    /// caption-only line, or a clip that wasn't preloaded, a failure.
    /// </summary>
    public class GameAutopilot : MonoBehaviour
    {
        [SerializeField] GameDirector director;
        [SerializeField] GameNet net;
        [SerializeField] bool runOnStart;
        [SerializeField] bool makeMistakes = true;
        [SerializeField] bool forceHornet = true;
        [SerializeField] float timeScale = 1f;
        [Tooltip("Runs back to back (each ends with a reset to pre).")]
        [SerializeField] int runs = 1;
        [Tooltip("Odd runs force GameNet offline (tests both offline paths).")]
        [SerializeField] bool alternateOfflineModes;
        [Tooltip("Quit the app when done, exit code 0 = pass (standalone smoke test).")]
        [SerializeField] bool quitWhenDone;
        [Tooltip("Test the max-duration path: this station (0 smoker .. 3 heat) is left alone until the director's timeout " +
                 "makes Beeswax solve it. -1 = off.")]
        [SerializeField] int idleStation = -1;
        [Tooltip("Offline only: don't press F/P in the what-if beat; Beeswax's offline timers start and put out the fire.")]
        [SerializeField] bool whatIfHandsOff;
        [Tooltip("Online smoke test: wait up to this long (real seconds) for the Hive connection before run 1. 0 = don't wait.")]
        [SerializeField] float waitForOnlineSeconds;
        [Tooltip("Desktop smoke player: don't fail on exceptions thrown entirely outside VirtualBeeSwarm/Beeswax code (e.g. the " +
                 "Meta SDK's missing native plugins on Linux). They are counted and listed in the result line.")]
        [SerializeField] bool ignoreForeignExceptions;
        [Tooltip("Fail if Beeswax starts a line without a pre-rendered clip, or a clip PreloadVoice hadn't loaded (S8.3).")]
        [SerializeField] bool requireClips;
        [Tooltip("Wait up to this long (real seconds) for GameNet.PreloadVoice before run 1. 0 = don't wait.")]
        [SerializeField] float waitForVoicePreloadSeconds = 30f;
        [SerializeField] KeyCode toggleKey = KeyCode.F2;
        [SerializeField] float stepTimeoutSeconds = 90f;

        public bool Running { get; private set; }
        public bool Passed { get; private set; }
        public IReadOnlyList<string> Failures => _failures;

        readonly List<string> _failures = new List<string>();
        readonly HashSet<string> _ignored = new HashSet<string>();
        int _ignoredCount;
        int _voiceLines, _clipLines, _preloadedLines, _streamLines, _captionOnlyLines;
        readonly SortedSet<string> _captionOnly = new SortedSet<string>(StringComparer.Ordinal);
        readonly SortedSet<string> _notPreloaded = new SortedSet<string>(StringComparer.Ordinal);
        readonly SortedSet<string> _clipIds = new SortedSet<string>(StringComparer.Ordinal);
        Coroutine _routine;
        bool _failed;

        void Awake()
        {
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            if (net == null) net = FindAnyObjectByType<GameNet>();
        }

        void OnEnable()
        {
            Application.logMessageReceived += OnLog;
            if (net != null)
            {
                net.LineStarted += OnLine;
                net.VoiceLineStarted += OnVoiceLine;
            }
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
            if (net != null)
            {
                net.LineStarted -= OnLine;
                net.VoiceLineStarted -= OnVoiceLine;
            }
        }

        void OnVoiceLine(GameNet.VoiceLineInfo line)
        {
            if (!Running) return;
            _voiceLines++;
            if (line.stream) _streamLines++;
            else if (line.hasClip)
            {
                _clipLines++;
                _clipIds.Add(line.id);
                if (line.preloaded) _preloadedLines++;
                else _notPreloaded.Add(line.id);
            }
            else
            {
                _captionOnlyLines++;
                _captionOnly.Add(line.id.Length > 0 ? line.id : "\"" + line.caption + "\"");
            }
        }

        void OnLine(string caption)
        {
            if (Running && string.IsNullOrEmpty(caption)) Fail("Beeswax spoke a line with no caption");
        }

        void Start()
        {
            if (runOnStart) Begin();
        }

        void Update()
        {
            if (!GameInput.KeyDown(toggleKey)) return;
            if (Running) Stop("stopped (F2)");
            else Begin();
        }

        public void Begin()
        {
            if (Running || director == null) return;
            _failures.Clear();
            _ignored.Clear();
            _ignoredCount = 0;
            _voiceLines = _clipLines = _preloadedLines = _streamLines = _captionOnlyLines = 0;
            _captionOnly.Clear();
            _notPreloaded.Clear();
            _clipIds.Clear();
            _failed = false;
            Running = true;
            _routine = StartCoroutine(RunAll());
        }

        void Stop(string why)
        {
            if (_routine != null) StopCoroutine(_routine);
            Running = false;
            Time.timeScale = 1f;
            Debug.Log("[VBSAutopilot] " + why);
        }

        void OnLog(string condition, string stack, LogType type)
        {
            if (!Running || type != LogType.Exception) return;
            if (ignoreForeignExceptions && !FromOurCode(stack))
            {
                _ignoredCount++;
                if (_ignored.Count < 5) _ignored.Add(condition.Length > 120 ? condition.Substring(0, 120) : condition);
                return;
            }
            Fail("exception: " + condition);
        }

        /// <summary>True if any stack frame is in the VirtualBeeSwarm or Beeswax namespaces.</summary>
        static bool FromOurCode(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return true; // no stack: can't tell, count it
            foreach (var raw in stack.Split('\n'))
            {
                var line = raw.TrimStart();
                if (line.StartsWith("at ")) line = line.Substring(3);
                if (line.StartsWith("VirtualBeeSwarm.") || line.StartsWith("Beeswax.")) return true;
            }
            return false;
        }

        void Fail(string what)
        {
            _failed = true;
            _failures.Add(what);
            Debug.LogWarning("[VBSAutopilot] CHECK FAILED: " + what);
        }

        bool Check(bool ok, string what)
        {
            if (!ok) Fail(what);
            return ok;
        }

        IEnumerator RunAll()
        {
            Time.timeScale = Mathf.Max(0.1f, timeScale);
            if (quitWhenDone) AudioListener.volume = 0f;
            if (waitForOnlineSeconds > 0f && net != null && !net.ForceOffline)
            {
                float waitFrom = Time.realtimeSinceStartup;
                while (!net.Online && Time.realtimeSinceStartup - waitFrom < waitForOnlineSeconds) yield return null;
                Debug.Log("[VBSAutopilot] Hive " + (net.Online ? "online (" + net.HostName + ")" : "still offline") + " after " +
                          (Time.realtimeSinceStartup - waitFrom).ToString("0.0") + " s");
            }
            if (waitForVoicePreloadSeconds > 0f && net != null)
            {
                net.PreloadVoice();
                float preloadFrom = Time.realtimeSinceStartup;
                while (!net.VoicePreloadDone && Time.realtimeSinceStartup - preloadFrom < waitForVoicePreloadSeconds) yield return null;
                Debug.Log("[VBSAutopilot] voice preload " + (net.VoicePreloadDone ? "done (" + net.VoiceClipsPreloaded + " clips)" : "NOT done") +
                          " after " + (Time.realtimeSinceStartup - preloadFrom).ToString("0.0") + " s");
            }
            float started = Time.realtimeSinceStartup;
            for (int r = 0; r < Mathf.Max(1, runs) && !_failed; r++)
            {
                if (alternateOfflineModes && net != null) net.ForceOffline = (r % 2) == 1;
                Debug.Log("[VBSAutopilot] run " + (r + 1) + (net != null ? " (online=" + net.Online + ", forceOffline=" + net.ForceOffline + ")" : ""));
                if (director.Beat != GameBeat.Pre) director.ResetToPre();
                yield return null;
                yield return RunOnce();
            }
            Time.timeScale = 1f;
            Debug.Log("[VBSAutopilot] voice: " + _voiceLines + " line(s) started, " + _clipLines + " with a pre-rendered clip (" +
                      _clipIds.Count + " different, " + _preloadedLines + " preloaded), " + _streamLines + " streamed, " + _captionOnlyLines +
                      " caption-only" + (_captionOnly.Count > 0 ? ": " + string.Join(", ", _captionOnly) : "") +
                      (_notPreloaded.Count > 0 ? "; clips NOT preloaded: " + string.Join(", ", _notPreloaded) : ""));
            if (requireClips)
            {
                if (_voiceLines == 0) Fail("Beeswax said nothing");
                if (_captionOnly.Count > 0) Fail("caption-only lines (no clip in Resources/BeeswaxLines): " + string.Join(", ", _captionOnly));
                if (_notPreloaded.Count > 0) Fail("clips not preloaded during the intro: " + string.Join(", ", _notPreloaded));
            }
            Running = false;
            Passed = !_failed;
            Debug.Log("[VBSAutopilot] " + (Passed ? "PASS" : "FAIL") + " after " +
                      (Time.realtimeSinceStartup - started).ToString("0") + " s real" +
                      (_failures.Count > 0 ? ": " + string.Join(" | ", _failures) : "") +
                      (_ignoredCount > 0 ? " (ignored " + _ignoredCount + " exception(s) outside game code: " + string.Join(" | ", _ignored) + ")" : ""));
            if (quitWhenDone)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit(Passed ? 0 : 1);
#endif
            }
        }

        IEnumerator RunOnce()
        {
            var prevHornet = director.Hornet;
            if (forceHornet) director.Hornet = GameDirector.HornetMode.Always;

            director.Next("autopilot start");
            yield return WaitBeat(GameBeat.Smoker);
            yield return Station<SmokerStation>(GameIds.Choice.Swat);
            yield return WaitBeat(GameBeat.Feed);
            if (_failed) yield break;

            var hive = FindAnyObjectByType<HiveBody>();
            yield return WaitFor(() => hive != null && hive.LiftEnabled, "hive lift enabled");
            if (hive != null)
            {
                hive.BeginGrab(hive.transform.position);
                hive.UpdateGrab(hive.transform.position + Vector3.up * 0.05f);
                yield return null;
                hive.EndGrab();
            }
            yield return Station<FeedStation>(GameIds.Choice.StoreHoney);
            yield return WaitBeat(GameBeat.Wasps);
            yield return Station<WaspsStation>(GameIds.Choice.BiggerDoor);
            if (_failed) yield break;

            if (director.ShouldPlayHornet())
            {
                yield return WaitBeat(GameBeat.Hornet);
                var hornet = FindAnyObjectByType<HornetBeat>();
                float t0 = Time.realtimeSinceStartup;
                while (director.Beat == GameBeat.Hornet && Time.realtimeSinceStartup - t0 < stepTimeoutSeconds)
                {
                    if (hornet != null) hornet.AddHeat(0.01f);
                    yield return null;
                }
                Check(hornet != null && hornet.Cooked, "hornet cooked");
            }
            yield return WaitBeat(GameBeat.Heat);
            yield return Station<HeatStation>(GameIds.Choice.DeepBowl);
            if (_failed) yield break;

            yield return WaitBeat(GameBeat.WhatIf);
            var whatIf = FindAnyObjectByType<WhatIfBeat>();
            var embers = FindAnyObjectByType<EmbersFx>();
            yield return Seconds(2f);
            if (whatIf != null && whatIfHandsOff && net != null && !net.Online)
            {
                yield return WaitFor(() => whatIf.FireStarted, "offline auto fire");
                yield return WaitFor(() => embers != null && embers.Emitting, "embers emitting");
                yield return WaitFor(() => whatIf.FireOut, "offline auto put-out");
                Check(net.CurrentScene == GameIds.Scene.FireOut, "window scene = fire_out (is " + net.CurrentScene + ")");
            }
            else if (whatIf != null)
            {
                whatIf.LocalWhatIfFire();
                Check(net == null || net.CurrentScene == GameIds.Scene.WhatIfFire, "window scene = whatif_fire (is " + (net != null ? net.CurrentScene : "-") + ")");
                yield return WaitFor(() => embers != null && embers.Emitting, "embers emitting");
                yield return Seconds(3f);
                whatIf.LocalPutOut();
                Check(embers == null || !embers.Emitting, "embers stopped");
                Check(whatIf.FireOut, "fire out");
            }

            yield return WaitBeat(GameBeat.Escape);
            var hex = FindAnyObjectByType<HexagonPanel>();
            Check(hex != null && hex.LitCount == 4, "4 hexagons lit (" + (hex != null ? hex.LitCount : -1) + ")");
            var escape = FindAnyObjectByType<EscapeSequence>();
            yield return WaitFor(() => escape != null && escape.LastScore.Length > 0, "score card");
            if (escape != null) Debug.Log("[VBSAutopilot] " + escape.LastScore + " (time scale x" + Time.timeScale + ")");

            yield return WaitBeat(GameBeat.T0);
            yield return WaitBeat(GameBeat.Map);
            Check(director.SolvedStations == 4, "4 stations solved (" + director.SolvedStations + ")");
            yield return Seconds(2f);

            director.ResetToPre();
            yield return null;
            Check(director.Beat == GameBeat.Pre, "back in pre");
            Check(hex == null || hex.LitCount == 0, "hexagons reset");
            director.Hornet = prevHornet;
        }

        IEnumerator Station<T>(string wrong) where T : StationBase
        {
            var st = FindAnyObjectByType<T>();
            if (!Check(st != null, typeof(T).Name + " exists")) yield break;
            if ((int)st.Station == idleStation)
            {
                Debug.Log("[VBSAutopilot] leaving " + typeof(T).Name + " alone (timeout path)");
                yield return WaitFor(() => st.Solved, typeof(T).Name + " solved by the timeout");
                yield break;
            }
            yield return WaitFor(() => st.Choosing, typeof(T).Name + " offers choices");
            if (makeMistakes)
            {
                st.Choose(wrong);
                yield return WaitFor(() => st.Choosing || st.Solved, typeof(T).Name + " retry");
                Check(!st.Solved, typeof(T).Name + ": a wrong choice must not solve it");
            }
            st.Choose(GameIds.CorrectChoice[(int)st.Station]);
            yield return WaitFor(() => st.Solved, typeof(T).Name + " solved");
        }

        IEnumerator WaitBeat(GameBeat beat) => WaitFor(() => director.Beat == beat, "beat " + beat);

        IEnumerator WaitFor(Func<bool> cond, string what)
        {
            if (_failed) yield break;
            float start = Time.realtimeSinceStartup;
            while (!cond())
            {
                if (Time.realtimeSinceStartup - start > stepTimeoutSeconds)
                {
                    Fail("timed out waiting for " + what + " (beat " + director.Beat + ")");
                    yield break;
                }
                yield return null;
            }
        }

        static IEnumerator Seconds(float scaledSeconds)
        {
            float end = Time.time + scaledSeconds;
            while (Time.time < end) yield return null;
        }
    }
}
