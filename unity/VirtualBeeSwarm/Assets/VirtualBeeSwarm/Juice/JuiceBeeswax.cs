using UnityEngine;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// Beeswax between lines (feel audit gap 6), through BeeswaxRig.Play only (if the rig is in the scene):
    ///   choosing    while a station waits for a pick and he is quiet: a small move every 5-7 s (Wag, Antenna Twitch,
    ///               Look Around, Thorn Flick, Curious Tilt), starting 2.5 s after his last line.
    ///   picks       right: Celebrate, wrong: Startle, at the slam (not 1-3 s later with the line).
    ///   stakes      the dark dive: Startle. Window scene change: Curious Tilt. Hive lifted: Startle.
    ///   AI          mic open (the player is talking): Curious Tilt held up every 2.4 s + antenna; waiting for the reply:
    ///               Look Around / Antenna Twitch every 1.7 s, so "he's thinking" is visible (left to AskBeeswax when
    ///               that lane is in the build and on: it tilts and buzzes in step with its card).
    /// While a line plays only Blink / Antenna / Thorn layers are used, never a gesture over the line's own.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class JuiceBeeswax : MonoBehaviour
    {
        static readonly string[] Idles = { "Wag", "Antenna Twitch", "Look Around", "Thorn Flick", "Curious Tilt", "Double Blink" };

        BeeswaxRig _rig;
        GameNet _net;
        GameDirector _director;
        float _nextFind, _nextIdle, _nextAi;
        int _idle, _ai;
        bool _wasSpeaking;

        public int Moves { get; private set; }

        // The ASK BEESWAX lane, when it is in the build and on, owns the thinking pose (tilt + buzz + card status in
        // step). Looked up by name once, so Juice/ still compiles and runs without AskBeeswax/.
        bool _askChecked, _askThinks;

        bool AskOwnsThinking()
        {
            if (_askChecked) return _askThinks;
            _askChecked = true;
            var type = System.Type.GetType("VirtualBeeSwarm.Ask.AskBeeswax");
            var ask = type != null ? FindAnyObjectByType(type) as Behaviour : null;
            var on = type != null ? type.GetProperty("On") : null;
            _askThinks = ask != null && ask.isActiveAndEnabled && (on == null || (bool)on.GetValue(ask));
            if (_askThinks) Debug.Log("[VBS][Juice] beeswax: the thinking pose is AskBeeswax's");
            return _askThinks;
        }

        public void Bind(GameNet net, GameDirector director) { _net = net; _director = director; }

        bool Speaking => _net != null && _net.IsSpeaking;

        bool Play(string clip, string why)
        {
            if (_rig == null || !_rig.Ready) return false;
            bool ok = _rig.Play(clip, "juice: " + why);
            if (ok) Moves++;
            return ok;
        }

        public void Pick(bool right)
        {
            if (Speaking) { Play(right ? "Thorn Flick" : "Double Blink", right ? "right pick (line playing)" : "wrong pick (line playing)"); Play("Antenna Twitch", "pick"); }
            else Play(right ? "Celebrate" : "Startle", right ? "right pick" : "wrong pick");
            _nextIdle = Time.time + 4f;
        }

        public void Flinch(string why)
        {
            if (Speaking) Play("Double Blink", why);
            else Play("Startle", why);
            _nextIdle = Time.time + 4f;
        }

        public void Curious(string why)
        {
            if (!Speaking) Play("Curious Tilt", why);
            else Play("Antenna Twitch", why);
        }

        void Update()
        {
            float now = Time.time;
            if (_rig == null)
            {
                if (now < _nextFind) return;
                _nextFind = now + 2f;
                _rig = FindAnyObjectByType<BeeswaxRig>();
                if (_rig == null) return;
            }
            if (!_rig.Ready || !_rig.isActiveAndEnabled) return;

            bool speaking = Speaking;
            if (speaking) { _wasSpeaking = true; return; }
            if (_wasSpeaking) { _wasSpeaking = false; _nextIdle = Mathf.Max(_nextIdle, now + 2.5f); }

            // AI moments first: listening to the player, then thinking about the reply
            if (_net != null && _net.MicListening)
            {
                if (now >= _nextAi) { _nextAi = now + 2.4f; Play((_ai++ & 1) == 0 ? "Curious Tilt" : "Antenna Twitch", "listening"); }
                _nextIdle = Mathf.Max(_nextIdle, now + 3f);
                return;
            }
            if (_net != null && _net.AwaitingReply)
            {
                if (!AskOwnsThinking() && now >= _nextAi) { _nextAi = now + 1.7f; Play((_ai++ & 1) == 0 ? "Look Around" : "Antenna Twitch", "thinking"); }
                _nextIdle = Mathf.Max(_nextIdle, now + 3f);
                return;
            }

            var beat = _director != null ? _director.Beat : GameBeat.Pre;
            bool choosing = beat >= GameBeat.Smoker && beat <= GameBeat.WhatIf;
            if (!choosing || now < _nextIdle) return;
            _nextIdle = now + Random.Range(5f, 7f);
            if (Play(Idles[_idle % Idles.Length], "between lines")) _idle++;
        }
    }
}
