using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// The AR switches from tuning.json (PLAN.md 3.5 / S2.3). Every AR piece reads its flag here, so the venue can turn
    /// it off without a rebuild:
    ///   ar.placement        raycast | manual        aim-and-pinch (EnvironmentRaycastManager), or only the old grab/two-point
    ///   ar.heightSnap       true | false            snap the hive height to the real table after any placement
    ///   ar.windowOnWall     true | false            meadow window on the real wall (else free-standing 0.7 m behind the hive);
    ///                                               false = today's layout (window on the table)
    ///   ar.occlusion        off | hard | soft       Depth API occlusion (EnvironmentDepthManager), off until the 13:00 A/B
    ///   ar.depthBiasContact 0.06                    depth bias on things that touch a real surface
    ///   ar.landing          true | false            bees land on the real table between beats
    ///   ar.shadows          true | false            blob contact shadows
    ///   ar.ceilingY         2.4                     ceiling height when nothing measured it
    ///   ar.tableDepth       0.6                     table depth assumed behind the placed front edge
    ///   ar.wallMaxDistance  3.0                     a wall farther than this from the hive is ignored
    /// Sources, later wins: built-in defaults, StreamingAssets/tuning.json (desktop/editor only), then
    /// persistentDataPath/tuning.json (adb push on the Quest, same file S2.3 uses), then Set() calls (the S2.3 tuning
    /// system or the Hive's `tuning` message forward them here). Accepts {"ar":{"occlusion":"soft"}} and
    /// {"ar.occlusion":"soft"}; keys outside "ar" are ignored. Never throws: a bad file logs one warning.
    /// </summary>
    public static class ArTuning
    {
        public enum Placement { Raycast, Manual }
        public enum Occlusion { Off, Hard, Soft }

        public static Placement PlacementMode { get; private set; } = Placement.Raycast;
        public static bool HeightSnap { get; private set; } = true;
        public static bool WindowOnWall { get; private set; } = true;
        public static Occlusion OcclusionMode { get; private set; } = Occlusion.Off;
        public static float DepthBiasContact { get; private set; } = 0.06f;
        public static bool Landing { get; private set; } = true;
        public static bool Shadows { get; private set; } = true;
        public static float CeilingY { get; private set; } = 2.4f;
        public static float TableDepth { get; private set; } = 0.6f;
        public static float WallMaxDistance { get; private set; } = 3f;

        /// <summary>Raised after Load() or Set() changed anything.</summary>
        public static event Action Changed;

        public static string LoadedFrom { get; private set; } = "defaults";

        static bool _loaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            Changed = null;
            ApplyDefaults();
        }

        static void ApplyDefaults()
        {
            PlacementMode = Placement.Raycast;
            HeightSnap = true;
            WindowOnWall = true;
            OcclusionMode = Occlusion.Off;
            DepthBiasContact = 0.06f;
            Landing = true;
            Shadows = true;
            CeilingY = 2.4f;
            TableDepth = 0.6f;
            WallMaxDistance = 3f;
            LoadedFrom = "defaults";
        }

        public static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        /// <summary>Re-reads the files (the Board's "reload tuning" can call this).</summary>
        public static void Load()
        {
            _loaded = true;
            ApplyDefaults();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            TryFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"), from);
#endif
            TryFile(Path.Combine(Application.persistentDataPath, "tuning.json"), from);
            if (from.Count > 0) LoadedFrom = string.Join(" + ", from);
            Changed?.Invoke();
        }

        static void TryFile(string path, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                var flat = new Dictionary<string, string>();
                FlatJson.Parse(File.ReadAllText(path), flat);
                int n = 0;
                foreach (var kv in flat)
                    if (kv.Key.StartsWith("ar.", StringComparison.Ordinal) && Apply(kv.Key, kv.Value)) n++;
                from.Add(path + " (" + n + " ar keys)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][AR] tuning: could not read " + path + ": " + e.Message);
            }
        }

        /// <summary>Live override, e.g. Set("ar.occlusion", "soft"). Returns false for an unknown key or bad value.</summary>
        public static bool Set(string key, string value)
        {
            EnsureLoaded();
            if (key == null) return false;
            if (!key.StartsWith("ar.", StringComparison.Ordinal)) key = "ar." + key;
            bool ok = Apply(key, value);
            if (ok) Changed?.Invoke();
            else Debug.LogWarning("[VBS][AR] tuning: ignored " + key + "=" + value);
            return ok;
        }

        static bool Apply(string key, string v)
        {
            v = (v ?? "").Trim();
            switch (key)
            {
                case "ar.placement":
                    if (Eq(v, "raycast") || Eq(v, "auto")) { PlacementMode = Placement.Raycast; return true; } // auto (MRUK) isn't built: raycast
                    if (Eq(v, "manual")) { PlacementMode = Placement.Manual; return true; }
                    return false;
                case "ar.occlusion":
                    if (Eq(v, "off") || Eq(v, "false") || Eq(v, "none")) { OcclusionMode = Occlusion.Off; return true; }
                    if (Eq(v, "hard")) { OcclusionMode = Occlusion.Hard; return true; }
                    if (Eq(v, "soft") || Eq(v, "true")) { OcclusionMode = Occlusion.Soft; return true; }
                    return false;
                case "ar.heightSnap": return Bool(v, b => HeightSnap = b);
                case "ar.windowOnWall": return Bool(v, b => WindowOnWall = b);
                case "ar.landing": return Bool(v, b => Landing = b);
                case "ar.shadows": return Bool(v, b => Shadows = b);
                case "ar.depthBiasContact": return Num(v, 0f, 0.5f, f => DepthBiasContact = f);
                case "ar.ceilingY": return Num(v, 1.5f, 6f, f => CeilingY = f);
                case "ar.tableDepth": return Num(v, 0.2f, 2f, f => TableDepth = f);
                case "ar.wallMaxDistance": return Num(v, 0.5f, 8f, f => WallMaxDistance = f);
                default: return false;
            }
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool Bool(string v, Action<bool> set)
        {
            if (Eq(v, "true") || Eq(v, "on") || v == "1") { set(true); return true; }
            if (Eq(v, "false") || Eq(v, "off") || v == "0") { set(false); return true; }
            return false;
        }

        static bool Num(string v, float min, float max, Action<float> set)
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || f < min || f > max) return false;
            set(f);
            return true;
        }

        public static string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return "placement=" + PlacementMode.ToString().ToLowerInvariant() + " heightSnap=" + OnOff(HeightSnap) +
                   " windowOnWall=" + OnOff(WindowOnWall) + " occlusion=" + OcclusionMode.ToString().ToLowerInvariant() +
                   " depthBiasContact=" + DepthBiasContact.ToString("0.###", c) + " landing=" + OnOff(Landing) +
                   " shadows=" + OnOff(Shadows) + " ceilingY=" + CeilingY.ToString("0.##", c) +
                   " tableDepth=" + TableDepth.ToString("0.##", c) + " wallMaxDistance=" + WallMaxDistance.ToString("0.##", c) +
                   " (from " + LoadedFrom + ")";
        }

        static string OnOff(bool b) => b ? "on" : "off";

        /// <summary>Tiny JSON reader: objects flatten to dotted keys, scalars become strings, arrays are skipped.</summary>
        public static class FlatJson
        {
            public static void Parse(string json, Dictionary<string, string> into)
            {
                int i = 0;
                Skip(json, ref i);
                if (i >= json.Length || json[i] != '{') throw new FormatException("not a JSON object");
                Obj(json, ref i, "", into);
            }

            static void Obj(string s, ref int i, string prefix, Dictionary<string, string> into)
            {
                i++; // {
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return; }
                while (i < s.Length)
                {
                    Skip(s, ref i);
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                    i++;
                    Skip(s, ref i);
                    string full = prefix.Length > 0 ? prefix + "." + key : key;
                    Value(s, ref i, full, into);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return; }
                    throw new FormatException("expected ',' or '}' at " + i);
                }
                throw new FormatException("unterminated object");
            }

            static void Value(string s, ref int i, string key, Dictionary<string, string> into)
            {
                char c = s[i];
                if (c == '{') { Obj(s, ref i, key, into); return; }
                if (c == '[') { SkipArray(s, ref i); return; }
                if (c == '"') { into[key] = Str(s, ref i); return; }
                int start = i;
                while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
                into[key] = s.Substring(start, i - start);
            }

            static void SkipArray(string s, ref int i)
            {
                int depth = 0;
                bool inStr = false;
                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                    if (c == '"') inStr = true;
                    else if (c == '[' || c == '{') depth++;
                    else if ((c == ']' || c == '}') && --depth == 0) { i++; return; }
                }
            }

            static string Str(string s, ref int i)
            {
                if (s[i] != '"') throw new FormatException("expected a string at " + i);
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        char e = s[i];
                        sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                i++; // closing quote
                return sb.ToString();
            }

            static void Skip(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }
        }
    }
}
