using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The fire brigade (PLAN v3.1 V18, SHOULD). When the window scene turns fire_out (voice, Board, F/P key: whoever
    /// put it out), a crown of 6 bees rushes a small rain cloud out of the window, parks it over the table for 2 s
    /// while it rains, then flies it back out. When the fire starts (whatif_fire) a burst of embers also spawns above
    /// the table, because embers from a window 2-3 m away don't reach it. Self-contained: add it anywhere
    /// (BuildGameScene: next to BeeDelivery). Nothing with delivery.mode off. Snaps away on a beat change or reset.
    /// </summary>
    public class DeliveryRainCloud : MonoBehaviour
    {
        [SerializeField] GameNet net;
        [SerializeField] BeeDelivery delivery;
        [Tooltip("Empty: the HiveBody in the scene.")]
        [SerializeField] Transform hiveRoot;
        [Tooltip("Empty: built from spheres at runtime.")]
        [SerializeField] Transform cloud;
        [SerializeField] float heightAboveHive = 0.45f;
        [SerializeField] float parkSeconds = 2f;
        [SerializeField] int crew = 6;
        [SerializeField] int emberBurst = 30;

        const float RainRate = 90f;
        const float RainRampSeconds = 0.25f;

        enum State { Idle, Coming, Raining, Leaving }
        State _state;
        float _rainSince;
        ParticleSystem _rain, _embers;
        GameDirector _director;

        public bool Busy => _state != State.Idle;

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (hiveRoot == null) { var hb = FindAnyObjectByType<HiveBody>(); if (hb != null) hiveRoot = hb.transform; }
        }

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (_director != null) { _director.BeatChanged += HandleBeat; _director.GroupResetDone += Stop; }
        }

        void OnEnable() { if (net != null) net.SceneChanged += HandleScene; }
        void OnDisable()
        {
            if (net != null) net.SceneChanged -= HandleScene;
            Stop();
        }

        void OnDestroy()
        {
            if (_director != null) { _director.BeatChanged -= HandleBeat; _director.GroupResetDone -= Stop; }
        }

        BeeDelivery D => delivery != null ? delivery : (delivery = BeeDelivery.Instance);

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat != GameBeat.WhatIf) Stop();
        }

        void HandleScene(string scene)
        {
            if (scene == GameIds.Scene.FireOut) Bring();
            else if (scene == GameIds.Scene.WhatIfFire) EmberBurst();
        }

        /// <summary>Bring the rain cloud now (also usable by an operator/test).</summary>
        public void Bring()
        {
            if (BeeDelivery.Mode == DeliveryMode.Off || D == null || _state != State.Idle) return;
            EnsureCloud();
            Vector3 c = AboveHive(heightAboveHive);
            Quaternion r = hiveRoot != null ? hiveRoot.rotation : Quaternion.identity;
            D.Park(cloud, new Pose(c, r), crew);
            _state = State.Coming;
            Debug.Log("[VBS][Delivery] fire brigade: rain cloud on its way at " + c.ToString("F2"));
        }

        Vector3 AboveHive(float height)
        {
            Vector3 origin = hiveRoot != null ? hiveRoot.position : transform.position;
            origin.y += height;
            if (Bloom.MeadowLayout.Active)
                origin.y = Mathf.Max(origin.y, Bloom.MeadowLayout.RoofY + 0.15f);
            return origin;
        }

        public void Stop()
        {
            if (_rain != null) _rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (_embers != null) _embers.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (cloud != null && D != null) D.Release(cloud, hide: true);
            else if (cloud != null) cloud.gameObject.SetActive(false);
            _state = State.Idle;
        }

        void Update()
        {
            if (_state == State.Idle || cloud == null || D == null) return;
            var di = cloud.GetComponent<DeliveryItem>();
            switch (_state)
            {
                case State.Coming:
                    if (di != null && di.parked)
                    {
                        _state = State.Raining;
                        _rainSince = Time.time;
                        if (_rain != null)
                        {
                            UpdateRainRate(0f);
                            _rain.Play(true);
                        }
                        GameSound.Cue("fire_out", cloud);
                    }
                    else if (!cloud.gameObject.activeInHierarchy) _state = State.Idle;
                    break;
                case State.Raining:
                    float elapsed = Time.time - _rainSince;
                    if (_rain != null) UpdateRainRate(elapsed);
                    if (elapsed >= parkSeconds)
                    {
                        if (_rain != null) _rain.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                        D.Return(cloud);
                        _state = State.Leaving;
                    }
                    break;
                case State.Leaving:
                    if (!cloud.gameObject.activeSelf) _state = State.Idle;
                    break;
            }
        }

        void UpdateRainRate(float elapsed)
        {
            float ramp = Mathf.Max(0.01f, Mathf.Min(RainRampSeconds, parkSeconds * 0.5f));
            float fade = Mathf.Clamp01(Mathf.Min(elapsed, parkSeconds - elapsed) / ramp);
            var emission = _rain.emission;
            emission.rateOverTime = RainRate * Mathf.SmoothStep(0f, 1f, fade);
        }

        void EnsureCloud()
        {
            if (cloud == null)
            {
                cloud = DeliveryPlaceholders.BuildCloud(transform);
                cloud.gameObject.SetActive(false);
            }
            var di = cloud.GetComponent<DeliveryItem>();
            if (di == null) di = cloud.gameObject.AddComponent<DeliveryItem>();
            di.weight = 0.3f;
            di.castShadow = false;
            if (_rain == null)
            {
                var go = new GameObject("[VBS] Rain");
                go.transform.SetParent(cloud, false);
                go.transform.localPosition = new Vector3(0f, 0.03f, 0f);
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // +Z (emission) points down
                _rain = go.AddComponent<ParticleSystem>();
                _rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _rain.main;
                main.loop = true;
                main.playOnAwake = false;
                main.startLifetime = 0.55f;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 0.9f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.003f, 0.006f);
                main.startColor = new Color(0.55f, 0.75f, 1f, 0.85f);
                main.gravityModifier = 0.6f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 120;
                var em = _rain.emission;
                em.rateOverTime = 0f;
                var sh = _rain.shape;
                sh.enabled = true;
                sh.shapeType = ParticleSystemShapeType.Box;
                sh.scale = new Vector3(0.16f, 0.1f, 0.01f);
                var pr = _rain.GetComponent<ParticleSystemRenderer>();
                pr.renderMode = ParticleSystemRenderMode.Stretch;
                pr.lengthScale = 4f;
                var mat = GameMaterials.Create("VBS_Rain (runtime)", Color.white, GameMaterials.Blend.Alpha, vertexColors: true);
                mat.mainTexture = GameMeshes.CreateDotTexture();
                pr.sharedMaterial = mat;
                pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pr.receiveShadows = false;
            }
        }

        void EmberBurst()
        {
            if (BeeDelivery.Mode == DeliveryMode.Off || emberBurst <= 0) return;
            if (_embers == null)
            {
                var go = new GameObject("[VBS] TableEmbers");
                go.transform.SetParent(transform, false);
                go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                _embers = go.AddComponent<ParticleSystem>();
                _embers.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var mat = GameMaterials.Create("VBS_TableEmber (runtime)", Color.white, GameMaterials.Blend.Additive, vertexColors: true, emission: 1.5f);
                mat.mainTexture = GameMeshes.CreateDotTexture();
                EmbersFx.ConfigurePuff(_embers, mat, new Color(1f, 0.6f, 0.15f, 1f), 0.3f, 0.014f, 2.2f, 0.05f);
                var sh = _embers.shape;
                sh.angle = 60f;
                sh.radius = 0.15f;
            }
            _embers.transform.position = AboveHive(0.45f);
            _embers.Emit(emberBurst);
        }
    }
}
