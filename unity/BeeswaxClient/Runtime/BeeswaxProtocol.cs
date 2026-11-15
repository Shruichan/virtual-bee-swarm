// C# mirror of hive/src/protocol.ts (PROTOCOL_VERSION 2, v2.1 additions). That file is the source of truth:
// change both together and log the change in PLAN.md.
//
// v2.1 (additive, still version 2): choice.request (operator twin of a station choice, the game applies it like the
// player's own), fx.force (Board → Hive; the Quest receives it as a plain fx), whatif.force (Board → Hive only),
// score, optional OrbisRequest.want, custom what-ifs (topic "custom", scene "whatif_custom", Intent.custom_prompt).
// v2.2 (additive, still version 2): scenes forecast_peaceful and forecast_ravaged (the hook's forecast, sent by the
// Quest). An older client ignores them; an older Hive answers the Quest's request with an error and the clip still plays.
//
// JsonUtility has no unions and no nullable types, so:
//   * Parse() peeks at "type" with a tiny Envelope, then fills the concrete class.
//   * A string that is null or missing in JSON becomes ""   (e.g. ScanVerdict.line_id, Intent.topic, OrbisStatus.scene).
//   * A number that is null or missing becomes -1           (ScanVerdict.socket, BeatRequest.n, ScanPhoto.width/height,
//     OrbisStatus.stage, OrbisRequest.stage, Welcome.beat, Fx.delay_ms/duration_ms, Score.elapsed_s/grievances).
//     Check HasSocket / HasN / HasDelay / HasElapsed.
//   * Optional fields are left out of the JSON when unset (OrbisRequest.want/stage/scene, Fx.delay_ms/duration_ms,
//     WhatIfForce.topic/undo).
//
// Field names are snake_case on purpose: JsonUtility maps fields to JSON keys by name.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Beeswax
{
    public static class BeeswaxProtocol
    {
        public const int Version = 2;

        /// <summary>MIC_PCM16 audio format: PCM16 little-endian mono at this rate.</summary>
        public const int MicSampleRate = 16000;

        /// <summary>BEATS: index = beat number. Unity is the only beat clock.</summary>
        public static readonly string[] Beats =
        {
            "pre", "hook", "meet", "smoker", "feed", "wasps", "hornet",
            "heat", "whatif", "escape", "t0", "map", "field",
        };

        /// <summary>SCAN_BEATS: beats in which a glasses press starts a scan (real-prop mode, later).</summary>
        public static readonly string[] ScanBeats = { "smoker", "feed", "wasps", "heat", "field" };

        /// <summary>VOICE_BEATS: beats in which voice is listened to.</summary>
        public static readonly string[] VoiceBeats = { "meet", "smoker", "feed", "wasps", "hornet", "heat", "whatif", "escape", "map", "field" };

        /// <summary>STATIONS. Each station name is also its beat name.</summary>
        public static readonly string[] Stations = { Station.Smoker, Station.Feed, Station.Wasps, Station.Heat };

        static readonly Dictionary<string, string[]> s_choices = new Dictionary<string, string[]>
        {
            { Station.Smoker, new[] { Choice.Swat, Choice.SprayWater, Choice.Smoker } },
            { Station.Feed, new[] { Choice.StoreHoney, Choice.SugarSyrup } },
            { Station.Wasps, new[] { Choice.BiggerDoor, Choice.SmallerDoor } },
            { Station.Heat, new[] { Choice.DeepBowl, Choice.PebbleDish, Choice.Nothing } },
        };

        static readonly Dictionary<string, string> s_correct = new Dictionary<string, string>
        {
            { Station.Smoker, Choice.Smoker },
            { Station.Feed, Choice.SugarSyrup },
            { Station.Wasps, Choice.SmallerDoor },
            { Station.Heat, Choice.PebbleDish },
        };

        /// <summary>SCENES: the future window's scenes (each has a live Orbis prompt and a recorded fallback clip).</summary>
        public static readonly string[] Scenes =
        {
            Scene.Meadow, Scene.SmokerSettle, Scene.SmokerAngry, Scene.FeedSick, Scene.FeedRecover, Scene.WaspsRaid,
            Scene.WaspsLeave, Scene.HeatStress, Scene.HeatDrink, Scene.WhatIfFire, Scene.FireOut, Scene.WhatIfRain,
            Scene.WhatIfFlowers, Scene.WhatIfSpray, Scene.WhatIfWinter, Scene.WhatIfCustom, Scene.FullBloom, Scene.Moon,
            Scene.ForecastPeaceful, Scene.ForecastRavaged,
        };

        /// <summary>WHATIF_TOPICS.</summary>
        public static readonly string[] WhatIfTopics =
        {
            WhatIfTopic.Fire, WhatIfTopic.Rain, WhatIfTopic.Flowers, WhatIfTopic.Spray, WhatIfTopic.Winter, WhatIfTopic.Custom,
        };

        /// <summary>FX: effect cues the Hive can trigger (Unity owns how they look).</summary>
        public static readonly string[] Fx =
        {
            FxName.Embers, FxName.EmbersStop, FxName.SwarmCalm, FxName.SwarmAngry, FxName.HiveGlowRed, FxName.HiveGlowOff,
        };

        /// <summary>INTENTS.</summary>
        public static readonly string[] Intents = { IntentName.WhatIf, IntentName.Undo, IntentName.Ask, IntentName.Ack, IntentName.Unknown };

        /// <summary>ITEMS: the only things a scan verdict can name.</summary>
        public static readonly string[] Items =
        {
            Item.RedRose, Item.PurpleBeeFlower, Item.CloverOrRosemary, Item.WaterDish, Item.BeeHotel,
            Item.SprayBottle, Item.PlasticFlower, Item.OtherBeePlant, Item.OtherPlant,
            Item.NonPlantObject, Item.Person, Item.Unclear,
        };

        /// <summary>VERDICTS.</summary>
        public static readonly string[] Verdicts = { Verdict.Good, Verdict.Bad, Verdict.Dangerous, Verdict.Roast };

        /// <summary>SOCKET_OF: mat socket (1-4) of an item, or -1 if it has none.</summary>
        public static int SocketOf(string item)
        {
            switch (item)
            {
                case Item.RedRose: return 1;
                case Item.PurpleBeeFlower: return 2;
                case Item.CloverOrRosemary: return 3;
                case Item.WaterDish: return 4;
                default: return -1;
            }
        }

        public static string BeatName(int n) => n >= 0 && n < Beats.Length ? Beats[n] : "";
        public static int BeatIndex(string name) => Array.IndexOf(Beats, name);
        public static bool IsScanBeat(int n) => Array.IndexOf(ScanBeats, BeatName(n)) >= 0;
        public static bool IsVoiceBeat(int n) => Array.IndexOf(VoiceBeats, BeatName(n)) >= 0;

        // ------------------------------------------------------------ stations & choices

        public static bool IsStation(string station) => station != null && s_choices.ContainsKey(station);

        /// <summary>CHOICES[station], or an empty array for an unknown station.</summary>
        public static IReadOnlyList<string> ChoicesOf(string station) =>
            station != null && s_choices.TryGetValue(station, out var c) ? c : Array.Empty<string>();

        /// <summary>CORRECT_CHOICE[station], or "" for an unknown station.</summary>
        public static string CorrectChoice(string station) =>
            station != null && s_correct.TryGetValue(station, out var c) ? c : "";

        public static bool IsValidChoice(string station, string choice) =>
            station != null && choice != null && s_choices.TryGetValue(station, out var c) && Array.IndexOf(c, choice) >= 0;

        public static bool IsCorrect(string station, string choice) => IsValidChoice(station, choice) && CorrectChoice(station) == choice;

        /// <summary>The station a choice belongs to (every choice is in exactly one station), or "".</summary>
        public static string StationOfChoice(string choice)
        {
            foreach (var kv in s_choices)
                if (Array.IndexOf(kv.Value, choice) >= 0) return kv.Key;
            return "";
        }

        /// <summary>The beat number of a station (station names are beat names), or -1.</summary>
        public static int StationBeat(string station) => IsStation(station) ? BeatIndex(station) : -1;

        public static bool IsScene(string scene) => Array.IndexOf(Scenes, scene) >= 0;

        /// <summary>
        /// False for a scene with no recorded fallback clip: whatif_custom (an unplanned what-if whose Orbis prompt
        /// Claude writes on the spot). While the window shows it, clips mode keeps the previous scene's clip.
        /// </summary>
        public static bool SceneHasClip(string scene) => IsScene(scene) && scene != Scene.WhatIfCustom;
        public static bool IsWhatIfTopic(string topic) => Array.IndexOf(WhatIfTopics, topic) >= 0;
        public static bool IsFx(string name) => Array.IndexOf(Fx, name) >= 0;
        public static bool IsIntent(string name) => Array.IndexOf(Intents, name) >= 0;

        /// <summary>The window scene for a what-if topic ("fire" → "whatif_fire", "custom" → "whatif_custom"), or "" if there is none.</summary>
        public static string SceneOfWhatIf(string topic)
        {
            if (!IsWhatIfTopic(topic)) return "";
            string scene = "whatif_" + topic;
            return IsScene(scene) ? scene : "";
        }

        // ------------------------------------------------------------ JSON

        // JsonUtility cannot put JSON null into an int/float field, so numeric nulls become -1 first.
        static readonly Regex NumericNull = new Regex(
            "\"(socket|n|width|height|stage|beat|sample_rate|delay_ms|duration_ms|elapsed_s|grievances)\"\\s*:\\s*null", RegexOptions.CultureInvariant);

        [Serializable] class Envelope { public string type = ""; }

        /// <summary>The "type" field of a JSON message, or "" if it isn't a JSON object with a type.</summary>
        public static string PeekType(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try { return JsonUtility.FromJson<Envelope>(json)?.type ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Fill a concrete message class. Throws ArgumentException on malformed JSON.</summary>
        public static T Parse<T>(string json) where T : BusMessage, new()
        {
            var msg = new T();
            JsonUtility.FromJsonOverwrite(NumericNull.Replace(json, "\"$1\":-1"), msg);
            msg.Normalize();
            return msg;
        }

        public static string ToJson(BusMessage msg) => msg.CustomJson() ?? JsonUtility.ToJson(msg);

        /// <summary>A JSON string literal (with quotes) for s.</summary>
        public static string JsonString(string s)
        {
            s = s ?? "";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ------------------------------------------------------------ binary frames: [kind: u8][payload...]

        /// <summary>
        /// NativeWebSocket delivers text and binary frames through the same OnMessage(byte[]), so frames are
        /// told apart by their first byte: JSON starts with '{' (or whitespace), binary with a BinaryKind.
        /// </summary>
        public static bool LooksLikeJson(byte[] data)
        {
            if (data == null) return false;
            foreach (byte b in data)
            {
                if (b == (byte)'{') return true;
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n' && b != 0xEF && b != 0xBB && b != 0xBF)
                    return false; // 0xEF 0xBB 0xBF = UTF-8 BOM
            }
            return false;
        }

        public static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);

        /// <summary>Payload of a binary frame (everything after the kind byte).</summary>
        public static byte[] Payload(byte[] frame)
        {
            if (frame == null || frame.Length <= 1) return Array.Empty<byte>();
            var payload = new byte[frame.Length - 1];
            Buffer.BlockCopy(frame, 1, payload, 0, payload.Length);
            return payload;
        }

        public static byte[] Frame(byte kind, byte[] payload) => Frame(kind, payload, 0, payload?.Length ?? 0);

        public static byte[] Frame(byte kind, byte[] payload, int offset, int count)
        {
            var frame = new byte[count + 1];
            frame[0] = kind;
            if (count > 0) Buffer.BlockCopy(payload, offset, frame, 1, count);
            return frame;
        }

        internal static string S(string s) => s ?? "";
    }

    /// <summary>BINARY_KIND: first byte of every binary frame on the bus.</summary>
    public static class BinaryKind
    {
        /// <summary>JPEG of the latest glasses photo; belongs to the most recent scan.photo.</summary>
        public const byte ScanPhoto = 0x01;
        /// <summary>PCM16 little-endian mono chunk; belongs to the most recent tts.start.</summary>
        public const byte TtsPcm16 = 0x02;
        /// <summary>Client → Hive: PCM16 LE mono 16 kHz mic audio; belongs to the sender's open listen.start.</summary>
        public const byte MicPcm16 = 0x03;
    }

    public static class Beat
    {
        public const int Pre = 0, Hook = 1, Meet = 2, Smoker = 3, Feed = 4, Wasps = 5, Hornet = 6,
            Heat = 7, WhatIf = 8, Escape = 9, T0 = 10, Map = 11, Field = 12;
    }

    public static class Station
    {
        public const string Smoker = "smoker", Feed = "feed", Wasps = "wasps", Heat = "heat";
    }

    /// <summary>CHOICES values. Note "smoker" is both a station and that station's correct choice.</summary>
    public static class Choice
    {
        public const string Swat = "swat", SprayWater = "spray_water", Smoker = "smoker";
        public const string StoreHoney = "store_honey", SugarSyrup = "sugar_syrup";
        public const string BiggerDoor = "bigger_door", SmallerDoor = "smaller_door";
        public const string DeepBowl = "deep_bowl", PebbleDish = "pebble_dish", Nothing = "nothing";
    }

    public static class Scene
    {
        public const string Meadow = "meadow";
        public const string SmokerSettle = "smoker_settle", SmokerAngry = "smoker_angry";
        public const string FeedSick = "feed_sick", FeedRecover = "feed_recover";
        public const string WaspsRaid = "wasps_raid", WaspsLeave = "wasps_leave";
        public const string HeatStress = "heat_stress", HeatDrink = "heat_drink";
        public const string WhatIfFire = "whatif_fire", FireOut = "fire_out";
        public const string WhatIfRain = "whatif_rain", WhatIfFlowers = "whatif_flowers", WhatIfSpray = "whatif_spray", WhatIfWinter = "whatif_winter";
        /// <summary>v2.1: an unplanned what-if (Claude writes a safe Orbis prompt). No recorded clip: clips mode keeps the last clip.</summary>
        public const string WhatIfCustom = "whatif_custom";
        public const string FullBloom = "full_bloom", Moon = "moon";
        /// <summary>v2.2: the hook's forecast. The Quest sends peaceful when the window opens and ravaged on "angry swarms".</summary>
        public const string ForecastPeaceful = "forecast_peaceful", ForecastRavaged = "forecast_ravaged";
    }

    public static class WhatIfTopic
    {
        public const string Fire = "fire", Rain = "rain", Flowers = "flowers", Spray = "spray", Winter = "winter";
        /// <summary>v2.1: anything else ("what if a dragon shows up?"). Intent.custom_prompt holds Claude's prompt, "" if refused.</summary>
        public const string Custom = "custom";
    }

    public static class FxName
    {
        public const string Embers = "embers", EmbersStop = "embers_stop", SwarmCalm = "swarm_calm", SwarmAngry = "swarm_angry",
            HiveGlowRed = "hive_glow_red", HiveGlowOff = "hive_glow_off";
    }

    public static class IntentName
    {
        public const string WhatIf = "what_if", Undo = "undo", Ask = "ask", Ack = "ack", Unknown = "unknown";
    }

    public static class IntentPath { public const string Keyword = "keyword", Claude = "claude"; }
    public static class ListenMode { public const string Ptt = "ptt", Vad = "vad"; }
    public static class OrbisWant { public const string Live = "live", Clips = "clips", Off = "off"; }

    public static class Item
    {
        public const string RedRose = "red_rose";
        public const string PurpleBeeFlower = "purple_bee_flower";
        public const string CloverOrRosemary = "clover_or_rosemary";
        public const string WaterDish = "water_dish";
        public const string BeeHotel = "bee_hotel";
        public const string SprayBottle = "spray_bottle";
        public const string PlasticFlower = "plastic_flower";
        public const string OtherBeePlant = "other_bee_plant";
        public const string OtherPlant = "other_plant";
        public const string NonPlantObject = "non_plant_object";
        public const string Person = "person";
        public const string Unclear = "unclear";
    }

    public static class Verdict
    {
        public const string Good = "good", Bad = "bad", Dangerous = "dangerous", Roast = "roast";
    }

    public static class Role
    {
        public const string Quest = "quest", Board = "board", Monitor = "monitor", Glasses = "glasses";
        /// <summary>Not a bus role: only appears as Latency.source / LogMsg.source for hops the Hive measures.</summary>
        public const string Hive = "hive";
    }

    public static class Channel { public const string Ear = "ear", Room = "room"; }
    public static class Mode { public const string Duo = "duo", Solo = "solo"; }
    public static class ScanPath { public const string Claude = "claude", Speech = "speech", Operator = "operator"; }

    public static class OrbisState
    {
        public const string Off = "off", Priming = "priming", Ready = "ready", Failed = "failed", Ended = "ended";
    }

    public static class MsgType
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string BeatSet = "beat.set";
        public const string BeatRequest = "beat.request";
        public const string Mode = "mode";
        public const string GroupReset = "group.reset";
        public const string ScanStarted = "scan.started";
        public const string ScanPhoto = "scan.photo";
        public const string ScanVerdict = "scan.verdict";
        public const string SwarmArrived = "swarm.arrived";
        public const string HornetCooked = "hornet.cooked";
        public const string Plea = "plea";
        public const string Say = "say";
        public const string TtsStart = "tts.start";
        public const string TtsEnd = "tts.end";
        public const string HumSet = "hum.set";
        public const string VerdictForce = "verdict.force";
        public const string SayForce = "say.force";
        public const string OrbisRequest = "orbis.request";
        public const string Orbis = "orbis";
        public const string Latency = "latency";
        public const string Log = "log";
        public const string Error = "error";
        // v2
        public const string ChoiceMade = "choice.made";
        public const string ListenStart = "listen.start";
        public const string ListenEnd = "listen.end";
        public const string Transcript = "transcript";
        public const string Intent = "intent";
        public const string Fx = "fx";
        // v2.1
        public const string ChoiceRequest = "choice.request";
        public const string FxForce = "fx.force";
        public const string WhatIfForce = "whatif.force";
        public const string Score = "score";
    }

    // ---------------------------------------------------------------- messages

    [Serializable]
    public abstract class BusMessage
    {
        public string type = "";
        /// <summary>Replaces JSON nulls with the "" / -1 conventions.</summary>
        internal virtual void Normalize() { type = BeeswaxProtocol.S(type); }
        /// <summary>Hand-written JSON for messages with optional fields; null = use JsonUtility.</summary>
        internal virtual string CustomJson() => null;
    }

    /// <summary>Sent by any client right after connecting (and by the Quest as its keepalive).</summary>
    [Serializable]
    public class Hello : BusMessage
    {
        public string role = Role.Quest;
        public string client = "";
        public int version = BeeswaxProtocol.Version;
        public Hello() { type = MsgType.Hello; }
    }

    /// <summary>Hive → client after hello. beat = -1 if absent.</summary>
    [Serializable]
    public class Welcome : BusMessage
    {
        public string group_id = "";
        public int beat = -1;
        public string mode = "";
        public double server_time;
        public string host = "";
        public Welcome() { type = MsgType.Welcome; }
        internal override void Normalize() { base.Normalize(); group_id = BeeswaxProtocol.S(group_id); mode = BeeswaxProtocol.S(mode); host = BeeswaxProtocol.S(host); }
    }

    /// <summary>Quest → Hive (authoritative): Unity's beat machine entered beat n.</summary>
    [Serializable]
    public class BeatSet : BusMessage
    {
        public int n;
        public string name = "";
        public BeatSet() { type = MsgType.BeatSet; }
        // -1 denotes an absent score on ordinary beats and from older clients.
        public int score_seconds = -1;
        public BeatSet(int n, int? scoreSeconds = null) : this()
        {
            this.n = n;
            name = BeeswaxProtocol.BeatName(n);
            score_seconds = scoreSeconds ?? -1;
        }
        internal override void Normalize() { base.Normalize(); name = BeeswaxProtocol.S(name); }
        internal override string CustomJson()
        {
            var json = new StringBuilder("{\"type\":").Append(BeeswaxProtocol.JsonString(type))
                .Append(",\"n\":").Append(n.ToString(CultureInfo.InvariantCulture))
                .Append(",\"name\":").Append(BeeswaxProtocol.JsonString(name));
            if (score_seconds >= 0) json.Append(",\"score_seconds\":").Append(score_seconds.ToString(CultureInfo.InvariantCulture));
            return json.Append('}').ToString();
        }
    }

    /// <summary>Board/Hive → Quest: please go to beat n (n = -1 if absent) or the next beat. Unity may ignore it.</summary>
    [Serializable]
    public class BeatRequest : BusMessage
    {
        public int n = -1;
        public bool next;
        public BeatRequest() { type = MsgType.BeatRequest; }
        public bool HasN => n >= 0;
    }

    [Serializable]
    public class ModeMsg : BusMessage
    {
        public string mode = "";
        public ModeMsg() { type = MsgType.Mode; }
        internal override void Normalize() { base.Normalize(); mode = BeeswaxProtocol.S(mode); }
    }

    /// <summary>New judging group (clears game memory).</summary>
    [Serializable]
    public class GroupReset : BusMessage
    {
        public string group_id = "";
        public GroupReset() { type = MsgType.GroupReset; }
        internal override void Normalize() { base.Normalize(); group_id = BeeswaxProtocol.S(group_id); }
    }

    /// <summary>Glasses button pressed inside a scan beat: blank polaroid + flash, Beeswax whips around.</summary>
    [Serializable]
    public class ScanStarted : BusMessage
    {
        public string scan_id = "";
        public ScanStarted() { type = MsgType.ScanStarted; }
        internal override void Normalize() { base.Normalize(); scan_id = BeeswaxProtocol.S(scan_id); }
    }

    /// <summary>Followed by one binary SCAN_PHOTO frame. width/height = -1 if absent.</summary>
    [Serializable]
    public class ScanPhoto : BusMessage
    {
        public string scan_id = "";
        public int width = -1;
        public int height = -1;
        public ScanPhoto() { type = MsgType.ScanPhoto; }
        internal override void Normalize() { base.Normalize(); scan_id = BeeswaxProtocol.S(scan_id); }
    }

    [Serializable]
    public class ScanVerdict : BusMessage
    {
        public string scan_id = "";
        public string item = "";
        public string verdict = "";
        public float confidence;
        /// <summary>≤ 8 words for the monitor ("Beeswax saw: ...").</summary>
        public string seen = "";
        /// <summary>1-4 = where the beeline goes; -1 = no beeline / U-turn (JSON null).</summary>
        public int socket = -1;
        /// <summary>Pre-rendered line to play from Beeswax, or "".</summary>
        public string line_id = "";
        /// <summary>Live line streamed after this message (tts.start), or "".</summary>
        public string tts_id = "";
        public string caption = "";
        public string path = "";
        /// <summary>Glasses press → verdict, in ms.</summary>
        public double ms;
        public ScanVerdict() { type = MsgType.ScanVerdict; }
        public bool HasSocket => socket >= 1 && socket <= 4;
        internal override void Normalize()
        {
            base.Normalize();
            scan_id = BeeswaxProtocol.S(scan_id); item = BeeswaxProtocol.S(item); verdict = BeeswaxProtocol.S(verdict);
            seen = BeeswaxProtocol.S(seen); line_id = BeeswaxProtocol.S(line_id); tts_id = BeeswaxProtocol.S(tts_id);
            caption = BeeswaxProtocol.S(caption); path = BeeswaxProtocol.S(path);
            if (!HasSocket) socket = -1;
        }
    }

    /// <summary>Quest → Hive → glasses: the swarm landed on a socket.</summary>
    [Serializable]
    public class SwarmArrived : BusMessage
    {
        public int socket;
        public SwarmArrived() { type = MsgType.SwarmArrived; }
    }

    /// <summary>Quest → Hive: hornet defeated. overshoot = they kept humming past 48 °C.</summary>
    [Serializable]
    public class HornetCooked : BusMessage
    {
        public bool overshoot;
        public HornetCooked() { type = MsgType.HornetCooked; }
    }

    /// <summary>The glasses judge's plea (Beeswax's clipboard).</summary>
    [Serializable]
    public class Plea : BusMessage
    {
        public string text = "";
        public Plea() { type = MsgType.Plea; }
        internal override void Normalize() { base.Normalize(); text = BeeswaxProtocol.S(text); }
    }

    /// <summary>Play a line. The Quest plays only channel "room", from Beeswax's body.</summary>
    [Serializable]
    public class Say : BusMessage
    {
        public string channel = "";
        public string line_id = "";
        public string tts_id = "";
        public string audio_url = "";
        public string caption = "";
        public Say() { type = MsgType.Say; }
        internal override void Normalize()
        {
            base.Normalize();
            channel = BeeswaxProtocol.S(channel); line_id = BeeswaxProtocol.S(line_id); tts_id = BeeswaxProtocol.S(tts_id);
            audio_url = BeeswaxProtocol.S(audio_url); caption = BeeswaxProtocol.S(caption);
        }
    }

    /// <summary>Live room line: followed by binary TTS_PCM16 frames, then tts.end.</summary>
    [Serializable]
    public class TtsStart : BusMessage
    {
        public string tts_id = "";
        public int sample_rate = 16000;
        public string caption = "";
        public TtsStart() { type = MsgType.TtsStart; }
        internal override void Normalize()
        {
            base.Normalize();
            tts_id = BeeswaxProtocol.S(tts_id); caption = BeeswaxProtocol.S(caption);
            if (sample_rate <= 0) sample_rate = 16000;
        }
    }

    [Serializable]
    public class TtsEnd : BusMessage
    {
        public string tts_id = "";
        public TtsEnd() { type = MsgType.TtsEnd; }
        internal override void Normalize() { base.Normalize(); tts_id = BeeswaxProtocol.S(tts_id); }
    }

    /// <summary>Operator override of the hum thermometer (0..1).</summary>
    [Serializable]
    public class HumSet : BusMessage
    {
        public float value;
        public HumSet() { type = MsgType.HumSet; }
    }

    [Serializable]
    public class VerdictForce : BusMessage
    {
        public string item = "";
        public VerdictForce() { type = MsgType.VerdictForce; }
        internal override void Normalize() { base.Normalize(); item = BeeswaxProtocol.S(item); }
    }

    [Serializable]
    public class SayForce : BusMessage
    {
        public string line_id = "";
        public string channel = "";
        public SayForce() { type = MsgType.SayForce; }
        internal override void Normalize() { base.Normalize(); line_id = BeeswaxProtocol.S(line_id); channel = BeeswaxProtocol.S(channel); }
    }

    /// <summary>
    /// Board|Quest → Hive: switch the window layer (want) and/or scene. want = "", stage = -1 and scene = "" when
    /// absent; each is left out of the JSON when unset. v2.1: a request carrying scene/stage only switches the scene;
    /// only `want` changes live/clips/off (a v2 Hive still requires want on every request, so the Quest sends it).
    /// </summary>
    [Serializable]
    public class OrbisRequest : BusMessage
    {
        public string want = "";
        public int stage = -1;
        public string scene = "";
        public OrbisRequest() { type = MsgType.OrbisRequest; }
        public bool HasWant => want.Length > 0;
        public bool HasScene => scene.Length > 0;
        internal override void Normalize() { base.Normalize(); want = BeeswaxProtocol.S(want); scene = BeeswaxProtocol.S(scene); }
        internal override string CustomJson()
        {
            var sb = new StringBuilder("{\"type\":").Append(BeeswaxProtocol.JsonString(type));
            if (!string.IsNullOrEmpty(want)) sb.Append(",\"want\":").Append(BeeswaxProtocol.JsonString(want));
            if (stage >= 0) sb.Append(",\"stage\":").Append(stage.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(scene)) sb.Append(",\"scene\":").Append(BeeswaxProtocol.JsonString(scene));
            return sb.Append('}').ToString();
        }
    }

    /// <summary>Window layer state. scene = "" and fallback_clip = "" when null (fallback_clip = clip name for scene).</summary>
    [Serializable]
    public class OrbisStatus : BusMessage
    {
        public string state = "";
        public int stage = -1;
        public string scene = "";
        public string fallback_clip = "";
        /// <summary>v2.3: show the scene's clip this long before live frames (0 = live whenever it flows).</summary>
        public float clip_first_s;
        public OrbisStatus() { type = MsgType.Orbis; }
        internal override void Normalize()
        {
            base.Normalize();
            state = BeeswaxProtocol.S(state); scene = BeeswaxProtocol.S(scene); fallback_clip = BeeswaxProtocol.S(fallback_clip);
        }
    }

    /// <summary>One measured hop, shown on the Board/monitor. source: a Role or "hive".</summary>
    [Serializable]
    public class Latency : BusMessage
    {
        public string hop = "";
        public double ms;
        public string source = Role.Quest;
        public Latency() { type = MsgType.Latency; }
        internal override void Normalize() { base.Normalize(); hop = BeeswaxProtocol.S(hop); source = BeeswaxProtocol.S(source); }
    }

    [Serializable]
    public class LogMsg : BusMessage
    {
        public string text = "";
        public string source = Role.Quest;
        public LogMsg() { type = MsgType.Log; }
        internal override void Normalize() { base.Normalize(); text = BeeswaxProtocol.S(text); source = BeeswaxProtocol.S(source); }
    }

    [Serializable]
    public class ErrorMsg : BusMessage
    {
        public string message = "";
        public ErrorMsg() { type = MsgType.Error; }
        internal override void Normalize() { base.Normalize(); message = BeeswaxProtocol.S(message); }
    }

    // ---------------------------------------------------------------- v2: stations, voice, scenes, fx

    /// <summary>Quest → Hive (authoritative): the player made a choice at a station. Use BeeswaxBus.SendChoiceMade.</summary>
    [Serializable]
    public class ChoiceMade : BusMessage
    {
        public string station = "";
        public string choice = "";
        public bool correct;
        public ChoiceMade() { type = MsgType.ChoiceMade; }
        public ChoiceMade(string station, string choice) : this()
        {
            this.station = station ?? "";
            this.choice = choice ?? "";
            correct = BeeswaxProtocol.IsCorrect(this.station, this.choice);
        }
        internal override void Normalize() { base.Normalize(); station = BeeswaxProtocol.S(station); choice = BeeswaxProtocol.S(choice); }
    }

    /// <summary>Client → Hive: an utterance starts; binary MIC_PCM16 frames follow until listen.end. mode: ptt | vad.</summary>
    [Serializable]
    public class ListenStart : BusMessage
    {
        public string source = Role.Quest;
        public string mode = ListenMode.Ptt;
        public bool wake_phrase;
        public bool ask_dialogue;
        public ListenStart() { type = MsgType.ListenStart; }
        internal override void Normalize() { base.Normalize(); source = BeeswaxProtocol.S(source); mode = BeeswaxProtocol.S(mode); }
    }

    /// <summary>Client → Hive: the utterance ended (cancel = throw it away, don't transcribe).</summary>
    [Serializable]
    public class ListenEnd : BusMessage
    {
        public string source = Role.Quest;
        public bool cancel;
        public ListenEnd() { type = MsgType.ListenEnd; }
        internal override void Normalize() { base.Normalize(); source = BeeswaxProtocol.S(source); }
    }

    /// <summary>Hive → all: speech-to-text result. source: quest | glasses. stt = which engine.</summary>
    [Serializable]
    public class Transcript : BusMessage
    {
        public string text = "";
        public bool final;
        public string source = "";
        public double ms;
        public string stt = "";
        public string wake_status = "";
        public Transcript() { type = MsgType.Transcript; }
        internal override void Normalize() { base.Normalize(); text = BeeswaxProtocol.S(text); source = BeeswaxProtocol.S(source); stt = BeeswaxProtocol.S(stt); wake_status = BeeswaxProtocol.S(wake_status); }
    }

    /// <summary>
    /// Hive → all: what the player meant. what_if → window scene + fx + line; undo ("put it out") → back;
    /// ask → live Beeswax answer (tts); ack/unknown → a canned acknowledgement. topic = "" unless what_if.
    /// v2.1: topic "custom" = an unplanned what-if; custom_prompt = the safe Orbis prompt Claude wrote, "" when it refused
    /// (or for any other topic). Beeswax says the refusal line by himself (a normal say).
    /// </summary>
    [Serializable]
    public class Intent : BusMessage
    {
        public string name = "";
        public string topic = "";
        public string text = "";
        public string path = "";
        public double ms;
        public string custom_prompt = "";
        public Intent() { type = MsgType.Intent; }
        public bool HasTopic => topic.Length > 0;
        /// <summary>An unplanned what-if (topic "custom"), refused or not.</summary>
        public bool IsCustom => name == IntentName.WhatIf && topic == WhatIfTopic.Custom;
        /// <summary>A custom what-if Claude refused (no prompt): the window stays where it is and Beeswax says the refusal line.</summary>
        public bool CustomRefused => IsCustom && custom_prompt.Length == 0;
        internal override void Normalize()
        {
            base.Normalize();
            name = BeeswaxProtocol.S(name); topic = BeeswaxProtocol.S(topic); text = BeeswaxProtocol.S(text); path = BeeswaxProtocol.S(path);
            custom_prompt = BeeswaxProtocol.S(custom_prompt);
        }
    }

    /// <summary>Hive → Quest: play an effect. delay_ms / duration_ms = -1 when absent.</summary>
    [Serializable]
    public class Fx : BusMessage
    {
        public string name = "";
        public int delay_ms = -1;
        public int duration_ms = -1;
        public Fx() { type = MsgType.Fx; }
        public bool HasDelay => delay_ms > 0;
        public bool HasDuration => duration_ms > 0;
        internal override void Normalize() { base.Normalize(); name = BeeswaxProtocol.S(name); }
        internal override string CustomJson()
        {
            var sb = new StringBuilder("{\"type\":").Append(BeeswaxProtocol.JsonString(type))
                .Append(",\"name\":").Append(BeeswaxProtocol.JsonString(name));
            if (delay_ms >= 0) sb.Append(",\"delay_ms\":").Append(delay_ms.ToString(CultureInfo.InvariantCulture));
            if (duration_ms >= 0) sb.Append(",\"duration_ms\":").Append(duration_ms.ToString(CultureInfo.InvariantCulture));
            return sb.Append('}').ToString();
        }
    }

    // ---------------------------------------------------------------- v2.1: operator ops + score

    /// <summary>
    /// Board → Hive → Quest: the operator's twin of a player's station choice. Apply it exactly as if the player chose it
    /// (hexagon, swarm, line: call BeeswaxStations.Choose, which reports choice.made as usual). You may ignore it outside
    /// that station's beat. BeeswaxBus.OnChoiceRequest; BeeswaxStations.applyChoiceRequests does it for you.
    /// </summary>
    [Serializable]
    public class ChoiceRequest : BusMessage
    {
        public string station = "";
        public string choice = "";
        public ChoiceRequest() { type = MsgType.ChoiceRequest; }
        /// <summary>The pair is in CHOICES.</summary>
        public bool IsValid => BeeswaxProtocol.IsValidChoice(station, choice);
        internal override void Normalize() { base.Normalize(); station = BeeswaxProtocol.S(station); choice = BeeswaxProtocol.S(choice); }
    }

    /// <summary>Board → Hive: force an effect. The Hive forwards it to the Quest as a plain fx, so BeeswaxBus.OnFx covers it.</summary>
    [Serializable]
    public class FxForce : BusMessage
    {
        public string name = "";
        public FxForce() { type = MsgType.FxForce; }
        internal override void Normalize() { base.Normalize(); name = BeeswaxProtocol.S(name); }
    }

    /// <summary>
    /// Board → Hive only: the operator's twin of a voice what-if (topic) or "put it out" (undo). The Hive runs the same
    /// act() path as a spoken one, so the Quest sees the usual say/orbis/fx, but NO transcript or intent (OnIntent does
    /// not fire): key what-if feel off the window scene (OrbisWindow.OnSceneChanged) or the fx. A custom what-if is not
    /// a whatif.force (the Board posts its words to /board/custom, which does send an intent). topic = "" when absent.
    /// </summary>
    [Serializable]
    public class WhatIfForce : BusMessage
    {
        public string topic = "";
        public bool undo;
        public WhatIfForce() { type = MsgType.WhatIfForce; }
        internal override void Normalize() { base.Normalize(); topic = BeeswaxProtocol.S(topic); }
        internal override string CustomJson()
        {
            var sb = new StringBuilder("{\"type\":").Append(BeeswaxProtocol.JsonString(type));
            if (!string.IsNullOrEmpty(topic)) sb.Append(",\"topic\":").Append(BeeswaxProtocol.JsonString(topic));
            if (undo) sb.Append(",\"undo\":true");
            return sb.Append('}').ToString();
        }
    }

    /// <summary>
    /// Hive → all: the run's score, sent when the escape beat starts. text is ready for the score card
    /// ("Hive saved in 2:47"); grievances = wrong picks this group. elapsed_s / grievances = -1 when missing.
    /// </summary>
    [Serializable]
    public class Score : BusMessage
    {
        public double elapsed_s = -1;
        public string text = "";
        public int grievances = -1;
        public Score() { type = MsgType.Score; }
        public bool HasElapsed => elapsed_s >= 0;
        /// <summary>elapsed_s as m:ss ("2:47"), or "" when missing.</summary>
        public string Clock
        {
            get
            {
                if (!HasElapsed) return "";
                int s = (int)Math.Round(elapsed_s, MidpointRounding.AwayFromZero); // like the Hive's Math.round (state.ts formatClock)
                return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
            }
        }
        internal override void Normalize() { base.Normalize(); text = BeeswaxProtocol.S(text); }
    }

    // ---------------------------------------------------------------- glasses HTTP API + model output shapes
    // Not used by the Quest; mirrored so this file stays one-to-one with protocol.ts.

    [Serializable] public class PressRequest { public double pressed_at; public string button = ""; public string press_type = ""; }
    [Serializable] public class PressResponse { public bool allowed; public string scan_id = ""; public string line_id = ""; }
    [Serializable] public class ScanRequest { public string scan_id = ""; public string photo_url = ""; public string heard = ""; public double pressed_at; }
    [Serializable]
    public class ScanResponse
    {
        public string item = "", verdict = "", line_id = "", audio_url = "", caption = "", path = "";
        public double ms;
    }
    [Serializable] public class PleaRequest { public string text = ""; }
    [Serializable] public class LatencyRequest { public string hop = ""; public double ms; }
    /// <summary>POST /glasses/utterance: a final transcription from the glasses' own speech-to-text.</summary>
    [Serializable] public class UtteranceRequest { public string text = ""; public double at; }
    [Serializable]
    public class UtteranceResponse { public string intent = "", topic = "", line_id = "", audio_url = "", caption = ""; }
    /// <summary>What the intent router model returns (Hive side). custom_prompt: v2.1, topic "custom" only.</summary>
    [Serializable] public class RouterOutput { public string intent = "", topic = "", answer = "", custom_prompt = ""; }
    /// <summary>What the scan model returns (Hive side).</summary>
    [Serializable]
    public class DirectorOutput
    {
        public string item = "", verdict = "", seen = "", line = "";
        public float confidence;
    }
}
