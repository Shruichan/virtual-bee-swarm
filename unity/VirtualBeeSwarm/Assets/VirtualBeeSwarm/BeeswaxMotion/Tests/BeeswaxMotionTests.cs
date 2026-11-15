using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace VirtualBeeSwarm.Tests
{
    /// <summary>BeeswaxMotion's pure parts: the comfort governor, the curves, fairness, and the T-0 perch timing.</summary>
    public class BeeswaxMotionTests
    {
        // the typical layout, table-local = world here: eye 0.85 m over the table, 0.37 m in front of the anchor
        static readonly Vector3 Eye = new Vector3(0f, 0.85f, -0.37f);
        static readonly Vector3 Hive = new Vector3(0f, 0f, 0.15f);
        static readonly Vector3 Podium = new Vector3(0.38f, 0.40f, -0.04f);

        static MotionGovernor.Ctx Ctx(Vector3 gazeAt) => MotionGovernor.Ctx.Make(Eye, gazeAt - Eye, Hive);

        // ------------------------------------------------------------ G1 / G2
        [Test]
        public void Distance_PushesOutToSoftAndHard()
        {
            var c = Ctx(Hive);
            Vector3 near = Eye + new Vector3(0.05f, -0.2f, 0.2f);
            Vector3 soft = MotionGovernor.Keep(near, c, MotionGovernor.SoftDistance, out var r1);
            Assert.GreaterOrEqual(Vector3.Distance(soft, Eye), MotionGovernor.SoftDistance - 1e-4f);
            Assert.IsNotNull(r1);
            Vector3 hard = MotionGovernor.Keep(near, c, MotionGovernor.HardDistance, out _);
            Assert.GreaterOrEqual(Vector3.Distance(hard, Eye), MotionGovernor.HardDistance - 1e-4f);
            Assert.AreEqual(Podium, MotionGovernor.Keep(Podium, c, MotionGovernor.SoftDistance, out var r3), "the podium is already fine");
            Assert.IsNull(r3);
        }

        [Test]
        public void NeverBehindOrAbove()
        {
            var c = Ctx(Hive);
            Vector3 behind = Eye + new Vector3(0.6f, -0.2f, -0.1f);
            Vector3 k = MotionGovernor.Keep(behind, c, MotionGovernor.SoftDistance, out _);
            Assert.GreaterOrEqual(Vector3.Dot(k - Eye, c.forward), MotionGovernor.MinForward - 1e-4f);
            Vector3 above = Eye + new Vector3(0f, 0.4f, 0.7f);
            k = MotionGovernor.Keep(above, c, MotionGovernor.SoftDistance, out _);
            Assert.LessOrEqual(k.y, Eye.y + MotionGovernor.MaxAboveEye + 1e-4f);
            c.attached = true;
            Assert.AreEqual(behind, MotionGovernor.Keep(behind, c, MotionGovernor.SoftDistance, out _), "off while attached (the rocket)");
        }

        [Test]
        public void HeadMoves_OnlyDistanceSnaps_AndTheMarkIsTheFloor()
        {
            // leaning over the hive: the table's forward keeps its last value instead of spinning with the head
            var before = Ctx(Hive);
            Vector3 over = new Vector3(0.05f, 0.9f, 0.2f);
            var c = MotionGovernor.Ctx.Make(over, Vector3.down, Hive, before.forward);
            Assert.Less(Vector3.Angle(before.forward, c.forward), 0.01f);
            // a crouching player: the brow rule never pushes him below his own mark
            var low = MotionGovernor.Ctx.Make(new Vector3(0f, 0.15f, -0.5f), Vector3.forward, Hive);
            Vector3 k = MotionGovernor.Keep(Podium, low, MotionGovernor.SoftDistance, out _, Podium.y);
            Assert.GreaterOrEqual(k.y, Podium.y - 1e-4f);
            // the hard distance holds for his surface, and KeepDistance moves him straight out along the eye ray
            Vector3 near = Eye + new Vector3(0.1f, -0.2f, 0.3f);
            Vector3 out1 = MotionGovernor.KeepDistance(near, before, MotionGovernor.HardCentre);
            Assert.GreaterOrEqual(Vector3.Distance(out1, Eye) - MotionGovernor.BodyRadius, MotionGovernor.HardDistance - 1e-4f);
            Assert.Less(Vector3.Angle(near - Eye, out1 - Eye), 0.01f);
        }

        [Test]
        public void ClearView_AChoiceMustNotHideHimEither()
        {
            var c = Ctx(Hive);
            var choice = new MotionGovernor.Disc(new Vector3(0.17f, 0.39f, 0.05f), 0.12f, "choice", true);
            var discs = new List<MotionGovernor.Disc> { choice };
            Vector3 behind = Eye + (choice.center - Eye) * 1.5f;
            Assert.IsTrue(MotionGovernor.Covers(behind, c, discs, out _), "he is hidden behind the choice");
            Vector3 moved = MotionGovernor.ClearView(behind, c, discs, out _);
            Assert.IsFalse(MotionGovernor.Covers(moved, c, discs, out _));
        }

        [Test]
        public void Take_AnticipationIsTheSameShapeAndShort()
        {
            Assert.AreEqual(1f, MotionCurves.TakeCrouch(MotionCurves.TakeIn + 0.05f), 1e-5f);
            Assert.AreEqual(0f, MotionCurves.TakeCrouch(MotionCurves.TakeIn + MotionCurves.TakeHold + MotionCurves.TakeOut + 0.01f), 1e-5f);
            Assert.LessOrEqual(MotionCurves.TakeHitstop + MotionCurves.TakeIn + MotionCurves.TakeHold, 0.45f, "the verdict is never more than 0.45 s after the poke");
        }

        // ------------------------------------------------------------ G3
        [Test]
        public void Speed_CapsPeripheryAndApproach()
        {
            var c = Ctx(new Vector3(-0.4f, 0.3f, 0.2f)); // looking left: the podium is far in the periphery
            Assert.AreEqual(MotionGovernor.PeripherySpeed, MotionGovernor.MaxSpeedAlong(Podium, Vector3.right, c, false), 1e-4f);
            var g = Ctx(Podium); // looking at him
            Assert.AreEqual(MotionGovernor.MaxSpeed, MotionGovernor.MaxSpeedAlong(Podium, Vector3.right, g, false), 0.05f);
            Vector3 toEye = (Eye - Podium).normalized;
            float cap = MotionGovernor.MaxSpeedAlong(Podium, toEye, g, false);
            Assert.LessOrEqual(cap, MotionGovernor.MaxApproach + 1e-4f, "nothing looms: 0.25 m/s toward the eye");
            Assert.LessOrEqual(MotionGovernor.MaxSpeedAlong(Podium, toEye, g, true), MotionGovernor.MaxApproach + 1e-4f, "fast moves never approach");
            Assert.Greater(MotionGovernor.MaxSpeedAlong(Podium, -toEye, g, true), MotionGovernor.MaxSpeed, "a short fast move away within gaze");
        }

        [Test]
        public void LimitStep_ApproachRate()
        {
            var c = Ctx(Podium);
            float dt = 1f / 72f;
            Vector3 to = Podium + (Eye - Podium).normalized * 0.05f; // a 5 cm lunge in one frame
            Vector3 got = MotionGovernor.LimitStep(Podium, to, dt, c, MotionGovernor.MaxSpeed);
            float inward = Vector3.Dot(got - Podium, (Eye - Podium).normalized) / dt;
            Assert.LessOrEqual(inward, MotionGovernor.MaxApproach + 1e-3f);
        }

        // ------------------------------------------------------------ G4 / G5
        [Test]
        public void ClearView_SlidesOffAProtectedThing()
        {
            var c = Ctx(Hive);
            var choice = new MotionGovernor.Disc(new Vector3(0.17f, 0.39f, 0.05f), 0.12f, "choice");
            var discs = new List<MotionGovernor.Disc> { choice };
            Vector3 inFront = Eye + (choice.center - Eye) * 0.75f; // between the eye and the prop
            Assert.IsTrue(MotionGovernor.Covers(inFront, c, discs, out _));
            Vector3 moved = MotionGovernor.ClearView(inFront, c, discs, out _);
            Assert.IsFalse(MotionGovernor.Covers(moved, c, discs, out _));
            Assert.AreEqual(Vector3.Distance(inFront, Eye), Vector3.Distance(moved, Eye), 1e-3f, "same distance from the eye");
            Vector3 behind = Eye + (choice.center - Eye) * 1.8f;
            Assert.IsFalse(MotionGovernor.Covers(behind, c, discs, out _), "hidden behind it is fine");
        }

        [Test]
        public void HeldHive_GoesToThePodium()
        {
            Vector3 roof = Hive + Vector3.up * 0.36f;
            Assert.AreEqual(Podium, MotionGovernor.HeldHive(roof, Hive + Vector3.up * 0.15f, Podium, true));
            Assert.AreEqual(roof, MotionGovernor.HeldHive(roof, Hive + Vector3.up * 0.15f, Podium, false));
        }

        // ------------------------------------------------------------ curves
        [Test]
        public void Curves_EndpointsAndTiming()
        {
            Vector3 a = new Vector3(0.12f, 0.42f, 0.08f), b = Podium;
            Vector3 c = MotionCurves.ArcControl(a, b, MotionCurves.ArcHeight(Vector3.Distance(a, b)));
            Assert.Less(Vector3.Distance(MotionCurves.Bezier(a, c, b, 0f), a), 1e-5f);
            Assert.Less(Vector3.Distance(MotionCurves.Bezier(a, c, b, 1f), b), 1e-5f);
            Assert.AreEqual(1.0f, MotionCurves.FlightSeconds(0.45f), 0.01f);
            Assert.AreEqual(0.5f, MotionCurves.FlightSeconds(0.01f), 1e-4f);
            Assert.AreEqual(1.6f, MotionCurves.FlightSeconds(3f), 1e-4f);
            var pts = new[] { a, new Vector3(0f, 0.56f, 0.45f), b };
            Assert.Less(Vector3.Distance(MotionCurves.CatmullRom(pts, 0f), a), 1e-5f);
            Assert.Less(Vector3.Distance(MotionCurves.CatmullRom(pts, 1f), b), 1e-5f);
            foreach (var u in new[] { 0f, 0.5f, 1f }) Assert.AreEqual(u, MotionCurves.InOutSine(u), 1e-5f);
        }

        [Test]
        public void Settle_StartsAtRestAndDiesBy1_8s()
        {
            Assert.AreEqual(0f, MotionCurves.SettleY(0f), 1e-6f, "no pop at the hand-off");
            Assert.AreEqual(0f, MotionCurves.SwayRoll(0f), 1e-6f);
            for (float t = 1.8f; t < 3f; t += 0.05f) Assert.Less(Mathf.Abs(MotionCurves.SwayRoll(t)), 0.5f);
            float minY = 0f;
            for (float t = 0f; t < 1f; t += 0.005f) minY = Mathf.Min(minY, MotionCurves.SettleY(t));
            Assert.AreEqual(-0.011f, minY, 0.003f, "about a 1.1 cm dip");
        }

        [Test]
        public void Metronome_AlternatesAndEscalates()
        {
            Assert.Greater(MotionCurves.TickRoll(0.3f), 5f);
            Assert.Less(MotionCurves.TickRoll(0.8f), -5f);
            MotionCurves.TickPhase(10f, out float p2, out float a2);
            MotionCurves.TickPhase(15f, out float p3, out float a3);
            Assert.AreEqual(0.375f, p2, 1e-5f);
            Assert.AreEqual(0.25f, p3, 1e-5f);
            Assert.AreEqual(8f, a3, 1e-5f);
            // the switches are short and crisp, and nothing jumps more than a tick's swing in one 72 Hz frame
            float prev = MotionCurves.TickRoll(0f);
            for (float t = 0f; t < 20f; t += 1f / 72f)
            {
                float r = MotionCurves.TickRoll(t);
                Assert.Less(Mathf.Abs(r - prev), 16.1f);
                prev = r;
            }
            Assert.AreEqual(0f, MotionCurves.WellLean(2.9f), 1e-5f);
            Assert.AreEqual(1f, MotionCurves.WellLean(3.5f), 1e-5f);
            Assert.AreEqual(0f, MotionCurves.TapHop(2.4f), 1e-6f, "the wait starts still");
            float hop = 0f;
            for (float t = 2.5f; t < 3.7f; t += 0.005f) hop = Mathf.Max(hop, MotionCurves.TapHop(t));
            Assert.AreEqual(0.02f, hop, 0.002f, "a tap you can see within the first 4 s");
        }

        [Test]
        public void Primitives_StayUnderTheSpeedCap()
        {
            float dt = 1f / 240f;
            float Peak(System.Func<float, float> f, float len)
            {
                float m = 0f;
                for (float t = 0f; t < len; t += dt) m = Mathf.Max(m, Mathf.Abs(f(t + dt) - f(t)) / dt);
                return m;
            }
            Assert.Less(Peak(MotionCurves.CarriedY, 1f), MotionGovernor.MaxSpeed, "carried hops");
            Assert.Less(Peak(MotionCurves.BuzzX, 0.4f), MotionGovernor.MaxSpeed, "grievance buzz");
            Assert.Less(Peak(MotionCurves.GavelY, 1.2f), MotionGovernor.MaxSpeed, "gavel slam");
            Assert.Less(Peak(t => 0.05f * MotionCurves.HeldSoft(t, 0.32f, -1f, 0.4f), 0.5f), MotionGovernor.MaxApproach + 0.01f, "take the floor: step toward the eye");
        }

        // ------------------------------------------------------------ fairness
        [Test]
        public void FinalAnswer_IsFair()
        {
            // the same finger over the same props: the response is the same whichever prop is the right one
            var props = new List<Vector3> { new Vector3(-0.23f, 0.39f, 0.05f), new Vector3(-0.03f, 0.39f, 0.05f), new Vector3(0.17f, 0.39f, 0.05f) };
            var tips = new[] { new Vector3(-0.2f, 0.42f, 0.02f), new Vector3(0.0f, 0.4f, 0.1f), new Vector3(0.15f, 0.45f, 0.05f), new Vector3(0.6f, 0.4f, 0f) };
            foreach (var station in new[] { GameStation.Smoker, GameStation.Heat })
            {
                var choices = GameIds.Choices[(int)station];
                foreach (var tip in tips)
                {
                    int a = MotionCurves.HoverTarget(tip, props, 0.12f);
                    // relabel: rotate which choice sits in which slot (so the right answer moves)
                    for (int shift = 1; shift < choices.Length; shift++)
                    {
                        var relabelled = new List<Vector3>();
                        for (int i = 0; i < props.Count; i++) relabelled.Add(props[(i + shift) % props.Count]);
                        int b = MotionCurves.HoverTarget(tip, relabelled, 0.12f);
                        Vector3 pa = a >= 0 ? props[a] : Vector3.zero, pb = b >= 0 ? relabelled[b] : Vector3.zero;
                        Assert.AreEqual(pa, pb, "the hovered prop (and so the lean / look) depends on geometry only");
                    }
                }
            }
        }

        // ------------------------------------------------------------ T-0 perch timing
        static float SimulatePerch(Vector3 from, Vector3 to, Vector3 gazeAt)
        {
            var c = Ctx(gazeAt);
            float dt = 1f / 72f, t = 0.32f; // the adjourn anticipation (dip + lift)
            Vector3 p0 = from + Vector3.up * 0.06f;
            Vector3 e = to + Vector3.up * 0.03f;
            float d = Vector3.Distance(from, to);
            float T = MotionCurves.FlightSeconds(d), tau = 0f;
            Vector3 cur = p0, ctrl = MotionCurves.ArcControl(p0, e, MotionCurves.ArcHeight(d));
            while (tau < T && t < 20f)
            {
                Vector3 b = MotionCurves.Bezier(p0, ctrl, e, MotionCurves.InOutSine((tau + dt) / T));
                float m = Vector3.Distance(b, cur);
                tau = m > 1e-6f ? MotionCurves.WarpedTau(tau, dt, m, Mathf.Min(MotionGovernor.FastSpeed, MotionGovernor.MaxSpeedAlong(cur, (b - cur) / m, c, false))) : tau + dt;
                cur = MotionCurves.Bezier(p0, ctrl, e, MotionCurves.InOutSine(Mathf.Min(1f, tau / T)));
                t += dt;
            }
            return t + 0.03f / 0.1f; // the slow last 3 cm
        }

        [Test]
        public void T0_PerchArrivesWithin4s()
        {
            Vector3 perch = new Vector3(0.31f, 0.36f, 0.20f);
            Assert.Less(SimulatePerch(Podium, perch, Hive), 4f, "the real layout, looking at the hive");
            Assert.Less(SimulatePerch(Podium, perch, new Vector3(-0.5f, 0.3f, 0.2f)), 4f, "the real layout, looking away (periphery cap)");
            Vector3 far = Podium + (new Vector3(-0.8f, 0.05f, 0.5f)).normalized * 1.2f;
            far.y = Mathf.Min(far.y, 0.6f);
            Assert.Less(SimulatePerch(far, perch, (far + perch) * 0.5f), 4f, "1.2 m in view");
        }

        [Test]
        public void WordTime_FromTheCaption()
        {
            string cap = "Four problems. Four minutes. Or you're on that rocket.";
            Assert.AreEqual(0f, MotionCurves.WordTime(cap, "Four problems", 3f), 1e-5f);
            Assert.AreEqual(3f * cap.IndexOf("rocket") / cap.Length, MotionCurves.WordTime(cap, "rocket", 3f), 1e-5f);
            Assert.AreEqual(cap.IndexOf("rocket") / 14.4f, MotionCurves.WordTime(cap, "rocket", 0f), 1e-5f);
            Assert.AreEqual(-1f, MotionCurves.WordTime(cap, "banana", 3f));
        }
    }
}
