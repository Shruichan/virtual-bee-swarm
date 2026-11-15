using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// The progress the player can see (feel audit gap 1): a little honeycomb plaque of 4 hex slots at the hive's foot,
    /// on the player's side (the hive art's own HexSlots face the wall). A right pick sends a gold hex token from the prop
    /// along an arc to its slot (3 gold bees carry it); it slams in (1.25 -> 0.9 -> 1), turns amber, the plaque punches
    /// and a gold sparkle fires. Escape: the four pulse one after another, then breathe. Board jumps / resets follow
    /// HexagonPanel and the beat. Shown MEET..ESCAPE, so the empty slots are there before the first station.
    /// </summary>
    [DefaultExecutionOrder(55)]
    public class JuiceHexPlaque : MonoBehaviour
    {
        const float R = 0.025f;                  // cell radius (corner), 5 cm across
        const float FlySeconds = 0.55f, SlamSeconds = 0.28f;
        static readonly Color Off = new Color(0.26f, 0.2f, 0.12f, 1f);
        static readonly Color Lit = new Color(1f, 0.72f, 0.1f, 1f);
        static readonly Color Flash = new Color(1f, 0.97f, 0.72f, 1f);
        static readonly Color PlateColor = new Color(0.11f, 0.08f, 0.05f, 1f);
        static readonly Color Gold = new Color(1f, 0.8f, 0.2f, 1f);

        JuiceParticles _fx;
        GameDirector _director;
        HiveBody _hive;
        HexagonPanel _panel;
        Transform _root, _token;
        readonly Transform[] _cell = new Transform[4];
        readonly Renderer[] _cellR = new Renderer[4];
        Renderer _tokenR;
        readonly bool[] _lit = new bool[4];
        readonly float[] _litAt = { -9f, -9f, -9f, -9f };
        readonly Vector3[] _slot = new Vector3[4];
        Material _mat;
        Mesh _hexMesh, _plateMesh;
        float _punchAt = -9f, _escapeAt = -9f, _nextFind;
        int _tokenSlot = -1;
        float _tokenStart = -9f;
        Vector3 _tokenFrom, _tokenCtrl;
        bool _placed;
        Vector3 _hivePosAtPlace;
        float _footY, _radius;

        public int LitCount { get { int n = 0; for (int i = 0; i < 4; i++) if (_lit[i]) n++; return n; } }
        public Transform Root => _root;

        public void Init(JuiceParticles fx, GameDirector director)
        {
            _fx = fx;
            _director = director;
            _mat = GameMaterials.Create("VBS_JuiceHex (runtime)", Color.white);
            _hexMesh = GameMeshes.CreateHexPrism(R, 0.008f, "VBS_JuiceHexCell");
            _plateMesh = GameMeshes.CreateHexPrism(R * 2.75f, 0.006f, "VBS_JuiceHexPlate");

            _root = new GameObject("HexPlaque").transform;
            _root.SetParent(transform, false);
            var plate = Part("Plate", _plateMesh, _root, new Vector3(0f, 0f, 0.006f), Vector3.one);
            GameMaterials.Tint(plate, PlateColor);
            float d = Mathf.Sqrt(3f) * R * 1.06f, h = 1.5f * R * 1.06f;
            _slot[0] = new Vector3(0f, h, 0f);        // smoker: top
            _slot[1] = new Vector3(-d * 0.5f, 0f, 0f); // feed: left
            _slot[2] = new Vector3(d * 0.5f, 0f, 0f);  // wasps: right
            _slot[3] = new Vector3(0f, -h, 0f);       // heat: bottom
            for (int i = 0; i < 4; i++)
            {
                _cellR[i] = Part("Hex" + i, _hexMesh, _root, _slot[i], Vector3.one);
                _cell[i] = _cellR[i].transform;
                GameMaterials.Tint(_cellR[i], Off);
            }
            _tokenR = Part("Token", _hexMesh, transform, Vector3.zero, Vector3.one);
            _token = _tokenR.transform;
            _token.gameObject.SetActive(false);
            _root.gameObject.SetActive(false);
        }

        Renderer Part(string name, Mesh mesh, Transform parent, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        // ------------------------------------------------------------------ events
        public void OnBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) { ResetAll(); return; }
            _placed = false; // re-place toward the player on every beat (placement may have moved the hive)
            for (int i = 0; i < 4; i++)
            {
                bool panelLit = _panel != null && _panel.IsLit(i);
                if (panelLit && !_lit[i]) SetLit(i, true, false);
                else if (!panelLit && _lit[i] && beat <= StationBeat(i) && beat < GameBeat.Escape) SetLit(i, false, false);
            }
            if (beat == GameBeat.Escape && prev != GameBeat.Escape) { _escapeAt = Time.time + 0.6f; for (int i = 0; i < 4; i++) _lit[i] = true; }
        }

        static GameBeat StationBeat(int i) => i == 0 ? GameBeat.Smoker : i == 1 ? GameBeat.Feed : i == 2 ? GameBeat.Wasps : GameBeat.Heat;

        public void ResetAll()
        {
            for (int i = 0; i < 4; i++) SetLit(i, false, false);
            _escapeAt = -9f;
            _tokenSlot = -1;
            if (_token != null) _token.gameObject.SetActive(false);
            _placed = false;
        }

        void SetLit(int i, bool on, bool slam)
        {
            _lit[i] = on;
            _litAt[i] = on && slam ? Time.time : -9f;
            if (_cellR[i] != null) GameMaterials.Tint(_cellR[i], on ? Lit : Off);
            if (_cell[i] != null) _cell[i].localScale = Vector3.one;
        }

        /// <summary>A right pick at station slot: the token flies from the prop (world point) to its slot.</summary>
        public void Fly(int slot, Vector3 from)
        {
            if (slot < 0 || slot > 3 || _root == null) return;
            if (!_placed) Place();
            if (!_root.gameObject.activeSelf) { SetLit(slot, true, true); return; }
            if (_tokenSlot >= 0 && _tokenSlot != slot) Land(); // a quicker second pick: land the first now
            _tokenSlot = slot;
            _tokenStart = Time.time;
            _tokenFrom = from;
            Vector3 to = _root.TransformPoint(_slot[slot]);
            _tokenCtrl = (from + to) * 0.5f + Vector3.up * 0.16f;
            _token.gameObject.SetActive(true);
            _token.position = from;
            _token.rotation = _root.rotation;
            _token.localScale = Vector3.one * 0.01f;
            GameMaterials.Tint(_tokenR, Flash);
            if (_fx != null)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 j = new Vector3(Mathf.Cos(k * 2.1f), 0.4f, Mathf.Sin(k * 2.1f)) * 0.025f;
                    _fx.Fly(from + j, to + j * 0.6f, 0.16f + j.y, FlySeconds, 0f, Gold, 0.016f, j);
                }
            Debug.Log("[VBS][Juice] hex token " + slot + " flying to the plaque");
        }

        void Land()
        {
            int s = _tokenSlot;
            _tokenSlot = -1;
            _token.gameObject.SetActive(false);
            if (s < 0) return;
            SetLit(s, true, true);
            _punchAt = Time.time;
            if (_fx != null && _root.gameObject.activeSelf)
            {
                Vector3 p = _root.TransformPoint(_slot[s]);
                _fx.Burst(p, 12, Gold, 0.55f, 0.45f, 0.012f, 0.8f, 0.5f, 4f);
                _fx.Ring(p, -_root.forward, 10, Flash, 0.6f, 0.3f, 0.008f);
            }
        }

        /// <summary>The station is done (beat moved on): a cleared flourish on the plaque.</summary>
        public void Cleared()
        {
            _punchAt = Time.time;
            if (_fx != null && _root != null && _root.gameObject.activeSelf)
                _fx.Ring(_root.position, -_root.forward, 14, Gold, 0.9f, 0.4f, 0.01f);
        }

        // ------------------------------------------------------------------ placement
        void Place()
        {
            if (_hive == null) return;
            var head = Camera.main != null ? Camera.main.transform : null;
            var rs = _hive.GetComponentsInChildren<Renderer>(false); // once per beat, not per frame
            bool any = false;
            Bounds b = new Bounds(_hive.transform.position, Vector3.zero);
            foreach (var r in rs)
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                if (r.transform.IsChildOf(transform)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            Vector3 c = _hive.transform.position;
            if (any) c = new Vector3(b.center.x, c.y, b.center.z);
            _footY = any ? b.min.y : c.y;
            _radius = any ? Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z), 0.06f, 0.3f) : 0.12f;
            Vector3 toHead = head != null ? head.position - c : -_hive.transform.forward;
            toHead.y = 0f;
            if (toHead.sqrMagnitude < 1e-4f) toHead = Vector3.back;
            toHead.Normalize();
            Vector3 pos = c + toHead * (_radius + 0.035f);
            pos.y = _footY + 0.09f;
            Vector3 look = head != null ? pos - head.position : -toHead;
            _root.SetPositionAndRotation(pos, Quaternion.LookRotation(look.sqrMagnitude > 1e-4f ? look : -toHead, Vector3.up));
            _hivePosAtPlace = _hive.transform.position;
            _placed = true;
        }

        // ------------------------------------------------------------------ frame
        void LateUpdate()
        {
            if (_root == null || _token == null) return;
            float now = Time.time;
            if ((_hive == null || _panel == null) && now >= _nextFind)
            {
                _nextFind = now + 2f;
                if (_hive == null) _hive = FindAnyObjectByType<HiveBody>();
                if (_panel == null) _panel = FindAnyObjectByType<HexagonPanel>();
            }
            var beat = _director != null ? _director.Beat : GameBeat.Pre;
            bool show = _hive != null && _hive.isActiveAndEnabled && beat >= GameBeat.Meet && beat <= GameBeat.Escape;
            if (_root.gameObject.activeSelf != show) _root.gameObject.SetActive(show);
            if (!show) { if (_token.gameObject.activeSelf) _token.gameObject.SetActive(false); _tokenSlot = -1; return; }
            if (!_placed || (!_hive.Held && _hive.LiftHeight < 0.005f && (_hive.transform.position - _hivePosAtPlace).sqrMagnitude > 0.0004f)) Place();

            // Board jumps: the hive's panel lit a slot we never saw picked
            if (_panel != null)
                for (int i = 0; i < 4; i++)
                    if (_panel.IsLit(i) && !_lit[i] && _tokenSlot != i) SetLit(i, true, true);

            // the token in flight
            if (_tokenSlot >= 0)
            {
                float t = (now - _tokenStart) / FlySeconds;
                Vector3 to = _root.TransformPoint(_slot[_tokenSlot]);
                if (t >= 1f) Land();
                else
                {
                    float e = t * t * (3f - 2f * t);
                    float u = 1f - e;
                    _token.position = u * u * _tokenFrom + 2f * u * e * _tokenCtrl + e * e * to;
                    _token.rotation = _root.rotation * Quaternion.Euler(0f, 0f, (1f - e) * 240f);
                    float s = t < 0.2f ? Mathf.Lerp(0.3f, 1.5f, t / 0.2f) : Mathf.Lerp(1.5f, 1.25f, (t - 0.2f) / 0.8f);
                    _token.localScale = new Vector3(s, s, s);
                }
            }

            // slam-in, escape pulse, celebrate breathing
            for (int i = 0; i < 4; i++)
            {
                if (_cell[i] == null) continue;
                float s = 1f;
                float a = now - _litAt[i];
                if (_litAt[i] > 0f && a < SlamSeconds)
                {
                    float k = a / SlamSeconds;
                    s = k < 0.35f ? Mathf.Lerp(1.25f, 0.9f, k / 0.35f) : Mathf.Lerp(0.9f, 1f, (k - 0.35f) / 0.65f);
                    GameMaterials.Tint(_cellR[i], Color.Lerp(Flash, Lit, k));
                }
                else if (_litAt[i] > 0f && a < SlamSeconds + 0.1f) GameMaterials.Tint(_cellR[i], _lit[i] ? Lit : Off);
                if (_escapeAt > 0f && now >= _escapeAt)
                {
                    float e = now - _escapeAt - i * 0.25f;
                    if (e >= 0f && e < 0.3f) { s = 1f + 0.3f * Mathf.Sin(e / 0.3f * Mathf.PI); GameMaterials.Tint(_cellR[i], Flash); }
                    else if (e >= 0.3f)
                    {
                        float br = 0.5f + 0.5f * Mathf.Sin((now - _escapeAt) * 4f - i * 0.8f);
                        GameMaterials.Tint(_cellR[i], Color.Lerp(Lit, Flash, br * 0.6f));
                        s = 1f + 0.05f * br;
                    }
                }
                _cell[i].localScale = new Vector3(s, s, s);
            }
            float p = now - _punchAt;
            float ps = p < 0.3f ? 1f + 0.12f * Mathf.Sin(p / 0.3f * Mathf.PI) * (1f - p / 0.3f) : 1f;
            _root.localScale = new Vector3(ps, ps, ps);
        }

        void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            if (_hexMesh != null) Destroy(_hexMesh);
            if (_plateMesh != null) Destroy(_plateMesh);
        }
    }
}
