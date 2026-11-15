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
        public static GameLine HighFive => RideLines.Get("room_pal_highfive", "Up top! I'd go high-six, but you're a finger short.");
        public static GameLine HoneyOffer => RideLines.Get("room_pal_honey_offer", "Bonus shift. Pinch that frame's top bar and pull it out.");
        public static GameLine HoneyCaps => RideLines.Get("room_pal_honey_caps", "See the wax lids? Bees cap honey once it's ripe. Scrape them off.");
        public static GameLine HoneyHelp => RideLines.Get("room_pal_honey_help", "Fine, I'll scrape. Drones can't make wax. Taking it off, I can do.");
        public static GameLine HoneyWinter => RideLines.Get("room_pal_honey_winter", "That's plenty. The rest stays. They eat it all winter.");
        public static GameLine Dance => RideLines.Get("room_pal_dance", "I don't forage, but I took the seminar. The waggle points to food from the sun. Longer waggle, farther flowers.");
        public static GameLine Palm => RideLines.Get("room_pal_palm", "Foragers away from home rarely sting. They're busy. Real ones? Look, don't touch.");
    }
}
