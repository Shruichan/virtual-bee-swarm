using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Ride script v2 lines the beats use by id. GameLines is generated from hive/lines/lines.json; the v2 ids are looked
    /// up by string, so a line that isn't in lines.json yet still plays as a caption (the script's text below) instead
    /// of breaking the build. Once lines.json has the id and GameLines is regenerated, its text (and clip) win.
    /// CHANGED lines keep their GameLines fields (same id, new text after the regeneration).
    /// </summary>
    public static class RideLines
    {
        public static GameLine Get(string id, string fallbackCaption) =>
            GameLines.TryGet(id, out var line) ? line : new GameLine(id, fallbackCaption);

        // pre + meet
        public static GameLine HookCarry => Get("room_hook_carry", "Easy! Easy! I'd help, but drones don't carry. No pollen baskets. It's in the contract.");
        public static GameLine MeetRole => Get("room_meet_role", "My hive's sick. Beekeeper quit. You signed. Congratulations, beekeeper.");
        public static GameLine Forecast => Get("room_forecast", "Job perk: that window shows the future. Paris, tomorrow. Lovely.");

        // stations
        public static GameLine HintSmoker => Get("room_hint_smoker", "Off the record? Beekeepers don't swat. They puff.");
        public static GameLine SmokerBanana => Get("room_smoker_banana", "Don't eat one near my house.");
        public static GameLine LiftLight => Get("room_lift_light", "Light, right? That's bad. Full hives are heavy with honey. The movers were being dramatic.");
        public static GameLine HintFeed => Get("room_hint_feed", "Off the record? You don't know where store honey's been.");
        public static GameLine FeedWhy => Get("room_feed_why", "Why not store honey? It can carry foulbrood spores. Fine on toast, deadly in a hive.");
        public static GameLine WaspsArrive => Get("room_wasps_arrive", "Uh oh. Problem three: wasps made a beeline for the syrup.");
        public static GameLine HintWasps => Get("room_hint_wasps", "Off the record? Think castle. Big gate or small gate?");
        public static GameLine WaspsStinger => Get("room_wasps_stinger", "I wasn't hiding. Drones can't sting. A stinger's a modified egg-laying tube. Girls only.");
        public static GameLine HintHeat => Get("room_hint_heat", "Off the record? Bees can't swim. Where would a bee stand to drink?");
        public static GameLine HeatMom => Get("room_heat_mom", "The queen's in there. She's everybody's mom. Including mine.");
        public static GameLine Timeout => Get("room_timeout", "Overruled. I'm picking for you. Watch closely.");
        public static GameLine ClockZero => Get("room_clock_zero", "Time's up. ...I'm granting an extension. Union discretion. Keep going.");

        // voice
        public static GameLine WhatIfNotFire => Get("room_whatif_notfire", "Rain, flowers, anything. Just not fire.");
        public static GameLine HintWhatIf => Get("room_hint_whatif", "Try: \"What if a fire starts?\" ...Actually, don't.");
        public static GameLine WhatIfPutOut => Get("room_whatif_putout", "It's spreading! Pinch and say \"put it out\"! SAY IT!");
        public static GameLine WhatIfSmokeWhy => Get("room_whatif_smoke_why", "Why do we gulp honey when you smoke us? Nobody's sure. One idea: smoke means fire, so we pack a lunch.");
        public static GameLine LiveFiller => Get("room_live_filler", "I could drone on about that. Short version:");
        public static GameLine Adjourn => Get("room_adjourn", "Motion to adjourn. Seconded, by me. All in favor?");

        // escape, T-0, unload
        public static GameLine EscapeMinutes => Get("room_escape_minutes", "For the minutes: smoke, syrup, small door, shallow water. That's real beekeeping.");
        public static GameLine EscapeCredit => Get("room_escape_credit", "Thanks, beekeeper. ...Strike that from the minutes.");
        public static GameLine T0Earth => Get("room_t0_earth", "Look at Earth. Up here it barely moves. Just hangs there. Like a drone. I'd know.");
        public static GameLine Truth => Get("room_truth", "Truth time. Real swarms are gentle. Real hives die quietly: mites, hunger, heat, poison.");
        public static GameLine MeetStat => Get("room_meet_stat", "In one recent year, American beekeepers lost more than half their hives. Somebody has to be loud.");
        public static GameLine EndContract => Get("room_end_contract", "You're hired. New contract: plant flowers, skip the bug spray, put out a pebble dish.");
        public static GameLine AskAnything => Get("room_ask_anything", "Got a bee question? Pinch and ask. I've got half a minute and a spreadsheet.");

        /// <summary>The end card (Unity text; the printed copy says the same). {0} = the score clock (m:ss).</summary>
        public const string EndCardTitle = "HIVE SAVED IN {0}";
        public const string EndCardBody =
            "YOUR NEW CONTRACT · 3 THINGS THIS WEEK\n" +
            "1. Plant native flowers that bloom\n    spring to fall. A balcony pot counts.\n" +
            "2. Skip the bug spray, especially on\n    anything in bloom.\n" +
            "3. Put out a shallow dish of water with\n    pebbles to land on. Bees can't swim.\n" +
            "Bonus: leave a patch of bare dirt.\n7 in 10 wild bees nest underground.\n" +
            "- Local 20,000";

        public const string AskCard =
            "ASK BEESWAX\nPinch, hold, ask a bee question\n\nTry: \"Why bananas?\"\n\"Can drones sting?\"\n" +
            "\"Do bees help after wildfires?\"\n\"Which bees need help most?\"";
    }

    /// <summary>
    /// Line lengths for staging: the clip when there is one, else what BeeswaxRoomVoice holds a caption-only line for.
    /// </summary>
    public static class LineTime
    {
        public static float Seconds(GameNet net, GameLine line)
        {
            float clip = net != null ? net.ClipSeconds(line.id) : 0f;
            if (clip > 0.5f) return clip;
            return CaptionSeconds(line.caption);
        }

        /// <summary>BeeswaxRoomVoice's reading time for a caption-only line.</summary>
        public static float CaptionSeconds(string caption)
        {
            int words = string.IsNullOrEmpty(caption) ? 0 : caption.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return Mathf.Clamp(words * 0.38f, 1.5f, 7f);
        }

        /// <summary>Seconds into a line of this length at which `words` are said (even pace per character), else fallbackFrac of it.</summary>
        public static float WordAt(float seconds, string caption, string words, float fallbackFrac)
        {
            int at = string.IsNullOrEmpty(caption) || string.IsNullOrEmpty(words) ? -1 : caption.IndexOf(words, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return seconds * Mathf.Clamp01(fallbackFrac);
            return seconds * at / Mathf.Max(1, caption.Length);
        }
    }

    /// <summary>
    /// Optional lines and cut-if-behind (ride script v2, "PACING AND CUT ORDER"). Auto follows the quick guide: no wrong
    /// picks = optional #1-#4, one or two = #1 only, three or more or any stall (a hint or a timeout) = none, and the
    /// cut-if-behind lines (#1 room_escape_credit, #2 room_smoker_banana, #3 room_map) drop out. #5-#9 never play by
    /// themselves (the Board's "Say a line" still plays any of them). The autopilot locks it to the main path (no
    /// optional line, no cut) so a smoke run only says must-render lines. Env VBS_RIDE_OPTIONAL = off (default: only the main path has clips) | auto | all.
    /// </summary>
    public static class RidePacing
    {
        public enum Mode { Auto, MainOnly, All }

        public static Mode Setting = ReadEnv();
        /// <summary>GameAutopilot holds this while it runs.</summary>
        public static bool AutopilotLock;
        public static int Stalls { get; private set; }

        public static Mode Effective => AutopilotLock ? Mode.MainOnly : Setting;
        static int Grievances => Stakes.StakesDirector.Instance != null ? Stakes.StakesDirector.Instance.Grievances : 0;
        public static bool Behind => Stalls >= 2 || Grievances >= 3;

        public static void NoteStall(string what)
        {
            Stalls++;
            Debug.Log("[VBS][Ride] stall #" + Stalls + ": " + what);
        }

        public static void ResetRun() => Stalls = 0;

        /// <summary>Should optional line #n play now? Logs the decision.</summary>
        public static bool Optional(int n, GameLine line)
        {
            bool play;
            string why;
            switch (Effective)
            {
                case Mode.MainOnly: play = false; why = AutopilotLock ? "autopilot: main path only" : "main path only"; break;
                case Mode.All: play = true; why = "VBS_RIDE_OPTIONAL=all"; break;
                default:
                    int g = Grievances;
                    if (n > 4) { play = false; why = "only #1-#4 play by themselves (Board: Say a line)"; }
                    else if (Behind) { play = false; why = "behind (" + g + " wrong picks, " + Stalls + " stalls)"; }
                    else if (g >= 1) { play = n == 1; why = g + " wrong pick(s): #1 only"; }
                    else { play = true; why = "clean run: #1-#4"; }
                    break;
            }
            Debug.Log("[VBS][Ride] optional #" + n + " " + line.id + ": " + (play ? "play" : "skip") + " (" + why + ")");
            return play;
        }

        /// <summary>Is cut-if-behind line #n cut now? Logs only when it is.</summary>
        public static bool Cut(int n, GameLine line)
        {
            bool cut = Effective == Mode.Auto && Behind;
            if (cut) Debug.Log("[VBS][Ride] cut-if-behind #" + n + " " + line.id + ": cut (" + Grievances + " wrong picks, " + Stalls + " stalls)");
            return cut;
        }

        static Mode ReadEnv()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable("VBS_RIDE_OPTIONAL"); } catch (Exception) { }
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "off": case "none": case "main": return Mode.MainOnly;
                case "all": case "on": return Mode.All;
                case "auto": return Mode.Auto;
                default: return Mode.MainOnly; // only the main path has voice clips
            }
        }
    }
}
