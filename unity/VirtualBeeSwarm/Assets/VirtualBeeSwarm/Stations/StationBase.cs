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

        [Header("Timing")]
        [SerializeField] protected float advanceDelay = 1.5f;
        [SerializeField] protected float minOutcomeSeconds = 3f;
        [SerializeField] protected float maxOutcomeSeconds = 10f;
        [SerializeField] protected float retryPauseSeconds = 0.8f;

        public GameStation Station => station;
        public bool Solved { get; private set; }
        public bool Busy { get; private set; }
        public bool Choosing { get; private set; }

        Coroutine _intro;
        bool _correctPending;   // the right choice is playing out (hexagon not lit yet)
        bool _timeoutPending;   // max duration hit while a wrong choice was playing: solve right after it
        bool _timeoutSolving;

        protected abstract GameLine Prompt { get; }

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
            if (!IsActive || !Choosing || Busy || HasButtons()) return;
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
        }

        // ------------------------------------------------------------ beat
        protected override void OnEnter(GameBeat beat)
        {
            Solved = false;
            Busy = false;
            Choosing = false;
            _correctPending = _timeoutPending = _timeoutSolving = false;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) { buttons[i].MarkWrong(false); buttons[i].SetInteractable(false); }
            SetActive(props, true);
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
            ShowButtons(false);
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
            _correctPending = _timeoutPending = _timeoutSolving = false;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) buttons[i].MarkWrong(false);
            ShowButtons(false);
            SetActive(props, false);
            OnStationReset();
        }

        IEnumerator TimeoutSolve()
        {
            _timeoutSolving = true;
            Busy = true; // nothing else may choose while Beeswax announces it
            Choosing = false;
            ShowButtons(false);
            yield return SayAndWait(GameLines.TimeoutSolve, 1f, 3.5f);
            Busy = false;
            Choose(GameIds.CorrectChoice[(int)station]);
        }

        // ------------------------------------------------------------ choices
        /// <summary>Default intro: prompt line, then the buttons.</summary>
        protected virtual IEnumerator StationIntro()
        {
            yield return SayAndWait(Prompt, 0.8f, 5f);
            OfferChoices();
        }

        protected void OfferChoices()
        {
            if (Solved) return;
            Choosing = true;
            ShowButtons(true);
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null) buttons[i].SetInteractable(!buttons[i].MarkedWrong && IsChoiceAllowed(buttons[i].ChoiceId));
        }

        /// <summary>Override to lock a choice for now (e.g. feed: show the honey mistake first).</summary>
        protected virtual bool IsChoiceAllowed(string choice) => true;

        void HandlePressed(ChoiceButton b)
        {
            if (!IsActive || Busy || Solved || !Choosing) return;
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
            GameLines.TryGetOutcome(station, choice, out var outcome);
            // choice.made -> the Hive plays the line, steers the window, fires fx; BeeswaxStations covers it offline.
            bool clientVoices = net != null && net.MakeChoice(station, choice, outcome.scene);

            StartCoroutine(ShowOutcome(choice, correct));
            if (clientVoices) yield return WaitForTalk(minOutcomeSeconds, maxOutcomeSeconds);
            else yield return SayAndWait(outcome.line, minOutcomeSeconds, maxOutcomeSeconds, unlessHive: true);

            if (correct)
            {
                FinishSolved();
                Busy = false;
                Complete(advanceDelay);
                yield break;
            }

            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null && buttons[i].ChoiceId == choice) buttons[i].MarkWrong(true);
            yield return Wait(retryPauseSeconds);
            OnRetry(choice);
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
    }
}
