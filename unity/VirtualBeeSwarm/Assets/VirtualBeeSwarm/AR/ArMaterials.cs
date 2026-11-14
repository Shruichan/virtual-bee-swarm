using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Per-material occlusion settings (PLAN.md 3.2 "Occlusion rules"), applied at runtime so no .mat file or the
    /// builder has to change. They only do something once GameUnlit.shader has the occlusion edit (it adds the
    /// `_Occlude` toggle and `_EnvironmentDepthBias`); without it every call is a no-op.
    ///   never occluded (_Occlude = 0): the window (frame, glow, panes), markers, pointer rays, blob shadows, dust,
    ///                                  the ghost plate. Captions use the font shader, so they never are.
    ///   contact bias (ar.depthBiasContact, 0.06): the base plate, hive, stand/roof, rocket, set-down props.
    ///   everything else: occluded, bias 0 (the shader defaults).
    /// In the editor the materials are cloned per renderer first, so play mode never writes to the tracked .mat files.
    /// </summary>
    public static class ArMaterials
    {
        public static readonly int OccludeId = Shader.PropertyToID("_Occlude");
        public static readonly int BiasId = Shader.PropertyToID("_EnvironmentDepthBias");

        static readonly string[] NeverOccluded =
        {
            "VBS_Portal", "VBS_PortalGlow", "VBS_Pane", "VBS_PaneLive", "VBS_Marker", "VBS_Ray",
        };

        static readonly string[] Contact =
        {
            "VBS_Plate", "VBS_Hive", "VBS_HiveRoof", "VBS_Rocket", "VBS_RocketRed", "VBS_Metal", "VBS_Honey", "VBS_Syrup",
            "VBS_Pebble", "VBS_Water", "VBS_Glass", "VBS_ThermoRed", "VBS_ThermoFill", "VBS_Hex",
        };

        public static bool ShaderSupportsOcclusion
        {
            get
            {
                var s = Shader.Find(GameMaterials.ShaderName);
                if (s == null) return false;
                return s.FindPropertyIndex("_Occlude") >= 0 && s.FindPropertyIndex("_EnvironmentDepthBias") >= 0;
            }
        }

        public static void MarkNeverOccluded(Material m)
        {
            if (m != null && m.HasProperty(OccludeId)) m.SetFloat(OccludeId, 0f);
        }

        public static void SetContactBias(Material m, float bias)
        {
            if (m != null && m.HasProperty(BiasId)) m.SetFloat(BiasId, bias);
        }

        static readonly Dictionary<Material, Material> Clones = new Dictionary<Material, Material>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clones.Clear();

        /// <summary>Applies the rules to every renderer under `root` (call once after the scene is up, and again after
        /// ar.depthBiasContact changes). Returns how many materials it set.</summary>
        public static int Apply(Transform root, float contactBias)
        {
            if (root == null || !ShaderSupportsOcclusion) return 0;
            int n = 0;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var seen = new HashSet<Material>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool swapped = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || m.shader == null || m.shader.name != GameMaterials.ShaderName) continue;
                    int rule = Rule(BaseName(m.name));
                    if (rule == 0) continue;
                    if (Application.isEditor)
                    {
                        if (!Clones.TryGetValue(m, out var clone))
                        {
                            clone = new Material(m) { name = m.name + " (AR)" };
                            Clones[m] = clone;
                        }
                        mats[i] = clone;
                        m = clone;
                        swapped = true;
                    }
                    if (!seen.Add(m)) continue;
                    if (rule == 1) MarkNeverOccluded(m);
                    else SetContactBias(m, contactBias);
                    n++;
                }
                if (swapped) r.sharedMaterials = mats;
            }
            return n;
        }

        static string BaseName(string name)
        {
            int k = name.IndexOf(" (", StringComparison.Ordinal);
            return k > 0 ? name.Substring(0, k) : name;
        }

        /// <summary>0 = defaults, 1 = never occluded, 2 = contact bias.</summary>
        static int Rule(string name)
        {
            for (int i = 0; i < NeverOccluded.Length; i++) if (name == NeverOccluded[i]) return 1;
            for (int i = 0; i < Contact.Length; i++) if (name == Contact[i]) return 2;
            return 0;
        }
    }
}
