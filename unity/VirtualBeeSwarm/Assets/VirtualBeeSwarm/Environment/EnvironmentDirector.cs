using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// Environment pass (art pass 2, 2026-11-14): makes the play space look like a place.
    ///   - MeadowIsland: a meadow patch the hive lands on (turf, clover, pebbles, a honeycomb path, flower clusters).
    ///     It spreads out from under the hive when it lands, mirrors the window (sick, hot, burnt, full bloom) and
    ///     turns into the Moon's surface at T-0, then back.
    ///   - PortalDressing: flowering vines on the window ring, petals drifting out of it, and light shafts in the
    ///     colour of whatever the window shows.
    ///   - Atmosphere: pollen motes around the hive and out of the window.
    ///   - MoonSky: at T-0 a star dome fills the dimmed room and the Earth gets a soft blue glow; the grey Moon ball
    ///     in front of the window is hidden (the table is the Moon now). VBS_ENV_MOONBALL=1 keeps it.
    /// Not in the scene builder: it installs itself into any scene with a GameDirector + GameNet and only listens
    /// (BeatChanged, GroupResetDone, GameNet.SceneChanged, BeeDelivery.Landed). Art: Art/Environment (Blender:
    /// art/blender/scripts/environment.py), kit: Environment/Resources/VBS_EnvironmentKit.asset. VBS_ENV=0 turns it
    /// off (before/after captures). Logs "[VBS][Env] ...".
    /// Quest budget: 4 opaque draws (turf, vines, 4 instanced flower meshes), 3-5 small additive draws, ~11k tris in
    /// all (the island 3.9k), no lights, no shadows, no per-frame allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public class EnvironmentDirector : MonoBehaviour
    {
        public static EnvironmentDirector Instance { get; private set; }

        [SerializeField] float moodSeconds = 1.6f;
        [SerializeField] float moonInSeconds = 1.6f;
        [SerializeField] float moonOutSeconds = 1.2f;

        EnvironmentKit _kit;
        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        FutureWindow _window;
        BeeDelivery _delivery;
        Transform _anchor, _moonBall;

        MeadowIsland _island;
        PortalDressing _portal;
        Atmosphere _air;
        MoonSky _sky;

        EnvMood _from, _to, _mood;
        float _moodT = 1f;
        bool _moonWanted;
        float _moonK;
        bool _islandWanted;

        public EnvMood Mood => _mood;
        public float MoonK => _moonK;
        public bool IslandShown => _islandWanted;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Spawn();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode mode) => Spawn();

        static void Spawn()
        {
            if (Instance != null) return;
            if (System.Environment.GetEnvironmentVariable("VBS_ENV") == "0")
            {
                Debug.Log("[VBS][Env] off (VBS_ENV=0)");
                return;
            }
            var director = FindAnyObjectByType<GameDirector>(FindObjectsInactive.Include);
            var net = FindAnyObjectByType<GameNet>(FindObjectsInactive.Include);
            if (director == null || net == null) return; // not a game scene
            var kit = Resources.Load<EnvironmentKit>(EnvironmentKit.ResourceName);
            if (kit == null)
            {
                Debug.LogWarning("[VBS][Env] no Resources/" + EnvironmentKit.ResourceName + ": no environment");
                return;
            }
            var go = new GameObject("Environment (art pass 2)");
            var env = go.AddComponent<EnvironmentDirector>();
            env.Init(kit, director, net);
        }

        void Init(EnvironmentKit kit, GameDirector director, GameNet net)
        {
            Instance = this;
            _kit = kit;
            _director = director;
            _net = net;
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            _delivery = FindAnyObjectByType<BeeDelivery>(FindObjectsInactive.Include);
            _anchor = _hive != null ? _hive.transform.parent : null;
            var moonScene = _anchor != null ? _anchor.Find("MoonScene") : null;
            _moonBall = moonScene != null ? moonScene.Find("Moon") : null;
            EnvMood.TryFor("", out _mood);
            _from = _to = _mood;

            string parts = "";
            try
            {
                if (_anchor != null)
                {
                    _island = new MeadowIsland(kit, _anchor, _hive.transform);
                    _air = new Atmosphere(_island.Root);
                    parts += "island (" + _island.BloomCount + " flowers), motes";
                }
                if (_window != null)
                {
                    _portal = new PortalDressing(kit, _window);
                    parts += (parts.Length > 0 ? ", " : "") + "window (" + _portal.Summary + ")";
                }
                _sky = new MoonSky(kit, _anchor);
                parts += ", Moon sky";
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][Env] install failed, environment off: " + e);
                enabled = false;
                return;
            }

            _director.BeatChanged += OnBeat;
            _director.GroupResetDone += OnReset;
            _net.SceneChanged += OnScene;
            if (_delivery != null) _delivery.Landed += OnLanded;
            if (_director.Beat >= GameBeat.Meet) ShowIsland(true, "started mid-show");
            if (!string.IsNullOrEmpty(_net.CurrentScene)) OnScene(_net.CurrentScene);
            Debug.Log("[VBS][Env] installed: " + parts + (_delivery == null ? "; no BeeDelivery (island shows at meet)" : ""));
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_director != null) { _director.BeatChanged -= OnBeat; _director.GroupResetDone -= OnReset; }
            if (_net != null) _net.SceneChanged -= OnScene;
            if (_delivery != null) _delivery.Landed -= OnLanded;
            _island?.Restore();
            _sky?.Restore();
        }

        // ------------------------------------------------------------------ events
        void OnBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) { HideAll("pre"); return; }
            if (beat >= GameBeat.Meet && !_islandWanted) ShowIsland(false, "beat " + beat);
            if (beat != GameBeat.T0 && _moonWanted) SetMoon(false, "beat " + beat);
        }

        void OnReset() => HideAll("group reset");

        void OnScene(string scene)
        {
            if (scene == GameIds.Scene.Moon) { SetMoon(true, "window scene moon"); return; }
            if (_moonWanted) SetMoon(false, "window scene " + scene);
            if (EnvMood.TryFor(scene, out var m)) SetMood(m);
        }

        void OnLanded(Transform t)
        {
            if (_hive == null || t == null) return;
            if (t == _hive.transform || _hive.transform.IsChildOf(t)) ShowIsland(false, "hive landed");
        }

        // ------------------------------------------------------------------ state
        void ShowIsland(bool instant, string why)
        {
            if (_islandWanted) return;
            _islandWanted = true;
            _island?.Show(instant);
            Debug.Log("[VBS][Env] meadow island " + (instant ? "on" : "spreads out") + " (" + why + ")" +
                      (_island != null ? ", fit x" + _island.Fit.ToString("0.00") : ""));
        }

        void HideAll(string why)
        {
            bool was = _islandWanted || _moonWanted || _moonK > 0f;
            _islandWanted = false;
            _moonWanted = false;
            _moonK = 0f;
            EnvMood.TryFor("", out _mood);
            _from = _to = _mood;
            _moodT = 1f;
            _island?.Hide();
            _sky?.Restore();
            if (was) Debug.Log("[VBS][Env] reset (" + why + ")");
        }

        void SetMood(EnvMood m)
        {
            _from = _mood;
            _to = m;
            _moodT = 0f;
        }

        void SetMoon(bool on, string why)
        {
            if (_moonWanted == on) return;
            _moonWanted = on;
            if (on && !_islandWanted) ShowIsland(true, "moon");
            if (on) _sky?.Begin(_island?.HasMoonGround == true);
            Debug.Log("[VBS][Env] Moon stage " + (on ? "in" : "out") + " (" + why + ")");
        }

        void Update()
        {
            float dt = Time.deltaTime;
            // T-0 again while the window still shows the Moon (the beat was left before the crash-back): no SceneChanged
            // fires, so take the Moon ball coming on as the cue, like the flags do
            if (!_moonWanted && _moonBall != null && _director.Beat == GameBeat.T0 && _net.CurrentScene == GameIds.Scene.Moon &&
                _moonBall.gameObject.activeInHierarchy) SetMoon(true, "Moon ball on, window already moon");
            if (_moodT < 1f)
            {
                _moodT = Mathf.Min(1f, _moodT + dt / Mathf.Max(0.05f, moodSeconds));
                _mood = EnvMood.Lerp(_from, _to, Mathf.SmoothStep(0f, 1f, _moodT));
            }
            float prevMoon = _moonK;
            _moonK = Mathf.MoveTowards(_moonK, _moonWanted ? 1f : 0f, dt / Mathf.Max(0.05f, _moonWanted ? moonInSeconds : moonOutSeconds));
            if (prevMoon > 0f && _moonK <= 0f) _sky?.Restore();

            bool open = _window != null && _window.IsOpen;
            _island?.Tick(_mood, _moonK);
            _portal?.Tick(_mood, _moonK, open);
            _air?.Tick(_mood, _moonK, _islandWanted);
            _sky?.Tick(_moonK);
        }
    }
}
