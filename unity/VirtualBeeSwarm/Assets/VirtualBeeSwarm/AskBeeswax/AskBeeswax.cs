using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace VirtualBeeSwarm.Ask
{
    /// <summary>
    /// ASK BEESWAX (ride script v2, the AI moment), the Unity side. Installs itself into any scene with a GameDirector (like
    /// StakesDirector) and only listens to existing events; no other file changes.
    ///   Opens:   1.5 s after room_end_contract ends (tuning ask.auto, on by default), on VoiceLineStarted
    ///            room_ask_anything (if the script has that line), or desktop key Y. Map and field beats only.
    ///            Beeswax flies to the card's inner edge and shakes the Moon dust off (his own colours).
    ///   Closes:  the beat leaves map/field (the crew flies it back out), or a group reset (it vanishes).
    ///   Input:   pinch-hold with either hand while the card is up = GameNet.BeginTalk/EndTalk (T and controller B/Y keep
    ///            working through WhatIfBeat). Wait for Beeswax to finish, then hold and start with "Hey Beeswax".
    ///            A pinch aimed at a chip clicks the chip instead. A short pinch is cancelled. A chip: online its
    ///            question goes to the Hive (answered from memory, in his voice);
    ///            offline, or if the Hive stays silent for 1.5 s, the canned answer plays locally.
    ///   Card:    idle = chips; listening = red pulsing border + hex mic meter; thinking = hex sweep; answer = the
    ///            answer in dark ink where the chips were (kept 5 s after he stops).
    ///   Thinking (from release; with the card closed: on AwaitingReply in every voice beat): Look Around every 2.5 s,
    ///            a soft wing buzz (6 s max, off while the mic listens), "Checking the spreadsheet...".
    ///   Answer:  buzz off; room_whatif_refuse = No Shake + stamp_grievance; anything else = Yes Nod; when it ends,
    ///            stamp_approved and MINUTES +1 (not for the stall, not-heard, off-list, people or thanks replies).
    ///            Offline press = ask_offline. Backstops: ask_not_heard, ask_stall at 10 s.
    /// Tuning (tuning.json, StreamingAssets on desktop / persistentDataPath on the Quest): ask.on, ask.auto, ask.chips,
    /// ask.bargeIn, ask.watchdogSeconds, ask.buzz. Env: VBS_ASK=off|on|auto|manual, VBS_ASK_SELFTEST=1 (a player self-test that
    /// replaces the autopilot and quits with 0 = pass). Logs start with [VBS][Ask].
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class AskBeeswax : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static AskBeeswax Instance { get; private set; }

        [SerializeField] KeyCode openKey = KeyCode.Y;
        [SerializeField] KeyCode talkKey = KeyCode.T;
        [SerializeField] float autoOpenDelay = 1.5f;
        [SerializeField] float minPressSeconds = 0.3f;
        [SerializeField] float bargeInHoldSeconds = 0.25f;
        [SerializeField] float answerHoldSeconds = 5f;
        [SerializeField] float chipHiveWaitSeconds = 1.5f;

        public bool On { get; private set; } = true;
        public bool Auto { get; private set; } = true;
        public bool Chips { get; private set; } = true;
        public bool Buzz { get; private set; } = true;
        public AskFlow Flow { get; } = new AskFlow();
        public AskCard Card => _card;
        public bool IsOpen => Flow.IsOpen;

        GameDirector _director;
        GameNet _net;
        BeeswaxRig _rig;
        BeeswaxController _beeswax;
        AskCard _card;
        AskBuzz _buzz;
        Transform _head, _window;

        bool _subscribed, _wasTalking, _pinchPrev, _pinchTalk, _keyPrev, _autoPending, _lookSet;
        float _pressAt = -1f, _autoOpenAt = -1f, _talkAt = -1f, _answerHoldUntil = -1f;
        int _lastChipPressFrame = -1;
        bool _bargePending, _captionAway;
        Transform _away;
        AskState _lastState = AskState.Closed;
        string _lastIntent = "";
        static bool SelfTest => Env("VBS_ASK_SELFTEST") == "1";
        readonly List<string> _selfLines = new List<string>();

        // ------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<AskBeeswax>() != null) return;
            if (SelfTest)
            {
                var ap = FindAnyObjectByType<GameAutopilot>();
                if (ap != null) ap.enabled = false; // the self-test drives the beats itself
            }
            var go = new GameObject("AskBeeswax");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<AskBeeswax>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            _rig = FindAnyObjectByType<BeeswaxRig>();
            _beeswax = FindAnyObjectByType<BeeswaxController>();
            var win = FindAnyObjectByType<FutureWindow>();
            _window = win != null ? win.PaneTransform : null;
            ReadTuning();
            Flow.BargeIn = BargeIn;
            Flow.WatchdogSeconds = WatchdogSeconds;

            _card = gameObject.AddComponent<AskCard>();
            _card.Build();
            _card.ChipPressed += PressChip;
            // its own child object: Beeswax's voice AudioSource stays the only one on his body
            var buzzGo = new GameObject("AskBuzz");
            buzzGo.transform.SetParent(_beeswax != null ? _beeswax.transform : transform, false);
            _buzz = buzzGo.AddComponent<AskBuzz>();
            _buzz.Init(_net);
            Debug.Log("[VBS][Ask] installed: on=" + On + " auto=" + Auto + " chips=" + Chips + " bargeIn=" + BargeIn + " buzz=" + Buzz +
                      " watchdog=" + WatchdogSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" +
                      (_window != null ? "" : " (no window: card goes in front of the player)") + (SelfTest ? " SELFTEST" : ""));
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (_director != null)
            {
                _director.BeatChanged += HandleBeat;
                _director.GroupResetDone += HandleReset;
            }
            if (_net != null)
            {
                _net.VoiceLineStarted += HandleLine;
                _net.TranscriptReceived += HandleTranscript;
                _net.IntentReceived += HandleIntent;
                _net.VoiceInputUnavailable += HandleVoiceInputUnavailable;
                _net.VoiceWakeWait += HandleVoiceWakeWait;
                _net.VoiceWakeRejected += HandleVoiceWakeRejected;
                _net.GroupReset += HandleReset;
            }
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_director != null)
            {
                _director.BeatChanged -= HandleBeat;
                _director.GroupResetDone -= HandleReset;
            }
            if (_net != null)
            {
                _net.VoiceLineStarted -= HandleLine;
                _net.TranscriptReceived -= HandleTranscript;
                _net.IntentReceived -= HandleIntent;
                _net.VoiceInputUnavailable -= HandleVoiceInputUnavailable;
                _net.VoiceWakeWait -= HandleVoiceWakeWait;
                _net.VoiceWakeRejected -= HandleVoiceWakeRejected;
                _net.GroupReset -= HandleReset;
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            if (SelfTest) StartCoroutine(SelfTestRun());
        }

        // ------------------------------------------------------------ open / close
        static bool AskBeat(GameBeat b) => b == GameBeat.Map || b == GameBeat.Field;

        public void Open(string why)
        {
            if (!On || Flow.IsOpen || _director == null || !AskBeat(_director.Beat)) return;
            if (_head == null && Camera.main != null) _head = Camera.main.transform;
            Apply(Flow.Open());
            _card.SetMinutes(AskFlow.MinutesText(Flow.Minutes));
            var pose = AskCard.PoseFor(_head, _window);
            _card.Show(pose);
            if (_beeswax != null)
            {
                // hover at the card's inner edge (the table side), looking at the player, in his own colours: he is the
                // one you talk to now, the Moon dust comes off
                _beeswax.FlyTo(pose.position + pose.rotation * new Vector3(-(AskCard.Width * AskCard.Scale * 0.5f + 0.16f), -0.08f, -0.03f));
                _beeswax.SetDusty(false);
                _beeswax.LookAtPlayer(true);
                _lookSet = true;
            }
            Debug.Log("[VBS][Ask] card open (" + why + ") at " + pose.position.ToString("F2") + (_net != null && _net.Online ? " online" : " OFFLINE"));
        }

        public void Close(string why, bool reset = false)
        {
            if (_pinchTalk && _net != null && _net.Talking) _net.EndTalk(cancel: true);
            _pinchTalk = false;
            _pressAt = -1f;
            bool wasOpen = Flow.IsOpen;
            _bargePending = false;
            _answerHoldUntil = -1f;
            CaptionAway(false);
            Apply(Flow.Close(resetMinutes: reset));
            _buzz.Stop();
            if (wasOpen || reset) _card.Hide(fly: !reset);
            if (_lookSet && _beeswax != null) _beeswax.LookAtPlayer(false);
            _lookSet = false;
            if (wasOpen) Debug.Log("[VBS][Ask] card closed (" + why + "), minutes " + Flow.Minutes);
        }

        /// <summary>While the card shows the answer, Beeswax's own caption (the same words, floating behind the card's
        /// edge) is parked out of sight; it comes back with the chips.</summary>
        void CaptionAway(bool away)
        {
            if (away == _captionAway || _beeswax == null) return;
            _captionAway = away;
            if (away && _away == null)
            {
                _away = new GameObject("AskCaptionAway").transform;
                _away.SetParent(transform, false);
                _away.position = new Vector3(0f, -30f, 0f);
            }
            _beeswax.SetCaptionAnchor(away ? _away : null);
        }

        // ------------------------------------------------------------ events
        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            _autoPending = false;
            _autoOpenAt = -1f;
            if (Flow.IsOpen && !AskBeat(beat)) Close("beat " + GameIds.BeatName(beat));
        }

        void HandleReset()
        {
            _autoPending = false;
            _autoOpenAt = -1f;
            Close("group reset", reset: true);
        }

        void HandleLine(GameNet.VoiceLineInfo info)
        {
            if (SelfTest) _selfLines.Add(info.id);
            if (On && info.id == AskLines.AskAnythingId) Open("operator line " + info.id);
            else if (On && Auto && info.id == AskLines.EndContractId) _autoPending = true;
            bool expecting = Flow.ExpectingReply;
            // the reply is a live line (no id) or one of the ask / ack / refusal lines; any other scripted line that
            // starts meanwhile is not the answer
            string id = info.id ?? "";
            bool reply = id.Length == 0 || info.stream || id == AskLines.FillerId || id == AskLines.RefuseId ||
                         id.StartsWith("ask_", StringComparison.Ordinal) || id.StartsWith("room_ack", StringComparison.Ordinal);
            bool counts = _lastIntent != "ack" && !id.StartsWith("room_ack", StringComparison.Ordinal) && !AskLines.IsNonAnswer(id, info.caption);
            Apply(Flow.LineStarted(id, Time.time, reply, counts));
            if (expecting && Flow.IsOpen && reply)
            {
                if (Flow.State == AskState.Answering) _card.ShowAnswer(info.caption);
                Debug.Log("[VBS][Ask] reply line " + (id.Length > 0 ? id : info.stream ? "(live stream)" : "(caption)") + (counts ? "" : " (not counted)") + ": " + info.caption);
            }
        }

        void HandleTranscript(string text, bool final)
        {
            FinishDetectedTalk();
            Apply(Flow.Transcript(text, final, Time.time));
            if (final && Flow.IsOpen) Debug.Log("[VBS][Ask] heard: \"" + text + "\"");
        }

        void HandleIntent(string name, string topic, string text)
        {
            _lastIntent = name ?? "";
            Apply(Flow.Intent(name));
        }

        void HandleVoiceInputUnavailable(bool hasPermission)
        {
            Apply(Flow.MicrophoneUnavailable(hasPermission));
        }

        void HandleVoiceWakeWait() => Apply(Flow.WaitForVoice());

        void HandleVoiceWakeRejected(bool heardWords)
        {
            FinishDetectedTalk();
            Apply(Flow.WakeRejected(heardWords));
        }

        void FinishDetectedTalk()
        {
            // A fast transcript can arrive before Update notices that the mic stopped.
            if (!_wasTalking || _net == null || _net.Talking) return;
            _wasTalking = _bargePending = false;
            Apply(Flow.TalkEnded(Time.time));
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            if (_net == null || _director == null) return;
            float now = Time.time;
            bool askBeat = AskBeat(_director.Beat);

            if (_autoPending && !_net.IsSpeaking) { _autoPending = false; _autoOpenAt = now + autoOpenDelay; }
            if (_autoOpenAt >= 0f && now >= _autoOpenAt) { _autoOpenAt = -1f; Open("auto after the contract"); }
            if (On && askBeat && GameInput.KeyDown(openKey))
            {
                if (Flow.IsOpen) Close("key " + openKey);
                else Open("key " + openKey);
            }

            bool talking = _net.Talking;
            if (talking != _wasTalking)
            {
                _wasTalking = talking;
                if (talking)
                {
                    _lastIntent = "";
                    // barge-in waits for a real hold: an accidental pinch must not cut his line
                    var cue = Flow.TalkStarted(now);
                    _bargePending = (cue & AskCue.StopVoice) != 0;
                    _talkAt = now;
                    Apply(cue & ~AskCue.StopVoice);
                }
                else
                {
                    _bargePending = false;
                    Apply(Flow.TalkEnded(now));
                }
                if (Flow.IsOpen) Debug.Log(talking ? "[VBS][Ask] listening" : "[VBS][Ask] released, thinking");
            }
            if (_bargePending && talking && now - _talkAt >= bargeInHoldSeconds)
            {
                _bargePending = false;
                Apply(AskCue.StopVoice);
            }
            Apply(Flow.Tick(now, _net.AwaitingReply, _net.IsSpeaking));
            if (Flow.IsOpen && !askBeat) Close("beat");
            if (Flow.IsOpen) CardLook(now);
            _lastState = Flow.State;
        }

        /// <summary>The card's look follows the state; the answer stays on it a few seconds after he stops. No allocation.</summary>
        void CardLook(float now)
        {
            var s = Flow.State;
            if (_lastState == AskState.Answering && s == AskState.Idle) _answerHoldUntil = now + answerHoldSeconds;
            if (s == AskState.Listening || s == AskState.Thinking) _answerHoldUntil = -1f;
            bool answer = s == AskState.Answering || (s == AskState.Idle && now < _answerHoldUntil);
            if (!answer && _card.AnswerShown) _card.ShowChips();
            var look = s == AskState.Listening ? AskCardLook.Listening : s == AskState.Thinking ? AskCardLook.Thinking
                     : answer ? AskCardLook.Answer : AskCardLook.Idle;
            if (look != _card.Look) _card.SetLook(look);
            CaptionAway(_card.AnswerShown);
            _card.Animate(now, _net.VoiceInputLevel);
        }

        void LateUpdate()
        {
            // GamePointer has resolved this frame's chip hover and click before we handle a pinch as speech.
            if (_net != null && Flow.IsOpen) Presses(Time.time);
        }

        /// <summary>Pinch-hold talks while the card is up. Offline (or the desktop T key offline): the press says ask_offline.</summary>
        void Presses(float now)
        {
            bool pinch = GameHands.PinchHeld(GameHand.Left) || GameHands.PinchHeld(GameHand.Right);
            bool talkButton = GameInput.KeyHeld(talkKey) || GameHands.AnyTalkHeld();
            bool key = !_net.Online && talkButton; // online, WhatIfBeat owns the talk button
            bool held = pinch || key;
            if (held && !_pinchPrev && !_keyPrev)
            {
                bool pointerClick = pinch && (_card.AnyChipHovered || _lastChipPressFrame == Time.frameCount);
                bool otherTalk = _net.Online && (talkButton || _net.Talking);
                if (pointerClick || otherTalk) _pressAt = -1f;
                else
                {
                    _pressAt = now;
                    _pinchTalk = pinch && _net.Online && _net.BeginTalk();
                }
            }
            else if (!held && (_pinchPrev || _keyPrev) && _pressAt >= 0f)
            {
                // a pinch shorter than a word is a flicker or a click on something else: cancel it, nothing is sent
                if (_pinchTalk) { if (_net.Talking) _net.EndTalk(cancel: now - _pressAt < minPressSeconds); }
                else if (!_net.Online && now - _pressAt >= minPressSeconds) OfflinePress(now);
                _pinchTalk = false;
                _pressAt = -1f;
            }
            _pinchPrev = pinch;
            _keyPrev = key;
        }

        void OfflinePress(float now)
        {
            Debug.Log("[VBS][Ask] press while offline: " + AskLines.Offline.id);
            Apply(Flow.OfflinePress(now));
        }

        /// <summary>
        /// A chip (poke, pointer, selftest). Online: its question goes to the Hive as the log line "ask.chip ...", which
        /// answers it like a spoken question, from memory and in Beeswax's voice; if no line comes back in 1.5 s (an
        /// older Hive) the canned answer plays locally. Offline: the canned answer plays locally at once.
        /// </summary>
        public void PressChip(int i)
        {
            if (!Flow.IsOpen || !Chips || i < 0 || i >= AskLines.Chips.Length) return;
            _lastChipPressFrame = Time.frameCount;
            _net?.ResetVoiceWakeGate();
            Debug.Log("[VBS][Ask] chip " + i + ": " + AskLines.ChipQuestions[i] + (_net != null && _net.Online ? " (to the Hive)" : " (local)"));
            _lastIntent = "";
            Apply(Flow.Chip(AskLines.ChipQuestions[i], Time.time));
            if (_net == null) return;
            if (_net.Online)
            {
                _net.SendLog("ask.chip " + AskLines.ChipQuestions[i]);
                _net.SayUnlessHive(AskLines.Chips[i], chipHiveWaitSeconds);
            }
            else _net.Say(AskLines.Chips[i]);
        }

        // ------------------------------------------------------------ cues -> effects
        void Apply(AskCue cue)
        {
            if (cue == AskCue.None) return;
            if ((cue & AskCue.StopVoice) != 0 && _net != null && _net.IsSpeaking) _net.StopVoice();
            if ((cue & AskCue.BuzzOff) != 0) _buzz.Stop();
            if ((cue & AskCue.BuzzOn) != 0 && Buzz) _buzz.Play();
            if ((cue & AskCue.Tilt) != 0 && _rig != null) _rig.Play("Look Around", "ask: thinking");
            if ((cue & AskCue.Nod) != 0 && _rig != null) _rig.Play("Yes Nod", "ask: answer");
            if ((cue & AskCue.Shake) != 0 && _rig != null) _rig.Play("No Shake", "ask: refusal");
            Transform at = _card != null ? _card.Root : null;
            if ((cue & AskCue.StampGrievance) != 0) GameSound.Cue("stamp_grievance", at);
            if ((cue & AskCue.StampApproved) != 0)
            {
                GameSound.Cue("stamp_approved", at);
                _card.Punch();
                Debug.Log("[VBS][Ask] answer done: " + AskFlow.MinutesText(Flow.Minutes));
            }
            if ((cue & AskCue.Status) != 0) _card.SetStatus(Flow.Status);
            if ((cue & AskCue.Minutes) != 0) _card.SetMinutes(AskFlow.MinutesText(Flow.Minutes));
            // lines last: saying one raises VoiceLineStarted, which comes back into the flow
            if (_net == null) return;
            if ((cue & AskCue.SayOffline) != 0) _net.Say(AskLines.Offline);
            if ((cue & AskCue.SayNotHeard) != 0) { Debug.Log("[VBS][Ask] empty transcript, nothing said: " + AskLines.NotHeard.id); _net.Say(AskLines.NotHeard); }
            if ((cue & AskCue.SayStall) != 0) { Debug.Log("[VBS][Ask] no reply in " + Flow.WatchdogSeconds + " s: " + AskLines.Stall.id); _net.Say(AskLines.Stall); }
        }

        // ------------------------------------------------------------ tuning
        public bool BargeIn { get; private set; } = true;
        public float WatchdogSeconds { get; private set; } = 10f;

        void ReadTuning()
        {
            var flat = new Dictionary<string, string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            ReadFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat);
