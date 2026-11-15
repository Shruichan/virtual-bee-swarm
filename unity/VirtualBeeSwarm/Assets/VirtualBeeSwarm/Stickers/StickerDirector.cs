using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// Daniel's sticker bees as progress and puzzle help, all on or by the real table (nothing is a HUD):
    ///   - the UNION CARD leans on the table left of the hive from "Four problems. Four minutes." on: a fix slaps that
    ///     station's sticker on, a station Beeswax had to do himself gets the sleeping bee, wrong picks add red tallies,
    ///     the corner runs the clock;
    ///   - when a station's hint fires (StationDelivery's lean, or 12 s without a pick), the winking bee pops up over
    ///     the right choice's price tag and Beeswax's off-the-record clue line shows on a slip tucked behind the card;
    ///   - at the escape the score card carries the sticker collection and the tally "for the minutes".
    /// Installs itself into any scene with a GameDirector; reads the game through public state only (StationBase.Solved /
    /// Choosing / Busy, StakesDirector.Grievances, GameNet.VoiceLineStarted) plus one private flag by reflection
    /// (StationBase._timeoutSolving, with a line-id fallback). VBS_UITHEME=0 or AutoInstall = false turns it off.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class StickerDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static StickerDirector Instance { get; private set; }

        [Tooltip("The card's spot in the TableAnchor's space: on the table, left of the hive and its base plate.")]
        [SerializeField] Vector3 cardSpot = new Vector3(-0.375f, 0f, 0.03f);
        [Tooltip("How far the card leans back from flat on the table (degrees).")]
        [SerializeField] float cardTilt = 54f;
        [Tooltip("Without a StationDelivery hint: seconds of choosing without a pick before the winking bee shows.")]
        [SerializeField] float ownHintSeconds = 12f;
        [SerializeField] float hintStickerSize = 0.075f;

        public UnionCard Card => _card;
        public bool HintShowing => _hintStation >= 0;

        GameDirector _director;
        GameNet _net;
        Transform _anchor;
        readonly StationBase[] _stations = new StationBase[4];
        readonly StationDelivery[] _deliveries = new StationDelivery[4];
        readonly ChoiceButton[] _rightButtons = new ChoiceButton[4];
        ChoiceButton[] _allButtons = new ChoiceButton[0];
        readonly int[] _wrong = new int[4];
        readonly bool[] _slept = new bool[4];
        FieldInfo _timeoutField;
        UnionCard _card;
        int _lastGrievances, _lastClockSecond = int.MinValue;
        float _choosingSince = -1f, _nextSlowPoll;
        bool _wasChoosing;

        // hint sticker + clue slip
        Transform _hint;
        int _hintStation = -1;
        float _hintAt;
        UiCard _clue;
        TextMeshPro _clueText;
        float _clueShownAt = -1f, _clueHideAt = -1f;
        bool _clueOn, _whatIfClueDone;

        // end card
        Transform _strip;
        readonly Transform[] _stripBees = new Transform[4];
        TextMeshPro _stripLine;
        float _escapeAt = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            Instance = null;
            if (!AutoInstall || !UiTheme.Enabled) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<StickerDirector>() != null) return;
            var go = new GameObject("[VBS] Stickers");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<StickerDirector>();
        }

        void Awake() => Instance = this;

        System.Collections.IEnumerator Start()
        {
            for (int i = 0; i < UiThemeDirector.StartFrames + 1; i++) yield return null; // see UiThemeDirector.Start
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            foreach (var st in FindObjectsByType<StationBase>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                int i = (int)st.Station;
                if (i < 0 || i >= 4 || _stations[i] != null) continue;
                _stations[i] = st;
                _deliveries[i] = st.GetComponent<StationDelivery>();
            }
            _allButtons = FindObjectsByType<ChoiceButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in _allButtons)
                for (int i = 0; i < 4; i++)
                    if (_rightButtons[i] == null && b.ChoiceId == GameIds.CorrectChoice[i]) _rightButtons[i] = b;
            _timeoutField = typeof(StationBase).GetField("_timeoutSolving", BindingFlags.Instance | BindingFlags.NonPublic);
            { int forced = ForcedSlept(); if (forced >= 0 && forced < 4) _slept[forced] = true; }

            _anchor = FindAnchor();
            _card = UnionCard.Create(_anchor != null ? _anchor : transform);
            BuildClue();
            _hint = StickerArt.Quad(transform, StickerArt.Winking, hintStickerSize, "[VBS] HintSticker");
            _hint.gameObject.SetActive(false);

            if (_director != null)
            {
                _director.BeatChanged += OnBeatChanged;
                _director.GroupResetDone += ResetRun;
            }
            if (_net != null)
            {
                _net.VoiceLineStarted += OnVoiceLine;
                _net.IntentReceived += OnIntent;
            }
            Debug.Log("[VBS][Stickers] union card ready (" + (_anchor != null ? "on " + _anchor.name : "no TableAnchor") + "), stations " +
                      Count(_stations) + "/4, timeout flag " + (_timeoutField != null ? "found" : "by line id"));
            if (_director != null && _director.Beat >= GameBeat.Smoker && _director.Beat <= GameBeat.Map) ShowCard("joined at " + GameIds.BeatName(_director.Beat));
        }

        static int Count(StationBase[] a) { int n = 0; foreach (var s in a) if (s != null) n++; return n; }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_director != null)
            {
                _director.BeatChanged -= OnBeatChanged;
                _director.GroupResetDone -= ResetRun;
            }
            if (_net != null)
            {
                _net.VoiceLineStarted -= OnVoiceLine;
                _net.IntentReceived -= OnIntent;
            }
        }

        static bool UnderEndPanel(Transform t, Transform card)
        {
            for (; t != null && t != card; t = t.parent)
                if (t.name == "EndCard") return true;
            return false;
        }

        static Transform FindAnchor()
        {
            var rig = VirtualBeeSwarm.AR.ArRig.Instance;
            if (rig != null && rig.TableAnchor != null) return rig.TableAnchor;
            var d = FindAnyObjectByType<BeeDelivery>(FindObjectsInactive.Include);
            if (d != null && d.TableAnchor != null) return d.TableAnchor;
            var hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            return hive != null ? hive.transform.parent : null;
        }

        // ------------------------------------------------------------ game events
        void OnBeatChanged(GameBeat prev, GameBeat beat)
        {
            int was = StationIndex(prev);
            if (was >= 0) Settle(was); // left a station (also by an operator skip): what counts is whether it got solved
            HideHint();
            HideClue();
            _choosingSince = -1f;
            _wasChoosing = false;
            if (beat == GameBeat.Pre) { ResetRun(); return; }
            if (beat >= GameBeat.Smoker && beat <= GameBeat.Map) ShowCard(GameIds.BeatName(beat));
            if (beat == GameBeat.Escape)
            {
                _escapeAt = Time.time;
                _card.Celebrate();
                BuildStrip();
            }
        }

        /// <summary>Dev stills only: VBS_STICKERS_SLEPT=3 shows station 3 as done by Beeswax without waiting out its timeout.</summary>
        static int ForcedSlept()
        {
            try { return int.TryParse(Environment.GetEnvironmentVariable("VBS_STICKERS_SLEPT"), out int n) ? n : -1; }
            catch (Exception) { return -1; }
        }

        void ResetRun()
        {
            int forced = ForcedSlept();
            for (int i = 0; i < 4; i++) { _wrong[i] = 0; _slept[i] = i == forced; }
            _lastGrievances = 0;
            _lastClockSecond = int.MinValue;
            _escapeAt = -1f;
            _whatIfClueDone = false;
            HideHint();
            HideClue();
            if (_card != null) { _card.ResetCard(); _card.Show(false); }
            if (_strip != null) _strip.gameObject.SetActive(false);
        }

        void OnVoiceLine(GameNet.VoiceLineInfo line)
        {
            if (_director == null) return;
            // "Four problems. Four minutes. Or you're on that rocket.": the card is how you keep count
            if (line.id == Line("RoomMeet2", "room_meet_2", "").id && _director.Beat == GameBeat.Meet) ShowCard("room_meet_2");
            int st = StationIndex(_director.Beat);
            if (st < 0) return;
            if (line.id == Line("FeedLiftHint", "room_feed_lift_hint", "").id) ShowClue(line.caption, 9f);
            else if (_timeoutField == null && line.id == Line("TimeoutSolve", "room_ack_minutes", "").id &&
                     _stations[st] != null && !_stations[st].Solved) _slept[st] = true;
        }

        void OnIntent(string name, string topic, string text)
        {
            if (_director == null || _director.Beat != GameBeat.WhatIf) return;
            _whatIfClueDone = true; // they asked: no clue needed
            HideClue();
        }

        static int StationIndex(GameBeat beat)
        {
            switch (beat)
            {
                case GameBeat.Smoker: return 0;
                case GameBeat.Feed: return 1;
                case GameBeat.Wasps: return 2;
                case GameBeat.Heat: return 3;
                default: return -1;
            }
        }

        /// <summary>A GameLines entry by field name, so a renamed or cut line degrades to the fallback instead of a compile error.</summary>
        static GameLine Line(string field, string fallbackId, string fallbackCaption)
        {
            try
            {
                var f = typeof(GameLines).GetField(field, BindingFlags.Public | BindingFlags.Static);
                if (f != null && f.GetValue(null) is GameLine gl && !string.IsNullOrEmpty(gl.id)) return gl;
            }
            catch (Exception) { }
            return new GameLine(fallbackId, fallbackCaption);
        }

        static string ClueFor(int station)
        {
            switch (station)
            {
                case 0: return Line("RoomHintSmoker", "room_hint_smoker", "Off the record? Beekeepers don't swat. They puff.").caption;
                case 1: return Line("RoomHintFeed", "room_hint_feed", "Off the record? You don't know where store honey's been.").caption;
                case 2: return Line("RoomHintWasps", "room_hint_wasps", "Off the record? Think castle. Big gate or small gate?").caption;
                default: return Line("RoomHintHeat", "room_hint_heat", "Off the record? Bees can't swim. Where would a bee stand to drink?").caption;
            }
        }

        // ------------------------------------------------------------ the card
        void ShowCard(string why)
        {
            if (_card == null || _card.Visible) return;
            PlaceCard();
            _card.Show(true);
            Debug.Log("[VBS][Stickers] union card on the table (" + why + ") at " + _card.transform.position.ToString("F2"));
        }

        /// <summary>On the table left of the hive, leaning back, turned to the player's head; kept on the real table top.</summary>
        void PlaceCard()
        {
            var cam = Camera.main;
            Vector3 origin = _anchor != null ? _anchor.position : transform.position;
            Vector3 p = _anchor != null ? _anchor.TransformPoint(cardSpot) : origin + cardSpot;
            var rig = VirtualBeeSwarm.AR.ArRig.Instance;
            if (rig != null && rig.Room != null && rig.Room.HasTable)
            {
                Vector3 home = origin;
                for (int i = 0; i < 10 && !rig.Room.InsideTable(p, 0.1f); i++) p = Vector3.MoveTowards(p, home, 0.03f);
            }
            p.y = origin.y;
            Vector3 toHead = cam != null ? cam.transform.position - p : (_anchor != null ? -_anchor.forward : Vector3.back);
            toHead.y = 0f;
            if (toHead.sqrMagnitude < 0.01f) toHead = _anchor != null ? -_anchor.forward : Vector3.back;
            toHead.y = 0f;
            Vector3 away = -toHead.normalized;
            float lift = UnionCard.Height * 0.5f * Mathf.Sin(cardTilt * Mathf.Deg2Rad) + 0.004f;
            _card.transform.SetPositionAndRotation(p + Vector3.up * lift,
                Quaternion.LookRotation(away, Vector3.up) * Quaternion.Euler(90f - cardTilt, 0f, 0f));
        }

        /// <summary>The station's slot: sticker on if it was solved (sleeping bee if Beeswax did it).</summary>
        void Settle(int st)
        {
            var s = _stations[st];
            if (s == null || _card == null || !s.Solved || _card.State(st) != 0) return;
            _card.Stick(st, _slept[st]);
            Debug.Log("[VBS][Stickers] " + GameIds.Stations[st] + ": " + (_slept[st] ? "sleeping bee (Beeswax did it)" : "sticker bee-" + UnionCard.SlotBees[st]) +
                      ", " + _wrong[st] + " grievance tick(s)");
        }

        void Update()
        {
            if (_director == null || _card == null) return;
            float now = Time.time;
            var beat = _director.Beat;
            UpdateClock(beat);

            int st = StationIndex(beat);
            var s = st >= 0 ? _stations[st] : null;
            if (s != null)
            {
                // wrong picks: the stakes meter counts them the frame they are made
                var stakes = StakesDirector.Instance;
                bool slow = now >= _nextSlowPoll;
                if (slow) _nextSlowPoll = now + 0.2f;
                if (stakes != null)
                {
                    int g = stakes.Grievances;
                    if (g > _lastGrievances) { _wrong[st] += g - _lastGrievances; _card.SetTicks(st, _wrong[st]); }
                    _lastGrievances = g;
                }
                else if (slow)
                {
                    int marked = 0;
                    foreach (var b in _allButtons)
                        if (b != null && b.MarkedWrong && Array.IndexOf(GameIds.Choices[st], b.ChoiceId) >= 0) marked++;
                    if (marked > _wrong[st]) { _wrong[st] = marked; _card.SetTicks(st, marked); }
                }
                if (slow && _timeoutField != null && !_slept[st] && !s.Solved)
                {
                    try { if ((bool)_timeoutField.GetValue(s)) _slept[st] = true; }
                    catch (Exception) { _timeoutField = null; }
                }
                Settle(st);

                // the hint: StationDelivery's lean, or our own timer when the choices are plain buttons
                bool choosing = s.Choosing && !s.Busy && !s.Solved;
                if (choosing && !_wasChoosing) _choosingSince = now;
                _wasChoosing = choosing;
                var d = _deliveries[st];
                bool hinting = choosing && ((d != null && d.Active && d.Hinting) ||
                                            ((d == null || !d.Active) && ownHintSeconds > 0f && now - _choosingSince >= ownHintSeconds));
                if (hinting && _hintStation != st) ShowHint(st);
                else if (!choosing && _hintStation >= 0) { HideHint(); HideClue(); }
            }
            else if (beat == GameBeat.WhatIf && !_whatIfClueDone && _director.BeatSeconds > 16f && _net != null && !_net.IsSpeaking)
            {
                _whatIfClueDone = true;
                ShowClue(Line("RoomHintWhatif", "room_hint_whatif", "Try asking about a fire. ...Actually, don't.").caption, 14f);
            }

            UpdateHint(now);
            UpdateClue(now);
            UpdateStrip(now);
        }

        void UpdateClock(GameBeat beat)
        {
            if (!_card.Visible || beat < GameBeat.Smoker) return;
            float run = _director.RunSeconds;
            float left = _director.TimeLimitSeconds - run;
            int key = _director.RunFinished ? -1000000 - Mathf.FloorToInt(run) : Mathf.CeilToInt(left);
            if (key == _lastClockSecond) return;
            _lastClockSecond = key;
            _card.SetClock(_director.RunFinished ? "SAVED " + GameMaterials.Clock(run)
                : left > 0f ? "T-" + GameMaterials.Clock(Mathf.Ceil(left)) : "OVERTIME");
        }

        // ------------------------------------------------------------ hint sticker over the right price tag
        void ShowHint(int st)
        {
            _hintStation = st;
            _hintAt = Time.time;
            _hint.gameObject.SetActive(true);
            _hint.localScale = Vector3.zero;
            ShowClue(ClueFor(st), 0f);
            Debug.Log("[VBS][Stickers] hint: winking bee over " + GameIds.CorrectChoice[st] + "'s price tag + clue slip (" + GameIds.Stations[st] + ")");
        }

        void HideHint()
        {
            if (_hintStation < 0) return;
            _hintStation = -1;
            if (_hint != null) _hint.gameObject.SetActive(false);
        }

        void UpdateHint(float now)
        {
            if (_hintStation < 0 || _hint == null) return;
            var b = _rightButtons[_hintStation];
            var cam = Camera.main;
            if (b == null || !b.gameObject.activeInHierarchy || cam == null) { _hint.gameObject.SetActive(false); return; }
            if (!_hint.gameObject.activeSelf) _hint.gameObject.SetActive(true);
            Vector3 tag = b.transform.position;
            Vector3 toHead = cam.transform.position - tag;
            float dist = toHead.magnitude;
            if (dist < 0.05f) return;
            toHead /= dist;
            float k = UiTheme.Pop((now - _hintAt) / 0.35f);
            float bob = Mathf.Sin(now * 5f) * 0.006f;
            // on the tag's top right corner (the label stays readable), a little toward the eye so the prop never hides it
            Vector3 right = Vector3.Cross(Vector3.up, -toHead).normalized;
            _hint.position = tag + Vector3.up * (0.068f + bob) + right * 0.046f + toHead * 0.03f;
            _hint.rotation = Quaternion.LookRotation(-toHead, Vector3.up) * Quaternion.Euler(0f, 0f, Mathf.Sin(now * 2.5f) * 9f);
            _hint.localScale = new Vector3(hintStickerSize * k, hintStickerSize * k, 1f);
        }

        // ------------------------------------------------------------ clue slip, tucked behind the union card
        void BuildClue()
        {
            _clue = UiCard.Create(_card.transform, "ClueSlip", new Vector2(0.24f, 0.058f), UiCard.Look.Slip);
            _clue.transform.localPosition = new Vector3(0.012f, UnionCard.Height * 0.5f + 0.019f, 0.006f);
            _clue.transform.localRotation = Quaternion.Euler(0f, 0f, -3.5f);
            var bee = StickerArt.Quad(_clue.transform, StickerArt.Winking, 0.052f, "Bee");
            bee.localPosition = new Vector3(-0.094f, 0.004f, -0.004f);
            bee.localRotation = Quaternion.Euler(0f, 0f, 10f);
            _clueText = UiTheme.NewText(_clue.transform, "Text", "", 8.6f, UiTheme.Ink, new Vector2(0.168f, 0.044f), align: TextAlignmentOptions.Left);
            _clueText.enableAutoSizing = true;
            _clueText.fontSizeMax = 8.6f;
            _clueText.fontSizeMin = 5.5f;
            _clueText.transform.localPosition = new Vector3(0.026f, 0.0015f, -0.002f);
            _clue.gameObject.SetActive(false);
        }

        void ShowClue(string text, float seconds)
        {
            if (_clue == null || string.IsNullOrEmpty(text) || !_card.Visible) return;
            _clueText.text = text;
            _clue.gameObject.SetActive(true);
            _clue.transform.localScale = Vector3.zero;
            _clueOn = true;
            _clueShownAt = Time.time;
            _clueHideAt = seconds > 0f ? Time.time + seconds : -1f;
            Debug.Log("[VBS][Stickers] clue slip: " + text);
        }

        void HideClue()
        {
            if (!_clueOn) return;
            _clueOn = false;
            if (_clue != null) _clue.gameObject.SetActive(false);
        }

        void UpdateClue(float now)
        {
            if (!_clueOn) return;
            if (_clueHideAt > 0f && now >= _clueHideAt) { HideClue(); return; }
            float k = UiTheme.Pop((now - _clueShownAt) / 0.4f);
            _clue.transform.localScale = new Vector3(k, k, 1f);
        }

        // ------------------------------------------------------------ end card: the collection, for the minutes
        void BuildStrip()
        {
            var esc = FindAnyObjectByType<EscapeSequence>(FindObjectsInactive.Include);
            GameObject scoreCard = null;
            if (esc != null)
                scoreCard = typeof(EscapeSequence).GetField("scoreCard", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(esc) as GameObject;
            if (scoreCard == null && _anchor != null) { var t = _anchor.Find("ScoreCard"); scoreCard = t != null ? t.gameObject : null; }
            if (scoreCard == null) { Debug.Log("[VBS][Stickers] no score card: the sticker collection stays on the union card"); return; }

            const float w = 0.34f, h = 0.112f, z = -0.002f;
            if (_strip == null || _strip.parent != scoreCard.transform)
            {
                if (_strip != null) Destroy(_strip.gameObject);
                _strip = new GameObject("[VBS] StickerStrip").transform;
                _strip.SetParent(scoreCard.transform, false);
                UiCard.Create(_strip, "Card", new Vector2(w, h), UiCard.Look.Paper);
                var title = UiTheme.NewText(_strip, "Title", "FOR THE MINUTES", 9.5f, UiTheme.Brown, new Vector2(w - 0.03f, 0.014f), display: true, wrap: false);
                title.transform.localPosition = new Vector3(0f, h * 0.5f - 0.0175f, z);
                for (int i = 0; i < 4; i++)
                {
                    var cell = StickerArt.Shape(_strip, true, UiTheme.Faded, new Vector2(0.046f, 0.046f), "Cell" + i);
                    cell.localPosition = new Vector3((i - 1.5f) * 0.07f, 0.002f, z);
                    _stripBees[i] = StickerArt.Quad(_strip, UnionCard.SlotBees[i], 0.052f, "Bee" + i);
                    _stripBees[i].localPosition = new Vector3((i - 1.5f) * 0.07f, 0.002f, z * 2.5f);
                }
                _stripLine = UiTheme.NewText(_strip, "Line", "", 7.6f, UiTheme.Ink, new Vector2(w - 0.028f, 0.014f), wrap: false);
                _stripLine.enableAutoSizing = true;
                _stripLine.fontSizeMax = 7.6f;
                _stripLine.fontSizeMin = 5f;
                _stripLine.transform.localPosition = new Vector3(0f, -h * 0.5f + 0.0175f, z);
            }

            // under the card's art, in metres whatever the card's scale
            Transform ct = scoreCard.transform;
            Vector3 ls = ct.lossyScale;
            float sx = Mathf.Max(1e-4f, Mathf.Abs(ls.x)), sy = Mathf.Max(1e-4f, Mathf.Abs(ls.y)), sz = Mathf.Max(1e-4f, Mathf.Abs(ls.z));
            float minY = float.PositiveInfinity, minZ = 0f, lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            bool any = false;
            Matrix4x4 toCard = ct.worldToLocalMatrix;
            foreach (var r in scoreCard.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r.GetComponent<TextMesh>() != null || r.transform.IsChildOf(_strip)) continue;
                if (r.name.StartsWith("[VBS]", StringComparison.Ordinal)) continue;
                if (UnderEndPanel(r.transform, ct)) continue; // the hidden end panel's 0.33 m backing put the strip on the table
                Bounds lb = r.localBounds;
                Matrix4x4 m = toCard * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)));
                    if (c.y < minY) minY = c.y;
                    if (!any || c.z < minZ) minZ = c.z;
                    if (c.x < lo) lo = c.x;
                    if (c.x > hi) hi = c.x;
                    any = true;
                }
            }
            float midX = any ? (lo + hi) * 0.5f : 0f;
            if (!any) { minY = -0.055f / sy; minZ = 0f; }
            _strip.localScale = new Vector3(1f / sx, 1f / sy, 1f / sz);
            _strip.localRotation = Quaternion.identity;
            _strip.localPosition = new Vector3(midX, minY - (h * 0.5f + 0.01f) / sy, minZ);
            _strip.gameObject.SetActive(true);

            int fixedByPlayer = 0, slept = 0, grievances = 0;
            for (int i = 0; i < 4; i++)
            {
                int state = _card.State(i);
                if (state == 1) fixedByPlayer++;
                else if (state == 2) slept++;
                grievances += _wrong[i];
                _stripBees[i].gameObject.SetActive(state != 0);
                StickerArt.SetBee(_stripBees[i], state == 2 ? StickerArt.Sleeping : UnionCard.SlotBees[i]);
                _stripBees[i].localScale = Vector3.zero;
            }
            _stripLine.text = Minutes(fixedByPlayer, slept, grievances);
            Debug.Log("[VBS][Stickers] end card: " + _stripLine.text);
        }

        /// <summary>The tally line under the collection, in Beeswax's paperwork voice.</summary>
        public static string Minutes(int fixedByPlayer, int slept, int grievances)
        {
            if (fixedByPlayer == 4 && grievances == 0) return "FOUR FIXES. ZERO GRIEVANCES. SUSPICIOUS.";
            string s = fixedByPlayer + (fixedByPlayer == 1 ? " FIX" : " FIXES");
            if (slept > 0) s += " · " + slept + " BY BEESWAX";
            s += " · " + grievances + (grievances == 1 ? " GRIEVANCE" : " GRIEVANCES") + " ON FILE";
            return s;
        }

        void UpdateStrip(float now)
        {
            if (_strip == null || _escapeAt < 0f || !_strip.gameObject.activeInHierarchy) return;
            for (int i = 0; i < 4; i++)
            {
                var b = _stripBees[i];
                if (b == null || !b.gameObject.activeSelf) continue;
                float t = (now - _escapeAt - 2.2f - i * 0.2f) / 0.32f; // after the crown has set the card down
                float k = t <= 0f ? 0f : UiTheme.Pop(t);
                b.localScale = new Vector3(0.052f * k, 0.052f * k, 1f);
                b.localRotation = Quaternion.Euler(0f, 0f, (i % 2 == 0 ? -6f : 7f) + (1f - Mathf.Clamp01(t)) * 25f);
            }
        }
    }
}
