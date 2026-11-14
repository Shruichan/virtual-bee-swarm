// Protocol v2 vocabulary (hive/src/protocol.ts) as the gameplay layer uses it.
// protocol.ts is the source of truth: if it changes, change this file (and only this file) to match.

namespace VirtualBeeSwarm
{
    /// <summary>BEATS (protocol v2, concept v3). The int value IS the protocol beat number.</summary>
    public enum GameBeat
    {
        Pre = 0,     // fitting, not on the clock
        Hook = 1,    // 0:00-0:15 hive on the table, HONEYMOON-1 parked
        Meet = 2,    // 0:15-0:35 Beeswax + the future window opens
        Smoker = 3,  // station 1
        Feed = 4,    // station 2 (tip the hive, then feed it)
        Wasps = 5,   // station 3 (door size)
        Hornet = 6,  // +30 s, only if ahead: fist + hum
        Heat = 7,    // station 4
        WhatIf = 8,  // voice what-if (fire -> put it out)
        Escape = 9,  // hexagons, door opens, score card
        T0 = 10,     // TAKE ME TO THE MOON, crash back
        Map = 11,    // "six more hives" locked-levels map
        Field = 12,  // post-demo (glasses only, no Quest effects)
    }

    /// <summary>STATIONS. The int value is also the hexagon index.</summary>
    public enum GameStation { Smoker = 0, Feed = 1, Wasps = 2, Heat = 3 }

    public static class GameIds
    {
        public const int ProtocolVersion = 2;
        public const int BeatCount = 13;

        public static readonly string[] Beats =
        {
            "pre", "hook", "meet", "smoker", "feed", "wasps", "hornet", "heat", "whatif", "escape", "t0", "map", "field",
        };

        public static string BeatName(GameBeat b) => Beats[(int)b];

        public static bool TryParseBeat(int n, out GameBeat beat)
        {
            beat = (GameBeat)n;
            return n >= 0 && n < BeatCount;
        }

        public static readonly string[] Stations = { "smoker", "feed", "wasps", "heat" };
        public static string StationName(GameStation s) => Stations[(int)s];

        // CHOICES / CORRECT_CHOICE
        public static class Choice
        {
            public const string Swat = "swat", SprayWater = "spray_water", Smoker = "smoker";
            public const string StoreHoney = "store_honey", SugarSyrup = "sugar_syrup";
            public const string BiggerDoor = "bigger_door", SmallerDoor = "smaller_door";
            public const string DeepBowl = "deep_bowl", PebbleDish = "pebble_dish", Nothing = "nothing";
        }

        public static readonly string[][] Choices =
        {
            new[] { Choice.Swat, Choice.SprayWater, Choice.Smoker },
            new[] { Choice.StoreHoney, Choice.SugarSyrup },
            new[] { Choice.BiggerDoor, Choice.SmallerDoor },
            new[] { Choice.DeepBowl, Choice.PebbleDish, Choice.Nothing },
        };

        public static readonly string[] CorrectChoice = { Choice.Smoker, Choice.SugarSyrup, Choice.SmallerDoor, Choice.PebbleDish };

        public static bool IsCorrect(GameStation s, string choice) => CorrectChoice[(int)s] == choice;

        /// <summary>Is `choice` one of CHOICES[station]?</summary>
        public static bool IsValidChoice(GameStation s, string choice)
        {
            var list = Choices[(int)s];
            for (int i = 0; i < list.Length; i++)
                if (list[i] == choice) return true;
            return false;
        }

        public static bool TryParseStation(string name, out GameStation station)
        {
            for (int i = 0; i < Stations.Length; i++)
            {
                if (Stations[i] != name) continue;
                station = (GameStation)i;
                return true;
            }
            station = GameStation.Smoker;
            return false;
        }

        // SCENES (the future window)
        public static class Scene
        {
            public const string Meadow = "meadow";
            public const string SmokerSettle = "smoker_settle", SmokerAngry = "smoker_angry";
            public const string FeedSick = "feed_sick", FeedRecover = "feed_recover";
            public const string WaspsRaid = "wasps_raid", WaspsLeave = "wasps_leave";
            public const string HeatStress = "heat_stress", HeatDrink = "heat_drink";
            public const string WhatIfFire = "whatif_fire", FireOut = "fire_out";
            public const string WhatIfRain = "whatif_rain", WhatIfFlowers = "whatif_flowers";
            public const string WhatIfSpray = "whatif_spray", WhatIfWinter = "whatif_winter";
            public const string WhatIfCustom = "whatif_custom"; // v2.1: an unplanned what-if (Claude writes the prompt)
            public const string FullBloom = "full_bloom", Moon = "moon";
        }

        public static readonly string[] Scenes =
        {
            Scene.Meadow, Scene.SmokerSettle, Scene.SmokerAngry, Scene.FeedSick, Scene.FeedRecover, Scene.WaspsRaid,
            Scene.WaspsLeave, Scene.HeatStress, Scene.HeatDrink, Scene.WhatIfFire, Scene.FireOut, Scene.WhatIfRain,
            Scene.WhatIfFlowers, Scene.WhatIfSpray, Scene.WhatIfWinter, Scene.WhatIfCustom, Scene.FullBloom, Scene.Moon,
        };

        /// <summary>Index of a scene in SCENES, or -1.</summary>
        public static int SceneIndex(string scene)
        {
            for (int i = 0; i < Scenes.Length; i++)
                if (Scenes[i] == scene) return i;
            return -1;
        }

        /// <summary>WHATIF_TOPICS -> the window scene for it.</summary>
        public static string SceneForTopic(string topic)
        {
            switch (topic)
            {
                case "fire": return Scene.WhatIfFire;
                case "rain": return Scene.WhatIfRain;
                case "flowers": return Scene.WhatIfFlowers;
                case "spray": return Scene.WhatIfSpray;
                case "winter": return Scene.WhatIfWinter;
                case "custom": return Scene.WhatIfCustom;
                default: return "";
            }
        }

        // FX
        public static class Fx
        {
            public const string Embers = "embers", EmbersStop = "embers_stop";
            public const string SwarmCalm = "swarm_calm", SwarmAngry = "swarm_angry";
            public const string HiveGlowRed = "hive_glow_red", HiveGlowOff = "hive_glow_off";
        }

        // INTENTS
        public static class Intent
        {
            public const string WhatIf = "what_if", Undo = "undo", Ask = "ask", Ack = "ack", Unknown = "unknown";
        }
    }
}
