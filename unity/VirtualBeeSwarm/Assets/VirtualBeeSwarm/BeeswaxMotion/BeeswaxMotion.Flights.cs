using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>BeeswaxMotion's flights: P1 TAKE THE PODIUM and the P10 ADJOURN variants (lap, duck, creep, drop, perch, limp, route).</summary>
    public partial class BeeswaxMotion
    {
        sealed class Flight
        {
            public string kind;
            public Vector3 start, p0, end, rawGoal, cur, vel, away;
            public Vector3[] chain;           // catmull-rom: p0, waypoints..., end
            public float h, T, speed, tau, pre, age, length, faceUntil = 0.7f;
            public string preKind = "";
            public float[] table;
            public bool crab, fastOk, pirouette, pirouetteDone, perch, settle = true, easeIn, linear, rollAtHalf, rollDone, wobble, tailing;
            public float minEye = 9f;
            public int order;                 // the order that planned it: only a newer order replans

            public Vector3 Path(float u)
            {
                Vector3 e = perch ? end + Vector3.up * 0.03f : end;
                if (chain != null) { chain[chain.Length - 1] = e; return MotionCurves.CatmullRom(chain, u); }
                if (h > 0f) return MotionCurves.Bezier(p0, MotionCurves.ArcControl(p0, e, h), e, u);
                return Vector3.Lerp(p0, e, u);
            }

            public float Progress => tailing ? 1f : speed > 0f ? (length > 1e-4f ? tau / length : 1f) : (T > 1e-4f ? tau / T : 1f);
            public bool FaceTravel => !crab && age >= pre && Progress < faceUntil;
            public float Ease(float u) => easeIn ? MotionCurves.InQuad(u) : linear ? MotionCurves.C01(u) : MotionCurves.InOutSine(u);

            public Vector3 PrePos(float a)
            {
                switch (preKind)
                {
                    case "adjourn":
                        // anticipation: dip 2 cm and back off 2 cm from the goal (0.12 s), then lift 8 cm (0.20 s)
                        Vector3 dip = start + (Vector3.down * 0.02f + away * 0.02f) * MotionCurves.OutQuad(a / 0.12f);
                        if (a <= 0.12f) return dip;
                        return start + Vector3.down * 0.02f + away * 0.02f + Vector3.up * (0.08f * MotionCurves.InOutSine((a - 0.12f) / 0.20f)); // peak 0.63 m/s
                    case "flinch":
                        return start + (Vector3.down * 0.08f + away * 0.05f) * MotionCurves.OutCubic(a / 0.3f);
                    case "dazed":
                        return start + (Vector3.up * 0.10f + away * 0.10f) * MotionCurves.InOutSine(Mathf.Clamp01(a / 0.6f)); // peak 0.37 m/s
                    default:
                        return start;
                }
            }
        }

        Flight _fl;
        bool _perched, _entrancePending, _pirouetteDone, _hiding;
        float _parkedSince = -1f, _lapWindowUntil = -1f, _jumpUntil = -1f;
        int _teleportFrame = -1;
        readonly List<System.Action> _onLanded = new List<System.Action>();
        float _onLandedUntil = -1f;

        static bool IsPerch(Transform t)
        {
            if (t == null) return false;
            string n = t.name;
            return n.Contains("Perch") || n.Contains("Seat") || n.Contains("Lid") || n.Contains("Roof");
        }

        Vector3 Local(Vector3 world) => _anchor != null ? _anchor.InverseTransformPoint(world) : world;
        Vector3 World(Vector3 local) => _anchor != null ? _anchor.TransformPoint(local) : local;

        bool OppositeSides(Vector3 a, Vector3 b)
        {
            Vector3 la = Local(a), lb = Local(b);
            return Mathf.Sign(la.x) != Mathf.Sign(lb.x) && Mathf.Abs(la.x - lb.x) > 0.4f;
        }

        bool HiddenBehindHive(Vector3 q)
        {
            if (_hive == null) return false;
            Vector3 l = Local(q), h = Local(_hive.transform.position);
            return l.z > h.z + 0.05f && l.y < h.y + 0.34f && Mathf.Abs(l.x - h.x) < 0.3f;
        }

        bool CardsUp()
        {
            if (_scoreCard != null && _scoreCard.gameObject.activeInHierarchy) return true;
            if (_map != null && _map.gameObject.activeInHierarchy) return true;
            for (int i = 0; i < KeepClearList.Count; i++)
                if (KeepClearList[i].Key != null && KeepClearList[i].Key.gameObject.activeInHierarchy) return true;
            return false;
        }

        // ------------------------------------------------------------ planning
        /// <summary>A new order (or the climb's fly-up): pick the flight. True = he is already there (no flight).</summary>
        bool Plan(Vector3 raw, Vector3 g)
        {
            Vector3 p = transform.position;
            float d = Vector3.Distance(p, g);
            string order = Time.time - _orderAt < 0.3f ? _order : "";
            if (Time.time < _jumpUntil)
            {
                // Board jump / reset: straight to the beat's rest pose
                transform.position = g;
                _teleportFrame = Time.frameCount;
                Log("Snap", "jump to " + BeatName);
                _parkedSince = Time.time;
                return true;
            }
            if (d < 0.01f) { InPlaceArrival(order); _entrancePending = false; return true; }

            bool parked = _parkedSince >= 0f && !_entrancePending;
            GameBeat beat = Beat;
            Flight fl;
            string why = order != "" ? order : "flight";
            if (_entrancePending)
            {
                // out of the hive door: a straight rise at about 0.35 m/s, then the hanging settle and a face snap
                _entrancePending = false;
                fl = new Flight { kind = "entrance", start = p, p0 = p, cur = p, rawGoal = raw, end = g, T = Mathf.Max(0.5f, d / 0.35f), crab = true };
                why = "climbed out";
            }
            else if (beat == GameBeat.T0 && order == "present" && _attachedThisBeat)
            {
                // the crash: DAZED (1.2 s on the spot), then LIMP home at 0.35 m/s with a wobble
                fl = MakeP1(p, raw, g, "limp", false);
                fl.preKind = "dazed"; fl.pre = 1.2f;
                // dazed in the open: 10 cm up and 10 cm clear of the fallen rocket (not tangled in it, not on the floor)
                Vector3 clear = _rocket != null ? Vector3.ProjectOnPlane(p - _rocket.position, Vector3.up) : Vector3.zero;
                if (clear.sqrMagnitude < 1e-4f && _hasEye) clear = Vector3.ProjectOnPlane(_ctx.eye - p, Vector3.up);
                fl.away = clear.sqrMagnitude > 1e-6f ? clear.normalized : Vector3.zero;
                fl.p0 = fl.PrePos(fl.pre);
                fl.speed = 0.35f; fl.wobble = true;
                PlayDazed();
                why = "crash landing";
            }
            else if (IsPerch(_b.GoalTransform))
            {
                fl = MakeP1(p, raw, g, "perch", parked);
                fl.perch = true; fl.settle = false;
                why = order + " " + _b.GoalTransform.name;
            }
            else if (beat == GameBeat.WhatIf && !ComfortMode && Time.time < _lapWindowUntil && OppositeSides(p, g))
            {
                fl = MakeLap(p, raw, g, parked);
                why = "four for four";
            }
            else if (beat == GameBeat.Wasps && HiddenBehindHive(g) && !HiddenBehindHive(p))
            {
                fl = MakeDuck(p, raw, g);
                why = "wasps";
            }
            else if (beat == GameBeat.Wasps && HiddenBehindHive(p) && !HiddenBehindHive(g))
            {
                fl = MakeP1(p, raw, g, "creep", false);
                fl.h = 0.02f; fl.speed = 0.30f;
                if (_hiding) { _hiding = false; _b.SetCaptionAnchor(null); }
                why = "out of hiding";
            }
            else if (!ComfortMode && p.y - g.y > 0.15f && Vector2.Distance(new Vector2(p.x, p.z), new Vector2(g.x, g.z)) < 0.15f)
            {
                fl = new Flight { kind = "drop", start = p, p0 = p, cur = p, rawGoal = raw, end = g, T = 0.55f, easeIn = true, fastOk = true, crab = true, settle = false };
            }
            else if (CardsUp() && OppositeSides(p, g))
            {
                fl = MakeRoute(p, raw, g, parked);
                why = order + " (behind the hive: a card is up)";
            }
            else if (d < 0.10f)
            {
                fl = new Flight { kind = "short", start = p, p0 = p, cur = p, rawGoal = raw, end = g, T = Mathf.Clamp(0.25f + d / 0.4f, 0.3f, 0.5f), crab = true, settle = false };
            }
            else
            {
                fl = MakeP1(p, raw, g, "p1", parked);
                if (beat == GameBeat.Smoker && order == "present" && !_pirouetteDone) { fl.pirouette = true; why = "problem one"; }
            }
            if (fl.speed > 0f) BuildTable(fl);
            StartFlight(fl, why);
            return false;
        }

        Flight MakeP1(Vector3 p, Vector3 raw, Vector3 g, string kind, bool parked)
        {
            float d = Vector3.Distance(p, g);
            var fl = new Flight { kind = kind, start = p, p0 = p, cur = p, rawGoal = raw, end = g, h = MotionCurves.ArcHeight(d), T = MotionCurves.FlightSeconds(d) };
            fl.crab = d < 0.35f; // never turn your back on the contestant
            fl.faceUntil = Mathf.Max(0.7f, 1f - 0.25f / fl.T);
            if (parked && d >= 0.25f) Adjourn(fl, g);
            return fl;
        }

        void Adjourn(Flight fl, Vector3 g)
        {
            Vector3 away = fl.start - g;
            away.y = 0f;
            fl.away = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.zero;
            fl.preKind = "adjourn";
            fl.pre = 0.32f; // never delays an ordered flight by more than 0.35 s
            fl.p0 = fl.PrePos(fl.pre);
            fl.cur = fl.start;
        }

        Flight MakeLap(Vector3 p, Vector3 raw, Vector3 g, bool parked)
        {
            var fl = new Flight { kind = "lap", start = p, p0 = p, cur = p, rawGoal = raw, end = g, speed = 0.6f, rollAtHalf = true };
            if (parked) Adjourn(fl, g);
            bool fromRight = Local(p).x > 0f;
            var w = new List<Vector3> { fl.p0 };
            Vector3 w1 = new Vector3(0.30f, 0.50f, 0.20f), w2 = new Vector3(0f, 0.56f, 0.45f), w3 = new Vector3(-0.30f, 0.50f, 0.20f);
            if (fromRight) { w.Add(World(w1)); w.Add(World(w2)); w.Add(World(w3)); }
            else { w.Add(World(w3)); w.Add(World(w2)); w.Add(World(w1)); }
            w.Add(g);
            fl.chain = w.ToArray();
            Rig("Celebrate", "four for four");
            return fl;
        }

        Flight MakeRoute(Vector3 p, Vector3 raw, Vector3 g, bool parked)
        {
            var fl = new Flight { kind = "route", start = p, p0 = p, cur = p, rawGoal = raw, end = g, speed = 0.6f };
            if (parked) Adjourn(fl, g);
            float y = 0.34f + 0.12f;
            bool fromRight = Local(p).x > 0f;
            Vector3 w1 = new Vector3(0.30f, y, 0.20f), w2 = new Vector3(0f, y, 0.45f), w3 = new Vector3(-0.30f, y, 0.20f);
            fl.chain = fromRight ? new[] { fl.p0, World(w1), World(w2), World(w3), g } : new[] { fl.p0, World(w3), World(w2), World(w1), g };
            return fl;
        }

        Flight MakeDuck(Vector3 p, Vector3 raw, Vector3 g)
        {
            var fl = new Flight { kind = "duck", start = p, cur = p, rawGoal = raw, end = g, T = 0.8f };
            Vector3 back = _hasEye ? p - _ctx.eye : -transform.forward;
            back.y = 0f;
            fl.away = back.sqrMagnitude > 1e-6f ? back.normalized : Vector3.forward;
            fl.preKind = "flinch"; fl.pre = 0.3f;
            fl.p0 = fl.PrePos(fl.pre);
            // around the hive's right side (between the hive and the rocket), never over the roof
            Vector3 hl = _hive != null ? Local(_hive.transform.position) : Vector3.zero;
            fl.chain = new[] { fl.p0, World(new Vector3(hl.x + 0.24f, 0.2f, hl.z)), g };
            Rig("Startle", "duck");
            return fl;
        }

        void BuildTable(Flight fl)
        {
            fl.table = MotionCurves.ArcTable(fl.Path, 64, out fl.length);
        }

        void StartFlight(Flight fl, string why)
        {
            _fl = fl;
            fl.order = _orderSerial;
            _govOff = Vector3.zero; // it starts from where he really is
            _parkedSince = -1f;
            if (_perched && _hasEye && (fl.start - _ctx.eye).sqrMagnitude <= 2.25f)
                GameSound.Cue("bee_wing", fl.start);
            _perched = false;
            _player.Cancel("Settle", _mt);
            float d = Vector3.Distance(fl.start, fl.end);
            string variant = fl.kind == "p1" ? "" : " " + fl.kind;
            Log("TakePodium" + variant, why + " d=" + d.ToString("0.00", CultureInfo.InvariantCulture) + " m" +
                (fl.speed > 0f ? " at " + fl.speed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s" : " T=" + fl.T.ToString("0.00", CultureInfo.InvariantCulture) + " s") +
                (fl.pre > 0f ? " +" + fl.preKind + " " + fl.pre.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "") + (fl.pirouette ? " +pirouette" : ""));
        }

        // ------------------------------------------------------------ stepping
        Vector3 StepFlight(Flight fl, float dt, out bool done)
        {
            done = false;
            fl.age += dt;
            Vector3 prev = fl.cur, pos;
            if (fl.age < fl.pre) pos = fl.PrePos(fl.age);
            else if (fl.tailing)
            {
                // PERCH: the last 3 cm at 0.1 m/s, no overshoot
                pos = Vector3.MoveTowards(fl.cur, fl.end, 0.1f * dt);
                done = (pos - fl.end).sqrMagnitude < 1e-6f;
            }
            else if (fl.speed > 0f)
            {
                if (fl.table == null) BuildTable(fl);
                float u = MotionCurves.UAtLength(fl.table, fl.tau);
                Vector3 a = fl.Path(u), b = fl.Path(Mathf.Min(1f, u + 0.01f));
                Vector3 dir = b - a;
                dir = dir.sqrMagnitude > 1e-10f ? dir.normalized : Vector3.zero;
                float cap = _hasEye ? Mathf.Min(MotionGovernor.FastSpeed, MotionGovernor.MaxSpeedAlong(fl.cur, dir, _ctx, fl.fastOk)) : fl.speed;
                fl.tau += Mathf.Min(fl.speed, cap) * dt;
                pos = fl.Path(MotionCurves.UAtLength(fl.table, fl.tau));
                if (fl.wobble)
                {
                    float w = fl.age - fl.pre;
                    pos.y += 0.015f * Mathf.Sin(2f * Mathf.PI * 3f * w) * Mathf.Exp(-w / 1.2f) * Mathf.Clamp01((fl.length - fl.tau) / 0.10f); // gone by the landing: no pop
                }
                if (fl.tau >= fl.length) { if (fl.perch) fl.tailing = true; else done = true; }
            }
            else
            {
                float next = fl.tau + dt;
                Vector3 b = fl.Path(fl.Ease(next / fl.T));
                Vector3 step = b - fl.cur;
                float m = step.magnitude;
                if (m > 1e-6f && _hasEye)
                    next = MotionCurves.WarpedTau(fl.tau, dt, m, Mathf.Min(MotionGovernor.FastSpeed, MotionGovernor.MaxSpeedAlong(fl.cur, step / m, _ctx, fl.fastOk)));
                fl.tau = next;
                pos = fl.Path(fl.Ease(Mathf.Min(1f, fl.tau / fl.T)));
                if (fl.pirouette && !fl.pirouetteDone && fl.T - fl.tau <= 0.5f)
                {
                    fl.pirouetteDone = _pirouetteDone = true;
                    PlayPirouette(Mathf.Max(0.2f, fl.T - fl.tau), "problem one (last 0.5 s of the approach)");
                }
                if (fl.tau >= fl.T) { if (fl.perch) fl.tailing = true; else done = true; }
            }
            if (fl.rollAtHalf && !fl.rollDone && fl.Progress >= 0.5f) { fl.rollDone = true; PlayBarrelRoll(); }
            fl.vel = dt > 1e-6f ? (pos - prev) / dt : Vector3.zero;
            fl.cur = pos;
            if (_hasEye) fl.minEye = Mathf.Min(fl.minEye, Vector3.Distance(pos, _ctx.eye));
            return pos;
        }

        void Landed(Flight fl)
        {
            _parkedSince = Time.time;
            if (logPrimitives)
                Debug.Log("[VBS][Motion] TakePodium " + fl.kind + " landed after " + fl.age.ToString("0.00", CultureInfo.InvariantCulture) + " s, min eye " +
                          fl.minEye.ToString("0.000", CultureInfo.InvariantCulture) + " m");
            if (fl.perch) _perched = true;
            if (fl.settle) PlaySettle();
            if (fl.kind == "drop") PlayBounce();
            if (_hasEye && (fl.perch || fl.kind == "drop")) GameSound.BeeLanding(fl.cur, _ctx.eye);
            if (fl.kind == "duck" && _b.PresenterPoint != null) { _hiding = true; _b.SetCaptionAnchor(_b.PresenterPoint, 0.13f); }
            // face snap with a small overshoot if he is not facing the player yet
            if (_hasEye && Vector3.Angle(transform.forward, _ctx.eye - transform.position) > 15f) _snapUntil = Time.time + 0.4f;
            if (_onLanded.Count > 0)
            {
                bool inTime = Time.time <= _onLandedUntil;
                var run = new List<System.Action>(_onLanded);
                _onLanded.Clear();
                if (inTime) foreach (var a in run) a();
            }
        }

        /// <summary>Accents wait for flights: run now if parked, else on landing (if that is within maxWait), else skip.</summary>
        void WhenLanded(System.Action a, float maxWait, string what)
        {
            if (_fl == null) { a(); return; }
            _onLanded.Add(a);
            _onLandedUntil = Time.time + maxWait;
            if (logPrimitives) Debug.Log("[VBS][Motion] " + what + " waits for the landing (up to " + maxWait.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
        }
    }
}
