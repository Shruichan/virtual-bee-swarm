using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.ArFirst
{
    /// <summary>
    /// Footage: makes the Capture tool's stand-in room a photograph of a real room, so desktop footage reads as
    /// passthrough (the game is AR: the room is never CG on the headset). Only in the capture scene (a ShowCapture is
    /// in it) and only with env VBS_CAPTURE_BACKPLATE=&lt;folder&gt;:
    ///   pano.jpg   an equirectangular photo of a room (a phone panorama of the venue table spot, or a CC0 interior)
    ///   plate.txt  optional key=value lines, distances in units of the panorama's eye height:
    ///                az=307.5   panorama azimuth (degrees, 0 = left edge, clockwise) that faces the game's window wall
    ///                eye=2.0    world height (m) the panorama's eye point is put at (scales the photographed room)
    ///                front=1.05 back=0.99 left=1.30 right=1.66 ceil=1.68   the room's walls / ceiling from the eye point
    ///                x=0        world x of the eye point (the table is at x = 0)
    /// The photo is projected from its eye point onto a box the size of the photographed room (RoomBackplate.shader), so
    /// both capture cameras see the same real floor and walls with true parallax, flowers stand on the photographed
    /// floor and the window hangs on the photographed wall. ShowCapture's CG walls / floor / rug are hidden; its table
    /// (the "real" table) stays, with a soft shadow on the floor and daylight from the photo's window side.
    /// Also takes both passthrough dims so footage shows them: the stakes dim (PassthroughDim.Applied) and the Moon's
    /// (IPassthroughDimmer, which ShowCapture doesn't implement).
    /// Nothing here runs on the headset.
    /// </summary>
    public class CaptureBackplate : MonoBehaviour, IPassthroughDimmer
    {
        const string Env = "VBS_CAPTURE_BACKPLATE";
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        string _dir;
        Material _mat;
        float _moon;
        [SerializeField, Range(0f, 1f)] float minOpacity = 0.35f; // ArFirstDirector raises it with arfirst.minOpacity

        static readonly int DimId = Shader.PropertyToID("_Dim");
        static readonly int SatId = Shader.PropertyToID("_Sat");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            string dir = null;
            try { dir = Environment.GetEnvironmentVariable(Env); } catch (Exception) { }
            if (string.IsNullOrEmpty(dir)) return;
            Component capture = null;
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (mb != null && mb.GetType().Name == "ShowCapture") { capture = mb; break; }
            if (capture == null) return; // the game scene: the room is the player's
            if (!File.Exists(Path.Combine(dir, "pano.jpg")))
            {
                Debug.LogWarning("[VBS][ArFirst] " + Env + "=" + dir + " has no pano.jpg: the stand-in room stays");
                return;
            }
            var go = new GameObject("CaptureBackplate");
            go.AddComponent<CaptureBackplate>()._dir = dir;
        }

        public void SetDim(float amount) => _moon = Mathf.Clamp01(amount);

        IEnumerator Start()
        {
            // ShowCapture builds its room in its own Start
            GameObject room = null;
            for (int i = 0; i < 300 && room == null; i++)
            {
                room = GameObject.Find("CaptureRoom");
                if (room == null) yield return null;
            }
            if (room == null) { Debug.LogWarning("[VBS][ArFirst] backplate: no CaptureRoom"); yield break; }

            var cfg = new Dictionary<string, float>
            {
                { "az", 307.5f }, { "eye", 2.0f }, { "front", 1.05f }, { "back", 0.99f }, { "left", 1.30f }, { "right", 1.66f }, { "ceil", 1.68f }, { "x", 0f },
            };
            string plate = Path.Combine(_dir, "plate.txt");
            if (File.Exists(plate))
                foreach (var line in File.ReadAllLines(plate))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    if (float.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, C, out var f)) cfg[line.Substring(0, eq).Trim()] = f;
                }

            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "VBS_BackplatePano", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            if (!tex.LoadImage(File.ReadAllBytes(Path.Combine(_dir, "pano.jpg")), true))
            {
                Debug.LogWarning("[VBS][ArFirst] backplate: pano.jpg didn't load");
                yield break;
            }
            var template = Resources.Load<Material>("VBS_RoomBackplate");
            var shader = template != null ? template.shader : Shader.Find("VirtualBeeSwarm/RoomBackplate");
            if (shader == null) { Debug.LogWarning("[VBS][ArFirst] backplate: no RoomBackplate shader in this build"); yield break; }
            _mat = new Material(shader) { name = "VBS_RoomBackplate (runtime)" };
            _mat.SetTexture("_MainTex", tex);

            // where ShowCapture put the wall and the table
            float wallZ = 2f, tableX = 0f, tableZ = 0.1f, tableY = 0.75f;
            Vector2 tableHalf = new Vector2(0.7f, 0.4f);
            foreach (Transform t in room.transform)
            {
                var r = t.GetComponent<Renderer>();
                switch (t.name)
                {
                    case "BackWall": wallZ = t.localPosition.z - t.localScale.z * 0.5f; break;
                    case "TableTop":
                        tableX = t.localPosition.x; tableZ = t.localPosition.z; tableY = t.localPosition.y + t.localScale.y * 0.5f;
                        tableHalf = new Vector2(t.localScale.x * 0.5f, t.localScale.z * 0.5f);
                        break;
                }
                if (r != null && (t.name == "Floor" || t.name == "Ceiling" || t.name.EndsWith("Wall", StringComparison.Ordinal) || t.name == "Baseboard" || t.name == "Rug"))
                    r.enabled = false;
            }
            float eye = cfg["eye"];
            Vector3 origin = new Vector3(tableX + cfg["x"], eye, wallZ - cfg["front"] * eye);
            float x0 = origin.x - cfg["left"] * eye, x1 = origin.x + cfg["right"] * eye;
            float z0 = origin.z - cfg["back"] * eye, z1 = wallZ, y1 = cfg["ceil"] * eye;
            _mat.SetVector("_Origin", origin);
            _mat.SetFloat("_Yaw", cfg["az"] * Mathf.Deg2Rad);

            var box = new GameObject("PhotoRoom");
            box.transform.SetParent(room.transform, false);
            box.AddComponent<MeshFilter>().sharedMesh = InwardBox(new Vector3(x0, 0f, z0), new Vector3(x1, y1, z1));
            var mr = box.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // the table's soft shadow on the photographed floor (the photo's daylight comes from the left wall)
            var shade = GameObject.CreatePrimitive(PrimitiveType.Quad);
            shade.name = "TableShade";
            Destroy(shade.GetComponent<Collider>());
            shade.transform.SetParent(room.transform, false);
            shade.transform.localPosition = new Vector3(tableX + 0.22f, 0.004f, tableZ + 0.05f);
            shade.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shade.transform.localScale = new Vector3(tableHalf.x * 2f + 1.0f, tableHalf.y * 2f + 0.9f, 1f);
            var sm = GameMaterials.Create("VBS_TableShade (capture)", new Color(0f, 0f, 0f, 0.55f), GameMaterials.Blend.Alpha, doubleSided: true);
            sm.SetTexture(GameMaterials.MainTexId, SoftRect());
            sm.renderQueue = (int)RenderQueue.Transparent - 20;
            var sr = shade.GetComponent<Renderer>();
            sr.sharedMaterial = sm;
            sr.shadowCastingMode = ShadowCastingMode.Off;

            // daylight like the photo's instead of the warm evening lamp
            foreach (var l in room.GetComponentsInChildren<Light>(true))
            {
                if (l.type == LightType.Directional)
                {
                    l.transform.rotation = Quaternion.Euler(46f, 72f, 0f);
                    l.color = new Color(1f, 0.97f, 0.93f);
                    l.intensity = 0.9f;
                    l.shadowStrength = 0.35f;
                }
                else
                {
                    l.color = new Color(0.92f, 0.95f, 1f);
                    l.intensity = 0.25f;
                }
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.66f, 0.66f, 0.68f);
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(RenderSettings.ambientLight);
            RenderSettings.ambientProbe = sh;

            // keep the wide camera inside the photographed room
            var wide = GameObject.Find("WideCamera");
            if (wide != null)
            {
                Vector3 p = wide.transform.position;
                Vector3 q = new Vector3(Mathf.Clamp(p.x, x0 + 0.25f, x1 - 0.25f), Mathf.Min(p.y, y1 - 0.2f), Mathf.Clamp(p.z, z0 + 0.25f, z1 - 0.25f));
                if (q != p) wide.transform.position = q;
            }
            Debug.Log("[VBS][ArFirst] backplate: " + tex.width + "x" + tex.height + " panorama from " + _dir + ", photographed room x " +
                      x0.ToString("0.00", C) + ".." + x1.ToString("0.00", C) + ", z " + z0.ToString("0.00", C) + ".." + z1.ToString("0.00", C) +
                      ", ceiling " + y1.ToString("0.00", C) + " m, eye " + origin.ToString("F2"));
        }

        void LateUpdate()
        {
            if (_mat == null) return;
            float stakes = Mathf.Clamp01(PassthroughDim.Applied);
            // the headset's stakes dim takes 0.8 x dim off brightness and saturation; the Moon lowers passthrough opacity
            _mat.SetFloat(DimId, (1f - 0.8f * stakes) * Mathf.Lerp(1f, minOpacity, _moon));
            _mat.SetFloat(SatId, 1f - 0.8f * stakes);
        }

        /// <summary>A box seen from the inside: 6 quads, 24 vertices, no normals needed (unlit, projected).</summary>
        static Mesh InwardBox(Vector3 a, Vector3 b)
        {
            var v = new List<Vector3>(24);
            var t = new List<int>(36);
            void Face(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            Face(new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z)); // floor
            Face(new Vector3(a.x, b.y, a.z), new Vector3(a.x, b.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z)); // ceiling
            Face(new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z)); // front (+z)
            Face(new Vector3(b.x, a.y, a.z), new Vector3(a.x, a.y, a.z), new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z)); // back (-z)
            Face(new Vector3(a.x, a.y, a.z), new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, b.y, a.z)); // left
            Face(new Vector3(b.x, a.y, b.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z)); // right
            var m = new Mesh { name = "VBS_PhotoRoom" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        static Texture2D SoftRect()
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "VBS_SoftRect" };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = Mathf.Abs((x + 0.5f) / N * 2f - 1f), dy = Mathf.Abs((y + 0.5f) / N * 2f - 1f);
                    float a = Mathf.SmoothStep(1f, 0.45f, dx) * Mathf.SmoothStep(1f, 0.45f, dy);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
