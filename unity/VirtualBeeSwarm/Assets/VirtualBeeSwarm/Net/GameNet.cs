// GameNet: the ONLY file in the gameplay layer that touches unity/BeeswaxClient (namespace Beeswax).
// Written against the protocol v2 client (BeeswaxBus, BeeswaxStations, BeeswaxVoiceInput, BeeswaxRoomVoice,
// HumDetector, OrbisWindow) as of 2026-11-14 01:07 PT. If the client API changes, fix it here; the rest of
// VirtualBeeSwarm/ only sees the events and methods below (game types and plain strings).
//
// Offline rules (Hive unreachable, no bus at all, or forceOffline / key O):
//   * Unity's beat clock never waits for the network (GameDirector's timers).
//   * Station choices: BeeswaxStations plays the outcome locally (line + window clip + fx) when the Hive can't;
//     with forceOffline or without BeeswaxStations, the stations play GameLines locally.
//   * The window: offline, every scene switch also switches the clip (StreamingAssets/<scene>.mp4) locally.
//   * Lines: beat lines play at once (the Hive never voices beat changes). Only a station outcome without
//     BeeswaxStations uses SayUnlessHive (wait briefly for the Hive's own room line, else play the local one).
//   * Desktop/operator simulations (F/P keys) are injected locally (bus.InjectLocal) so every listener reacts.
//   * v2.1 choice.request (the Board's operator twin of a choice) comes through the client's typed
//     BeeswaxBus.OnChoiceRequest (client 0.2.1+; keep BeeswaxStations.applyChoiceRequests OFF, or the client applies
//     it too). fx.force needs nothing here: the Hive forwards it as a plain fx, and the v2.1 client turns a stray
//     fx.force into OnFx itself.
//
// Beeswax's voice: pre-rendered clips are Resources/BeeswaxLines/<line id> (imported by Editor/Voice/BeeswaxLinesImporter:
// mono, Vorbis, Decompress On Load, preload, background load; 22050 Hz on Android). BeeswaxRoomVoice plays them by id.
// PreloadVoice (called by the intro) loads all of them during pre/hook and warms the voice's clip cache, so starting a
// line never hits Resources.Load. Every line Beeswax starts is logged as "[VBS] voice CLIP|CAPTION-ONLY|STREAM ..." and
// raised as VoiceLineStarted (the autopilot counts them).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VirtualBeeSwarm
{
    [DefaultExecutionOrder(-50)]
    public class GameNet : MonoBehaviour
    {
        public static GameNet Instance { get; private set; }

        [Header("BeeswaxClient components (left empty = found at runtime)")]
        [SerializeField] Beeswax.BeeswaxBus bus;
        [SerializeField] Beeswax.BeeswaxStations stations;
        [SerializeField] Beeswax.BeeswaxVoiceInput voiceInput;
        [SerializeField] Beeswax.BeeswaxRoomVoice voice;
        [SerializeField] Beeswax.HumDetector hum;
        [SerializeField] Beeswax.OrbisWindow orbis;

        [Header("Offline / fallback")]
        [Tooltip("Pretend the Hive is unreachable (desktop key O toggles it).")]
        [SerializeField] bool forceOffline;
        [Tooltip("Online: wait this long for the Hive's own room line before playing the local one.")]
        [SerializeField] float hiveLineWaitSeconds = 1.2f;
        [Tooltip("Caption-only lines (no BeeswaxRoomVoice in the scene) stay up this long per character.")]
        [SerializeField] float captionSecondsPerChar = 0.065f;
        [SerializeField] float captionMinSeconds = 1.8f;
        [Tooltip("Flap Beeswax's mouth while a caption-only line (no audio clip yet) is up.")]
        [SerializeField] bool fakeMouthWhenSilent = true;
        [SerializeField] bool logEvents = true;
        [Tooltip("Log every line Beeswax starts: CLIP (with length, rate, preloaded or not), CAPTION-ONLY or STREAM.")]
        [SerializeField] bool logVoiceLines = true;
        [Tooltip("PreloadVoice waits at most this long (real seconds) for clips still decoding in the background.")]
        [SerializeField] float preloadDecodeTimeoutSeconds = 15f;
        [SerializeField] KeyCode toggleOfflineKey = KeyCode.O;

        /// <summary>The Resources folder of the pre-rendered lines (BeeswaxRoomVoice.resourcesFolder; the builder sets it).</summary>
        public const string VoiceFolder = "BeeswaxLines";

        /// <summary>One line Beeswax started.</summary>
        public readonly struct VoiceLineInfo
        {
            /// <summary>The line id ("" for a caption the Hive sent without one).</summary>
            public readonly string id;
            public readonly string caption;
            /// <summary>A pre-rendered clip plays (false: caption only, or a live stream).</summary>
            public readonly bool hasClip;
            /// <summary>A live TTS line streamed by the Hive.</summary>
            public readonly bool stream;
            /// <summary>The clip was loaded by PreloadVoice before the line started (no Resources.Load at line start).</summary>
            public readonly bool preloaded;
            public readonly float clipSeconds;
            public VoiceLineInfo(string id, string caption, bool hasClip, bool stream, bool preloaded, float clipSeconds)
            {
                this.id = id;
                this.caption = caption;
                this.hasClip = hasClip;
                this.stream = stream;
                this.preloaded = preloaded;
                this.clipSeconds = clipSeconds;
            }
        }

        // ---------------------------------------------------------------- events (main thread, game types only)
        public event Action<bool> ConnectionChanged;               // online?
        public event Action HiveWelcomed;                           // first welcome on each connection
        public event Action<int, bool> BeatRequested;              // (n, or -1 with next = true)
        public event Action GroupReset;
        public event Action<string, bool> TranscriptReceived;      // (text, final)
        public event Action<string, string, string> IntentReceived; // (name, topic or "", text)
        public event Action<bool> VoiceInputUnavailable;          // microphone permission granted?
        public event Action VoiceWakeWait;
        public event Action VoiceDialogueOpened;
        public event Action<bool> VoiceWakeRejected;              // was anything heard?
        public event Action<string, int, int> FxReceived;          // (name, delay_ms or -1, duration_ms or -1)
        public event Action<string> SceneChanged;                  // window scene (local request, or the Hive's orbis{scene})
        public event Action<float> OperatorHeat;                   // hum.set from the Board (0..1)
        public event Action<Texture, bool> OrbisTextureChanged;    // (texture or null, live?)
        public event Action<string> LineStarted;                   // caption
        public event Action<VoiceLineInfo> VoiceLineStarted;       // the same line: id, clip or caption only, preloaded
        /// <summary>Board operator twin of a player's choice (protocol v2.1 choice.request). Already validated.</summary>
        public event Action<GameStation, string> ChoiceRequested;
        /// <summary>Every station pick, however it was made (prop, button, key, Board): (station, choice, correct).</summary>
        public event Action<GameStation, string, bool> ChoiceMade;

        // ---------------------------------------------------------------- state
        public bool Online => !forceOffline && _bound != null && _bound.IsConnected;
        public bool ForceOffline
        {
            get => forceOffline;
            set
            {
                forceOffline = value;
                if (value)
                {
                    voiceInput?.CancelPendingTurn();
                    ResetVoiceWakeGate();
                }
            }
        }
        public string CurrentScene { get; private set; } = "";
        public bool IsSpeaking => voice != null ? voice.IsSpeaking : Time.time < _localCaptionUntil;
        public string CurrentCaption => voice != null ? voice.CurrentCaption : (Time.time < _localCaptionUntil ? _localCaption : "");
        /// <summary>0..1 mouth/scale driver: BeeswaxRoomVoice.Amplitude, or a fake flap for caption-only lines.</summary>
        public float VoiceAmplitude { get; private set; }
        public float HumHeat => hum != null ? hum.Heat : _operatorHeat;
        public float HumLevel => hum != null ? hum.Level : 0f;
        public bool HumVoiced => hum != null && hum.Voiced;
        public bool HasHumDetector => hum != null;
        public Texture OrbisTexture => orbis != null ? orbis.CurrentTexture : null;
        public bool OrbisIsLive => orbis != null && orbis.IsLive;
        public bool HasVoiceInput => voiceInput != null;
        public bool Talking => voiceInput != null && voiceInput.IsTalking;
        public float VoiceInputLevel => voiceInput != null ? voiceInput.Level : 0f;
        public bool AskFollowUpOpen => _askQuestionsLeft > 0 && Time.realtimeSinceStartup <= _askFollowUpUntil;
        /// <summary>The mic is taking the player's voice (push-to-talk / VAD) or the hornet's hum: game sound stays silent.</summary>
        public bool MicListening => (voiceInput != null && (voiceInput.IsTalking || voiceInput.IsDucking)) || (hum != null && hum.Listening);
        /// <summary>Between the end of an utterance and its intent, including reconnect delivery.</summary>
        public bool AwaitingReply => voiceInput != null && (voiceInput.Delivering || voiceInput.AwaitingReply);
        public string HostName => _bound != null ? _bound.CurrentHost : "";
        /// <summary>PreloadVoice has finished (or there is no BeeswaxRoomVoice, so nothing to load).</summary>
        public bool VoicePreloadDone { get; private set; }
        /// <summary>Clips PreloadVoice found in Resources/BeeswaxLines.</summary>
        public int VoiceClipsPreloaded => _preloaded.Count;

        /// <summary>Length of a pre-rendered line PreloadVoice loaded (0 when it has no clip or isn't loaded yet).</summary>
        public float ClipSeconds(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)) return 0f;
            foreach (var c in _voiceClips)
                if (c != null && c.name == lineId) return c.length;
            return 0f;
        }

        Beeswax.BeeswaxBus _bound;
        Beeswax.BeeswaxRoomVoice _boundVoice;
        AudioSource _voiceSource;
        readonly HashSet<string> _preloaded = new HashSet<string>();
        readonly List<AudioClip> _voiceClips = new List<AudioClip>(); // keeps the preloaded clips resident
        Coroutine _preloadRoutine;
        Beeswax.OrbisWindow _boundOrbis;
        bool _wasOnline;
        float _operatorHeat;
        string _localCaption = "";
        float _localCaptionUntil = -1f;
        float _lastHiveLineAt = -100f;
        GameLine _pendingLine;
        bool _hasPending;
        float _pendingCreatedAt, _pendingDeadline;
        string _lastLineKey = "";
        bool _lineHasAudio;

        // ---------------------------------------------------------------- lifecycle
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS] a second GameNet was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
        }

        void Start()
        {
            ResolveComponents();
            Bind();
            _wasOnline = Online;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Unbind();
        }

        void OnDisable()
        {
            // Coroutines die with the object; let a later PreloadVoice start over.
            if (_preloadRoutine != null) StopCoroutine(_preloadRoutine);
            _preloadRoutine = null;
        }

        void Update()
        {
            if (GameInput.KeyDown(toggleOfflineKey))
            {
                ForceOffline = !forceOffline;
                Debug.Log("[VBS] force offline = " + forceOffline);
            }

            Bind();

            bool online = Online;
            if (online != _wasOnline)
            {
                _wasOnline = online;
                if (!online) _askQuestionsLeft = 0; // the Hive discarded the old socket's dialogue
                if (logEvents) Debug.Log("[VBS] Hive " + (online ? "online (" + HostName + ")" : "offline: local script + timers"));
                try { ConnectionChanged?.Invoke(online); } catch (Exception e) { Debug.LogException(e); }
            }

            if (_hasPending && Time.time >= _pendingDeadline)
            {
                _hasPending = false;
                if (_lastHiveLineAt < _pendingCreatedAt) Say(_pendingLine);
            }

            UpdateAmplitude();
        }

        void ResolveComponents()
        {
            if (bus == null) bus = Beeswax.BeeswaxBus.Instance;
            if (stations == null) stations = FindAnyObjectByType<Beeswax.BeeswaxStations>();
            if (voiceInput == null) voiceInput = FindAnyObjectByType<Beeswax.BeeswaxVoiceInput>();
            if (voice == null) voice = FindAnyObjectByType<Beeswax.BeeswaxRoomVoice>();
            if (hum == null) hum = FindAnyObjectByType<Beeswax.HumDetector>();
            if (orbis == null) orbis = FindAnyObjectByType<Beeswax.OrbisWindow>();
        }

        void Bind()
        {
            var b = bus != null ? bus : Beeswax.BeeswaxBus.Instance;
            if (b != _bound)
            {
                Unbind();
                _bound = b;
                if (b != null)
                {
                    b.OnWelcome += HandleWelcome;
                    b.OnBeatRequest += HandleBeatRequest;
                    b.OnGroupReset += HandleGroupReset;
                    b.OnSay += HandleSay;
                    b.OnTtsStart += HandleTtsStart;
                    b.OnHumSet += HandleHumSet;
                    b.OnTranscript += HandleTranscript;
                    b.OnIntent += HandleIntent;
                    b.OnFx += HandleFx;
                    b.OnChoiceRequest += HandleChoiceRequest;
                }
            }
            if (voice != _boundVoice)
            {
                if (_boundVoice != null)
                {
                    _boundVoice.OnLineStarted -= HandleLineStarted;
                    _boundVoice.CanSpeakAskCaption = null;
                    _boundVoice.PreloadedAnswerId = null;
                }
                _boundVoice = voice;
                _voiceSource = voice != null ? voice.GetComponent<AudioSource>() : null;
                if (voice != null)
                {
                    voice.OnLineStarted += HandleLineStarted;
                    voice.CanSpeakAskCaption = ShouldSpeakDeviceCaption;
                    voice.PreloadedAnswerId = PreloadedAnswerId;
                }
            }
            if (orbis != _boundOrbis)
            {
                if (_boundOrbis != null)
                {
                    _boundOrbis.OnTextureChanged -= HandleOrbisTexture;
                    _boundOrbis.OnSceneChanged -= HandleOrbisScene;
                }
                _boundOrbis = orbis;
                if (orbis != null)
                {
                    orbis.OnTextureChanged += HandleOrbisTexture;
                    orbis.OnSceneChanged += HandleOrbisScene;
                }
            }
        }

        void Unbind()
        {
            if (_bound != null)
            {
                _bound.OnWelcome -= HandleWelcome;
                _bound.OnBeatRequest -= HandleBeatRequest;
                _bound.OnGroupReset -= HandleGroupReset;
                _bound.OnSay -= HandleSay;
                _bound.OnTtsStart -= HandleTtsStart;
                _bound.OnHumSet -= HandleHumSet;
                _bound.OnTranscript -= HandleTranscript;
                _bound.OnIntent -= HandleIntent;
                _bound.OnFx -= HandleFx;
                _bound.OnChoiceRequest -= HandleChoiceRequest;
            }
            _bound = null;
            if (_boundVoice != null)
            {
                _boundVoice.OnLineStarted -= HandleLineStarted;
                _boundVoice.CanSpeakAskCaption = null;
                _boundVoice.PreloadedAnswerId = null;
            }
            _boundVoice = null;
            if (_boundOrbis != null)
            {
                _boundOrbis.OnTextureChanged -= HandleOrbisTexture;
                _boundOrbis.OnSceneChanged -= HandleOrbisScene;
            }
            _boundOrbis = null;
        }

        // ---------------------------------------------------------------- inbound (bus events; ignored while forceOffline)
        void HandleWelcome(Beeswax.Welcome _)
        {
            if (!forceOffline) HiveWelcomed?.Invoke();
        }

        void HandleBeatRequest(Beeswax.BeatRequest r)
        {
            if (forceOffline) return;
            int n = r.HasN ? r.n : -1;
            if (logEvents) Debug.Log("[VBS] <- beat.request " + (n >= 0 ? n.ToString() : "next"));
            BeatRequested?.Invoke(n, n < 0);
        }

        void HandleGroupReset(Beeswax.GroupReset g)
        {
            EndVoiceDialogue();
            if (forceOffline) return;
            voiceInput?.CancelPendingTurn();
            _hasPending = false;
            _localCaptionUntil = -1f;
            if (logEvents) Debug.Log("[VBS] <- group.reset");
            GroupReset?.Invoke();
        }

        void HandleSay(Beeswax.Say s)
        {
            if (s.channel != Beeswax.Channel.Room) return;
            _lastHiveLineAt = Time.time;
            _hasPending = false; // the Hive spoke: drop the local fallback line
        }

        void HandleTtsStart(Beeswax.TtsStart t)
        {
            _lastHiveLineAt = Time.time;
            _hasPending = false;
        }

        void HandleHumSet(Beeswax.HumSet h)
        {
            if (forceOffline) return;
            _operatorHeat = Mathf.Clamp01(h.value);
            OperatorHeat?.Invoke(_operatorHeat);
        }

        void HandleTranscript(Beeswax.Transcript t)
        {
            if (forceOffline) return;
            if (_askUtterance && t.final && t.source == Beeswax.Role.Quest)
            {
                bool requireWake = _wakeUtterance;
                _askUtterance = _wakeUtterance = false;
                bool wake = Ask.AskFlow.TryWakePhrase(t.text, out var question);
                bool legacyHive = string.IsNullOrEmpty(t.wake_status);
                if (t.wake_status == "opened" || (legacyHive && wake && question.Length == 0))
                {
                    _askQuestionsLeft = MaxAskQuestions;
                    _askFollowUpUntil = Time.realtimeSinceStartup + AskFollowUpSeconds;
                    DiscardRemoteAskReply();
                    VoiceDialogueOpened?.Invoke();
                    Say(GameLines.RoomAckAllow);
                    return;
                }
                if (t.wake_status == "rejected" || (legacyHive && ((!wake && requireWake) || string.IsNullOrWhiteSpace(t.text))))
                {
                    // Older Hives may still answer a rejected turn. Ignore those replies until the next deliberate input.
                    EndVoiceDialogue();
                    DiscardRemoteAskReply();
                    VoiceWakeRejected?.Invoke(!string.IsNullOrWhiteSpace(t.text));
                    return;
                }
                if (wake) _askQuestionsLeft = MaxAskQuestions;
                _askQuestionsLeft = Mathf.Max(0, _askQuestionsLeft - 1);
                _askFollowUpUntil = Time.realtimeSinceStartup + AskFollowUpSeconds;
                string accepted = wake ? question : t.text;
                TranscriptReceived?.Invoke(accepted, true);
                TryShowAskClip(accepted);
                return;
            }
            TranscriptReceived?.Invoke(t.text, t.final);
        }

        static readonly Regex AskShowRequest = new Regex(
            @"^(?:(?:can|could|would|will) you[ ,]+|please[ ,]+|now[ ,]+)*show (?:me|us)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        void TryShowAskClip(string question)
        {
            if (_lastBeat != GameBeat.Map || orbis == null || string.IsNullOrWhiteSpace(question) ||
                !AskShowRequest.IsMatch(question.Trim())) return;
            string scene = AskClipScene(question);
            if (!orbis.PreviewClip(scene, 8f)) return;
            var line = GameLines.RoomPalShowMe;
            if (voice != null)
            {
                // The preview answered this turn. Drop the legacy server's union-break line or duplicate pointer.
                voice.SuppressAskReplies = true;
                voice.SuppressNextFromBus(line.id);
                _deviceAskReplyPending = true;
                voice.Play(line.id, line.caption, allowDeviceSpeech: true);
            }
            else Say(line);
            Debug.Log("[VBS][Ask] window preview " + scene);
        }

        static string AskClipScene(string text)
        {
            if (Regex.IsMatch(text, @"\b(?:moon|earth from space)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.Moon;
            if (Regex.IsMatch(text, @"\b(?:snow|winter|ice|frost)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.WhatIfWinter;
            if (Regex.IsMatch(text, @"\b(?:rain|storm|downpour)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.WhatIfRain;
            if (Regex.IsMatch(text, @"\b(?:fire|wildfire|burning)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.WhatIfFire;
            if (Regex.IsMatch(text, @"\b(?:pesticide|spray|spraying)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.WhatIfSpray;
            if (Regex.IsMatch(text, @"\b(?:wasp|wasps|hornet|hornets)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.WaspsRaid;
            if (Regex.IsMatch(text, @"\b(?:flower|flowers|garden|lavender|pollination)\b", RegexOptions.IgnoreCase)) return GameIds.Scene.WhatIfFlowers;
            return GameIds.Scene.FullBloom;
        }

        void HandleIntent(Beeswax.Intent i)
        {
            if (forceOffline || (voice != null && voice.SuppressAskReplies)) return;
            _deviceAskReplyPending = i.name == GameIds.Intent.Ask && (_lastBeat == GameBeat.Map || _lastBeat == GameBeat.Field);
            if (logEvents) Debug.Log("[VBS] <- intent " + i.name + " " + i.topic);
            IntentReceived?.Invoke(i.name, i.topic, i.text);
        }

        void HandleFx(Beeswax.Fx f)
        {
            if (forceOffline) return;
            if (logEvents) Debug.Log("[VBS] <- fx " + f.name + (f.HasDelay ? " delay " + f.delay_ms : ""));
            FxReceived?.Invoke(f.name, f.delay_ms, f.duration_ms);
        }

        // v2.1 choice.request (the client already checked the pair against CHOICES; checked again against ours).
        void HandleChoiceRequest(Beeswax.ChoiceRequest r)
        {
            if (forceOffline || r == null) return;
            if (!GameIds.TryParseStation(r.station, out var st) || !GameIds.IsValidChoice(st, r.choice))
            {
                Debug.LogWarning("[VBS] ignored choice.request " + r.station + "/" + r.choice);
                return;
            }
            if (logEvents) Debug.Log("[VBS] <- choice.request " + r.station + "/" + r.choice);
            ChoiceRequested?.Invoke(st, r.choice);
        }

        void HandleLineStarted(string caption)
        {
            var id = voice != null ? voice.CurrentLineId : "";
            if (voice != null && (voice.CurrentTtsId.Length > 0 || id.StartsWith("ask_", StringComparison.Ordinal)))
                _deviceAskReplyPending = false;
            if (id == GameLines.RoomMeetHeyBeeswax.id && voiceInput != null) voiceInput.CancelPendingTurn();
            ReportLine(id, voice != null ? voice.CurrentTtsId : "", caption);
            LineStarted?.Invoke(caption);
        }

        void HandleOrbisTexture(Texture tex, bool live) => OrbisTextureChanged?.Invoke(tex, live);

        void HandleOrbisScene(string scene)
        {
            if (!string.IsNullOrEmpty(scene) && scene != CurrentScene) SetSceneLocal(scene);
        }

        // ---------------------------------------------------------------- outbound
        GameBeat? _lastBeat;
        bool _askUtterance, _wakeUtterance;
        bool _deviceAskReplyPending;

        static string PreloadedAnswerId(string caption)
        {
            foreach (var answer in Ask.AskLines.Chips)
                if (string.Equals(answer.caption, caption, StringComparison.Ordinal)) return answer.id;
            return null;
        }

        // The Quest's on-device speech engine is a different voice from Beeswax's recorded and live ElevenLabs voice.
        // dartiros wants one consistent voice, so it's opt-in now: VBS_DEVICE_SPEECH=1 turns it back on. Off, a failed
        // live reply shows its caption, and the Board says which service failed.
        static readonly bool DeviceSpeechFallback = ReadDeviceSpeechSwitch();

        static bool ReadDeviceSpeechSwitch()
        {
            try { return System.Environment.GetEnvironmentVariable("VBS_DEVICE_SPEECH") == "1"; }
            catch (System.Exception) { return false; }
        }

        bool ShouldSpeakDeviceCaption(string caption)
        {
            if (!DeviceSpeechFallback) { _deviceAskReplyPending = false; return false; }
            if (!_deviceAskReplyPending) return false;
            _deviceAskReplyPending = false;
            return !string.IsNullOrWhiteSpace(caption) && !Ask.AskLines.IsNonAnswer("", caption);
        }

        const int MaxAskQuestions = 3;
        const float AskFollowUpSeconds = 20f;
        int _askQuestionsLeft;
        float _askFollowUpUntil;
        /// <summary>Unity is the only beat clock: report every beat change. The bus re-sends it after a reconnect.</summary>
        public void SendBeat(GameBeat beat, int? scoreSeconds = null)
        {
            if (_lastBeat != beat)
            {
                voiceInput?.CancelPendingTurn();
                EndVoiceDialogue();
            }
            _lastBeat = beat;
            if (logEvents) Debug.Log("[VBS] beat " + (int)beat + " " + GameIds.BeatName(beat) + (Online ? "" : " (offline)"));
            if (forceOffline || _bound == null) return;
            _bound.SendBeatSet((int)beat, scoreSeconds);
        }

        /// <summary>
        /// A station choice. Sends choice.made (through BeeswaxStations, which also plays the outcome locally when the
        /// Hive is down or silent) and switches the local window tint. Returns true when the client takes care of
        /// Beeswax's outcome line; false = the caller plays GameLines' line (forceOffline / no BeeswaxStations).
        /// </summary>
        public bool MakeChoice(GameStation station, string choice, string localScene)
        {
            string s = GameIds.StationName(station);
            bool correct = GameIds.IsCorrect(station, choice);
            if (logEvents) Debug.Log("[VBS] choice " + s + "/" + choice + (correct ? " (correct)" : " (wrong)"));
            try { ChoiceMade?.Invoke(station, choice, correct); } catch (Exception e) { Debug.LogException(e); }
            if (!string.IsNullOrEmpty(localScene))
            {
                SetSceneLocal(localScene);
                // Forced offline nobody else moves the window's clip (BeeswaxStations isn't asked): switch it here.
                if (forceOffline && orbis != null) orbis.ShowSceneLocally(localScene);
            }
            if (forceOffline) return false;
            if (stations != null) return stations.Choose(s, choice) != null;
            if (_bound != null) _bound.SendChoiceMade(s, choice); // queued by the bus while offline
            return false;
        }

        /// <summary>
        /// Show a window scene. sendToHive = true: the client's OrbisWindow switches its clip now and asks the Hive to
        /// steer Orbis there (queued while offline). false: the local fallback tint, and while the Hive can't drive the
        /// window (offline) the clip too (online the Hive drives it itself, e.g. after choice.made or a voice intent).
        /// </summary>
        public void RequestScene(string scene, bool sendToHive)
        {
            if (string.IsNullOrEmpty(scene)) return;
            SetSceneLocal(scene);
            if (orbis == null) return;
            if (sendToHive)
            {
                if (forceOffline) orbis.ShowSceneLocally(scene);
                else orbis.RequestScene(scene);
            }
            else if (!Online) orbis.ShowSceneLocally(scene);
        }

        void SetSceneLocal(string scene)
        {
            if (scene == CurrentScene) return;
            CurrentScene = scene;
            if (logEvents) Debug.Log("[VBS] window scene " + scene);
            try { SceneChanged?.Invoke(scene); } catch (Exception e) { Debug.LogException(e); }
        }

        public void SendHornetCooked(bool overshoot)
        {
            if (logEvents) Debug.Log("[VBS] hornet cooked, overshoot=" + overshoot);
            if (forceOffline || _bound == null) return;
            _bound.SendHornetCooked(overshoot); // queued by the bus while offline
        }

        public void SendLog(string text)
        {
            if (Online) _bound.SendLog(text);
        }

        // ---------------------------------------------------------------- Beeswax's voice
        /// <summary>Play a line now from Beeswax (clip Resources/BeeswaxLines/&lt;id&gt; or caption only).</summary>
        public void Say(GameLine line) => Say(line.id, line.caption);

        public void Say(string lineId, string caption)
        {
            if (voice != null)
            {
                if (!string.IsNullOrEmpty(lineId)) voice.SuppressNextFromBus(lineId); // the Hive's copy of it won't double up
                voice.Play(lineId, caption);
                return;
            }
            _localCaption = caption ?? "";
            _localCaptionUntil = Time.time + Mathf.Max(captionMinSeconds, _localCaption.Length * captionSecondsPerChar);
            ReportLine(lineId ?? "", "", _localCaption);
            LineStarted?.Invoke(_localCaption);
        }

        /// <summary>
        /// Loads every pre-rendered line (GameLines.ClipIds, lines.json order, so pre/hook/meet come first) from
        /// Resources/BeeswaxLines and warms BeeswaxRoomVoice's clip cache, so starting a line never hits Resources.Load.
        /// The clips import with Preload Audio Data + Load In Background + Decompress On Load, so decoding stays off the
        /// main thread. The intro calls it when pre (or hook) starts; later calls do nothing. Without a BeeswaxRoomVoice
        /// (caption-only scene) there is nothing to load.
        /// </summary>
        public void PreloadVoice()
        {
            if (VoicePreloadDone || _preloadRoutine != null || !isActiveAndEnabled) return;
            if (voice == null) voice = FindAnyObjectByType<Beeswax.BeeswaxRoomVoice>();
            if (voice == null)
            {
                VoicePreloadDone = true;
                if (logVoiceLines) Debug.Log("[VBS] voice preload: no BeeswaxRoomVoice in the scene, captions only");
                return;
            }
            _preloadRoutine = StartCoroutine(PreloadVoiceRoutine());
        }

        IEnumerator PreloadVoiceRoutine()
        {
            float t0 = Time.realtimeSinceStartup;
            var ids = GameLines.ClipIds();
            int scriptLineCount = ids.Length;
            Array.Resize(ref ids, scriptLineCount + Ask.AskLines.Chips.Length);
            for (int i = 0; i < Ask.AskLines.Chips.Length; i++)
                ids[scriptLineCount + i] = Ask.AskLines.Chips[i].id;
            var requests = new ResourceRequest[ids.Length];
            for (int i = 0; i < ids.Length; i++) requests[i] = Resources.LoadAsync<AudioClip>(VoiceFolder + "/" + ids[i]);
            var missing = new List<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                while (!requests[i].isDone) yield return null;
                var clip = requests[i].asset as AudioClip;
                if (clip == null) { missing.Add(ids[i]); continue; }
                _voiceClips.Add(clip);
                if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                // The asset is in memory now, so this Resources.Load inside the voice is a lookup, and its cache keeps it.
                if (voice != null && voice.HasClip(ids[i])) _preloaded.Add(ids[i]);
            }
            float until = Time.realtimeSinceStartup + preloadDecodeTimeoutSeconds;
            while (Time.realtimeSinceStartup < until && AnyClipDecoding()) yield return null;
            // Do the PCM envelope work here, one bundled answer per frame, rather than at first playback.
            for (int i = scriptLineCount; i < ids.Length; i++)
            {
                var clip = requests[i].asset as AudioClip;
                if (voice != null) voice.WarmEnvelope(clip);
                yield return null;
            }
            int loaded = 0, failed = 0, pending = 0;
            foreach (var c in _voiceClips)
            {
                if (c.loadState == AudioDataLoadState.Loaded) loaded++;
                else if (c.loadState == AudioDataLoadState.Failed) failed++;
                else pending++;
            }
            VoicePreloadDone = true;
            _preloadRoutine = null;
            Debug.Log("[VBS] voice preload: " + _preloaded.Count + "/" + ids.Length + " clips from Resources/" + VoiceFolder + " in " +
                      ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms (audio data: " + loaded + " loaded, " + failed +
                      " failed, " + pending + " not loaded" + (pending + failed > 0 && loaded == 0 ? ": Unity audio is off in this player" : "") + ")" +
                      (missing.Count > 0 ? "; no clip, caption only: " + string.Join(", ", missing) : ""));
        }

        bool AnyClipDecoding()
        {
            foreach (var c in _voiceClips)
                if (c != null && c.loadState == AudioDataLoadState.Loading) return true;
            return false;
        }

        void ReportLine(string id, string ttsId, string caption)
        {
            bool stream = !string.IsNullOrEmpty(ttsId);
            // Play(lineId) already looked the clip up, so HasClip is a cache hit here.
            bool hasClip = !stream && voice != null && !string.IsNullOrEmpty(id) && voice.HasClip(id);
            AudioClip clip = hasClip && _voiceSource != null ? _voiceSource.clip : null;
            bool preloaded = hasClip && _preloaded.Contains(id);
            var info = new VoiceLineInfo(id ?? "", caption ?? "", hasClip, stream, preloaded, clip != null ? clip.length : 0f);
            if (logVoiceLines)
            {
                string what;
                if (stream) what = "STREAM tts:" + ttsId;
                else if (hasClip)
                    what = "CLIP " + id + (clip != null ? " " + clip.length.ToString("0.00") + "s " + clip.frequency + "Hz " + clip.channels + "ch" : "") +
                           (preloaded ? " preloaded" : " NOT preloaded");
                else what = "CAPTION-ONLY " + (string.IsNullOrEmpty(id) ? "(no line id)" : id + (voice != null ? " (no clip in Resources/" + VoiceFolder + ")" : " (no BeeswaxRoomVoice)"));
                Debug.Log("[VBS] voice " + what + ": " + caption);
            }
            try { VoiceLineStarted?.Invoke(info); } catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>
        /// Online: wait briefly for the Hive's own room line (its reaction to what we just sent) and play the local
        /// line only if none came. Offline: play it now.
        /// </summary>
        public void SayUnlessHive(GameLine line, float waitSeconds = -1f)
        {
            if (!Online)
            {
                Say(line);
                return;
            }
            if (_hasPending) Say(_pendingLine); // don't swallow the previous fallback
            _pendingLine = line;
            _hasPending = true;
            _pendingCreatedAt = Time.time;
            _pendingDeadline = Time.time + (waitSeconds >= 0f ? waitSeconds : hiveLineWaitSeconds);
        }

        public void StopVoice()
        {
            _hasPending = false;
            _localCaptionUntil = -1f;
            if (voice != null) voice.StopAll();
        }

        void UpdateAmplitude()
        {
            float amp = 0f;
            bool speaking = IsSpeaking;
            if (voice != null)
            {
                amp = voice.Amplitude;
                string key = voice.CurrentLineId;
                if (!ReferenceEquals(key, _lastLineKey))
                {
                    _lastLineKey = key;
                    _lineHasAudio = voice.CurrentTtsId.Length > 0 || (key.Length > 0 && voice.HasClip(key));
                }
            }
            else _lineHasAudio = false;

            if (fakeMouthWhenSilent && speaking && !_lineHasAudio)
            {
                float t = Time.time;
                amp = Mathf.Clamp01(0.35f + 0.35f * Mathf.Sin(t * 17f) * Mathf.Sin(t * 4.3f + 1f));
            }
            VoiceAmplitude = amp;
        }

        // ---------------------------------------------------------------- push-to-talk (BeeswaxVoiceInput)
        /// <summary>
        /// Start streaming the Quest mic to the Hive (the client keeps the audio through a short reconnect).
        /// False when forced offline, without BeeswaxVoiceInput, or without mic permission yet.
        /// </summary>
        public bool BeginTalk()
        {
            if (Talking) return true;
            if (forceOffline || voiceInput == null || _bound == null) return false;
            bool ask = _lastBeat == GameBeat.Map || _lastBeat == GameBeat.Field;
            if (ask && voice != null && voice.IsSpeakingDeviceFallback) voice.StopAll();
            bool wake = ask && !AskFollowUpOpen;
            // Protect the instructional phrase even though it plays before the card is available.
            if ((ask && (IsSpeaking || AwaitingReply)) ||
                (voice != null && voice.CurrentLineId == GameLines.RoomMeetHeyBeeswax.id))
            {
                if (ask) VoiceWakeWait?.Invoke();
                return false;
            }
            ResetVoiceWakeGate();
            bool ok = voiceInput.BeginTalk(wake, ask);
            _askUtterance = ok && ask;
            _wakeUtterance = ok && wake;
            if (ok && logEvents) Debug.Log("[VBS] talk start");
            if (!ok) VoiceInputUnavailable?.Invoke(Beeswax.BeeswaxMic.HasPermission);
            return ok;
        }

        public void ResetVoiceWakeGate()
        {
            _askUtterance = _wakeUtterance = false;
            _deviceAskReplyPending = false;
            if (voice != null) voice.SuppressAskReplies = false;
        }

        public void EndVoiceDialogue()
        {
            _askQuestionsLeft = 0;
            _askFollowUpUntil = 0f;
            ResetVoiceWakeGate();
            orbis?.ClearClipPreview();
            if (voice != null && voice.IsSpeakingDeviceFallback) voice.StopAll();
        }

        void DiscardRemoteAskReply()
        {
            voiceInput?.CancelPendingTurn();
            if (voice != null) voice.SuppressAskReplies = true;
        }

        public void EndTalk(bool cancel = false)
        {
            if (voiceInput == null || !voiceInput.IsTalking) return;
            if (cancel) voiceInput.Cancel();
            else voiceInput.EndTalk();
            if (logEvents) Debug.Log("[VBS] talk " + (cancel ? "cancel" : "end"));
        }

        // ---------------------------------------------------------------- hum (hornet)
        public void SetHumListening(bool on)
        {
            if (hum != null) hum.Listening = on;
        }

        public void WarmUpHum()
        {
            if (hum != null) hum.WarmUp();
        }

        public void ResetHum()
        {
            _operatorHeat = 0f;
            if (hum != null) hum.ResetHum();
        }

        public void CalibrateHum()
        {
            if (hum != null && hum.isActiveAndEnabled) hum.CalibrateNoiseFloor();
        }

        // ---------------------------------------------------------------- local simulation (desktop keys / operator backup)
        // Injected through the bus (when there is one and we're not forced offline) so the client's own components
        // react exactly as if the Hive had sent it.
        bool CanInject => !forceOffline && _bound != null;

        public void SimulateIntent(string name, string topic, string text)
        {
            if (logEvents) Debug.Log("[VBS] (local) intent " + name + " " + topic);
            if (CanInject) _bound.InjectLocal(new Beeswax.Intent { name = name ?? "", topic = topic ?? "", text = text ?? "", path = Beeswax.IntentPath.Keyword });
            else IntentReceived?.Invoke(name ?? "", topic ?? "", text ?? "");
        }

        public void SimulateFx(string name, int delayMs = -1, int durationMs = -1)
        {
            if (logEvents) Debug.Log("[VBS] (local) fx " + name);
            if (CanInject) _bound.InjectLocal(new Beeswax.Fx { name = name ?? "", delay_ms = delayMs, duration_ms = durationMs });
            else FxReceived?.Invoke(name ?? "", delayMs, durationMs);
        }

        public void SimulateTranscript(string text, bool final)
        {
            if (CanInject) _bound.InjectLocal(new Beeswax.Transcript { text = text ?? "", final = final, source = Beeswax.Role.Quest });
            else TranscriptReceived?.Invoke(text ?? "", final);
        }

        // ---------------------------------------------------------------- scene builder helper (editor time)
        /// <summary>
        /// Adds the BeeswaxClient components the gameplay uses and wires them to this adapter and to each other.
        /// Called by Editor/BuildGameScene so that no other gameplay file references the client.
        /// clientRoot must be a scene ROOT object (BeeswaxBus persists across scenes). voiceHost = Beeswax's body
        /// (BeeswaxRoomVoice + AudioSource), orbisHost = the portal (OrbisWindow).
        /// offline = true builds a scene that never touches the network or the microphone (tests): no HumDetector, no
        /// BeeswaxVoiceInput, bus/window don't connect, and this adapter is forced offline.
        /// </summary>
        public void EnsureClientComponents(GameObject clientRoot, GameObject voiceHost, GameObject orbisHost, bool offline = false)
        {
            if (clientRoot != null)
            {
                bus = GetOrAdd<Beeswax.BeeswaxBus>(clientRoot);
                stations = GetOrAdd<Beeswax.BeeswaxStations>(clientRoot);
                if (!offline)
                {
                    hum = GetOrAdd<Beeswax.HumDetector>(clientRoot);
                    voiceInput = GetOrAdd<Beeswax.BeeswaxVoiceInput>(clientRoot);
                }
                GetOrAdd<Beeswax.LatencyReporter>(clientRoot);
            }
            if (voiceHost != null)
            {
                GetOrAdd<AudioSource>(voiceHost);
                voice = GetOrAdd<Beeswax.BeeswaxRoomVoice>(voiceHost);
#if UNITY_EDITOR
                SetSerialized(voice, "resourcesFolder", VoiceFolder); // its default; pinned so PreloadVoice and the voice agree
#endif
            }
            if (orbisHost != null) orbis = GetOrAdd<Beeswax.OrbisWindow>(orbisHost);
            forceOffline = offline;
#if UNITY_EDITOR
            // "Now HUM!" / Beeswax's own lines must not count as the player's voice.
            if (hum != null && voice != null) SetSerialized(hum, "ignoreWhileSpeaking", voice);
            if (voiceInput != null && voice != null) SetSerialized(voiceInput, "ignoreWhileSpeaking", voice);
            // Offline outcomes need the voice and the window.
            if (stations != null)
            {
                if (voice != null) SetSerialized(stations, "voice", voice);
                if (orbis != null) SetSerialized(stations, "window", orbis);
            }
            if (voice != null) SeedCaptions(voice);
            if (bus != null) SetSerialized(bus, "connectOnStart", !offline);
            if (orbis != null) SetSerialized(orbis, "connectOnStart", !offline);
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Adds every GameLines id + caption to BeeswaxRoomVoice's line list (clip left empty), so lines played by
        /// id without a caption (BeeswaxStations' offline outcomes: stations.json has no text) still show one.
        /// Existing entries (e.g. with a clip a teammate dropped in) are kept.
        /// </summary>
        static void SeedCaptions(Beeswax.BeeswaxRoomVoice v)
        {
            var so = new UnityEditor.SerializedObject(v);
            var list = so.FindProperty("lines");
            if (list == null || !list.isArray)
            {
                Debug.LogWarning("[VBS] BeeswaxRoomVoice.lines not found (client API changed?): captions not seeded");
                return;
            }
            var have = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var id = list.GetArrayElementAtIndex(i).FindPropertyRelative("id");
                if (id != null) have.Add(id.stringValue);
            }
            foreach (var line in GameLines.All())
            {
                if (string.IsNullOrEmpty(line.id) || !have.Add(line.id)) continue;
                int n = list.arraySize;
                list.arraySize = n + 1;
                var e = list.GetArrayElementAtIndex(n);
                e.FindPropertyRelative("id").stringValue = line.id;
                e.FindPropertyRelative("caption").stringValue = line.caption;
                e.FindPropertyRelative("clip").objectReferenceValue = null;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetSerialized(UnityEngine.Object target, string field, object value)
        {
            var so = new UnityEditor.SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogWarning("[VBS] " + target.GetType().Name + "." + field + " not found (client API changed?)");
                return;
            }
            if (value is bool b) p.boolValue = b;
            else if (value is string s) p.stringValue = s;
            else p.objectReferenceValue = value as UnityEngine.Object;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
#endif

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }
    }
}
