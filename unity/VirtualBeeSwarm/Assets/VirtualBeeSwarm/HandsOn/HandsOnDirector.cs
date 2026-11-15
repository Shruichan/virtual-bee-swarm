using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// The hands-on layer over the four stations (tuning.json handsOn.mode = full | lever | off).
    /// full: ORDER UP tickets, the crew flies the one ordered item in, and the right item has to be set up by hand
    /// (pump the smoker with the lever, pour the syrup into the roof feeder, slot the reducer into the door, set the
    /// dish down and pump the water). lever: today's delivered props, plus the lever at the smoker and at T-0.
    /// It never replaces StationBase.Choose: it only delays the call until the hand step is done, so every outcome,
    /// line, stamp and hexagon stays the station's own. It watches the station (IsActive / Choosing / Busy / Solved);
    /// any outside solve (timeout, operator, autopilot Choose) cancels the hand step cleanly.
    /// Desktop: 1-3 order a ticket, X carries the prop to its socket, Y pulls the lever (mouse drag works too).
    /// Built at runtime after the scene loads; mode off = nothing is created and no hook is set.
    /// </summary>
    [DefaultExecutionOrder(-5)]
    public class HandsOnDirector : MonoBehaviour
    {
        public static HandsOnDirector Instance { get; private set; }
        /// <summary>The autopilot should play the hand steps (VBS_AUTOPILOT_HANDSON=1) instead of calling Choose.</summary>
        public static bool AutoDrives => Instance != null && HandsOnTuning.AutoDrive;

        enum Step { Idle, Ordering, Inbound, Setup, Done }

        const float InboundMaxSeconds = 4f, OrderHintSeconds = 8f, PourSeconds = 1.2f;
        const KeyCode LeverKey = KeyCode.Y, CarryKey = KeyCode.X;

        public bool WaitingForLaunch { get; private set; }
        public HoneyLever Lever => _lever;

        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        SwarmSystem _swarm;
        BeeswaxController _beeswax;
        Transform _table;
        StationBase[] _stations;

        HoneyLever _lever;
        OrderBoard _board;
        SetupSocket _socket;
        HandsOnPrompt _prompt, _leverNote;
        Renderer[] _puffs;
        float[] _puffAt;
        Vector3[] _puffFrom, _puffTo;
        Color _puffColor;
        Renderer _stream, _water;

        Step _step;
        StationBase _cur;
        StationDelivery _sd;
        string _ordered;
        readonly HashSet<string> _refused = new HashSet<string>();
        float _stepAt, _assistAt, _hintAt, _fill;
        int _pumps, _sub;
        bool _orderHinted, _autoCarrying, _launchPulled;
        int _launchTick;
        readonly HashSet<StationBase> _byHand = new HashSet<StationBase>();
        bool _autoExpectHand;

        /// <summary>Autopilot: what went wrong in the last AutoPlay (null = it was played by hand, as it should be).</summary>
        public string AutoProblem { get; private set; }
        Transform _item;
        CarryProp _carry;
        Coroutine _auto;

        // ------------------------------------------------------------ bootstrap
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (HandsOnTuning.Mode == HandsOnMode.Off || Instance != null) return;
            if (FindAnyObjectByType<GameDirector>() == null) return; // not the game scene
            new GameObject("[VBS] HandsOn").AddComponent<HandsOnDirector>();
        }

        // Wait a few frames: building it in the first frames (while a headless player fails to load the window videos)
        // trips a GC race in the ForecastMoment VideoPlayer error callback (see notes-arui). Nothing needs it sooner.
        IEnumerator Start()
        {
            for (int f = 0; f < 8; f++) yield return null;
            // the table / hive can arrive later (room scan, placement, the hive landing): keep looking, 4x a second
            float warnAt = Time.realtimeSinceStartup + 10f;
            var wait = new WaitForSecondsRealtime(0.25f);
            while (!Init(Time.realtimeSinceStartup > warnAt))
            {
                if (warnAt > 0f && Time.realtimeSinceStartup > warnAt) warnAt = float.MaxValue;
                yield return wait;
            }
        }

        bool Init(bool last)
        {
            _director = FindAnyObjectByType<GameDirector>();
            _net = FindAnyObjectByType<GameNet>();
            _hive = FindAnyObjectByType<HiveBody>();
            _swarm = FindAnyObjectByType<SwarmSystem>();
            _beeswax = FindAnyObjectByType<BeeswaxController>();
            _stations = FindObjectsByType<StationBase>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _table = BeeDelivery.Instance != null && BeeDelivery.Instance.TableAnchor != null ? BeeDelivery.Instance.TableAnchor
                : _hive != null && _hive.transform.parent != null ? _hive.transform.parent : null;
            if (_table == null || _hive == null)
            {
                if (last) Debug.LogWarning("[VBS][HandsOn] still no table anchor / hive (hive " + (_hive != null ? "found" : "missing") +
                                           ", delivery " + (BeeDelivery.Instance != null ? "found" : "missing") + "): waiting");
                return false;
            }
            Instance = this;
            Build();
            StationBase.PickGate = PickGate;
            StationDelivery.OrderMode = sd => HandsOnTuning.Mode == HandsOnMode.Full;
            T0Sequence.LaunchGate = LaunchGate;
            if (_net != null && HandsOnTuning.VoiceOrder) _net.TranscriptReceived += HandleTranscript;
            Debug.Log("[VBS][HandsOn] ready: " + HandsOnTuning.ModeName(HandsOnTuning.Mode) + ", lever at " + _lever.transform.localPosition +
                      ", " + _stations.Length + " stations, table " + _table.name);
            return true;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            StationBase.PickGate = null;
            StationBase.SetupByHand = false;
            StationDelivery.OrderMode = null;
            T0Sequence.LaunchGate = null;
            if (_net != null) _net.TranscriptReceived -= HandleTranscript;
        }

        void Build()
        {
            bool leverLeft = HandsOnTuning.LeverLeft;
            float x = leverLeft ? -0.46f : 0.56f;
            var leverPosition = new Vector3(x, 0f, -0.02f);
            var boardPosition = new Vector3(-0.04f, 0f, -0.04f);
            float controlScale = 1f;
            if (Bloom.MeadowLayout.Active && Bloom.MeadowLayout.Anchor == _table)
            {
                // Match the meadow's player-facing choice row and keep these controls at hand size.
                float scale = Bloom.MeadowLayout.Scale;
                float height = Bloom.MeadowTuning.ChoiceHeight;
                float rowZ = -Bloom.MeadowLayout.RowOffset(scale);
                controlScale = 1f / scale;
                leverPosition = new Vector3((leverLeft ? -0.43f : 0.43f) / scale, (height - 0.23f) / scale, rowZ + 0.02f);
                boardPosition = new Vector3(-0.04f / scale, (height - OrderBoard.TicketRise) / scale, rowZ);
            }
            _lever = HoneyLever.Build(_table, leverPosition, leverLeft);
            _lever.transform.localScale = Vector3.one * controlScale;
            _lever.Pulled += HandlePulled;
            _lever.Refused += HandleRefused;
            _board = OrderBoard.Build(_table, boardPosition);
            _board.transform.localScale = Vector3.one * controlScale;
            _board.Ordered += Order;
            if (leverPosition.y > 0f)
            {
                AddControlStake(_lever.transform);
                AddControlStake(_board.transform);
            }
            _socket = SetupSocket.Build(_table);
            _prompt = HandsOnPrompt.Build(_table, "[VBS] HandsOnPrompt");
            _leverNote = HandsOnPrompt.Build(_table, "[VBS] LeverNote", 0.002f);

            var fx = new GameObject("[VBS] HandsOnFx").transform;
            fx.SetParent(_table, false);
            _puffs = new Renderer[9];
            _puffAt = new float[_puffs.Length];
            _puffFrom = new Vector3[_puffs.Length];
            _puffTo = new Vector3[_puffs.Length];
            for (int i = 0; i < _puffs.Length; i++)
            {
                _puffs[i] = HandsOnKit.Glow(PrimitiveType.Sphere, "Puff" + i, fx, Vector3.zero, Vector3.one * 0.03f, Color.clear);
                _puffs[i].gameObject.SetActive(false);
                _puffAt[i] = -1f;
            }
            _stream = HandsOnKit.Glow(PrimitiveType.Cylinder, "Stream", fx, Vector3.zero, Vector3.one, new Color(1f, 0.78f, 0.25f, 0.9f));
            _stream.gameObject.SetActive(false);
            _water = HandsOnKit.Glow(PrimitiveType.Cylinder, "Water", fx, Vector3.zero, Vector3.one, new Color(0.25f, 0.6f, 1f, 0.8f));
            _water.gameObject.SetActive(false);
        }

        void AddControlStake(Transform control)
        {
            float height = _table.TransformVector(Vector3.up * control.localPosition.y).y;
            HandsOnKit.Prim(PrimitiveType.Cube, "MeadowStake", control,
                Vector3.down * (height * 0.5f), new Vector3(0.04f, height, 0.04f), HandsOnKit.Wood);
        }

        bool FullFor(StationBase st)
        {
            if (HandsOnTuning.Mode != HandsOnMode.Full || st == null) return false;
            var sd = st == _cur ? _sd : st.GetComponent<StationDelivery>();
            return sd != null && sd.Active; // delivery off / missing: behaves as lever
        }

        static string Correct(StationBase st) => GameIds.CorrectChoice[(int)st.Station];
        static void Log(string msg) => Debug.Log("[VBS][HandsOn] " + msg);

        // ------------------------------------------------------------ the watch
        void Update()
        {
            if (_lever == null) return; // not built yet
            StationBase active = null;
            for (int i = 0; i < _stations.Length; i++)
                if (_stations[i] != null && _stations[i].IsActive) { active = _stations[i]; break; }
            if (active != _cur)
            {
                LeaveStation();
                _cur = active;
                _sd = active != null ? active.GetComponent<StationDelivery>() : null;
                if (active != null) _byHand.Remove(active);
                _refused.Clear();
                _step = Step.Idle;
            }

            // T0Sequence's coroutine stopped mid-wait (reset, Board jump): the code after the wait never ran
            if (WaitingForLaunch && Time.frameCount - _launchTick > 2) EndLaunchWait(false);
            if (GameInput.KeyDown(LeverKey)) _lever.SimulatePull();
            if (HandsOnTuning.AutoDrive && WaitingForLaunch && !_launchPulled && Time.time - _stepAt > 0.8f) _lever.SimulatePull();
            if (_water.gameObject.activeSelf && (_director == null || _director.Beat == GameBeat.Pre)) _water.gameObject.SetActive(false);
            StepPuffs();
            if (_cur == null) return;

            switch (_step)
            {
                case Step.Idle:
                    if (_cur.Choosing && !_cur.Busy && !_cur.Solved && FullFor(_cur)) BeginOrdering();
                    break;
                case Step.Ordering:
                    if (_cur.Busy || _cur.Solved) { Cancel("the station moved on while ordering"); break; }
                    var list = GameIds.Choices[(int)_cur.Station];
                    for (int i = 0; i < list.Length; i++)
                        if (GameInput.DigitDown(i + 1)) { Order(list[i]); break; }
                    if (_step == Step.Ordering && !_orderHinted && Time.time - _stepAt > OrderHintSeconds)
                    {
                        _orderHinted = true;
                        var t = _board.Ticket(Correct(_cur));
                        if (t != null) t.Wiggle(2.5f);
                        Log("hint: the " + Correct(_cur) + " ticket wiggles");
                    }
                    break;
                case Step.Inbound:
                    if (_cur.Busy || _cur.Solved) { Cancel("the station moved on while the order flew in"); break; }
                    if (_sd.AcceptsPoke(_ordered) || Time.time - _stepAt > InboundMaxSeconds) Arrived();
                    break;
                case Step.Setup:
                    if (_cur.Busy || _cur.Solved) { Cancel("the station moved on during set-up"); break; }
                    StepSetup();
                    break;
            }
        }

        void LeaveStation()
        {
            if (_auto != null) { StopCoroutine(_auto); _auto = null; }
            _autoCarrying = false;
            if (_carry != null) _carry.Disarm();
            _carry = null;
            _item = null;
            _ordered = null;
            _lever.Live = WaitingForLaunch;
            _lever.ShowCradle(false);
            _socket.Hide();
            _board.Hide();
            _stream.gameObject.SetActive(false);
            if (!WaitingForLaunch) _prompt.Hide();
        }

        /// <summary>Someone else solved or took over (timeout, operator, autopilot Choose): drop the hand step.</summary>
        void Cancel(string why)
        {
            Log("cancelled at " + GameIds.StationName(_cur.Station) + ": " + why);
            var st = _cur;
            LeaveStation();
            _step = st.Solved ? Step.Done : Step.Idle;
        }

        // ------------------------------------------------------------ order
        void BeginOrdering()
        {
            var list = GameIds.Choices[(int)_cur.Station];
            var labels = new string[list.Length];
            for (int i = 0; i < list.Length; i++) labels[i] = GameLines.Label(list[i]);
            _board.Show(list, labels, c => _refused.Contains(c));
            _prompt.Show("ORDER UP\nPoke a ticket. The crew flies it in.", _board.transform, Vector3.up * 0.175f);
            _step = Step.Ordering;
            _stepAt = Time.time;
            _orderHinted = false;
            Log("order board up at " + GameIds.StationName(_cur.Station) + " (" + list.Length + " tickets, " + _refused.Count + " crossed out)");
        }

        /// <summary>A ticket was poked / clicked / keyed (or said): the crew flies that one item in.</summary>
        public void Order(string choice)
        {
            if (_step != Step.Ordering || _cur == null || _refused.Contains(choice) || !GameIds.IsValidChoice(_cur.Station, choice)) return;
            _ordered = choice;
            _board.MarkOrdered(choice);
            if (_beeswax != null) _beeswax.WhipAround(); // he shouts the order at the window
            GameSound.Cue("order_up", _board.transform);
            _prompt.Hide();
            Log("ordered " + choice + " at " + GameIds.StationName(_cur.Station));
            _cur.RestartPickClock(); // no "Off the record?" hint / "Overruled" while it flies in
            if (choice == GameIds.Choice.Nothing) { Arrived(); return; } // nothing to fly
            bool right = GameIds.IsCorrect(_cur.Station, choice);
            if (right && _cur.Station == GameStation.Smoker)
            {
                _lever.ShowCradle(true);
                _sd.ParkOrdered(choice, _lever.CradlePose); // docked at the lever, ready to pump
            }
            else _sd.ParkOrdered(choice);
            _step = Step.Inbound;
            _stepAt = Time.time;
        }

        void Arrived()
        {
            string choice = _ordered;
            if (!GameIds.IsCorrect(_cur.Station, choice))
            {
                // wrong order: the station's own consequence, stamp, line and repo crew; the ticket stays, crossed out
                _refused.Add(choice);
                var t = _board.Ticket(choice);
                if (t != null) t.StampGrievance();
                _step = Step.Idle;
                Log("wrong order " + choice + ": the station plays it out");
                _cur.Choose(choice);
                return;
            }
            _board.Hide();
            BeginSetup(_cur);
        }

        // ------------------------------------------------------------ set-up
        void BeginSetup(StationBase st)
        {
            _step = Step.Setup;
            _stepAt = Time.time;
            _hintAt = Time.time + HandsOnTuning.HintSeconds;
            _assistAt = Time.time + HandsOnTuning.AssistSeconds;
            _pumps = 0;
            _sub = 0;
            _fill = 0f;
            int i = Array.IndexOf(GameIds.Choices[(int)st.Station], Correct(st));
            _item = _sd != null ? _sd.Item(i) : null;
            Transform spot = _sd != null ? _sd.Spot : null;
            Log("set-up starts at " + GameIds.StationName(st.Station));
            st.RestartPickClock();
            if (st.Station == GameStation.Smoker)
            {
                _lever.Live = true;
                _lever.ShowCradle(_sd != null && _sd.Active);
                ShowAtLever("PULL THE LEVER 3 TIMES\nTO SMOKE THE HIVE");
                return;
            }
            if (_item == null || spot == null) { Succeed(""); return; }
            _carry = _item.GetComponent<CarryProp>();
            if (_carry == null)
            {
                _carry = _item.gameObject.AddComponent<CarryProp>();
                _carry.Grabbed += HandleGrabbed;
            }
            _carry.Arm(new Pose(_item.position, _item.rotation), _table.position.y + 0.005f);
            switch (st.Station)
            {
                case GameStation.Feed:
                    _socket.Show(spot.position + Vector3.up * 0.006f, _table.rotation, 0.075f, false);
                    _prompt.Show("HOLD SYRUP OVER\nTHE ROOF FEEDER", spot, Vector3.up * 0.3f);
                    break;
                case GameStation.Wasps:
                    _socket.Show(spot.position + Vector3.up * 0.035f - _hive.transform.forward * 0.012f, _hive.transform.rotation, 0.06f, true);
                    _prompt.Show("GRAB THE REDUCER\nSLOT IT INTO THE DOOR", spot, Vector3.up * 0.09f - _hive.transform.right * 0.2f - _hive.transform.forward * 0.05f); // beside the door, not over it
                    break;
                default:
                    _socket.Show(spot.position + Vector3.up * 0.004f, _table.rotation, 0.095f, false);
                    _prompt.Show("SET THE DISH DOWN", spot, Vector3.up * 0.16f);
                    break;
            }
        }

        void ShowAtLever(string text) => _prompt.Show(text, _lever.transform, new Vector3(0f, 0.36f, -0.02f));

        void HandleGrabbed()
        {
            // the crew lets go: it is in the player's hands now
            if (_item != null && BeeDelivery.Instance != null) BeeDelivery.Instance.Release(_item, hide: false);
            Progress();
            Log("picked up " + (_item != null ? _item.name : "?"));
        }

        /// <summary>The player is doing something: the crew assist waits a little longer.</summary>
        void Progress()
        {
            _assistAt = Mathf.Max(_assistAt, Time.time + 5f);
            if (_cur != null) _cur.RestartPickClock();
        }

        void StepSetup()
        {
            float now = Time.time;
            if (_carry != null && _carry.Live && !_carry.Held && _sd != null && !_sd.AcceptsPoke(_ordered))
                _carry.Home = new Pose(_item.position, _item.rotation); // still flying in: wherever the crew has it
            if (GameInput.KeyDown(CarryKey) && _carry != null && _carry.Live && !_autoCarrying) _auto = StartCoroutine(AutoCarry());

            bool hint = now > _hintAt;
            _socket.Bright = hint;
            Transform spot = _sd != null ? _sd.Spot : null;
            switch (_cur.Station)
            {
                case GameStation.Feed:
                {
                    Vector3 d = _item.position - spot.position;
                    float flat = new Vector2(d.x, d.z).magnitude;
                    bool over = _carry.Held && flat <= 0.1f && d.y >= 0.04f && d.y <= 0.25f;
                    _carry.Tip = over ? 110f : 0f;
                    if (over)
                    {
                        _fill += Time.deltaTime / PourSeconds;
                        Progress();
                    }
                    _socket.Fill = _fill;
                    Pour(over, _item.position + _item.up * 0.13f, spot.position);
                    if (_fill >= 1f) { Succeed("TOPPED UP."); return; }
                    break;
                }
                case GameStation.Wasps:
                {
                    float dist = Vector3.Distance(_item.position, spot.position);
                    _socket.Tighten(1f - Mathf.InverseLerp(0.07f, 0.3f, dist));
                    if (_carry.Held && dist <= 0.07f) { Succeed("SNUG."); return; }
                    break;
                }
                case GameStation.Heat:
                    if (_sub == 0)
                    {
                        Vector3 d = _item.position - spot.position;
                        if (_carry.Held && new Vector2(d.x, d.z).magnitude <= 0.08f && Mathf.Abs(d.y) <= 0.1f)
                        {
                            _carry.Disarm();
                            _item.SetPositionAndRotation(spot.position, spot.rotation);
                            _socket.Tint = HandsOnKit.Good;
                            _socket.StopSweep();
                            Log("dish set down");
                            BeeDelivery.Instance?.PlayManualTouchdown(_item);
                            if (!HandsOnTuning.HeatPump) { Succeed("POOL'S OPEN.\nNO DIVING."); return; }
                            _sub = 1;
                            _lever.Live = true;
                            _hintAt = now + HandsOnTuning.HintSeconds;
                            Progress();
                            ShowAtLever("PULL THE LEVER\nTO FILL THE DISH");
                        }
                    }
                    break;
            }
            if (_step != Step.Setup) return;
            if (hint && _carry != null && _carry.Live) _socket.SweepFrom(_item.position);
            else _socket.StopSweep();
            if (now > _assistAt && (_carry == null || !_carry.Held) && !_lever.Held) CrewAssist();
        }

        void HandlePulled()
        {
            if (WaitingForLaunch) { _launchPulled = true; return; }
            if (_step != Step.Setup || _cur == null) return;
            Progress();
            if (_cur.Station == GameStation.Smoker)
            {
                _pumps++;
                Vector3 from = _item != null && _item.gameObject.activeInHierarchy ? _item.position + Vector3.up * 0.12f : _lever.HeadPosition;
                Puff(from, _hive.SwarmCenter.position, new Color(0.92f, 0.92f, 0.95f), 3);
                GameSound.Cue("smoker_puff", _item != null ? _item : _lever.transform);
                if (_swarm != null) _swarm.Agitation *= 0.5f;
                Log("puff " + _pumps + " of 3");
                if (_pumps >= 3) Succeed("SMOKIN'.");
                else ShowAtLever("PUFF " + _pumps + " OF 3");
            }
            else if (_cur.Station == GameStation.Heat && _sub == 1)
            {
                Transform spot = _sd.Spot;
                Puff(_lever.transform.position + Vector3.up * 0.05f, spot.position + Vector3.up * 0.03f, HandsOnKit.Water, 3);
                GameSound.Cue("water_pump", spot);
                _water.transform.SetPositionAndRotation(spot.position + Vector3.up * 0.022f, _table.rotation);
                _water.transform.localScale = new Vector3(0.1f, 0.0015f, 0.1f);
                _water.gameObject.SetActive(true);
                Succeed("POOL'S OPEN.\nNO DIVING.");
            }
        }

        void HandleRefused()
        {
            if (_lever.Live) return;
            _leverNote.Show("NOT YET.\nUNION RULES.", _lever.transform, new Vector3(0f, 0.34f, -0.02f), 1.6f);
        }

        /// <summary>The hand step is done: now the station's own right-choice outcome plays (stamp, crew, line, hexagon).</summary>
        void Succeed(string cheer)
        {
            var st = _cur;
            Transform spot = _sd != null ? _sd.Spot : null;
            Transform at = st.Station == GameStation.Smoker || spot == null ? _lever.transform : spot;
            if (_carry != null) _carry.Disarm();
            if (_item != null && spot != null && st.Station != GameStation.Smoker && st.Station != GameStation.Heat)
                _item.SetPositionAndRotation(spot.position, spot.rotation);
            _lever.Live = false;
            _stream.gameObject.SetActive(false);
            if (_socket.Visible) _socket.Burst(HandsOnKit.Good);
            if (!string.IsNullOrEmpty(cheer) && at != null)
                _prompt.Show(cheer, at, at == _lever.transform ? new Vector3(0f, 0.36f, -0.02f) : Vector3.up * 0.2f, 2.2f, HandsOnKit.Good);
            else _prompt.Hide();
            GameSound.Cue("setup_done", at);
            _step = Step.Done;
            Log("set-up done at " + GameIds.StationName(st.Station) + " in " + (Time.time - _stepAt).ToString("0.0") + " s");
            _byHand.Add(st);
            ChooseCorrect(st, setupByHand: true);
        }

        /// <summary>Nobody did the hand step in time: the crew does it, with the station's own animation.</summary>
        void CrewAssist()
        {
            var st = _cur;
            Log("crew assist at " + GameIds.StationName(st.Station) + " after " + (Time.time - _stepAt).ToString("0.0") + " s");
            if (_carry != null) _carry.Disarm();
            _lever.Live = false;
            _socket.Hide();
            _stream.gameObject.SetActive(false);
            Transform at = st.Station == GameStation.Smoker ? _lever.transform : _sd != null && _sd.Spot != null ? _sd.Spot : _lever.transform;
            _prompt.Show("CREW ASSIST", at, at == _lever.transform ? new Vector3(0f, 0.36f, -0.02f) : Vector3.up * 0.2f, 2f);
            _step = Step.Done;
            ChooseCorrect(st, setupByHand: st.Station == GameStation.Heat && _sub == 1);
        }

        static void ChooseCorrect(StationBase st, bool setupByHand)
        {
            StationBase.SetupByHand = setupByHand;
            try { st.Choose(Correct(st)); }
            finally { StationBase.SetupByHand = false; }
        }

        // ------------------------------------------------------------ hooks
        /// <summary>StationBase.HandlePressed: true = this poke is ours (not a pick yet).</summary>
        bool PickGate(StationBase st, string choice)
        {
            if (st != _cur) return false;
            if (FullFor(st))
            {
                if (_step == Step.Ordering) Order(choice);
                return true; // full: a prop is never a button
            }
            if (st.Station != GameStation.Smoker || choice != GameIds.Choice.Smoker) return false;
            if (_step == Step.Idle) StartLeverSmoker(st);
            return true;
        }

        /// <summary>lever mode: the smoker was picked; it hops to the lever and has to be pumped.</summary>
        void StartLeverSmoker(StationBase st)
        {
            _ordered = GameIds.Choice.Smoker;
            BeginSetup(st);
            if (_sd != null && _sd.Active && _item != null && BeeDelivery.Instance != null)
                BeeDelivery.Instance.Park(_item, _lever.CradlePose);
        }

        /// <summary>T0Sequence, after 3-2-1: the lever is the launch button (auto-launch after launchWaitSeconds).</summary>
        IEnumerator LaunchGate(TextMesh display)
        {
            if (_lever == null || !isActiveAndEnabled) yield break;
            WaitingForLaunch = true;
            _launchPulled = false;
            _stepAt = Time.time;
            _lever.Live = true;
            if (display != null) display.text = "PULL!";
            ShowAtLever("PULL FOR LIFTOFF");
            Log("T-0: waiting for the lever");
            float t0 = Time.time;
            _launchTick = Time.frameCount;
            while (WaitingForLaunch && !_launchPulled && Time.time - t0 < HandsOnTuning.LaunchWaitSeconds)
            {
                _launchTick = Time.frameCount;
                yield return null;
            }
            if (!WaitingForLaunch) yield break;
            Log(_launchPulled ? "T-0: launched by the lever after " + (Time.time - t0).ToString("0.0") + " s" : "T-0: nobody pulled, auto-launch");
            EndLaunchWait(_launchPulled);
        }

        void EndLaunchWait(bool pulled)
        {
            if (!pulled && _step != Step.Setup) Log("T-0 wait ended without a pull");
            WaitingForLaunch = false;
            _launchPulled = false;
            if (_step != Step.Setup) _lever.Live = false;
            if (pulled) _prompt.Show("LIFTOFF!", _lever.transform, new Vector3(0f, 0.36f, -0.02f), 1.5f, HandsOnKit.Good);
            else if (_step != Step.Setup) _prompt.Hide();
        }

        // ------------------------------------------------------------ voice order (stretch, handsOn.voiceOrder)
        static readonly string[][] Keywords =
        {
            new[] { GameIds.Choice.Smoker, "smoker", "smoke" }, new[] { GameIds.Choice.Swat, "swat" },
            new[] { GameIds.Choice.SprayWater, "spray", "water" }, new[] { GameIds.Choice.StoreHoney, "honey" },
            new[] { GameIds.Choice.SugarSyrup, "syrup", "sugar" }, new[] { GameIds.Choice.BiggerDoor, "bigger", "crowbar" },
            new[] { GameIds.Choice.SmallerDoor, "smaller", "reducer" }, new[] { GameIds.Choice.DeepBowl, "bowl" },
            new[] { GameIds.Choice.PebbleDish, "pebble", "dish" }, new[] { GameIds.Choice.Nothing, "nothing" },
        };

        void HandleTranscript(string text, bool final)
        {
            if (!final || _step != Step.Ordering || _cur == null || string.IsNullOrEmpty(text)) return;
            string said = text.ToLowerInvariant();
            for (int k = 0; k < Keywords.Length; k++)
            {
                if (!GameIds.IsValidChoice(_cur.Station, Keywords[k][0])) continue;
                for (int w = 1; w < Keywords[k].Length; w++)
                    if (said.Contains(Keywords[k][w]))
                    {
                        Log("voice order: \"" + text + "\" -> " + Keywords[k][0]);
                        Order(Keywords[k][0]);
                        return;
                    }
            }
        }

        // ------------------------------------------------------------ desktop key X / autopilot
        /// <summary>Carries the prop to its socket through the same grab calls a hand makes.</summary>
        IEnumerator AutoCarry()
        {
            var c = _carry;
            var st = _cur;
            if (c == null || !c.Live || _sd == null || _sd.Spot == null) yield break;
            _autoCarrying = true;
            Vector3 from = c.transform.position;
            c.BeginGrab(from);
            float t = 0f;
            while (c.Live && _step == Step.Setup && _cur == st)
            {
                t += Time.deltaTime;
                Vector3 to = _sd.Spot.position + (st.Station == GameStation.Feed ? Vector3.up * 0.12f : Vector3.zero);
                c.UpdateGrab(Vector3.Lerp(from, to, HandsOnKit.Smooth(t / 0.5f)));
                if (t > 4f) break;
                yield return null;
            }
            c.EndGrab();
            _autoCarrying = false;
            _auto = null;
        }

        /// <summary>Autopilot (VBS_AUTOPILOT_HANDSON=1): order wrong, order right, then do the hand step for real.</summary>
        public IEnumerator AutoPlay(StationBase st, string wrong)
        {
            string correct = Correct(st);
            AutoProblem = null;
            _autoExpectHand = true;
            Log("autopilot plays " + GameIds.StationName(st.Station) + " by hand" + (wrong != null ? " (first the wrong " + wrong + ")" : ""));
            if (FullFor(st))
            {
                if (wrong != null)
                {
                    yield return Until(() => (_cur == st && _step == Step.Ordering) || st.Solved, 10f);
                    Order(wrong);
                    yield return Until(() => st.Busy || st.Solved, 8f);
                    yield return Until(() => (!st.Busy && _step == Step.Ordering) || st.Solved, 40f);
                    if (st.Solved) { AutoProblem = "the wrong order " + wrong + " solved it"; yield break; }
                }
                yield return Until(() => (_cur == st && _step == Step.Ordering) || st.Solved, 10f);
                if (st.Solved) { AutoProblem = "solved before it was ordered by hand"; yield break; }
                Order(correct);
                yield return Until(() => _step == Step.Setup || st.Solved, 10f);
            }
            else
            {
                if (wrong != null)
                {
                    st.Choose(wrong);
                    yield return Until(() => st.Choosing || st.Solved, 40f);
                    if (st.Solved) { AutoProblem = "the wrong pick " + wrong + " solved it"; yield break; }
                }
                if (st.Station == GameStation.Smoker && _cur == st && _step == Step.Idle) StartLeverSmoker(st);
                else { _autoExpectHand = false; st.Choose(correct); yield break; }
            }
            float start = Time.time;
            while (_step == Step.Setup && _cur == st && Time.time - start < 30f)
            {
                if (st.Station == GameStation.Smoker || (st.Station == GameStation.Heat && _sub == 1))
                {
                    _lever.SimulatePull();
                    yield return Until(() => _step != Step.Setup, 0.75f);
                }
                else if (!_autoCarrying) _auto = StartCoroutine(AutoCarry());
                yield return null;
            }
            if (_autoExpectHand && !_byHand.Contains(st)) AutoProblem = "not solved by hand (crew assist / timeout / stuck)";
            Log("autopilot " + GameIds.StationName(st.Station) + ": " + (AutoProblem ?? (_autoExpectHand ? "done by hand" : "picked")));
        }

        static IEnumerator Until(Func<bool> cond, float seconds)
        {
            float end = Time.time + seconds;
            while (!cond() && Time.time < end) yield return null;
        }

        // ------------------------------------------------------------ fx
        void Puff(Vector3 from, Vector3 to, Color c, int count)
        {
            _puffColor = c;
            for (int i = 0; i < _puffs.Length && count > 0; i++)
            {
                if (_puffAt[i] >= 0f) continue;
                _puffAt[i] = Time.time + (3 - count) * 0.09f;
                _puffFrom[i] = from;
                _puffTo[i] = to + UnityEngine.Random.insideUnitSphere * 0.04f;
                count--;
            }
        }

        void StepPuffs()
        {
            for (int i = 0; i < _puffs.Length; i++)
            {
                if (_puffAt[i] < 0f) continue;
                float k = (Time.time - _puffAt[i]) / 0.9f;
                var go = _puffs[i].gameObject;
                if (k >= 1f) { _puffAt[i] = -1f; go.SetActive(false); continue; }
                if (k < 0f) continue;
                if (!go.activeSelf) go.SetActive(true);
                go.transform.position = Vector3.Lerp(_puffFrom[i], _puffTo[i], HandsOnKit.Smooth(k)) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.06f);
                go.transform.localScale = Vector3.one * Mathf.Lerp(0.03f, 0.11f, k);
                Color c = _puffColor;
                c.a = 0.85f * (1f - k * k);
                GameMaterials.Tint(_puffs[i], c);
            }
        }

        void Pour(bool on, Vector3 from, Vector3 to)
        {
            if (_stream.gameObject.activeSelf != on) _stream.gameObject.SetActive(on);
            if (!on) return;
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            _stream.transform.position = (from + to) * 0.5f;
            _stream.transform.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            _stream.transform.localScale = new Vector3(0.012f, len * 0.5f, 0.012f);
        }
    }
}
