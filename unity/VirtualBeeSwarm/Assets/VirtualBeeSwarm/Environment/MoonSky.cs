using UnityEngine;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// T-0: a star dome around the player (240 stars, 2.1-2.8 m out, 4-80 deg up, never hidden by the real room: the
    /// passthrough is dimmed, so it reads as a night sky through the ceiling), twinkling and turning slowly; a soft blue
    /// glow behind the Earth (MoonScene/Earth); and the grey Moon ball (MoonScene/Moon) hidden while the table itself
    /// is the Moon (VBS_ENV_MOONBALL=1 keeps it).
    /// </summary>
    sealed class MoonSky
    {
        readonly Transform _anchor, _root, _glowT;
        readonly Renderer _a, _b, _glow;
        readonly Transform _moonBall, _earth;
        readonly bool _keepBall = System.Environment.GetEnvironmentVariable("VBS_ENV_MOONBALL") == "1";
        Renderer[] _hidden;
        Renderer[] _earthR;

        public MoonSky(EnvironmentKit kit, Transform anchor)
        {
            _anchor = anchor;
            _root = new GameObject("MoonSky (stars)").transform;
            EnvUtil.MakeMesh("StarsA", _root, kit.starsA, kit.stars, false, out _a);
            EnvUtil.MakeMesh("StarsB", _root, kit.starsB, kit.stars, false, out _b);
            _root.gameObject.SetActive(false);
            _glowT = EnvUtil.MakeMesh("EarthGlow", null, kit.earthGlow, kit.glow, false, out _glow);
            _glowT.gameObject.SetActive(false);
            var moonScene = anchor != null ? anchor.Find("MoonScene") : null;
            if (moonScene != null)
            {
                _moonBall = moonScene.Find("Moon");
                _earth = moonScene.Find("Earth");
            }
        }

        /// <summary>The Moon stage starts: centre the dome on the player, hide the Moon ball.</summary>
        public void Begin()
        {
            var cam = Camera.main;
            Vector3 c = cam != null ? cam.transform.position
                : _anchor != null ? _anchor.TransformPoint(new Vector3(0f, 0.6f, -0.55f)) : new Vector3(0f, 1.4f, -0.6f);
            _root.SetPositionAndRotation(c, Quaternion.Euler(0f, _anchor != null ? _anchor.eulerAngles.y : 0f, 0f));
            if (_moonBall != null && !_keepBall && _hidden == null)
            {
                var rs = _moonBall.GetComponentsInChildren<Renderer>(true);
                int n = 0;
                foreach (var r in rs) if (r.enabled) n++;
                _hidden = new Renderer[n];
                n = 0;
                foreach (var r in rs) if (r.enabled) { r.enabled = false; _hidden[n++] = r; }
            }
            if (_earth != null) _earthR = _earth.GetComponentsInChildren<Renderer>(true);
        }

        public void Restore()
        {
            if (_hidden != null)
                foreach (var r in _hidden) if (r != null) r.enabled = true;
            _hidden = null;
            if (_root != null && _root.gameObject.activeSelf) _root.gameObject.SetActive(false); // (null while the scene unloads)
            if (_glowT != null && _glowT.gameObject.activeSelf) _glowT.gameObject.SetActive(false);
        }

        public void Tick(float moonK)
        {
            float k = Mathf.SmoothStep(0f, 1f, moonK);
            bool on = k > 0.002f;
            if (_root.gameObject.activeSelf != on) _root.gameObject.SetActive(on);
            if (!on)
            {
                if (_glowT.gameObject.activeSelf) _glowT.gameObject.SetActive(false);
                return;
            }
            float t = Time.time;
            EnvUtil.Tint(_a, new Color(1f, 1f, 1f, k * (0.78f + 0.22f * Mathf.Sin(t * 1.7f))));
            EnvUtil.Tint(_b, new Color(1f, 1f, 1f, k * (0.7f + 0.3f * Mathf.Sin(t * 2.3f + 1.3f))));
            _root.Rotate(0f, Time.deltaTime * 0.6f, 0f, Space.World);

            // the Earth's glow: a camera-facing halo just behind it
            bool glow = false;
            var cam = Camera.main;
            if (_earthR != null && cam != null)
            {
                bool any = false;
                Bounds b = default;
                foreach (var r in _earthR)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (any)
                {
                    float rad = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
                    Vector3 away = (b.center - cam.transform.position).normalized;
                    _glowT.SetPositionAndRotation(b.center + away * rad * 0.6f, Quaternion.LookRotation(away, Vector3.up));
                    _glowT.localScale = Vector3.one * (rad * 2f);
                    EnvUtil.Tint(_glow, new Color(1f, 1f, 1f, k * (0.9f + 0.1f * Mathf.Sin(t * 0.9f))));
                    glow = rad > 0.005f;
                }
            }
            if (_glowT.gameObject.activeSelf != glow) _glowT.gameObject.SetActive(glow);
        }
    }
}
