using UnityEngine;
using C = VirtualBeeSwarm.MotionCurves;

namespace VirtualBeeSwarm
{
    /// <summary>BeeswaxMotion's in-place primitives (P2-P9 and the accents) as small tween players on the body.</summary>
    public partial class BeeswaxMotion
    {
        MotionTrack Play(MotionTrack t, string why, bool log = true)
        {
            t.why = log ? why : null;
            if (!_active) return t; // module off (the Field beat): nothing piles up for later
            _player.Play(t, _mt);
            if (log) Log(t.name, why);
            return t;
        }

        void Rig(string clip, string reason)
        {
            if (_rig != null && _rig.isActiveAndEnabled) _rig.Play(clip, "motion: " + reason);
        }

        Vector3 Horizontal(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.zero; }

        // ------------------------------------------------------------ flight helpers
        void PlaySettle()
        {
            Play(new MotionTrack
            {
                name = "Settle", length = C.SettleSeconds,
                eval = (tr, t) => new MotionSample { stage = new Vector3(C.SwayX(t), C.SettleY(t) + C.SwayY(t), 0f), rot = new Vector3(0f, 0f, C.SwayRoll(t)) }
            }, null, false);
        }

        void PlayBounce()
        {
            Play(new MotionTrack { name = "Bounce", length = 0.2f, eval = (tr, t) => new MotionSample { stage = new Vector3(0f, C.Hop(t, 0.015f, 0.08f, 0.12f), 0f) } }, null, false);
        }

        void PlayPirouette(float seconds, string why)
        {
            Play(new MotionTrack
            {
                name = "Pirouette", length = seconds, spin = true,
                eval = (tr, t) => new MotionSample { spinYaw = 360f * C.InOutSine(t / seconds) }
            }, why);
        }

        void PlayBarrelRoll()
        {
            Play(new MotionTrack { name = "BarrelRoll", length = 0.45f, spin = true, eval = (tr, t) => new MotionSample { spinRoll = 360f * C.InOutSine(t / 0.45f) } }, "four for four (the top of the lap)");
        }

        void PlaySpin(string why, float seconds = 0.5f)
        {
            Play(new MotionTrack { name = "Spin", length = seconds, spin = true, eval = (tr, t) => new MotionSample { spinYaw = C.Spin(t, seconds) } }, why);
        }

        void PlayDazed()
        {
            Play(new MotionTrack
            {
                name = "Dazed", length = 1.5f, ambient = true,
                eval = (tr, t) =>
                {
                    float fade = t > 1.2f ? 1f - (t - 1.2f) / 0.3f : 1f;
                    float a = 2f * Mathf.PI * 1.3f * t;
                    return new MotionSample { stage = new Vector3(Mathf.Cos(a) * 0.012f, Mathf.Sin(a) * 0.012f, 0f) * fade, rot = new Vector3(0f, 0f, 10f * Mathf.Sin(a) * fade) };
                }
            }, "crash landing (sooty)");
        }

        /// <summary>An order to where he already is: re-take the podium in place (the first station: a pirouette).</summary>
        void InPlaceArrival(string order)
        {
            bool station = Beat == GameBeat.Smoker || Beat == GameBeat.Feed || Beat == GameBeat.Wasps || Beat == GameBeat.Heat;
            if (order != "present" || !station || _dropAct) return;
            if (Beat == GameBeat.Smoker && !_pirouetteDone)
            {
                _pirouetteDone = true;
                PlayPirouette(0.6f, "problem one (already on the podium)");
                Play(new MotionTrack { name = "Hop", length = 0.3f, eval = (tr, t) => new MotionSample { stage = new Vector3(0f, C.Hop(t, 0.02f, 0.12f, 0.18f), 0f) } }, null, false);
                Log("TakePodium in place", "problem one");
            }
            else
            {
                Play(new MotionTrack { name = "Hop", length = 0.3f, eval = (tr, t) => new MotionSample { stage = new Vector3(0f, C.Hop(t, 0.02f, 0.12f, 0.18f), 0f) } }, null, false);
                Log("TakePodium in place", "station " + BeatName);
            }
        }