#endif
            ReadFile(Path.Combine(Application.persistentDataPath, "tuning.json"), flat);
            On = Bool(flat, "ask.on", true);
            Auto = Bool(flat, "ask.auto", true);
            Chips = Bool(flat, "ask.chips", true);
            BargeIn = Bool(flat, "ask.bargeIn", true);
            Buzz = Bool(flat, "ask.buzz", true) && Bool(flat, "sound.on", true);
            if (flat.TryGetValue("ask.watchdogSeconds", out var w) &&
                float.TryParse(w.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ws))
                WatchdogSeconds = Mathf.Clamp(ws, 3f, 20f);
            string env = Env("VBS_ASK").ToLowerInvariant();
            if (env == "off" || env == "0" || env == "false") On = false;
            else if (env == "auto") { On = true; Auto = true; }
            else if (env == "manual") { On = true; Auto = false; }
            else if (env == "on" || env == "1" || env == "true") On = true;
            if (SelfTest) On = true;
        }

        static void ReadFile(string path, Dictionary<string, string> into)
        {
            try
            {
                if (File.Exists(path)) AR.ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
            }
            catch (Exception e) { Debug.LogWarning("[VBS][Ask] tuning: could not read " + path + ": " + e.Message); }
        }

        static bool Bool(Dictionary<string, string> flat, string key, bool fallback)
        {
            if (!flat.TryGetValue(key, out var s)) return fallback;
            s = s.Trim().Trim('"').ToLowerInvariant();
            if (s == "true" || s == "on" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "off" || s == "0" || s == "no") return false;
            return fallback;
        }

        static string Env(string name)
        {
            try { return Environment.GetEnvironmentVariable(name) ?? ""; } catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------ player self-test (VBS_ASK_SELFTEST=1, Hive off)
        /// <summary>VBS_ASK_SHOT=dir (a player with graphics): a PNG of the scene at each self-test step.</summary>
        IEnumerator Shot(string name)
        {
            string dir = Env("VBS_ASK_SHOT");
            if (dir.Length == 0) yield break;
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(dir, "ask-" + name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log("[VBS][Ask] selftest shot " + path);
            yield return null;
        }

        IEnumerator SelfTestRun()
        {
            var fails = new List<string>();
            void Check(bool ok, string what) { if (!ok) fails.Add(what); Debug.Log("[VBS][Ask] selftest " + (ok ? "ok: " : "FAILED: ") + what); }
            float Wait(float s) => Time.realtimeSinceStartup + s;

            float until = Wait(3f);
            while (Time.realtimeSinceStartup < until) yield return null;
            _director.GoTo(GameBeat.Map, "ask selftest");
            until = Wait(1f);
            while (Time.realtimeSinceStartup < until) yield return null;

            Open("selftest");
            Check(Flow.IsOpen, "card open in the map beat");
            until = Wait(6f);
            while (Time.realtimeSinceStartup < until && !(_card.Visible && BeeDelivery.Instance != null && !BeeDelivery.Instance.IsFlying(_card.Root))) yield return null;
            Check(_card.Visible, "card delivered and visible");
            Check(_net == null || !_net.Online, "Hive off (offline run)");

            yield return Shot("1-open");

            _selfLines.Clear();
            _card.Press(0);
            until = Wait(1f);
            while (Time.realtimeSinceStartup < until && !_selfLines.Contains(AskLines.Bananas.id)) yield return null;
            Check(_selfLines.Contains(AskLines.Bananas.id), "chip 0 plays " + AskLines.Bananas.id + " locally");
            yield return Shot("2-chip");
            until = Wait(15f);
            while (Time.realtimeSinceStartup < until && Flow.Minutes < 1) yield return null;
            Check(Flow.Minutes == 1, "answer ended: stamp + minutes 1 (" + Flow.Minutes + ")");

            _selfLines.Clear();
            OfflinePress(Time.time);
            until = Wait(1f);
            while (Time.realtimeSinceStartup < until && !_selfLines.Contains(AskLines.Offline.id)) yield return null;
            Check(_selfLines.Contains(AskLines.Offline.id), "offline press says " + AskLines.Offline.id);
            until = Wait(10f);
            while (Time.realtimeSinceStartup < until && Flow.State != AskState.Idle) yield return null;
            Check(Flow.Minutes == 1, "offline line doesn't count as a question");

            // thinking feedback: a release with a reply pending (the backstop then says ask_stall)
            _selfLines.Clear();
            Flow.WatchdogSeconds = 2f;
            Apply(Flow.TalkStarted(Time.time));
            Apply(Flow.TalkEnded(Time.time));
            Check(Flow.Thinking && (!Buzz || _buzz.Playing), "release starts thinking (tilt + buzz)");
            yield return Shot("3-thinking");
            until = Wait(4f);
            while (Time.realtimeSinceStartup < until && Flow.State == AskState.Thinking && Flow.ExpectingReply)
            {
                Apply(Flow.Tick(Time.time, true, false)); // pretend the Hive is still working on it
                yield return null;
            }
            until = Wait(1f);
            while (Time.realtimeSinceStartup < until && !_selfLines.Contains(AskLines.Stall.id)) yield return null;
            Check(_selfLines.Contains(AskLines.Stall.id), "no reply: backstop says " + AskLines.Stall.id);
            Flow.WatchdogSeconds = WatchdogSeconds;

            _director.ResetToPre();
            until = Wait(1f);
            while (Time.realtimeSinceStartup < until) yield return null;
            Check(!Flow.IsOpen && !_card.Visible, "reset closes the card");

            bool pass = fails.Count == 0;
            Debug.Log("[VBS][Ask] selftest " + (pass ? "PASS" : "FAIL: " + string.Join("; ", fails)));
#if !UNITY_EDITOR
            Application.Quit(pass ? 0 : 1);
#endif
        }
    }
}
