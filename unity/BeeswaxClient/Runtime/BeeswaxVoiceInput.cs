using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Beeswax
{
    /// <summary>
    /// The player's voice → the Hive (→ speech-to-text → intent). Push-to-talk: bind BeginTalk()/EndTalk() (and
    /// Cancel()) to any input you like (pinch, button, poke). Optional energy VAD mode: set VadArmed = true in the
    /// beats where the player may just speak.
    ///
    /// While talking it streams listen.start → binary MIC_PCM16 frames (16 kHz PCM16 LE mono, frameMs each) →
    /// listen.end, starting with preRollSeconds of audio from just before BeginTalk (people talk as they press).
    /// Capped at maxUtteranceSeconds. If the bus is down or drops mid-utterance, the audio is kept and re-sent from
    /// the start once the link is back (up to deliverGraceSeconds after the player stops).
    ///
    /// Mic: shared with HumDetector through BeeswaxMic (one Unity recording; both may listen at once). Whatever rate
    /// the device gives is resampled to 16 kHz. Nothing is sent unless an utterance is open.
    /// Ducking: OnDuck(true/false) (and the optional mixer snapshots) while listening, so game audio can drop.
    /// </summary>
    public class BeeswaxVoiceInput : MonoBehaviour
    {
        public enum TalkMode { PushToTalk, Vad }
        public enum EndReason { Released, Cancelled, Capped, Silence, TooShort, Disabled }

        [SerializeField] BeeswaxBus bus;
        [Tooltip("PushToTalk: only BeginTalk/EndTalk. Vad: also starts by itself on speech while VadArmed.")]
        [SerializeField] TalkMode mode = TalkMode.PushToTalk;

        [Header("Microphone")]
        [Tooltip("Off = never open the mic; audio only comes from FeedSamples (tests, another capture API).")]
        [SerializeField] bool useMicrophone = true;
        [Tooltip("Empty = default mic. Shared with HumDetector: whoever opens the mic first picks the device.")]
        [SerializeField] string deviceName = "";
        [Tooltip("Open the mic at Start (asks for permission early; nothing is sent until talking). Otherwise it opens " +
                 "on WarmUp()/BeginTalk()/VadArmed, and the first ~0.2 s of a cold BeginTalk may be lost.")]
        [SerializeField] bool openMicOnStart = true;

        [Header("Streaming")]
        [Range(10, 100)] [SerializeField] int frameMs = 40;
        [Tooltip("Audio per utterance, pre-roll included (the Hive keeps at most 15 s).")]
        [SerializeField] float maxUtteranceSeconds = 15f;
        [Tooltip("Audio from just before BeginTalk / VAD onset that starts the utterance.")]
        [Range(0f, 1f)] [SerializeField] float preRollSeconds = 0.25f;
        [Tooltip("Keep recording this long after EndTalk (people let go before the last word ends).")]
        [Range(0f, 0.5f)] [SerializeField] float releaseTailSeconds = 0.15f;
        [Tooltip("Shorter push-to-talk presses are cancelled (accidental taps).")]
        [SerializeField] float minUtteranceSeconds = 0.3f;
        [Tooltip("After the player stops: how long to wait for the bus to come back before the utterance is dropped.")]
        [SerializeField] float deliverGraceSeconds = 5f;
        [Tooltip("Pause sending while this much is still queued on the socket (slow or dead link).")]
        [SerializeField] int maxQueuedSendBytes = 64000;

        [Header("Voice activity detection (VadArmed)")]
        [SerializeField] float vadStartAboveFloorDb = 12f;
        [SerializeField] float vadStopAboveFloorDb = 6f;
        [Tooltip("Never start below this level, however quiet the room.")]
        [SerializeField] float vadMinLevelDb = -50f;
        [SerializeField] float vadStartSeconds = 0.12f;
        [SerializeField] float vadEndSilenceSeconds = 0.8f;
        [Tooltip("VAD does not start while this voice speaks (the mic hears Beeswax).")]
        [SerializeField] BeeswaxRoomVoice ignoreWhileSpeaking;

        [Header("Ducking")]
        [Tooltip("Optional: snapshot that ducks game audio while listening (and its partner to restore it).")]
        [SerializeField] AudioMixerSnapshot duckedSnapshot;
        [SerializeField] AudioMixerSnapshot normalSnapshot;
        [SerializeField] float snapshotFadeSeconds = 0.15f;
        [Tooltip("Also duck while VAD is armed and waiting (game audio would otherwise trigger it).")]
        [SerializeField] bool duckWhileVadArmed = true;

        // ---------------------------------------------------------------- events (main thread)
        /// <summary>An utterance started: "ptt" or "vad".</summary>
        public event Action<string> OnTalkStarted;
        /// <summary>The player stopped (reason, seconds talked). Delivery to the Hive may still be finishing.</summary>
        public event Action<EndReason, float> OnTalkEnded;
        /// <summary>true = duck game audio now, false = restore it.</summary>
        public event Action<bool> OnDuck;
        /// <summary>The Hive's transcript of a Quest utterance (partial ones have final = false).</summary>
        public event Action<Transcript> OnTranscript;
        /// <summary>The Hive's intent for the last utterance (also on BeeswaxBus.OnIntent, with glasses ones).</summary>
        public event Action<Intent> OnIntent;

        // ---------------------------------------------------------------- state
        public bool IsTalking => _talking;
        /// <summary>EndTalk was called; still recording the release tail.</summary>
        public bool IsEnding => _talking && _endAt >= 0f;
        public float TalkSeconds => _talking ? Time.realtimeSinceStartup - _talkStartedAt : 0f;
        public string TalkModeName { get; private set; } = "";
        public bool MicRunning => _micClip != null && BeeswaxMic.Clip == _micClip;
        /// <summary>Speech level for a meter, 0..1 (relative to the room's noise floor).</summary>
        public float Level { get; private set; }
        public float LevelDb { get; private set; } = -120f;
        public float NoiseFloorDb => _floorDb;
        public bool IsDucking { get; private set; }
        /// <summary>listen.end was sent and no intent came back yet (show "thinking").</summary>
        public bool AwaitingReply => _endSentAt >= 0f && Time.realtimeSinceStartup - _endSentAt < 10f;
        /// <summary>Audio of the current/last utterance not yet handed to the bus (frames).</summary>
        public int PendingFrames => _frames.Count - _sent;
        public bool Delivering => _delivering;
        public Transcript LastTranscript { get; private set; }
        public Intent LastIntent { get; private set; }
        public EndReason LastEndReason { get; private set; }
        public int UtterancesSent { get; private set; }
        public int UtterancesLost { get; private set; }

        public TalkMode Mode { get => mode; set => mode = value; }

        public bool UseMicrophone
        {
            get => useMicrophone;
            set { useMicrophone = value; if (!value) CloseMic(); }
        }

        /// <summary>VAD: listen for speech on its own (opens the mic). Turn on in voice beats, off elsewhere.</summary>
        public bool VadArmed
        {
            get => _vadArmed;
            set
            {
                if (_vadArmed == value) return;
                _vadArmed = value;
                _vadOnset = 0f;
                if (!value && _talking && TalkModeName == ListenMode.Vad) Cancel();
            }
        }

        const int Rate = BeeswaxProtocol.MicSampleRate;
        const int Block = Rate / 100; // 10 ms analysis block at 16 kHz

        BeeswaxBus _bound;
        AudioClip _micClip;
        bool _wantMic, _vadArmed;
        readonly MicCursor _cursor = new MicCursor();
        readonly StreamResampler _resampler = new StreamResampler(Rate);
        Action<float[], int> _onMicBlock, _on16k;

        // 16 kHz pipeline
        readonly float[] _blk = new float[Block];
        int _blkFill;
        float[] _preroll = new float[Rate];
        int _prerollWrite, _prerollCount;

        // utterance
        bool _talking, _streamOpen, _delivering;
        float _talkStartedAt, _releasedAt = -1f, _endAt = -1f, _deliverDeadline, _endSentAt = -1f;
        readonly List<byte[]> _frames = new List<byte[]>();
        int _sent;
        float _prerollUsed;
        byte[] _frame = new byte[1280];
        int _frameFill;
        string _deliverMode = ListenMode.Ptt;

        // VAD
        float _floorDb = -60f, _vadOnset, _vadSilence, _vadSpeech;
        bool _waitForQuiet;

        // ---------------------------------------------------------------- lifecycle
        void OnEnable() => Bind();

        void Start()
        {
            Bind();
            if (openMicOnStart) WarmUp();
        }

        void OnDisable()
        {
            if (_talking) Finish(EndReason.Disabled, cancel: true);
            DropDelivery();
            SetDuck(false);
            CloseMic();
            Unbind();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && _talking) Finish(EndReason.Cancelled, cancel: true);
        }

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnTranscript += HandleTranscript;
            b.OnIntent += HandleIntent;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnTranscript -= HandleTranscript;
            _bound.OnIntent -= HandleIntent;
            _bound = null;
        }

        // ---------------------------------------------------------------- public API
        /// <summary>
        /// Start an utterance (push-to-talk). Returns false only when there is no microphone/permission yet
        /// (the permission prompt is shown once). Works while the bus is reconnecting: audio is kept and sent later.
        /// </summary>
        public bool BeginTalk() => Begin(ListenMode.Ptt);

        /// <summary>Stop talking; the utterance is sent for transcription (after releaseTailSeconds).</summary>
        public void EndTalk()
        {
            if (!_talking || _endAt >= 0f) return;
            _releasedAt = Time.realtimeSinceStartup;
            if (releaseTailSeconds <= 0f) Finish(EndReason.Released, cancel: false);
            else _endAt = _releasedAt + releaseTailSeconds;
        }

        /// <summary>Throw the utterance away (the Hive is told to discard it).</summary>
        public void Cancel()
        {
            if (_talking) Finish(EndReason.Cancelled, cancel: true);
        }

        /// <summary>Discard the previous beat's voice input and clear its waiting indicator.</summary>
        public void CancelPendingTurn()
        {
            Cancel();
            DropDelivery();
            _endSentAt = -1f;
        }

        /// <summary>Open the mic now so the next BeginTalk has pre-roll and no start-up gap.</summary>
        public void WarmUp()
        {
            _wantMic = true;
            OpenMic();
        }

        /// <summary>Close the mic (it reopens on BeginTalk / WarmUp / VadArmed).</summary>
        public void CloseMic()
        {
            _wantMic = false;
            if (_micClip == null) return;
            BeeswaxMic.Release(this);
            _micClip = null;
            _cursor.Reset();
        }

        /// <summary>
        /// Push audio from somewhere other than the mic (tests, another capture API): it goes through the same
        /// resampling, VAD and streaming. Turn useMicrophone off first, or the two streams interleave.
        /// </summary>
        public void FeedSamples(float[] samples, int count, int sampleRate)
        {
            if (samples == null || count <= 0) return;
            _resampler.Configure(sampleRate);
            _resampler.Process(samples, Math.Min(count, samples.Length), _on16k ?? (_on16k = On16k));
        }

        // ---------------------------------------------------------------- main loop
        void Update()
        {
            Bind();
            float now = Time.realtimeSinceStartup;

            if (useMicrophone && (_wantMic || _talking || _vadArmed) && !MicRunning) OpenMic();
            if (_micClip != null)
            {
                BeeswaxMic.EnsureRecording();
                var shared = BeeswaxMic.Clip;
                if (shared == null) { _micClip = null; BeeswaxMic.Release(this); _cursor.Reset(); }
                else
                {
                    _micClip = shared;
                    _resampler.Configure(shared.frequency);
                    _cursor.Pull(shared, BeeswaxMic.Position, _onMicBlock ?? (_onMicBlock = OnMicBlock));
                }
            }

            if (_talking)
            {
                if (_endAt >= 0f && now >= _endAt) Finish(EndReason.Released, cancel: false);
                else if (now - _talkStartedAt + _prerollUsed >= maxUtteranceSeconds) Finish(EndReason.Capped, cancel: false);
            }
            if (_talking || _delivering) Deliver(now);

            bool duck = _talking || (_vadArmed && duckWhileVadArmed && mode == TalkMode.Vad);
            if (duck != IsDucking) SetDuck(duck);
        }

        void OpenMic()
        {
            if (!useMicrophone || (_micClip != null && BeeswaxMic.Clip == _micClip)) return;
            if (!BeeswaxMic.Acquire(this, deviceName, Rate)) return;
            _micClip = BeeswaxMic.Clip;
            _resampler.Configure(_micClip.frequency);
            _cursor.Reset();
        }

        void OnMicBlock(float[] samples, int count) =>
            _resampler.Process(samples, count, _on16k ?? (_on16k = On16k));

        // 16 kHz audio, in arbitrary chunks → 10 ms blocks: analyse (may start a VAD utterance), append, remember.
        void On16k(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _blk[_blkFill++] = samples[i];
                if (_blkFill < Block) continue;
                _blkFill = 0;
                Analyse(_blk);
                if (_talking) AppendPcm(_blk, Block);
                WritePreroll(_blk, Block);
            }
        }

        bool Begin(string listenMode)
        {
            if (_talking)
            {
                if (_endAt < 0f) return true; // already talking
                Finish(EndReason.Released, cancel: false); // still in the release tail: close it, then start anew
            }
            OpenMic();
            if (useMicrophone && !MicRunning)
            {
                Debug.LogWarning("[Beeswax] voice: no microphone (or no permission yet); not listening.");
                return false;
            }
            if (_delivering) FlushDelivery();

            _talking = true;
            _talkStartedAt = Time.realtimeSinceStartup;
            _releasedAt = -1f;
            _endAt = -1f;
            _endSentAt = -1f;
            TalkModeName = listenMode;
            _deliverMode = listenMode;
            _frames.Clear();
            _sent = 0;
            _streamOpen = false;
            _vadSilence = 0f;
            _vadSpeech = 0f;
            int frameBytes = Mathf.Clamp(frameMs, 10, 100) * Rate / 1000 * 2;
            if (_frame.Length != frameBytes) _frame = new byte[frameBytes];
            _frameFill = 0;
            AppendPreroll();
            SetDuck(true);
            Raise(OnTalkStarted, listenMode);
            Deliver(Time.realtimeSinceStartup);
            return true;
        }

        void Finish(EndReason reason, bool cancel)
        {
            if (!_talking) return;
            float seconds = (_releasedAt >= 0f ? _releasedAt : Time.realtimeSinceStartup) - _talkStartedAt;
            if (!cancel && reason == EndReason.Released && seconds < minUtteranceSeconds) { reason = EndReason.TooShort; cancel = true; }
            if (!cancel && reason == EndReason.Silence && _vadSpeech < minUtteranceSeconds) { reason = EndReason.TooShort; cancel = true; }
            _talking = false;
            _endAt = -1f;
            _prerollCount = 0; // the pre-roll ring holds this utterance's end; a quick re-press must not resend it
            LastEndReason = reason;
            CommitFrame();
            if (reason == EndReason.Capped && TalkModeName == ListenMode.Vad) _waitForQuiet = true;

            if (cancel)
            {
                if (_streamOpen && _bound != null) _bound.SendListenEnd(cancel: true);
                _streamOpen = false;
                _frames.Clear();
                _sent = 0;
            }
            else
            {
                _delivering = true;
                _deliverDeadline = Time.realtimeSinceStartup + deliverGraceSeconds;
                Deliver(Time.realtimeSinceStartup);
            }
            SetDuck(_vadArmed && duckWhileVadArmed && mode == TalkMode.Vad);
            Raise(OnTalkEnded, reason, Mathf.Max(0f, seconds));
        }

        // Open the stream when the bus is up, send frames (respecting backpressure), close it when the player is done.
        void Deliver(float now)
        {
            var b = _bound;
            if (b != null)
            {
                if (_streamOpen && !b.IsListening)
                {
                    // The link dropped (or someone else closed the utterance): the Hive lost it. Re-send from the start.
                    _streamOpen = false;
                    _sent = 0;
                }
                if (!_streamOpen && b.IsConnected && b.SendListenStart(_deliverMode)) { _streamOpen = true; _sent = 0; }
                if (_streamOpen)
                {
                    while (_sent < _frames.Count && b.QueuedSendBytes < maxQueuedSendBytes)
                    {
                        if (!b.SendMicFrame(_frames[_sent])) break;
                        _sent++;
                    }
                    if (_delivering && _sent == _frames.Count)
                    {
                        if (b.SendListenEnd(cancel: false))
                        {
                            _endSentAt = now;
                            LatencyReporter.Mark("voice");
                            UtterancesSent++;
                            EndDelivery();
                            return;
                        }
                        // The end marker didn't reach the send queue. Keep the audio for the reconnect retry.
                        _streamOpen = false;
                        _sent = 0;
                    }
                }
            }
            if (_delivering && now > _deliverDeadline)
            {
                Debug.LogWarning($"[Beeswax] voice: the Hive was unreachable; dropped a {_frames.Count * frameMs / 1000f:0.0} s utterance.");
                DropDelivery();
            }
        }

        // A new utterance starts while the last one is still being delivered: push the rest out now, or drop it.
        void FlushDelivery()
        {
            var b = _bound;
            if (_streamOpen && b != null && b.IsListening)
            {
                while (_sent < _frames.Count && b.SendMicFrame(_frames[_sent])) _sent++;
                bool complete = _sent == _frames.Count;
                if (b.SendListenEnd(cancel: !complete) && complete) { _endSentAt = Time.realtimeSinceStartup; LatencyReporter.Mark("voice"); UtterancesSent++; }
                else UtterancesLost++;
                EndDelivery();
            }
            else DropDelivery();
        }

        void DropDelivery()
        {
            if (!_delivering) return;
            if (_streamOpen && _bound != null) _bound.SendListenEnd(cancel: true);
            UtterancesLost++;
            EndDelivery();
        }

        void EndDelivery()
        {
            _delivering = false;
            _streamOpen = false;
            _frames.Clear();
            _sent = 0;
        }

        // ---------------------------------------------------------------- audio helpers
        void AppendPcm(float[] s, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int v = Mathf.RoundToInt(Mathf.Clamp(s[i], -1f, 1f) * 32767f);
                _frame[_frameFill++] = (byte)v;
                _frame[_frameFill++] = (byte)(v >> 8);
                if (_frameFill == _frame.Length) CommitFrame();
            }
        }

        void CommitFrame()
        {
            if (_frameFill == 0) return;
            var copy = new byte[_frameFill];
            Buffer.BlockCopy(_frame, 0, copy, 0, _frameFill);
            _frames.Add(copy);
            _frameFill = 0;
        }

        void WritePreroll(float[] s, int n)
        {
            int cap = Mathf.Clamp(Mathf.RoundToInt(preRollSeconds * Rate), 0, _preroll.Length);
            if (cap == 0) return;
            for (int i = 0; i < n; i++)
            {
                _preroll[_prerollWrite] = s[i];
                _prerollWrite = (_prerollWrite + 1) % cap;
            }
            _prerollCount = Mathf.Min(cap, _prerollCount + n);
        }

        void AppendPreroll()
        {
            int cap = Mathf.Clamp(Mathf.RoundToInt(preRollSeconds * Rate), 0, _preroll.Length);
            _prerollUsed = 0f;
            if (cap == 0 || _prerollCount == 0) return;
            int count = Mathf.Min(_prerollCount, cap);
            _prerollUsed = count / (float)Rate;
            int start = (_prerollWrite - count + cap) % cap;
            var tmp = new float[count];
            for (int i = 0; i < count; i++) tmp[i] = _preroll[(start + i) % cap];
            AppendPcm(tmp, count);
            _prerollCount = 0; // each audio sample starts at most one utterance
        }

        // Level, adaptive noise floor and the VAD state machine, once per 10 ms block.
        void Analyse(float[] blk)
        {
            double sum = 0;
            for (int i = 0; i < Block; i++) sum += blk[i] * blk[i];
            LevelDb = 20f * Mathf.Log10(Mathf.Sqrt((float)(sum / Block)) + 1e-9f);
            const float dt = Block / (float)Rate;

            bool inSpeech = _talking && TalkModeName == ListenMode.Vad;
            if (!_talking)
            {
                // Floor: drops fast, rises slowly (~2.5 s), so speech onsets barely move it.
                _floorDb = LevelDb < _floorDb ? Mathf.Lerp(_floorDb, LevelDb, 0.2f) : Mathf.Lerp(_floorDb, LevelDb, 0.004f);
                _floorDb = Mathf.Clamp(_floorDb, -90f, -20f);
            }
            float target = Mathf.Clamp01((LevelDb - _floorDb - vadStopAboveFloorDb) / 30f);
            Level = Mathf.Lerp(Level, target, target > Level ? 0.5f : 0.15f);

            bool loud = LevelDb > Mathf.Max(_floorDb + vadStartAboveFloorDb, vadMinLevelDb);
            bool quiet = LevelDb < Mathf.Max(_floorDb + vadStopAboveFloorDb, vadMinLevelDb - 6f);

            if (inSpeech)
            {
                if (quiet) _vadSilence += dt;
                else { _vadSilence = 0f; _vadSpeech += dt; }
                if (_vadSilence >= vadEndSilenceSeconds) Finish(EndReason.Silence, cancel: false);
                return;
            }
            if (!_vadArmed || mode != TalkMode.Vad || _talking) { _vadOnset = 0f; return; }
            if (_waitForQuiet)
            {
                if (quiet) _waitForQuiet = false;
                return;
            }
            bool beeswaxTalking = ignoreWhileSpeaking != null && ignoreWhileSpeaking.IsSpeaking;
            _vadOnset = loud && !beeswaxTalking ? _vadOnset + dt : 0f;
            if (_vadOnset >= vadStartSeconds)
            {
                _vadOnset = 0f;
                if (Begin(ListenMode.Vad)) _vadSpeech = vadStartSeconds;
            }
        }

        void SetDuck(bool duck)
        {
            if (duck == IsDucking) return;
            IsDucking = duck;
            var snap = duck ? duckedSnapshot : normalSnapshot;
            if (snap != null) snap.TransitionTo(snapshotFadeSeconds);
            Raise(OnDuck, duck);
        }

        // ---------------------------------------------------------------- Hive replies
        void HandleTranscript(Transcript t)
        {
            if (t.source != Role.Quest) return;
            LastTranscript = t;
            if (t.final) LatencyReporter.Lap("voice", "quest.listen_end_to_transcript", clear: false);
            if (t.final && t.text.Length == 0) _endSentAt = -1f; // heard nothing: no intent will follow
            Raise(OnTranscript, t);
        }

        void HandleIntent(Intent i)
        {
            bool ours = _endSentAt >= 0f;
            LastIntent = i;
            if (ours)
            {
                LatencyReporter.Lap("voice", "quest.listen_end_to_intent");
                _endSentAt = -1f;
            }
            Raise(OnIntent, i);
        }

        static void Raise<T>(Action<T> ev, T a)
        {
            if (ev == null) return;
            foreach (var d in ev.GetInvocationList())
                try { ((Action<T>)d)(a); } catch (Exception e) { Debug.LogException(e); }
        }

        static void Raise<T1, T2>(Action<T1, T2> ev, T1 a, T2 b)
        {
            if (ev == null) return;
            foreach (var d in ev.GetInvocationList())
                try { ((Action<T1, T2>)d)(a, b); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}
