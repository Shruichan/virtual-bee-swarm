using UnityEngine;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The globe bursting into the room: petals and pollen thrown out from a shell around a point (the player's head
    /// after the dive, the globe itself in comfort mode), slowing to a drift and fading. Two one-shot particle systems.
    /// </summary>
    public class GlobeBurst
    {
        readonly ParticleSystem _petals, _pollen;
        readonly Material _petalMat, _pollenMat;
        readonly Texture2D _petalTex, _dotTex;

        /// <param name="queue">Render queue for both materials (-1: the default transparent one).</param>
        public GlobeBurst(Transform parent, int queue = -1)
        {
            _petalTex = Petal(32, 48);
            _dotTex = Dot(32);
            _petalMat = GameMaterials.Create("VBS_GlobePetal", Color.white, GameMaterials.Blend.Alpha, vertexColors: true, instancing: false, doubleSided: true);
            _petalMat.mainTexture = _petalTex;
            _pollenMat = GameMaterials.Create("VBS_GlobePollen", Color.white, GameMaterials.Blend.Additive, vertexColors: true, instancing: false, doubleSided: true);
            _pollenMat.mainTexture = _dotTex;
            if (queue >= 0) _petalMat.renderQueue = _pollenMat.renderQueue = queue;

            var petalColors = new Gradient();
            petalColors.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.72f, 0.82f), 0f), new GradientColorKey(new Color(1f, 0.98f, 0.95f), 0.35f),
                        new GradientColorKey(new Color(0.82f, 0.7f, 1f), 0.65f), new GradientColorKey(new Color(1f, 0.86f, 0.35f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            _petals = Make(parent, "GlobePetals", _petalMat, new ParticleSystem.MinMaxGradient(petalColors) { mode = ParticleSystemGradientMode.RandomColor },
                new Vector2(0.022f, 0.042f), new Vector2(1.3f, 2.1f), new Vector2(1.1f, 2.4f), 0.04f, true);
            _pollen = Make(parent, "GlobePollen", _pollenMat,
                new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.4f), new Color(1f, 0.95f, 0.7f)),
                new Vector2(0.006f, 0.014f), new Vector2(0.9f, 1.6f), new Vector2(0.8f, 2.8f), 0f, false);
        }

        static ParticleSystem Make(Transform parent, string name, Material mat, ParticleSystem.MinMaxGradient color, Vector2 size,
            Vector2 life, Vector2 speed, float gravity, bool spin)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 3f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = color;
            main.gravityModifier = gravity;
            main.maxParticles = 160;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.5f;
            shape.radiusThickness = 0f; // from the shell, outward
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0.22f;
            limit.dampen = 0.07f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.06f), new GradientAlphaKey(0.9f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(fade);
            if (spin)
            {
                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-4f, 4f);
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.minParticleSize = 0f;
            r.maxParticleSize = 0.5f;
            return ps;
        }

        /// <summary>Throw `petals` petals and twice as much pollen out from a shell of `radius` round `at`.</summary>
        public void Emit(Vector3 at, float radius, int petals)
        {
            Fire(_petals, at, radius, petals);
            Fire(_pollen, at, radius * 0.9f, petals * 2);
        }

        static void Fire(ParticleSystem ps, Vector3 at, float radius, int n)
        {
            if (ps == null) return;
            ps.transform.position = at;
            var shape = ps.shape;
            shape.radius = Mathf.Max(0.05f, radius);
            if (!ps.isPlaying) ps.Play(false);
            ps.Emit(n);
        }

        public void Clear()
        {
            if (_petals != null) _petals.Clear();
            if (_pollen != null) _pollen.Clear();
        }

        public void Destroy()
        {
            if (_petalMat != null) Object.Destroy(_petalMat);
            if (_pollenMat != null) Object.Destroy(_pollenMat);
            if (_petalTex != null) Object.Destroy(_petalTex);
            if (_dotTex != null) Object.Destroy(_dotTex);
        }

        /// <summary>A petal: a soft ellipse, pointed at one end, white (tinted per particle).</summary>
        static Texture2D Petal(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "VBS_GlobePetal", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = (y + 0.5f) / h;                    // 0 = base, 1 = tip
                float half = Mathf.Sin(Mathf.PI * Mathf.Pow(v, 0.8f)) * 0.5f; // widest a little past the middle
                float u = Mathf.Abs((x + 0.5f) / w - 0.5f);
                float a = Mathf.Clamp01((half - u) * w * 0.6f);
                byte shade = (byte)(235 + 20 * v);
                px[y * w + x] = new Color32(shade, shade, shade, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>A soft round dot (pollen).</summary>
        static Texture2D Dot(int s)
        {
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { name = "VBS_GlobeDot", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * s + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