        // ------------------------------------------------------------ P2 TAKE THE FLOOR
        void PlayTakeFloor(string why, float pitch = 8f)
        {
            if (_dropAct || _hiding) return;
            WhenLanded(() =>
            {
                var tr = Play(new MotionTrack
                {
                    name = "TakeFloor", outSeconds = 0.4f,
                    eval = (t0, t) =>
                    {
                        float w = C.HeldSoft(t, 0.32f, t0.Rel, 0.40f);
                        return new MotionSample { stage = new Vector3(0f, 0.015f * w, 0.05f * w), rot = new Vector3(pitch * w, 0f, 0f), bob = -0.34f * w };
                    }
                }, why);
                _releaseOnLineEnd.Add(tr);
                _p2Done = true;
            }, 1.5f, "TakeFloor");
        }

        // ------------------------------------------------------------ P3 TABLE IT
        void PlayTableIt(string why)
        {
            if (_dropAct || _hiding) return;
            ChoiceTargets();
            if (_choiceP.Count == 0) return;
            Vector3 arc = Vector3.zero;
            for (int i = 0; i < _choiceP.Count; i++) arc += _choiceP[i];
            arc /= _choiceP.Count;
            Vector3 p = transform.position;
            Vector3 eye = _hasEye ? _ctx.eye : p - transform.forward;
            // yaw at most 20 deg toward the arc centre: both eyes stay on the player
            float full = Vector3.Angle(Horizontal(eye - p), Horizontal(arc - p));
            float lookW = full > 1f ? Mathf.Min(1f, 20f / full) : 0f;
            // slide 4 cm toward the arc along the circle around the eye, never closer than 14.5 cm to a prop
            Vector3 radial = Horizontal(p - eye);
            Vector3 tangent = Vector3.Cross(Vector3.up, radial);
            if (Vector3.Dot(tangent, arc - p) < 0f) tangent = -tangent;
            float slide = 0.04f;
            for (int i = 0; i < _choiceP.Count; i++)
            {
                Vector3 q = p + tangent * slide, c = _choiceP[i];
                float hd = Vector2.Distance(new Vector2(q.x, q.z), new Vector2(c.x, c.z));
                if (hd < 0.145f) slide = Mathf.Max(0f, slide - (0.145f - hd));
            }
            Vector3 slideV = tangent * slide;
            Play(new MotionTrack
            {
                name = "TableIt", length = 1.2f, target = arc,
                eval = (tr, t) =>
                {
                    float w = C.Pulse(t, 0.25f, 0.6f, 0.35f);
                    return new MotionSample { world = slideV * w, lookW = lookW * w, lookAt = tr.target };
                }
            }, why + " (yaw " + Mathf.Min(20f, full).ToString("0") + " deg, slide " + (slide * 100f).ToString("0.0") + " cm)");
        }

        // ------------------------------------------------------------ P4 HOLD THE FLOOR
        float _tickPause;

        void PlayHoldFloor(string why, bool calm = false)
        {
            if (_dropAct) return;
            float heat = Beat == GameBeat.Heat ? 0.6f : 1f;
            float tiltSide = _hive != null && transform.InverseTransformPoint(_hive.transform.position).x > 0f ? 1f : -1f; // head tilts toward the choices
            Play(new MotionTrack
            {
                name = "HoldFloor", outSeconds = 0.25f,
                eval = (tr, t) =>
                {
                    float env = C.Held(t, 0.2f, tr.Rel, 0.25f);
                    if (_hiding) env = 0f;
                    float well = calm ? 0f : C.WellLean(t);
                    float tick = calm ? 0f : C.TickRoll(t) * heat * (1f - _tickPause);
                    return new MotionSample
                    {
                        stage = new Vector3(0f, calm ? 0f : C.TapHop(t), 0.045f * well) * env,
                        rot = new Vector3(18f * well, 0f, tick * (1f - well) + 12f * tiltSide * well) * env,
                    };
                }
            }, why);
        }

        // ------------------------------------------------------------ P4 extras: OFF THE RECORD, GAVEL
        void PlayOffRecord(string why)
        {
            if (_dropAct || _hiding) return;
            float yawSign = 1f;
            if (_hive != null) yawSign = transform.InverseTransformPoint(_hive.transform.position).x > 0f ? -1f : 1f; // turn away from the hive
            var tr = Play(new MotionTrack
            {
                name = "OffRecord", outSeconds = 0.4f, pausesTick = true,
                eval = (t0, t) =>
                {
                    float w = C.HeldSoft(t, 0.35f, t0.Rel, 0.40f);
                    return new MotionSample { stage = new Vector3(0f, -0.03f * w, 0.03f * w), rot = new Vector3(6f * w, 15f * yawSign * w, 0f) };
                }
            }, why);
            _releaseOnLineEnd.Add(tr);
        }

