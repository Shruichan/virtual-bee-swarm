using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Beeswax
{
    /// <summary>
    /// "Now HUM!": pitch-based hum detector on the Quest microphone (YIN, analysed at ~16 kHz, 20 ms hops).
    /// Voiced = a pitch in [minHz, maxHz] that stays within maxJumpSemitones for at least holdSeconds,
    /// louder than the calibrated noise floor. Talking and clapping rarely hold a steady pitch; humming does.
    ///
    /// Outputs: Voiced, PitchHz, Level (0..1), HumSeconds (voiced time) and Heat (0..1 thermometer:
    /// rises while humming, cools slowly; the operator's hum.set overrides it).
    ///
    /// Only analyses while Listening (set it for the hornet beat). Call CalibrateNoiseFloor() at reset while
    /// nobody hums. While listening, duck or high-pass (> 600 Hz) the game's own audio: the Quest mic hears
    /// its speakers, and the hive/hornet drones sit in the 80-500 Hz hum band. Assign the two mixer
    /// snapshots below to do that automatically. Beeswax's own lines are ignored via ignoreWhileSpeaking.
    /// The mic is shared through BeeswaxMic, so BeeswaxVoiceInput can listen at the same time (it then hears the
    /// hum too, and this detector hears the player talking).
    /// </summary>
    public class HumDetector : MonoBehaviour
    {
        [Header("Microphone")]
        [Tooltip("Empty = default mic. The mic is shared (BeeswaxMic): whoever opens it first picks the device.")]
        [SerializeField] string deviceName = "";
        [SerializeField] int requestedSampleRate = 16000;
        [Tooltip("Keep the mic open after listening stops (faster restart). Nothing is recorded or sent.")]
        [SerializeField] bool keepMicOpen = true;

        [Header("Pitch")]
        [SerializeField] float minHz = 80f;
        [SerializeField] float maxHz = 500f;
        [Tooltip("YIN threshold: higher accepts noisier/group hums, lower is stricter.")]
        [Range(0.05f, 0.5f)] [SerializeField] float yinThreshold = 0.25f;
        [SerializeField] float holdSeconds = 0.3f;
        [Tooltip("Short unvoiced gaps (breaths, a voice crack) that don't reset the hold.")]
        [SerializeField] float dropoutGraceSeconds = 0.12f;
        [Tooltip("Max pitch change between 20 ms hops. Generous, so a group humming different notes still counts.")]
        [SerializeField] float maxJumpSemitones = 4f;

        [Header("Level")]
        [SerializeField] float noiseFloorDb = -55f;
        [Tooltip("A hop must be this much louder than the floor to count.")]
        [SerializeField] float gateAboveFloorDb = 6f;
        [Tooltip("Level reaches 1 this far above the floor.")]
        [SerializeField] float fullScaleAboveFloorDb = 30f;

        [Header("Heat (thermometer, 0..1)")]
        [SerializeField] float heatPerHumSecond = 0.12f;
        [SerializeField] float coolPerSecond = 0.03f;

        [Header("Wiring")]
        [Tooltip("Receives hum.set (operator slider). Defaults to BeeswaxBus.Instance.")]
        [SerializeField] BeeswaxBus bus;
        [Tooltip("Hops are ignored while this voice is speaking (\"Now HUM!\" is pitched too).")]
        [SerializeField] BeeswaxRoomVoice ignoreWhileSpeaking;
        [Tooltip("Optional: a snapshot that ducks/high-passes game audio while listening.")]
        [SerializeField] AudioMixerSnapshot listeningSnapshot;
        [SerializeField] AudioMixerSnapshot normalSnapshot;
        [SerializeField] float snapshotFadeSeconds = 0.25f;

        public event Action<bool> OnVoicedChanged;
        /// <summary>The operator moved the hum slider (value 0..1, already applied to Heat).</summary>
        public event Action<float> OnOperatorHeat;

        public bool Voiced { get; private set; }
        public float PitchHz { get; private set; }
        public float Clarity { get; private set; }
        public float Level { get; private set; }
        public float HumSeconds { get; private set; }
        public float Heat { get; private set; }
        public float LevelDb { get; private set; } = -120f;
        public float NoiseFloorDb => noiseFloorDb;
        public bool MicRunning => _clip != null && BeeswaxMic.Clip == _clip;
        public bool Calibrating => _calibration != null;

        public bool Listening
        {
            get => _listening;
            set
            {
                if (_listening == value) return;
                _listening = value;
                if (value) StartMic();
                else
                {
                    SetVoiced(false);
                    _held = 0f;
                    Level = 0f;
                    if (!keepMicOpen && _calibration == null) StopMic();
                }
                var snap = value ? listeningSnapshot : normalSnapshot;
                if (snap != null) snap.TransitionTo(snapshotFadeSeconds);
            }
        }

        const int AnalysisRate = 16000;
        bool _listening;
        AudioClip _clip;
        readonly MicCursor _cursor = new MicCursor();
        Action<float[], int> _onMicBlock;
        int _rate, _hop, _decim, _window, _feedFill;
        float[] _hopBuf, _ana, _scratch;
        float _held, _gap, _lastPitch;
        List<float> _calibration;
        BeeswaxBus _bound;

        // ---------------------------------------------------------------- lifecycle
        void OnEnable() => Bind();
        void Start() => Bind();
        void OnDisable() { Unbind(); Listening = false; StopMic(); }

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnHumSet += HandleHumSet;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnHumSet -= HandleHumSet;
            _bound = null;
        }

        void HandleHumSet(HumSet h)
        {
            SetHeat(h.value);
            try { OnOperatorHeat?.Invoke(Heat); } catch (Exception e) { Debug.LogException(e); }
        }

        // ---------------------------------------------------------------- public API
        /// <summary>Thermometer override (operator slider, or your own logic).</summary>
        public void SetHeat(float value) => Heat = Mathf.Clamp01(value);

        /// <summary>New group / new hornet: zero the hum counters.</summary>
        public void ResetHum()
        {
            HumSeconds = 0f;
            Heat = 0f;
            Level = 0f;
            _held = 0f;
            SetVoiced(false);
        }

        /// <summary>Open the mic early (e.g. at the start of the hornet beat) so listening starts instantly.</summary>
        public void WarmUp() => StartMic();

        /// <summary>Measure room noise for `seconds` (nobody humming) and use the median as the floor.</summary>
        public Coroutine CalibrateNoiseFloor(float seconds = 1.5f) => StartCoroutine(Calibrate(seconds));

        IEnumerator Calibrate(float seconds)
        {
            if (!StartMic()) { Debug.LogWarning("[Beeswax] hum calibration skipped: no microphone."); yield break; }
            _calibration = new List<float>();
            yield return new WaitForSecondsRealtime(seconds);
            var samples = _calibration;
            _calibration = null;
            if (samples.Count < 5) yield break;
            samples.Sort();
            noiseFloorDb = samples[samples.Count / 2];
            Debug.Log($"[Beeswax] hum noise floor = {noiseFloorDb:0.0} dBFS ({samples.Count} hops)");
            if (!_listening && !keepMicOpen) StopMic();
        }

        // ---------------------------------------------------------------- microphone
        bool StartMic()
        {
            if (_clip != null) return true;
            if (!BeeswaxMic.Acquire(this, deviceName, requestedSampleRate)) return false; // Update retries while Listening
            _clip = BeeswaxMic.Clip;
            Configure(_clip.frequency);
            _cursor.Reset();
            return true;
        }

        void Configure(int rate)
        {
            _rate = rate;
            _hop = _rate / 50;                            // 20 ms (a divisor of the rate)
            while (_hop > 1 && _rate % _hop != 0) _hop--;
            _decim = Mathf.Max(1, Mathf.RoundToInt((float)_rate / AnalysisRate));
            int anaRate = _rate / _decim;
            _window = anaRate / 40;                       // 25 ms
            int tauMax = Mathf.CeilToInt(anaRate / minHz);
            _ana = new float[_window + tauMax + 2];
            _scratch = new float[tauMax + 2];
            _hopBuf = new float[_hop];
            _feedFill = 0;
        }

        /// <summary>
        /// Analyse audio from somewhere other than the mic (a recorded test clip, another capture API).
        /// Always analyses, Listening or not. Don't mix with the mic.
        /// </summary>
        public void FeedSamples(float[] samples, int count, int sampleRate)
        {
            if (_hopBuf == null || sampleRate != _rate) Configure(sampleRate);
            Feed(samples, count, true);
        }

        void Feed(float[] samples, int count, bool analyse)
        {
            for (int i = 0; i < count; i++)
            {
                _hopBuf[_feedFill++] = samples[i];
                if (_feedFill < _hop) continue;
                _feedFill = 0;
                ProcessHop(analyse);
            }
        }

        void StopMic()
        {
            if (_clip == null) return;
            BeeswaxMic.Release(this);
            _clip = null;
            _cursor.Reset();
        }

        void Update()
        {
            if (_listening && _clip == null) StartMic();
            if (_clip == null) return;
            BeeswaxMic.EnsureRecording();
            var shared = BeeswaxMic.Clip;
            if (shared == null) { StopMic(); return; } // lost the device; StartMic retries while Listening
            if (shared != _clip) { _clip = shared; Configure(_clip.frequency); } // restarted by BeeswaxMic
            if (_onMicBlock == null) _onMicBlock = (block, n) => Feed(block, n, _listening);
            _cursor.Pull(_clip, BeeswaxMic.Position, _onMicBlock);
        }

        // ---------------------------------------------------------------- analysis
        void ProcessHop(bool analyse)
        {
            double sum = 0;
            for (int i = 0; i < _hop; i++) sum += _hopBuf[i] * _hopBuf[i];
            LevelDb = 20f * Mathf.Log10(Mathf.Sqrt((float)(sum / _hop)) + 1e-9f);
            _calibration?.Add(LevelDb);

            // Slide the analysis window and append this hop, decimated to ~16 kHz (box filter; fine for a hum).
            int m = _hop / _decim;
            int keep = _ana.Length - m;
            if (keep > 0) Array.Copy(_ana, m, _ana, 0, keep);
            for (int i = Mathf.Max(0, -keep); i < m; i++)
            {
                float acc = 0f;
                for (int k = 0; k < _decim; k++) acc += _hopBuf[i * _decim + k];
                _ana[keep + i] = acc / _decim;
            }

            if (!analyse) return;
            float hopSeconds = (float)_hop / _rate;
            bool speaking = ignoreWhileSpeaking != null && ignoreWhileSpeaking.IsSpeaking;

            bool voicedHop = false;
            float pitch = 0f;
            if (!speaking && LevelDb > noiseFloorDb + gateAboveFloorDb)
            {
                pitch = YinPitch.Detect(_ana, 0, _window, _rate / _decim, minHz, maxHz, yinThreshold, _scratch, out float clarity);
                Clarity = clarity;
                voicedHop = pitch > 0f &&
                    (_lastPitch <= 0f || Mathf.Abs(12f * Mathf.Log(pitch / _lastPitch, 2f)) <= maxJumpSemitones);
            }

            if (voicedHop)
            {
                _held += hopSeconds;
                _gap = 0f;
                _lastPitch = pitch;
                PitchHz = pitch;
            }
            else
            {
                _gap += hopSeconds;
                if (_gap > dropoutGraceSeconds) { _held = 0f; _lastPitch = 0f; PitchHz = 0f; }
            }

            bool voiced = _held >= holdSeconds;
            SetVoiced(voiced);
            float target = voiced
                ? Mathf.Clamp01((LevelDb - noiseFloorDb - gateAboveFloorDb) / Mathf.Max(1f, fullScaleAboveFloorDb - gateAboveFloorDb))
                : 0f;
            Level = Mathf.Lerp(Level, target, voiced ? 0.35f : 0.15f);

            if (voiced)
            {
                HumSeconds += hopSeconds;
                Heat = Mathf.Clamp01(Heat + heatPerHumSecond * hopSeconds * (0.5f + 0.5f * Level));
            }
            else if (!speaking)
            {
                Heat = Mathf.Clamp01(Heat - coolPerSecond * hopSeconds);
            }
        }

        void SetVoiced(bool v)
        {
            if (Voiced == v) return;
            Voiced = v;
            try { OnVoicedChanged?.Invoke(v); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}
