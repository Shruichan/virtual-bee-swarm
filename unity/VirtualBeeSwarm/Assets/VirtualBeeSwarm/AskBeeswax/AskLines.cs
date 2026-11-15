namespace VirtualBeeSwarm.Ask
{
    /// <summary>
    /// ASK BEESWAX's canned answers: ids and captions copied from hive/lines/ask.json (hive/test/ask.test.ts checks they
    /// match). The Hive speaks them from memory; here they play as captions (or from Resources/BeeswaxLines when the
    /// Plan B clips exist): the card's chips, the offline line, and the local stall backstop.
    /// </summary>
    public static class AskLines
    {
        public static readonly GameLine Bananas = new GameLine("ask_bananas", "Our alarm smell shares a chemical with bananas. Smoke drowns it out. Noted for the minutes.");
        public static readonly GameLine DroneSting = new GameLine("ask_drone_sting", "No. A stinger's a modified egg-laying tube, and drones don't lay eggs. All buzz, no sting.");
        public static readonly GameLine Wildfire = new GameLine("ask_wildfire", "We don't stop fires. But wild bees often bounce back after one and pollinate what regrows.");
        public static readonly GameLine HelpMost = new GameLine("ask_help_most", "Wild ones. About four thousand kinds live in the US, with no beekeeper. Plant flowers.");
        public static readonly GameLine Killing = new GameLine("ask_killing", "A big one is a mite called varroa. It spreads viruses. Grievance filed.");
        public static readonly GameLine StingHelp = new GameLine("ask_sting_help", "Stung? Tell a grown-up. Allergic? That's a doctor's job, not a union job.");
        public static readonly GameLine OffList = new GameLine("ask_offlist", "That's above my drone pay grade. Ask me about smoke, syrup, doors, water or wild bees.");
        public static readonly GameLine People = new GameLine("ask_people", "I rate hives, not people. Back to work.");
        public static readonly GameLine Stall = new GameLine("ask_stall", "My spreadsheet froze. Ask me again. Union pace.");
        public static readonly GameLine NotHeard = new GameLine("ask_not_heard", "Didn't catch that. Hold the pinch the whole time you talk.");
        public static readonly GameLine Offline = new GameLine("ask_offline", "I can't reach the hive. Poke a question on the card.");

        /// <summary>The card's four TRY chips (ride script v2), in order, with what each one says.</summary>
        public static readonly GameLine[] Chips = { Bananas, DroneSting, Wildfire, HelpMost };
        public static readonly string[] ChipLabels = { "\"Why bananas?\"", "\"Can drones sting?\"", "\"Do bees help after\nwildfires?\"", "\"Which bees need\nhelp most?\"" };
        public static readonly string[] ChipQuestions = { "Why bananas?", "Can drones sting?", "Do bees help after wildfires?", "Which bees need help most?" };

        /// <summary>A reply that is not an answer (no stamp, no minute): the Hive speaks these live, so they arrive with
        /// no id and are told by their text.</summary>
        public static bool IsNonAnswer(string id, string caption)
        {
            if (id == Stall.id || id == NotHeard.id || id == Offline.id || id == OffList.id || id == People.id) return true;
            if (string.IsNullOrEmpty(caption)) return false;
            return caption == Stall.caption || caption == NotHeard.caption || caption == Offline.caption ||
                   caption == OffList.caption || caption == People.caption;
        }

        /// <summary>Lines that open the card or matter to it (lines.json ids).</summary>
        public const string AskAnythingId = "room_ask_anything";
        public const string EndContractId = "room_end_contract";
        public const string RefuseId = "room_whatif_refuse";
        public const string FillerId = "room_live_filler";
    }
}
