using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// One station = one beat: prompt -> choices -> outcome. Every choice sends choice.made (GameNet), switches
    /// the window scene locally, and plays Beeswax's line (the Hive's if it answers in time, else the local one).
    /// Right: hexagon lights, beat completes. Wrong: its consequence plays out, that button is crossed out, retry.
    /// Timeout: Beeswax does it himself ("Noted for the minutes") so the show goes on.
    /// Subclasses add the props and the per-choice visuals.
    /// Ride script v2: 8 s after the choices are offered with no pick, Beeswax's "Off the record?" hint (once per
    /// station); 20 s with no pick, room_timeout ("Overruled. I'm picking for you.") and the right choice plays. Both
    /// count as a stall (RidePacing). AfterSolved() runs after the right line, before the beat moves on.
    /// </summary>
    public abstract class StationBase : BeatBehaviour
    {
        [SerializeField] protected GameStation station;
        [SerializeField] protected ChoiceButton[] buttons = new ChoiceButton[0];
        [Tooltip("Parent of the buttons; shown only while choosing.")]
        [SerializeField] protected GameObject buttonRow;
        [Tooltip("This station's props; shown during the station.")]
        [SerializeField] protected GameObject props;
        [SerializeField] protected HexagonPanel hexagons;
        [SerializeField] protected SwarmSystem swarm;
        [SerializeField] protected HiveBody hive;
        [SerializeField] protected BeeswaxController beeswax;
        [Tooltip("Delivered choices (v3.1 V15). Empty: the StationDelivery on this GameObject, if any (none = today's buttons).")]
        [SerializeField] protected StationDelivery delivery;

        [Header("Timing")]
        [SerializeField] protected float advanceDelay = 1.5f;
        [SerializeField] protected float minOutcomeSeconds = 3f;
        [SerializeField] protected float maxOutcomeSeconds = 10f;
        [SerializeField] protected float retryPauseSeconds = 0.8f;
        [Tooltip("Delivered choices: wait at most this long for every choice to park before they accept pokes.")]
        [SerializeField] protected float maxParkWaitSeconds = 4f;
        [Tooltip("v2: the spoken hint this long after the choices are offered with no pick (once per station; 0 = off).")]
        [SerializeField] protected float hintAfterSeconds = 8f;
        [Tooltip("v2: no pick this long after the choices are offered: room_timeout, then the right choice (0 = off).")]
        [SerializeField] protected float noPickTimeoutSeconds = 20f;

        public GameStation Station => station;
        public bool Solved { get; private set; }
        public bool Busy { get; private set; }
        public bool Choosing { get; private set; }

        Coroutine _intro, _offerWait;
        bool _correctPending;   // the right choice is playing out (hexagon not lit yet)
        bool _timeoutPending;   // max duration hit while a wrong choice was playing: solve right after it
        bool _timeoutSolving;
        float _offeredAt = -1f;
        bool _hintSaid;

        protected abstract GameLine Prompt { get; }
        /// <summary>v2: the "Off the record?" hint (no id = none).</summary>
        protected virtual GameLine Hint => default;

        public override bool Handles(GameBeat beat) => beat == BeatOf(station);

        public static GameBeat BeatOf(GameStation s)
        {
            switch (s)
            {
                case GameStation.Smoker: return GameBeat.Smoker;
                case GameStation.Feed: return GameBeat.Feed;
                case GameStation.Wasps: return GameBeat.Wasps;
                default: return GameBeat.Heat;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            if (hexagons == null) hexagons = FindAnyObjectByType<HexagonPanel>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (delivery == null) delivery = GetComponent<StationDelivery>();
            ShowButtons(false);
            SetActive(props, false);
        }

        protected virtual void OnEnable()
        {
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) buttons[i].Pressed += HandlePressed;
            if (net != null) net.ChoiceRequested += HandleChoiceRequest;
        }

        protected virtual void OnDisable()
        {
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) buttons[i].Pressed -= HandlePressed;
            if (net != null) net.ChoiceRequested -= HandleChoiceRequest;
        }

        /// <summary>Board operator twin of a player's choice (protocol v2.1 choice.request): applied like a press.</summary>
        void HandleChoiceRequest(GameStation s, string choice)
        {
            if (s != station) return;
            if (!IsActive || Busy || Solved)
            {
                Debug.Log("[VBS] choice.request " + GameIds.StationName(s) + "/" + choice + " ignored (" +
                          (!IsActive ? "not this station's beat" : Busy ? "a choice is playing" : "already solved") + ")");
                return;
            }
            StopIntro();
            Choose(choice);
        }

        /// <summary>Desktop keys when the buttons are gone (e.g. the modeler replaced them with props): 1-3 = CHOICES order.</summary>
        protected virtual void Update()
        {
            if (!IsActive || !Choosing || Busy) return;
            if (_offeredAt >= 0f && !Solved)
            {
                float waited = Time.time - _offeredAt;
                if (noPickTimeoutSeconds > 0f && waited >= noPickTimeoutSeconds)
                {
                    _offeredAt = -1f;
                    RidePacing.NoteStall(GameIds.StationName(station) + ": no pick " + noPickTimeoutSeconds.ToString("0") + " s after the offer");
                    StopIntro();
                    StartCoroutine(TimeoutSolve());
                    return;
                }
                if (!_hintSaid && hintAfterSeconds > 0f && waited >= hintAfterSeconds && (net == null || !net.IsSpeaking))
                {
                    _hintSaid = true;
                    var hint = Hint;
                    if (!string.IsNullOrEmpty(hint.id))
                    {
                        RidePacing.NoteStall(GameIds.StationName(station) + ": hint " + hint.id + " after " + waited.ToString("0.0") + " s");
                        if (net != null) net.Say(hint);
                    }
                }
            }
            if (delivery != null && delivery.Active)
            {
                // a delivered choice takes pokes only while parked (an offer that gave up waiting unlocks them as they land)
                for (int i = 0; i < buttons.Length; i++)
                {
                    var b = buttons[i];
                    if (b != null) b.SetInteractable(!b.MarkedWrong && IsChoiceAllowed(b.ChoiceId) && delivery.AcceptsPoke(b.ChoiceId));
                }
            }
            if (HasButtons()) return;
            var list = GameIds.Choices[(int)station];
            for (int i = 0; i < list.Length; i++)
                if (GameInput.DigitDown(i + 1)) { Choose(list[i]); return; }
        }

        bool HasButtons()
        {
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) return true;
            return false;
        }

        void StopIntro()
        {
            if (_intro != null) StopCoroutine(_intro);
            _intro = null;
            if (_offerWait != null) StopCoroutine(_offerWait);
            _offerWait = null;
        }

        // ------------------------------------------------------------ beat
        protected override void OnEnter(GameBeat beat)
        {
            Solved = false;
            Busy = false;
            Choosing = false;
            _offerWait = null;
            _correctPending = _timeoutPending = _timeoutSolving = false;
            _offeredAt = -1f;
            _hintSaid = false;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) { buttons[i].MarkWrong(false); buttons[i].SetInteractable(false); }
            SetActive(props, true);
            if (delivery != null) delivery.Prepare(); // delivery on: the props wait outside the window
            if (beeswax != null) beeswax.GoPresent();
            OnStationEnter();
            _intro = StartCoroutine(StationIntro());
        }

        protected override void OnExit(GameBeat beat)
        {
            // Left while the right answer was still playing (operator skip, or the director's timeout grace ran out):
            // it still counts, so the hexagon and the score match what the player did.
            if (!Solved && (_correctPending || _timeoutSolving)) FinishSolved();
            Choosing = false;
            Busy = false;
            _intro = null;
            _offerWait = null;
            _offeredAt = -1f;
            ShowButtons(false);
            if (delivery != null) delivery.OnStationExit(Solved); // flights snap: the right one on its spot, the rest gone
            OnStationExit();
            if (!KeepPropsAfter) SetActive(props, false);
        }

        protected override void OnBeatTimeout()
        {
            if (Solved) { Complete(); return; }
            if (_timeoutSolving || _correctPending) return; // already wrapping up
            if (Busy) { _timeoutPending = true; return; }   // a wrong choice is playing: Beeswax solves it right after
            StopIntro();
            StartCoroutine(TimeoutSolve());
        }

        protected override void OnResetState()
        {
            Solved = false;
            Busy = false;
            Choosing = false;
            _intro = null;
            _offerWait = null;
            _correctPending = _timeoutPending = _timeoutSolving = false;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) buttons[i].MarkWrong(false);
            ShowButtons(false);
            SetActive(props, false);
            if (delivery != null) delivery.ResetItems();
            OnStationReset();
        }

        IEnumerator TimeoutSolve()
        {
            _timeoutSolving = true;
            Busy = true; // nothing else may choose while Beeswax announces it
            Choosing = false;
            _offeredAt = -1f;
            ShowButtons(false);
            var line = RideLines.Timeout; // v2: "Overruled. I'm picking for you. Watch closely."
            yield return SayAndWait(line, 1f, LineTime.Seconds(net, line) + 1f);
            Busy = false;
            Choose(GameIds.CorrectChoice[(int)station]);
        }

        // ------------------------------------------------------------ choices
        /// <summary>Default intro: prompt line, then the buttons.</summary>
        protected virtual IEnumerator StationIntro()
        {
            if (delivery != null) delivery.ParkChoices(); // the choices fly in as the prompt starts
            yield return SayAndWait(Prompt, 0.8f, 5f);
            OfferChoices();
        }

        /// <summary>
        /// The choices accept pokes. Delivered: only once every choice still in play is parked (never mid-flight), so
        /// Choosing (what the autopilot waits for) means "parked"; gives up waiting after maxParkWaitSeconds.
        /// </summary>
        protected void OfferChoices()
        {
            if (Solved) return;
            if (delivery != null && !delivery.EnsureParked())
            {
                if (_offerWait == null) _offerWait = StartCoroutine(OfferWhenParked());
                return;
            }
            Offer(0f);
        }

        IEnumerator OfferWhenParked()
        {
            float start = Time.time;
            while (!delivery.AllParked && Time.time - start < maxParkWaitSeconds) yield return null;
            _offerWait = null;
            if (!IsActive || Busy || Solved) yield break;
            if (!delivery.AllParked)
                Debug.LogWarning("[VBS][Delivery] " + GameIds.StationName(station) + ": choices still not parked after " +
                                 maxParkWaitSeconds.ToString("0.0") + " s; offering anyway (each unlocks as it parks)");
            Offer(Time.time - start);
        }

        void Offer(float waited)
        {
            Choosing = true;
            _offeredAt = Time.time;
            ShowButtons(true);
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null)
                    buttons[i].SetInteractable(!buttons[i].MarkedWrong && IsChoiceAllowed(buttons[i].ChoiceId) &&
                                               (delivery == null || delivery.AcceptsPoke(buttons[i].ChoiceId)));
            if (delivery != null) delivery.OnChoicesOffered(waited);
        }

        /// <summary>Override to lock a choice for now (e.g. feed: show the honey mistake first).</summary>
        protected virtual bool IsChoiceAllowed(string choice) => true;

        void HandlePressed(ChoiceButton b)
        {
            if (!IsActive || Busy || Solved || !Choosing) return;
            if (delivery != null && !delivery.AcceptsPoke(b.ChoiceId))
            {
                Debug.Log("[VBS][Delivery] poke on " + b.ChoiceId + " ignored: still flying");
                return;
            }
            Choose(b.ChoiceId);
        }

        /// <summary>Make a choice (buttons, keys, physical props, timeouts all end up here).</summary>
        public void Choose(string choice)
        {
            if (!IsActive || Busy || Solved) return;
            if (!GameIds.IsValidChoice(station, choice))
            {
                Debug.LogWarning("[VBS] '" + choice + "' is not a choice of station " + GameIds.StationName(station));
                return;
            }
            StopIntro(); // a physical prop / operator / key can answer before the prompt finished
            StartCoroutine(ChoiceRoutine(choice));
        }

        IEnumerator ChoiceRoutine(string choice)
        {
            Busy = true;
            Choosing = false;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) buttons[i].SetInteractable(false);

            bool correct = GameIds.IsCorrect(station, choice);
            if (correct) _correctPending = true;
            if (delivery != null) delivery.OnChoiceMade(choice, correct); // stamp, same frame as the poke
            var stakes = VirtualBeeSwarm.Stakes.StakesDirector.Instance; // the dark swarm as the meter (V19)
            if (stakes != null) stakes.ReportChoice(station, choice, correct);
            GameLines.TryGetOutcome(station, choice, out var outcome);
            // choice.made -> the Hive plays the line, steers the window, fires fx; BeeswaxStations covers it offline.
            bool clientVoices = net != null && net.MakeChoice(station, choice, outcome.scene);

            _offeredAt = -1f;
            StartCoroutine(OutcomeThenDelivery(choice, correct));
            if (clientVoices) yield return WaitForTalk(minOutcomeSeconds, maxOutcomeSeconds);
            else yield return SayAndWait(outcome.line, minOutcomeSeconds, maxOutcomeSeconds, unlessHive: true);

            if (correct)
            {
                FinishSolved();
                yield return AfterSolved(choice);
                Busy = false;
                Complete(advanceDelay);
                yield break;
            }

            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null && buttons[i].ChoiceId == choice) buttons[i].MarkWrong(true);
            yield return Wait(retryPauseSeconds);
            OnRetry(choice);
            // let the wrong-pick line and "Pick again." finish before the choices come back
            float retrySaid = Time.time;
            while (net != null && net.IsSpeaking && Time.time - retrySaid < 8f) yield return null;
            if (_timeoutPending)
            {
                // The beat ran out of time during this wrong answer: Beeswax does it himself now (still Busy).
                _timeoutPending = false;
                yield return TimeoutSolve();
                yield break;
            }
            Busy = false;
            OfferChoices();
        }

        /// <summary>The station's own consequence, then the crews: wrong = repo haul, right (feed) = set down.</summary>
        IEnumerator OutcomeThenDelivery(string choice, bool correct)
        {
            yield return ShowOutcome(choice, correct);
            if (delivery != null) delivery.OnOutcomeShown(choice, correct);
        }

        void FinishSolved()
        {
            if (Solved) return;
            Solved = true;
            _correctPending = false;
            _timeoutSolving = false;
            if (hexagons != null) hexagons.Light((int)station);
            if (director != null) director.NotifyStationSolved();
            ShowButtons(false);
            OnSolved();
        }

        protected void ShowButtons(bool on)
        {
            if (buttonRow != null) SetActive(buttonRow, on);
            else
                for (int i = 0; i < buttons.Length; i++)
                    if (buttons[i] != null) SetActive(buttons[i].gameObject, on);
        }

        // ------------------------------------------------------------ subclass hooks
        protected virtual bool KeepPropsAfter => false;
        protected virtual void OnStationEnter() { }
        protected virtual void OnStationExit() { }
        protected virtual void OnStationReset() { }
        /// <summary>Visuals for a choice; runs alongside Beeswax's line.</summary>
        protected abstract IEnumerator ShowOutcome(string choice, bool correct);
        /// <summary>A wrong choice played out; undo what must be undone before the retry.</summary>
        protected virtual void OnRetry(string wrongChoice) { }
        protected virtual void OnSolved() { }
        /// <summary>v2: the lines after the right one (banana, feed_why, optional lines), before the beat moves on.</summary>
        protected virtual IEnumerator AfterSolved(string choice) { yield break; }

        /// <summary>Say a line and wait for it (its clip or caption length; a silent smoke player doesn't cut it short).</summary>
        protected IEnumerator SayLine(GameLine line, float minSeconds = 0.8f)
        {
            float len = LineTime.Seconds(net, line);
            yield return SayAndWait(line, Mathf.Min(minSeconds, len), len + 1.5f);
        }
    }
}