        void PlayGavel(string why)
        {
            if (_dropAct || _hiding) return;
            Play(new MotionTrack
            {
                name = "Gavel", length = C.GavelSeconds, primary = true,
                eval = (tr, t) => new MotionSample { stage = new Vector3(0f, C.GavelY(t), 0f), squash = new Vector3(0.08f, -0.12f, 0.08f) * C.GavelSquash(t) }
            }, why);
        }

        // ------------------------------------------------------------ P5 FINAL ANSWER?
        MotionTrack _final;

        void StartFinalAnswer(Transform prop, string why)
        {
            if (_dropAct || _hiding) return;
            _final = Play(new MotionTrack
            {
                name = "FinalAnswer", outSeconds = 0.2f, pausesTick = true, targetT = prop,
                eval = (tr, t) =>
                {
                    float w = C.Held(t, 0.15f, tr.Rel, 0.2f);
                    Vector3 lean = Horizontal(tr.Target - transform.position) * 0.025f;
                    return new MotionSample { world = lean * w, lookW = 0.55f * w, lookAt = tr.Target, bob = -0.5f * w };
                }
            }, why);
        }

        void EndFinalAnswer(string why)
        {
            if (_final == null) return;
            _player.Release("FinalAnswer", _mt);
            _final = null;
            Play(new MotionTrack { name = "Exhale", length = 0.6f, eval = (tr, t) => new MotionSample { stage = new Vector3(0f, -0.015f * C.Pulse(t, 0.2f, 0f, 0.4f), 0f) } }, why);
        }

        // ------------------------------------------------------------ P6 THE TAKE
        void PlayTake(Transform prop, string why)
        {
            Vector3 at = prop != null ? prop.position : transform.position + transform.forward;
            // anticipation, the same for right and wrong: crouch 2 cm, squash 6%, tip 8 deg toward the pick, hold, release
            float side = prop != null ? Mathf.Sign(transform.InverseTransformPoint(prop.position).x) : 0f;
            Play(new MotionTrack
            {
                name = "Take", length = 0.9f, targetT = prop, target = at,
                eval = (tr, t) =>
                {
                    float w = C.Pulse(t, 0.10f, 0.40f, 0.25f), c = C.TakeCrouch(t);
                    return new MotionSample
                    {
                        stage = new Vector3(0f, -0.02f * c, 0f), squash = new Vector3(0.06f, -0.06f, 0.06f) * c, rot = new Vector3(0f, 0f, -8f * side * c),
                        lookW = (prop != null ? 0.5f : 0f) * w, lookAt = tr.Target, bob = -1f * c,
                    };
                }
            }, why);
        }

        // ------------------------------------------------------------ P7 MOTION CARRIED
        void PlayCarried(string why, Transform lookAt)
        {
            Play(new MotionTrack
            {
                name = "Carried", length = C.CarriedSeconds, primary = true, targetT = lookAt,
                eval = (tr, t0) =>
                {
                    float t = t0 - C.CarriedDelay; // the stamp lands, then he hops
                    return new MotionSample
                    {
                        stage = new Vector3(0f, C.CarriedY(t), 0f),
                        squash = new Vector3(0.06f, -0.08f, 0.06f) * C.CarriedSquash(t),
                        rot = new Vector3(C.CarriedBow(t), 0f, 0f),
                        lookW = tr.targetT != null ? C.CarriedLook(t) : 0f,
                        lookAt = tr.Target,
                    };
                }
            }, why);
        }

        // ------------------------------------------------------------ P8 GRIEVANCE
        void PlayGrievance(string why, bool spin, Transform glareAt)
        {
            Play(new MotionTrack
            {
                name = "Grievance", length = 2.4f, primary = true, targetT = glareAt,
                eval = (tr, t) => new MotionSample
                {
                    stage = new Vector3(spin ? 0f : C.BuzzX(t), 0f, 0f),
                    rot = new Vector3(0f, spin ? 0f : C.BuzzYaw(t), C.TutTut(t)),
                    spinYaw = spin ? C.Spin(t, 0.55f) : 0f,
                    lookW = tr.targetT != null ? 0.4f * C.Pulse(t - 0.4f, 0.3f, 1.2f, 0.3f) : 0f,
                    lookAt = tr.Target,
                }
            }, why);
            _player.Cancel("Deflate", _mt);
            Play(new MotionTrack
            {
                name = "Deflate", outSeconds = 0.85f,
                eval = (tr, t) =>
                {
                    float w = C.Deflate(t, tr.Rel) * C.SlumpKeep(t); // the slump eases up: he is readable through the correction line
                    return new MotionSample { stage = new Vector3(0f, -0.06f * w, 0f), rot = new Vector3(14f * w, 0f, 0f), squash = new Vector3(0.05f, -0.07f, 0.05f) * w };
                }
            }, null, false);
        }

