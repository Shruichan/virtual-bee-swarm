using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    // The story moments of dartiros' new props (see NewProps). Positions are TableAnchor coordinates: x to the player's
    // right, y up from the table top, z away from the player; the hive stands at (0, 0, 0.15). His export faces -Z
    // (Blender -Y front through export_dart_props.py), so yaw 0 shows a prop's front to the player.

    /// <summary>The union badge on Beeswax's chest, on his rig's Body bone so it rides every gesture; Settle on union lines.</summary>
    public class BadgeAct : NewPropAct
    {
        public float widthMeters = 0.026f;          // the badge is 3 cm wide in Blender
        public float lower = 36f;                   // deg below his eyes: on his chest, clear of the smile
        public float faceDown = 22f;                // the badge looks this much below the eye line
        public float sink = 0.04f;                  // x the radius, into the fuzz

        static readonly string[] Lines = { GameLines.RoomMeet1.id, GameLines.RoomEscape.id, GameLines.RoomEndContract.id };

        GameObject _badge;
        Animation _anim;

        public override string Setup()
        {
            var bw = Hub.Beeswax;
            if (Kit.unionBadge == null || bw == null) return null;
            var root = bw.transform;
            var rig = NewProps.FindDeep(root, "Beeswax Rig");
            var bone = rig != null ? NewProps.FindDeep(rig, "Body") : null;
            var holder = bone != null ? bone : (NewProps.FindDeep(root, "Body") ?? root);
            var art = rig != null && rig.parent != null ? rig.parent : holder;
            var b = NewProps.LocalBounds(root, art);
            Vector3 c = root.InverseTransformPoint(holder.position);
            // his face: between the eyes (Daniel's rig: Eyelid.L/R; the older art: EyeL/R), in his root's space
            var eyeL = NewProps.FindDeep(root, "Eyelid.L") ?? NewProps.FindDeep(root, "Temporary Eye.L") ?? NewProps.FindDeep(root, "EyeL");
            var eyeR = NewProps.FindDeep(root, "Eyelid.R") ?? NewProps.FindDeep(root, "Temporary Eye.R") ?? NewProps.FindDeep(root, "EyeR");
            Vector3 face = eyeL != null && eyeR != null ? root.InverseTransformPoint((eyeL.position + eyeR.position) * 0.5f) : c + Vector3.forward * 0.05f;
            Vector3 d = face - c;
            d.y = Mathf.Max(0f, d.y) * 0.3f;            // the eyes sit high: aim the badge at the front, not the forehead
            if (d.sqrMagnitude < 1e-8f) d = Vector3.forward;
            d.Normalize();
            // his round front: the mesh's extent from the Body bone along the face axis (box corners overshoot a ball)
            float r = Mathf.Abs(d.z) >= Mathf.Abs(d.x) ? (d.z > 0f ? b.max.z - c.z : c.z - b.min.z) : (d.x > 0f ? b.max.x - c.x : c.x - b.min.x);
            if (r <= 0.001f) r = 0.05f;
            var axis = Vector3.Cross(Vector3.up, d).normalized;
            Vector3 pos = c + Quaternion.AngleAxis(lower, axis) * d * (r * (1f - sink));
            Vector3 n = Quaternion.AngleAxis(faceDown, axis) * d;

            _badge = NewProps.Spawn(Kit.unionBadge, holder, Vector3.zero, 0f, 1f, "NP_UnionBadge");
            var t = _badge.transform;
            // the badge's printed face (-Z) along n
            t.SetPositionAndRotation(root.TransformPoint(pos), root.rotation * Quaternion.LookRotation(-n, Vector3.up));
            float s = widthMeters / 0.03f / Mathf.Max(1e-4f, holder.lossyScale.x);
            t.localScale = Vector3.one * s;
            Debug.Log("[VBS][NewProps] badge: bone " + c.ToString("F3") + " face " + face.ToString("F3") + " mesh " + b.min.ToString("F3") + ".." +
                      b.max.ToString("F3") + " r " + r.ToString("0.000") + " at " + pos.ToString("F3") + " (Beeswax root space, scale " + root.lossyScale.x.ToString("0.00") + ")");
            _anim = PropClip.Prepare(_badge);
            PropClip.Pose(_anim, "Settle", 0f);
            return "badge on " + (bone != null ? "Beeswax Rig/../Body bone" : holder.name) + " (" + (widthMeters * 100f).ToString("0.0") + " cm)";
        }

        public override void OnLine(string id, string caption)
        {
            if (System.Array.IndexOf(Lines, id) < 0) return;
            PropClip.Play(_anim, "Settle", 1.2f, WrapMode.Once, "UnionBadge", id);
        }

        public override void OnReset() => PropClip.Pose(_anim, "Settle", 0f);
    }

    /// <summary>
    /// The beekeeper who quit left a bench by the hive: frame rest with a comb, queen cage, honey sample tray, hive tool.
    /// The hive lift (feed) levers the tool and lifts the comb, the escape opens the cage and shows off the comb,
    /// "queen" in a line pulls the cage cork, "honey" pops the samples (wax, open comb, capped honey).
    /// </summary>
    public class BenchAct : NewPropAct
    {
        public Vector3 framePos = new Vector3(-0.47f, 0f, 0.13f);   // clear of the meadow island's rim (x -0.37)
        public float frameYaw = -20f, frameScale = 0.6f;
        // the kit: tray, cage and tool together on a cream mat, left of the island's near rim (positions on the mat)
        public Vector3 kitPos = new Vector3(-0.385f, 0f, -0.095f);
        public float kitYaw = -10f;
        public Vector2 matSize = new Vector2(0.27f, 0.25f);
        public Vector3 trayPos = new Vector3(0.025f, 0f, -0.005f);
        public float trayYaw = 0f, trayScale = 0.75f;
        public Vector3 cagePos = new Vector3(0.015f, 0f, 0.07f);
        public float cageYaw = 22f, cageScale = 0.8f;
        public Vector3 toolPos = new Vector3(0f, 0f, -0.078f);
        public float toolYaw = -2f, toolScale = 0.75f;
        public float tableMargin = 0.03f, maxShift = 0.10f;         // a narrow real table: slide in this far, else hide

        Animation _frame, _cage, _tray, _tool;
        Transform _kit, _frameT;
        float _liftOk, _cageOk, _trayOk, _nextFit;

        public override string Setup()
        {
            var t = Hub.Table;
            int n = 0;
            if (Kit.frameRest != null)
            {
                var f = NewProps.Spawn(Kit.frameRest, t, framePos, frameYaw, frameScale, "NP_FrameRest");
                _frameT = f.transform;
                _frame = PropClip.Prepare(f);
                n++;
            }
            if (Kit.sampleTray != null || Kit.queenCage != null || Kit.hiveTool != null)
            {
                _kit = new GameObject("NP_BenchKit").transform;
                _kit.SetParent(t, false);
                _kit.localPosition = kitPos;
                _kit.localRotation = Quaternion.Euler(0f, kitYaw, 0f);
                GameObject first = null, go;
                if (Kit.sampleTray != null) { go = NewProps.Spawn(Kit.sampleTray, _kit, trayPos, trayYaw, trayScale, "NP_SampleTray"); _tray = PropClip.Prepare(go); first = go; n++; }
                if (Kit.queenCage != null) { go = NewProps.Spawn(Kit.queenCage, _kit, cagePos, cageYaw, cageScale, "NP_QueenCage"); _cage = PropClip.Prepare(go); first = first ?? go; n++; }
                if (Kit.hiveTool != null) { go = NewProps.Spawn(Kit.hiveTool, _kit, toolPos, toolYaw, toolScale, "NP_HiveTool"); _tool = PropClip.Prepare(go); first = first ?? go; n++; }
                var r = first.GetComponentInChildren<Renderer>(true);
                if (r != null) Mat(r.sharedMaterial);
            }
            OnReset();
            return n > 0 ? "bench of " + n + " on the table" : null;
        }

        /// <summary>The beekeeper's mat under the kit: cream with a teal border, vertex colours, 4 tris (reads on any real table).</summary>
        void Mat(Material vc)
        {
            float hx = matSize.x * 0.5f, hz = matSize.y * 0.5f, b = 0.006f;
            Color32 teal = new Color32(0x2F, 0x7F, 0x73, 255), cream = new Color32(0xEF, 0xE3, 0xC4, 255);
            var v = new[]
            {
                new Vector3(-hx, 0.001f, -hz), new Vector3(-hx, 0.001f, hz), new Vector3(hx, 0.001f, hz), new Vector3(hx, 0.001f, -hz),
                new Vector3(-hx + b, 0.0016f, -hz + b), new Vector3(-hx + b, 0.0016f, hz - b), new Vector3(hx - b, 0.0016f, hz - b), new Vector3(hx - b, 0.0016f, -hz + b),
            };
            var m = new Mesh { name = "BenchMat" };
            m.vertices = v;
            m.colors32 = new[] { teal, teal, teal, teal, cream, cream, cream, cream };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            m.uv = new Vector2[8];
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            m.RecalculateBounds();
            var go = new GameObject("Mat");
            go.transform.SetParent(_kit, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = vc;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        void Update()
        {
            if (Time.time < _nextFit) return;
            _nextFit = Time.time + 0.5f;
            Fit(_frameT, framePos, 0.07f);
            Fit(_kit, kitPos, Mathf.Max(matSize.x, matSize.y) * 0.5f);
        }

        /// <summary>Keeps a bench piece on the real table top: slides it in from the edge, or hides it when there is no room.</summary>
        void Fit(Transform t, Vector3 home, float radius)
        {
            if (t == null) return;
            Vector3 pos = home;
            bool show = true;
            var ar = VirtualBeeSwarm.AR.ArRig.Instance;
            if (ar != null && ar.Room.HasTable)
            {
                var table = ar.Room.Table;
                Vector3 l = table.toLocal.MultiplyPoint3x4(Hub.Table.TransformPoint(home));
                float hx = table.half.x - radius - tableMargin, hz = table.half.y - radius - tableMargin;
                var c = new Vector3(Mathf.Clamp(l.x, -Mathf.Max(0f, hx), Mathf.Max(0f, hx)), l.y, Mathf.Clamp(l.z, -Mathf.Max(0f, hz), Mathf.Max(0f, hz)));
                float shift = (c - l).magnitude;
                if (shift > maxShift) show = false;
                else if (shift > 0.002f)
                {
                    pos = Hub.Table.InverseTransformPoint(table.toLocal.inverse.MultiplyPoint3x4(c));
                    pos.y = home.y;
                }
            }
            if (t.gameObject.activeSelf != show)
            {
                t.gameObject.SetActive(show);
                Debug.Log("[VBS][NewProps] " + t.name + (show ? " back on the table" : " hidden: no room on this table"));
                if (show) OnReset();
            }
            if (show && (t.localPosition - pos).sqrMagnitude > 1e-6f) t.localPosition = pos;
        }

        public override void OnLifted()
        {
            if (Time.time < _liftOk) return;
            _liftOk = Time.time + 4f;
            PropClip.Play(_tool, "Lever", 1.2f, WrapMode.Once, "HiveTool", "hive lifted");
            PropClip.Play(_frame, "InspectComb", 0.8f, WrapMode.Once, "FrameRest", "hive lifted");
        }

        public override void OnBeat(GameBeat from, GameBeat to)
        {
            if (to == GameBeat.Escape) StartCoroutine(Escape());
            else if (to == GameBeat.Pre) { StopAllCoroutines(); OnReset(); }
        }

        IEnumerator Escape()
        {
            yield return new WaitForSeconds(1.2f);
            Cage("escape");
            yield return new WaitForSeconds(1.0f);
            PropClip.Play(_frame, "InspectComb", 0.6f, WrapMode.Once, "FrameRest", "escape");
            PropClip.Play(_tool, "Lever", 1f, WrapMode.Once, "HiveTool", "escape");
        }

        void Cage(string why)
        {
            if (Time.time < _cageOk) return;
            _cageOk = Time.time + 5f;
            PropClip.Play(_cage, "OpenPlug", 1f, WrapMode.Once, "QueenCage", why);
        }

        public override void OnLine(string id, string caption)
        {
            string c = caption.ToLowerInvariant();
            if (c.Contains("queen")) Cage(id);
            if (System.Text.RegularExpressions.Regex.IsMatch(c, @"\bhoney\b") && Time.time >= _trayOk) // not "Honeymoon"
            {
                _trayOk = Time.time + 10f;
                PropClip.Play(_tray, "Inspect", 0.8f, WrapMode.Once, "SampleTray", id);
            }
        }

        public override void OnReset()
        {
            PropClip.Pose(_frame, "InspectComb", 0f);
            PropClip.Pose(_tray, "Inspect", 0f);
            PropClip.Pose(_cage, "OpenPlug", 0f);
            PropClip.Pose(_tool, "Lever", 0f);
            _liftOk = _cageOk = _trayOk = 0f;
        }
    }

    /// <summary>Right feed pick (room_feed_sugar_syrup): three nectar drops fall onto the hive and soak in (Wobble on landing).</summary>
    public class NectarAct : NewPropAct
    {
        public float dropScale = 2.2f;              // 1.8 cm in Blender -> 4 cm
        public float fallHeight = 0.24f;
        public float gravity = 2.6f;

        // left half of the roof: clear of the choice chip and the stamp on the right
        static readonly Vector3[] Offsets = { new Vector3(-0.095f, 0f, 0.01f), new Vector3(-0.03f, 0f, -0.025f), new Vector3(-0.06f, 0f, 0.035f) };

        GameObject[] _drops;
        Animation[] _anims;
        Vector3 _top;

        public override string Setup()
        {
            if (Kit.nectarDroplet == null || Hub.LiftRoot == null) return null;
            var b = NewProps.LocalBounds(Hub.LiftRoot, Hub.LiftRoot, t => t.name.Contains("HeatGlow") || t.name.StartsWith("Hex"));
            _top = new Vector3(b.center.x, b.max.y, b.center.z);
            _drops = new GameObject[Offsets.Length];
            _anims = new Animation[Offsets.Length];
            for (int i = 0; i < _drops.Length; i++)
            {
                _drops[i] = NewProps.Spawn(Kit.nectarDroplet, Hub.LiftRoot, _top, 0f, dropScale, "NP_Nectar" + i);
                _anims[i] = PropClip.Prepare(_drops[i]);
                _drops[i].SetActive(false);
            }
            return "nectar x" + _drops.Length + " over the hive roof (y " + _top.y.ToString("0.00") + ")";
        }

        public override void OnLine(string id, string caption)
        {
            if (id != GameLines.RoomFeedSugarSyrup.id) return;
            StopAllCoroutines();
            for (int i = 0; i < _drops.Length; i++) StartCoroutine(Drop(i, 0.15f + i * 0.32f));
        }

        IEnumerator Drop(int i, float delay)
        {
            var go = _drops[i];
            var t = go.transform;
            go.SetActive(false);
            yield return new WaitForSeconds(delay);
            Vector3 land = _top + Offsets[i] + Vector3.up * 0.004f;
            t.localPosition = land + Vector3.up * fallHeight;
            t.localScale = Vector3.one * dropScale;
            PropClip.Pose(_anims[i], "Wobble", 0f);
            go.SetActive(true);
            float t0 = Time.time, fall = Mathf.Sqrt(2f * fallHeight / gravity);
            while (Time.time - t0 < fall)
            {
                float k = Time.time - t0;
                t.localPosition = land + Vector3.up * (fallHeight - 0.5f * gravity * k * k);
                yield return null;
            }
            t.localPosition = land;
            PropClip.Play(_anims[i], "Wobble", 2.5f, WrapMode.Once, go.name, i == 0 ? "right feed pick" : "drop " + (i + 1));
            yield return new WaitForSeconds(0.3f);
            float s0 = Time.time;
            while (Time.time - s0 < 0.35f)
            {
                float k = (Time.time - s0) / 0.35f;
                t.localScale = Vector3.one * dropScale * (1f - k * k);
                t.localPosition = land + Vector3.down * (0.008f * k);
                yield return null;
            }
            go.SetActive(false);
        }

        public override void OnBeat(GameBeat from, GameBeat to)
        {
            if (to != GameBeat.Feed) { StopAllCoroutines(); OnReset(); }
        }

        public override void OnReset()
        {
            if (_drops == null) return;
            foreach (var d in _drops) if (d != null) d.SetActive(false);
        }
    }

    /// <summary>
    /// Escape: once the lid is up, the queen climbs out of the hive over the open lid and hovers in front of it, facing the
    /// player (she's safe); Blink now and then. T-0: back over the lid into the box.
    /// </summary>
    public class QueenAct : NewPropAct
    {
        public float scale = 1.1f;                  // 14 cm long in Blender -> 15 cm
        public float heroScale = 1.4f;              // x scale while she presents herself to the player
        public float heroSeconds = 3.2f, heroToward = 0.13f, heroUp = 0.07f;
        public float noseUp = 38f;                  // deg: her face and crown to a standing player, not her back
        public float riseDelay = 1.9f;              // the lid (escape + 0.3 s, LidOpen 1.5 s) is up by then
        public float climbSeconds = 1.3f, glideSeconds = 1.1f;
        public float hoverAbove = 0.10f;            // over the box top
        public float frontGap = 0.12f;              // in front of the open lid

        GameObject _queen;
        Animation _anim;
        Transform _lid;
        Bounds _box;
        Vector3 _inside, _over, _hover;
        bool _up;
        float _nextBlink;

        public override string Setup()
        {
            if (Kit.queenBee == null || Hub.LiftRoot == null) return null;
            var box = NewProps.FindDeep(Hub.LiftRoot, "BoxUpper");
            _box = box != null ? NewProps.LocalBounds(Hub.LiftRoot, box)
                : NewProps.LocalBounds(Hub.LiftRoot, Hub.LiftRoot, t => t.name.Contains("HeatGlow") || t.name == "Lid");
            _lid = NewProps.FindDeep(Hub.LiftRoot, "Lid");
            _inside = new Vector3(_box.center.x, _box.max.y - 0.05f, _box.center.z);
            _queen = NewProps.Spawn(Kit.queenBee, Hub.LiftRoot, _inside, 0f, scale, "NP_QueenBee");
            _anim = PropClip.Prepare(_queen);
            if (PropClip.Has(_anim, "Blink"))
            {
                var blink = _anim["Blink"];
                blink.layer = 1;
                foreach (var eye in new[] { "Eye.L", "Eye.R" })
                {
                    var e = NewProps.FindDeep(_queen.transform, eye);
                    if (e != null) blink.AddMixingTransform(e, true);
                }
            }
            _queen.SetActive(false);
            return "queen in the hive (box top y " + _box.max.y.ToString("0.00") + (_lid != null ? ", lid found" : ", no lid") + ")";
        }

        /// <summary>Her path for the lid as it is now: straight up over the lid's top edge, then in front of it.</summary>
        void PlanPath()
        {
            _hover = new Vector3(_box.center.x, _box.max.y + hoverAbove, _box.center.z - 0.02f);
            _over = new Vector3(_box.center.x, _box.max.y + hoverAbove + 0.05f, _box.center.z);
            if (_lid == null) return;
            var lb = NewProps.LocalBounds(Hub.LiftRoot, _lid);
            if (lb.size.sqrMagnitude < 1e-8f) return;
            _over.y = Mathf.Max(_over.y, lb.max.y + 0.05f);
            _hover.z = Mathf.Min(lb.min.z, _box.center.z) - frontGap;   // the player's side of the lid
            _hover.y = Mathf.Max(_hover.y, lb.center.y + 0.25f * lb.size.y);   // the lid's upper third, clear of the label
            Debug.Log("[VBS][NewProps] queen path: lid " + lb.min.ToString("F2") + ".." + lb.max.ToString("F2") + ", over " + _over.ToString("F2") +
                      ", hover " + _hover.ToString("F2") + " (LiftRoot space)");
        }

        public override void OnBeat(GameBeat from, GameBeat to)
        {
            StopAllCoroutines();
            if (to == GameBeat.Escape) StartCoroutine(Rise());
            else if (_up && to > GameBeat.Escape) StartCoroutine(Back());
            else OnReset();
        }

        IEnumerator Rise()
        {
            yield return new WaitForSeconds(riseDelay);
            PlanPath();
            var t = _queen.transform;
            t.localPosition = _inside;
            _queen.SetActive(true);
            _up = true;
            PropClip.Play(_anim, "Hover", 1f, WrapMode.Loop, "QueenBee", "escape: the queen is safe");
            float t0 = Time.time;
            while (Time.time - t0 < climbSeconds)
            {
                float k = (Time.time - t0) / climbSeconds;
                t.localPosition = Vector3.Lerp(_inside, _over, 1f - (1f - k) * (1f - k));
                t.localRotation = Quaternion.Euler(noseUp * k, 360f * Mathf.SmoothStep(0f, 1f, k), 0f); // a turn on the way up
                yield return null;
            }
            yield return Glide(_over, _hover, glideSeconds);
            Debug.Log("[VBS][NewProps] QueenBee presents herself (x" + heroScale.ToString("0.00") + " for " + heroSeconds.ToString("0.0") + " s)");
            _nextBlink = Time.time + 0.6f;
            float h0 = Time.time;
            while (true)
            {
                float s = Time.time - h0;
                // her moment: out toward the player and bigger, a slow turn to show the crown, then back to the lid
                float h = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(s / 0.6f)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((s - heroSeconds) / 0.8f)));
                t.localScale = Vector3.one * scale * Mathf.Lerp(1f, heroScale, h);
                t.localPosition = _hover + new Vector3(Mathf.Sin(s * 0.9f) * 0.012f, Mathf.Sin(s * 2.3f) * 0.006f + heroUp * h, -heroToward * h);
                t.localRotation = Quaternion.Euler(noseUp, Mathf.Sin(s * 0.7f) * 14f + Mathf.Sin(s * 1.9f) * 16f * h, Mathf.Sin(s * 0.9f) * -6f);
                if (Time.time >= _nextBlink && PropClip.Has(_anim, "Blink"))
                {
                    _nextBlink = Time.time + Random.Range(1.6f, 3.6f);
                    _anim["Blink"].speed = 1.4f;
                    _anim.Rewind("Blink");
                    _anim.Play("Blink");
                }
                yield return null;
            }
        }

        /// <summary>Forward first, down later, so she clears the lid's edge.</summary>
        IEnumerator Glide(Vector3 from, Vector3 to, float seconds)
        {
            var t = _queen.transform;
            float t0 = Time.time;
            while (Time.time - t0 < seconds)
            {
                float k = (Time.time - t0) / seconds;
                float kz = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k / 0.6f)), ky = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((k - 0.3f) / 0.7f));
                t.localPosition = new Vector3(Mathf.Lerp(from.x, to.x, kz), Mathf.Lerp(from.y, to.y, ky), Mathf.Lerp(from.z, to.z, kz));
                yield return null;
            }
            t.localPosition = to;
        }

        IEnumerator Back()
        {
            var t = _queen.transform;
            Vector3 from = t.localPosition;
            float t0 = Time.time;
            while (Time.time - t0 < 0.7f) // up and back over the lid (y first)
            {
                float k = (Time.time - t0) / 0.7f;
                float ky = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k / 0.6f)), kz = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((k - 0.3f) / 0.7f));
                t.localPosition = new Vector3(Mathf.Lerp(from.x, _over.x, kz), Mathf.Lerp(from.y, _over.y, ky), Mathf.Lerp(from.z, _over.z, kz));
                yield return null;
            }
            t0 = Time.time;
            while (Time.time - t0 < 0.6f)
            {
                t.localPosition = Vector3.Lerp(_over, _inside, Mathf.SmoothStep(0f, 1f, (Time.time - t0) / 0.6f));
                yield return null;
            }
            OnReset();
        }

        public override void OnReset()
        {
            _up = false;
            if (_anim != null) _anim.Stop();
            if (_queen != null) { _queen.transform.localScale = Vector3.one * scale; _queen.SetActive(false); }
        }
    }

    /// <summary>
    /// T-0: on the Moon the humans' Earth flag already stands (you humans got here first); two seconds after the Moon
    /// appears the union flag drops in and plants itself beside it. Both flutter (shape keys).
    /// With the environment pass installed the table is the Moon (MeadowIsland/MoonGround) and the grey Moon ball is
    /// hidden, so the flags stand on that Moon ground to the right of the hive (the rocket has left); without it they are
    /// children of the Moon ball, so they grow in, move and hide with it.
    /// </summary>
    public class MoonFlagAct : NewPropAct
    {
        public float heightOfRadius = 0.8f;         // ball: flag height / Moon radius
        public float plantDelay = 2.2f;
        public float groundHeight = 0.21f;          // table Moon: the humans' flag, m
        public float groundUnionScale = 1.22f;      // the union flag stands taller
        public float groundEarthDelay = 1.0f;       // the regolith has spread by then
        // spots on the island's outline (deg from the player's right, + = away; fraction of its radius), then yaw
        public Vector3 groundEarth = new Vector3(12f, 0.92f, 14f);   // back from the pebble dish and its stamp
        public Vector3 groundUnion = new Vector3(44f, 0.84f, -8f);
        public float flutterSpeed = 1.3f;

        const float IslandRx = 0.37f, IslandRz = 0.335f, FlagHeight = 0.188f; // environment.py's island; the flag in Blender

        Transform _moon, _island;
        GameObject _bee, _human;
        Animation _beeAnim, _humanAnim;
        Vector3 _beePos, _beeUp;
        Quaternion _beeRot;
        float _r, _beeScale, _humanScale;
        bool _wasOn;
        Coroutine _plant;

        public override string Setup()
        {
            var scene = NewProps.FindDeep(Hub.Table, "MoonScene");
            _moon = scene != null ? scene.Find("Moon") : null;
            if (_moon == null || Kit.moonFlag == null) return null;
            var island = Hub.Table.Find("MeadowIsland");
            _island = island != null && island.Find("MoonGround") != null ? island : null;
            if (_island != null)
            {
                _humanScale = groundHeight / FlagHeight;
                _beeScale = _humanScale * groundUnionScale;
                _beeUp = Vector3.up;
                _beeRot = Quaternion.Euler(0f, groundUnion.z, 0f);
                _r = groundHeight * _beeScale / _humanScale / 0.8f; // the drop height below is 0.35 r
                _bee = NewProps.Spawn(Kit.moonFlag, Hub.Table, Vector3.zero, groundUnion.z, _beeScale, "NP_MoonFlag");
                _beeAnim = PropClip.Prepare(_bee);
                _bee.SetActive(false);
                if (Kit.earthFlag != null)
                {
                    _human = NewProps.Spawn(Kit.earthFlag, Hub.Table, Vector3.zero, groundEarth.z, _humanScale, "NP_EarthFlag");
                    _humanAnim = PropClip.Prepare(_human);
                    _human.SetActive(false);
                }
                return "flags on the table Moon (" + (groundHeight * 100f).ToString("0") + " cm, right of the hive)";
            }
            var b = NewProps.LocalBounds(_moon, _moon);
            _r = Mathf.Max(0.05f, b.extents.y);
            float s = heightOfRadius * _r / FlagHeight;
            _beeScale = s;
            Place(b.center, 34f, 22f, out _beePos, out _beeRot, out _beeUp);
            _bee = NewProps.Spawn(Kit.moonFlag, _moon, _beePos, 0f, s, "NP_MoonFlag");
            _bee.transform.localRotation = _beeRot;
            _beeAnim = PropClip.Prepare(_bee);
            _bee.SetActive(false);
            if (Kit.earthFlag != null)
            {
                Place(b.center, 26f, -30f, out var p, out var q, out _);
                _human = NewProps.Spawn(Kit.earthFlag, _moon, p, 0f, s * 0.92f, "NP_EarthFlag");
                _human.transform.localRotation = q;
                _humanAnim = PropClip.Prepare(_human);
            }
            return "flags on the Moon (r " + _r.ToString("0.00") + " m, flag x" + s.ToString("0.00") + ")";
        }

        /// <summary>A spot on the Moon's surface: tipped toward the player (-Z) by toward deg, sideways by side deg.</summary>
        void Place(Vector3 center, float toward, float side, out Vector3 pos, out Quaternion rot, out Vector3 up)
        {
            float a = toward * Mathf.Deg2Rad, s = side * Mathf.Deg2Rad;
            up = new Vector3(Mathf.Sin(s) * Mathf.Cos(a), Mathf.Cos(s) * Mathf.Cos(a), -Mathf.Sin(a)).normalized;
            pos = center + up * (_r * 0.96f);
            var toPlayer = Vector3.ProjectOnPlane(Vector3.back, up).normalized;
            rot = Quaternion.LookRotation(-toPlayer, up); // dartiros' art faces -Z: the printed side to the player
        }

        /// <summary>A spot on the table Moon (TableAnchor space): the island's outline as it is fitted to the table now.</summary>
        Vector3 GroundSpot(Vector3 spot)
        {
            float fit = _island.localScale.x, a = spot.x * Mathf.Deg2Rad;
            var p = _island.localPosition + new Vector3(Mathf.Cos(a) * IslandRx, 0f, Mathf.Sin(a) * IslandRz) * (spot.y * fit);
            p.y = 0.002f;
            return p;
        }

        void Update()
        {
            bool on = _moon != null && _moon.gameObject.activeInHierarchy;
            if (on && !_wasOn)
            {
                if (_plant != null) StopCoroutine(_plant);
                if (_island != null)
                {
                    _beePos = GroundSpot(groundUnion);
                    if (_human != null) { _human.transform.localPosition = GroundSpot(groundEarth); StartCoroutine(RaiseHuman()); }
                }
                else PropClip.Play(_humanAnim, "Flutter", flutterSpeed * 0.85f, WrapMode.Loop, "EarthFlag", "the Moon");
                _plant = StartCoroutine(Plant());
            }
            else if (!on && _wasOn) { StopAllCoroutines(); OnReset(); }
            _wasOn = on;
        }

        /// <summary>The humans' flag comes up out of the regolith once it has covered the meadow.</summary>
        IEnumerator RaiseHuman()
        {
            _human.SetActive(false);
            yield return new WaitForSeconds(groundEarthDelay);
            var t = _human.transform;
            _human.SetActive(true);
            PropClip.Play(_humanAnim, "Flutter", flutterSpeed * 0.85f, WrapMode.Loop, "EarthFlag", "the table Moon");
            float t0 = Time.time;
            while (Time.time - t0 < 0.45f)
            {
                t.localScale = Vector3.one * _humanScale * NewProps.EaseOutBack((Time.time - t0) / 0.45f);
                yield return null;
            }
            t.localScale = Vector3.one * _humanScale;
        }

        IEnumerator Plant()
        {
            _bee.SetActive(false);
            yield return new WaitForSeconds(plantDelay);
            var t = _bee.transform;
            Vector3 from = _beePos + _beeUp * (_r * 0.35f);
            t.localRotation = _beeRot;
            _bee.SetActive(true);
            PropClip.Pose(_beeAnim, "Flutter", 0f);
            float t0 = Time.time;
            while (Time.time - t0 < 0.28f)
            {
                float k = (Time.time - t0) / 0.28f;
                t.localPosition = Vector3.Lerp(from, _beePos, k * k);
                yield return null;
            }
            t.localPosition = _beePos;
            PropClip.Play(_beeAnim, "Flutter", flutterSpeed, WrapMode.Loop, "MoonFlag", "planted on the Moon");
            Vector3 axis = _beeRot * Vector3.forward;
            float w0 = Time.time;
            while (Time.time - w0 < 0.7f)
            {
                float k = (Time.time - w0) / 0.7f;
                t.localRotation = Quaternion.AngleAxis(Mathf.Sin(k * 18f) * 9f * (1f - k), axis) * _beeRot;
                yield return null;
            }
            t.localRotation = _beeRot;
            _plant = null;
        }

        public override void OnReset()
        {
            if (_plant != null) { StopCoroutine(_plant); _plant = null; }
            if (_bee != null) { _bee.SetActive(false); _bee.transform.localPosition = _beePos; _bee.transform.localRotation = _beeRot; }
            if (_island != null && _human != null) { _human.SetActive(false); _human.transform.localScale = Vector3.one * _humanScale; }
            if (_beeAnim != null) _beeAnim.Stop();
            if (_humanAnim != null) _humanAnim.Stop();
            _wasOn = _moon != null && _moon.gameObject.activeInHierarchy;
        }
    }

    /// <summary>
    /// The contract ("plant flowers", room_end_contract; or 6 s into room_map when the build has no contract line): three
    /// pots pop onto the table, the watering can drops in, tips (Pour Tilt: stream + water level) and slides over them;
    /// each pot fills with soil and a bunch of flowers pops up. The can flies off; pots and flowers stay until pre.
    /// </summary>
    public class ContractAct : NewPropAct
    {
        public Vector3 potsPos = new Vector3(-0.04f, 0f, -0.205f);  // on the table in front of the island: right of the kit mat, left of the rocket
        public float potsScale = 0.52f, canScale = 0.52f;
        public float flowerHeight = 0.15f;          // m, a bunch above the soil
        public float drySoil = 0.6f;                // x: the soil before the water (the pot is never empty)
        public float mapLineToPour = 6.3f;          // room_map -> room_end_contract in the v2 unload

        static readonly string[] Pots = { "Terracotta Pot", "Sage Pot", "Cream Pot" };

        GameObject _pots, _can;
        Animation _canAnim;
        readonly Transform[] _soil = new Transform[3], _anchor = new Transform[3];
        readonly GameObject[] _flowers = new GameObject[3];
        Vector3[] _flowerScale = new Vector3[3];
        Vector3 _streamOffset;
        float _pourAt = -1f;
        bool _started, _done;

        public override string Setup()
        {
            if (Kit.flowerPots == null || Kit.wateringCan == null) return null;
            var t = Hub.Table;
            _pots = NewProps.Spawn(Kit.flowerPots, t, potsPos, 0f, potsScale, "NP_FlowerPots");
            PropClip.Prepare(_pots);
            _can = NewProps.Spawn(Kit.wateringCan, t, potsPos, 0f, canScale, "NP_WateringCan");
            _canAnim = PropClip.Prepare(_can);

            int found = 0;
            for (int i = 0; i < Pots.Length; i++)
            {
                _soil[i] = NewProps.FindDeep(_pots.transform, Pots[i] + " Soil");
                _anchor[i] = NewProps.FindDeep(_pots.transform, Pots[i] + " Plant Anchor");
                if (_anchor[i] == null) continue;
                found++;
                if (Kit.flowers == null) continue;
                var f = Instantiate(Kit.flowers, _anchor[i], false);
                f.name = "NP_Flowers" + i;
                // bunch the three flowers (11 cm apart in the art) into the pot, each turned a little
                for (int c = 0; c < f.transform.childCount; c++)
                {
                    var ch = f.transform.GetChild(c);
                    ch.localPosition = new Vector3(ch.localPosition.x * 0.22f, 0f, (c - 1) * 0.012f);
                    ch.localRotation = Quaternion.Euler(0f, i * 40f + c * 75f, (c - 1) * 9f);
                }
                foreach (var r in f.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                float h = Mathf.Max(0.01f, NewProps.LocalBounds(f.transform, f.transform).size.y);
                f.transform.localPosition = Vector3.zero;
                f.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // the older flower art faces +Z
                f.transform.localScale = Vector3.one * (flowerHeight / h / Mathf.Max(1e-4f, _anchor[i].lossyScale.y));
                _flowers[i] = f;
                _flowerScale[i] = f.transform.localScale;
            }

            // where the stream ends with the can tipped (Pour mid-clip), relative to the can's root, in table space
            PropClip.Pose(_canAnim, "Pour", 0.5f);
            var stream = NewProps.FindDeep(_can.transform, "Pour Stream Preview");
            var spout = NewProps.FindDeep(_can.transform, "Spout Outlet");
            Vector3 end = spout != null ? t.InverseTransformPoint(spout.position) : potsPos;
            if (stream != null)
            {
                var sb = NewProps.LocalBounds(t, stream);
                if (sb.size.sqrMagnitude > 1e-8f) end = new Vector3(sb.center.x, sb.min.y, sb.center.z);
            }
            _streamOffset = end - _can.transform.localPosition;
            PropClip.Pose(_canAnim, "Pour", 0f);
            OnReset();
            return "pots (" + found + " anchors) + can, stream end " + _streamOffset.ToString("F3");
        }

        Vector3 PotLocal(int i) => _anchor[i] != null ? Hub.Table.InverseTransformPoint(_anchor[i].position) : potsPos;

        public override void OnBeat(GameBeat from, GameBeat to)
        {
            if (to == GameBeat.Field) { if (!_done) Finish(); return; }
            if (to == GameBeat.Map) { _pourAt = -1f; return; }
            if (to > GameBeat.Map) return;              // a later end beat: the planted pots stay
            StopAllCoroutines();
            OnReset();
        }

        public override void OnLine(string id, string caption)
        {
            if (Hub.Director == null || _started) return;
            if (id == GameLines.RoomEndContract.id) Begin(id);   // whichever beat says it
            else if (id == GameLines.RoomMap.id && Hub.Director.Beat == GameBeat.Map && _pourAt < 0f) _pourAt = Time.time + mapLineToPour;
        }

        void Update()
        {
            if (_pourAt > 0f && Time.time >= _pourAt && !_started) Begin("room_map +" + mapLineToPour.ToString("0.0") + " s");
        }

        void Begin(string why)
        {
            _pourAt = -1f;
            _started = true;
            Debug.Log("[VBS][NewProps] contract: plant flowers (" + why + ")");
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            // pots pop in
            _pots.SetActive(true);
            var pt = _pots.transform;
            float t0 = Time.time;
            while (Time.time - t0 < 0.45f)
            {
                pt.localScale = Vector3.one * potsScale * NewProps.EaseOutBack((Time.time - t0) / 0.45f);
                yield return null;
            }
            pt.localScale = Vector3.one * potsScale;

            // the can drops in over the first pot
            var ct = _can.transform;
            Vector3 over0 = PotLocal(0) - _streamOffset + Vector3.up * 0.01f;
            Vector3 over2 = PotLocal(2) - _streamOffset + Vector3.up * 0.01f;
            _can.SetActive(true);
            PropClip.Pose(_canAnim, "Pour", 0f);
            t0 = Time.time;
            while (Time.time - t0 < 0.5f)
            {
                float k = (Time.time - t0) / 0.5f;
                ct.localPosition = over0 + Vector3.up * (0.25f * (1f - Mathf.SmoothStep(0f, 1f, k)));
                ct.localScale = Vector3.one * canScale * Mathf.Min(1f, k * 3f);
                yield return null;
            }
            ct.localPosition = over0;
            ct.localScale = Vector3.one * canScale;

            // tip it (frames 1-30 of Pour: tilt + stream), hold the tilt while it slides over the three pots
            var st = PropClip.Play(_canAnim, "Pour", 0.9f, WrapMode.ClampForever, "WateringCan", "plant flowers");
            while (st != null && st.normalizedTime < 0.5f) yield return null;
            if (st != null) st.speed = 0f;
            Debug.Log("[VBS][NewProps] WateringCan pouring over the pots");
            const float slide = 2.4f;
            bool[] grown = new bool[3];
            t0 = Time.time;
            while (true)
            {
                float k = Mathf.Clamp01((Time.time - t0) / slide);
                ct.localPosition = Vector3.Lerp(over0, over2, Mathf.SmoothStep(0f, 1f, k));
                for (int i = 0; i < 3; i++)
                    if (!grown[i] && k >= i * 0.5f - 0.02f) { grown[i] = true; StartCoroutine(Grow(i)); }
                if (k >= 1f) break;
                yield return null;
            }
            yield return new WaitForSeconds(0.25f);
            if (st != null) st.speed = 0.9f;
            while (st != null && _canAnim.IsPlaying("Pour") && st.normalizedTime < 0.98f) yield return null;

            // job done: up and away (the table stays clear for the rocket's crash spot and the map)
            Vector3 from = ct.localPosition;
            t0 = Time.time;
            while (Time.time - t0 < 0.7f)
            {
                float k = (Time.time - t0) / 0.7f;
                ct.localPosition = from + new Vector3(0f, 0.28f, 0.12f) * Mathf.SmoothStep(0f, 1f, k);
                ct.localScale = Vector3.one * canScale * (1f - k * k);
                yield return null;
            }
            _can.SetActive(false);
            PropClip.Pose(_canAnim, "Pour", 0f);
            _done = true;
        }

        IEnumerator Grow(int i)
        {
            if (_soil[i] != null)
            {
                float t0 = Time.time;
                while (Time.time - t0 < 0.35f)
                {
                    _soil[i].localScale = Vector3.one * Mathf.Lerp(drySoil, 1f, (Time.time - t0) / 0.35f);
                    yield return null;
                }
                _soil[i].localScale = Vector3.one;
            }
            var f = _flowers[i];
            if (f == null) yield break;
            yield return new WaitForSeconds(0.1f);
            f.SetActive(true);
            Debug.Log("[VBS][NewProps] Flowers " + (i + 1) + " up");
            float g0 = Time.time;
            while (Time.time - g0 < 0.5f)
            {
                f.transform.localScale = _flowerScale[i] * NewProps.EaseOutBack((Time.time - g0) / 0.5f);
                yield return null;
            }
            f.transform.localScale = _flowerScale[i];
        }

        void Finish()
        {
            StopAllCoroutines();
            _started = _done = true;
            _pots.SetActive(true);
            _pots.transform.localScale = Vector3.one * potsScale;
            _can.SetActive(false);
            for (int i = 0; i < 3; i++)
            {
                if (_soil[i] != null) _soil[i].localScale = Vector3.one;
                if (_flowers[i] != null) { _flowers[i].SetActive(true); _flowers[i].transform.localScale = _flowerScale[i]; }
            }
        }

        public override void OnReset()
        {
            _pourAt = -1f;
            _started = _done = false;
            if (_pots != null) _pots.SetActive(false);
            if (_can != null) { _can.SetActive(false); PropClip.Pose(_canAnim, "Pour", 0f); }
            for (int i = 0; i < 3; i++)
            {
                if (_soil[i] != null) _soil[i].localScale = Vector3.one * drySoil;
                if (_flowers[i] != null) { _flowers[i].SetActive(false); _flowers[i].transform.localScale = _flowerScale[i]; }
            }
        }
    }
}
