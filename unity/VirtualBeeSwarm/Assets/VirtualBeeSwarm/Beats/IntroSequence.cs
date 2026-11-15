using System.Collections;
using UnityEngine;
using UnityEngine.XR;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// An unnamed worker bee delivers SIGN HERE. Signing sends the clipboard back and starts the ring cut.
    /// The crew flies the hive through the window while Beeswax speaks from inside. He emerges after it lands.
    /// With deliveries off, START begins the same introduction with the hive and rocket already in place.
    /// The meet starts with room_meet_1, followed by
    /// room_meet_role, room_forecast (peaceful Paris on "future"), room_hook_1 (pour + cut on "angry swarms"),
    /// room_hook_2 (the rocket comes through as it starts), room_meet_2 (on "Four minutes": the band thins, the dim
    /// eases, the window goes back to the meadow, he drops to the hive door).
    /// Forecast (stakes.forecast, both modes): v2 moves it into meet. room_forecast turns the window to peaceful Paris on
    /// "future"; on room_hook_1's "angry swarms" it cuts to the ravaged world (a giant laser-eyed bee topples the Eiffel
    /// Tower) as the dark swarm pours; room_meet_2's "Four minutes" sends the window back to the meadow.
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
        [SerializeField] float clipboardYaw = 25f;
        [Tooltip("Top leans away from the player (degrees).")]
        [SerializeField] float clipboardTilt = 15f;
        [Tooltip("The APPROVED stamp shows this long before the crew flies the clipboard out.")]
        [SerializeField] float stampHoldSeconds = 0.45f;

        [Header("Hook timing (V14, seconds from the start of the hook)")]
        [SerializeField] float windowOpenAt = 0.6f;
        [Tooltip("The hive leaves the window before the ring cut (2 s) is quite closed.")]
        [SerializeField] float hiveAwayAt = 1.4f;
        [Tooltip("v2: room_hook_carry starts from inside the box as the hive clears the ring (it lands near \"contract\").")]
        [SerializeField] float threatLineAt = 1.4f;
        [Tooltip("The hive's festoon flight (BeeDelivery.festoonSeconds): its departure is delayed so it lands on \"contract\".")]
        [SerializeField] float hiveFlightSeconds = 4.5f;
        [Tooltip("Where Beeswax flies after climbing out (empty: his home point).")]
        [SerializeField] Transform hookMark;
        [SerializeField] bool logHook = true;

        [Header("Forecast (v2, in meet: peaceful on room_forecast, cut on room_hook_1)")]
        [Tooltip("Empty: found in the scene, else added to the window at runtime (then without SFX).")]
        [SerializeField] ForecastMoment forecast;

        bool Delivering => delivery != null && BeeDelivery.Mode != DeliveryMode.Off;
        bool Forecasting => forecast != null && window != null && StakesFlags.Forecast;
        bool UseClipboard => Delivering && signHere != null;
        Transform Rocket => rocket != null ? rocket : rocketPerch != null ? rocketPerch.parent : null;
        Transform HookMark => hookMark != null ? hookMark : beeswax != null ? beeswax.HomePoint : null;
        GameDirector _beats;

        // pre
        float _clipParkedAt = -1f, _clipOutOfReachAt = -1f, _preStartedAt, _presenceAt = -1f;
        bool _stamped;
        int _presence = -1; // -1 unknown (desktop, no sensor), 0 off, 1 on
        TextMesh _instructions;
        string _instructionsText;

        // hook
        float _hookStartedAt = -1f, _threatAt = -1f, _hook2At = -1f, _hiveLandedAt = -1f, _outAt = -1f, _windowAt = -1f;
        float _meetStartedAt = -1f;
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
            // hook -> meet: the rocket flies in on room_hook_2 (Meet does it); any later beat: it is simply there
            if (Rocket != null && !(beat == GameBeat.Meet && prev == GameBeat.Hook)) delivery.EnsureHome(Rocket, DeliveryStyle.Crown, 12);
            if (beeswax != null)
            {
                beeswax.SetCaptionAnchor(null);
                // v2: in meet he squeezes out of the door on room_meet_1 (Meet does it); any later beat: just out
                if (!(beat == GameBeat.Meet && prev == GameBeat.Hook)) beeswax.EmergeNow(HookMark);
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
                (_clipParkedAt >= 0f ? ", " + (Time.time - _clipParkedAt).ToString("0.0") + " s after it landed" : ""));
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
                    (delivery == null ? ", no BeeDelivery" : "") + "): room_hook_carry +" + Fmt(_threatAt) +
                    (Delivering ? ", hive landed +" + Fmt(_hiveLandedAt) : "") +
                    (_windowAt >= 0f ? ", window open +" + Fmt(_windowAt) : ""));
                _hookStartedAt = -1f;
            }
            if (beat == GameBeat.Meet && _meetStartedAt >= 0f)
            {
                Log("meet beat " + (Time.time - _meetStartedAt).ToString("0.00") + " s: Beeswax out +" + Fmt(_outAt) + ", room_hook_1 +" +
                    Fmt(_threatAt) + ", room_hook_2 +" + Fmt(_hook2At) + (forecast != null && forecast.Summary.Length > 0 ? " | " + forecast.Summary : ""));
                _meetStartedAt = -1f;
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
                _instructions.text = clip ? "Poke SIGN HERE to accept delivery." : _instructionsText;
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
            _stamped = false;
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
            if (BringClipboard != null) yield return BringClipboard(this, signHere); // [Opening hook] Beeswax brings it himself
            if (BringClipboard == null || !signHere.gameObject.activeInHierarchy) PlaceClipboard("pre");
            while (true)
            {
                var t = signHere.transform;
                if (_clipParkedAt < 0f && t.gameObject.activeInHierarchy && !delivery.IsFlying(t))
                {
                    _clipParkedAt = Time.time;
                    signHere.Arm(true);
                    Log("SIGN HERE parked " + (Time.time - _preStartedAt).ToString("0.0") + " s into pre (" + Vector3.Distance(t.position, HeadPosition()).ToString("0.00") + " m from the head)");
                }
                if (Time.time >= _presenceAt)
                {
                    _presenceAt = Time.time + 0.25f;
                    int p = ReadPresence();
                    if (p == 1 && _presence == 0) PlaceClipboard("headset on"); // OVRManager.HMDMounted, without the Meta SDK
                    _presence = p;
                    if (_clipParkedAt >= 0f && p != 0 && Vector3.Distance(t.position, HeadPosition()) > 1.1f)
                    {
                        if (_clipOutOfReachAt < 0f) _clipOutOfReachAt = Time.time;
                        else if (Time.time - _clipOutOfReachAt > 1.5f) PlaceClipboard("player moved");
                    }
                    else _clipOutOfReachAt = -1f;
                }
                yield return null;
            }
        }

        // [Opening hook] Opening/OpeningDirector: Beeswax flies in and hands SIGN HERE over himself (null: one crew bee parks it).
        public static System.Func<IntroSequence, SignHereClipboard, IEnumerator> BringClipboard;

        /// <summary>[Opening hook] Where SIGN HERE parks in front of the player now (pivot = its bottom edge).</summary>
        public Pose ClipboardParkPose()
        {
            if (signHere == null) return new Pose(transform.position, transform.rotation);
            Transform head = delivery != null ? delivery.Head : null;
            Vector3 eye = head != null ? head.position : transform.position + Vector3.up * 1.3f;
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd = Quaternion.Euler(0f, clipboardYaw, 0f) * fwd.normalized;
            float h = SignHereClipboard.Height * Mathf.Abs(signHere.transform.lossyScale.y);
            Vector3 centre = eye + fwd * clipboardAhead - Vector3.up * clipboardBelowEye;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(clipboardTilt, 0f, 0f);
            return new Pose(centre - rot * Vector3.up * (h * 0.5f), rot);
        }

        void PlaceClipboard(string why)
        {
            if (signHere == null || delivery == null) return;
            Transform head = delivery.Head;
            Vector3 eye = head != null ? head.position : transform.position + Vector3.up * 1.3f;
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd = Quaternion.Euler(0f, clipboardYaw, 0f) * fwd.normalized;
            float h = SignHereClipboard.Height * Mathf.Abs(signHere.transform.lossyScale.y);
            // Offset the clipboard from the title and keep its bottom-edge pivot in reach.
            Vector3 centre = eye + fwd * clipboardAhead - Vector3.up * clipboardBelowEye;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(clipboardTilt, 0f, 0f);
            Vector3 pivot = centre - rot * Vector3.up * (h * 0.5f);
            signHere.Arm(false);
            _clipParkedAt = _clipOutOfReachAt = -1f;
            var item = delivery.Register(signHere.transform);
            item.crewOverride = 1;
            delivery.Park(signHere.transform, new Pose(pivot, rot));
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
            Log("start (delivery " + BeeDelivery.ModeName(BeeDelivery.Mode) + (Forecasting ? ", forecast in meet" : "") + ")");
            if (UseClipboard && signHere.gameObject.activeSelf) StartCoroutine(ClipboardAway());
            // From inside the box, as the crew starts the ring: "Signed. Crew, cut the window!" (room_hook_carry queues behind it)
            if (UseClipboard && flyHive && net != null) net.Say(GameLines.RoomPreSigned);
            var carry = RideLines.HookCarry;

            if (flyHive)
            {
                // 20 bees cut the ring; the window opens on the faded meadow; the hive swings out on 6 bee chains while
                // Beeswax complains from inside the box (room_hook_carry), landing on "contract"
                _hiveLanded = false;
                delivery.CutRing(() => Log("ring cut done"));
                Log("ring cut");
                yield return UntilHook(windowOpenAt);
                OpenWindow();
                yield return UntilHook(Mathf.Max(hiveAwayAt, threatLineAt));
                _threatAt = HookTime();
                Log(carry.id + " (from inside the box)");
                bool away = false;
                yield return SayAndHold(carry, 2f, 9f, (len, heardAt) =>
                {
                    // leave so the 4.5 s festoon flight ends on "contract"
                    float contractAt = LineTime.WordAt(len, carry.caption, "contract", 0.85f);
                    return heardAt + Mathf.Max(0f, contractAt - hiveFlightSeconds);
                }, () =>
                {
                    away = true;
                    delivery.DeliverHome(hive.transform, DeliveryStyle.Festoon, 0, HiveLandedInHook);
                    Log("hive away");
                });
                if (!away) { delivery.DeliverHome(hive.transform, DeliveryStyle.Festoon, 0, HiveLandedInHook); Log("hive away (late)"); }
                float from = Time.time;
                while (!_hiveLanded && Time.time - from < hiveFlightSeconds + 1.5f) yield return null;
            }
            else if (delivering)
            {
                // came back to the hook with the hive already here: just the line
                OpenWindow();
                _hiveLanded = true;
                _threatAt = HookTime();
                yield return SayAndHold(carry, 2f, 9f);
            }
            else
            {
                if (beeswax != null)
                {
                    if (rocketPerch != null) beeswax.AttachTo(rocketPerch);
                    beeswax.WhipAround();
                }
                if (swarm != null) swarm.SetMode(SwarmMode.Idle);
                OpenWindow();
                _threatAt = HookTime();
                yield return SayAndHold(carry, 2f, 9f);
            }
            Complete(0.4f);
        }

        void HiveLandedInHook()
        {
            HiveLanded();
            _hiveLanded = true;
            if (!IsActive || ActiveBeat != GameBeat.Hook) return; // snapped by a beat change
            _hiveLandedAt = HookTime();
            Log("hive landed (the door bulges; Beeswax is still inside)");
        }

        void ClimbOut()
        {
            if (beeswax == null || hive == null) return;
            Vector3 outward = hive.DoorPoint.position - hive.transform.position;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-4f) outward = HeadPosition() - hive.transform.position;
            beeswax.ClimbOut(outward, HookMark, () =>
            {
                if (_meetStartedAt < 0f) return;
                _outAt = Time.time - _meetStartedAt;
                Log("Beeswax out of the door, up to his mark");
            });
            Log("Beeswax squeezes out");
        }

        /// <summary>Open the window on the faded meadow (v2: the forecast waits for room_forecast in meet).</summary>
        void OpenWindow()
        {
            if (window == null) return;
            window.Open();
            _windowAt = HookTime();
            if (net != null) net.RequestScene(GameIds.Scene.Meadow, sendToHive: true);
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
        float MeetTime() => _meetStartedAt >= 0f ? Time.time - _meetStartedAt : -1f;

        IEnumerator UntilHook(float seconds)
        {
            while (HookTime() < seconds) yield return null;
        }

        /// <summary>
        /// Say a line and hold until it has really finished: at least its length from the moment it started (it may queue
        /// behind another line), and while Beeswax is still talking. Same on the headset and in a silent smoke player (no
        /// audio = IsSpeaking drops at once, so SayAndWait would cut the intro short there). cueAt(length, startedAt)
        /// returns when to run `cue` (game time; on a word of the line); it runs at the latest when the line ends.
        /// </summary>
        IEnumerator SayAndHold(GameLine line, float minSeconds, float maxSeconds, System.Func<float, float, float> cueAt = null, System.Action cue = null)
        {
            _heardId = null;
            if (net != null) net.Say(line);
            float said = Time.time;
            yield return null;
            while (_heardId != line.id && net != null && Time.time - said < 6f) yield return null; // queued behind another line
            bool heard = _heardId == line.id;
            float start = heard ? _heardAt : said;
            float len = heard && _heardClip > 0f ? _heardClip : LineTime.Seconds(net, line);
            float hold = heard ? len : 0f;
            float fireAt = cue != null ? (cueAt != null ? cueAt(len, start) : start) : -1f;
            while (true)
            {
                if (fireAt >= 0f && Time.time >= fireAt) { fireAt = -1f; cue(); }
                float t = Time.time - start;
                bool talking = net != null && net.IsSpeaking;
                if ((t >= maxSeconds && t >= hold) || (t >= minSeconds && t >= hold && !talking)) break;
                yield return null;
            }
            if (fireAt >= 0f) cue();
        }

        // ------------------------------------------------------------ meet (v2: who, your role, the forecast, the rules)
        IEnumerator Meet()
        {
            _meetStartedAt = Time.time;
            _outAt = _threatAt = _hook2At = -1f;
            bool delivering = Delivering && hive != null;
            if (beeswax != null)
            {
                if (delivering && beeswax.Inside) ClimbOut(); // he says his name mid-squeeze
                else
                {
                    beeswax.EmergeNow(HookMark); // only if he never got out (a jump past the hook)
                    beeswax.Detach();
                    beeswax.GoPresent();
                }
            }
            if (window != null && !window.IsOpen) window.Open();
            yield return SayAndHold(GameLines.MeetIntro, 2f, 9f);   // room_meet_1
            yield return SayAndHold(RideLines.MeetRole, 2f, 8f);    // raps the roof on "sick", jabs a leg on "You signed"
            yield return SayAndHold(GameLines.RoomMeetHeyBeeswax, 2f, 8f);

            // "Job perk: that window shows the future." The window goes to peaceful Paris on "future"; the cut to the
            // ravaged one comes on "angry swarms" in room_hook_1 (StakesDirector pours, ForecastMoment cuts).
            var fc = RideLines.Forecast;
            yield return SayAndHold(fc, 2f, 8f, (len, at) => at + LineTime.WordAt(len, fc.caption, "future", 0.55f), () =>
            {
                if (!Forecasting) return;
                float len = LineTime.Seconds(net, fc);
                float rest = len - LineTime.WordAt(len, fc.caption, "future", 0.55f);
                forecast.ShowPeaceful(rest + 0.45f + ThreatWordsOffset());
                Log("window cross-fades to the forecast on \"future\"");
            });

            _threatAt = MeetTime();
            Log("threat line (room_hook_1)");
            yield return SayAndHold(GameLines.Hook, 3f, 9f); // the dark swarm pours on "angry swarms" (StakesDirector)

            _hook2At = MeetTime();
            yield return SayAndHold(GameLines.Hook2, 1.5f, 8f, null, () =>
            {
                // the rocket comes through the window as the line starts; planted on "window seat"
                if (delivering && Rocket != null && !IsDelivered(Rocket))
                {
                    delivery.DeliverHome(Rocket, DeliveryStyle.Crown, 12);
                    Log("rocket away");
                }
            });

            var rules = GameLines.Meet; // room_meet_2: "Four problems. Four minutes. Or you're on that rocket."
            yield return SayAndHold(rules, 2f, 7f, (len, at) => at + LineTime.WordAt(len, rules.caption, "Four minutes", 0.4f), () =>
            {
                var stakes = Stakes.StakesDirector.Instance;
                if (stakes != null) stakes.RulesNow();
                if (net != null) net.RequestScene(GameIds.Scene.Meadow, sendToHive: true); // the forecast ends; back to the (stormy) meadow
                if (beeswax != null && hive != null)
                {
                    Vector3 door = hive.DoorPoint.position;
                    Vector3 outward = door - hive.transform.position;
                    outward.y = 0f;
                    beeswax.FlyTo(door + (outward.sqrMagnitude > 1e-4f ? outward.normalized * 0.12f : Vector3.zero) + Vector3.up * 0.1f);
                }
                Log("\"Four minutes\": BOARDING lit, window back to the meadow, Beeswax drops to the hive door");
            });
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
