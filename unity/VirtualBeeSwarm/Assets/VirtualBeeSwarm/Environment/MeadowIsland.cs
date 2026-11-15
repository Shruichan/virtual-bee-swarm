using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// The meadow patch under the hive (TableAnchor/MeadowIsland, at the hive's home): one turf mesh (clover, pebbles,
    /// grass fringe and the honeycomb path baked in, 1.2 mm thick where things stand so props, landed bees and the
    /// 2 mm contact shadows all stay on top), flower clusters around the rim (instanced), and the Moon ground that
    /// grows over it at T-0. It spreads out from under the hive when the hive lands, takes the window's mood and
    /// shrinks to fit the real table top when the room model knows one. The grab plate's renderer is hidden while it
    /// shows (the turf is the base now; the plate's collider still grabs). Flowers make room for props standing on the
    /// island (the table's "NP_" children: the new-props bench and flower pots): one under a prop shrinks away.
    /// </summary>
    sealed class MeadowIsland
    {
        struct Bloom
        {
            public Transform t;
            public Renderer r;
            public Quaternion rot;
            public Vector3 tiltAxis;
            public float scale, delay, stagger;
            public bool extra;
            public float room; // 1 = free, 0 = a prop stands here
            public bool blocked;
        }

        const float GrowSeconds = 0.85f;
        const float PopSeconds = 0.4f;
        const float Margin = 0.015f;
        const float BloomSize = 1.35f; // the flowers read from the player's spot (modelled 5-10 cm)
        // the island's outline in root space (Blender rx, ry, centre y), a little wider for the grass fringe
        const float Rx = 0.37f * 1.06f, Rz = 0.335f * 1.06f, Cz = 0.012f;

        readonly Transform _anchor, _hive, _root, _turf, _moon;
        readonly Renderer _turfR;
        readonly Bloom[] _blooms;
        readonly Renderer _plate;
        bool _plateHidden;
        bool _shown;
        float _growStart = -100f;
        float _fit = 1f, _nextFit;
        Color _lastTint = new Color(-1f, 0f, 0f, 0f);
        readonly ParticleSystem _burst;
        readonly List<Renderer> _props = new List<Renderer>();
        const float PropMargin = 0.035f;    // m around a prop's footprint
        const float PropStandsBelow = 0.03f; // its underside this close to the table top: it stands on the island
        float _burstAt = -1f;
        bool _moonHalf;

        public int BloomCount => _blooms.Length;
        public Transform Root => _root;
        public float Fit => _fit;

        public MeadowIsland(EnvironmentKit kit, Transform anchor, Transform hive)
        {
            _anchor = anchor;
            _hive = hive;
            _root = new GameObject("MeadowIsland").transform;
            _root.SetParent(anchor, false);
            _root.gameObject.layer = anchor.gameObject.layer;
            PlaceAtHive();
            _turf = MakeMesh("Turf", _root, kit.island, kit.ground, out _turfR);
            _moon = MakeMesh("MoonGround", _root, kit.moonGround, kit.ground, out _);
            _moon.gameObject.SetActive(false);

            var spots = kit.BloomSpots();
            var list = new List<Bloom>(spots.Length);
            foreach (var s in spots)
            {
                var mesh = kit.Bloom(s.kind);
                if (mesh == null) continue;
                var t = MakeMesh("Bloom_" + s.kind, _root, mesh, kit.vc, out var r);
                t.localPosition = new Vector3(s.x, s.z, s.y); // Blender (x, y, z up) -> Unity (x, y up, z)
                var rot = Quaternion.Euler(0f, 180f + s.yaw, 0f);
                t.localRotation = rot;
                float ang = Mathf.Atan2(s.y, s.x); // pop in a sweep around the hive, from the player's left
                float sweep = Mathf.Repeat((ang + Mathf.PI * 0.5f) / (2f * Mathf.PI), 1f);
                var axis = new Vector3(s.x, 0f, s.y);
                axis = Vector3.Cross(axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward, Vector3.up); // droop outward (left-handed)
                list.Add(new Bloom
                {
                    t = t, r = r, rot = rot, tiltAxis = axis, scale = s.scale, extra = s.extra,
                    delay = 0.35f + 0.7f * sweep + (s.extra ? 0.15f : 0f),
                    stagger = Mathf.Repeat(sweep * 7.31f, 1f),
                    room = 1f,
                });
                t.gameObject.SetActive(false);
            }
            _blooms = list.ToArray();
            var plate = anchor.Find("BasePlate (grab to place)");
            _plate = plate != null ? plate.GetComponent<Renderer>() : null;
            _burst = EnvUtil.Particles("SpreadBurst", _root, new Vector3(0f, 0.012f, Cz), 120);
            {
                var main = _burst.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 1.0f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.12f, 0.3f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.013f);
                main.gravityModifier = 0.012f;
                var sh = _burst.shape;
                sh.shapeType = ParticleSystemShapeType.Circle;
                sh.radius = 0.34f;
                sh.radiusThickness = 0.15f;
                sh.rotation = new Vector3(90f, 0f, 0f); // flat on the table, outward
                sh.scale = new Vector3(1f, 0.92f, 1f);
                var vel = _burst.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
                vel.y = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
                vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
                var col = _burst.colorOverLifetime;
                col.enabled = true;
                col.color = EnvUtil.FadeInOut(1f);
                var pr = _burst.GetComponent<ParticleSystemRenderer>();
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.sharedMaterial = Atmosphere.MoteMaterial;
            }
            _root.gameObject.SetActive(false);
        }

        /// <summary>A ring of sparkles (or Moon dust) thrown off the island's rim.</summary>
        void Burst(Color c, int n)
        {
            var main = _burst.main;
            main.startColor = new ParticleSystem.MinMaxGradient(c, Color.Lerp(c, Color.white, 0.5f));
            if (!_burst.isPlaying) _burst.Play();
            _burst.Emit(n);
        }

        static Transform MakeMesh(string name, Transform parent, Mesh mesh, Material mat, out Renderer r) =>
            EnvUtil.MakeMesh(name, parent, mesh, mat, true, out r);

        void PlaceAtHive()
        {
            var p = _hive != null && _hive.parent == _anchor ? _hive.localPosition : new Vector3(0f, 0f, 0.15f);
            _root.localPosition = new Vector3(p.x, 0f, p.z);
            _root.localRotation = Quaternion.identity;
        }

        public void Show(bool instant)
        {
            _shown = true;
            PlaceAtHive();
            _root.gameObject.SetActive(true);
            _turf.gameObject.SetActive(true);
            _growStart = instant ? Time.time - 10f : Time.time;
            _burstAt = instant ? -1f : Time.time + 0.32f;
            _moonHalf = false;
            _nextFit = 0f;
            _lastTint = new Color(-1f, 0f, 0f, 0f);
            if (_plate != null && _plate.enabled) { _plate.enabled = false; _plateHidden = true; }
            FindProps();
        }

        public void Hide()
        {
            _shown = false;
            _root.gameObject.SetActive(false);
            _moon.gameObject.SetActive(false);
            Restore();
        }

        /// <summary>Gives the grab plate its renderer back.</summary>
        public void Restore()
        {
            if (_plateHidden && _plate != null) _plate.enabled = true;
            _plateHidden = false;
        }

        public void Tick(EnvMood mood, float moonK)
        {
            if (!_shown) return;
            float g = Time.time - _growStart;
            if (Time.time >= _nextFit)
            {
                _nextFit = Time.time + 0.5f;
                FitToTable();
                if (_props.Count == 0) FindProps(); // a mid-show start: the props spawn after the island shows
                MakeRoom();
            }
            if (_burstAt > 0f && Time.time >= _burstAt) { _burstAt = -1f; Burst(new Color(1f, 0.86f, 0.4f), 70); }
            bool half = moonK > 0.5f;
            if (half != _moonHalf)
            {
                _moonHalf = half;
                if (half) Burst(new Color(0.78f, 0.78f, 0.8f), 60);   // Moon dust as the regolith covers the meadow
                else Burst(new Color(1f, 0.86f, 0.4f), 70);         // and gold when the meadow comes back
            }

            // turf: spreads out from under the hive; covered by the Moon ground at T-0
            float s = 0.06f + 0.94f * EaseOutBack(Mathf.Clamp01(g / GrowSeconds));
            _turf.localScale = new Vector3(s, 1f, s);
            bool turfOn = moonK < 0.93f;
            if (_turf.gameObject.activeSelf != turfOn) _turf.gameObject.SetActive(turfOn);
            bool moonOn = moonK > 0.0005f;
            if (_moon.gameObject.activeSelf != moonOn) _moon.gameObject.SetActive(moonOn);
            if (moonOn)
            {
                float m = 0.02f + 0.98f * EaseOutCubic(moonK);
                _moon.localScale = new Vector3(m, 1f, m);
            }

            // the window's mood: tint (fire flickers), flower size, full-bloom extras, droop
            Color tint = mood.tint;
            if (mood.fire)
            {
                float f = 0.88f + 0.12f * Mathf.Sin(Time.time * 11f) * Mathf.Sin(Time.time * 4.3f + 1f);
                tint = new Color(tint.r * f * 1.05f, tint.g * f, tint.b * f, 1f);
            }
            tint.a = 1f;
            bool tintChanged = Mathf.Abs(tint.r - _lastTint.r) + Mathf.Abs(tint.g - _lastTint.g) + Mathf.Abs(tint.b - _lastTint.b) > 0.002f;
            if (tintChanged)
            {
                _lastTint = tint;
                Tint(_turfR, tint);
            }
            float droop = Mathf.Clamp01((0.8f - mood.bloom) / 0.6f) * 38f;
            for (int i = 0; i < _blooms.Length; i++)
            {
                ref var b = ref _blooms[i];
                float pop = EaseOutBack(Mathf.Clamp01((g - b.delay) / PopSeconds));
                float hide = Mathf.Clamp01((moonK - 0.05f - 0.35f * b.stagger) / 0.25f);
                b.room = Mathf.MoveTowards(b.room, b.blocked ? 0f : 1f, Time.deltaTime * 4f);
                float size = BloomSize * b.scale * mood.bloom * (b.extra ? mood.extra : 1f) * pop * (1f - hide) * b.room;
                bool on = size > 0.01f;
                if (b.t.gameObject.activeSelf != on) b.t.gameObject.SetActive(on);
                if (!on) continue;
                b.t.localScale = new Vector3(size, size, size);
                b.t.localRotation = droop > 0.1f ? Quaternion.AngleAxis(droop * (0.7f + 0.3f * b.stagger), b.tiltAxis) * b.rot : b.rot;
                if (tintChanged || pop < 1f) Tint(b.r, tint);
            }
        }

        static void Tint(Renderer r, Color c) => EnvUtil.Tint(r, c);

        /// <summary>The props that may stand on the island: renderers below the table anchor's "NP_" children (found once per show).</summary>
        void FindProps()
        {
            _props.Clear();
            for (int i = 0; i < _anchor.childCount; i++)
            {
                var c = _anchor.GetChild(i);
                if (c.name.StartsWith("NP_", System.StringComparison.Ordinal)) _props.AddRange(c.GetComponentsInChildren<Renderer>(true));
            }
            for (int i = 0; i < _blooms.Length; i++) { _blooms[i].blocked = false; _blooms[i].room = 1f; }
            if (_props.Count > 0) MakeRoom(true);
        }

        /// <summary>Marks the flowers under a standing prop (every 0.5 s; the pots arrive late and the island refits).</summary>
        void MakeRoom(bool instant = false)
        {
            if (_props.Count == 0) return;
            float top = _anchor.position.y + PropStandsBelow * _anchor.lossyScale.y;
            for (int i = 0; i < _blooms.Length; i++)
            {
                Vector3 p = _root.TransformPoint(_blooms[i].t.localPosition);
                bool blocked = false;
                for (int j = 0; j < _props.Count && !blocked; j++)
                {
                    var r = _props[j];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    var b = r.bounds;
                    if (b.min.y > top || b.size.sqrMagnitude < 1e-10f) continue; // hovering (watering can, queen) or not drawn yet
                    blocked = p.x > b.min.x - PropMargin && p.x < b.max.x + PropMargin && p.z > b.min.z - PropMargin && p.z < b.max.z + PropMargin;
                }
                _blooms[i].blocked = blocked;
                if (instant) _blooms[i].room = blocked ? 0f : 1f;
            }
        }

        /// <summary>Largest scale (0.6..1) at which the island stays on the room model's table top.</summary>
        void FitToTable()
        {
            var ar = VirtualBeeSwarm.AR.ArRig.Instance;
            float fit = 1f;
            if (ar != null && ar.Room.HasTable)
            {
                var table = ar.Room.Table;
                fit = 0.6f;
                for (float s = 1f; s >= 0.6f; s -= 0.05f)
                {
                    if (Fits(table, s)) { fit = s; break; }
                }
            }
            if (Mathf.Abs(fit - _fit) > 0.01f)
            {
                Debug.Log("[VBS][Env] island fit x" + fit.ToString("0.00") + " (table top " +
                          (ar != null && ar.Room.HasTable ? (ar.Room.Table.half * 2f).ToString("0.00") : "unknown") + ")");
                _fit = fit;
            }
            _root.localScale = new Vector3(_fit, _fit, _fit);
        }

        bool Fits(VirtualBeeSwarm.AR.RoomModel.TableTop table, float s)
        {
            var parent = _root.parent;
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI * 2f / 24f;
                var local = new Vector3(Mathf.Cos(a) * Rx, 0f, Cz + Mathf.Sin(a) * Rz) * s;
                var world = parent.TransformPoint(_root.localPosition + _root.localRotation * local);
                var t = table.toLocal.MultiplyPoint3x4(world);
                if (Mathf.Abs(t.x) > table.half.x - Margin || Mathf.Abs(t.z) > table.half.y - Margin) return false;
            }
            return true;
        }

        static float EaseOutBack(float x) => EnvUtil.EaseOutBack(x);
        static float EaseOutCubic(float x) => EnvUtil.EaseOutCubic(x);
    }
}