        void PlayHarrumph(string why)
        {
            if (!_player.Playing("Deflate") && !_player.Playing("LookUp")) return;
            _player.Release("Deflate", _mt);
            Play(new MotionTrack { name = "Harrumph", length = 0.6f, eval = (tr, t) => new MotionSample { squash = Vector3.one * (0.05f * C.Pulse(t - 0.35f, 0.05f, 0.10f, 0.15f)) } }, why);
        }

        void PlayLookUp(string why)
        {
            if (_dropAct || _hiding) return;
            _player.Release("Deflate", _mt);
            Vector3 up = (_hive != null ? _hive.transform.position : transform.position) + Vector3.up * 0.9f;
            Play(new MotionTrack
            {
                name = "LookUp", length = 2.2f, pausesTick = true, target = up,
                eval = (tr, t) =>
                {
                    float w = C.Pulse(t, 0.4f, 1.4f, 0.4f);
                    return new MotionSample { stage = new Vector3(0f, 0.05f * w, 0f), rot = new Vector3(-25f * w, 0f, 0f), lookW = 0.5f * w, lookAt = tr.target };
                }
            }, why);
            Schedule(2.2f, () => Play(new MotionTrack { name = "Harrumph", length = 0.3f, eval = (tr, t) => new MotionSample { squash = Vector3.one * (0.05f * C.Pulse(t, 0.05f, 0.10f, 0.15f)) } }, "after the look up"));
        }

        // ------------------------------------------------------------ FOUR FOR FOUR in place
        void PlayFourForFour(string why)
        {
            if (_dropAct) return;
            Rig("Celebrate", "four for four");
            Play(new MotionTrack
            {
                name = "FourForFour", length = 1.6f, primary = true, spin = true,
                eval = (tr, t) => new MotionSample
                {
                    stage = new Vector3(0f, C.Hop(t, 0.04f, 0.12f, 0.14f) + C.Hop(t - 0.35f, 0.035f, 0.12f, 0.14f) + C.Hop(t - 0.7f, 0.03f, 0.12f, 0.14f), 0f),
                    spinYaw = C.Spin(t - 1.0f, 0.55f),
                }
            }, why);
        }

        // ------------------------------------------------------------ P9 PICTURE IT
        void PlayPictureIt(Vector3 subject, Transform subjectT, string why)
        {
            if (_dropAct) return;
            if (_player.Playing("PictureIt")) return;
            Rig("Antenna Twitch", "picture it");
            var tr = Play(new MotionTrack
            {
                name = "PictureIt", outSeconds = 0.3f, ambient = true, targetT = subjectT, target = subject, pausesTick = true,
                eval = (t0, t) =>
                {
                    float w = C.Held(t, 0.3f, t0.Rel, 0.3f);
                    float a = 2f * Mathf.PI * 0.8f * t;
                    return new MotionSample { stage = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * (0.008f * w), rot = new Vector3(6f * w, 0f, 0f), lookW = 0.8f * w, lookAt = t0.Target };
                }
            }, why);
            ScheduleTwitch(tr);
        }

        void ScheduleTwitch(MotionTrack tr)
        {
            Schedule(1.5f, () =>
            {
                if (_player.Find("PictureIt") != tr) return;
                Rig("Antenna Twitch", "picture it");
                ScheduleTwitch(tr);
            });
        }

        void EndPictureIt(string why, float ding = 0.02f)
        {
            if (!_player.Playing("PictureIt")) return;
            _player.Release("PictureIt", _mt);
            Play(new MotionTrack { name = "Ding", length = 0.3f, eval = (tr, t) => new MotionSample { stage = new Vector3(0f, C.Hop(t, ding, 0.08f, 0.2f), 0f) } }, why);
            _snapUntil = Time.time + 0.4f;
        }

