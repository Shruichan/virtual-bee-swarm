using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The floor meadow switches (tuning.json meadow.*, same files and order as BloomTuning), then env
    /// VBS_MEADOW_FLAGS="meadow.density=0.6,meadow.rigScale=2.5". Read at start and on every re-layout.
    ///   meadow.on           true   the whole-floor meadow from the title screen to the end card
    ///   meadow.density      1.2    0.2..1.5 blades per tile (1 = ~250 blades + 22 flowers per 0.45 m tile)
    ///   meadow.extent       5      m, the square meadow when there's no room scan (clipped to a known wall)
    ///   meadow.mruk         true   use the headset's room scan (MRUK) floor outline, walls + furniture when it has a room
    ///   meadow.bees         true   bees working the flowers (more as the hive gets better; comfort: fewer)
    ///   meadow.walls        true   vines, climbing flowers and leaves on the walls all ride long (short while sick)
    ///   meadow.rig          true   no table: the whole hive rig (TableAnchor) is scaled up and stands on the floor
    ///                              (off: the old table layout, untouched). Alias: meadow.stand
    ///   meadow.rigScale     3.0    how much bigger the rig is (the hive body is then ~0.3-1.2 m above the floor)
    ///   meadow.hiveStand    0.3    m, the hive stand (legs) under the hive box
    ///   meadow.choiceHeight 1.25   m above the floor of the price-tag choices (on garden stakes) and START
    ///   meadow.distance     0.45   m from the player's eyes to the choice row at the automatic start placement
    ///   meadow.yard         0.7    plant height kept in the mown yard round the hive and its props (0..1)
    ///   meadow.roomBees     300    bees working the flowers on the floor AND the wall vines all over the room (0..600, low-poly bee)
    ///   meadow.rocketHeight 1.45   m, the rocket's height beside the hive (0 = rig scale only)
    ///   meadow.rocketX      1.33   m right of the rig origin; meadow.rocketZ 1.06 m forward (beside the hive, whole in the POV)
    ///   meadow.windowWidth  1.9    m, the free-floating meadow portal behind the hive
    ///   meadow.propSize     0.45   m, the hand props (smoker, jar, ...) grow toward this size (largest side)
    ///   meadow.propScale    2      ... by at most this much on top of the rig scale
    ///   meadow.wallStart    2.2    m the wall growth reaches while the hive is sick (thinner and paler; to the ceiling at the escape)
    /// Bee counts (same files; the instanced bee is one draw per pool):
    ///   swarm.count         500    the hive's own swarm (SwarmSystem, 20..640; a third roam the room when calm)
    ///   darkswarm.count     400    the dark swarm over the room (DarkSwarm, 20..600; stakes sizes scale with it)
    ///   crew.scale          2.5    delivery crews x this (BeeDelivery, 1..3)
    ///   swarm.vatNear       80     per bee pool, only this many bees (the nearest, within 2.5 m) draw the animated VAT bee
    ///                              (~560 tris); the rest draw the low-poly bee (~100 tris). BeeLod.
    /// </summary>
    public static class MeadowTuning
    {
        public static bool On = true;
        public static float Density = 1.2f;
        public static float Extent = 5f;
        public static bool Mruk = true;
        public static bool Bees = true;
        public static bool Walls = true;
        public static bool Rig = true;
        public static float RigScale = 3.0f;
        public static float HiveStand = 0.3f;
        public static float ChoiceHeight = 1.25f;
        public static float Distance = 0.45f;
        public static float Yard = 0.7f;
        public static int RoomBees = 300;
        public static float RocketX = 1.33f, RocketZ = 1.06f;
        public static float RocketHeight = 1.45f, WindowWidth = 1.9f, PropScale = 2f, PropSize = 0.45f, WallStart = 2.2f;
        public static int SwarmCount = 500, DarkCount = 400, VatNear = 80;
        public static float CrewScale = 2.5f;
        public static string LoadedFrom = "defaults";

        /// <summary>Old name, kept for callers: the free-standing (no table) layout.</summary>
        public static bool Stand => Rig;

        static bool _loaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            Defaults();
        }

        static void Defaults()
        {
            On = true;
            Density = 1.2f;
            Extent = 5f;
            Mruk = true;
            Bees = true;
            Walls = true;
            Rig = true;
            RigScale = 3.0f;
            HiveStand = 0.3f;
            ChoiceHeight = 1.25f;
            Distance = 0.45f;
            Yard = 0.7f;
            RoomBees = 300;
            RocketHeight = 1.45f;
            RocketX = 1.33f;
            RocketZ = 1.06f;
            WindowWidth = 1.9f;
            PropScale = 2f;
            PropSize = 0.45f;
            WallStart = 2.2f;
            SwarmCount = 500;
            DarkCount = 400;
            VatNear = 80;
            CrewScale = 2.5f;
            LoadedFrom = "defaults";
            if (QuestPerf.On)
            {
                // the headset (72 fps): a lighter meadow and fewer bees; far bees are the low-poly bee (BeeLod)
                Density = 0.8f;
                RoomBees = 120;
                SwarmCount = 260;
                DarkCount = 220;
                VatNear = 32;
                CrewScale = 1.5f;
                LoadedFrom = "quest defaults";
            }
        }

        public static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        public static void Load()
        {
            _loaded = true;
            Defaults();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            TryFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"), from);
#endif
            TryFile(Path.Combine(Application.persistentDataPath, "tuning.json"), from);
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_MEADOW_FLAGS"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env))
            {
                int n = 0;
                foreach (var pair in env.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && Apply(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1))) n++;
                }
                from.Add("env VBS_MEADOW_FLAGS (" + n + ")");
            }
            if (from.Count > 0) LoadedFrom = string.Join(" + ", from);
        }

        static void TryFile(string path, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                var flat = new Dictionary<string, string>();
                ArTuning.FlatJson.Parse(File.ReadAllText(path), flat);
                int n = 0;
                foreach (var kv in flat)
                    if ((kv.Key.StartsWith("meadow.", StringComparison.Ordinal) || kv.Key == "swarm.count" || kv.Key == "darkswarm.count" || kv.Key == "swarm.vatNear" ||
                         kv.Key == "crew.scale") && Apply(kv.Key, kv.Value)) n++;
                if (n > 0) from.Add(path + " (" + n + " meadow keys)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][Meadow] tuning: could not read " + path + ": " + e.Message);
            }
        }

        static bool Apply(string key, string v)
        {
            v = (v ?? "").Trim().Trim('"');
            switch (key)
            {
                case "swarm.count": return Int(v, 20, 640, ref SwarmCount);
                case "darkswarm.count": return Int(v, 20, 600, ref DarkCount);
                case "swarm.vatNear": return Int(v, 0, 640, ref VatNear);
                case "crew.scale": return Num(v, 1f, 3f, ref CrewScale);
            }
            if (!key.StartsWith("meadow.", StringComparison.Ordinal)) key = "meadow." + key;
            switch (key)
            {
                case "meadow.roomBees": return Int(v, 0, 600, ref RoomBees);
                case "meadow.rocketHeight": return Num(v, 0f, 2.2f, ref RocketHeight);
                case "meadow.rocketX": return Num(v, 0f, 3f, ref RocketX);
                case "meadow.rocketZ": return Num(v, -1f, 3f, ref RocketZ);
                case "meadow.windowWidth": return Num(v, 0.8f, 2.4f, ref WindowWidth);
                case "meadow.propScale": return Num(v, 1f, 3f, ref PropScale);
                case "meadow.propSize": return Num(v, 0.1f, 0.8f, ref PropSize);
                case "meadow.wallStart": return Num(v, 0.3f, 2.8f, ref WallStart);
                case "meadow.on": return Bool(v, ref On);
                case "meadow.mruk": return Bool(v, ref Mruk);
                case "meadow.bees": return Bool(v, ref Bees);
                case "meadow.walls": return Bool(v, ref Walls);
                case "meadow.rig":
                case "meadow.stand": return Bool(v, ref Rig);
                case "meadow.density": return Num(v, 0.2f, 1.5f, ref Density);
                case "meadow.extent": return Num(v, 1f, 12f, ref Extent);
                case "meadow.rigScale": return Num(v, 1f, 4f, ref RigScale);
                case "meadow.hiveStand": return Num(v, 0f, 0.6f, ref HiveStand);
                case "meadow.choiceHeight": return Num(v, 0.6f, 1.4f, ref ChoiceHeight);
                case "meadow.distance": return Num(v, 0.3f, 1.5f, ref Distance);
                case "meadow.yard": return Num(v, 0.05f, 1f, ref Yard);
                default: return false;
            }
        }

        static bool Bool(string v, ref bool into)
        {
            switch (v.ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": into = true; return true;
                case "0": case "off": case "false": case "no": into = false; return true;
                default: return false;
            }
        }

        static bool Int(string v, int min, int max, ref int into)
        {
            float f = into;
            if (!Num(v, min, max, ref f)) return false;
            into = Mathf.RoundToInt(f);
            return true;
        }

        static bool Num(string v, float min, float max, ref float into)
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false;
            into = Mathf.Clamp(f, min, max);
            return true;
        }

        public static string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return "on=" + On + " density=" + Density.ToString("0.##", c) + " extent=" + Extent.ToString("0.#", c) + " mruk=" + Mruk +
                   " bees=" + Bees + " walls=" + Walls + " rig=" + Rig + " rigScale=" + RigScale.ToString("0.##", c) + " hiveStand=" +
                   HiveStand.ToString("0.00", c) + " choiceHeight=" + ChoiceHeight.ToString("0.00", c) + " distance=" +
                   Distance.ToString("0.00", c) + " yard=" + Yard.ToString("0.00", c) + " roomBees=" + RoomBees + " rocket=" + RocketHeight.ToString("0.0", c) + " at " + RocketX.ToString("0.00", c) + "," + RocketZ.ToString("0.00", c) +
                   " window=" + WindowWidth.ToString("0.0", c) + " props=" + PropSize.ToString("0.00", c) + "/x" + PropScale.ToString("0.0", c) + " wallStart=" + WallStart.ToString("0.0", c) +
                   " | swarm.count=" + SwarmCount + " darkswarm.count=" + DarkCount + " crew.scale=" + CrewScale.ToString("0.0", c) + " vatNear=" + VatNear + " (from " + LoadedFrom + ")";
        }
    }
}
