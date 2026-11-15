using System;
using System.Collections.Generic;
using UnityEngine;

namespace Beeswax
{
    /// <summary>
    /// Beeswax's voice in the room, from his body (put this on Beeswax with a spatialized AudioSource).
    /// Plays one line at a time from a queue:
    ///  * pre-rendered clips by line_id: the serialized list first, then Resources/BeeswaxLines/&lt;line_id&gt;;
    ///    a missing clip still shows its caption for a reading-time estimate, so the beat never stalls;
    ///  * live lines: tts.start + PCM16 chunks + tts.end from the bus, streamed through a PCMReaderCallback clip.
    ///    A stream that ends or stalls before any audio plays falls back to its caption.
    /// Listens to say{channel:"room"}, scan.verdict line_id (optional), tts.*, and group.reset.
    /// Exposes Amplitude (0..1) for mouth flaps and OnLineStarted/OnLineEnded(caption) for captions.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class BeeswaxRoomVoice : MonoBehaviour
    {
        [Serializable]
        public class LineClip
        {
            public string id;
            public AudioClip clip;
            [TextArea] public string caption;
        }

        [SerializeField] BeeswaxBus bus;
        [SerializeField] AudioSource source;
        [SerializeField] List<LineClip> lines = new List<LineClip>();
        [Tooltip("Clips not in the list load from Resources/<folder>/<line_id>.")]
        [SerializeField] string resourcesFolder = "BeeswaxLines";
        [Tooltip("Play scan.verdict line_id here. Turn off if your beat machine plays verdict lines itself.")]
        [SerializeField] bool playVerdictLines = true;
        [Tooltip("On Awake: 3D (spatialBlend 1), spatialized if a spatializer plugin is set, no doppler.")]
        [SerializeField] bool configureSpatialSource = true;

        [Header("Live lines")]
        [SerializeField] float prebufferSeconds = 0.2f;
        [Tooltip("A live line with no new audio for this long is treated as finished.")]
        [SerializeField] float streamStallSeconds = 4f;

        [Header("Queue")]
        [SerializeField] int maxQueued = 4;
        [Tooltip("Lines that waited longer than this are skipped (stale).")]
        [SerializeField] float maxQueueWaitSeconds = 15f;
        [Tooltip("The same line_id from the bus within this window plays once (say + scan.verdict).")]
        [SerializeField] float dedupSeconds = 3f;

        [Header("Mouth")]
        [SerializeField] float amplitudeGain = 4f;
        [SerializeField] float attackPerSecond = 30f;
        [SerializeField] float releasePerSecond = 8f;

        public event Action<string> OnLineStarted; // caption
        public event Action<string> OnLineEnded;   // caption

        public bool IsSpeaking => _current != null;
        public bool IsSpeakingDeviceFallback => _current?.deviceSpeech == true;
        public Func<string, bool> CanSpeakAskCaption { get; set; }
        /// <summary>Smoothed loudness of what Beeswax is saying now, 0..1.</summary>
        public float Amplitude { get; private set; }
        public string CurrentCaption => _current?.caption ?? "";
        public string CurrentLineId => _current?.lineId ?? "";
        public string CurrentTtsId => _current?.ttsId ?? "";
        public int QueuedCount => _queue.Count;
        /// <summary>Skip unsolicited Ask replies from an older Hive that ignored the wake-phrase flag.</summary>
        public bool SuppressAskReplies { get; set; }

        const int EnvelopeRate = 60; // amplitude frames per second for pre-rendered clips

        sealed class Line
        {
            public string lineId = "", ttsId = "", caption = "";
            public AudioClip clip;
            public float[] envelope;
            public float queuedAt, startedAt, captionOnlySeconds;
            public bool started;
            public bool deviceSpeech, allowDeviceSpeech;
            // live line
            public PcmRing pcm;
            public int sampleRate;
            public bool ended;
            public float lastChunkAt, drainedSince = -1f, countChangedAt;
            public int lastCount = -1;
            public int carry = -1;
            public volatile float rms;
            public volatile int readBlock; // samples Unity pulls per callback (its stream read-ahead)
            public bool IsStream => pcm != null;
        }

        readonly LinkedList<Line> _queue = new LinkedList<Line>();
        readonly Dictionary<string, Line> _streams = new Dictionary<string, Line>();
        readonly Dictionary<string, string> _ttsCaptions = new Dictionary<string, string>();
        readonly Dictionary<string, float> _recent = new Dictionary<string, float>();
        readonly Dictionary<string, float> _suppressUntil = new Dictionary<string, float>();
        readonly Dictionary<string, AudioClip> _clipCache = new Dictionary<string, AudioClip>();
        readonly Dictionary<AudioClip, float[]> _envelopes = new Dictionary<AudioClip, float[]>();
        readonly float[] _outBuf = new float[256];
        readonly byte[] _pair = new byte[2];
        readonly DeviceSpeech _deviceSpeech = new DeviceSpeech();
        Line _current;
        BeeswaxBus _bound;
        float _dialogueGain = 1f;

        // ---------------------------------------------------------------- lifecycle
        void Reset() { source = GetComponent<AudioSource>(); }

        void Awake()
        {
            if (source == null) source = GetComponent<AudioSource>();
            _deviceSpeech.Warm();
            foreach (var l in lines)
                if (l != null && !string.IsNullOrEmpty(l.id) && l.clip != null) _clipCache[l.id] = l.clip;
            if (configureSpatialSource)
            {
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                if (!string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName())) source.spatialize = true;
                if (Application.platform == RuntimePlatform.Android)
                {
                    source.spatialBlend = 0.8f;
                    source.minDistance = Mathf.Max(source.minDistance, 1.8f);
                    _dialogueGain = 1.41f; // +3 dB on quiet blocks, also on streamed speech
                }
            }
        }

