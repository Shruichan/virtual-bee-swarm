using System.Collections;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;
using VirtualBeeSwarm.Pals;

namespace VirtualBeeSwarm.Throw
{
    /// <summary>
    /// The desktop path for Throwables: the Pals ghost hand (PalHands) acts each move out, so the real code runs (the
    /// carry's grab calls and the throw hook for a throw, the hand reader for a knock). Off the headset only.
    /// Keys: F4 flings the prop the set-up step wants, F11 taps the nearest resting prop over. Env VBS_THROW_DEMO=1
    /// (captures): at the feed station it orders the syrup, throws it, knocks it over once it's back, then pours it
    /// over the feeder by hand (the thrown order still counts); in between it throws it at the hive once. The autopilot leaves this station to the demo; use
    /// VBS_AUTOPILOT_MISTAKES= (empty).
    /// </summary>
    public class ThrowDemo : MonoBehaviour
    {
        Coroutine _act;
        bool _demoDone;

        void Update()
        {
            var tw = Throwables.Instance;
            if (tw == null || !PalHands.OffHeadset) return;
            if (GameInput.KeyDown(KeyCode.F4)) { var c = tw.LiveCarry(); if (c != null) Run(Act(Fling(c))); }
            if (GameInput.KeyDown(KeyCode.F11))
            {
                var t = tw.NearestKnockable(Head(out _, out _, out _));
                if (t != null) Run(Act(Tap(t)));
            }
            if (!_demoDone && Throwables.Demo && FeedStation() is StationBase feed && feed.IsActive)
            {
                _demoDone = true;
                Run(Demo(feed));
            }
        }

        void Run(IEnumerator e)
        {
            if (_act != null) StopCoroutine(_act);
            _act = StartCoroutine(e);
        }

        IEnumerator Act(IEnumerator move)
        {
            yield return move;
            PalHands.Instance?.GhostDown();
            _act = null;
        }

        static StationBase FeedStation()
        {
            foreach (var st in FindObjectsByType<StationBase>(FindObjectsSortMode.None))
                if (st.Station == GameStation.Feed) return st;
            return null;
        }

        IEnumerator Demo(StationBase feed)
        {
            var tw = Throwables.Instance;
            Debug.Log("[VBS][Throw] demo: order the syrup, throw it, knock it over, throw it at the hive, pour it");
            CarryProp c = null;
            float giveUp = Time.time + 90f;
            while (c == null && Time.time < giveUp && feed.IsActive)
            {
                if (HandsOnDirector.Instance != null && feed.Choosing && !feed.Busy) HandsOnDirector.Instance.Order(GameIds.Choice.SugarSyrup);
                c = tw.LiveCarry();
                yield return new WaitForSeconds(0.25f);
            }
            if (c == null) { Debug.Log("[VBS][Throw] demo: no carry prop came"); yield break; }
            // parked and still
            Vector3 last = c.transform.position;
            float still = 0f;
            while (still < 0.4f && c.Live && Time.time < giveUp)
            {
                yield return null;
                bool flying = BeeDelivery.Instance != null && BeeDelivery.Instance.IsFlying(c.transform);
                still = !flying && (c.transform.position - last).sqrMagnitude < 1e-6f ? still + Time.deltaTime : 0f;
                last = c.transform.position;
            }
            yield return new WaitForSeconds(0.5f);
            if (!c.Live) yield break;
            yield return Fling(c);
            float wait = Time.time + 8f;
            while (tw.Busy && Time.time < wait) yield return null;
            yield return new WaitForSeconds(0.7f);
            if (!c.Live) { PalHands.Instance?.GhostDown(); yield break; }
            yield return Tap(c.transform);
            yield return new WaitForSeconds(2.1f);
            var hive = FindAnyObjectByType<HiveBody>();
            if (c.Live && hive != null) // at the hive: it bounces off, never through
            {
                yield return Fling(c, tw.HiveCentre(out var hc) ? hc : hive.transform.position);
                wait = Time.time + 8f;
                while (tw.Busy && Time.time < wait) yield return null;
                yield return new WaitForSeconds(0.5f);
            }
            if (c.Live) yield return NotAThrow(c, ray: true);
            if (c.Live) yield return NotAThrow(c, ray: false);
            if (c.Live) yield return Pour(c, feed);
            PalHands.Instance?.GhostDown();
            Debug.Log("[VBS][Throw] demo done: " + tw.Throws + " throw(s), " + tw.Knocks + " knock(s)");
        }

