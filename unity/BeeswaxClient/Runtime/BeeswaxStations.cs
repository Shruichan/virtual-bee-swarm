using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Beeswax
{
    /// <summary>
    /// Station outcomes for when the Hive can't answer: an offline copy of hive/lines/stations.json
    /// (station → intro_line + choices → {correct, line_id, scene, fx}).
    ///
    /// Data, first found wins: the `stationsJson` TextAsset on a component → Resources/beeswax_stations(.json)
    /// (this package ships one in BeeswaxClient/Resources; refresh it from hive/lines/stations.json, or from
    /// hive/out/unity/stations.json that the Hive's prerender writes) → built-in defaults (the protocol's scene
    /// per choice and the Hive's line naming, room_&lt;station&gt;_&lt;choice&gt;).
    ///
    /// Component: Choose(station, choice) is the one call for a station choice. It sends choice.made; the Hive then
    /// plays the line, switches the window and fires fx. If the bus is offline, or nothing comes back within
    /// hiveGraceSeconds, it plays the same outcome locally (voice line, window scene, fx via BeeswaxBus.OnFx), so
    /// Unity's offline timers still show the right thing. Static TryGet/Resolve work without the component.
    /// </summary>
    public class BeeswaxStations : MonoBehaviour
    {
        [Serializable]
        public class Outcome
        {
            public string station = "", choice = "";
            public bool correct;
            /// <summary>Room line to play ("" = none).</summary>
            public string line_id = "";
            /// <summary>Caption if the data has one (else BeeswaxRoomVoice's own list).</summary>
            public string caption = "";
            /// <summary>Window scene (one of SCENES).</summary>
            public string scene = "";
            /// <summary>FX names to fire (may be empty).</summary>
            public string[] fx = Array.Empty<string>();
            /// <summary>True when this came from built-in defaults, not from stations.json.</summary>
            public bool isDefault;
        }

        [SerializeField] BeeswaxBus bus;
        [Tooltip("Plays the line locally when the Hive is unreachable.")]
        [SerializeField] BeeswaxRoomVoice voice;
        [Tooltip("Switches the window scene locally when the Hive is unreachable.")]
        [SerializeField] OrbisWindow window;
        [Tooltip("Optional: overrides Resources/beeswax_stations.")]
        [SerializeField] TextAsset stationsJson;
        [Tooltip("Online: if neither the line nor the scene comes back from the Hive within this, play it locally.")]
        [SerializeField] float hiveGraceSeconds = 1.5f;
        [SerializeField] bool playLocallyWhenOffline = true;
        [Tooltip("Locally played lines: skip the Hive's copy if a queued choice.made reaches it within this.")]
        [SerializeField] float suppressEchoSeconds = 15f;
        [Tooltip("The same station+choice again within this is ignored (a double-fired grab). The Hive drops such a " +
                 "repeat too, so sending it would only make the outcome replay locally after hiveGraceSeconds.")]
        [SerializeField] float repeatIgnoreSeconds = 1.5f;

        /// <summary>Every Choose: (outcome, sentToHive). Light the hexagon / count the score here.</summary>
        public event Action<Outcome, bool> OnChoice;
        /// <summary>The outcome was played locally (Hive offline or silent).</summary>
        public event Action<Outcome> OnPlayedLocally;

        public int PendingCount => _watches.Count;

        sealed class Watch { public Outcome outcome; public float deadline; public bool heard; }
        readonly List<Watch> _watches = new List<Watch>();
        BeeswaxBus _bound;
        string _lastKey = "";
        float _lastAt = -100f;

        // ---------------------------------------------------------------- component
        void Awake()
        {
            if (stationsJson != null) Load(stationsJson.text, stationsJson.name);
        }

        void OnEnable() => Bind();
        void Start() => Bind();
        void OnDisable() { Unbind(); _watches.Clear(); }

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnSay += HandleSay;
            b.OnOrbis += HandleOrbis;
            b.OnGroupReset += HandleGroupReset;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnSay -= HandleSay;
            _bound.OnOrbis -= HandleOrbis;
            _bound.OnGroupReset -= HandleGroupReset;
            _bound = null;
        }

        /// <summary>
        /// The player chose `choice` at `station`: tell the Hive, and play the outcome locally if it can't.
        /// Returns the outcome (null for a pair that is not in CHOICES).
        /// </summary>
        public Outcome Choose(string station, string choice)
        {
            if (!BeeswaxProtocol.IsValidChoice(station, choice))
            {
                Debug.LogError($"[Beeswax] '{choice}' is not a choice of station '{station}'.");
                return null;
            }
            Bind();
            var o = Resolve(station, choice);
            string key = station + ":" + choice;
            float now = Time.realtimeSinceStartup;
            if (key == _lastKey && now - _lastAt < repeatIgnoreSeconds)
            {
                Debug.Log($"[Beeswax] {key} chosen again within {repeatIgnoreSeconds:0.0}s; ignored.");
                return o;
            }
            _lastKey = key;
            _lastAt = now;
            bool sent = _bound != null && _bound.SendChoiceMade(station, choice);
            if (sent) _watches.Add(new Watch { outcome = o, deadline = Time.realtimeSinceStartup + hiveGraceSeconds });
            else if (playLocallyWhenOffline) PlayLocally(o);
            Raise(OnChoice, o, sent);
            return o;
        }

        /// <summary>Play an outcome here, without the Hive: line (Beeswax), window scene, fx (through BeeswaxBus.OnFx).</summary>
        public void PlayLocally(Outcome o)
        {
            if (o == null) return;
            Debug.Log($"[Beeswax] playing {o.station}:{o.choice} locally (line {o.line_id}, scene {o.scene}).");
            var v = voice;
            if (v != null && o.line_id.Length > 0)
            {
                v.SuppressNextFromBus(o.line_id, suppressEchoSeconds);
                v.Play(o.line_id, o.caption.Length > 0 ? o.caption : null);
            }
            if (window != null && o.scene.Length > 0) window.RequestScene(o.scene);
            var b = _bound != null ? _bound : BeeswaxBus.Instance;
            if (b != null)
                foreach (string name in o.fx)
                    b.InjectLocal(new Fx { name = name });
            try { OnPlayedLocally?.Invoke(o); } catch (Exception e) { Debug.LogException(e); }
        }

        void Update()
        {
            Bind();
            if (_watches.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _watches.Count - 1; i >= 0; i--)
            {
                var w = _watches[i];
                if (w.heard) { _watches.RemoveAt(i); continue; }
                if (now < w.deadline) continue;
                _watches.RemoveAt(i);
                Debug.LogWarning($"[Beeswax] no answer from the Hive for {w.outcome.station}:{w.outcome.choice} in {hiveGraceSeconds:0.0}s.");
                if (playLocallyWhenOffline) PlayLocally(w.outcome);
            }
        }

        // Any room line or the expected scene means the Hive is handling the choice.
        void HandleSay(Say s)
        {
            if (s.channel != Channel.Room) return;
            foreach (var w in _watches) w.heard = true;
        }

        void HandleOrbis(OrbisStatus s)
        {
            foreach (var w in _watches)
                if (s.scene.Length > 0 && s.scene == w.outcome.scene) w.heard = true;
        }

        void HandleGroupReset(GroupReset _)
        {
            _watches.Clear();
            _lastKey = "";
        }

        static void Raise(Action<Outcome, bool> ev, Outcome o, bool sent)
        {
            if (ev == null) return;
            foreach (var d in ev.GetInvocationList())
                try { ((Action<Outcome, bool>)d)(o, sent); } catch (Exception e) { Debug.LogException(e); }
        }

        // ---------------------------------------------------------------- data (static)
        static readonly Dictionary<string, Outcome> s_table = new Dictionary<string, Outcome>();
        static readonly Dictionary<string, string> s_intro = new Dictionary<string, string>();
        static readonly Dictionary<string, string> s_captions = new Dictionary<string, string>();
        static bool s_triedResources;

        /// <summary>Where the table came from ("Resources/beeswax_stations", a TextAsset name, ...) or "" (defaults only).</summary>
        public static string Source { get; private set; } = "";
        /// <summary>Outcomes loaded from data (not defaults).</summary>
        public static int LoadedCount => s_table.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_table.Clear();
            s_intro.Clear();
            s_captions.Clear();
            s_triedResources = false;
            Source = "";
        }

        /// <summary>The outcome for a choice: from the data if present, else the built-in default.</summary>
        public static Outcome Resolve(string station, string choice)
        {
            EnsureLoaded();
            if (s_table.TryGetValue(station + ":" + choice, out var o)) return o;
            return Default(station, choice);
        }

        /// <summary>Only data from stations.json (false when the pair is missing there).</summary>
        public static bool TryGet(string station, string choice, out Outcome outcome)
        {
            EnsureLoaded();
            return s_table.TryGetValue(station + ":" + choice, out outcome);
        }

        /// <summary>The station's intro line id (stations.json intro_line, else room_&lt;station&gt;_intro), "" if unknown station.</summary>
        public static string IntroLine(string station)
        {
            EnsureLoaded();
            if (s_intro.TryGetValue(station ?? "", out var id)) return id;
            return BeeswaxProtocol.IsStation(station) ? "room_" + station + "_intro" : "";
        }

        public static void EnsureLoaded()
        {
            if (s_table.Count > 0 || s_triedResources) return;
            s_triedResources = true;
            var asset = Resources.Load<TextAsset>("beeswax_stations");
            if (asset != null) Load(asset.text, "Resources/beeswax_stations");
            else Debug.Log("[Beeswax] no Resources/beeswax_stations; station outcomes use built-in defaults.");
        }

        /// <summary>
        /// Replace the table from stations.json text. Tolerant: unknown keys are ignored, fx may be a string or a
        /// list, captions are read from "caption"/"text" on a choice or from a top-level "lines" map. Returns false
        /// (keeping the previous table) if the JSON is unreadable or has no valid outcome.
        /// </summary>
        public static bool Load(string json, string source)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(json) as Dictionary<string, object>; }
            catch (Exception e) { Debug.LogWarning($"[Beeswax] {source}: bad JSON ({e.Message}); keeping the old station table."); return false; }
            if (root == null) { Debug.LogWarning($"[Beeswax] {source}: not a JSON object."); return false; }

            var table = new Dictionary<string, Outcome>();
            var intro = new Dictionary<string, string>();
            var captions = new Dictionary<string, string>();
            if (root.TryGetValue("lines", out var linesObj) && linesObj is Dictionary<string, object> lines)
                foreach (var kv in lines)
                {
                    string text = kv.Value as string ?? Str(kv.Value as Dictionary<string, object>, "caption", "text");
                    if (!string.IsNullOrEmpty(text)) captions[kv.Key] = text;
                }
            var stations = root.TryGetValue("stations", out var st) && st is Dictionary<string, object> nested ? nested : root;

            foreach (string station in BeeswaxProtocol.Stations)
            {
                if (!stations.TryGetValue(station, out var sObj) || !(sObj is Dictionary<string, object> s)) continue;
                string introId = Str(s, "intro_line", "intro");
                if (!string.IsNullOrEmpty(introId)) intro[station] = introId;
                var choices = s.TryGetValue("choices", out var c) && c is Dictionary<string, object> cd ? cd : s;
                foreach (string choice in BeeswaxProtocol.ChoicesOf(station))
                {
                    if (!choices.TryGetValue(choice, out var oObj) || !(oObj is Dictionary<string, object> od)) continue;
                    var o = Default(station, choice);
                    o.isDefault = false;
                    o.line_id = Str(od, "line_id", "line") ?? "";
                    string scene = Str(od, "scene");
                    if (BeeswaxProtocol.IsScene(scene)) o.scene = scene;
                    else if (scene != null) Debug.LogWarning($"[Beeswax] {source}: {station}.{choice} has unknown scene '{scene}'; using {o.scene}.");
                    o.fx = ReadFx(od, source, station, choice);
                    if (od.TryGetValue("correct", out var corr) && corr is bool b && b != o.correct)
                        Debug.LogWarning($"[Beeswax] {source}: {station}.{choice} correct={b} disagrees with CORRECT_CHOICE; using the protocol.");
                    o.caption = Str(od, "caption", "text") ?? (captions.TryGetValue(o.line_id, out var cap) ? cap : "");
                    table[station + ":" + choice] = o;
                }
            }
            if (table.Count == 0) { Debug.LogWarning($"[Beeswax] {source}: no station outcomes found; keeping the old table."); return false; }

            s_table.Clear();
            foreach (var kv in table) s_table[kv.Key] = kv.Value;
            s_intro.Clear();
            foreach (var kv in intro) s_intro[kv.Key] = kv.Value;
            s_captions.Clear();
            foreach (var kv in captions) s_captions[kv.Key] = kv.Value;
            s_triedResources = true;
            Source = source;
            int expected = 0;
            foreach (string station in BeeswaxProtocol.Stations) expected += BeeswaxProtocol.ChoicesOf(station).Count;
            if (table.Count < expected) Debug.LogWarning($"[Beeswax] {source}: {table.Count} of {expected} station outcomes; the rest use defaults.");
            return true;
        }

        static string[] ReadFx(Dictionary<string, object> od, string source, string station, string choice)
        {
            if (!od.TryGetValue("fx", out var v) || v == null) return Array.Empty<string>();
            var list = new List<string>();
            if (v is string one) list.Add(one);
            else if (v is List<object> many) foreach (var x in many) if (x is string sx) list.Add(sx);
            list.RemoveAll(f =>
            {
                if (BeeswaxProtocol.IsFx(f)) return false;
                Debug.LogWarning($"[Beeswax] {source}: {station}.{choice} has unknown fx '{f}'; ignored.");
                return true;
            });
            return list.ToArray();
        }

        static string Str(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            foreach (var k in keys)
                if (d.TryGetValue(k, out var v) && v is string s) return s;
            return null;
        }

        // Protocol comments give the scene per choice; line ids follow the Hive's room_<station>_<choice> naming.
        static Outcome Default(string station, string choice)
        {
            string scene;
            switch (choice)
            {
                case Choice.Smoker: scene = Scene.SmokerSettle; break;
                case Choice.Swat: case Choice.SprayWater: scene = Scene.SmokerAngry; break;
                case Choice.StoreHoney: scene = Scene.FeedSick; break;
                case Choice.SugarSyrup: scene = Scene.FeedRecover; break;
                case Choice.BiggerDoor: scene = Scene.WaspsRaid; break;
                case Choice.SmallerDoor: scene = Scene.WaspsLeave; break;
                case Choice.PebbleDish: scene = Scene.HeatDrink; break;
                case Choice.DeepBowl: case Choice.Nothing: scene = Scene.HeatStress; break;
                default: scene = ""; break;
            }
            string line = "room_" + station + "_" + choice;
            return new Outcome
            {
                station = station ?? "",
                choice = choice ?? "",
                correct = BeeswaxProtocol.IsCorrect(station, choice),
                line_id = line,
                caption = s_captions.TryGetValue(line, out var cap) ? cap : "",
                scene = scene,
                isDefault = true,
            };
        }
    }

    /// <summary>Minimal JSON reader: objects → Dictionary&lt;string, object&gt;, arrays → List&lt;object&gt;, numbers → double.</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new FormatException("null");
            int i = 0;
            var v = Value(json, ref i);
            Ws(json, ref i);
            if (i != json.Length) throw new FormatException("trailing data at " + i);
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '﻿')) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            return Num(s, ref i);
        }

        static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string k = Str(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                d[k] = Value(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return l; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            throw new FormatException("unterminated string");
        }

        static double Num(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("unexpected '" + s[i] + "' at " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
