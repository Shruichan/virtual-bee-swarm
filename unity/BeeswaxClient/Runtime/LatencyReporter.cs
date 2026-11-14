using System.Collections.Generic;
using UnityEngine;

namespace Beeswax
{
    /// <summary>
    /// Times hops on the Quest and reports them as latency{hop, ms, source:"quest"} for the Board.
    /// Static API for your own hops:
    ///   LatencyReporter.Mark("bees");  ...  LatencyReporter.Lap("bees", "quest.verdict_to_landed");
    /// Add the component to also report the standard Quest hops automatically:
    ///   quest.scan_to_photo, quest.photo_decode, quest.scan_to_verdict, quest.verdict_to_voice, quest.tts_to_audio.
    /// </summary>
    public class LatencyReporter : MonoBehaviour
    {
        [SerializeField] BeeswaxBus bus;
        [Tooltip("Optional: reports quest.photo_decode.")]
        [SerializeField] PolaroidFeed polaroid;
        [Tooltip("Optional: reports quest.verdict_to_voice and quest.tts_to_audio.")]
        [SerializeField] BeeswaxRoomVoice voice;
        [SerializeField] bool logToConsole;

        static readonly Dictionary<string, double> Marks = new Dictionary<string, double>();
        static bool s_log;

        static double Now => Time.realtimeSinceStartupAsDouble * 1000.0;

        /// <summary>Start (or restart) a timer.</summary>
        public static void Mark(string key) => Marks[key] = Now;

        /// <summary>ms since Mark(key), or -1 if not marked.</summary>
        public static double Elapsed(string key) => Marks.TryGetValue(key, out double t) ? Now - t : -1;

        /// <summary>Report ms since Mark(key) as `hop`. Returns false if the key was never marked.</summary>
        public static bool Lap(string key, string hop, bool clear = true)
        {
            double ms = Elapsed(key);
            if (ms < 0) return false;
            if (clear) Marks.Remove(key);
            Report(hop, ms);
            return true;
        }

        public static void Clear(string key) => Marks.Remove(key);

        /// <summary>Send one measured hop to the Hive (dropped when offline).</summary>
        public static void Report(string hop, double ms)
        {
            ms = System.Math.Round(ms, 1);
            if (s_log) Debug.Log($"[Beeswax] latency {hop}: {ms} ms");
            var b = BeeswaxBus.Instance;
            if (b != null) b.SendLatency(hop, ms);
        }

        // ---------------------------------------------------------------- automatic Quest hops
        BeeswaxBus _bound;

        void OnEnable() { s_log = logToConsole; Bind(); }
        void Start() => Bind();
        void OnDisable() => Unbind();

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnScanStarted += OnScan;
            b.OnScanPhoto += OnPhoto;
            b.OnScanVerdict += OnVerdict;
            b.OnTtsStart += OnTts;
            if (polaroid != null) polaroid.OnPhoto += OnDecoded;
            if (voice != null) voice.OnLineStarted += OnVoice;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnScanStarted -= OnScan;
            _bound.OnScanPhoto -= OnPhoto;
            _bound.OnScanVerdict -= OnVerdict;
            _bound.OnTtsStart -= OnTts;
            if (polaroid != null) polaroid.OnPhoto -= OnDecoded;
            if (voice != null) voice.OnLineStarted -= OnVoice;
            _bound = null;
        }

        void OnScan(ScanStarted s) => Mark("scan");
        void OnPhoto(ScanPhoto p, byte[] jpeg) => Lap("scan", "quest.scan_to_photo", clear: false);
        void OnDecoded(Texture2D tex, ScanPhoto p) => Report("quest.photo_decode", polaroid.LastDecodeMs);

        void OnVerdict(ScanVerdict v)
        {
            Lap("scan", "quest.scan_to_verdict");
            if (v.line_id.Length > 0) Mark("verdict");
        }

        void OnTts(TtsStart t) => Mark("tts:" + t.tts_id);

        void OnVoice(string caption)
        {
            string tts = voice.CurrentTtsId;
            if (tts.Length > 0) Lap("tts:" + tts, "quest.tts_to_audio");
            else Lap("verdict", "quest.verdict_to_voice");
        }
    }
}