        // ------------------------------------------------------------------ moves
        static Vector3 Head(out Vector3 fwd, out Vector3 right, out Vector3 up)
        {
            var del = BeeDelivery.Instance;
            Transform h = del != null ? del.Head : (Camera.main != null ? Camera.main.transform : null);
            up = Vector3.up;
            fwd = h != null ? Vector3.ProjectOnPlane(h.forward, up) : Vector3.forward;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            right = Vector3.Cross(up, fwd);
            return h != null ? h.position : new Vector3(0f, 1.6f, -0.7f);
        }

        static void Ghost(Vector3 centre, Vector3 normal, Vector3 fingers, bool pinch = false) =>
            PalHands.Instance?.Ghost(centre, normal, fingers, false, pinch);

        static IEnumerator Move(float seconds, System.Action<float> step)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                step(Mathf.Clamp01(t / seconds));
                yield return null;
            }
            step(1f);
        }

        static float Smooth(float k) => k * k * (3f - 2f * k);

        /// <summary>Reach, pinch, wind up, fling forward (a little left) and up, let go (the throw hook does the rest).</summary>
        IEnumerator Fling(CarryProp c, Vector3? at = null)
        {
            Vector3 eye = Head(out var fwd, out var right, out var up);
            Vector3 rest = eye + fwd * 0.3f + right * 0.16f - up * 0.42f;
            Vector3 item = c.transform.position;
            Vector3 back = -fwd * 0.07f;
            yield return Move(0.45f, k => Ghost(Vector3.Lerp(rest, item + back, Smooth(k)), -up, fwd));
            if (!c.Live) yield break;
            c.BeginGrab(item);
            Vector3 wind = item - fwd * 0.12f + up * 0.05f + right * 0.06f;
            yield return Move(0.35f, k =>
            {
                Vector3 hp = Vector3.Lerp(item, wind, Smooth(k));
                c.UpdateGrab(hp);
                Ghost(hp + back, -up, fwd, true);
            });
            Vector3 dir = (fwd * 0.6f - right * 0.7f + up * 0.45f).normalized;
            if (at.HasValue) // straight at a point (the hive), a little up
            {
                Vector3 to = at.Value - wind;
                to.y = 0f;
                dir = (to.normalized + up * 0.12f).normalized;
            }
            Vector3 from = wind;
            yield return Move(0.17f, k =>
            {
                Vector3 hp = from + dir * (0.55f * k * k * 0.5f + 0.55f * k * 0.5f);
                c.UpdateGrab(hp);
                Ghost(hp + back, -up, dir, true);
            });
            if (c.Held) c.EndGrab();
            Vector3 end = from + dir * 0.55f;
            yield return Move(0.2f, k => Ghost(end + dir * 0.12f * Smooth(k) + back, -up, dir));
            Vector3 after = end + dir * 0.12f;
            yield return Move(0.5f, k => Ghost(Vector3.Lerp(after, rest, Smooth(k)), -up, fwd));
        }

        /// <summary>Two releases that must NOT throw. ray: a fast swing of the grab point 0.5 m away from the hand (a ray
        /// grab's wrist wobble). Else: the hand at the grip, still, with one 15 cm tracking jump just before letting go.</summary>
        IEnumerator NotAThrow(CarryProp c, bool ray)
        {
            var tw = Throwables.Instance;
            Vector3 eye = Head(out var fwd, out var right, out var up);
            Vector3 rest = eye + fwd * 0.3f + right * 0.16f - up * 0.42f;
            Vector3 item = c.transform.position, back = -fwd * 0.07f;
            int before = tw.Throws;
            Vector3 hand = ray ? item - fwd * 0.5f + up * 0.05f : item + back;
            yield return Move(0.45f, k => Ghost(Vector3.Lerp(rest, hand, Smooth(k)), -up, fwd));
            if (!c.Live) yield break;
            c.BeginGrab(item);
            if (ray)
                yield return Move(0.3f, k => { c.UpdateGrab(item - right * 0.6f * k); Ghost(hand, -up, fwd, true); });
            else
            {
                yield return Move(0.3f, k => { c.UpdateGrab(item); Ghost(hand, -up, fwd, true); });
                c.UpdateGrab(item + right * 0.15f); // the jump
                Ghost(hand, -up, fwd, true);
                yield return null;
                c.UpdateGrab(item + right * 0.15f);
                yield return null;
            }
            if (c.Held) c.EndGrab();
            Debug.Log("[VBS][Throw] demo: " + (ray ? "ray-style release (hand 0.5 m from the grab point, 2 m/s swing)" : "release after a 15 cm tracking jump") +
                      ": " + (tw.Throws == before ? "no throw, it drifts home (right)" : "THREW (wrong)"));
            yield return Move(0.5f, k => Ghost(Vector3.Lerp(hand, rest, Smooth(k)), -up, fwd));
            yield return new WaitForSeconds(0.8f);
        }

        /// <summary>A quick open-hand swipe through the prop from its right (it falls to the left).</summary>
        IEnumerator Tap(Transform t)
        {
            Vector3 eye = Head(out var fwd, out var right, out var up);
            Vector3 rest = eye + fwd * 0.3f + right * 0.16f - up * 0.42f;
            Vector3 p = Throwables.PropBounds(t, out var pb) ? pb.center : t.position + up * 0.05f; // through the middle of the prop
            Vector3 a = p + right * 0.28f, b = p - right * 0.14f;
            yield return Move(0.5f, k => Ghost(Vector3.Lerp(rest, a, Smooth(k)), -right, fwd));
            yield return Move(0.12f, k => Ghost(Vector3.Lerp(a, b, k), -right, fwd)); // a slap, ~3.5 m/s
            yield return Move(0.6f, k => Ghost(Vector3.Lerp(b, rest, Smooth(k)), -right, fwd));
        }

        /// <summary>The ordinary set-up after all: pinch it, hold it over the roof feeder until it's poured.</summary>
        IEnumerator Pour(CarryProp c, StationBase feed)
        {
            var sd = feed.GetComponent<StationDelivery>();
            if (sd == null || sd.Spot == null) yield break;
            Vector3 eye = Head(out var fwd, out var right, out var up);
            Vector3 rest = eye + fwd * 0.3f + right * 0.16f - up * 0.42f;
            Vector3 item = c.transform.position, back = -fwd * 0.07f;
            yield return Move(0.45f, k => Ghost(Vector3.Lerp(rest, item + back, Smooth(k)), -up, fwd));
            if (!c.Live) yield break;
            c.BeginGrab(item);
            Vector3 over = sd.Spot.position + up * 0.12f;
            yield return Move(0.7f, k =>
            {
                Vector3 hp = Vector3.Lerp(item, over, Smooth(k));
                c.UpdateGrab(hp);
                Ghost(hp + back, -up, fwd, true);
            });
            float until = Time.time + 3.5f;
            while (c.Live && c.Held && Time.time < until)
            {
                c.UpdateGrab(over);
                Ghost(over + back, -up, fwd, true);
                yield return null;
            }
            if (c.Held) c.EndGrab();
            yield return Move(0.5f, k => Ghost(Vector3.Lerp(over + back, rest, Smooth(k)), -up, fwd));
        }
    }
}