        Vector3 UpAndAway()
        {
            Vector3 p = transform.position;
            Vector3 eye = _hasEye ? _ctx.eye : p - transform.forward;
            Vector3 toEye = eye - p;
            return p + Quaternion.AngleAxis(20f, Vector3.up) * (toEye.normalized * 0.5f) + Vector3.up * 0.25f;
        }

        // ------------------------------------------------------------ accents
        void PlayJab(string why)
        {
            if (_dropAct || _hiding || Flying) return;
            Play(new MotionTrack { name = "Jab", length = 0.8f, eval = (tr, t) => { float w = C.Pulse(t, 0.2f, 0.35f, 0.25f); return new MotionSample { stage = new Vector3(0f, 0f, 0.04f * w), rot = new Vector3(6f * w, 0f, 0f) }; } }, why);
        }

        void PlayNod(string why)
        {
            if (_dropAct || _hiding || Flying) return;
            Play(new MotionTrack
            {
                name = "CountOff", length = 0.25f,
                eval = (tr, t) => new MotionSample { rot = new Vector3(8f * (t < 0.1f ? C.OutCubic(t / 0.1f) : 1f - C.InOutSine((t - 0.1f) / 0.15f)), 0f, 0f) }
            }, why);
        }

        void PlayBrace(string why, Vector3 lookUpAt)
        {
            if (_dropAct || Flying) return;
            var tr = Play(new MotionTrack
            {
                name = "Brace", outSeconds = 0.4f, target = lookUpAt,
                eval = (t0, t) =>
                {
                    float w = C.Held(t, 0.25f, t0.Rel, 0.4f);
                    return new MotionSample { stage = new Vector3(0f, -0.03f * w, -0.02f * w), rot = new Vector3(-15f * w, 0f, 0f), lookW = 0.45f * w, lookAt = t0.target };
                }
            }, why);
            _releaseOnLineEnd.Add(tr);
        }

        MotionTrack PlayLook(string name, Transform t0, Vector3 at, float weight, float inS, string why, bool untilLineEnd, float length = float.PositiveInfinity)
        {
            if (_dropAct) return null;
            var tr = Play(new MotionTrack
            {
                name = name, outSeconds = 0.25f, targetT = t0, target = at, length = length,
                eval = (x, t) =>
                {
                    float w = float.IsPositiveInfinity(x.length) ? C.Held(t, inS, x.Rel, 0.25f) : C.Pulse(t, inS, x.length - inS - 0.25f, 0.25f);
                    return new MotionSample { lookW = weight * w, lookAt = x.Target };
                }
            }, why);
            if (untilLineEnd) _releaseOnLineEnd.Add(tr);
            return tr;
        }

        void PlayRecoil(Vector3 from, float dist, string why, float hold = 1.2f)
        {
            if (_dropAct) return;
            Vector3 away = Horizontal(transform.position - from) * dist;
            Play(new MotionTrack
            {
                name = "Recoil", length = 0.3f + hold + 0.5f,
                eval = (tr, t) => { float w = C.Pulse(t, 0.3f, hold, 0.5f); return new MotionSample { world = away * w, rot = new Vector3(-8f * w, 0f, 0f) }; }
            }, why);
        }

        void PlayListen(string why)
        {
            if (_dropAct || _player.Playing("Listen")) return;
            Rig("Antenna Twitch", "listening");
            Play(new MotionTrack
            {
                name = "Listen", outSeconds = 0.3f,
                eval = (tr, t) => { float w = C.HeldSoft(t, 0.3f, tr.Rel, 0.3f); return new MotionSample { stage = new Vector3(0f, 0f, 0.025f * w), rot = new Vector3(8f * w, 0f, 0f) }; }
            }, why);
        }

        void PlayWilt(string why)
        {
            if (_dropAct || _player.Playing("Wilt")) return;
            Play(new MotionTrack
            {
                name = "Wilt", outSeconds = 0.6f, ambient = true,
                eval = (tr, t) =>
                {
                    float w = C.Held(t, 0.6f, tr.Rel, 0.6f);
                    return new MotionSample { stage = new Vector3(0f, -0.03f * w, 0f), rot = new Vector3(0f, 5f * Mathf.Sin(2f * Mathf.PI * 0.2f * t) * w, 0f) };
                }
            }, why);
        }