        void OnAudioFilterRead(float[] data, int channels) => ApplyDialogueGain(data, _dialogueGain);

        static void ApplyDialogueGain(float[] data, float gain)
        {
            if (gain <= 1f) return;
            float peak = 0f;
            foreach (float sample in data) peak = Math.Max(peak, Math.Abs(sample));
            // Keep the shape of the whole block instead of clipping each loud sample.
            if (peak > 0f) gain = Math.Min(gain, 0.98f / peak);
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
        }

        void OnEnable() => Bind();
        void Start() => Bind();
        void OnDisable() { Unbind(); StopAll(); }
        void OnDestroy() => _deviceSpeech.Dispose();
        void OnApplicationPause(bool paused)
        {
            if (paused && IsSpeakingDeviceFallback) StopAll();
        }

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnSay += HandleSay;
            b.OnScanVerdict += HandleVerdict;
            b.OnTtsStart += HandleTtsStart;
            b.OnTtsChunk += HandleTtsChunk;
            b.OnTtsEnd += HandleTtsEnd;
            b.OnGroupReset += HandleGroupReset;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnSay -= HandleSay;
            _bound.OnScanVerdict -= HandleVerdict;
            _bound.OnTtsStart -= HandleTtsStart;
            _bound.OnTtsChunk -= HandleTtsChunk;
            _bound.OnTtsEnd -= HandleTtsEnd;
            _bound.OnGroupReset -= HandleGroupReset;
            _bound = null;
        }

        // ---------------------------------------------------------------- public API
        /// <summary>Queue a pre-rendered line. Returns false if no clip was found (its caption still shows).</summary>
        public bool Play(string lineId, string caption = null)
        {
            var clip = FindClip(lineId);
            if (clip == null) Debug.LogWarning($"[Beeswax] no clip for line '{lineId}'; showing the caption only.");
            Enqueue(new Line { lineId = lineId ?? "", clip = clip, caption = caption ?? CaptionFor(lineId) });
            return clip != null;
        }

        /// <summary>Queue a caption with no audio (shown for a reading-time estimate, like a line whose clip is missing).</summary>
        public void PlayCaption(string caption)
        {
            if (string.IsNullOrEmpty(caption)) return;
            Enqueue(new Line { caption = caption });
        }

        public void PlayClip(AudioClip clip, string caption = "")
        {
            if (clip == null) return;
            Enqueue(new Line { lineId = clip.name, clip = clip, caption = caption ?? "" });
        }

        /// <summary>Stop the current line and play the next one.</summary>
        public void SkipCurrent()
        {
            if (_current != null) Finish();
        }

        public void StopAll()
        {
            _queue.Clear();
            _streams.Clear();
            _ttsCaptions.Clear();
            if (_current != null) Finish();
            Amplitude = 0f;
        }

        public bool HasClip(string lineId) => FindClip(lineId) != null;

        /// <summary>
        /// Skip the next say/verdict for lineId from the bus if it arrives within `seconds`. For lines you played
        /// locally because the Hive was unreachable: a queued choice.made reaching the Hive later must not repeat it.
        /// </summary>
        public void SuppressNextFromBus(string lineId, float seconds = 15f)
        {
            if (!string.IsNullOrEmpty(lineId)) _suppressUntil[lineId] = Time.realtimeSinceStartup + seconds;
        }

        // ---------------------------------------------------------------- bus handlers
        void HandleSay(Say s)
        {
            if (s.channel != Channel.Room) return; // ear lines play only in the glasses
            if (s.line_id == "room_live_filler") return; // older Hives still send this before Ask replies
            if (SuppressAskReplies && (s.line_id.Length == 0 || s.line_id.StartsWith("ask_", StringComparison.Ordinal) ||
                s.line_id.StartsWith("room_ack", StringComparison.Ordinal) ||
                s.line_id.StartsWith("room_whatif_", StringComparison.Ordinal))) return;
            if (s.line_id.Length > 0) PlayFromBus(s.line_id, s.caption);
            else if (s.tts_id.Length > 0) { if (s.caption.Length > 0) _ttsCaptions[s.tts_id] = s.caption; }
            // Neither a clip nor a stream: the Hive's live voice is down, so Beeswax "says" the caption (reading time).
            else if (s.caption.Length > 0) Enqueue(new Line { caption = s.caption, allowDeviceSpeech = true });
        }

        void HandleVerdict(ScanVerdict v)
        {
            if (playVerdictLines && v.line_id.Length > 0) PlayFromBus(v.line_id, v.caption);
            if (v.tts_id.Length > 0 && v.caption.Length > 0) _ttsCaptions[v.tts_id] = v.caption;
        }

        void HandleTtsStart(TtsStart t)
        {
            if (SuppressAskReplies || t.tts_id.Length == 0 || _streams.ContainsKey(t.tts_id)) return;
            string caption = t.caption.Length > 0 ? t.caption : (_ttsCaptions.TryGetValue(t.tts_id, out var c) ? c : "");
            var line = new Line
            {
                ttsId = t.tts_id,
                caption = caption,
                sampleRate = t.sample_rate,
                pcm = new PcmRing(t.sample_rate * 5),
                lastChunkAt = Time.realtimeSinceStartup,
            };
            _streams[t.tts_id] = line;
            Enqueue(line);
        }

        void HandleTtsChunk(string ttsId, byte[] bytes)
        {
            if (!_streams.TryGetValue(ttsId ?? "", out var line) || line.ended || bytes.Length == 0) return;
            int offset = 0;
            if (line.carry >= 0)
            {
                _pair[0] = (byte)line.carry;
                _pair[1] = bytes[0];
                line.pcm.WritePcm16(_pair, 0, 2);
                line.carry = -1;
                offset = 1;
            }
            int even = (bytes.Length - offset) & ~1;
            line.pcm.WritePcm16(bytes, offset, even);
            if (offset + even < bytes.Length) line.carry = bytes[bytes.Length - 1];
            line.lastChunkAt = Time.realtimeSinceStartup;
        }

        void HandleTtsEnd(TtsEnd e)
        {
            if (_streams.TryGetValue(e.tts_id, out var line)) line.ended = true;
            _ttsCaptions.Remove(e.tts_id);
        }

        void HandleGroupReset(GroupReset _)
        {
            SuppressAskReplies = false;
            _suppressUntil.Clear();
            StopAll();
        }

        void PlayFromBus(string lineId, string caption)
        {
            float now = Time.realtimeSinceStartup;
            if (_suppressUntil.TryGetValue(lineId, out float until))
            {
                _suppressUntil.Remove(lineId);
                if (now < until) return;
            }
            if (_recent.TryGetValue(lineId, out float at) && now - at < dedupSeconds) return;
            _recent[lineId] = now;
            Play(lineId, caption.Length > 0 ? caption : null);
        }

        // ---------------------------------------------------------------- queue
        void Enqueue(Line line)
        {
            line.queuedAt = Time.realtimeSinceStartup;
            while (_queue.Count >= Mathf.Max(1, maxQueued))
            {
                var dropped = _queue.First.Value;
                _queue.RemoveFirst();
                if (dropped.IsStream) _streams.Remove(dropped.ttsId);
                Debug.LogWarning($"[Beeswax] voice queue full; dropped '{Describe(dropped)}'.");
            }
            _queue.AddLast(line);
        }

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (_current != null && IsFinished(_current, now))
            {
                if (!_current.started && !string.IsNullOrWhiteSpace(_current.caption))
                    ShowStreamCaption(_current, now);
                else Finish();
            }
            if (_current == null) StartNext(now);
            if (_current != null && _current.IsStream && !_current.started && StreamReady(_current, now)) BeginStream(_current, now);
            UpdateAmplitude();
        }

        void StartNext(float now)
        {
            while (_queue.Count > 0)
            {
                var line = _queue.First.Value;
                _queue.RemoveFirst();
                if (now - line.queuedAt > maxQueueWaitSeconds)
                {
                    if (line.IsStream) _streams.Remove(line.ttsId);
                    Debug.LogWarning($"[Beeswax] skipped stale line '{Describe(line)}'.");
                    continue;
                }
                _current = line;
                if (line.IsStream) return; // starts once enough audio is buffered
                BeginLocalLine(line, now);
                return;
            }
        }

        void BeginLocalLine(Line line, float now)
        {
            line.started = true;
            line.startedAt = now;
            if (line.clip != null)
            {
                line.envelope = EnvelopeOf(line.clip);
                source.clip = line.clip;
                source.Play();
            }
            else
            {
                int words = line.caption.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                line.captionOnlySeconds = Mathf.Clamp(words * 0.38f, 1.5f, 7f);
                if (line.allowDeviceSpeech && CanSpeakAskCaption?.Invoke(line.caption) == true)
                    line.deviceSpeech = _deviceSpeech.Speak(line.caption);
            }
            RaiseLine(OnLineStarted, line.caption);
        }

        void ShowStreamCaption(Line line, float now)
        {
            _streams.Remove(line.ttsId);
            _ttsCaptions.Remove(line.ttsId);
            line.ttsId = "";
            line.pcm = null;
            line.allowDeviceSpeech = true;
            BeginLocalLine(line, now);
        }

        // Start once the prebuffer is full, or with what we have if the line ended or the feed paused.
        bool StreamReady(Line l, float now)
        {
            int n = l.pcm.Count;
            return n > 0 && (n >= l.sampleRate * prebufferSeconds || l.ended || now - l.lastChunkAt > 0.5f);
        }

        void BeginStream(Line line, float now)
        {
            line.started = true;
            line.startedAt = now;
            // A long streamed clip; it plays whatever the ring buffer holds and silence when it runs dry.
            line.clip = AudioClip.Create("beeswax_tts_" + line.ttsId, line.sampleRate * 600, 1, line.sampleRate, true,
                data => ReadStream(line, data));
            source.clip = line.clip;
            source.Play();
            RaiseLine(OnLineStarted, line.caption);
        }

        // Unity resamples the clip from line.sampleRate (16 kHz from the Hive) to the output rate itself.
        // Runs on Unity's audio/stream thread: no Unity API in here.
        static void ReadStream(Line line, float[] data)
        {
            line.readBlock = data.Length;
            int n = line.pcm.Read(data, 0, data.Length);
            if (n < data.Length) Array.Clear(data, n, data.Length - n);
            double sum = 0;
            for (int i = 0; i < n; i++) sum += data[i] * data[i];
            line.rms = n > 0 ? (float)Math.Sqrt(sum / n) : 0f;
        }

        bool IsFinished(Line l, float now)
        {
            if (!l.started)
                return l.IsStream && l.pcm.Count == 0 && (l.ended || now - l.lastChunkAt > streamStallSeconds);
            if (l.IsStream)
            {
                int count = l.pcm.Count;
                if (count != l.lastCount) { l.lastCount = count; l.countChangedAt = now; }
                bool noMoreAudio = l.ended || now - l.lastChunkAt > streamStallSeconds;
                if (!noMoreAudio) { l.drainedSince = -1f; return false; }
                // Buffered audio that nobody pulls (source disabled, audio off) must not block the queue forever.
                if (count > 0) return now - l.countChangedAt > 1.5f;
                if (l.drainedSince < 0f) l.drainedSince = now;
                // Let Unity's own stream buffer play out: up to ~2 read-ahead blocks after the last pull.
                float tail = Mathf.Clamp(2f * l.readBlock / Mathf.Max(1, l.sampleRate) + 0.1f, 0.35f, 1.5f);
                return now - l.drainedSince > tail;
            }
            if (l.clip == null)
            {
                float elapsed = now - l.startedAt;
                return elapsed >= l.captionOnlySeconds && (!l.deviceSpeech || elapsed >= 12f || !_deviceSpeech.Speaking);
            }
            return (!source.isPlaying && now - l.startedAt > 0.1f) || now - l.startedAt > l.clip.length + 1f;
        }

        void Finish()
        {
            var line = _current;
            _current = null;
            if (line == null) return;
            if (line.deviceSpeech) _deviceSpeech.Stop();
            if (source != null && line.clip != null && source.clip == line.clip)
            {
                source.Stop();
                source.clip = null;
            }
            if (line.IsStream)
            {
                _streams.Remove(line.ttsId);
                if (line.clip != null) Destroy(line.clip);
            }
            if (line.started) RaiseLine(OnLineEnded, line.caption);
        }

        // ---------------------------------------------------------------- clips & amplitude
        AudioClip FindClip(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)) return null;
            if (_clipCache.TryGetValue(lineId, out var clip)) return clip;
            clip = string.IsNullOrEmpty(resourcesFolder) ? null : Resources.Load<AudioClip>(resourcesFolder + "/" + lineId);
            _clipCache[lineId] = clip; // caches misses too
            return clip;
        }

        string CaptionFor(string lineId)
        {
            foreach (var l in lines)
                if (l != null && l.id == lineId) return l.caption ?? "";
            return "";
        }

        // Per-clip loudness envelope, so mouth flaps don't depend on the spatializer's output tap.
        // Needs Load Type = Decompress On Load; otherwise Amplitude falls back to GetOutputData.
        float[] EnvelopeOf(AudioClip clip)
        {
            if (_envelopes.TryGetValue(clip, out var env)) return env;
            env = null;
            if (clip.loadType == AudioClipLoadType.DecompressOnLoad && clip.loadState == AudioDataLoadState.Loaded && clip.samples > 0)
            {
                var data = new float[clip.samples * clip.channels];
                if (clip.GetData(data, 0))
                {
                    int win = Mathf.Max(1, clip.frequency / EnvelopeRate) * clip.channels;
                    env = new float[(data.Length + win - 1) / win];
                    for (int f = 0; f < env.Length; f++)
                    {
                        int start = f * win, end = Mathf.Min(data.Length, start + win);
                        double sum = 0;
                        for (int i = start; i < end; i++) sum += data[i] * data[i];
                        env[f] = (float)Math.Sqrt(sum / Mathf.Max(1, end - start));
                    }
                }
            }
            _envelopes[clip] = env;
            return env;
        }

        void UpdateAmplitude()
        {
            float target = 0f;
            var l = _current;
            if (l != null && l.started && l.clip != null)
            {
                if (l.IsStream) target = l.rms;
                else if (l.envelope != null && l.envelope.Length > 0)
                    target = l.envelope[Mathf.Clamp((int)(source.time * EnvelopeRate), 0, l.envelope.Length - 1)];
                else
                {
                    source.GetOutputData(_outBuf, 0);
                    double sum = 0;
                    foreach (float s in _outBuf) sum += s * s;
                    target = (float)Math.Sqrt(sum / _outBuf.Length);
                }
            }
            target = Mathf.Clamp01(target * amplitudeGain);
            float rate = target > Amplitude ? attackPerSecond : releasePerSecond;
            Amplitude = Mathf.Lerp(Amplitude, target, 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime));
        }

        static string Describe(Line l) => l.IsStream ? "tts:" + l.ttsId : l.lineId;

        static void RaiseLine(Action<string> ev, string caption)
        {
            if (ev == null) return;
            foreach (var d in ev.GetInvocationList())
                try { ((Action<string>)d)(caption); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}
