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
    /// Ride script v2: room_whatif_prompt then room_whatif_notfire ("Just not fire."); 8 s of silence -> room_hint_whatif
    /// (once); the fire burning 4 s after its line with no "put it out" -> room_whatif_putout (once per fire); after
    /// the fire is out (+ optional #2 room_whatif_smoke_why), after another what-if + 5 s, or after 25 s of silence ->
    /// room_adjourn ("All in favor?"), and the escape starts when it ends (the swarm's roar).
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
        [Tooltip("If the Hive's fx 'embers' hasn't come this long after the fire started, start the embers anyway.")]
        [SerializeField] float emberBackupDelaySeconds = 4f;
        [Tooltip("Offline only: no fire this long after the prompt -> Beeswax starts it himself (0 = never).")]
        [SerializeField] float offlineFireAfterSeconds = 13f; // v2: after the 8 s hint
        [Tooltip("Offline only: the fire burns this long -> it gets put out (0 = never).")]
        [SerializeField] float offlinePutOutAfterSeconds = 14f; // v2: after the 4 s put-out hint
        [Header("v2 voice beat")]
        [SerializeField] float hintAfterSilenceSeconds = 8f;
        [SerializeField] float putOutHintSeconds = 4f;
        [SerializeField] float adjournAfterWhatIfSeconds = 5f;
        [SerializeField] float adjournAfterSilenceSeconds = 25f;
        [SerializeField] int wrapChars = 36;

        public bool FireStarted { get; private set; }
        public bool FireOut { get; private set; }
        /// <summary>room_whatif_prompt and room_whatif_notfire are done (the player's turn).</summary>
        public bool PromptDone => _promptDoneAt >= 0f;
        /// <summary>room_adjourn has been said (the escape follows).</summary>
        public bool Adjourned { get; private set; }
        /// <summary>v2 ASK BEESWAX: a pinch also talks while the card is up (map beat).</summary>
        public static bool AskOpen { get; set; }
        /// <summary>The talk input is held in a voice beat, online or offline (the game sound stays silent meanwhile).</summary>
        public static bool TalkHeld { get; private set; }

        bool _wasOnline = true;
        bool _holdPrev;         // talk input held last frame (edge detection: one BeginTalk per press)
        bool _pressStreams;     // this press went to the Hive (online); false = offline press, acted on at release
        float _promptDoneAt = -1f, _fireAt = -1f;
        float _quietSince = -1f, _fireLineEndAt = -1f, _otherWhatIfAt = -1f;
        bool _hinted, _putOutHinted, _adjourning;

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
            ResetV2();
            SetActive(ui, true);
            if (promptText != null) promptText.text = "Ask me anything.\n\"What if...?\"";
            if (transcriptText != null) transcriptText.text = "";
            _wasOnline = !(net != null && net.Online); // force a hint refresh
            if (beeswax != null) beeswax.GoPresent();
            StartCoroutine(Intro());
            // Someone asked about fire in an earlier voice beat: it's already burning, so "put it out" finishes this one.
            if (net != null && net.CurrentScene == GameIds.Scene.WhatIfFire) MarkFireStarted();
        }

        void ResetV2()
        {
            _quietSince = _fireLineEndAt = _otherWhatIfAt = -1f;
            _hinted = _putOutHinted = _adjourning = false;
            Adjourned = false;
        }

        IEnumerator Intro()
        {
            yield return SayAndWait(GameLines.WhatIfPrompt, 1.5f, LineTime.Seconds(net, GameLines.WhatIfPrompt) + 1f);
            if (!FireStarted)
            {
                var notFire = RideLines.WhatIfNotFire; // reverse psychology: "Just not fire."
                yield return SayAndWait(notFire, 1f, LineTime.Seconds(net, notFire) + 1f);
            }
            _promptDoneAt = Time.time;
            _quietSince = Time.time;
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
            ResetV2();
            SetActive(ui, false);
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            if (net == null || director == null) return;
            GameBeat beat = director.Beat;
            bool voiceBeat = IsActive || (talkInAllVoiceBeats && IsVoiceBeat(beat));

            bool hold = voiceBeat && (GameInput.KeyHeld(talkKey) || GameHands.AnyTalkHeld() || ((IsActive || AskOpen) && GameHands.AnyPinchHeld()));
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

            UpdateV2(hold);

            // Offline: the show must still show the fire.
            if (!online && _promptDoneAt >= 0f && !_adjourning)
            {
                if (!FireStarted && offlineFireAfterSeconds > 0f && Time.time - _promptDoneAt >= offlineFireAfterSeconds) LocalWhatIfFire();
                else if (FireStarted && !FireOut && offlinePutOutAfterSeconds > 0f && _fireAt >= 0f && Time.time - _fireAt >= offlinePutOutAfterSeconds) LocalPutOut();
            }
        }

        /// <summary>v2: the hint, the put-out hint and the adjourn.</summary>
        void UpdateV2(bool holding)
        {
            if (_promptDoneAt < 0f || _adjourning) return;
            float now = Time.time;
            bool speaking = net.IsSpeaking || net.AwaitingReply;
            if (speaking || holding || net.Talking) _quietSince = -1f;
            else if (_quietSince < 0f) _quietSince = now;
            float quiet = _quietSince >= 0f ? now - _quietSince : 0f;

            if (!FireStarted && !_hinted && quiet >= hintAfterSilenceSeconds)
            {
                _hinted = true;
                RidePacing.NoteStall("what-if: " + hintAfterSilenceSeconds.ToString("0") + " s of silence");
                net.Say(RideLines.HintWhatIf);
                _quietSince = -1f;
                return;
            }
            if (FireStarted && !FireOut)
            {
                // the fire line has ended once Beeswax goes quiet after it started
                if (_fireLineEndAt < 0f && !speaking && now - _fireAt > 1.5f) _fireLineEndAt = now;
                if (!_putOutHinted && _fireLineEndAt >= 0f && now - _fireLineEndAt >= putOutHintSeconds && !speaking && !holding)
                {
                    _putOutHinted = true;
                    if (beeswax != null) beeswax.WhipAround();
                    net.Say(RideLines.WhatIfPutOut);
                }
                return;
            }
            if (FireOut) return; // MarkFireOut adjourns
            if (_otherWhatIfAt >= 0f && speaking) _otherWhatIfAt = now; // count the 5 s from the end of its line
            if (_otherWhatIfAt >= 0f && !speaking && now - _otherWhatIfAt >= adjournAfterWhatIfSeconds) StartAdjourn("another what-if + " + adjournAfterWhatIfSeconds.ToString("0") + " s");
            else if (quiet >= adjournAfterSilenceSeconds) StartAdjourn(adjournAfterSilenceSeconds.ToString("0") + " s of silence");
        }

        void StartAdjourn(string why)
        {
            if (_adjourning || !IsActive) return;
            _adjourning = true;
            Debug.Log("[VBS][Ride] what-if: adjourn (" + why + ")");
            StartCoroutine(Adjourn(why == "fire out"));
        }

        IEnumerator Adjourn(bool afterFire)
        {
            yield return WaitForTalk(afterFire ? 2f : 0.2f, 9f); // room_whatif_out (the Hive's or ours) ends
            if (afterFire)
            {
                var why = RideLines.WhatIfSmokeWhy; // optional #2
                if (RidePacing.Optional(2, why)) yield return SayAndWait(why, 1f, LineTime.Seconds(net, why) + 1f);
            }
            var adjourn = RideLines.Adjourn;
            yield return SayAndWait(adjourn, 1f, LineTime.Seconds(net, adjourn) + 1f);
            Adjourned = true;
            Complete(0.3f); // "All in favor?" -> the swarm roars yes (escape)
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
            _fireLineEndAt = -1f;
            _putOutHinted = false;
            if (beeswax != null) beeswax.WhipAround();
            if (embers != null) embers.PlayIfIdle(emberBackupDelaySeconds); // the Hive's fx embers normally beats this
        }

        void MarkFireOut()
        {
            if (embers != null) embers.Stop();
            if (!FireStarted || FireOut) return;
            FireOut = true;
            StartAdjourn("fire out"); // v2: room_whatif_out (+ optional #2) -> room_adjourn -> escape
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
                    if (topic != "fire") _otherWhatIfAt = Time.time; // v2: another what-if, then adjourn 5 s after its line
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

        protected override void OnBeatTimeout()
        {
            if (_adjourning) return; // room_adjourn is wrapping up (the director's grace covers it)
            StartAdjourn("beat max");
        }
    }
}
