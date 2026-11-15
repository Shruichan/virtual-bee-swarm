namespace VirtualBeeSwarm.Pals
{
    /// <summary>
    /// Beeswax's hand-play lines (hive/lines/lines.json, tags "pals"). Until they are voiced they play as captions
    /// (RideLines.Get: the id has no clip yet). None is said on the autopilot's main path, so SMOKE_REQUIRE_CLIPS runs
    /// never meet them.
    /// </summary>
    public static class PalLines
    {
        public static GameLine BoopGiggle => RideLines.Get("room_pal_boop_giggle", "Hee! Quit it. I'm on the clock.");
        public static GameLine BoopFile => RideLines.Get("room_pal_boop_file", "Was that a boop? That's harassment. I'm filing.");
        public static GameLine BoopBlush => RideLines.Get("room_pal_boop_blush", "Okay. One boop. Off the record.");
        public static GameLine Wave => RideLines.Get("room_pal_wave", "Hi! Waving's free. Dues aren't.");
        public static GameLine HighFive => RideLines.Get("room_pal_highfive", "Up top! I'd high-six you, but you're a leg short.");
        public static GameLine Palm => RideLines.Get("room_pal_palm", "Foragers away from home rarely sting. They're busy. Real ones? Look, don't touch.");
    }
}
