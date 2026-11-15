using System;
using System.Text.RegularExpressions;

namespace VirtualBeeSwarm.Ask
{
    public enum AskState { Closed, Idle, Listening, Thinking, Answering }

    /// <summary>What <see cref="AskBeeswax"/> should do after an AskFlow call (several at once).</summary>
    [Flags]
    public enum AskCue
    {
        None = 0,
        StopVoice = 1 << 0,      // barge-in: the mic must never hear Beeswax
        Tilt = 1 << 1,           // Look Around (thinking)
        BuzzOn = 1 << 2,
        BuzzOff = 1 << 3,
        Nod = 1 << 4,            // an answer started
        Shake = 1 << 5,          // the refusal started
        StampApproved = 1 << 6,  // an answer ended: one more question in the minutes
        StampGrievance = 1 << 7,
        SayStall = 1 << 8,       // nothing came back in time (local backstop)
        SayOffline = 1 << 9,
        SayNotHeard = 1 << 10,   // empty transcript and the Hive said nothing either
        Status = 1 << 11,        // the card's status text changed
        Minutes = 1 << 12,       // the card's minutes counter changed
    }

    /// <summary>
    /// The ASK BEESWAX card's state machine, free of Unity objects so the EditMode tests can drive it with plain times:
    /// Closed -> Idle (card up) -> Listening (talk held) -> Thinking (released; tilt every 2.5 s, buzz at most 6 s) ->
    /// Answering (Beeswax's reply line plays; the filler line doesn't count) -> Idle (stamp, minutes +1).
    /// Backstops: no AwaitingReply within 0.5 s of release = a short press (hint); an empty transcript with nothing said
    /// for 2 s = ask_not_heard; no line at all 10 s after release = ask_stall (the Hive's own worst case is about 10 s). With the card closed it still gives the
    /// thinking feedback while the Hive works on any voice reply (AwaitingReply), in every voice beat.
    /// Strings are built only on events (never per frame).
    /// </summary>
    public sealed class AskFlow
    {
        public const string StatusIdle = "Hold pinch or B/Y. Say “Hey Beeswax,” then release.";
        public const string StatusFollowUp = "Hold to ask a follow-up. Release to send.";
        public const string StatusListening = "Listening. Release to send.";
        public const string StatusShort = "Hold while you talk, then release.";
        public const string StatusNotHeard = "Didn't catch that. Try again or pick a question.";
        public const string StatusOffline = "Can't reach the hive. Poke a question.";
        public const string StatusMicPermission = "Allow microphone access or pick a question.";
        public const string StatusMicUnavailable = "Mic unavailable. Pick a question.";
        public const string StatusWakeWaiting = "Let Beeswax finish, then hold to talk.";
        public const string StatusWakeMissing = "Start with “Hey Beeswax,” then ask a question.";
        public static readonly string[] StatusThinking = { "Checking the spreadsheet", "Checking the spreadsheet.", "Checking the spreadsheet..", "Checking the spreadsheet..." };

        public bool BargeIn = true;
        public float WatchdogSeconds = 10f;
        public float ShortPressSeconds = 0.5f;
        public float TiltEverySeconds = 2.5f;
        public float BuzzMaxSeconds = 6f;
        public float NotHeardWaitSeconds = 2f;
        /// <summary>Closed card: stop the thinking feedback this long after the reply was routed if no line came.</summary>
        public float ClosedGraceSeconds = 3f;

        public AskState State { get; private set; } = AskState.Closed;
        public bool IsOpen => State != AskState.Closed;
        public int Minutes { get; private set; }
        public string Status { get; private set; } = "";
        public bool Thinking => _thinkingSince >= 0f;
        public bool Buzzing { get; private set; }
        public bool ExpectingReply => _expectReply;

