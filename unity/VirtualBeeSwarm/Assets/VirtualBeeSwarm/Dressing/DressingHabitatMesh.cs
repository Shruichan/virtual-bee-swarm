using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm;

namespace VirtualBeeSwarm.Dressing
{
    /// <summary>Small, one-time OBJ/MTL reader for the optional pollinator habitat dressing.</summary>
    internal static class DressingHabitatMesh
    {
        internal static GameObject Create(string label, TextAsset objAsset, TextAsset mtlAsset, Transform parent,
            Vector3 localPosition, Quaternion localRotation)
        {
            if (objAsset == null || string.IsNullOrWhiteSpace(objAsset.text))
            {
                Debug.LogWarning(DressingDirector.Tag + label + " skipped (OBJ resource missing)");
                return null;
            }

            Mesh mesh;
            Material[] materials;
            string error;
            if (!TryParse(label, objAsset.text, mtlAsset != null ? mtlAsset.text : "", out mesh, out materials, out error))
            {
                Debug.LogWarning(DressingDirector.Tag + label + " skipped (" + error + ")");
                return null;
            }

            var go = new GameObject("[VBS] " + label);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }

        static bool TryParse(string name, string obj, string mtl, out Mesh mesh, out Material[] materials, out string error)
        {
            mesh = null;
            materials = null;
            error = "invalid OBJ";
            var vertices = new List<Vector3>();
            var order = new List<string>();
            var triangles = new Dictionary<string, List<int>>();
            string current = "default";

            try
            {
                using (var reader = new StringReader(obj))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] p = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length == 0 || p[0].StartsWith("#", StringComparison.Ordinal)) continue;
                        if (p[0] == "v" && p.Length >= 4)
                        {
                            vertices.Add(new Vector3(Number(p[1]), Number(p[2]), Number(p[3])));
                        }
                        else if (p[0] == "usemtl" && p.Length >= 2)
                        {
                            current = p[1];
                        }
                        else if (p[0] == "f" && p.Length >= 4)
                        {
                            List<int> group;
                            if (!triangles.TryGetValue(current, out group))
                            {
                                group = new List<int>();
                                triangles.Add(current, group);
                                order.Add(current);
                            }
                            var face = new List<int>(p.Length - 1);
                            for (int i = 1; i < p.Length; i++)
                            {
                                string token = p[i].Split('/')[0];
                                int index = int.Parse(token, CultureInfo.InvariantCulture);
                                index = index < 0 ? vertices.Count + index : index - 1;
                                if (index < 0 || index >= vertices.Count) throw new FormatException("face index out of range");
                                face.Add(index);
                            }
                            for (int i = 1; i + 1 < face.Count; i++)
                            {
                                group.Add(face[0]); group.Add(face[i]); group.Add(face[i + 1]);
                            }
                        }
                    }
                }

                if (vertices.Count == 0 || order.Count == 0) throw new FormatException("no mesh faces");
                var colors = ParseMaterials(mtl);
                mesh = new Mesh { name = name };
                mesh.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(vertices);
                mesh.subMeshCount = order.Count;
                materials = new Material[order.Count];
                for (int i = 0; i < order.Count; i++)
                {
                    Color color;
                    if (!colors.TryGetValue(order[i], out color)) color = new Color(0.72f, 0.66f, 0.51f, 1f);
                    mesh.SetTriangles(triangles[order[i]], i, false);
                    materials[i] = GameMaterials.Create(name + " " + order[i], color, emission: 1f, instancing: true, doubleSided: true);
                }
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                error = null;
                return true;
            }
            catch (Exception e)
            {
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
                if (materials != null)
                    foreach (var material in materials) if (material != null) UnityEngine.Object.Destroy(material);
                mesh = null;
                materials = null;
                error = e.Message;
                return false;
            }
        }

        static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);

        static Dictionary<string, Color> ParseMaterials(string mtl)
        {
            var colors = new Dictionary<string, Color>();
            string current = null;
            using (var reader = new StringReader(mtl ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] p = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 0 || p[0].StartsWith("#", StringComparison.Ordinal)) continue;
                    if (p[0] == "newmtl" && p.Length >= 2)
                    {
                        current = p[1];
                        colors[current] = Color.white;
                    }
                    else if (p[0] == "Kd" && p.Length >= 4 && current != null)
                    {
                        colors[current] = new Color(Number(p[1]), Number(p[2]), Number(p[3]), 1f);
                    }
                }
            }
            return colors;
        }
    }
}
