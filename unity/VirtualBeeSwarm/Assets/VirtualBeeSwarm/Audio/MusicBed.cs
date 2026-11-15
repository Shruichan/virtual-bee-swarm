using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The music bed: Daniel's 60 s seamless Loop 02 ("Revised", Audio/Music/Resources/VBS_MusicLoop02.ogg, Vorbis,
    /// streamed) under the whole show at a low volume. It ducks smoothly under every Beeswax line (GameNet.IsSpeaking),
    /// goes silent while the player talks (push-to-talk) and in the hornet beat (the hum detector listens), and fades out
    /// for the T-0 Moon silence (window scene "moon") until the crash landing.
    ///
    /// Not in the scene builder: it spawns itself after a scene with a GameNet loads ("Music (bed)" at the scene root).
    /// tuning.json (same files as GameTuning): "music": { "on": true, "volume": 0.16 } (flat keys music.on /
    /// music.volume work too). Env VBS_MUSIC=0 turns it off. Logs "[VBS][Music] ...".
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public class MusicBed : MonoBehaviour
    {
        public const string ClipResource = "VBS_MusicLoop02";
        public const float DefaultVolume = 0.16f;

        /// <summary>Read from tuning.json at spawn.</summary>
        public static bool On { get; private set; } = true;
        public static float Volume { get; private set; } = DefaultVolume;
        public static string TuningFrom { get; private set; } = "defaults";

        [SerializeField] GameNet net;
        [SerializeField] GameDirector director;
        [Tooltip("Volume multiplier while Beeswax speaks.")]
        [SerializeField, Range(0f, 1f)] float duckUnderVoice = 0.3f;
        [Tooltip("While the player holds the mic: quiet, not off.")]
        [SerializeField, Range(0f, 1f)] float duckWhilePlayerTalks = 0.2f;
        [SerializeField] float duckDownSeconds = 0.25f;
        [SerializeField] float duckUpSeconds = 1.2f;
        [Tooltip("Fade for the Moon and for switching on/off.")]
        [SerializeField] float moonFadeSeconds = 1.5f;

        AudioSource _src;
        float _gain;          // 0..1 of Volume
        bool _moon;
        string _lastState = "playing";
        bool _wasDucked;

        /// <summary>Times the bed ducked under a Beeswax line.</summary>
        public static int Ducks { get; private set; }

        // ------------------------------------------------------------------ spawn
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
            if (FindAnyObjectByType<MusicBed>() != null) return;
            var net = FindAnyObjectByType<GameNet>();
            if (net == null) return; // not a game scene (rig template, tests without the show)
            ReadTuning();
            if (!On)
            {
                Debug.Log("[VBS][Music] off (music.on=false in " + TuningFrom + ")");
                return;
            }
            var clip = Resources.Load<AudioClip>(ClipResource);
            if (clip == null)
            {
                Debug.LogWarning("[VBS][Music] no Resources/" + ClipResource + ": no music");
                return;
            }
            var go = new GameObject("Music (bed)");
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;   // 2D bed, the same in both ears
            src.priority = 160;      // below the voice (128): the voice never loses a channel to it
            src.volume = 0f;
            src.bypassReverbZones = true;
            src.dopplerLevel = 0f;
            var bed = go.AddComponent<MusicBed>();
            bed.net = net;
            src.Play();
            Debug.Log("[VBS][Music] " + clip.name + " (" + clip.length.ToString("0.0", CultureInfo.InvariantCulture) + " s loop, " +
                      clip.loadType + ") at volume " + Volume.ToString("0.00", CultureInfo.InvariantCulture) + ", x" +
                      bed.duckUnderVoice.ToString("0.0#", CultureInfo.InvariantCulture) + " under Beeswax, off while talking / hornet / on the Moon; tuning from " + TuningFrom);
        }

        /// <summary>music.on / music.volume from the tuning.json files GameTuning reads (later files win).</summary>
        public static void ReadTuning()
        {
            var flat = new Dictionary<string, string>();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            Read(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat, from);
#endif
            Read(Path.Combine(Application.persistentDataPath, "tuning.json"), flat, from);
            TuningFrom = from.Count > 0 ? string.Join(" + ", from) : "defaults";
            On = true;
            Volume = DefaultVolume;
            if (flat.TryGetValue("music.on", out var on))
            {
                string v = on.Trim().ToLowerInvariant();
                On = !(v == "false" || v == "0" || v == "off" || v == "no");
            }
            if (flat.TryGetValue("music.volume", out var vol) && float.TryParse(vol, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                Volume = Mathf.Clamp01(f);
            if (Environment.GetEnvironmentVariable("VBS_MUSIC") == "0") { On = false; TuningFrom += " + VBS_MUSIC=0"; }
        }

        static void Read(string path, Dictionary<string, string> into, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                VirtualBeeSwarm.AR.ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
                from.Add(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][Music] tuning: could not read " + path + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------ runtime
        void Awake()
        {
            _src = GetComponent<AudioSource>();
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (director == null) director = FindAnyObjectByType<GameDirector>();
        }

        void OnEnable()
        {
            if (net != null) net.SceneChanged += OnScene;
        }

        void OnDisable()
        {
            if (net != null) net.SceneChanged -= OnScene;
        }

        void OnScene(string scene) => _moon = scene == GameIds.Scene.Moon;

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bool speaking = net != null && net.IsSpeaking;
            bool talking = net != null && net.Talking;
            bool waiting = net != null && net.AwaitingReply;
            bool hornet = director != null && director.Beat == GameBeat.Hornet;

            float target = 1f;
            string state = "playing";
            if (_moon) { target = 0f; state = "off (T-0 Moon)"; }
            // dartiros: quieter while the player asks, never silent (the bed is ~0.16 to begin with, so the mic barely hears it)
            else if (talking) { target = duckWhilePlayerTalks; state = "quiet (player talking)"; }
            else if (waiting) { target = duckUnderVoice; state = "quiet (waiting for Beeswax)"; }
            else if (hornet) { target = 0f; state = "off (hornet: the hum detector listens)"; }
            else if (speaking) { target = duckUnderVoice; state = "ducked"; }

            float seconds = target < _gain ? (_moon ? moonFadeSeconds : duckDownSeconds) : (_moon ? moonFadeSeconds : duckUpSeconds);
            _gain = Mathf.MoveTowards(_gain, target, dt / Mathf.Max(0.01f, seconds));
            _src.volume = Volume * _gain;

            // log the on/off changes (not every duck: there's one per line), with the duck count so far
            bool ducked = state == "ducked";
            if (ducked && !_wasDucked) Ducks++;
            _wasDucked = ducked;
            string logical = ducked ? "playing" : state;
            if (logical != _lastState)
            {
                Debug.Log("[VBS][Music] " + logical + " (ducked under " + Ducks + " Beeswax line(s) so far)");
                _lastState = logical;
            }
        }
    }
}
