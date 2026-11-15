using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;
using VirtualBeeSwarm.Juice;
using VirtualBeeSwarm.Pals;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.BatOff
{
    /// <summary>
    /// Wasp bat-off (station 3): right after "wasps made a beeline for the syrup", 12 wasps dive in over ~15 s
    /// one by one toward the front of the hive. Missed wasps regroup at the roof and shake the hive. Bat them away
    /// with an open hand or a controller: the hand's velocity (frame deltas) becomes a real Rigidbody knockback, the wasp
    /// hit-stops for 50 ms, tumbles, bounces off the floor, sees stars, then flies off out of the room. Nobody is
    /// squished. Guards break up the raid (swarm spike and gold burst). GUARD DUTY counts the bounces.
    /// Then the station carries on to its door choice.
    /// Self-installing; hooks in through WaspsStation.BatOff. Desktop/autopilot (no hands tracked): a ghost paw bats
    /// most wasps; key V bats the nearest one. Tuning: wasps.batOff, wasps.batSeconds, wasps.batCount (BatOffTuning).
    /// Budget: 14 wasps x ~140 tris, one shared unlit material, physics only on the wasps (+ one floor box while one
    /// tumbles), no per-frame allocations.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class WaspBatOff : MonoBehaviour
    {
        public static WaspBatOff Instance { get; private set; }

        const int Max = 14;
        const float WaspLength = 0.09f, WaspRadius = 0.028f, HandRadius = 0.06f, ControllerRadius = 0.075f;
        const float SwatterLength = 0.28f, SwingSpeed = 1.25f;
        const float HitStopSeconds = 0.05f, TumbleSeconds = 1.8f, DazedSeconds = 0.75f, LeaveSeconds = 2.4f;
        const float HeadKeepOut = 0.36f, HeadSafe = 0.5f, MaxBatSpeed = 4.5f, RaidSeconds = 2.2f;
        const KeyCode BatKey = KeyCode.V;

        enum S { Gone, Wait, Dive, Hover, Raid, HitStop, Tumble, Dazed, Leave }

        struct Wasp
        {
            public S state;
            public float at, spawnAt, dur, lastHit, phase, verticalSpeed;
            public Vector3 from, ctrl, to, launch, last;
            public bool autoBat, counted;
        }

        readonly Wasp[] _w = new Wasp[Max];
        readonly Transform[] _t = new Transform[Max];
        readonly Transform[] _wings = new Transform[Max];
        readonly Rigidbody[] _rb = new Rigidbody[Max];
        readonly SphereCollider[] _col = new SphereCollider[Max];
        readonly Vector3[] _handTip = new Vector3[2], _handBack = new Vector3[2], _handVel = new Vector3[2], _handPrev = new Vector3[2];
        readonly bool[] _handOk = new bool[2], _handCtl = new bool[2], _swinging = new bool[2];
        readonly Transform[] _swatters = new Transform[2];
        readonly Vector3[] _swatterHead = new Vector3[2];

        GameObject _floor;
        JuiceParticles _fx;
        GuardDutyCard _card;
        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        SwarmSystem _swarm;
        FutureWindow _window;
        WaspsStation _station;
        AudioSource _raidBuzz;
        Transform _shakeRoot;
        Vector3 _shakeBase, _shakeApplied;
        bool _shaking;

        bool _running, _burstDone, _saidNice, _saidDoor, _auto, _pilot;
        int _n, _bounced, _reached;
        float _startedAt, _lastLineAt, _cardHideAt = -1f, _floorY, _scale = 1f, _roofY, _frontD, _raidAt = -1f;
        Vector3 _cardPos, _hiveC, _toPlayer;

        static readonly Color Star = new Color(1f, 0.86f, 0.2f), Puff = new Color(1f, 0.97f, 0.88f), Gold = new Color(1f, 0.72f, 0.12f);

        // ------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!BatOffTuning.Enabled || Instance != null) return;
            if (FindAnyObjectByType<GameDirector>() == null) return; // not the game scene
            new GameObject("[VBS] WaspBatOff").AddComponent<WaspBatOff>();
        }

        void Awake()
        {
            Instance = this;
            Build();
            WaspsStation.BatOff = Run; // WASPBATOFF hook
            ExtendWaspsCap();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            StopRaid();
            Instance = null;
            if (WaspsStation.BatOff == (System.Func<WaspsStation, IEnumerator>)Run) WaspsStation.BatOff = null;
        }

        void OnDisable() => StopRaid();

        /// <summary>The director's wasps cap (50 s) was set before the burst existed: give the burst its seconds back.</summary>
        void ExtendWaspsCap()
        {
            _director = FindAnyObjectByType<GameDirector>();
            var f = typeof(GameDirector).GetField("beatCapSeconds", BindingFlags.NonPublic | BindingFlags.Instance);
            int w = (int)GameBeat.Wasps;
            if (_director != null && f != null && f.GetValue(_director) is float[] caps && w < caps.Length && caps[w] > 0f)
            {
                caps[w] += BatOffTuning.Seconds + 14f;
                Debug.Log("[VBS][BatOff] wasps beat cap -> " + caps[w].ToString("0") + " s");
            }
            else Debug.LogWarning("[VBS][BatOff] could not extend the wasps beat cap (GameDirector.beatCapSeconds not found)");
        }

        void Build()
        {
            var body = BatOffArt.CreateWaspBody();
            var wings = BatOffArt.CreateWaspWings();
            var mat = GameMaterials.Create("VBS_BatOffWasp (runtime)", Color.white, vertexColors: true, doubleSided: true);
            int ignoreRaycast = 2; // built-in "Ignore Raycast": hand/controller rays never hit a wasp or the floor box
            for (int i = 0; i < Max; i++)
            {
                var go = new GameObject("BatWasp" + i) { layer = ignoreRaycast };
                go.transform.SetParent(transform, false);
                var mesh = new GameObject("Body") { layer = ignoreRaycast };
                mesh.transform.SetParent(go.transform, false);
                mesh.transform.localScale = Vector3.one * WaspLength;
                BatOffArt.Renderer(mesh, body, mat);
                var wing = new GameObject("Wings") { layer = ignoreRaycast };
                wing.transform.SetParent(mesh.transform, false);
                wing.transform.localPosition = new Vector3(0f, 0.1f, 0.1f);
                BatOffArt.Renderer(wing, wings, mat);
                var rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = true;
                rb.mass = 0.02f;
                rb.linearDamping = 1.2f; // tumble, bounce and stars stay in front of the player (passthrough has no walls)
                rb.angularDamping = 0.6f;
                rb.interpolation = RigidbodyInterpolation.None; // on only while physics drives it (Launch)
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                var col = go.AddComponent<SphereCollider>();
                col.radius = WaspRadius;
                col.enabled = false;
                _t[i] = go.transform;
                _wings[i] = wing.transform;
                _rb[i] = rb;
                _col[i] = col;
                go.SetActive(false);
            }

            _floor = new GameObject("BatOffFloor") { layer = ignoreRaycast };
            _floor.transform.SetParent(transform, false);
            var box = _floor.AddComponent<BoxCollider>();
            box.size = new Vector3(10f, 0.2f, 10f);
            box.sharedMaterial = new PhysicsMaterial("VBS_BatOffBounce")
            {
                bounciness = 0.6f, dynamicFriction = 0.4f, staticFriction = 0.4f, bounceCombine = PhysicsMaterialCombine.Maximum,
            };
            _floor.SetActive(false);

            _fx = gameObject.AddComponent<JuiceParticles>();
            _card = GuardDutyCard.Create(transform);
            var buzz = new GameObject("Wasp Raid Buzz");
            buzz.transform.SetParent(transform, false);
            _raidBuzz = buzz.AddComponent<AudioSource>();
            _raidBuzz.clip = Resources.Load<AudioClip>("SatieTakes/loop_shared_wasp_buzz");
            _raidBuzz.playOnAwake = false;
            _raidBuzz.loop = true;
            _raidBuzz.spatialBlend = 1f;
            _raidBuzz.dopplerLevel = 0f;
            _raidBuzz.minDistance = 0.65f;
            _raidBuzz.maxDistance = 5f;
            _raidBuzz.pitch = 0.82f;
            BuildSwatters();
        }

        // ------------------------------------------------------------ the burst (runs inside WaspsStation.StationIntro)
        IEnumerator Run(WaspsStation st)
        {
            if (!BatOffTuning.Enabled || st == null) yield break;
            if (!Begin(st)) yield break;
            while (_running && !_burstDone) yield return null;
            if (!_running) yield break;
            float wait = Time.time + 4f;
            while (_net != null && _net.IsSpeaking && Time.time < wait) yield return null;
            var done = RideLines.Get("room_wasps_batoff_done", "Shift's over. Zero squished, zero alarm smell. Overtime's bee-yond my pay grade.");
            Say(done);
            float until = Time.time + LineTime.Seconds(_net, done) + 0.3f;
            while (_running && Time.time < until) yield return null;
            _cardHideAt = Time.time + 2.5f;
        }

        bool Begin(WaspsStation st)
        {
            _station = st;
            if (_director == null) _director = FindAnyObjectByType<GameDirector>();
            _net = FindAnyObjectByType<GameNet>();
            _pilot = FindAnyObjectByType<GameAutopilot>() != null;
            _hive = FindAnyObjectByType<HiveBody>();
            _swarm = FindAnyObjectByType<SwarmSystem>();
            _window = FindAnyObjectByType<FutureWindow>();
            if (_hive == null) { Debug.LogWarning("[VBS][BatOff] no hive: skipped"); return false; }

            var hb = HiveBounds();
            _floorY = Bloom.MeadowLayout.Active ? Mathf.Min(hb.min.y, Bloom.MeadowLayout.FloorY) : hb.min.y;
            _scale = Mathf.Clamp(Bloom.MeadowLayout.Active ? Bloom.MeadowLayout.Scale : 1f, 1f, 3.2f); // wasps match the big hive
            Transform head = Head();
            Vector3 headPos = head != null ? head.position : _hive.transform.position - _hive.transform.forward * 0.6f + Vector3.up * 1.5f;
            _floor.transform.position = new Vector3(headPos.x, _floorY - 0.1f, headPos.z);
            Vector3 toPlayer = headPos - hb.center; toPlayer.y = 0f;
            toPlayer = toPlayer.sqrMagnitude > 1e-4f ? toPlayer.normalized : -_hive.transform.forward;
            _hiveC = hb.center;
            _toPlayer = toPlayer;
            _roofY = Mathf.Max(hb.max.y, Bloom.MeadowLayout.Active ? Bloom.MeadowLayout.RoofY : hb.max.y);
            _frontD = Mathf.Max(0.05f, Vector3.Dot(_hive.DoorPoint.position - hb.center, toPlayer));
            Vector3 side = Vector3.Cross(Vector3.up, toPlayer);
            _cardPos = new Vector3(hb.center.x, hb.max.y + 0.22f, hb.center.z) - side * (hb.extents.x + 0.12f) + toPlayer * 0.1f;

            _n = BatOffTuning.Count;
            _bounced = _reached = 0;
            _raidAt = -1f;
            _shakeRoot = _hive.transform.Find("LiftRoot");
            _saidNice = _saidDoor = false;
            _startedAt = Time.time;
            _cardHideAt = -1f;
            _burstDone = false;
            _running = true;
            _auto = AutoMode();

            // Stagger the whole pool across the round so there is always another wasp on the way.
            for (int i = 0; i < _n; i++)
            {
                ref var w = ref _w[i];
                w = default;
                w.state = S.Wait;
                // dartiros: the last spawn leaves room for the slower dive (up to 4 s) to arrive before the raid
                w.spawnAt = _startedAt + Mathf.Lerp(0.35f, Mathf.Max(1.5f, BatOffTuning.Seconds - 4.4f), i / (float)(_n - 1));
                w.autoBat = i % 4 != 3; // exercise the missed-wasp payoff on desktop
                w.phase = Random.Range(0f, 10f);
            }
            for (int i = 0; i < Max; i++)
            {
                if (i >= _n) _w[i].state = S.Gone;
                Park(i);
            }
            _card.transform.position = _cardPos;
            _card.Show(true);
            _card.SetCount(0);
            Say(RideLines.Get("room_wasps_batoff", "Guard duty! Bounce 'em, don't squish 'em. A squished wasp's alarm scent calls in her nestmates."));
            Debug.Log("[VBS][BatOff] start: " + _n + " wasps over " + BatOffTuning.Seconds.ToString("0") + " s, wasp " + (WaspLength * Size(0) * 100f).ToString("0") + " cm" +
                      (_auto ? " (ghost paw: no hands tracked)" : "") + ", floor y " + _floorY.ToString("0.00"));
            return true;
        }

        Bounds HiveBounds()
        {
            var rs = _hive.GetComponentsInChildren<Renderer>(); // once per burst
            var b = new Bounds(_hive.transform.position, Vector3.one * 0.1f);
            bool any = false;
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || !rs[i].enabled || rs[i] is ParticleSystemRenderer) continue;
                if (!any) { b = rs[i].bounds; any = true; } else b.Encapsulate(rs[i].bounds);
            }
            return b;
        }

        static Transform Head() { var c = Camera.main; return c != null ? c.transform : null; }

        /// <summary>Ghost paw only off-headset (desktop, editor) or under the autopilot: on a Quest, hands out of view must not
        /// bat wasps for the player.</summary>
        bool AutoMode() => BatOffTuning.ForceAuto || _pilot ||
                           (!XRSettings.isDeviceActive && !GameHands.IsTracked(GameHand.Left) && !GameHands.IsTracked(GameHand.Right));

        void Say(GameLine line)
        {
            if (_net == null) return;
            _net.Say(line);
            _lastLineAt = Time.time;
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            if (_cardHideAt > 0f && Time.time >= _cardHideAt) { _cardHideAt = -1f; _card.Show(false); }
            if (!_running) return;
            if (_director == null || _director.Beat != GameBeat.Wasps || _station == null || !_station.IsActive) { Cleanup("left the wasps beat"); return; }
            if (!_burstDone && (_station.Busy || _station.Solved || _station.Choosing)) EndBurst("the station moved on");

            float now = Time.time, dt = Mathf.Max(Time.deltaTime, 1e-4f);
            _auto = AutoMode();
            ReadHands(dt);
            Transform head = Head();
            Vector3 headPos = head != null ? head.position : Vector3.up * 1.6f;
            bool key = GameInput.KeyDown(BatKey);
            int nearest = -1;
            float nearestD = float.MaxValue;
            bool anyLive = false, anyTumbling = false, anyRaid = false;
            float age = now - _startedAt;

            for (int i = 0; i < _n; i++)
            {
                ref var w = ref _w[i];
                var tr = _t[i];
                switch (w.state)
                {
                    case S.Gone: continue;
                    case S.Wait:
                        anyLive = true;
                        if (_burstDone || now < w.spawnAt) continue;
                        Spawn(i, headPos, head);
                        break;
                    case S.Dive:
                    {
                        anyLive = true;
                        float k = Mathf.Clamp01((now - w.at) / w.dur);
                        Vector3 p = Bezier(w.from, w.ctrl, w.to, k) + Buzz(now, w.phase, 0.012f);
                        p = KeepOffHead(p, headPos);
                        Fly(i, p, now);
                        if (_auto && w.autoBat && k > 0.78f) { GhostBat(i, headPos); break; }
                        if (k >= 1f) { w.state = S.Hover; w.at = now; }
                        break;
                    }
                    case S.Hover:
                    {
                        anyLive = true;
                        Vector3 p = KeepOffHead(w.to + Buzz(now * 1.35f, w.phase, 0.025f), headPos);
                        Fly(i, p, now);
                        if (_auto && w.autoBat) { GhostBat(i, headPos); break; }
                        if (age >= BatOffTuning.Seconds)
                        {
                            w.from = p;
                            w.state = S.Raid;
                            w.at = now;
                            _reached++;
                            if (_raidAt < 0f) StartRaid(now);
                            anyRaid = true;
                        }
                        break;
                    }
                    case S.Raid:
                    {
                        anyLive = anyRaid = true;
                        Vector3 p = Vector3.Lerp(w.from, RaidPoint(i, now), Mathf.SmoothStep(0f, 1f, (now - w.at) / 0.38f));
                        Fly(i, KeepOffHead(p, headPos), now);
                        break;
                    }
                    case S.HitStop:
                        anyLive = true;
                        tr.localScale = Vector3.one * Size(i) * (1f + 0.35f * Mathf.Sin(Mathf.Clamp01((now - w.at) / HitStopSeconds) * Mathf.PI));
                        if (now - w.at >= HitStopSeconds) Launch(i, OffHead(tr.position, w.launch, headPos));
                        break;
                    case S.Tumble:
                    {
                        anyLive = anyTumbling = true;
                        Vector3 p = _rb[i].position;
                        if ((p - headPos).sqrMagnitude < HeadSafe * HeadSafe) _rb[i].linearVelocity = OffHead(p, _rb[i].linearVelocity, headPos);
                        float vertical = _rb[i].linearVelocity.y;
                        if (w.verticalSpeed < -0.65f && vertical > 0.2f && p.y < _floorY + Radius(i) + 0.08f)
                        {
                            Vector3 floor = new Vector3(p.x, _floorY + 0.01f, p.z);
                            _fx.Burst(floor, 5, Puff, 0.5f, 0.24f, 0.022f, gravity: 0.35f, upBias: 0.5f);
                            GameSound.Cue("wasp_bounce", floor);
                        }
                        w.verticalSpeed = vertical;
                        if (now - w.at >= TumbleSeconds || p.y < _floorY - 0.4f) Daze(i, now);
                        else if (Mathf.Repeat(now + w.phase, 0.16f) < dt) _fx.Burst(p, 1, Star, 0.25f, 0.45f, 0.011f * _scale, gravity: -0.4f, upBias: 1f);
                        break;
                    }
                    case S.Dazed:
                    {
                        anyLive = true;
                        float k = (now - w.at) / DazedSeconds;
                        Vector3 p = w.from + Vector3.up * (0.14f * Mathf.SmoothStep(0f, 1f, k));
                        tr.position = p;
                        float wob = (1f - k) * 35f;
                        tr.rotation = Quaternion.Euler(Mathf.Sin(now * 13f) * wob, now * 140f, Mathf.Cos(now * 11f) * wob);
                        FlapWings(i, now, 0.35f);
                        if (Mathf.Repeat(now + w.phase, 0.12f) < dt)
                        {
                            float a = now * 9f + w.phase;
                            _fx.Burst(p + new Vector3(Mathf.Cos(a), 0.6f, Mathf.Sin(a)) * 0.04f * _scale, 1, Star, 0.08f, 0.4f, 0.012f * _scale, gravity: -0.2f, upBias: 1f);
                        }
                        if (k >= 1f) Leave(i, now, headPos);
                        break;
                    }
                    case S.Leave:
                    {
                        float k = Mathf.Clamp01((now - w.at) / LeaveSeconds);
                        Fly(i, Bezier(w.from, w.ctrl, w.to, k * k), now);
                        if (k >= 1f) { w.state = S.Gone; Park(i); }
                        break;
                    }
                }
                if (w.state == S.Dive || w.state == S.Hover || w.state == S.Raid || w.state == S.Tumble)
                {
                    TouchHands(i, headPos);
                    if (key && (w.state == S.Dive || w.state == S.Hover || w.state == S.Raid))
                    {
                        float d = (tr.position - headPos).sqrMagnitude;
                        if (d < nearestD) { nearestD = d; nearest = i; }
                    }
                }
            }
            if (nearest >= 0) GhostBat(nearest, headPos);
            if (_floor.activeSelf != anyTumbling) _floor.SetActive(anyTumbling);

            if (!_saidNice && _bounced >= 3 && now - _lastLineAt > 2f && (_net == null || !_net.IsSpeaking))
            {
                _saidNice = true;
                Say(RideLines.Get("room_wasps_batoff_nice", "Look at you, bouncing. Real guard bees check everyone at the door and shove intruders out."));
            }

            if (_raidAt >= 0f && !anyRaid) StopRaid();
            if (!_burstDone && (!anyLive || age >= BatOffTuning.Seconds + RaidSeconds ||
                (_raidAt >= 0f && now - _raidAt >= RaidSeconds)))
                EndBurst(anyLive ? "time" : "all bounced or shoved out");
        }

        void LateUpdate()
        {
            ShakeHive();
            if (!_card.isActiveAndEnabled) return;
            var head = Head();
            if (head == null) return;
            Vector3 look = _card.transform.position - head.position;
            if (look.sqrMagnitude > 1e-4f) _card.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
        }

        // ------------------------------------------------------------ wasp states
        void Spawn(int i, Vector3 headPos, Transform head)
        {
            ref var w = ref _w[i];
            Vector3 fwd = head != null ? head.forward : _hive.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Vector3 from;
            if (_window != null && i % 2 == 0)
                from = _window.PaneTransform.position + Random.insideUnitSphere * 0.15f;
            else
            {
                float yaw = Random.Range(-70f, 70f);
                from = headPos + Quaternion.Euler(0f, yaw, 0f) * fwd * Random.Range(2.0f, 3.0f) + Vector3.up * Random.Range(-0.2f, 0.6f);
            }
            w.from = from;
            w.to = HoverPoint(i, headPos);
            Path(ref w, Random.Range(0.22f, 0.45f), Random.Range(0.7f, 1.0f)); // dartiros: slower than the original 0.9-1.25 (an audit found 1.25-1.8, faster)
            if (Vector3.Dot(from - _hiveC, _toPlayer) < _frontD + 0.1f) OverRoof(ref w); // spawned behind the hive: never through it
            w.state = S.Dive;
            w.at = Time.time;
            w.last = from;
            var tr = _t[i];
            tr.position = from;
            tr.localScale = Vector3.one * Size(i);
            tr.gameObject.SetActive(true);
        }

        static void Path(ref Wasp w, float arc, float speed)
        {
            Vector3 d = w.to - w.from;
            Vector3 side = Vector3.Cross(Vector3.up, d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward);
            w.ctrl = Vector3.Lerp(w.from, w.to, 0.5f) + Vector3.up * arc + side * Random.Range(-0.45f, 0.45f);
            w.dur = Mathf.Clamp(d.magnitude / speed, 1.6f, 4.0f); // easy to follow with a swatter
        }

        /// <summary>Behind the hive: start above the roof and bend in over the front edge, so the dive drops onto the door
        /// from above instead of through the hive body.</summary>
        void OverRoof(ref Wasp w)
        {
            float lift = 0.25f * _scale;
            w.from.y = Mathf.Max(w.from.y, _roofY + 0.8f * lift + 0.3f);
            Vector3 front = _hiveC + _toPlayer * (_frontD + 0.15f * _scale + 0.2f);
            w.ctrl = new Vector3(front.x, _roofY + lift + 0.25f, front.z) + Vector3.Cross(Vector3.up, _toPlayer) * Random.Range(-0.2f, 0.2f);
            w.dur = Mathf.Max(w.dur, 1.6f);
        }

        float Size(int i) => Mathf.Max(2.4f, _scale);
        float Radius(int i) => WaspRadius * Size(i);

        /// <summary>Strips the part of a velocity that heads into the player's face while the wasp is near it.</summary>
        static Vector3 OffHead(Vector3 p, Vector3 v, Vector3 head)
        {
            Vector3 d = p - head;
            float m = d.magnitude;
            if (m > HeadSafe || m < 1e-4f) return v;
            Vector3 n = d / m;
            float toward = Vector3.Dot(v, n);
            return toward < 0f ? v - n * toward : v;
        }

        Vector3 HoverPoint(int i, Vector3 headPos)
        {
            Vector3 side = Vector3.Cross(Vector3.up, _toPlayer);
            Vector3 p = _hiveC + _toPlayer * (_frontD + 0.22f + (i % 2) * 0.13f) +
                side * ((i % 5 - 2) * 0.15f);
            p.y = Mathf.Clamp(headPos.y - 0.50f, _floorY + 0.20f, _roofY + 0.23f) + (i % 3 - 1) * 0.11f;
            return p;
        }

        Vector3 RaidPoint(int i, float now)
        {
            float a = now * 6.4f + i * 2.39996f;
            Vector3 side = Vector3.Cross(Vector3.up, _toPlayer);
            Vector3 p = _hiveC + _toPlayer * (_frontD + 0.10f + 0.10f * Mathf.Cos(a)) + side * (0.21f * Mathf.Sin(a));
            p.y = _roofY + 0.12f + 0.08f * Mathf.Sin(a * 1.4f);
            return p;
        }

        static Vector3 KeepOffHead(Vector3 p, Vector3 head)
        {
            Vector3 d = p - head;
            float m = d.magnitude;
            return m < HeadKeepOut ? head + (m > 1e-4f ? d / m : Vector3.forward) * HeadKeepOut : p;
        }

        void StartRaid(float now)
        {
            _raidAt = now;
            Vector3 attack = _hive.DoorPoint.position;
            if (_swarm != null) _swarm.Spike(0.35f);
            _fx.Burst(attack, 10, Gold, 0.7f, 0.45f, 0.012f * _scale, gravity: 0.6f, upBias: 0.4f);
            if (_raidBuzz.clip != null && Sound.SoundDirector.Instance != null && Sound.SoundDirector.Instance.isActiveAndEnabled)
            {
                _raidBuzz.transform.position = attack;
                _raidBuzz.volume = 0.34f;
                _raidBuzz.Play();
            }
            Debug.Log("[VBS][BatOff] missed wasps swarm the hive");
            if (!_saidDoor && Time.time - _lastLineAt > 3f && (_net == null || !_net.IsSpeaking))
            {
                _saidDoor = true;
                Say(RideLines.Get("room_wasps_batoff_door", "One made it to the door! Guards are on her. Shoving's in the job description."));
            }
        }

        void ShakeHive()
        {
            RestoreShake();
            if (_raidAt < 0f || _shakeRoot == null || _hive == null || _hive.Held || _hive.LiftHeight > 0.001f) return;
            float t = Time.time - _raidAt;
            float fade = Mathf.Clamp01(t * 10f) * Mathf.Clamp01((RaidSeconds - t) * 5f);
            float strength = (StakesFlags.Comfort ? 0.009f : 0.024f) * fade;
            _shakeBase = _shakeRoot.localPosition;
            _shakeApplied = _shakeBase + new Vector3(Mathf.Sin(t * 44f), Mathf.Abs(Mathf.Sin(t * 57f)) * 0.5f, Mathf.Cos(t * 39f) * 0.5f) * strength;
            _shakeRoot.localPosition = _shakeApplied;
            _shaking = true;
        }

        void RestoreShake()
        {
            if (_shaking && _shakeRoot != null && _shakeRoot.localPosition == _shakeApplied)
                _shakeRoot.localPosition = _shakeBase;
            _shaking = false;
        }

        void StopRaid()
        {
            RestoreShake();
            if (_raidBuzz != null && _raidBuzz.isPlaying) _raidBuzz.Stop();
            _raidAt = -1f;
        }

        void Hit(int i, Vector3 handVel, Vector3 away, int hand, Vector3 headPos)
        {
            ref var w = ref _w[i];
            if (Time.time - w.lastHit < 0.25f) return;
            w.lastHit = Time.time;
            Vector3 v = handVel * 1.3f + away * 1.2f + Vector3.up * 0.7f;
            float s = v.magnitude;
            v = s < 1e-3f ? Vector3.up * 2f : v * (Mathf.Clamp(s, 1.8f, MaxBatSpeed) / s);
            Vector3 p = _t[i].position;
            v = OffHead(p, v, headPos);
            if (v.sqrMagnitude < 1.2f * 1.2f) v += Vector3.up * 1.2f; // a backhand toward the face still pops her up and away
            if (w.state == S.Tumble) _rb[i].linearVelocity = v; // juggled mid-air
            else
            {
                if (_rb[i].isKinematic == false) _rb[i].isKinematic = true;
                w.state = S.HitStop;
                w.at = Time.time;
                w.launch = v;
            }
            if (!w.counted)
            {
                w.counted = true;
                _bounced++;
                _card.SetCount(_bounced);
            }
            _fx.Burst(p, 7, Puff, 1.1f, 0.3f, 0.014f * _scale, gravity: 0.5f, upBias: 0.3f, drag: 5f);
            _fx.Burst(p, 3, Star, 0.6f, 0.5f, 0.016f * _scale, gravity: -0.3f, upBias: 1f);
            GameSound.Cue("swat", _t[i]);
            if (hand >= 0) HandBuzz((GameHand)hand);
            Debug.Log("[VBS][BatOff] bounced " + _bounced + " (" + (hand < 0 ? "ghost paw" : ((GameHand)hand).ToString().ToLowerInvariant() + " hand") +
                      ", " + handVel.magnitude.ToString("0.0") + " m/s)");
        }

        void GhostBat(int i, Vector3 headPos)
        {
            Vector3 p = _t[i].position;
            Vector3 away = p - _hive.DoorPoint.position;
            Vector3 fromHead = p - headPos;
            fromHead.y = 0f;
            if (fromHead.sqrMagnitude < 0.8f * 0.8f) // at the face: a sideways swat across the view, like a real hand
                away = Vector3.Cross(Vector3.up, fromHead.sqrMagnitude > 1e-4f ? fromHead.normalized : Vector3.forward) * (i % 2 == 0 ? 1f : -1f) + fromHead.normalized * 0.4f;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f) away = -_hive.DoorPoint.forward;
            away = Quaternion.Euler(0f, Random.Range(-30f, 30f), 0f) * away.normalized;
            Hit(i, (away + Vector3.up * 0.5f) * Random.Range(2.6f, 4f), away, -1, headPos);
        }

        void Launch(int i, Vector3 v)
        {
            ref var w = ref _w[i];
            var rb = _rb[i];
            _t[i].localScale = Vector3.one * Size(i);
            _col[i].enabled = true;
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.position = _t[i].position;
            rb.linearVelocity = v;
            rb.angularVelocity = Random.onUnitSphere * Random.Range(14f, 24f);
            w.state = S.Tumble;
            w.at = Time.time;
            w.verticalSpeed = v.y;
            _floor.SetActive(true);
        }

        void Daze(int i, float now)
        {
            ref var w = ref _w[i];
            var rb = _rb[i];
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }
            rb.interpolation = RigidbodyInterpolation.None;
            _col[i].enabled = false;
            Vector3 p = _t[i].position;
            p.y = Mathf.Max(p.y, _floorY + Radius(i));
            w.from = p;
            w.state = S.Dazed;
            w.at = now;
        }

        void Leave(int i, float now, Vector3 headPos)
        {
            ref var w = ref _w[i];
            Vector3 p = _t[i].position;
            Vector3 away = p - headPos;
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            away = Quaternion.Euler(0f, Random.Range(-35f, 35f), 0f) * away;
            w.from = p;
            w.to = p + away * 5f;
            w.to.y = Mathf.Max(p.y + 2.2f, _roofY + 1.5f);
            w.ctrl = p + away * 0.5f; // up first, then away: a dazed wasp in front of the hive never flies through it
            w.ctrl.y = Mathf.Max(p.y + 0.6f, _roofY + 0.7f);
            w.state = S.Leave;
            w.at = now;
        }

        void Fly(int i, Vector3 p, float now)
        {
            ref var w = ref _w[i];
            var tr = _t[i];
            Vector3 d = p - w.last;
            tr.position = p;
            if (d.sqrMagnitude > 1e-8f) tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.LookRotation(d, Vector3.up), 0.35f);
            w.last = p;
            FlapWings(i, now, 1f);
        }

        void FlapWings(int i, float now, float rate)
        {
            float f = Mathf.Abs(Mathf.Sin(now * 70f * rate + _w[i].phase));
            _wings[i].localScale = new Vector3(0.45f + 0.55f * f, 1f, 1f);
        }

        static Vector3 Bezier(Vector3 a, Vector3 c, Vector3 b, float k)
        {
            float u = 1f - k;
            return u * u * a + 2f * u * k * c + k * k * b;
        }

        static Vector3 Buzz(float t, float ph, float amp) =>
            new Vector3(Mathf.Sin(t * 7.1f + ph), Mathf.Sin(t * 9.3f + ph * 2f), Mathf.Sin(t * 6.3f + ph * 3f)) * amp;

        void Park(int i)
        {
            var rb = _rb[i];
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }
            rb.interpolation = RigidbodyInterpolation.None;
            _col[i].enabled = false;
            if (_t[i].gameObject.activeSelf) _t[i].gameObject.SetActive(false);
        }

        // ------------------------------------------------------------ hands
        void ReadHands(float dt)
        {
            for (int h = 0; h < 2; h++)
            {
                var hand = (GameHand)h;
                if (!GameHands.TryGetIndexTip(hand, out var tip))
                {
                    _handOk[h] = _swinging[h] = false;
                    if (_swatters[h] != null) _swatters[h].gameObject.SetActive(false);
                    continue;
                }
                var src = GameHands.For(hand);
                bool controller = src is IControllerPoseSource c && c.IsController(hand);
                bool hadPose = _handOk[h] && controller == _handCtl[h];
                _handCtl[h] = controller;
                Vector3 back;
                bool hasRay = GameHands.TryGetPointerRay(hand, out var ray);
                if (hasRay)
                {
                    Vector3 d = tip - ray.origin;
                    float m = d.magnitude;
                    back = m > 0.03f && m < 0.3f ? tip - d / m * 0.09f : tip - ray.direction * 0.09f;
                }
                else back = tip;
                bool hadSwatter = _swatters[h] != null && _swatters[h].gameObject.activeSelf;
                bool hasSwatter = PoseSwatter(h, tip, back, src, hasRay, ray);
                Vector3 strike = hasSwatter ? _swatterHead[h] : (tip + back) * 0.5f;
                if (hadPose && hadSwatter == hasSwatter)
                {
                    Vector3 v = (strike - _handPrev[h]) / dt;
                    // Reacquired tracking or changed pose source can move the blade discontinuously.
                    if (v.sqrMagnitude > 12f * 12f) v = Vector3.zero;
                    _handVel[h] = Vector3.Lerp(_handVel[h], v, 0.6f);
                }
                else _handVel[h] = Vector3.zero;
                _handPrev[h] = strike;
                _handTip[h] = tip;
                _handBack[h] = back;
                _handOk[h] = true;
                if (hasSwatter) StepSwingCue(h);
            }
        }

        void BuildSwatters()
        {
            var filters = FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var filter in filters)
            {
                if (filter.name != "Swatter (art)" || filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var art)) continue;
                float scale = SwatterLength / Mathf.Max(0.001f, filter.sharedMesh.bounds.size.y);
                for (int h = 0; h < _swatters.Length; h++)
                {
                    var go = new GameObject(h == 0 ? "Left fly swatter" : "Right fly swatter") { layer = 2 };
                    go.transform.SetParent(transform, false);
                    go.transform.localScale = Vector3.one * scale;
                    BatOffArt.Renderer(go, filter.sharedMesh, art.sharedMaterial);
                    _swatters[h] = go.transform;
                    go.SetActive(false);
                }
                break;
            }
        }

        bool PoseSwatter(int h, Vector3 tip, Vector3 back, IHandPoseSource source, bool hasRay, Ray ray)
        {
            Transform swatter = _swatters[h];
            if (swatter == null) return false;
            if (_burstDone)
            {
                swatter.gameObject.SetActive(false);
                _swinging[h] = false;
                return false;
            }
            Vector3 aim = hasRay ? ray.direction.normalized : (tip - back).normalized;
            Vector3 up = Vector3.up;
            Vector3 grip = Vector3.Lerp(back, tip, 0.4f);
            var hand = (GameHand)h;
            if (!_handCtl[h] && source is IHandPalmSource palm &&
                palm.TryGetPalm(hand, out var centre, out var normal, out var fingers, out _))
            {
                grip = centre;
                aim = fingers.normalized;
                up = normal;
            }
            else if (_handCtl[h] && TryGetControllerUp(hand, out var controllerUp))
                up = controllerUp;
            if (aim.sqrMagnitude < 0.5f)
            {
                var head = Head();
                aim = head != null ? (tip - head.position).normalized : transform.forward;
            }
            if (aim.sqrMagnitude < 0.5f) { swatter.gameObject.SetActive(false); return false; }
            up = Vector3.ProjectOnPlane(up, aim);
            if (up.sqrMagnitude < 0.01f)
                up = Mathf.Abs(Vector3.Dot(aim, Vector3.up)) > 0.98f ? transform.forward : Vector3.up;
            swatter.SetPositionAndRotation(grip, Quaternion.LookRotation(aim, up) * Quaternion.Euler(90f, 0f, 0f));
            _swatterHead[h] = grip + aim * (SwatterLength * 0.85f);
            if (!swatter.gameObject.activeSelf)
            {
                _fx.Burst(grip, 9, Puff, 0.45f, 0.38f, 0.022f, gravity: -0.15f, upBias: 0.35f);
                GameSound.Cue("swatter_swoosh", grip);
                swatter.gameObject.SetActive(true);
            }
            return true;
        }

        static bool TryGetControllerUp(GameHand hand, out Vector3 up)
        {
            var device = InputDevices.GetDeviceAtXRNode(hand == GameHand.Left ? XRNode.LeftHand : XRNode.RightHand);
            up = Vector3.up;
            if (!device.isValid || !device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation)) return false;
            var head = Head();
            Transform trackingSpace = head != null ? head.parent : null;
            up = (trackingSpace != null ? trackingSpace.rotation * rotation : rotation) * Vector3.up;
            return true;
        }

        void StepSwingCue(int h)
        {
            float speed = _handVel[h].sqrMagnitude;
            if (!_swinging[h] && speed > SwingSpeed * SwingSpeed)
            {
                GameSound.Cue("swatter_swoosh", _swatterHead[h]);
                _swinging[h] = true;
            }
            else if (speed < 0.65f * 0.65f) _swinging[h] = false;
        }

        void TouchHands(int i, Vector3 headPos)
        {
            Vector3 p = _t[i].position;
            for (int h = 0; h < 2; h++)
            {
                if (!_handOk[h]) continue;
                bool hasSwatter = _swatters[h] != null && _swatters[h].gameObject.activeSelf;
                Vector3 a = _handBack[h], b = hasSwatter ? _swatterHead[h] : _handTip[h], ab = b - a;
                float k = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                Vector3 closest = a + ab * k;
                Vector3 d = p - closest;
                float r = (_handCtl[h] ? ControllerRadius : HandRadius) + Radius(i);
                if (d.sqrMagnitude > r * r) continue;
                Vector3 away = d.sqrMagnitude > 1e-6f ? d.normalized : (_handVel[h].sqrMagnitude > 1e-4f ? _handVel[h].normalized : Vector3.up);
                Hit(i, _handVel[h], away, h, headPos);
                return;
            }
        }

        void HandBuzz(GameHand hand)
        {
            GameHaptics.Pulse(hand, 0.7f, 0.07f);
        }

        // ------------------------------------------------------------ end
        void EndBurst(string why)
        {
            if (_burstDone) return;
            _burstDone = true;
            StopRaid();
            float now = Time.time;
            Vector3 headPos = Head() != null ? Head().position : Vector3.up * 1.6f;
            for (int i = 0; i < _n; i++)
            {
                ref var w = ref _w[i];
                if (w.state == S.Wait) { w.state = S.Gone; Park(i); }
                else if (w.state == S.Dive || w.state == S.Hover || w.state == S.Raid || w.state == S.HitStop) Leave(i, now, headPos);
            }
            if (_station != null && (_station.Busy || _station.Solved || _station.Choosing)) _cardHideAt = now + 1f; // Run was stopped: hide the card ourselves
            Debug.Log("[VBS][BatOff] done (" + why + "): " + _bounced + " bounced, " + _reached + " reached the door, " +
                      (now - _startedAt).ToString("0.0") + " s");
        }

        void Cleanup(string why)
        {
            if (!_burstDone) EndBurst(why);
            _running = false;
            for (int i = 0; i < Max; i++) { _w[i].state = S.Gone; Park(i); }
            _floor.SetActive(false);
            foreach (var swatter in _swatters)
                if (swatter != null) swatter.gameObject.SetActive(false);
            _card.Show(false);
            _cardHideAt = -1f;
        }
    }
}
