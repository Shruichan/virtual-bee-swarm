using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.EnvironmentArt
{
    static class EnvUtil
    {
        static MaterialPropertyBlock _mpb;

        /// <summary>A plain mesh renderer (no shadows, probes or motion vectors). yaw180: turn the art's +Z front to the player.</summary>
        public static Transform MakeMesh(string name, Transform parent, Mesh mesh, Material mat, bool yaw180, out Renderer r)
        {
            var go = new GameObject(name);
            if (parent != null) go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localRotation = yaw180 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            Quiet(mr);
            r = mr;
            return go.transform;
        }

        public static void Quiet(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>_Color per renderer (instanced property: keeps instancing, no material copies).</summary>
        public static void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            _mpb = _mpb ?? new MaterialPropertyBlock();
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(GameMaterials.ColorId, c);
            r.SetPropertyBlock(_mpb);
        }

        public static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float u = x - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        public static float EaseOutCubic(float x)
        {
            float u = 1f - x;
            return 1f - u * u * u;
        }

        /// <summary>A small looping particle system that starts silent (rateOverTime 0); the caller sets the rate.</summary>
        public static ParticleSystem Particles(string name, Transform parent, Vector3 localPos, int max)
        {
            var go = new GameObject(name);
            if (parent != null) go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.duration = 5f;
            main.playOnAwake = true;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var pr = go.GetComponent<ParticleSystemRenderer>();
            Quiet(pr);
            ps.Play();
            return ps;
        }

        public static Gradient FadeInOut(float peak)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(peak, 0.25f), new GradientAlphaKey(peak, 0.7f), new GradientAlphaKey(0f, 1f) });
            return g;
        }
    }
}
