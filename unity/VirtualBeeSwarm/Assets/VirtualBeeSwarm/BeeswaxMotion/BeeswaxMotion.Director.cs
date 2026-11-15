using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// BeeswaxMotion's director: listens (orders, beats, lines, scenes, picks, the station's Choosing/Busy/Solved edges,
    /// the delivery's Parked/AllParked, hands, the held hive) and picks a primitive per moment. It never calls the game.
    /// </summary>
    public partial class BeeswaxMotion
    {
        string _order = "";
        float _orderAt = -10f;
        bool _attachedThisBeat;

        // station
        StationBase _st;
        StationDelivery _del;
        bool _pChoosing, _pBusy, _pSolved, _pParked, _pAllParked;
        bool _tabled, _p2Done, _lastWrong, _curiousThisOffer;

        // picks
        GameStation _pcStation, _candStation;
        string _pcChoice, _candChoice;
        float _pcAt = -10f, _candAt = -10f, _takeAt = -10f, _busyAt = -10f, _whipAt = -10f;
        bool _busyWaiting, _takeWhip;

        // lines
        string _lineId = "", _lineCaption = "";
        float _lineAt = -10f, _lineClip;
        bool _lineActive, _forecastLine, _countPending, _pictureEndsOnLine, _pictureEndsOnScene, _listening, _held;
        readonly List<MotionTrack> _releaseOnLineEnd = new List<MotionTrack>();

        // scheduled actions (real time; a jump or reset drops them)
        readonly List<(float at, int gen, Action a)> _sched = new List<(float, int, Action)>();
        int _gen;

        // hands (P5)
        int _hoverIdx = -1;
        float _hoverSince = -1f, _awaySince = -1f;
        readonly List<Transform> _choiceT = new List<Transform>();
        readonly List<Vector3> _choiceP = new List<Vector3>();

        static Dictionary<string, (GameStation s, string c)> _outcomes;

        void Schedule(float delay, Action a) => _sched.Add((Time.time + delay, _gen, a));

        void RunScheduled()
        {
            if (_sched.Count == 0) return;
            float now = Time.time;
            List<Action> due = null;
            for (int i = _sched.Count - 1; i >= 0; i--)
            {
                if (_sched[i].gen != _gen) { _sched.RemoveAt(i); continue; }
                if (now < _sched[i].at) continue;
                (due ??= new List<Action>()).Add(_sched[i].a);
                _sched.RemoveAt(i);
            }
            if (due == null) return;
            for (int i = due.Count - 1; i >= 0; i--) due[i]();
        }

        // ------------------------------------------------------------ orders from the controller
        void OnOrderedCore(string what)
        {
            _order = what;
            _orderAt = Time.time;
            if (what == "fly" || what == "present" || what == "home") _orderSerial++;
            switch (what)
            {
                case "attach":
                    // the seat he is actually on (not the last flight's goal): the rocket perch faces away from the player
                    _perchFace = _b.AttachedTransform != null && _b.AttachedTransform.name.Contains("Perch");
                    _fl = null;
                    _attachedThisBeat = true;
                    _parkedSince = Time.time;
                    _onLanded.Clear();
                    _teleportFrame = Time.frameCount;
                    break;
                case "detach":
                    _perchFace = false;
                    _teleportFrame = Time.frameCount;
                    break;
                case "snap":
                case "emerge":
                    _fl = null;
                    _entrancePending = false;
                    _parkedSince = Time.time;
                    _teleportFrame = Time.frameCount;
                    break;
                case "reset":
                case "hide":
                    _fl = null;
                    _player.CancelAll(_mt, true);
                    _entrancePending = false;
                    _perched = _hiding = _perchFace = false;
                    _lastGoalT = null;
                    _parkedSince = Time.time;
                    _teleportFrame = Time.frameCount;
                    _onLanded.Clear();
                    if (what == "reset") { _pirouetteDone = false; _gen++; _sched.Clear(); }
                    break;
                case "climb":
                    _fl = null;
                    _entrancePending = true;
                    break;
                case "whip":
                    _whipAt = Time.time;
                    if (Time.time - _pcAt < 0.4f || Time.time - _takeAt < 0.4f) _takeWhip = true; // swat / nothing: P8 spins instead of shaking
                    else if (!_dropAct && !_hiding && Free) // on a perch / in the door the stock code does nothing either
                    {
                        PlaySpin("whip around (" + BeatName + ")");
                        if (Beat == GameBeat.WhatIf && _window != null) { var w = _window; Schedule(0.4f, () => PlayRecoil(w.position, 0.08f, "the fire (away from the window)", 2.5f)); }
                    }
                    break;
            }
        }

        // ------------------------------------------------------------ beats
        void OnBeatCore(GameBeat from, GameBeat to)
        {
            LogBeatStats(from);
            if (to != GameBeat.Field && intensity >= 0f) _active = true; // back from the Field beat: this beat's moves count
            bool jump = to != from && (_dir == null || _dir.NextBeat(from) != to);
            if (jump || (to == GameBeat.Pre && from != GameBeat.Pre))
            {
                // a Board jump / reset: straight to the new beat's rest pose
                _jumpUntil = Time.time + 0.6f;
                _player.CancelAll(_mt, true);
                _gen++;
                _sched.Clear();
                _onLanded.Clear();
                _releaseOnLineEnd.Clear();
                if (_fl != null) { transform.position = _fl.end; _fl = null; _parkedSince = Time.time; _teleportFrame = Time.frameCount; }
                Log("Snap", "beat jump " + GameIds.BeatName(from) + " -> " + GameIds.BeatName(to));
            }
            _attachedThisBeat = false;
            if (from == GameBeat.Wasps && _hiding) { _hiding = false; _b.SetCaptionAnchor(null); }
            if (from == GameBeat.Heat) _player.Release("Wilt", _mt);
            if (from == GameBeat.Hornet) _player.Release("HumAlong", _mt);
            if (from == GameBeat.Map && _dropAct) { _dropAct = false; Log("DropTheAct off", "left the map"); }
            if (to == GameBeat.Pre) _pirouetteDone = false;

            if (to == GameBeat.Heat) PlayWilt("heat wave");
            if (to == GameBeat.Hornet) PlayHumAlong("hornet");
            if (from == GameBeat.Heat && to == GameBeat.WhatIf && !jump)
            {
                _lapWindowUntil = Time.time + 2.5f;
                Schedule(0.8f, () => { if (_fl == null || _fl.kind != "lap") PlayFourForFour("four for four (no flight to the window: in place)"); });
            }
            if (to == GameBeat.Escape && _countPending) { _countPending = false; EndPictureIt("the count: the roar", 0.06f); }
            if (to == GameBeat.Map)
            {
                _dropAct = true;
                _player.CancelAll(_mt, false);
                Log("DropTheAct", "map: face the player, no bob, nothing else");
            }
        }

        // ------------------------------------------------------------ lines
        static bool TryOutcome(string id, out GameStation s, out string choice)
        {
            if (_outcomes == null)
            {
                _outcomes = new Dictionary<string, (GameStation, string)>();
                foreach (GameStation st in Enum.GetValues(typeof(GameStation)))
                    foreach (var c in GameIds.Choices[(int)st])
                        if (GameLines.TryGetOutcome(st, c, out var o) && !string.IsNullOrEmpty(o.line.id)) _outcomes[o.line.id] = (st, c);
            }
            if (id != null && _outcomes.TryGetValue(id, out var v)) { s = v.s; choice = v.c; return true; }
            s = GameStation.Smoker;
            choice = null;
            return false;
        }

        void ScheduleWord(string id, string word, Action a, float fallback = -1f)
        {
            float t = MotionCurves.WordTime(_lineCaption, word, _lineClip);
            if (t < 0f) t = fallback;
            if (t < 0f) return;
            Schedule(t, () => { if (_lineId == id) a(); });
        }

        void ReleaseLineHolds()
        {
            for (int i = 0; i < _releaseOnLineEnd.Count; i++) _releaseOnLineEnd[i].released = _releaseOnLineEnd[i].released < 0f ? _mt : _releaseOnLineEnd[i].released;
            _releaseOnLineEnd.Clear();
        }

        void OnLineCore(GameNet.VoiceLineInfo info)
        {
            string id = info.id ?? "";
            ReleaseLineHolds();
            _lineId = id;
            _lineAt = Time.time;
            _lineClip = Mathf.Max(0f, info.clipSeconds);
            _lineCaption = info.caption ?? "";
            _lineActive = true;
            _forecastLine = id == "room_forecast" || id == "room_hook_1" || id == "room_hook_2";
            if (_pictureEndsOnLine && _player.Playing("PictureIt")) { _pictureEndsOnLine = false; EndPictureIt("next line " + id); }
            if (_busyWaiting && TryOutcome(id, out var os, out var oc) && _st != null && _st.Station == os) StartTake(os, oc, "outcome line " + id);
            if (_dropAct || !_active) return;

            switch (id)
            {
                case "room_smoker_intro":
                case "room_feed_intro":
                case "room_feed_prompt":
                case "room_wasps_intro":
                    PlayTakeFloor("question " + id);
                    return;
                case "room_heat_intro":
                    PlayTakeFloor("last problem " + id, 12f);
                    return;
                case "room_meet_1":
                    PlayTakeFloor("introduction " + id);
                    return;
                case "room_t0_4":
                    ScheduleWord(id, "Mind", () => PlayTakeFloor("mind your own beeswax"), 0.5f);
                    return;
                case "room_timeout":
                    PlayGavel("overruled " + id);
                    return;
                case "room_ack_minutes":
                    if (_st != null && _st.Busy && !_st.Choosing && Time.time - _takeAt > 1f) PlayGavel("noted for the minutes (timeout solve)");
                    return;
                case "room_strike_lookup":
                    PlayLookUp("look up " + id);
                    return;
                case "room_forecast":
                    PlayLook("Forecast", _window, Vector3.zero, 0.7f, 0.4f, "job perk: the window " + id, true);
                    Rig("Curious Tilt", "job perk");
                    return;
                case "room_hook_1":
                    {
                        Vector3 up = (_hive != null ? _hive.transform.position : transform.position) + Vector3.up * 0.8f;
                        ScheduleWord(id, "angry", () => PlayBrace("angry swarms " + id, up), 1.2f);
                        return;
                    }
                case "room_hook_2":
                    if (_rocket != null)
                    {
                        PlayLook("Track", _rocket, Vector3.zero, 0.6f, 0.3f, "the rocket " + id, true);
                        ScheduleWord(id, "window seat", () => { _player.Release("Track", _mt); _snapUntil = Time.time + 0.3f; });
                    }
                    return;
                case "room_meet_role":
                    ScheduleWord(id, "You signed", () => PlayJab("you signed"));
                    return;
                case "room_meet_2":
                    ScheduleWord(id, "Four problems", () => PlayNod("count-off: four problems"), 0f);
                    ScheduleWord(id, "Four minutes", () => PlayNod("count-off: four minutes"));
                    ScheduleWord(id, "Or you", () => PlayNod("count-off: or"));
                    if (_rocket != null) ScheduleWord(id, "rocket", () => PlayLook("Glance", _rocket, Vector3.zero, 0.6f, 0.15f, "glance at the rocket", false, 0.9f));
                    return;
                case "room_escape_minutes":
                    foreach (var w in new[] { "smoke", "syrup", "small", "shallow" }) { string ww = w; ScheduleWord(id, ww, () => PlayNod("for the minutes: " + ww)); }
                    return;
                case "room_escape_credit":
                    ScheduleWord(id, "Strike", () =>
                    {
                        Vector3 p = transform.position, e = _hasEye ? _ctx.eye : p - transform.forward;
                        PlayLook("LookAway", null, p + Quaternion.AngleAxis(25f, Vector3.up) * (e - p), 0.9f, 0.2f, "strike that", true);
                    });
                    return;
                case "room_t0_1":
                    PlayDoubleTake("non-refundable " + id);
                    Rig("Curious Tilt", "ticket");
                    return;
                case "room_crew_heavy":
                    {
                        Transform jar = HeavyItem();
                        if (jar != null)
                        {
                            PlayLook("Track", jar, Vector3.zero, 0.6f, 0.15f, "the jar's crew " + id, true);
                            PlayBark(jar.position, "lift with your wings");
                        }
                        return;
                    }
                case "room_live_filler":
                    _pictureEndsOnLine = true;
                    PlayPictureIt(UpAndAway(), null, "thinking " + id);
                    return;
                case "room_whatif_putout":
                    PlayFrantic("put it out " + id, _lineClip > 0f ? _lineClip : 2.5f);
                    return;
                case "room_whatif_notfire":
                    ScheduleWord(id, "Just not fire", () => PlayOffRecord("just not fire"));
                    return;
                case "room_hornet_4":
                    PlayRecoil(_hive != null ? _hive.transform.position + Vector3.up * 0.3f : transform.position + transform.forward, 0.06f, "stop, you're cooking the girls", 1f);
                    return;
                case "room_adjourn":
                    return; // THE COUNT starts when the line ends
            }
            if (id.StartsWith("room_hint_", StringComparison.Ordinal) || id == "room_smoker_banana") { PlayOffRecord("off the record " + id); return; }
            if (id.StartsWith("room_whatif_custom", StringComparison.Ordinal))
            {
                _pictureEndsOnScene = true;
                PlayPictureIt(Vector3.zero, _window, "picturing it " + id);
                return;
            }
            if (id.StartsWith("room_whatif_fire", StringComparison.Ordinal)) { ScheduleWord(id, "can't look", () => PlayCantLook("I can't look"), 2.5f); return; }
            if (id.StartsWith("room_whatif_out", StringComparison.Ordinal))
            {
                _player.Release("CantLook", _mt);
                _player.Release("Recoil", _mt);
                WhenLanded(() => PlayCarried("fire's out " + id, null), 1.5f, "Carried");
            }
        }

        void OnLineEnd()
        {
            var holds = new List<MotionTrack>(_releaseOnLineEnd);
            _releaseOnLineEnd.Clear();
            if (holds.Count > 0) Schedule(0.2f, () => { foreach (var h in holds) if (h.released < 0f) h.released = _mt; });
            if (_pictureEndsOnScene && _player.Playing("PictureIt")) { _pictureEndsOnScene = false; EndPictureIt("line end"); }
            if (_lineId == "room_adjourn" && !_dropAct)
            {
                _countPending = true;
                Vector3 band = (_window != null ? _window.position : transform.position) + Vector3.up * 0.35f;
                PlayPictureIt(band, null, "the count (waiting for the roar)");
            }
        }

        void OnSceneCore(string scene)
        {
            if (_pictureEndsOnScene && _player.Playing("PictureIt") && scene != GameIds.Scene.WhatIfCustom) { _pictureEndsOnScene = false; EndPictureIt("window " + scene); }
            if (scene == GameIds.Scene.FireOut) _player.Release("CantLook", _mt);
        }

        Transform HeavyItem()
        {
            if (_del == null) return null;
            for (int i = 0; i < _del.Count; i++)
            {
                var it = _del.Item(i);
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var di = it.GetComponent<DeliveryItem>();
                if (di != null && di.Heavy) return it;
            }
            return null;
        }

        // ------------------------------------------------------------ picks
        void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Log || msg == null || !msg.StartsWith("[VBS] choice ", StringComparison.Ordinal)) return;
            try { OnChoiceLog(msg); } catch (Exception) { } // never throw (or log) from inside the log callback
        }

        void OnChoiceLog(string msg)
        {
            string rest = msg.Substring(13);
            int sp = rest.IndexOf(' ');
            string sc = sp > 0 ? rest.Substring(0, sp) : rest;
            int slash = sc.IndexOf('/');
            if (slash <= 0) return;
            if (!GameIds.TryParseStation(sc.Substring(0, slash), out var s)) return;
            _pcStation = s;
            _pcChoice = sc.Substring(slash + 1);
            _pcAt = Time.time;
        }

        void OnPressed(ChoiceButton b)
        {
            // only a press the station can take: an ignored one (prop still flying, verdict playing) is not a pick
            if (b == null || _st == null || !_st.Choosing || !GameIds.IsValidChoice(_st.Station, b.ChoiceId)) return;
            _candStation = _st.Station;
            _candChoice = b.ChoiceId;
            _candAt = Time.time;
        }

        void OnChoiceRequested(GameStation s, string choice)
        {
            _candStation = s;
            _candChoice = choice;
            _candAt = Time.time;
        }

        Transform PropFor(GameStation station, string choice)
        {
            int idx = Array.IndexOf(GameIds.Choices[(int)station], choice);
            if (_del != null && _del.Active && idx >= 0 && idx < _del.Count) return _del.Item(idx);
            for (int i = 0; i < _buttons.Length; i++)
                if (_buttons[i] != null && _buttons[i].ChoiceId == choice && _buttons[i].gameObject.activeInHierarchy) return _buttons[i].transform;
            return null;
        }

        /// <summary>P6 THE TAKE: a short hitstop, then the crouch (0.2 s in, 0.15 s held) with a look at the pick; the verdict (P7/P8) on its release.</summary>
        void StartTake(GameStation station, string choice, string source)
        {
            _takeAt = Time.time;
            _busyWaiting = false;
            if (_dropAct) return;
            bool correct = GameIds.IsCorrect(station, choice);
            if (_final != null) { _player.Release("FinalAnswer", _mt); _final = null; }
            _player.Release("HoldFloor", _mt);
            _player.Release("TakeFloor", _mt);
            _player.Cancel("TableIt", _mt);
            _player.Cancel("LookUp", _mt); // a leftover look-up would tip the bow sideways
            if (Time.time - _whipAt < 0.4f) _takeWhip = true;
            Transform prop = PropFor(station, choice);
            _stampProp = prop; // the stamp lands on it: keep clear for a moment (ProtectedDiscs)
            string pick = GameIds.StationName(station) + "/" + choice + (correct ? " (right)" : " (wrong)");
            if (_hiding)
            {
                Log("Take", pick + " via " + source + " (hidden: the line carries it)");
                _lastWrong = !correct;
                return;
            }
            _hitstopUntil = Time.time + MotionCurves.TakeHitstop;
            PlayTake(prop, pick + " via " + source);
            bool mild = choice == GameIds.Choice.SprayWater || choice == GameIds.Choice.BiggerDoor || choice == GameIds.Choice.DeepBowl;
            bool glare = choice == GameIds.Choice.StoreHoney;
            Schedule(MotionCurves.TakeHitstop + MotionCurves.TakeIn + MotionCurves.TakeHold, () =>
            {
                _lastWrong = !correct;
                bool spin = _takeWhip;
                _takeWhip = false;
                WhenLanded(() =>
                {
                    if (correct)
                        PlayCarried("motion carried: " + pick, _del != null && _del.Active && _del.Spot != null ? _del.Spot : prop);
                    else
                    {
                        PlayGrievance("grievance: " + pick + (spin ? " (spin)" : ""), spin, glare ? prop : null);
                        if (mild) Schedule(0.35f, () => Rig("Sad", "grievance"));
                    }
                }, 2f, "verdict");
            });
        }

        // ------------------------------------------------------------ polling (every frame, before the controller)
        void Poll(float dt)
        {
            float now = Time.time;
            _choosingNow = _st != null && _st.Choosing;
            bool lineActive = _net != null && (_net.IsSpeaking || now < _lineAt + _lineClip);
            if (_lineActive && !lineActive) OnLineEnd();
            _lineActive = lineActive;

            float tp = _player.Any(t => t.pausesTick) ? 1f : lineActive ? 0.5f : 0f;
            _tickPause = Mathf.MoveTowards(_tickPause, tp, dt / 0.1f);

            bool talk = GameHands.AnyTalkHeld();
            if (talk && !_listening) PlayListen("pinch-talk");
            if (!talk && _listening) _player.Release("Listen", _mt);
            _listening = talk;

            bool held = _hive != null && _hive.Held;
            if (held && !_held && Vector3.Distance(transform.position, _hive.transform.position + Vector3.up * 0.34f) < 0.12f) PlayTumble();
            _held = held;

            PollStation();
            PollHands();

            if (_pcAt > _takeAt + 0.01f && now - _pcAt < 0.5f && _st != null && _st.Station == _pcStation) StartTake(_pcStation, _pcChoice, "choice log");
            if (_busyWaiting)
            {
                if (_candAt >= _busyAt - 0.5f && _st != null && _st.Station == _candStation) StartTake(_candStation, _candChoice, "button/request");
                else if (now - _busyAt > 0.35f) _busyWaiting = false; // busy without a pick we can name (the timeout solve's wait)
            }
        }

        void PlayTumble()
        {
            // FEED: the player lifts the hive from under him: pop up and aside (toward the podium side), roll, drop
            Vector3 side = _b.PresenterPoint != null ? Horizontal(_b.PresenterPoint.position - transform.position) : transform.right;
            Play(new MotionTrack
            {
                name = "Tumble", length = 0.9f, spin = true,
                eval = (tr, t) => new MotionSample
                {
                    world = Vector3.up * (0.06f * MotionCurves.Pulse(t, 0.2f, 0.3f, 0.4f)) + side * (0.10f * MotionCurves.OutCubic(t / 0.3f)) * MotionCurves.Pulse(t, 0.3f, 0.6f, 0f),
                    spinRoll = 360f * MotionCurves.InOutSine(t / 0.5f),
                }
            }, "the hive lifted under him");
        }

        void PollStation()
        {
            StationBase st = null;
            for (int i = 0; i < _stations.Length; i++)
                if (_stations[i] != null && _stations[i].IsActive) { st = _stations[i]; break; }
            if (st != _st)
            {
                if (_st != null)
                {
                    _player.Release("HoldFloor", _mt);
                    _player.Release("Deflate", _mt);
                    _player.Release("TakeFloor", _mt);
                    if (_final != null) { _player.Release("FinalAnswer", _mt); _final = null; }
                }
                _st = st;
                _del = st != null ? st.GetComponent<StationDelivery>() : null;
                _tabled = _p2Done = _lastWrong = _curiousThisOffer = false;
                _busyWaiting = false;
                _pChoosing = st != null && st.Choosing;
                _pBusy = st != null && st.Busy;
                _pSolved = st != null && st.Solved;
                _pParked = _del != null && _del.Active && _del.Parked;
                _pAllParked = _pParked && _del.AllParked;
                return;
            }
            if (st == null) return;
            float now = Time.time;
            bool choosing = st.Choosing, busy = st.Busy, solved = st.Solved;
            bool dActive = _del != null && _del.Active;
            bool parked = dActive && _del.Parked;
            bool allParked = parked && _del.AllParked;

            if (parked && !_pParked)
            {
                var s0 = st;
                Schedule(2f, () => { if (_st == s0 && !_p2Done && !s0.Busy && !s0.Solved) PlayTakeFloor("choices parked, no question line (fallback)"); });
            }
            if (allParked && !_pAllParked && !_tabled && !busy && !solved) { _tabled = true; PlayTableIt("choices parked in the arc"); }
            if (choosing && !_pChoosing)
            {
                _curiousThisOffer = false;
                if (!dActive && !_tabled) { _tabled = true; PlayTableIt("choices offered (no delivery)"); }
                if (_lastWrong)
                {
                    PlayHarrumph("retry offer");
                    var s1 = st;
                    Schedule(0.5f, () => { if (_st == s1 && s1.Choosing) PlayHoldFloor("the wait (retry: timers reset)"); });
                }
                else PlayHoldFloor("the wait");
            }
            if (!choosing && _pChoosing) _player.Release("HoldFloor", _mt);
            if (busy && !_pBusy && now - _pcAt > 0.5f) { _busyWaiting = true; _busyAt = now; }
            if (solved && !_pSolved && st.Station == GameStation.Heat) _player.Release("Wilt", _mt);
            _pChoosing = choosing;
            _pBusy = busy;
            _pSolved = solved;
            _pParked = parked;
            _pAllParked = allParked;
        }

        /// <summary>The choices that take a poke right now (delivered props, else the station's buttons).</summary>
        void ChoiceTargets()
        {
            _choiceT.Clear();
            _choiceP.Clear();
            if (_st == null) return;
            if (_del != null && _del.Active)
            {
                for (int i = 0; i < _del.Count; i++)
                {
                    var it = _del.Item(i);
                    if (it == null || !it.gameObject.activeInHierarchy || !_del.AcceptsPokeAt(i)) continue;
                    _choiceT.Add(it);
                    _choiceP.Add(DiscOf(it, 0f, "", out var d) ? d.center : it.position);
                }
                return;
            }
            for (int i = 0; i < _buttons.Length; i++)
            {
                var b = _buttons[i];
                if (b == null || !b.Interactable || !GameIds.IsValidChoice(_st.Station, b.ChoiceId)) continue;
                _choiceT.Add(b.transform);
                _choiceP.Add(b.transform.position);
            }
        }

        void PollHands()
        {
            if (_st == null || !_st.Choosing || _hiding || _dropAct)
            {
                if (_final != null) EndFinalAnswer("no longer choosing");
                _hoverIdx = -1;
                return;
            }
            if (!GameHands.HasAnySource) return;
            ChoiceTargets();
            if (_choiceP.Count == 0) return;
            float now = Time.time;
            int idx = -1;
            bool anyNear = false;
            for (int h = 0; h < 2; h++)
            {
                if (!GameHands.TryGetIndexTip((GameHand)h, out var tip)) continue;
                if (Vector3.Distance(tip, BodyPos) < 0.25f) continue; // a finger at Beeswax himself: no reaction
                int k = MotionCurves.HoverTarget(tip, _choiceP, 0.12f);
                if (k >= 0 && idx < 0) idx = k;
                if (MotionCurves.HoverTarget(tip, _choiceP, 0.20f) >= 0) anyNear = true;
            }
            if (idx >= 0)
            {
                _awaySince = -1f;
                if (idx != _hoverIdx) { _hoverIdx = idx; _hoverSince = now; }
                if (_final == null && now - _hoverSince >= 0.2f)
                {
                    StartFinalAnswer(_choiceT[idx], "fingertip on " + _choiceT[idx].name);
                    if (!_curiousThisOffer && !_lineActive) { Rig("Curious Tilt", "final answer?"); _curiousThisOffer = true; }
                }
                else if (_final != null) _final.targetT = _choiceT[idx]; // he follows the finger from prop to prop
                return;
            }
            _hoverIdx = -1;
            if (_final == null) return;
            if (anyNear) { _awaySince = -1f; return; }
            if (_awaySince < 0f) _awaySince = now;
            if (now - _awaySince >= 0.3f) EndFinalAnswer("finger away");
        }
    }
}
