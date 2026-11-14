using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Embers that spill OUT of the portal into the room: fx "embers" (honours delay_ms / duration_ms, so they land
    /// when the Orbis fire morph does), fx "embers_stop" stops emitting (live embers drift out and fade).
    /// One small particle system (&lt;= 80 particles, additive unlit, world space). The particle system's +Z must point
    /// out of the window toward the player; the box shape matches the pane. Keep it on an always-active object (not
    /// under the portal root, which FutureWindow hides while closed), or it misses fx events.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class EmbersFx : MonoBehaviour
    {
        [SerializeField] GameNet net;
        [SerializeField] float defaultDelaySeconds = 2.5f;

        public bool Emitting { get; private set; }
        public bool Scheduled => _startAt >= 0f;

        ParticleSystem _ps;
        float _startAt = -1f, _stopAt = -1f;

        // Lazy: other systems may call in before this object's Awake (e.g. while a parent is inactive).
        ParticleSystem Ps => _ps != null ? _ps : (_ps = GetComponent<ParticleSystem>());

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            Ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        void OnEnable() { if (net != null) net.FxReceived += HandleFx; }
        void OnDisable() { if (net != null) net.FxReceived -= HandleFx; }

        void HandleFx(string name, int delayMs, int durationMs)
        {
            if (name == GameIds.Fx.Embers) Play(delayMs >= 0 ? delayMs / 1000f : 0f, durationMs > 0 ? durationMs / 1000f : -1f);
            else if (name == GameIds.Fx.EmbersStop) Stop();
        }

        /// <summary>Start emitting after `delay` s; stop after `duration` s (negative = until embers_stop).</summary>
        public void Play(float delay, float duration = -1f)
        {
            _startAt = Time.time + Mathf.Max(0f, delay);
            _stopAt = duration > 0f ? _startAt + duration : -1f;
        }

        /// <summary>Backup for when the Hive's fx doesn't come: only if nothing is emitting or scheduled.</summary>
        public void PlayIfIdle(float delay = -1f)
        {
            if (Emitting || Scheduled) return;
            Play(delay >= 0f ? delay : defaultDelaySeconds);
        }

        public void Stop()
        {
            _startAt = -1f;
            _stopAt = -1f;
            if (Ps.isEmitting) Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Emitting = false;
        }

        public void ResetFx()
        {
            _startAt = _stopAt = -1f;
            Emitting = false;
            Ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        void Update()
        {
            float t = Time.time;
            if (_startAt >= 0f && t >= _startAt)
            {
                _startAt = -1f;
                Emitting = true;
                Ps.Play(true);
            }
            if (_stopAt >= 0f && t >= _stopAt) Stop();
        }

        /// <summary>Scene builder: sets up a cheap ember system for a pane of paneSize (m). Stored in the scene, tweak freely.</summary>
        public static void Configure(ParticleSystem ps, Material material, Vector2 paneSize)
        {
            var main = ps.main;
            main.duration = 5f;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.12f, 0.35f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.008f, 0.02f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.55f, 0.1f, 1f), new Color(1f, 0.85f, 0.3f, 1f));
            main.gravityModifier = -0.02f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var emission = ps.emission;
            emission.rateOverTime = 22f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(paneSize.x, paneSize.y, 0.01f);
            shape.randomDirectionAmount = 0.3f;

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.12f;
            noise.frequency = 0.8f;
            noise.scrollSpeed = 0.4f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.9f, 0.4f), 0f), new GradientColorKey(new Color(1f, 0.4f, 0.05f), 0.5f), new GradientColorKey(new Color(0.6f, 0.1f, 0.02f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f), new GradientAlphaKey(0.9f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Scene builder: a one-shot puff (smoke / water spray / dust). Fire it with ParticleSystem.Emit(n).</summary>
        public static void ConfigurePuff(ParticleSystem ps, Material material, Color color, float speed, float size, float lifetime, float gravity)
        {
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.7f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.5f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size);
            main.startColor = color;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.01f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var sz = ps.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.6f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
    }
}
