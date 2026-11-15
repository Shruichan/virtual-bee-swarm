using System;
using UnityEngine;

namespace VirtualBeeSwarm.EnvironmentArt
{
    /// <summary>
    /// Everything the environment pass draws, in one asset (Environment/Resources/VBS_EnvironmentKit.asset) so the
    /// self-installing EnvironmentDirector needs no scene edits. Made by Environment/Editor/EnvironmentKitBuilder.cs
    /// from Art/Environment/environment.fbx + environment_layout.json (art/blender/scripts/environment.py).
    /// </summary>
    public class EnvironmentKit : ScriptableObject
    {
        public const string ResourceName = "VBS_EnvironmentKit";

        [Header("Meshes (environment.fbx)")]
        public Mesh island;
        public Mesh moonGround;
        public Mesh portalVines;
        public Mesh shafts;
        public Mesh starsA, starsB;
        public Mesh earthGlow;
        public Mesh petal;
        [Tooltip("Bloom_Daisy, Bloom_Cosmos, Bloom_Cornflower, Bloom_Lupine, Bloom_Sunflower")]
        public Mesh[] blooms = new Mesh[0];

        [Header("Materials (Art/Environment)")]
        [Tooltip("Opaque vertex colour, contact depth bias (island, Moon ground).")]
        public Material ground;
        [Tooltip("Opaque vertex colour (flowers, vines, petals).")]
        public Material vc;
        [Tooltip("Additive vertex colour, both sides (light shafts, Earth glow).")]
        public Material glow;
        [Tooltip("Additive vertex colour, never hidden by the real room (stars).")]
        public Material stars;

        [Header("Layout (environment_layout.json)")]
        public TextAsset layout;

        public Mesh Bloom(string kind)
        {
            string name = "Bloom_" + kind;
            foreach (var m in blooms)
                if (m != null && m.name == name) return m;
            return null;
        }

        [Serializable]
        public class BloomSpot
        {
            public string kind;
            public float x, y, z, yaw, scale = 1f;
            public bool extra;
        }

        [Serializable]
        class Layout
        {
            public BloomSpot[] blooms;
        }

        /// <summary>Blender coordinates (x right, y away from the player, z up) around the hive's centre.</summary>
        public BloomSpot[] BloomSpots()
        {
            if (layout == null) return new BloomSpot[0];
            try
            {
                var l = JsonUtility.FromJson<Layout>(layout.text);
                return l != null && l.blooms != null ? l.blooms : new BloomSpot[0];
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][Env] bad environment_layout.json: " + e.Message);
                return new BloomSpot[0];
            }
        }
    }
}
