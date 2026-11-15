using UnityEngine;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// The window ring as a magic portal: flowering vines on the frame (Portal art space, so they open, grow and swell
    /// with it), soft light shafts out of it toward the table in the colour of the window's scene (white-hot at the
    /// heat station, flickering orange in the fire, gone on the Moon), petals and motes drifting out of it.
    /// Needs the Portal art (ArtSlots "Portal (art)"); with the primitive placeholder only the motes run.
    /// </summary>
    sealed class PortalDressing
    {
        readonly FutureWindow _window;
        readonly Renderer _vines, _shafts;
        readonly ParticleSystem _petals, _motes;
        float _open, _shaft;
        Color _lastVine = new Color(-1f, 0f, 0f, 0f);

        public string Summary { get; }

        public PortalDressing(EnvironmentKit kit, FutureWindow window)
        {
            _window = window;
            var pane = window.PaneTransform;
            var art = pane != null ? pane.parent : null;
            bool hasArt = art != null && art.name.Contains("(art)");
            if (hasArt)
            {
                EnvUtil.MakeMesh("PortalVines", art, kit.portalVines, kit.vc, false, out _vines);
                EnvUtil.MakeMesh("LightShafts", art, kit.shafts, kit.glow, false, out _shafts);
                _shafts.enabled = false;
            }
            // petals off the ring, motes out of the pane; both drift toward the player (+Z in the art's space)
            var host = hasArt ? pane : window.transform;
            _petals = EnvUtil.Particles("Petals", host, new Vector3(0f, 0f, 0.03f), 24);
            {
                var main = _petals.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 5f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.04f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.25f);
                main.startRotation3D = true;
                main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = 0.006f;
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white, new Color(1f, 0.82f, 0.95f));
                var sh = _petals.shape;
                sh.shapeType = ParticleSystemShapeType.Circle;
                sh.radius = hasArt ? 0.37f : 0.25f;
                sh.radiusThickness = 0f;
                var vel = _petals.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.Local;
                vel.x = new ParticleSystem.MinMaxCurve(-0.01f, 0.01f);
                vel.y = new ParticleSystem.MinMaxCurve(-0.02f, 0.0f);
                vel.z = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
                var rot = _petals.rotationOverLifetime;
                rot.enabled = true;
                rot.separateAxes = true;
                rot.x = new ParticleSystem.MinMaxCurve(-2f, 2f);
                rot.y = new ParticleSystem.MinMaxCurve(-2f, 2f);
                rot.z = new ParticleSystem.MinMaxCurve(-2f, 2f);
                var size = _petals.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.2f), new Keyframe(0.12f, 1f),
                                                                                  new Keyframe(0.85f, 1f), new Keyframe(1f, 0f)));
                var pr = _petals.GetComponent<ParticleSystemRenderer>();
                pr.renderMode = ParticleSystemRenderMode.Mesh;
                pr.mesh = kit.petal;
                pr.enableGPUInstancing = false; // GameUnlit has no procedural instancing setup: instanced mesh particles would all draw at one point
                pr.alignment = ParticleSystemRenderSpace.World;
                pr.sharedMaterial = kit.vc;
            }
            _motes = EnvUtil.Particles("WindowMotes", host, new Vector3(0f, 0f, 0.02f), 30);
            {
                var main = _motes.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 6f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.0f, 0.01f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.005f, 0.011f);
                var sh = _motes.shape;
                sh.shapeType = ParticleSystemShapeType.Box;
                sh.scale = hasArt ? new Vector3(0.55f, 0.5f, 0.02f) : new Vector3(0.45f, 0.25f, 0.02f);
                var vel = _motes.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.Local;
                vel.x = new ParticleSystem.MinMaxCurve(-0.015f, 0.015f);
                vel.y = new ParticleSystem.MinMaxCurve(-0.01f, 0.012f);
                vel.z = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
                var col = _motes.colorOverLifetime;
                col.enabled = true;
                col.color = EnvUtil.FadeInOut(0.9f);
                var pr = _motes.GetComponent<ParticleSystemRenderer>();
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.sharedMaterial = Atmosphere.MoteMaterial;
            }
            Summary = hasArt ? "vines, light shafts, petals, motes" : "no Portal art: petals + motes only";
        }

        public void Tick(EnvMood mood, float moonK, bool open)
        {
            float dt = Time.deltaTime;
            _open = Mathf.MoveTowards(_open, open ? 1f : 0f, dt / 0.8f);
            float t = Time.time;

            // light shafts: the window's light, out toward the table
            float target = Mathf.Clamp(mood.sunny, 0f, 1.4f) * _open * (1f - moonK);
            _shaft = Mathf.MoveTowards(_shaft, target, dt * 0.9f);
            if (_shafts != null)
            {
                bool on = _shaft > 0.01f;
                if (_shafts.enabled != on) _shafts.enabled = on;
                if (on)
                {
                    float shimmer = 0.86f + 0.14f * Mathf.Sin(t * 1.3f) * Mathf.Sin(t * 0.7f + 2f);
                    if (mood.fire) shimmer *= 0.75f + 0.25f * Mathf.Sin(t * 13f) * Mathf.Sin(t * 5.1f);
                    var c = mood.light;
                    c.a = Mathf.Clamp01(_shaft * shimmer * 0.95f);
                    EnvUtil.Tint(_shafts, c);
                }
            }

            // vines: greener than the island (the portal's own magic), but they wilt and burn with it
            if (_vines != null)
            {
                var v = Color.LerpUnclamped(Color.white, mood.tint, 0.6f);
                if (mood.fire) { float f = 0.9f + 0.1f * Mathf.Sin(t * 9f); v = new Color(v.r * f, v.g * f, v.b * f, 1f); }
                v.a = 1f;
                if (Mathf.Abs(v.r - _lastVine.r) + Mathf.Abs(v.g - _lastVine.g) + Mathf.Abs(v.b - _lastVine.b) > 0.002f)
                {
                    _lastVine = v;
                    EnvUtil.Tint(_vines, v);
                }
            }

            float sunny = Mathf.Clamp01(mood.sunny) * _open * (1f - moonK);
            var pe = _petals.emission;
            pe.rateOverTime = 2.6f * sunny * Mathf.Clamp01(mood.bloom);
            var me = _motes.emission;
            me.rateOverTime = 4f * Mathf.Max(0.25f, sunny) * _open * (1f - moonK);
            var mm = _motes.main;
            mm.startColor = new Color(mood.light.r, mood.light.g * 0.95f, mood.light.b * 0.8f, 1f);
        }
    }
}
