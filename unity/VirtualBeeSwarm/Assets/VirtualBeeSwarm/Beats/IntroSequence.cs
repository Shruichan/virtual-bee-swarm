using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// pre (fitting: place the hive; START), hook (the threat and the rocket) and meet (Beeswax flies to his spot).
    /// Entering pre also puts every world object back to its start state, so group.reset / Shift+R give a clean run.
    ///
    /// v3.1 with deliveries on/swirl (V14):
    ///   pre   a crew of 4 bees parks the SIGN HERE clipboard 0.45 m ahead of the head, 0.1 m below eye level (again
    ///         whenever the headset is put back on). Poke / pinch / click it, or N / Space, to start. Nobody signs
    ///         within 6 s of it landing: room_pre, once. Beeswax waits inside the hive (hidden, caption at the clipboard).
    ///   hook  the clipboard is stamped APPROVED and flown out; 20 bees cut the window ring, the window opens, the hive
    ///         leaves on 6 bee chains and Beeswax starts the threat from inside it (the dark swarm pours on "angry
    ///         swarms", StakesDirector), the hive lands, Beeswax squeezes out of the door, shakes off and flies up to his
    ///         mark; the rocket is flown in on room_hook_2. Deliveries overlap the lines, so the hook runs about as
    ///         long as with deliveries off; no line is ever cut (each one holds for its clip).
    /// delivery.mode off: today's START button, the hive and rocket in place, Beeswax on the rocket perch.
    /// Forecast (stakes.forecast, both modes): the window opens on a peaceful Paris (ForecastMoment) and room_hook_1 waits
    /// until that has been fully open peacefulMinSeconds by the time "angry swarms" is said; on those words the window
    /// cuts to the ravaged world (a giant laser-eyed bee topples the Eiffel Tower) as the dark swarm pours. With deliveries
    /// off the window now opens at the start of the hook instead of in meet.
    /// </summary>
    public class IntroSequence : BeatBehaviour
    {
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] FutureWindow window;
        [SerializeField] SwarmSystem swarm;
        [SerializeField] HexagonPanel hexagons;
        [SerializeField] HiveBody hive;
        [SerializeField] EmbersFx embers;
        [Tooltip("Shown during pre: instructions + the START button.")]
        [SerializeField] GameObject preUi;
        [SerializeField] ChoiceButton startButton;
        [Tooltip("Where Beeswax sits on HONEYMOON-1 during pre/hook (delivery off).")]
        [SerializeField] Transform rocketPerch;
        [Tooltip("v3.1: rings the window, flies the hive in on bee chains and the rocket in nose-up. Empty = today's hook.")]
        [SerializeField] BeeDelivery delivery;
        [Tooltip("HONEYMOON-1 (empty: rocketPerch's parent).")]
        [SerializeField] Transform rocket;

        [Header("SIGN HERE (V14, deliveries on/swirl)")]
        [Tooltip("Empty: found in the scene, else built at runtime from primitives.")]
        [SerializeField] SignHereClipboard signHere;
        [SerializeField] float clipboardDelay = 0.6f;
        [SerializeField] float clipboardAhead = 0.45f;
        [SerializeField] float clipboardBelowEye = 0.1f;
        [Tooltip("Top leans away from the player (degrees).")]
        [SerializeField] float clipboardTilt = 15f;
        [SerializeField] int clipboardCrew = 4;
        [Tooltip("room_pre plays once when nobody signs this long after the clipboard lands (and the headset is on).")]
        [SerializeField] float nudgeAfterSeconds = 6f;
        [Tooltip("The APPROVED stamp shows this long before the crew flies the clipboard out.")]
        [SerializeField] float stampHoldSeconds = 0.45f;

        [Header("Hook timing (V14, seconds from the start of the hook)")]
        [SerializeField] float windowOpenAt = 0.6f;
        [Tooltip("The hive leaves the window before the ring cut (2 s) is quite closed.")]
        [SerializeField] float hiveAwayAt = 1.4f;
        [Tooltip("Beeswax starts the threat from inside the flying hive (not before hiveAwayAt).")]
        [SerializeField] float threatLineAt = 1.4f;
        [Tooltip("Where Beeswax flies after climbing out (empty: his home point).")]
        [SerializeField] Transform hookMark;
        [SerializeField] bool logHook = true;

        [Header("Forecast (room_hook_1: peaceful Paris, cut to the ravaged one on \"angry swarms\")")]
        [Tooltip("Empty: found in the scene, else added to the window at runtime (then without SFX).")]
        [SerializeField] ForecastMoment forecast;
        [Tooltip("The peaceful forecast is fully open at least this long before the cut (room_hook_1 starts late enough).")]
        [SerializeField] float peacefulMinSeconds = 3f;

        bool Delivering => delivery != null && BeeDelivery.Mode != DeliveryMode.Off;
        bool Forecasting => forecast != null && window != null && StakesFlags.Forecast;
        bool UseClipboard => Delivering && signHere != null;
        Transform Rocket => rocket != null ? rocket : rocketPerch != null ? rocketPerch.parent : null;
        Transform HookMark => hookMark != null ? hookMark : beeswax != null ? beeswax.HomePoint : null;
        GameDirector _beats;

        // pre
        float _clipParkedAt = -1f, _preStartedAt, _presenceAt = -1f;
        bool _nudged, _stamped;
        int _presence = -1; // -1 unknown (desktop, no sensor), 0 off, 1 on
        TextMesh _instructions;
        string _instructionsText;

        // hook
        float _hookStartedAt = -1f, _threatAt = -1f, _hook2At = -1f, _hiveLandedAt = -1f, _outAt = -1f, _windowAt = -1f;
        bool _hiveLanded;
        string _heardId;
        float _heardAt, _heardClip;

        public override bool Handles(GameBeat beat) => beat == GameBeat.Pre || beat == GameBeat.Hook || beat == GameBeat.Meet;

        /// <summary>The clipboard is parked in front of the player and takes a signature.</summary>
        public bool SignHereReady => IsActive && ActiveBeat == GameBeat.Pre && signHere != null && signHere.Interactable;
        /// <summary>Is the clipboard (not the START button) the way to start this run?</summary>
        public bool SignHereShown => UseClipboard && IsActive && ActiveBeat == GameBeat.Pre;
        /// <summary>Sign it as if poked (autopilot, Board). False if it isn't ready.</summary>
        public bool SignHereNow(string how) => SignHereReady && signHere.Sign(how);
        /// <summary>Seconds the last hook beat took (-1 before one finished).</summary>
        public float LastHookSeconds { get; private set; } = -1f;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (hexagons == null) hexagons = FindAnyObjectByType<HexagonPanel>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            if (embers == null) embers = FindAnyObjectByType<EmbersFx>();
            if (delivery == null) delivery = FindAnyObjectByType<BeeDelivery>();
            if (signHere == null) signHere = FindAnyObjectByType<SignHereClipboard>(FindObjectsInactive.Include);
            if (forecast == null) forecast = FindAnyObjectByType<ForecastMoment>();
            if (forecast == null && window != null) forecast = window.gameObject.AddComponent<ForecastMoment>();
            if (signHere == null && delivery != null)
            {
                // the scene builder didn't make one (art slot SignHere dresses the builder's placeholder): primitives
                Transform parent = delivery.TableAnchor != null ? delivery.TableAnchor : hive != null ? hive.transform.parent : transform;
                signHere = SignHereClipboard.Build(parent);
            }
            if (preUi != null)
                foreach (var tm in preUi.GetComponentsInChildren<TextMesh>(true))
                    if (tm.name == "Instructions") { _instructions = tm; _instructionsText = tm.text; break; }
        }

        void Start()
        {
            _beats = director != null ? director : GameDirector.Instance;
            if (_beats != null) _beats.BeatChanged += HandleBeatChanged;
        }

        void OnDestroy()
        {
            if (_beats != null) _beats.BeatChanged -= HandleBeatChanged;
        }

        void OnEnable()
        {
            if (startButton != null) startButton.Pressed += HandleStart;
            if (signHere != null) signHere.Signed += HandleSigned;
            if (net != null) net.VoiceLineStarted += HandleVoiceLine;
        }

        void OnDisable()
        {
            if (startButton != null) startButton.Pressed -= HandleStart;
            if (signHere != null) signHere.Signed -= HandleSigned;
            if (net != null) net.VoiceLineStarted -= HandleVoiceLine;
        }

        // A Board jump / skip past the hook: the hive and the rocket are simply there, the clipboard is gone and Beeswax
        // is out (no flight holds a beat).
        void HandleBeatChanged(GameBeat prev, GameBeat beat)
        {
            if (beat <= GameBeat.Hook || delivery == null) return;
            if (signHere != null && signHere.gameObject.activeSelf) { signHere.Arm(false); delivery.Release(signHere.transform, hide: true); }
            if (hive != null) delivery.EnsureHome(hive.transform, DeliveryStyle.Festoon, 0, HiveLanded);
            if (Rocket != null) delivery.EnsureHome(Rocket, DeliveryStyle.Crown, 12);
            if (beeswax != null)
            {
                beeswax.SetCaptionAnchor(null);
                beeswax.EmergeNow(HookMark);
            }
        }

        void HiveLanded()
        {
            if (swarm != null && swarm.Mode == SwarmMode.Hidden)
            {
                swarm.Scatter();
                swarm.SetMode(SwarmMode.Idle);
            }
        }

        void HandleVoiceLine(GameNet.VoiceLineInfo info)
        {
            _heardId = info.id;
            _heardAt = Time.time;
            _heardClip = info.hasClip ? info.clipSeconds : 0f;
        }

        void HandleStart(ChoiceButton b)
        {
            if (IsActive && ActiveBeat == GameBeat.Pre && director != null) director.Next("start button");
        }

        void HandleSigned(SignHereClipboard clip, string how)
        {
            if (!IsActive || ActiveBeat != GameBeat.Pre || director == null) return;
            Log("SIGN HERE signed (" + how + ") " + (Time.time - _preStartedAt).ToString("0.0") + " s into pre" +
                (_clipParkedAt >= 0f ? ", " + (Time.time - _clipParkedAt).ToString("0.0") + " s after it landed" : "") +
                (_nudged ? ", after the room_pre nudge" : ""));
            clip.Arm(false);
            if (delivery != null) { delivery.Stamp(clip.transform, true); _stamped = true; }
            director.Next("sign here");
        }

        protected override void OnEnter(GameBeat beat)
        {
            switch (beat)
            {
                case GameBeat.Pre:
                    if (net != null) net.PreloadVoice(); // every clip loads while the hive is being placed (S8.2)
                    ResetWorld();
                    SetActive(preUi, true);
                    ShowStartPath();
                    _preStartedAt = Time.time;
                    if (UseClipboard) StartCoroutine(PreClipboard());
                    break;
                case GameBeat.Hook:
                    if (net != null) net.PreloadVoice(); // no-op once done; covers a jump straight to hook
                    SetActive(preUi, false);
                    if (Forecasting) forecast.Prepare();
                    StartCoroutine(Hook());
                    break;
                case GameBeat.Meet:
                    StartCoroutine(Meet());
                    break;
            }
        }

        protected override void OnExit(GameBeat beat)
        {
            if (beat == GameBeat.Pre)
            {
                SetActive(preUi, false);
                if (signHere != null) signHere.Arm(false);
                if (beeswax != null) beeswax.SetCaptionAnchor(null);
            }
            if (beat == GameBeat.Hook && _hookStartedAt >= 0f)
            {
                LastHookSeconds = Time.time - _hookStartedAt;
                Log("hook beat " + LastHookSeconds.ToString("0.00") + " s (delivery " + BeeDelivery.ModeName(BeeDelivery.Mode) +
                    (delivery == null ? ", no BeeDelivery" : "") + "): threat line +" + Fmt(_threatAt) + ", room_hook_2 +" + Fmt(_hook2At) +
                    (Delivering ? ", hive landed +" + Fmt(_hiveLandedAt) + ", Beeswax out +" + Fmt(_outAt) : "") +
                    (_windowAt >= 0f ? ", window open +" + Fmt(_windowAt) : "") +
                    (forecast != null && forecast.Summary.Length > 0 ? " | " + forecast.Summary : ""));
                _hookStartedAt = -1f;
            }
            if (beat == GameBeat.Meet && window != null && !window.IsOpen) window.Open(); // skipped ahead: still open it
        }

        protected override void OnResetState()
        {
            SetActive(preUi, false);
            if (signHere != null) signHere.Arm(false);
        }

        void ShowStartPath()
        {
            bool clip = UseClipboard;
            if (startButton != null)
            {
                SetActive(startButton.gameObject, !clip);
                startButton.SetInteractable(true);
            }
            if (_instructions != null)
                _instructions.text = clip && _instructionsText != null ? _instructionsText.Replace("Then press START.", "Then sign the clipboard.") : _instructionsText;
        }

        void ResetWorld()
        {
            if (hexagons != null) hexagons.ResetAll();
            if (hive != null) hive.ResetHive();
            if (window != null) window.ResetWindow();
            if (forecast != null)
            {
                forecast.ResetForecast();
                if (Forecasting) forecast.Prepare(); // the clips load while the hive is being placed
            }
            if (swarm != null) swarm.ResetSwarm();
            if (embers != null) embers.ResetFx();
            _clipParkedAt = -1f;
            _nudged = _stamped = false;
            _presence = -1;
            _presenceAt = -1f;
            if (signHere != null)
            {
                signHere.Arm(false);
                if (delivery != null) delivery.Release(signHere.transform, hide: true);
                else SetActive(signHere.gameObject, false);
            }
            if (beeswax != null)
            {
                beeswax.ResetBeeswax();
                if (rocketPerch != null) beeswax.AttachTo(rocketPerch);
            }
            if (delivery == null) return;
            if (Delivering)
            {
                // v3.1: nothing is here until the bees bring it; Beeswax waits inside the hive
                if (hive != null) delivery.SnapHome(hive.transform, hide: true);
                if (Rocket != null) delivery.SnapHome(Rocket, hide: true);
                if (swarm != null) swarm.SetMode(SwarmMode.Hidden);
                if (beeswax != null && hive != null) beeswax.HideInside(hive.DoorPoint);
            }
            else
            {
                if (hive != null) { delivery.SnapHome(hive.transform, hide: false); hive.gameObject.SetActive(true); }
                if (Rocket != null) { delivery.SnapHome(Rocket, hide: false); Rocket.gameObject.SetActive(true); }
            }
        }

        // ------------------------------------------------------------ pre: SIGN HERE
        IEnumerator PreClipboard()
        {
            yield return Wait(clipboardDelay); // also lets a group reset's CancelAll pass first
            _presence = ReadPresence();
            PlaceClipboard("pre");
            while (true)
            {
                var t = signHere.transform;
                if (_clipParkedAt < 0f && t.gameObject.activeInHierarchy && !delivery.IsFlying(t))
                {
                    _clipParkedAt = Time.time;
                    signHere.Arm(true);
                    Log("SIGN HERE parked " + (Time.time - _preStartedAt).ToString("0.0") + " s into pre (crew " +
                        clipboardCrew + ", " + Vector3.Distance(t.position, HeadPosition()).ToString("0.00") + " m from the head)");
                }
                if (Time.time >= _presenceAt)
                {
                    _presenceAt = Time.time + 0.25f;
                    int p = ReadPresence();
                    if (p == 1 && _presence == 0) PlaceClipboard("headset on"); // OVRManager.HMDMounted, without the Meta SDK
                    _presence = p;
                }
                if (!_nudged && _clipParkedAt >= 0f && Time.time - _clipParkedAt >= nudgeAfterSeconds && _presence != 0 &&
                    (net == null || !net.IsSpeaking))
                {
                    _nudged = true;
                    Log("room_pre nudge: nobody signed " + nudgeAfterSeconds.ToString("0") + " s after the clipboard landed");
                    if (net != null) net.Say(GameLines.PreStart);
                }
                yield return null;
            }
        }

        void PlaceClipboard(string why)
        {
            if (signHere == null || delivery == null) return;
            Transform head = delivery.Head;
            Vector3 eye = head != null ? head.position : transform.position + Vector3.up * 1.3f;
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            float h = SignHereClipboard.Height * Mathf.Abs(signHere.transform.lossyScale.y);
            // board centre 0.45 m ahead and 0.1 m below the eye; pivot = bottom edge centre
            Vector3 centre = eye + fwd * clipboardAhead - Vector3.up * clipboardBelowEye;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(clipboardTilt, 0f, 0f);
            Vector3 pivot = centre - rot * Vector3.up * (h * 0.5f);
            signHere.Arm(false);
            _clipParkedAt = -1f;
            delivery.Park(signHere.transform, new Pose(pivot, rot), clipboardCrew);
            if (beeswax != null) beeswax.SetCaptionAnchor(signHere.transform, h + 0.05f);
            Log("SIGN HERE on its way (" + why + ")");
        }

        Vector3 HeadPosition()
        {
            Transform head = delivery != null ? delivery.Head : null;
            return head != null ? head.position : transform.position;
        }

        /// <summary>1 = headset on, 0 = off, -1 = unknown (desktop, no proximity sensor reported).</summary>
        static int ReadPresence()
        {
            var dev = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!dev.isValid) return -1;
            return dev.TryGetFeatureValue(CommonUsages.userPresence, out bool on) ? (on ? 1 : 0) : -1;
        }

        IEnumerator ClipboardAway()
        {
            var t = signHere.transform;
            if (!_stamped) { delivery.Stamp(t, true); _stamped = true; } // started by N / Space / the Board
            yield return Wait(stampHoldSeconds);
            delivery.Return(t);
        }

        // ------------------------------------------------------------ hook
        IEnumerator Hook()
        {
            _hookStartedAt = Time.time;
            _threatAt = _hook2At = _hiveLandedAt = _outAt = _windowAt = -1f;
            bool delivering = Delivering && hive != null;
            bool flyHive = delivering && !IsDelivered(hive.transform);
            Log("start (delivery " + BeeDelivery.ModeName(BeeDelivery.Mode) + (Forecasting ? ", forecast" : "") + ")");
            if (UseClipboard && signHere.gameObject.activeSelf) StartCoroutine(ClipboardAway());

            if (flyHive)
            {
                // 20 bees cut the ring; the window opens; the hive leaves on 6 bee chains before the ring is quite
                // closed, and Beeswax starts the threat from inside it while it flies (4.5 s)
                _hiveLanded = false;
                delivery.CutRing(() => Log("ring cut done"));
                Log("ring cut");
                yield return UntilHook(windowOpenAt);
                OpenWindow(threatLineAt);
                yield return UntilHook(hiveAwayAt);
                delivery.DeliverHome(hive.transform, DeliveryStyle.Festoon, 0, HiveLandedInHook);
                Log("hive away");
                yield return UntilHook(ThreatLineAt(threatLineAt));
            }
            else if (delivering)
            {
                // came back to the hook with the hive already here: just the lines (and Beeswax out if he isn't)
                OpenWindow(0f);
                _hiveLanded = true;
                if (beeswax != null && beeswax.Inside) ClimbOut();
                yield return UntilHook(ThreatLineAt(0f));
            }
            else
            {
                if (beeswax != null)
                {
                    if (rocketPerch != null) beeswax.AttachTo(rocketPerch);
                    beeswax.WhipAround();
                }
                if (swarm != null) swarm.SetMode(SwarmMode.Idle);
                if (Forecasting)
                {
                    // the window opens now (without the forecast it waits for meet) so the forecast can show
                    OpenWindow(0f);
                    yield return UntilHook(ThreatLineAt(0f));
                }
            }

            _threatAt = HookTime();
            Log("threat line (room_hook_1)");
            yield return SayAndHold(GameLines.Hook, 3f, 9f); // the dark swarm pours on "angry swarms" (StakesDirector)
            _hook2At = HookTime();
            if (delivering && Rocket != null && !IsDelivered(Rocket))
            {
                delivery.DeliverHome(Rocket, DeliveryStyle.Crown, 12); // flown in nose-up on "Your evacuation rocket"
                Log("rocket away");
            }
            yield return SayAndHold(GameLines.Hook2, 1.5f, 8f);
            if (delivering)
            {
                // the hive must be down and Beeswax out before meet sends him to his spot (normally long done)
                float from = Time.time;
                while ((!_hiveLanded || (beeswax != null && (beeswax.Inside || beeswax.Climbing))) && Time.time - from < 3f) yield return null;
            }
            Complete(0.4f);
        }

        void HiveLandedInHook()
        {
            HiveLanded();
            _hiveLanded = true;
            if (!IsActive || ActiveBeat != GameBeat.Hook) return; // snapped by a beat change: HandleBeatChanged puts Beeswax out
            _hiveLandedAt = HookTime();
            Log("hive landed");
            ClimbOut();
        }

        void ClimbOut()
        {
            if (beeswax == null || hive == null) return;
            Vector3 outward = hive.DoorPoint.position - hive.transform.position;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-4f) outward = HeadPosition() - hive.transform.position;
            beeswax.ClimbOut(outward, HookMark, () =>
            {
                if (_hookStartedAt < 0f) return;
                _outAt = HookTime();
                Log("Beeswax out of the door, flying up to his mark");
            });
            Log("Beeswax squeezes out");
        }

        /// <summary>Open the window; with the forecast on, it shows peaceful Paris until the cut on "angry swarms".</summary>
        void OpenWindow(float threatFloor)
        {
            if (window == null) return;
            window.Open();
            _windowAt = HookTime();
            if (!Forecasting) return;
            float lineAt = ThreatLineAt(threatFloor), words = ThreatWordsOffset();
            forecast.ShowPeaceful(lineAt + words - _windowAt);
            Log("window opening on the forecast: room_hook_1 at +" + lineAt.ToString("0.00") + ", \"" + HookTiming.ThreatWords +
                "\" (the cut) ~" + words.ToString("0.00") + " s into it, so peaceful is fully open " +
                (lineAt + words - _windowAt - window.OpenSeconds).ToString("0.00") + " s before the cut");
        }

        /// <summary>When room_hook_1 may start: not before `floor`, and with the forecast on not before the peaceful
        /// window has been fully open peacefulMinSeconds when "angry swarms" is said.</summary>
        float ThreatLineAt(float floor)
        {
            if (!Forecasting || _windowAt < 0f) return floor;
            return Mathf.Max(floor, _windowAt + window.OpenSeconds + peacefulMinSeconds - ThreatWordsOffset());
        }

        /// <summary>Seconds into room_hook_1 when "angry swarms" is said (the same estimate StakesDirector pours on).</summary>
        float ThreatWordsOffset()
        {
            var line = GameLines.Hook;
            float clip = net != null ? net.ClipSeconds(line.id) : 0f;
            return HookTiming.WordOffset(clip, line.caption, HookTiming.ThreatWords, HookTiming.DefaultThreatOffset);
        }

        bool IsDelivered(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            var di = t.GetComponent<DeliveryItem>();
            return di != null && di.delivered && di.job < 0;
        }

        float HookTime() => _hookStartedAt >= 0f ? Time.time - _hookStartedAt : -1f;

        IEnumerator UntilHook(float seconds)
        {
            while (HookTime() < seconds) yield return null;
        }

        /// <summary>
        /// Say a line and hold until it has really finished: at least its clip length from the moment it started (it
        /// may queue behind room_pre), and while Beeswax is still talking. Same on the headset and in a silent smoke
        /// player (no audio = IsSpeaking drops at once, so SayAndWait would cut the hook short there).
        /// </summary>
        IEnumerator SayAndHold(GameLine line, float minSeconds, float maxSeconds)
        {
            _heardId = null;
            if (net != null) net.Say(line);
            float said = Time.time;
            yield return null;
            while (_heardId != line.id && net != null && Time.time - said < 6f) yield return null; // queued behind another line
            bool heard = _heardId == line.id;
            float start = heard ? _heardAt : said;
            float clip = heard ? _heardClip : 0f;
            while (true)
            {
                float t = Time.time - start;
                if (t >= maxSeconds && t >= clip) break;
                bool talking = net != null && net.IsSpeaking;
                if (t >= minSeconds && t >= clip && !talking) break;
                yield return null;
            }
        }

        // ------------------------------------------------------------ meet
        IEnumerator Meet()
        {
            if (beeswax != null)
            {
                beeswax.EmergeNow(HookMark); // only if he never got out (a jump past the hook)
                beeswax.Detach();
                beeswax.GoPresent();
            }
            yield return Wait(0.8f);
            if (window != null) window.Open();
            if (net != null) net.RequestScene(GameIds.Scene.Meadow, sendToHive: true);
            yield return SayAndWait(GameLines.MeetIntro, 2f, 8f);
            yield return SayAndWait(GameLines.Meet, 2f, 6f); // room_meet_window is off the demo path (v3.1)
            Complete(0.4f);
        }

        void Log(string msg)
        {
            if (!logHook) return;
            Debug.Log(_hookStartedAt >= 0f && IsActive && ActiveBeat == GameBeat.Hook
                ? "[VBS][Hook] +" + HookTime().ToString("0.00") + " s " + msg
                : "[VBS][Hook] " + msg);
        }

        static string Fmt(float t) => t >= 0f ? t.ToString("0.00") : "-";
    }
}
