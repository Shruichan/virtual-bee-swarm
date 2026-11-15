using System.Globalization;
using UnityEngine;
using VirtualBeeSwarm.Bloom;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm.RoomAware
{
    /// <summary>
    /// Keeps the ORDER UP board (with its stake and prompt) and the honey lever where the player can use them without
    /// them standing in the way (no file in HandsOn/ changes: it moves their transforms sideways along the row):
    ///   never between the eyes and the hive (a line from the eyes to the hive body and its roof), never closer to the
    ///   eyes than room.headGap (0.4 m), never in real furniture (RoomKeepOut, as an oriented box in its row's frame),
    ///   never into the hive, rocket or each other, within reach (0.95 m flat from the eyes) and within 35 deg of the
    ///   eye-to-hive direction (in view while the player looks at the hive). The height is never changed (dartiros'
    ///   OrderBoard.KeepOutOfPlayerSpace clamps it under the eyes on the headset).
    /// The board is fitted from its built spot while it is hidden (twice a second). The frame it appears it is fitted
    /// again from where OrderBoard just put it (keeping that adjustment) only if that spot breaks a rule; it never moves
    /// while it is up. The lever is fitted only while it is neither held nor live. Nothing happens without a head (camera).
    /// Logs "[VBS][Room] ORDER UP board ..." / "... lever ..." when either moves.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class ControlKeeper : MonoBehaviour
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const float Reach = 0.95f, ViewCone = 35f;

        OrderBoard _board;
        HoneyLever _lever;
        HiveBody _hive;
        Vector3 _boardHome, _leverHome; // local, as HandsOn built them
        bool _boardWasVisible;
        float _next, _nextFind;
        float _boardShift = float.NaN, _leverShift = float.NaN;
        public string BoardState { get; private set; } = "not found";
        public string LeverState { get; private set; } = "not found";

        void LateUpdate()
        {
            if (!RoomAwareTuning.On) return;
            if (_board == null || _lever == null)
            {
                if (Time.time < _nextFind) return;
                _nextFind = Time.time + 1f;
                if (_board == null)
                {
                    _board = FindAnyObjectByType<OrderBoard>(FindObjectsInactive.Include);
                    if (_board != null) _boardHome = _board.transform.localPosition;
                }
                if (_lever == null)
                {
                    _lever = FindAnyObjectByType<HoneyLever>(FindObjectsInactive.Include);
                    if (_lever != null) _leverHome = _lever.transform.localPosition;
                }
                if (_board == null || _lever == null) return;
            }
            bool vis = _board.Visible;
            // on show, OrderBoard may have pushed itself out of the player's space (Android): keep that unless it now breaks a rule
            if (vis && !_boardWasVisible && !Acceptable(_board.transform, true)) Fit(_board.transform, _board.transform.localPosition, true, "shown");
            _boardWasVisible = vis;
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;
            // only when the spot it has now breaks a rule (no flip-flopping between the two)
            if (!vis && !Acceptable(_board.transform, true)) Fit(_board.transform, _boardHome, true, null);
            if (!_lever.Held && !_lever.Live && !Acceptable(_lever.transform, false)) Fit(_lever.transform, _leverHome, false, null);
        }

        static readonly float[] Steps = { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f };
        static readonly float[] Depths = { 0f, 0.1f, -0.1f, 0.2f };

        void Fit(Transform t, Vector3 from, bool isBoard, string why)
        {
            var cam = Camera.main;
            var anchor = t.parent;
            if (cam == null || anchor == null) return;
            if (_hive == null) _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            if (!FullObb(t, out var b0)) return;
            Vector3 fromWorld = anchor.TransformPoint(from);
            b0 = b0.Moved(fromWorld - t.position);
            Vector3 eye = cam.transform.position;
            Vector3 right = anchor.right; right.y = 0f;
            Vector3 fwd = anchor.forward; fwd.y = 0f;
            if (right.sqrMagnitude < 1e-6f || fwd.sqrMagnitude < 1e-6f) return;
            right.Normalize();
            fwd.Normalize();
            Bounds hiveB = default;
            bool haveHive = _hive != null && RoomKeepOut.MeshBounds(_hive.transform, out hiveB, true);
            Vector3 aimA = haveHive ? hiveB.center : anchor.position + Vector3.up * 0.6f;
            Vector3 aimB = haveHive ? new Vector3(hiveB.center.x, hiveB.max.y - 0.03f, hiveB.center.z) : aimA;
            Vector3 view = aimA - eye; view.y = 0f;
            Bounds other = default;
            bool haveOther = false;
            var otherT = isBoard ? (_lever != null ? _lever.transform : null) : (_board != null ? _board.transform : null);
            if (otherT != null && FullObb(otherT, out var oo)) { other = oo.Aabb; haveOther = true; }
            var rocket = MeadowLayout.Anchor != null ? MeadowLayout.Anchor.Find("HONEYMOON-1") : null;
            Bounds rocketB = default;
            bool haveRocket = rocket != null && rocket.gameObject.activeInHierarchy && RoomKeepOut.MeshBounds(rocket, out rocketB, true);
            // the board goes to the side away from the lever, the lever keeps its own side
            float leverSide = Vector3.Dot(anchor.TransformPoint(_leverHome) - anchor.position, right);
            float prefer = isBoard ? (leverSide > 0f ? -1f : 1f) : (Vector3.Dot(fromWorld - anchor.position, right) < 0f ? -1f : 1f);

            float bestCost = float.MaxValue;
            Vector3 bestMove = Vector3.zero;
            string bestNote = "";
            // pass 1: wider view cone (55 deg); pass 2: no view cone, reach 1.2 m; pass 3: the sightline relaxed (scored)
            for (int pass = 0; pass < 4 && bestCost == float.MaxValue; pass++)
            {
                foreach (float st in Steps)
                for (int sgn = 1; sgn >= -1; sgn -= 2)
                {
                    if (st == 0f && sgn < 0) continue;
                    float dx = st * sgn * prefer;
                    foreach (float dz in Depths)
                    {
                        Vector3 move = right * dx + fwd * dz;
                        var b = b0.Moved(move);
                        if (!RoomKeepOut.Free(b, 0.03f)) continue;
                        if (Vector3.Distance(eye, b.ClosestPoint(eye)) < RoomAwareTuning.HeadGap) continue;
                        Vector3 flat = b.c - eye; flat.y = 0f;
                        if (flat.magnitude > (pass < 2 ? Reach : 1.2f)) continue;
                        if (pass < 2 && view.sqrMagnitude > 1e-4f && Vector3.Angle(flat, view) > (pass == 0 ? ViewCone : 55f)) continue;
                        var bx = b.Grown(0.04f);
                        var ab = bx.Aabb;
                        if (haveHive && ab.Intersects(hiveB)) continue;
                        if (haveRocket && ab.Intersects(rocketB)) continue;
                        if (haveOther && ab.Intersects(other)) continue;
                        bool blocks = bx.Segment(eye, aimA) || bx.Segment(eye, aimB);
                        if (blocks && pass < 3) continue;
                        float cost = Mathf.Abs(dx) + Mathf.Abs(dz) * 1.5f + (blocks ? 5f : 0f) + (flat.magnitude > Reach ? 2f : 0f);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestMove = move;
                            bestNote = blocks ? " (still crosses the eye-to-hive line: no free spot)" : pass == 1 ? " (wide of the view)" : pass == 2 ? " (out of the view cone or a stretch away: no closer free spot)" : "";
                        }
                    }
                }
            }
            if (bestCost == float.MaxValue) { Note(isBoard, "no free spot near its row: left where it is"); return; }
            Vector3 target = fromWorld + bestMove;
            float shift = Vector3.Dot(bestMove, right);
            if ((t.position - target).sqrMagnitude > 1e-6f) t.position = target;
            ref float last = ref (isBoard ? ref _boardShift : ref _leverShift);
            if (float.IsNaN(last) || Mathf.Abs(last - shift) > 0.05f || why != null)
            {
                last = shift;
                Note(isBoard, (Mathf.Abs(shift) < 0.01f && bestMove.sqrMagnitude < 1e-4f ? "at its spot" :
                    "moved " + Mathf.Abs(shift).ToString("0.00", C) + " m " + (shift < 0f ? "left" : "right") + (Mathf.Abs(Vector3.Dot(bestMove, fwd)) > 0.01f ? ", " + Vector3.Dot(bestMove, fwd).ToString("+0.00;-0.00", C) + " m back" : "")) +
                    ": clear of the eye-to-hive line, " + RoomAwareTuning.HeadGap.ToString("0.00", C) + " m+ from the eyes, out of furniture" + bestNote + (why != null ? " [" + why + "]" : ""));
            }
        }

        /// <summary>The control where it stands now: clear of furniture, of the eye-to-hive line and of the eyes, in view?</summary>
        bool Acceptable(Transform t, bool isBoard)
        {
            var cam = Camera.main;
            if (cam == null || !FullObb(t, out var b)) return true;
            Vector3 eye = cam.transform.position;
            if (!RoomKeepOut.Free(b, 0.03f) || Vector3.Distance(eye, b.ClosestPoint(eye)) < RoomAwareTuning.HeadGap) return false;
            var bx = b.Grown(0.04f);
            var otherT = isBoard ? (_lever != null ? _lever.transform : null) : (_board != null ? _board.transform : null);
            if (otherT != null && FullObb(otherT, out var ob) && bx.Aabb.Intersects(ob.Aabb)) return false;
            if (_hive == null) _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            if (_hive == null || !RoomKeepOut.MeshBounds(_hive.transform, out var hb, true)) return true;
            Vector3 flat = b.c - eye, view = hb.center - eye;
            flat.y = 0f; view.y = 0f;
            if (view.sqrMagnitude > 1e-4f && Vector3.Angle(flat, view) > ViewCone) return false;
            return !bx.Segment(eye, hb.center) && !bx.Segment(eye, new Vector3(hb.center.x, hb.max.y - 0.03f, hb.center.z)) && !bx.Aabb.Intersects(hb);
        }

        /// <summary>The control's meshes at full size as a box in its row's frame (the lever's visual sits at 0.1% scale
        /// while it is hidden).</summary>
        bool FullObb(Transform t, out Obb o)
        {
            Transform v = _lever != null && t == _lever.transform ? _lever.VisualRoot : null;
            // in the rig anchor's frame (yaw only): the row the board hangs on is tilted toward the player
            Transform frame = MeadowLayout.Anchor != null && t.IsChildOf(MeadowLayout.Anchor) ? MeadowLayout.Anchor : null;
            if (v == null) return RoomKeepOut.MeshObb(t, frame, out o);
            Vector3 s = v.localScale;
            v.localScale = Vector3.one;
            bool ok = RoomKeepOut.MeshObb(t, frame, out o);
            v.localScale = s;
            return ok;
        }

        void Note(bool isBoard, string what)
        {
            if (isBoard) { if (BoardState == what) return; BoardState = what; }
            else { if (LeverState == what) return; LeverState = what; }
            Debug.Log("[VBS][Room] " + (isBoard ? "ORDER UP board " : "lever ") + what);
        }
    }
}
