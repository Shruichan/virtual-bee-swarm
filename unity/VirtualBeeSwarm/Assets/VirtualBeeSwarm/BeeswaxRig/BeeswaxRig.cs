using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Daniel's rigged Beeswax (Art/Beeswax/Rig/Beeswax.fbx, Generic rig, 21 baked in-place clips) on the Animator that
    /// Editor/BeeswaxRigBuilder generates. Hover loops on the base layer; masked override layers (BeeswaxRig.Layers) play
    /// the rest, so one gesture never resets unrelated bones (the clips are baked on every bone).
    ///
    /// Nothing calls this: it listens. GameNet.VoiceLineStarted (line id -> gesture, LineCues + the station outcome table:
    /// right pick = Yes Nod, wrong = No Shake), GameNet.IsSpeaking (Talk on Mouth / Mouth O), GameNet.SceneChanged
    /// (whatif_fire = Startle), GameDirector.BeatChanged (meet = Look Around, escape = Celebrate), and its own re-enable in
    /// T-0 (BeeswaxController.SetVisible(true) after the crash landing = Sooty Shake). Idle: Blink / Double Blink every
    /// 3-6 s, Antenna Twitch, Wag or a thorn move when nothing else plays; Arm Swing while he flies.
    /// BeeswaxController keeps the root (fly, face the player, bob, voice pulse, dusty tint). With the rig it must not
    /// flap wings or scale the mouth: RigDriven (if the controller has it) is set on Awake, and the prefab's
    /// Body/Mouth, Body/WingL, Body/WingR are empty targets, so an unpatched controller moves nothing visible either.
    /// Every trigger logs "[VBS][BeeswaxRig] ...".
    /// </summary>
    [DisallowMultipleComponent]
    public class BeeswaxRig : MonoBehaviour
    {
        // ------------------------------------------------------------------ the controller layout (the builder reads this)
        public enum Layer { Base = 0, Arms, Antenna, Thorn, Blink, Talk, Face, Gesture, Blush }

        /// <summary>Bones each override layer may move (Generic AvatarMask, by bone name under the armature).</summary>
        public static readonly Dictionary<Layer, string[]> Masks = new Dictionary<Layer, string[]>
        {
            { Layer.Arms, new[] { "Leg_Front_Upper.L", "Leg_Front_Upper.R" } },
            { Layer.Antenna, new[] { "Antenna_Base.L", "Antenna_Tip.L", "Antenna_Base.R", "Antenna_Tip.R" } },
            { Layer.Thorn, new[] { "Thorn" } },
            { Layer.Blink, new[] { "Eyelid.L", "Eyelid.R" } },
            { Layer.Talk, new[] { "Mouth", "Mouth O" } },
            // above Talk, so an angry/sad frown hides the smile while Talk keeps opening the O
            { Layer.Face, new[] { "Head", "Antenna_Base.L", "Antenna_Tip.L", "Antenna_Base.R", "Antenna_Tip.R", "Eyelid.L", "Eyelid.R",
                                  "Angry Brow.L", "Angry Brow.R", "Angry Frown", "Mouth" } },
            { Layer.Gesture, new[] { "Head", "Antenna_Base.L", "Antenna_Tip.L", "Antenna_Base.R", "Antenna_Tip.R", "Abdomen",
                                     "Leg_Front_Upper.L", "Leg_Front_Upper.R" } },
            { Layer.Blush, new[] { "Blush.L", "Blush.R" } },
        };

        /// <summary>Every state: (layer, clip name = state name). Hover is the base layer's default; Walk is there unused.</summary>
        public static readonly (Layer layer, string clip)[] States =
        {
            (Layer.Base, "Hover"), (Layer.Base, "Walk"),
            (Layer.Arms, "Arm Swing"),
            (Layer.Antenna, "Antenna Twitch"),
            (Layer.Thorn, "Thorn Bob"), (Layer.Thorn, "Thorn Flick"), (Layer.Thorn, "Thorn Spin"),
            (Layer.Blink, "Blink"), (Layer.Blink, "Double Blink"),
            (Layer.Talk, "Talk"),
            (Layer.Face, "Angry"), (Layer.Face, "Sad"),
            (Layer.Gesture, "Yes Nod"), (Layer.Gesture, "No Shake"), (Layer.Gesture, "Curious Tilt"), (Layer.Gesture, "Look Around"),
            (Layer.Gesture, "Startle"), (Layer.Gesture, "Celebrate"), (Layer.Gesture, "Sooty Shake"), (Layer.Gesture, "Wag"),
            (Layer.Blush, "Blush"),
        };

        /// <summary>Clips imported with Loop Time.</summary>
        public static readonly string[] LoopClips = { "Hover", "Walk", "Arm Swing", "Talk", "Thorn Spin" };

        /// <summary>Face states play at this float parameter's speed (0 = hold the expression while the line lasts).</summary>
        public const string FaceSpeedParam = "FaceSpeed";
        public const string IdleState = "Idle";

        // ------------------------------------------------------------------ what triggers what
        struct Cue
        {
            public string gesture, face;
            public bool holdFace;
            public Cue(string gesture, string face = null, bool holdFace = false) { this.gesture = gesture; this.face = face; this.holdFace = holdFace; }
        }

        /// <summary>Line id (or id prefix ending in '*') -> reaction. Station outcomes come from GameLines' outcome table.</summary>
        static readonly Dictionary<string, Cue> LineCues = new Dictionary<string, Cue>
        {
            { "room_pre", new Cue("Wag") },
            { "room_hook_carry", new Cue("Startle") },
            { "room_hook_1", new Cue(null, "Angry", holdFace: true) },        // the threat: "angry swarms ravage the Earth"
            { "room_meet_window", new Cue("Curious Tilt") },
            { "room_meet_stat", new Cue(null, "Sad", holdFace: true) },
            // ride script v2
            { "room_meet_role", new Cue("Yes Nod") },
            { "room_forecast", new Cue("Curious Tilt") },                    // turns to the window on "Job perk"
            { "room_meet_2", new Cue(null, "Angry", holdFace: true) },        // "Four problems. Four minutes."
            { "room_hint_*", new Cue("Curious Tilt") },                       // "Off the record?"
            { "room_lift_light", new Cue("Startle") },                        // tumbles off the lifted hive
            { "room_feed_tip", new Cue(null, "Sad") },                        // then droops
            { "room_whatif_putout", new Cue("Startle") },
            { "room_end_contract", new Cue("Wag") },
            // prompts, hints and questions
            { "room_smoker_intro", new Cue("Curious Tilt") },
            { "room_feed_intro", new Cue("Curious Tilt") },
            { "room_feed_lift_hint", new Cue("Curious Tilt") },
            { "room_feed_prompt", new Cue("Curious Tilt") },
            { "room_wasps_intro", new Cue("Startle") },                       // v2: ducks behind the hive
            { "room_heat_intro", new Cue("Curious Tilt") },
            { "room_whatif_prompt", new Cue("Curious Tilt") },
            { "room_whatif_nothing", new Cue("Curious Tilt") },
            { "room_map", new Cue("Curious Tilt") },
            { "room_canned_unclear", new Cue("Curious Tilt") },
            { "room_verdict_person", new Cue("No Shake") },
            { "ear_hint_*", new Cue("Curious Tilt") },
            // the worst wrong picks also get the angry face
            { "room_smoker_swat", new Cue("No Shake", "Angry") },
            { "room_feed_store_honey", new Cue("No Shake", "Angry") },
            { "room_heat_nothing", new Cue("No Shake", "Angry") },
            // what-if
            { "room_whatif_fire*", new Cue("Startle") },
            { "room_whatif_out*", new Cue("Yes Nod") },
            { "room_whatif_flowers", new Cue("Celebrate") },
            { "room_whatif_spray", new Cue("No Shake", "Angry") },
            { "room_whatif_winter", new Cue(null, "Sad") },
            { "room_whatif_refuse", new Cue("No Shake") },
            // acks, hornet, stakes
            { "room_ack_carried", new Cue("Yes Nod") },
            { "room_ack_allow", new Cue("Yes Nod") },
            { "room_ack_denied", new Cue("No Shake") },
            { "room_ack_grievance", new Cue("No Shake") },
            { "room_hornet_1", new Cue("Startle") },
            { "room_hornet_4", new Cue("Startle") },
            { "room_strike_lookup", new Cue("Look Around") },
            // escape + T-0
            { "room_escape", new Cue("Celebrate") },
            { "room_t0_1", new Cue(null, "Sad") },
            { "room_t0_2", new Cue("Celebrate") },
        };

        /// <summary>Lines that blush (on their own layer, so it stacks with a gesture).</summary>
        static readonly HashSet<string> BlushLines = new HashSet<string> { "room_whatif_out_3", "room_t0_4", "room_meet_1", "room_heat_mom", "room_escape_credit" }; // t0_4: "Nobody saw that."

        // ------------------------------------------------------------------ inspector
        [SerializeField] Animator animator;
        [Tooltip("Empty = found in the scene.")]
        [SerializeField] GameNet net;
        [SerializeField] GameDirector director;
        [SerializeField] bool logGestures = true;

        [Header("Idle")]
        [SerializeField] Vector2 blinkEverySeconds = new Vector2(3f, 6f);
        [SerializeField, Range(0f, 1f)] float doubleBlinkChance = 0.25f;
        [SerializeField] Vector2 antennaEverySeconds = new Vector2(5f, 10f);
        [Tooltip("Wag or a thorn move after this long with no gesture and no line.")]
        [SerializeField] Vector2 idleGestureAfterSeconds = new Vector2(8f, 14f);

        [Header("Blending")]
        [SerializeField] float fadeInSeconds = 0.12f;
        [SerializeField] float fadeOutSeconds = 0.25f;
        [SerializeField] float talkFadeSeconds = 0.1f;
        [Tooltip("Arm Swing is full at this flight speed (m/s, world).")]
        [SerializeField] float armSwingFullSpeed = 0.35f;
        [Tooltip("A held face (Angry/Sad during a line) lets go after this long at most.")]
        [SerializeField] float maxFaceHoldSeconds = 7f;
        [Tooltip("Same gesture again within this many seconds is ignored (a line and a scene change can both ask).")]
        [SerializeField] float repeatGuardSeconds = 1.5f;

        // ------------------------------------------------------------------ state
        sealed class Run
        {
            public string state;
            public float start, length, end;     // end < 0: looping until stopped
            public bool hold;                    // face: pause mid-clip while the line plays
            public bool holding;
            public float holdStartedAt;
            public float weight;
            public bool idle;                    // an idle variation (anything else may replace it)
        }

        readonly Run[] _runs = new Run[Enum.GetValues(typeof(Layer)).Length];
        readonly Dictionary<string, float> _clipLength = new Dictionary<string, float>();
        readonly Dictionary<string, float> _lastPlayed = new Dictionary<string, float>();
        readonly List<(float at, string clip, string reason)> _queue = new List<(float, string, string)>();
        static Dictionary<string, bool> _outcomeLines;   // line id -> correct?

        BeeswaxController _controller;
        Transform _root;
        Vector3 _lastRootPos;
        float _armWeight, _talkWeight;
        float _nextBlink, _nextAntenna, _nextIdleGesture, _lastActivity;
        int _faceSpeedId;
        bool _ready, _startLoops;
        GameBeat _hiddenInBeat = (GameBeat)(-1);
        string _lastLine, _catchUpLine;   // v32: he hears lines while hidden inside the hive (body inactive)
        float _lastLineEnd;
        int _idleBlinks, _idleAntenna, _idleGestures, _triggers;

        public bool Ready => _ready;
        /// <summary>Gestures triggered by game events (not idle), since start.</summary>
        public int Triggers => _triggers;

        void Awake()
        {
            if (animator == null) animator = GetComponent<Animator>();
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            _controller = GetComponentInParent<BeeswaxController>(true);
            _root = _controller != null ? _controller.transform : transform;
            _faceSpeedId = Animator.StringToHash(FaceSpeedParam);
            if (net != null) net.VoiceLineStarted += RememberLine; // also while disabled (OnEnable catches up)
            for (int i = 0; i < _runs.Length; i++) _runs[i] = new Run();

            if (animator == null || animator.runtimeAnimatorController == null)
            {
                Debug.LogWarning("[VBS][BeeswaxRig] no Animator/controller on " + name + ": Beeswax stays in his bind pose");
                return;
            }
            foreach (var clip in animator.runtimeAnimatorController.animationClips)
                if (clip != null) _clipLength[clip.name] = clip.length;
            if (!animator.TryGetComponent<InsectBodySway>(out _)) animator.gameObject.AddComponent<InsectBodySway>();
            _ready = true;

            string patched = "not patched (the empty Body/Mouth, Body/WingL, Body/WingR targets take the procedural flap)";
            if (_controller != null)
            {
                var p = typeof(BeeswaxController).GetProperty("RigDriven", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) { p.SetValue(_controller, true); patched = "RigDriven = true (procedural wing flap + mouth scale off)"; }
            }
            Debug.Log("[VBS][BeeswaxRig] ready: " + _clipLength.Count + " clips, " + animator.layerCount + " layers; BeeswaxController " +
                      (_controller != null ? patched : "not found (stills/preview)"));
        }

        void OnEnable()
        {
            if (net != null)
            {
                net.VoiceLineStarted += OnLine;
                net.SceneChanged += OnScene;
            }
            if (director != null) director.BeatChanged += OnBeat;
            ResetRuns();
            if (_ready && _hiddenInBeat == GameBeat.T0 && director != null && director.Beat == GameBeat.T0)
            {
                Queue(0.35f, "Sooty Shake", "crash landing at T-0 (back from the Moon, dusty)");
            }
            // v32 hook: room_hook_1 starts while he is still inside the hive; react to it when he squeezes out mid-line
            else if (_ready && _hiddenInBeat != (GameBeat)(-1) && net != null && LineInProgress) _catchUpLine = _lastLine;
            _hiddenInBeat = (GameBeat)(-1);
        }

        void OnDestroy()
        {
            if (net != null) net.VoiceLineStarted -= RememberLine;
        }

        void RememberLine(GameNet.VoiceLineInfo info)
        {
            _lastLine = info.id;
            _lastLineEnd = Time.time + Mathf.Max(0f, info.clipSeconds);
        }

        /// <summary>The last line is still playing (IsSpeaking, or inside its clip length: a muted player ends lines at once).</summary>
        bool LineInProgress => net != null && (net.IsSpeaking || Time.time < _lastLineEnd);

        void OnDisable()
        {
            if (net != null)
            {
                net.VoiceLineStarted -= OnLine;
                net.SceneChanged -= OnScene;
            }
            if (director != null) director.BeatChanged -= OnBeat;
            if (director != null) _hiddenInBeat = director.Beat;
        }

        void ResetRuns()
        {
            float now = Time.time;
            foreach (var r in _runs) { r.state = null; r.weight = 0f; r.holding = false; r.end = 0f; }
            _armWeight = _talkWeight = 0f;
            _lastRootPos = _root != null ? _root.position : transform.position;
            _nextBlink = now + UnityEngine.Random.Range(blinkEverySeconds.x, blinkEverySeconds.y);
            _nextAntenna = now + UnityEngine.Random.Range(antennaEverySeconds.x, antennaEverySeconds.y);
            _nextIdleGesture = now + UnityEngine.Random.Range(idleGestureAfterSeconds.x, idleGestureAfterSeconds.y);
            _lastActivity = now;
            _queue.Clear();
            _catchUpLine = null;
            if (!_ready) return;
            _startLoops = true; // on the next Update: the Animator may not be initialized yet in OnEnable
        }

        // ------------------------------------------------------------------ public API (others may call it too)
        /// <summary>Play a clip by name on its layer now ("Yes Nod", "Angry", "Blink", ...). Returns false if unknown.</summary>
        public bool Play(string clip, string reason = null) => Play(clip, reason, false, false);

        bool Play(string clip, string reason, bool holdFace, bool idle)
        {
            if (!_ready || string.IsNullOrEmpty(clip)) return false;
            if (!TryLayer(clip, out var layer) || !_clipLength.TryGetValue(clip, out float len)) return false;
            float now = Time.time;
            if (!idle && _lastPlayed.TryGetValue(clip, out float last) && now - last < repeatGuardSeconds) return false;
            var r = _runs[(int)layer];
            if (idle && r.state != null && now < r.end && !r.idle) return false; // never interrupt a reaction with an idle move
            _lastPlayed[clip] = now;
            r.state = clip;
            r.start = now;
            r.length = len;
            r.end = now + len;
            r.hold = holdFace && layer == Layer.Face;
            r.holding = false;
            r.idle = idle;
            if (layer == Layer.Face) animator.SetFloat(_faceSpeedId, 1f);
            animator.CrossFadeInFixedTime(Hash(layer, clip), r.weight > 0.01f ? fadeInSeconds : 0f, (int)layer, 0f);
            if (!idle)
            {
                _lastActivity = now;
                _triggers++;
                if (layer == Layer.Gesture) _nextIdleGesture = now + len + UnityEngine.Random.Range(idleGestureAfterSeconds.x, idleGestureAfterSeconds.y);
                if (logGestures)
                    Debug.Log("[VBS][BeeswaxRig] " + clip + " (" + layer + " layer" + (r.hold ? ", held while the line plays" : "") + ") <- " +
                              (reason ?? "API") + (director != null ? " [beat " + GameIds.BeatName(director.Beat) + "]" : ""));
            }
            return true;
        }

        static bool TryLayer(string clip, out Layer layer)
        {
            foreach (var (l, c) in States)
                if (c == clip) { layer = l; return true; }
            layer = Layer.Base;
            return false;
        }

        static readonly Dictionary<(Layer, string), int> HashCache = new Dictionary<(Layer, string), int>();
        static int Hash(Layer layer, string state)
        {
            if (!HashCache.TryGetValue((layer, state), out int h))
            {
                h = Animator.StringToHash(layer + "." + state);
                HashCache[(layer, state)] = h;
            }
            return h;
        }

        void Queue(float delay, string clip, string reason) => _queue.Add((Time.time + delay, clip, reason));

        // ------------------------------------------------------------------ events
        void OnLine(GameNet.VoiceLineInfo info) => React(info.id, "");

        void React(string id, string note)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (TryCue(id, out var cue))
            {
                if (cue.face != null) Play(cue.face, "line " + id + note, cue.holdFace, false);
                if (cue.gesture != null) Play(cue.gesture, "line " + id + note, false, false);
            }
            else if (IsOutcome(id, out bool correct))
            {
                Play(correct ? "Yes Nod" : "No Shake", "line " + id + (correct ? " (right pick)" : " (wrong pick)") + note, false, false);
            }
            if (BlushLines.Contains(id)) Play("Blush", "line " + id + note, false, false);
        }

        static bool TryCue(string id, out Cue cue)
        {
            if (LineCues.TryGetValue(id, out cue)) return true;
            foreach (var kv in LineCues)
            {
                string k = kv.Key;
                if (k.EndsWith("*", StringComparison.Ordinal) && id.StartsWith(k.Substring(0, k.Length - 1), StringComparison.Ordinal))
                {
                    cue = kv.Value;
                    return true;
                }
            }
            cue = default;
            return false;
        }

        /// <summary>Is this line a station outcome (GameLines' outcome table), and the right pick?</summary>
        static bool IsOutcome(string id, out bool correct)
        {
            if (_outcomeLines == null)
            {
                _outcomeLines = new Dictionary<string, bool>();
                foreach (GameStation s in Enum.GetValues(typeof(GameStation)))
                {
                    int si = (int)s;
                    if (si < 0 || si >= GameIds.Choices.Length) continue;
                    foreach (var choice in GameIds.Choices[si])
                        if (GameLines.TryGetOutcome(s, choice, out var o) && !string.IsNullOrEmpty(o.line.id))
                            _outcomeLines[o.line.id] = o.correct;
                }
            }
            return _outcomeLines.TryGetValue(id, out correct);
        }

        void OnScene(string scene)
        {
            if (scene == GameIds.Scene.WhatIfFire) Play("Startle", "window scene " + scene, false, false);
        }

        void OnBeat(GameBeat from, GameBeat to)
        {
            if (logGestures && (_idleBlinks + _idleAntenna + _idleGestures) > 0)
                Debug.Log("[VBS][BeeswaxRig] idle during " + GameIds.BeatName(from) + ": " + _idleBlinks + " blink(s), " + _idleAntenna +
                          " antenna twitch(es), " + _idleGestures + " idle move(s)");
            _idleBlinks = _idleAntenna = _idleGestures = 0;
            if (to == GameBeat.Meet) Queue(0.4f, "Look Around", "beat meet");
            else if (to == GameBeat.Escape) Queue(0.2f, "Celebrate", "beat escape");
            else if (to == GameBeat.Pre) ResetRuns();
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (!_ready) return;
            float now = Time.time, dt = Time.deltaTime;
            if (_startLoops)
            {
                _startLoops = false;
                animator.SetFloat(_faceSpeedId, 1f);
                for (int l = 1; l < _runs.Length; l++) animator.SetLayerWeight(l, 0f);
                // the looping layers run all the time; only their weight changes
                animator.Play(Hash(Layer.Talk, "Talk"), (int)Layer.Talk, 0f);
                animator.Play(Hash(Layer.Arms, "Arm Swing"), (int)Layer.Arms, 0f);
            }
            if (_catchUpLine != null)
            {
                string id = _catchUpLine;
                _catchUpLine = null;
                if (LineInProgress) React(id, " (in progress when he came out)");
            }

            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (now < _queue[i].at) continue;
                var q = _queue[i];
                _queue.RemoveAt(i);
                Play(q.clip, q.reason, false, false);
            }

            bool speaking = net != null && net.IsSpeaking;
            if (speaking) _lastActivity = now;

            // Talk: on Mouth / Mouth O while a line plays
            _talkWeight = Mathf.MoveTowards(_talkWeight, speaking ? 1f : 0f, dt / Mathf.Max(0.01f, talkFadeSeconds));
            animator.SetLayerWeight((int)Layer.Talk, _talkWeight);

            // Arm Swing: front stubs while flying
            Vector3 p = _root != null ? _root.position : transform.position;
            float speed = dt > 1e-5f ? (p - _lastRootPos).magnitude / dt : 0f;
            _lastRootPos = p;
            float armTarget = Mathf.Clamp01((speed - 0.05f) / Mathf.Max(0.01f, armSwingFullSpeed - 0.05f));
            _armWeight = Mathf.MoveTowards(_armWeight, armTarget, dt * 3f);
            animator.SetLayerWeight((int)Layer.Arms, _armWeight);

            // idle variations
            if (now >= _nextBlink)
            {
                _nextBlink = now + UnityEngine.Random.Range(blinkEverySeconds.x, blinkEverySeconds.y);
                if (Busy(Layer.Face, now) == false && Play(UnityEngine.Random.value < doubleBlinkChance ? "Double Blink" : "Blink", null, false, true)) _idleBlinks++;
            }
            if (now >= _nextAntenna)
            {
                _nextAntenna = now + UnityEngine.Random.Range(antennaEverySeconds.x, antennaEverySeconds.y);
                if (!Busy(Layer.Gesture, now) && !Busy(Layer.Face, now) && Play("Antenna Twitch", null, false, true)) _idleAntenna++;
            }
            if (now >= _nextIdleGesture && !speaking && now - _lastActivity > 2f && !Busy(Layer.Gesture, now) && !Busy(Layer.Face, now))
            {
                _nextIdleGesture = now + UnityEngine.Random.Range(idleGestureAfterSeconds.x, idleGestureAfterSeconds.y);
                float pick = UnityEngine.Random.value;
                string clip = pick < 0.55f ? "Wag" : pick < 0.7f ? "Thorn Bob" : pick < 0.85f ? "Thorn Flick" : "Thorn Spin";
                if (Play(clip, null, false, true))
                {
                    _idleGestures++;
                    if (logGestures) Debug.Log("[VBS][BeeswaxRig] " + clip + " (idle" + (director != null ? ", beat " + GameIds.BeatName(director.Beat) : "") + ")");
                }
            }

            // one-shot layers: fade in, hold a face while the line lasts, fade out at the end
            for (int l = (int)Layer.Antenna; l < _runs.Length; l++)
            {
                if (l == (int)Layer.Talk) continue;
                var r = _runs[l];
                float target = 0f;
                if (r.state != null)
                {
                    if (r.hold)
                    {
                        if (!r.holding && now - r.start >= r.length * 0.5f)
                        {
                            r.holding = true;
                            r.holdStartedAt = now;
                            animator.SetFloat(_faceSpeedId, 0f);
                        }
                        if (r.holding && (!speaking || now - r.holdStartedAt > maxFaceHoldSeconds))
                        {
                            r.hold = r.holding = false;
                            animator.SetFloat(_faceSpeedId, 1f);
                            r.end = now + r.length * 0.5f;
                        }
                        else if (r.holding) r.end = now + r.length * 0.5f;
                    }
                    if (now < r.end) target = Mathf.Min(1f, (r.end - now) / Mathf.Max(0.01f, fadeOutSeconds));
                    else r.state = null;
                    if (r.state != null && now - r.start < fadeInSeconds) target = Mathf.Min(target, Mathf.Max(r.weight, (now - r.start) / fadeInSeconds));
                }
                r.weight = r.state == null ? Mathf.MoveTowards(r.weight, 0f, dt / Mathf.Max(0.01f, fadeOutSeconds)) : target;
                animator.SetLayerWeight(l, r.weight);
            }
        }

        bool Busy(Layer layer, float now)
        {
            var r = _runs[(int)layer];
            return r.state != null && now < r.end;
        }
    }
}
