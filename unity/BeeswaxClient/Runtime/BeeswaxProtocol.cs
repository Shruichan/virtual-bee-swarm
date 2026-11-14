// C# mirror of hive/src/protocol.ts (PROTOCOL_VERSION 1). That file is the source of truth:
// change both together and log the change in PLAN.md.
//
// JsonUtility has no unions and no nullable types, so:
//   * Parse() peeks at "type" with a tiny Envelope, then fills the concrete class.
//   * A string that is null or missing in JSON becomes ""   (e.g. ScanVerdict.line_id, Say.tts_id).
//   * A number that is null or missing becomes -1           (ScanVerdict.socket, BeatRequest.n,
//     ScanPhoto.width/height, OrbisStatus.stage, Welcome.beat). Check HasSocket / HasN.
//
// Field names are snake_case on purpose: JsonUtility maps fields to JSON keys by name.

using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Beeswax
{
    public static class BeeswaxProtocol
    {
        public const int Version = 1;

        /// <summary>BEATS: index = beat number. Unity is the only beat clock.</summary>
        public static readonly string[] Beats =
        {
            "pre", "meet", "roses", "beeline", "offlist", "hornet",
            "plea", "buffer", "t0", "splashdown", "field",
        };

        /// <summary>SCAN_BEATS: beats in which a glasses press starts a scan.</summary>
        public static readonly string[] ScanBeats = { "roses", "beeline", "offlist", "field" };

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

        // ------------------------------------------------------------ JSON

        // JsonUtility cannot put JSON null into an int/float field, so numeric nulls become -1 first.
        static readonly Regex NumericNull = new Regex(
            "\"(socket|n|width|height|stage|beat|sample_rate)\"\\s*:\\s*null", RegexOptions.CultureInvariant);

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

        public static string ToJson(BusMessage msg) => JsonUtility.ToJson(msg);

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

        public static byte[] Frame(byte kind, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var frame = new byte[payload.Length + 1];
            frame[0] = kind;
            Buffer.BlockCopy(payload, 0, frame, 1, payload.Length);
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
    }

    public static class Beat
    {
        public const int Pre = 0, Meet = 1, Roses = 2, Beeline = 3, Offlist = 4, Hornet = 5,
            Plea = 6, Buffer = 7, T0 = 8, Splashdown = 9, Field = 10;
    }

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
    }

    // ---------------------------------------------------------------- messages

    [Serializable]
    public abstract class BusMessage
    {
        public string type = "";
        /// <summary>Replaces JSON nulls with the "" / -1 conventions.</summary>
        internal virtual void Normalize() { type = BeeswaxProtocol.S(type); }
    }

    /// <summary>Sent by any client right after connecting.</summary>
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
        public BeatSet(int n) : this() { this.n = n; name = BeeswaxProtocol.BeatName(n); }
        internal override void Normalize() { base.Normalize(); name = BeeswaxProtocol.S(name); }
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

    /// <summary>stage = -1 if absent.</summary>
    [Serializable]
    public class OrbisRequest : BusMessage
    {
        public string want = "";
        public int stage = -1;
        public OrbisRequest() { type = MsgType.OrbisRequest; }
        internal override void Normalize() { base.Normalize(); want = BeeswaxProtocol.S(want); }
    }

    /// <summary>Window layer state. fallback_clip = "" if null.</summary>
    [Serializable]
    public class OrbisStatus : BusMessage
    {
        public string state = "";
        public int stage = -1;
        public string fallback_clip = "";
        public OrbisStatus() { type = MsgType.Orbis; }
        internal override void Normalize() { base.Normalize(); state = BeeswaxProtocol.S(state); fallback_clip = BeeswaxProtocol.S(fallback_clip); }
    }

    /// <summary>One measured hop, shown on the Board/monitor.</summary>
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
}