        void PlayHumAlong(string why)
        {
            if (_dropAct || _player.Playing("HumAlong")) return;
            Play(new MotionTrack
            {
                name = "HumAlong", outSeconds = 0.3f, ambient = true,
                eval = (tr, t) =>
                {
                    float w = C.Held(t, 0.3f, tr.Rel, 0.3f) * Mathf.Clamp01(2f * (_net != null ? _net.HumLevel : 0f));
                    return new MotionSample { stage = new Vector3(0.004f * Mathf.Sin(2f * Mathf.PI * 24f * t) * w, 0f, 0f) };
                }
            }, why);
        }

        /// <summary>The fire: "I can't look" - turned 150 deg away from the window, peeking back 40 deg for 0.4 s every 2 s.</summary>
        void PlayCantLook(string why)
        {
            if (_dropAct || _window == null) return;
            Vector3 wl = transform.InverseTransformPoint(_window.position);
            float bearing = Mathf.Atan2(wl.x, wl.z) * Mathf.Rad2Deg; // where the window is, from his facing
            float away = Mathf.DeltaAngle(0f, bearing + 150f), away2 = Mathf.DeltaAngle(0f, bearing - 150f);
            float turn = Mathf.Abs(away) < Mathf.Abs(away2) ? away : away2;
            float peek = -Mathf.Sign(turn) * 40f;
            var tr = Play(new MotionTrack
            {
                name = "CantLook", outSeconds = 0.4f,
                eval = (t0, t) =>
                {
                    float w = C.Held(t, 0.35f, t0.Rel, 0.4f);
                    float pk = t > 2f ? C.Pulse((t - 2f) % 2f, 0.12f, 0.16f, 0.12f) : 0f;
                    return new MotionSample { rot = new Vector3(20f * w, (turn + peek * pk) * w, 0f), bob = -0.6f * w };
                }
            }, why);
            _cantLook = tr;
        }

        MotionTrack _cantLook;

        void PlayFrantic(string why, float seconds)
        {
            if (_dropAct || _window == null) return;
            _player.Release("CantLook", _mt);
            Vector3 wl = transform.InverseTransformPoint(_window.position);
            float bearing = Mathf.Clamp(Mathf.Atan2(wl.x, wl.z) * Mathf.Rad2Deg, -60f, 60f);
            seconds = Mathf.Clamp(seconds, 1f, 6f);
            Play(new MotionTrack
            {
                name = "Frantic", length = seconds,
                eval = (tr, t) =>
                {
                    float env = C.Pulse(t, 0.15f, seconds - 0.45f, 0.3f);
                    float a = 2f * Mathf.PI * 6f * t;
                    float yawK = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 2f * t); // window <-> player twice a second
                    return new MotionSample { stage = new Vector3(Mathf.Sin(a), Mathf.Cos(a * 1.3f), 0f) * (0.01f * env), rot = new Vector3(0f, bearing * yawK * env, 0f) };
                }
            }, why);
        }

        void PlayDoubleTake(string why)
        {
            // he sits on the perch here (attached): body pitch only. Look down 0.3 s, hold 0.5, snap up 0.12, hold 0.4, back down 0.25
            var tr = Play(new MotionTrack
            {
                name = "DoubleTake", outSeconds = 0.3f,
                eval = (t0, t) =>
                {
                    float down;
                    if (t < 0.3f) down = C.OutCubic(t / 0.3f);
                    else if (t < 0.8f) down = 1f;
                    else if (t < 0.92f) down = 1f - C.OutCubic((t - 0.8f) / 0.12f);
                    else if (t < 1.32f) down = 0f;
                    else down = C.InOutSine((t - 1.32f) / 0.25f);
                    float rel = t0.Rel;
                    if (rel >= 0f && t > rel) down *= 1f - C.InOutSine((t - rel) / 0.3f);
                    return new MotionSample { rot = new Vector3(25f * down, 0f, 0f) };
                }
            }, why);
            _releaseOnLineEnd.Add(tr);
        }

        void PlayBark(Vector3 at, string why)
        {
            if (_dropAct || _hiding) return;
            Vector3 toward = Horizontal(at - transform.position) * 0.02f;
            Play(new MotionTrack
            {
                name = "Bark", length = 0.45f,
                eval = (tr, t) => { float w = C.Pulse(t, 0.06f, 0.09f, 0.3f); return new MotionSample { world = toward * w, rot = new Vector3(10f * w, 0f, 0f) }; }
            }, why);
        }
    }
}