        float _releasedAt = -1f, _thinkingSince = -1f, _nextTilt = -1f, _dotsAt = -1f, _answerAt = -1f, _emptyAt = -1f, _awaitEndedAt = -1f;
        int _dots;
        bool _expectReply, _sawAwaiting, _noCount, _refusal, _silentOk, _wasAwaiting;
        bool _dialogueOpen;
        string IdleStatus => _dialogueOpen ? StatusFollowUp : StatusIdle;
        static readonly Regex WakePrefix = new Regex(
            @"^\s*[""“']?hey[\s,.!?]+bee(?:['’]?s|z)[\s-]*wax\b[\s,.:;!?…–—""'”]*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        // ------------------------------------------------------------ open / close
        public AskCue Open()
        {
            if (IsOpen) return AskCue.None;
            State = AskState.Idle;
            return SetStatus(IdleStatus);
        }

        public AskCue Close(bool resetMinutes = false)
        {
            var cue = StopThinking();
            State = AskState.Closed;
            _expectReply = false;
            _dialogueOpen = false;
            if (resetMinutes && Minutes != 0) { Minutes = 0; cue |= AskCue.Minutes; }
            return cue;
        }

        // ------------------------------------------------------------ talk
        public AskCue TalkStarted(float now)
        {
            var cue = StopThinking();
            _expectReply = _noCount = _refusal = _silentOk = false;
            _emptyAt = _answerAt = -1f;
            if (!IsOpen) return cue;
            State = AskState.Listening;
            if (BargeIn) cue |= AskCue.StopVoice;
            return cue | SetStatus(StatusListening);
        }

        public AskCue TalkEnded(float now)
        {
            if (!IsOpen) return AskCue.None;
            _releasedAt = now;
            _expectReply = true;
            _sawAwaiting = false;
            State = AskState.Thinking;
            return StartThinking(now);
        }

        public AskCue MicrophoneUnavailable(bool hasPermission)
        {
            return State == AskState.Idle
                ? SetStatus(hasPermission ? StatusMicUnavailable : StatusMicPermission)
                : AskCue.None;
        }

        public AskCue WaitForVoice()
        {
            return IsOpen ? SetStatus(StatusWakeWaiting) : AskCue.None;
        }

        public AskCue WakeRejected(bool heardWords)
        {
            if (!IsOpen || !_expectReply) return AskCue.None;
            _expectReply = false;
            State = AskState.Idle;
            return StopThinking() | SetStatus(heardWords ? StatusWakeMissing : StatusNotHeard);
        }

        public AskCue DialogueOpened()
        {
            _dialogueOpen = true;
            if (!IsOpen) return AskCue.None;
            _expectReply = false;
            State = AskState.Idle;
            return StopThinking() | SetStatus(StatusFollowUp);
        }

        public AskCue SetDialogueOpen(bool open)
        {
            if (_dialogueOpen == open) return AskCue.None;
            bool showIdle = State == AskState.Idle && Status == IdleStatus;
            _dialogueOpen = open;
            return showIdle ? SetStatus(IdleStatus) : AskCue.None;
        }

        public static bool TryWakePhrase(string text, out string question)
        {
            question = "";
            var match = WakePrefix.Match(text ?? "");
            if (!match.Success) return false;
            string suffix = match.Groups[1].Value.Trim();
            foreach (char c in suffix)
                if (char.IsLetterOrDigit(c)) { question = suffix; break; }
            return true;
        }

        public static string WakeQuestion(string text) => TryWakePhrase(text, out var question) ? question : "";

        /// <summary>A press while the Hive is unreachable: Beeswax says so (the card's chips still work).</summary>
        public AskCue OfflinePress(float now)
        {
            if (!IsOpen) return AskCue.None;
            var cue = StopThinking();
            _expectReply = _sawAwaiting = true; // a local line, no Hive round trip: never a "short press"
            _noCount = true;
            _releasedAt = now;
            State = AskState.Thinking;
            return cue | AskCue.SayOffline | AskCue.StopVoice | SetStatus(StatusOffline);
        }

        /// <summary>A chip was poked: its canned line plays at once (the caller says it right after).</summary>
        public AskCue Chip(string question, float now)
        {
            if (!IsOpen) return AskCue.None;
            var cue = StopThinking() | AskCue.StopVoice;
            _expectReply = _sawAwaiting = true;
            _noCount = _refusal = _silentOk = false;
            _releasedAt = now;
            State = AskState.Thinking;
            return cue | SetStatus(Asked(question));
        }

        // ------------------------------------------------------------ Hive replies
        public AskCue Transcript(string text, bool final, float now)
        {
            if (!final || !IsOpen || !_expectReply) return AskCue.None;
            _sawAwaiting = true; // Fast replies may arrive before Tick sees the pending flag.
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                _noCount = true;
                _emptyAt = now;
                return SetStatus(StatusNotHeard);
            }
            return SetStatus(Asked(text));
        }

        /// <summary>The Hive routed the question. "unknown" with nothing to say (his own echo) is not a stall.</summary>
        public AskCue Intent(string name)
        {
            if (IsOpen && _expectReply && name == "unknown") _silentOk = true;
            if (IsOpen && _expectReply && name == "ack") _noCount = true; // "thanks" gets a reply, not a line in the minutes
            return AskCue.None;
        }

        /// <summary>A line of Beeswax's started. reply = it can be the answer to the question (a live line or one of the
        /// ask lines; a scripted room line that happens to start meanwhile is not). counts = a real answer: the stall,
        /// "didn't catch that", the off-list and people lines get no stamp and no minute.</summary>
        public AskCue LineStarted(string id, float now, bool reply = true, bool counts = true)
        {
            if (!_expectReply && !Thinking) return AskCue.None;
            if (!reply && IsOpen) return AskCue.None; // keep waiting for the real reply
            var cue = StopThinking();
            if (!counts) _noCount = true;
            if (id == AskLines.FillerId) return cue; // "I could drone on all day. Short version:" ... the answer follows
            if (!_expectReply) return cue;            // closed card: the reply is here, the feedback stops
            _expectReply = false;
            _emptyAt = -1f;
            _refusal = id == AskLines.RefuseId;
            if (!IsOpen) return cue | (_refusal ? AskCue.Shake : AskCue.Nod);
            State = AskState.Answering;
            _answerAt = now;
            return cue | (_refusal ? AskCue.Shake | AskCue.StampGrievance : AskCue.Nod);
        }

