using System.Globalization;
using UnityEngine;

namespace VirtualBeeSwarm.Stakes
{
    /// <summary>
    /// The stakes switches (PLAN.md v3.1 → 3.5 tuning flags). Plain statics so the tuning.json loader (S2.3), the Board
    /// or a test can flip them without a scene reference: call <see cref="TrySet"/> with the flag name and its text.
    ///   stakes.swarm        true   the dark swarm (false: no swarm, the dim and the rocket display still run)
    ///   stakes.dim          true   the room dim (false: never dims)
    ///   stakes.dimHook      0.25   dim at the stakes line
    ///   stakes.dimStations  0.12   dim during meet and the stations
    ///   stakes.dimMethod    brightness | opacity   how the Meta output dims passthrough (see StakesPassthroughDimmer)
    ///   stakes.display      true   the rocket display states (false: GameDirector's own T-m:ss text stays)
    ///   stakes.strikeLine   true   room_strike_lookup after the first grievance
    ///   comfort             false  fewer bees (60 at most), no overhead pass, no dives
    /// Desktop: F3 toggles comfort (StakesDirector.comfortKey).
    /// </summary>
    public static class StakesFlags
    {
        public static bool Swarm = true;
        public static bool Dim = true;
        public static float DimHook = 0.25f;
        public static float DimStations = 0.12f;
        public static bool UseOpacity;
        public static bool Display = true;
        public static bool StrikeLine = true;

        static bool _comfort;
        public static bool Comfort
        {
            get => _comfort;
            set
            {
                if (_comfort == value) return;
                _comfort = value;
                Debug.Log("[VBS][Stakes] comfort " + (value ? "ON" : "off"));
                ComfortChanged?.Invoke(value);
            }
        }

        public static event System.Action<bool> ComfortChanged;

        /// <summary>Set one flag from its tuning.json name and text value. Returns false for an unknown name or a bad value.</summary>
        public static bool TrySet(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return false;
            value = value.Trim();
            switch (key)
            {
                case "stakes.swarm": return TryBool(value, ref Swarm);
                case "stakes.dim": return TryBool(value, ref Dim);
                case "stakes.dimHook": return TryFloat(value, ref DimHook);
                case "stakes.dimStations": return TryFloat(value, ref DimStations);
                case "stakes.display": return TryBool(value, ref Display);
                case "stakes.strikeLine": return TryBool(value, ref StrikeLine);
                case "stakes.dimMethod":
                    if (value == "opacity") { UseOpacity = true; return true; }
                    if (value == "brightness") { UseOpacity = false; return true; }
                    return false;
                case "comfort":
                    bool c = _comfort;
                    if (!TryBool(value, ref c)) return false;
                    Comfort = c;
                    return true;
                default: return false;
            }
        }

        static bool TryBool(string v, ref bool into)
        {
            if (v == "true" || v == "1" || v == "on") { into = true; return true; }
            if (v == "false" || v == "0" || v == "off") { into = false; return true; }
            return false;
        }

        static bool TryFloat(string v, ref float into)
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return false;
            into = Mathf.Clamp01(f);
            return true;
        }
    }
}
