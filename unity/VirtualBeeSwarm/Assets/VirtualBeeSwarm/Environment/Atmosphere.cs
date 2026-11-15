using UnityEngine;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// Pollen motes: tiny warm specks of light drifting around the hive (on the island, so only once it has landed),
    /// thick when the meadow is healthy, thin when it's sick, none on the Moon. One additive billboard system.
    /// </summary>
    sealed class Atmosphere
    {
        static Material _mote;

        /// <summary>Additive soft dot (shared with the window motes).</summary>
        public static Material MoteMaterial
        {
            get
            {
                if (_mote == null)
                {
                    _mote = GameMaterials.Create("VBS_EnvMote (runtime)", Color.white, GameMaterials.Blend.Additive, vertexColors: true);
                    _mote.mainTexture = GameMeshes.CreateDotTexture(32);
                    if (_mote.HasProperty(GameMaterials.CullId)) _mote.SetFloat(GameMaterials.CullId, 0f);
                }
                return _mote;
            }
        }

        readonly ParticleSystem _ps;

        public Atmosphere(Transform islandRoot)
        {
            _ps = EnvUtil.Particles("PollenMotes", islandRoot, new Vector3(0f, 0.2f, 0.012f), 48);
            var main = _ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.004f, 0.014f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.005f, 0.01f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.86f, 0.45f), new Color(1f, 0.95f, 0.75f));
            main.gravityModifier = -0.0008f; // they rise, just
            var sh = _ps.shape;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = 0.34f;
            sh.radiusThickness = 0.6f;
            var noise = _ps.noise;
            noise.enabled = true;
            noise.strength = 0.012f;
            noise.frequency = 0.6f;
            noise.scrollSpeed = 0.15f;
            noise.quality = ParticleSystemNoiseQuality.Low;
            var col = _ps.colorOverLifetime;
            col.enabled = true;
            col.color = EnvUtil.FadeInOut(0.85f);
            var pr = _ps.GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.sharedMaterial = MoteMaterial;
        }

        public void Tick(EnvMood mood, float moonK, bool islandShown)
        {
            var em = _ps.emission;
            em.rateOverTime = islandShown ? 6.5f * Mathf.Clamp01(0.25f + 0.75f * mood.sunny) * Mathf.Clamp01(mood.bloom) * (1f - moonK) : 0f;
        }
    }
}