        // ------------------------------------------------------------ every frame (allocation-free)
        public AskCue Tick(float now, bool awaiting, bool speaking)
        {
            var cue = AskCue.None;
            if (awaiting) _sawAwaiting = true;
            if (_wasAwaiting && !awaiting) _awaitEndedAt = now;
            _wasAwaiting = awaiting;

            if (!IsOpen)
            {
                // Thinking feedback in any voice beat: from AwaitingReply until a line starts (or a short grace after).
                if (awaiting && !speaking && !Thinking) cue |= StartThinking(now);
                else if (Thinking && !awaiting && _awaitEndedAt >= 0f && now - _awaitEndedAt > ClosedGraceSeconds) cue |= StopThinking();
            }
            else if (State == AskState.Idle && Status == StatusWakeWaiting && !speaking && !awaiting)
            {
                cue |= SetStatus(IdleStatus);
            }
            else if (State == AskState.Thinking && _expectReply)
            {
                if (!_sawAwaiting && !_noCount && now - _releasedAt > ShortPressSeconds)
                {
                    _expectReply = false;
                    State = AskState.Idle;
                    return cue | StopThinking() | SetStatus(StatusShort);
                }
                if (_emptyAt >= 0f && !speaking && now - _emptyAt > NotHeardWaitSeconds)
                {
                    _emptyAt = -1f;
                    cue |= AskCue.SayNotHeard;
                }
                else if (!_silentOk && now - _releasedAt > WatchdogSeconds)
                {
                    _noCount = true;
                    _releasedAt = now; // the stall line itself gets a fresh window
                    cue |= AskCue.SayStall;
                }
                else if (_silentOk && !awaiting && !speaking && now - _releasedAt > 1f)
                {
                    _expectReply = false;
                    State = AskState.Idle;
                    return cue | StopThinking() | SetStatus(IdleStatus);
                }
            }
            else if (State == AskState.Answering && !speaking && now - _answerAt > 0.3f)
            {
                State = AskState.Idle;
                bool counts = !_noCount && !_refusal;
                if (counts) { Minutes++; cue |= AskCue.StampApproved | AskCue.Minutes; }
                cue |= SetStatus(IdleStatus);
            }

            if (Thinking)
            {
                if (now >= _nextTilt) { _nextTilt = now + TiltEverySeconds; cue |= AskCue.Tilt; }
                if (Buzzing && now - _thinkingSince > BuzzMaxSeconds) { Buzzing = false; cue |= AskCue.BuzzOff; }
                if (IsOpen && now >= _dotsAt)
                {
                    _dotsAt = now + 0.4f;
                    _dots = (_dots + 1) % StatusThinking.Length;
                    cue |= SetStatusIfThinking(StatusThinking[_dots]);
                }
            }
            return cue;
        }

        // ------------------------------------------------------------ helpers
        AskCue StartThinking(float now)
        {
            _thinkingSince = now;
            _nextTilt = now + TiltEverySeconds;
            _dotsAt = now + 0.4f;
            _dots = 0;
            Buzzing = true;
            var cue = AskCue.Tilt | AskCue.BuzzOn;
            if (IsOpen && Status != StatusNotHeard && !Status.StartsWith("You asked", StringComparison.Ordinal)) cue |= SetStatus(StatusThinking[0]);
            return cue;
        }

        AskCue StopThinking()
        {
            if (!Thinking) return AskCue.None;
            _thinkingSince = -1f;
            if (!Buzzing) return AskCue.None;
            Buzzing = false;
            return AskCue.BuzzOff;
        }

        AskCue SetStatusIfThinking(string s)
        {
            // keep "You asked: ..." and the not-heard hint up; only the plain thinking line animates its dots
            if (Status.Length > 0 && !Status.StartsWith("Checking", StringComparison.Ordinal)) return AskCue.None;
            return SetStatus(s);
        }

        AskCue SetStatus(string s)
        {
            if (s == Status) return AskCue.None;
            Status = s;
            return AskCue.Status;
        }

        /// <summary>You asked: "why bananas" (quotes, trimmed, at most 60 characters).</summary>
        public static string Asked(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length > 60) t = t.Substring(0, 57) + "...";
            return "You asked: \"" + t + "\"";
        }

        public static string MinutesText(int n) => "MINUTES: " + n + (n == 1 ? " QUESTION NOTED" : " QUESTIONS NOTED");
    }
}
