using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The what-if bang. "Ask me anything. What if...?" Push-to-talk streams the Quest mic to the Hive
    /// (GameNet.BeginTalk/EndTalk -> the client's BeeswaxVoiceInput): hold a pinch or trigger here, controller
    /// B/Y in any voice beat, or hold T on the desktop. The live transcript shows under the prompt.
    /// The fire is tracked from the window scene (whatif_fire / fire_out), whoever caused it: the player's voice
    /// intent, the Board's whatif.force, an orbis.request, or the local backup. fire_out completes the beat.
    /// Offline (Hive unreachable or forced): a talk press + release is taken as the question (1st = "what if a
    /// fire starts?", 2nd = "put it out!"), and if nobody does anything Beeswax plays both on timers, so the beat
    /// still shows the fire. Desktop/operator backup: F = fire, P = put it out (also steers the live window).
    /// </summary>
    public class WhatIfBeat : BeatBehaviour
    {
        [SerializeField] GameObject ui;
        [SerializeField] TextMesh promptText;
        [SerializeField] TextMesh transcriptText;
        [SerializeField] TextMesh hintText;
        [SerializeField] Renderer micIndicator;
        [SerializeField] EmbersFx embers;
        [SerializeField] BeeswaxController beeswax;

        [Header("Input")]
        [SerializeField] KeyCode talkKey = KeyCode.T;
        [SerializeField] KeyCode fireKey = KeyCode.F;
        [SerializeField] KeyCode putOutKey = KeyCode.P;
        [Tooltip("Key T / controller B-Y push-to-talk also work in the other voice beats (not the hornet: the hum detector owns the mic then).")]
        [SerializeField] bool talkInAllVoiceBeats = true;

        [Header("Timing")]
        [SerializeField] float completeAfterFireOutSeconds = 4f;
        [Tooltip("If the Hive's fx 'embers' hasn't come this long after the fire started, start the embers anyway.")]
        [SerializeField] float emberBackupDelaySeconds = 4f;
        [Tooltip("Offline only: no fire this long after the prompt -> Beeswax starts it himself (0 = never).")]
        [SerializeField] float offlineAutoFireSeconds = 9f;
        [Tooltip("Offline only: the fire burns this long -> it gets put out (0 = never).")]
        [SerializeField] float offlineAutoPutOutSeconds = 8f;
        [SerializeField] int wrapChars = 36;

        public bool FireStarted { get; private set; }
        public bool FireOut { get; private set; }
        /// <summary>The talk input is held in a voice beat, online or offline (the game sound stays silent meanwhile).</summary>
        public static bool TalkHeld { get; private set; }

        bool _wasOnline = true;
        bool _holdPrev;         // talk input held last frame (edge detection: one BeginTalk per press)
        bool _pressStreams;     // this press went to the Hive (online); false = offline press, acted on at release
        float _promptDoneAt = -1f, _fireAt = -1f;

        public override bool Handles(GameBeat beat) => beat == GameBeat.WhatIf;

        protected override void Awake()
        {
            base.Awake();
            if (embers == null) embers = FindAnyObjectByType<EmbersFx>();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            SetActive(ui, false);
        }

        void OnEnable()
        {
            if (net == null) return;
            net.TranscriptReceived += HandleTranscript;
            net.IntentReceived += HandleIntent;
            net.SceneChanged += HandleScene;
        }

        void OnDisable()
        {
            TalkHeld = false;
            if (net == null) return;
            net.TranscriptReceived -= HandleTranscript;
            net.IntentReceived -= HandleIntent;
            net.SceneChanged -= HandleScene;
        }

        protected override void OnEnter(GameBeat beat)
        {
            FireStarted = FireOut = false;
            _promptDoneAt = _fireAt = -1f;
            SetActive(ui, true);
            if (promptText != null) promptText.text = "Ask me anything.\n\"What if...?\"";
            if (transcriptText != null) transcriptText.text = "";
            _wasOnline = !(net != null && net.Online); // force a hint refresh
            if (beeswax != null) beeswax.GoPresent();
            StartCoroutine(Intro());
            // Someone asked about fire in an earlier voice beat: it's already burning, so "put it out" finishes this one.
            if (net != null && net.CurrentScene == GameIds.Scene.WhatIfFire) MarkFireStarted();
        }

        IEnumerator Intro()
        {
            yield return SayAndWait(GameLines.WhatIfPrompt, 1.5f, 4f);
            _promptDoneAt = Time.time;
        }

        protected override void OnExit(GameBeat beat)
        {
            SetActive(ui, false);
            if (net != null && net.Talking) net.EndTalk();
            if (embers != null && (embers.Emitting || embers.Scheduled)) embers.Stop();
        }

        protected override void OnResetState()
        {
            FireStarted = FireOut = false;
            _promptDoneAt = _fireAt = -1f;
            SetActive(ui, false);
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            if (net == null || director == null) return;
            GameBeat beat = director.Beat;
            bool voiceBeat = IsActive || (talkInAllVoiceBeats && IsVoiceBeat(beat));

            bool hold = voiceBeat && (GameInput.KeyHeld(talkKey) || GameHands.AnyTalkHeld() || (IsActive && GameHands.AnyPinchHeld()));
            TalkHeld = hold;
            if (hold && !_holdPrev)
            {
                // Decide once per press: online -> stream the mic; offline -> the release is the question (what-if only).
                _pressStreams = net.Online && net.BeginTalk();
            }
            else if (!hold && _holdPrev)
            {
                // Released: send it. Left the voice beats while held (hornet, reset): throw it away.
                if (_pressStreams) { if (net.Talking) net.EndTalk(cancel: !voiceBeat); }
                else if (IsActive && !net.Online) OfflineQuestion();
                _pressStreams = false;
            }
            else if (!voiceBeat && net.Talking) net.EndTalk(true);
            _holdPrev = hold;

            if (!IsActive) return;

            if (micIndicator != null)
            {
                bool live = net.Talking || (hold && !_pressStreams);
                float pulse = live ? 0.6f + 0.4f * Mathf.Sin(Time.time * 10f) : 0.25f;
                GameMaterials.Tint(micIndicator, new Color(1f, 0.25f, 0.2f, 1f) * pulse);
            }

            bool online = net.Online;
            if (online != _wasOnline && hintText != null)
            {
                _wasOnline = online;
                hintText.text = online
                    ? "Hold pinch / trigger (or T) and ask"
                    : "Hold pinch (or T), ask, let go.  F = fire, P = put it out";
            }

            if (GameInput.KeyDown(fireKey)) LocalWhatIfFire();
            if (GameInput.KeyDown(putOutKey)) LocalPutOut();

            // Offline: the show must still show the fire.
            if (!online && _promptDoneAt >= 0f)
            {
                if (!FireStarted && offlineAutoFireSeconds > 0f && Time.time - _promptDoneAt >= offlineAutoFireSeconds) LocalWhatIfFire();
                else if (FireStarted && !FireOut && offlineAutoPutOutSeconds > 0f && _fireAt >= 0f && Time.time - _fireAt >= offlineAutoPutOutSeconds) LocalPutOut();
            }
        }

        void OfflineQuestion()
        {
            if (!FireStarted) LocalWhatIfFire();
            else if (!FireOut) LocalPutOut();
        }

        static bool IsVoiceBeat(GameBeat b)
        {
            switch (b)
            {
                case GameBeat.Meet: case GameBeat.Smoker: case GameBeat.Feed: case GameBeat.Wasps: case GameBeat.Heat:
                case GameBeat.WhatIf: case GameBeat.Escape: case GameBeat.Map: case GameBeat.Field:
                    return true;
                default: return false; // pre, hook, hornet (mic belongs to the hum), t0
            }
        }

        // ------------------------------------------------------------ fire state (from the window scene)
        void HandleScene(string scene)
        {
            if (!IsActive) return;
            if (scene == GameIds.Scene.WhatIfFire) MarkFireStarted();
            else if (scene == GameIds.Scene.FireOut) MarkFireOut();
        }

        void MarkFireStarted()
        {
            if (FireStarted && !FireOut) return;
            FireStarted = true;
            FireOut = false;
            _fireAt = Time.time;
            if (beeswax != null) beeswax.WhipAround();
            if (embers != null) embers.PlayIfIdle(emberBackupDelaySeconds); // the Hive's fx embers normally beats this
        }

        void MarkFireOut()
        {
            if (embers != null) embers.Stop();
            if (!FireStarted || FireOut) return;
            FireOut = true;
            Complete(completeAfterFireOutSeconds);
        }

        // ------------------------------------------------------------ voice results
        void HandleTranscript(string text, bool final)
        {
            if (!IsActive || transcriptText == null) return;
            transcriptText.text = string.IsNullOrEmpty(text) ? "" : GameMaterials.Wrap("\"" + text + (final ? "\"" : "...\""), wrapChars);
        }

        void HandleIntent(string name, string topic, string text)
        {
            if (!IsActive || net == null) return;
            switch (name)
            {
                case GameIds.Intent.WhatIf:
                {
                    // The Hive steers Orbis itself; this only switches the local tint (and HandleScene tracks the fire).
                    string scene = GameIds.SceneForTopic(topic);
                    if (scene.Length > 0) net.RequestScene(scene, sendToHive: false);
                    break;
                }
                case GameIds.Intent.Undo:
                    if (FireStarted && !FireOut) net.RequestScene(GameIds.Scene.FireOut, sendToHive: false);
                    break;
            }
        }

        // ------------------------------------------------------------ offline / operator backup
        /// <summary>F: what the Hive would do for "What if a fire starts?", done locally (and steering the live window).</summary>
        public void LocalWhatIfFire()
        {
            if (!IsActive || net == null) return;
            net.SimulateTranscript("What if a fire starts?", true);
            net.Say(GameLines.WhatIfFire);
            net.RequestScene(GameIds.Scene.WhatIfFire, sendToHive: true);
            net.SimulateFx(GameIds.Fx.Embers, 2500);
            net.SimulateIntent(GameIds.Intent.WhatIf, "fire", "What if a fire starts?");
            MarkFireStarted(); // in case the scene was already whatif_fire
        }

        /// <summary>P: "Put it out!"</summary>
        public void LocalPutOut()
        {
            if (!IsActive || net == null) return;
            net.SimulateTranscript("Put it out!", true);
            net.Say(GameLines.FireOut);
            net.RequestScene(GameIds.Scene.FireOut, sendToHive: true);
            net.SimulateFx(GameIds.Fx.EmbersStop);
            net.SimulateIntent(GameIds.Intent.Undo, "", "Put it out!");
            if (!FireStarted) { FireStarted = true; _fireAt = Time.time; } // P without F: still finish the beat
            MarkFireOut();
        }

        protected override void OnBeatTimeout() => Complete();
    }
}
