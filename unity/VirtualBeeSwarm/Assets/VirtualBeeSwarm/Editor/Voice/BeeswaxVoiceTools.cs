using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Beeswax's lines in Unity (PLAN S8.1 + S8.2). Three jobs, each a menu item (Virtual Bee Swarm > Voice) and a batch
    /// method (exit code 0 = no errors):
    ///   Generate  hive/lines/lines.json + stations.json -> Core/GameLines.cs: every line as a field, the names the beats
    ///             use (the Aliases table below), the station outcome table. Never edit GameLines.cs by hand.
    ///   Import    hive/out/unity/*.mp3 + hive/out/manifest.json -> Assets/Resources/BeeswaxLines/ (new or changed files
    ///             only; clips that are no longer rendered are reported, not deleted). BeeswaxLinesImporter sets the import
    ///             settings.
    ///   Check     the two-way id check: lines.json ids = compiled GameLines ids with identical captions (plus the declared
    ///             local-only lines); stations.json = GameIds (stations, choices, correct, scenes, fx) = GameLines' outcomes;
    ///             GameLines.cs = what the generator writes now; the client's beeswax_stations.json = stations.json; every
    ///             lines.json id has a clip and every clip has an id; no clip was rendered from older text (the render
    ///             manifest's caption != lines.json); every clip's import settings.
    ///
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.Editor.BeeswaxVoiceTools.Check -logFile check.log
    ///
    /// Paths (environment, optional): VBS_LINES_DIR = the folder with lines.json + stations.json (default
    /// &lt;project&gt;/../../hive/lines, the repo layout), VBS_VOICE_OUT = the prerender's Unity folder (default
    /// &lt;project&gt;/../../hive/out/unity; manifest.json is read from its parent). VBS_STRICT_CLIPS=1 turns missing,
    /// orphan and stale clips from warnings into errors (voice freeze, S8.7). Log lines start with [VBSVoice].
    /// </summary>
    public static class BeeswaxVoiceTools
    {
        public const string ClipFolder = BeeswaxLinesImporter.Folder;
        public const string ManifestAsset = ClipFolder + "/_manifest.json";
        const string GameLinesPath = "Assets/VirtualBeeSwarm/Core/GameLines.cs";
        const string ClientStationsAsset = "Packages/com.virtualbeeswarm.beeswax-client/Resources/beeswax_stations.json";
        const string Tag = "[VBSVoice] ";

        // The names the beats use -> line id. To give a beat another line, change it here and run Generate.
        static readonly string[,] Aliases =
        {
            { "PreStart", "room_pre", "pre: place the hive, START" },
            { "Hook", "room_hook_1", "" },
            { "Hook2", "room_hook_2", "" },
            { "MeetIntro", "room_meet_1", "" },
            { "Meet", "room_meet_2", "" },
            { "SmokerPrompt", "room_smoker_intro", "= stations.json smoker.intro_line" },
            { "FeedPrompt", "room_feed_intro", "= stations.json feed.intro_line" },
            { "FeedLiftHint", "room_feed_lift_hint", "the player hasn't lifted the hive yet" },
            { "FeedTooLight", "room_feed_tip", "after the lift" },
            { "FeedChoose", "room_feed_prompt", "" },
            { "WaspsPrompt", "room_wasps_intro", "= stations.json wasps.intro_line" },
            { "HeatPrompt", "room_heat_intro", "= stations.json heat.intro_line" },
            { "HornetWarning", "room_hornet_1", "" },
            { "HornetHum", "room_hornet_2", "" },
            { "HornetLouder", "room_hornet_3", "" },
            { "HornetOvershoot", "room_hornet_4", "" },
            { "HornetDone", "room_ack_carried", "hornet cooked" },
            { "WhatIfPrompt", "room_whatif_prompt", "" },
            { "WhatIfFire", "room_whatif_fire", "" },
            { "FireOut", "room_whatif_out", "" },
            { "Escape", "room_escape", "" },
            { "T0Going", "room_t0_1", "" },
            { "T0Launch", "room_t0_2", "" },
            { "T0Moon", "room_t0_3", "" },
            { "T0Back", "room_t0_4", "" },
            { "Map", "room_map", "" },
            { "TimeoutSolve", "room_ack_minutes", "a station ran out of time: Beeswax solves it" },
            { "Retry", "room_ack_denied", "" },
        };

        // Lines the game plays that lines.json doesn't have (yet): caption only, no clip. Once lines.json has the id,
        // its text wins and Check says to remove the entry here.
        static readonly string[,] LocalOnly =
        {
            { "room_feed_lift_hint", "Grab it and lift. Union rules allow it." },
        };

        // Per GameStation (0..3): the alias of its prompt, which must be stations.json's intro_line.
        static readonly string[] StationPromptAlias = { "SmokerPrompt", "FeedPrompt", "WaspsPrompt", "HeatPrompt" };

        // ------------------------------------------------------------------------------------------- entry points
        [MenuItem("Virtual Bee Swarm/Voice/Regenerate GameLines (lines.json + stations.json)")]
        static void GenerateMenu() => RunGenerate(false);

        [MenuItem("Virtual Bee Swarm/Voice/Import clips from hive/out")]
        static void ImportMenu() => RunImport(false);

        [MenuItem("Virtual Bee Swarm/Voice/Check lines, clips and import settings")]
        static void CheckMenu() => RunCheck(false);

        /// <summary>Batch: write GameLines.cs (only if it changes). Exit code 0 = written or already current.</summary>
        public static void Generate() => RunGenerate(Application.isBatchMode);

        /// <summary>Batch: copy new/changed clips + the manifest from VBS_VOICE_OUT into Resources/BeeswaxLines.</summary>
        public static void Import() => RunImport(Application.isBatchMode);

        /// <summary>Batch: every check above. Exit code 0 = no errors (warnings allowed unless VBS_STRICT_CLIPS=1).</summary>
        public static void Check() => RunCheck(Application.isBatchMode);

        // Git can check the input and generated LF files out with CRLF on Windows.
        static string ReadTextWithLf(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

        static string ReadGeneratedGameLines() => File.Exists(GameLinesPath) ? ReadTextWithLf(GameLinesPath) : "";

        static int RunGenerate(bool exit)
        {
            var r = new Report();
            try
            {
                var src = LoadSource(r);
                string code = src != null ? Emit(src, r) : null;
                if (code != null && r.Errors.Count == 0)
                {
                    string old = ReadGeneratedGameLines();
                    if (old == code) r.Info("GameLines.cs is already current (" + src.Lines.Count + " lines)");
                    else
                    {
                        File.WriteAllText(GameLinesPath, code, new UTF8Encoding(false));
                        AssetDatabase.ImportAsset(GameLinesPath);
                        r.Info("wrote " + GameLinesPath + ": " + src.Lines.Count + " lines from lines.json, " + LocalOnlyIds(src).Count +
                               " local-only, " + Aliases.GetLength(0) + " beat names, " + src.Choices.Count + " outcomes");
                    }
                }
                else if (code != null) r.Info("GameLines.cs NOT written: fix the errors first");
            }
            catch (Exception e) { r.Error("exception: " + e); }
            return Finish(r, "GENERATE", exit);
        }

        static int RunImport(bool exit)
        {
            var r = new Report();
            try
            {
                string outDir = VoiceOutDir();
                if (!Directory.Exists(outDir)) throw new DirectoryNotFoundException("no prerender folder " + outDir + " (set VBS_VOICE_OUT)");
                Directory.CreateDirectory(ClipFolder);
                int added = 0, changed = 0, same = 0;
                var rendered = new HashSet<string>();
                foreach (var file in Directory.GetFiles(outDir, "*.mp3").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(file);
                    rendered.Add(Path.GetFileNameWithoutExtension(name));
                    string dest = ClipFolder + "/" + name;
                    if (!File.Exists(dest)) { File.Copy(file, dest); added++; }
                    else if (!SameBytes(file, dest)) { File.Copy(file, dest, true); changed++; r.Info("updated " + name); }
                    else same++;
                }
                string manifest = Path.Combine(Path.GetDirectoryName(outDir) ?? outDir, "manifest.json");
                if (File.Exists(manifest))
                {
                    if (!File.Exists(ManifestAsset) || !SameBytes(manifest, ManifestAsset)) { File.Copy(manifest, ManifestAsset, true); r.Info("updated " + ManifestAsset); }
                }
                else r.Warn("no " + manifest + ": stale-clip detection keeps the old manifest");
                foreach (var have in ClipFiles())
                    if (!rendered.Contains(have.Key)) r.Warn("clip " + have.Key + " is in " + ClipFolder + " but not in " + outDir + " (kept)");
                AssetDatabase.Refresh();
                r.Info("import from " + outDir + ": " + added + " added, " + changed + " changed, " + same + " unchanged");
            }
            catch (Exception e) { r.Error("exception: " + e); }
            return Finish(r, "IMPORT", exit);
        }

        static int RunCheck(bool exit)
        {
            var r = new Report();
            bool strict = Environment.GetEnvironmentVariable("VBS_STRICT_CLIPS") == "1";
            try
            {
                var src = LoadSource(r);
                if (src != null)
                {
                    string code = Emit(src, r);
                    if (code != null)
                    {
                        string disk = ReadGeneratedGameLines();
                        if (disk != code) r.Error("GameLines.cs is not what lines.json + stations.json generate now (stale or hand-edited): run Generate");
                        else r.Info("GameLines.cs = generator output for lines.json " + Short(src.LinesSha) + " + stations.json " + Short(src.StationsSha));
                    }
                    CheckCompiled(src, r);
                    CheckClientStations(src, r);
                    CheckClips(src, r, strict);
                }
            }
            catch (Exception e) { r.Error("exception: " + e); }
            return Finish(r, "CHECK" + (strict ? " (strict clips)" : ""), exit);
        }

        static int Finish(Report r, string what, bool exit)
        {
            foreach (var s in r.Infos) Debug.Log(Tag + s);
            foreach (var s in r.Warnings) Debug.LogWarning(Tag + "WARNING " + s);
            foreach (var s in r.Errors) Debug.LogError(Tag + "ERROR " + s);
            int rc = r.Errors.Count == 0 ? 0 : 1;
            Debug.Log(Tag + what + (rc == 0 ? " PASS" : " FAIL") + ": " + r.Errors.Count + " error(s), " + r.Warnings.Count + " warning(s)");
            if (exit) EditorApplication.Exit(rc);
            return rc;
        }

        // ------------------------------------------------------------------------------------------- source files
        sealed class Line
        {
            public string Id, Channel, Beat, Text, Field;
            public List<string> Tags = new List<string>();
        }

        sealed class Outcome
        {
            public GameStation Station;
            public string Choice, LineId, Scene, Fx;
            public bool Correct;
        }

        sealed class Source
        {
            public string LinesPath, StationsPath, LinesSha, StationsSha;
            public readonly List<Line> Lines = new List<Line>();
            public readonly Dictionary<string, Line> ById = new Dictionary<string, Line>();
            public readonly string[] IntroLine = new string[4];
            public readonly List<Outcome> Choices = new List<Outcome>();
            public MiniJson.Obj StationsJson;
        }

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        static string EnvDir(string env, params string[] fallback)
        {
            string v = Environment.GetEnvironmentVariable(env);
            return Path.GetFullPath(!string.IsNullOrEmpty(v) ? v : Path.Combine(new[] { ProjectRoot }.Concat(fallback).ToArray()));
        }

        static string LinesDir() => EnvDir("VBS_LINES_DIR", "..", "..", "hive", "lines");
        static string VoiceOutDir() => EnvDir("VBS_VOICE_OUT", "..", "..", "hive", "out", "unity");

        static Source LoadSource(Report r)
        {
            var src = new Source { LinesPath = Path.Combine(LinesDir(), "lines.json"), StationsPath = Path.Combine(LinesDir(), "stations.json") };
            if (!File.Exists(src.LinesPath) || !File.Exists(src.StationsPath))
            {
                r.Error("no lines.json / stations.json in " + LinesDir() + " (set VBS_LINES_DIR to the hive/lines folder)");
                return null;
            }
            string linesJson = ReadTextWithLf(src.LinesPath), stationsJson = ReadTextWithLf(src.StationsPath);
            src.LinesSha = Sha256(Encoding.UTF8.GetBytes(linesJson));
            src.StationsSha = Sha256(Encoding.UTF8.GetBytes(stationsJson));
            r.Info("lines.json " + src.LinesPath + " sha256 " + Short(src.LinesSha) + "; stations.json sha256 " + Short(src.StationsSha));

            // lines.json: [{ id, channel, beat?, text, tags[] }]
            if (!(MiniJson.Parse(linesJson) is List<object> arr)) { r.Error("lines.json is not an array"); return null; }
            var idRx = new Regex("^[a-z][a-z0-9_]*$");
            var fields = new Dictionary<string, string>();
            foreach (var item in arr)
            {
                if (!(item is MiniJson.Obj o)) { r.Error("lines.json: an entry is not an object"); continue; }
                var line = new Line { Id = o.Str("id"), Channel = o.Str("channel") ?? "", Beat = o.Str("beat") ?? "", Text = o.Str("text") };
                if (string.IsNullOrEmpty(line.Id) || !idRx.IsMatch(line.Id)) { r.Error("lines.json: bad id '" + line.Id + "'"); continue; }
                if (src.ById.ContainsKey(line.Id)) { r.Error("lines.json: duplicate id " + line.Id); continue; }
                if (string.IsNullOrEmpty(line.Text)) r.Error("lines.json: " + line.Id + " has no text");
                if (line.Channel != "room" && line.Channel != "ear") r.Warn("lines.json: " + line.Id + " has channel '" + line.Channel + "'");
                var tags = o.A("tags");
                if (tags != null) foreach (var t in tags) if (t is string ts) line.Tags.Add(ts);
                line.Field = Pascal(line.Id);
                if (fields.TryGetValue(line.Field, out var other)) r.Error("lines.json: ids " + other + " and " + line.Id + " both become GameLines." + line.Field);
                else fields[line.Field] = line.Id;
                src.Lines.Add(line);
                src.ById[line.Id] = line;
            }

            // stations.json: { station: { intro_line, choices: { choice: { correct, line_id, scene, fx? } } } }
            src.StationsJson = MiniJson.Parse(stationsJson) as MiniJson.Obj;
            if (src.StationsJson == null) { r.Error("stations.json is not an object"); return null; }
            var seenStations = new HashSet<GameStation>();
            foreach (var kv in src.StationsJson.Items)
            {
                if (!GameIds.TryParseStation(kv.Key, out var st)) { r.Error("stations.json: station '" + kv.Key + "' is not in GameIds.Stations"); continue; }
                seenStations.Add(st);
                var so = kv.Value as MiniJson.Obj;
                string intro = so?.Str("intro_line");
                src.IntroLine[(int)st] = intro;
                if (string.IsNullOrEmpty(intro) || !src.ById.ContainsKey(intro)) r.Error("stations.json: " + kv.Key + ".intro_line '" + intro + "' is not in lines.json");
                var choices = so?.O("choices");
                if (choices == null) { r.Error("stations.json: " + kv.Key + " has no choices"); continue; }
                var seenChoices = new HashSet<string>();
                foreach (var ckv in choices.Items)
                {
                    var co = ckv.Value as MiniJson.Obj;
                    var oc = new Outcome { Station = st, Choice = ckv.Key, LineId = co?.Str("line_id"), Scene = co?.Str("scene"), Fx = co?.Str("fx") };
                    oc.Correct = co != null && co.TryGet("correct", out var cv) && cv is bool b && b;
                    seenChoices.Add(oc.Choice);
                    if (!GameIds.IsValidChoice(st, oc.Choice)) { r.Error("stations.json: " + kv.Key + "." + oc.Choice + " is not in GameIds.Choices"); continue; }
                    if (oc.Correct != GameIds.IsCorrect(st, oc.Choice)) r.Error("stations.json: " + kv.Key + "." + oc.Choice + " correct=" + oc.Correct + " but GameIds.CorrectChoice says " + !oc.Correct);
                    if (string.IsNullOrEmpty(oc.LineId) || !src.ById.ContainsKey(oc.LineId)) r.Error("stations.json: " + kv.Key + "." + oc.Choice + ".line_id '" + oc.LineId + "' is not in lines.json");
                    if (GameIds.SceneIndex(oc.Scene ?? "") < 0) r.Error("stations.json: " + kv.Key + "." + oc.Choice + ".scene '" + oc.Scene + "' is not in GameIds.Scenes");
                    if (!string.IsNullOrEmpty(oc.Fx) && ConstName(typeof(GameIds.Fx), oc.Fx) == null) r.Error("stations.json: " + kv.Key + "." + oc.Choice + ".fx '" + oc.Fx + "' is not in GameIds.Fx");
                    src.Choices.Add(oc);
                }
                foreach (var c in GameIds.Choices[(int)st])
                    if (!seenChoices.Contains(c)) r.Error("stations.json: " + kv.Key + " lacks GameIds choice '" + c + "'");
            }
            for (int s = 0; s < GameIds.Stations.Length; s++)
                if (!seenStations.Contains((GameStation)s)) r.Error("stations.json lacks GameIds station '" + GameIds.Stations[s] + "'");

            // The beat names: each must resolve, and the station prompts must be stations.json's intro lines.
            var local = LocalOnlyIds(src);
            for (int i = 0; i < Aliases.GetLength(0); i++)
            {
                string name = Aliases[i, 0], id = Aliases[i, 1];
                if (fields.ContainsKey(name)) r.Error("beat name " + name + " clashes with the field of line " + fields[name]);
                if (!src.ById.ContainsKey(id) && !local.ContainsKey(id)) r.Error("beat name GameLines." + name + " -> '" + id + "': not in lines.json (and not declared local-only)");
            }
            for (int s = 0; s < StationPromptAlias.Length; s++)
            {
                string id = AliasId(StationPromptAlias[s]);
                if (src.IntroLine[s] != null && id != src.IntroLine[s])
                    r.Error("GameLines." + StationPromptAlias[s] + " = " + id + " but stations.json " + GameIds.Stations[s] + ".intro_line = " + src.IntroLine[s]);
            }
            foreach (var kv in local) if (fields.ContainsKey(Pascal(kv.Key))) r.Error("local-only " + kv.Key + " clashes with a lines.json field");
            for (int i = 0; i < LocalOnly.GetLength(0); i++)
                if (src.ById.ContainsKey(LocalOnly[i, 0]))
                    r.Warn("local-only line " + LocalOnly[i, 0] + " is in lines.json now (its text is used): remove it from BeeswaxVoiceTools.LocalOnly");
            return src;
        }

        static Dictionary<string, string> LocalOnlyIds(Source src)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < LocalOnly.GetLength(0); i++)
                if (!src.ById.ContainsKey(LocalOnly[i, 0])) d[LocalOnly[i, 0]] = LocalOnly[i, 1];
            return d;
        }

        static string AliasId(string name)
        {
            for (int i = 0; i < Aliases.GetLength(0); i++) if (Aliases[i, 0] == name) return Aliases[i, 1];
            return null;
        }

        // ------------------------------------------------------------------------------------------- GameLines.cs
        static string Emit(Source src, Report r)
        {
            var local = LocalOnlyIds(src);
            var w = new StringBuilder();
            void L(string s = "") => w.Append(s).Append('\n');

            L("// <auto-generated>");
            L("// GameLines: Beeswax's lines and the station outcome table. GENERATED from hive/lines/lines.json + stations.json by");
            L("// Editor/Voice/BeeswaxVoiceTools.cs: never edit by hand. Change lines.json / stations.json (lane D) or the beat names");
            L("// (BeeswaxVoiceTools.Aliases), then Virtual Bee Swarm > Voice > Regenerate GameLines, or in batch");
            L("// -executeMethod VirtualBeeSwarm.Editor.BeeswaxVoiceTools.Generate (VBS_LINES_DIR = the hive/lines folder if the");
            L("// project isn't at unity/<Project> in the repo). ...BeeswaxVoiceTools.Check runs the two-way id check.");
            L("// lines.json sha256 " + src.LinesSha);
            L("// stations.json sha256 " + src.StationsSha);
            L("//");
            L("// Online, the Hive answers choice.made / intents with its own `say`, and a local station line only plays if no room line");
            L("// arrives in time (GameNet.SayUnlessHive). Offline, these ARE the show. Clips: Resources/BeeswaxLines/<line id>");
            L("// (BeeswaxRoomVoice plays them by id; GameNet.PreloadVoice loads all of them during the intro). A line without a");
            L("// clip shows its caption for a reading-time estimate.");
            L("// </auto-generated>");
            L();
            L("namespace VirtualBeeSwarm");
            L("{");
            L("    public readonly struct GameLine");
            L("    {");
            L("        public readonly string id;");
            L("        public readonly string caption;");
            L("        public GameLine(string id, string caption) { this.id = id; this.caption = caption; }");
            L("    }");
            L();
            L("    /// <summary>What one station choice does locally: the window scene, Beeswax's line, and whether it's right.</summary>");
            L("    public readonly struct GameOutcome");
            L("    {");
            L("        public readonly GameStation station;");
            L("        public readonly string choice;");
            L("        public readonly bool correct;");
            L("        public readonly string scene;");
            L("        public readonly GameLine line;");
            L("        public GameOutcome(GameStation station, string choice, string scene, GameLine line)");
            L("        {");
            L("            this.station = station;");
            L("            this.choice = choice;");
            L("            correct = GameIds.IsCorrect(station, choice);");
            L("            this.scene = scene;");
            L("            this.line = line;");
            L("        }");
            L("        public GameOutcome(GameStation station, string choice, string scene, string lineId, string caption)");
            L("            : this(station, choice, scene, new GameLine(lineId, caption)) { }");
            L("    }");
            L();
            L("    public static class GameLines");
            L("    {");
            L("        public const string LinesSha256 = " + Lit(src.LinesSha) + ";");
            L("        public const string StationsSha256 = " + Lit(src.StationsSha) + ";");
            L();
            L("        // ------------------------------------------------------------ every line in lines.json, in file order (channel | beat | tags)");
            foreach (var line in src.Lines)
                L("        public static readonly GameLine " + line.Field + " = new GameLine(" + Lit(line.Id) + ", " + Lit(line.Text) + "); // " +
                  line.Channel + " | " + (line.Beat.Length > 0 ? line.Beat : "-") + " | " + string.Join(", ", line.Tags));
            L();
            L("        // ------------------------------------------------------------ local only: the game plays these, lines.json doesn't have them");
            L("        // (no clip: caption only). Add them to hive/lines/lines.json + render them, and they come from there.");
            foreach (var kv in local)
                L("        public static readonly GameLine " + Pascal(kv.Key) + " = new GameLine(" + Lit(kv.Key) + ", " + Lit(kv.Value) + ");");
            L();
            L("        // ------------------------------------------------------------ the names the beats use (BeeswaxVoiceTools.Aliases)");
            for (int i = 0; i < Aliases.GetLength(0); i++)
            {
                string id = Aliases[i, 1];
                if (!src.ById.ContainsKey(id) && !local.ContainsKey(id)) return null; // already an error
                string note = Aliases[i, 2];
                L("        public static readonly GameLine " + Aliases[i, 0] + " = " + Pascal(id) + ";" + (note.Length > 0 ? " // " + note : ""));
            }
            L();
            L("        // ------------------------------------------------------------ station outcomes (stations.json: CHOICES x CORRECT_CHOICE)");
            L("        static readonly GameOutcome[] Outcomes =");
            L("        {");
            GameStation? prev = null;
            foreach (var o in src.Choices)
            {
                if (!src.ById.ContainsKey(o.LineId ?? "")) return null;
                if (prev.HasValue && prev.Value != o.Station) L();
                prev = o.Station;
                string choice = ConstName(typeof(GameIds.Choice), o.Choice), scene = ConstName(typeof(GameIds.Scene), o.Scene);
                L("            new GameOutcome(GameStation." + o.Station + ", " + (choice != null ? "GameIds.Choice." + choice : Lit(o.Choice)) + ", " +
                  (scene != null ? "GameIds.Scene." + scene : Lit(o.Scene)) + ", " + src.ById[o.LineId].Field + "), // " +
                  (o.Correct ? "correct" : "wrong") + (string.IsNullOrEmpty(o.Fx) ? "" : ", fx " + o.Fx));
            }
            L("        };");
            L();
            L("        static readonly GameLine[] FileLines =");
            L("        {");
            foreach (var chunk in Chunks(src.Lines.Select(l => l.Field), 6)) L("            " + string.Join(", ", chunk) + ",");
            L("        };");
            L();
            L("        static readonly GameLine[] LocalLines = { " + string.Join(", ", local.Keys.Select(Pascal)) + (local.Count > 0 ? " " : "") + "};");
            L();
            L("        /// <summary>Every line: lines.json order, then the local-only ones. Allocates.</summary>");
            L("        public static GameLine[] All()");
            L("        {");
            L("            var all = new GameLine[FileLines.Length + LocalLines.Length];");
            L("            FileLines.CopyTo(all, 0);");
            L("            LocalLines.CopyTo(all, FileLines.Length);");
            L("            return all;");
            L("        }");
            L();
            L("        /// <summary>The ids that have (or should have) a clip in Resources/BeeswaxLines: every lines.json line, in file order. Allocates.</summary>");
            L("        public static string[] ClipIds()");
            L("        {");
            L("            var ids = new string[FileLines.Length];");
            L("            for (int i = 0; i < ids.Length; i++) ids[i] = FileLines[i].id;");
            L("            return ids;");
            L("        }");
            L();
            L("        /// <summary>A line the game plays that lines.json doesn't have (caption only).</summary>");
            L("        public static bool IsLocalOnly(string id)");
            L("        {");
            L("            for (int i = 0; i < LocalLines.Length; i++)");
            L("                if (LocalLines[i].id == id) return true;");
            L("            return false;");
            L("        }");
            L();
            L("        public static bool TryGet(string id, out GameLine line)");
            L("        {");
            L("            for (int i = 0; i < FileLines.Length; i++)");
            L("                if (FileLines[i].id == id) { line = FileLines[i]; return true; }");
            L("            for (int i = 0; i < LocalLines.Length; i++)");
            L("                if (LocalLines[i].id == id) { line = LocalLines[i]; return true; }");
            L("            line = default;");
            L("            return false;");
            L("        }");
            L();
            L("        public static bool TryGetOutcome(GameStation station, string choice, out GameOutcome outcome)");
            L("        {");
            L("            for (int i = 0; i < Outcomes.Length; i++)");
            L("            {");
            L("                if (Outcomes[i].station == station && Outcomes[i].choice == choice)");
            L("                {");
            L("                    outcome = Outcomes[i];");
            L("                    return true;");
            L("                }");
            L("            }");
            L("            outcome = default;");
            L("            return false;");
            L("        }");
            L();
            L("        /// <summary>Button label for a choice id.</summary>");
            L("        public static string Label(string choice)");
            L("        {");
            L("            switch (choice)");
            L("            {");
            L("                case GameIds.Choice.Swat: return \"Swat\";");
            L("                case GameIds.Choice.SprayWater: return \"Spray water\";");
            L("                case GameIds.Choice.Smoker: return \"Smoker\";");
            L("                case GameIds.Choice.StoreHoney: return \"Store honey\";");
            L("                case GameIds.Choice.SugarSyrup: return \"Sugar syrup\";");
            L("                case GameIds.Choice.BiggerDoor: return \"Bigger door\";");
            L("                case GameIds.Choice.SmallerDoor: return \"Smaller door\";");
            L("                case GameIds.Choice.DeepBowl: return \"Deep bowl\";");
            L("                case GameIds.Choice.PebbleDish: return \"Pebble dish\";");
            L("                case GameIds.Choice.Nothing: return \"Nothing\";");
            L("                default: return choice;");
            L("            }");
            L("        }");
            L("    }");
            L("}");
            return w.ToString();
        }

        // ------------------------------------------------------------------------------------------- checks
        /// <summary>The compiled GameLines against lines.json + stations.json, both ways, captions identical.</summary>
        static void CheckCompiled(Source src, Report r)
        {
            var compiled = new Dictionary<string, string>();
            foreach (var l in GameLines.All())
            {
                if (compiled.ContainsKey(l.id)) r.Error("GameLines.All() has " + l.id + " twice");
                compiled[l.id] = l.caption;
            }
            int same = 0;
            foreach (var line in src.Lines)
            {
                if (!compiled.TryGetValue(line.Id, out var cap)) r.Error("lines.json id " + line.Id + " is not in GameLines (compiled)");
                else if (cap != line.Text) r.Error("caption of " + line.Id + " differs: GameLines \"" + cap + "\" vs lines.json \"" + line.Text + "\"");
                else same++;
            }
            // By reflection, so this tool still compiles against an older GameLines.cs (it must be able to regenerate it).
            var isLocalOnly = typeof(GameLines).GetMethod("IsLocalOnly", BindingFlags.Public | BindingFlags.Static);
            int local = 0;
            foreach (var kv in compiled)
            {
                if (src.ById.ContainsKey(kv.Key)) continue;
                if (isLocalOnly != null && (bool)isLocalOnly.Invoke(null, new object[] { kv.Key })) { local++; r.Warn("local-only line " + kv.Key + " is not in lines.json: caption only, no clip (lane D: add it to hive/lines/lines.json and render it)"); }
                else r.Error("GameLines id " + kv.Key + " is not in lines.json");
            }
            // Every GameLine field (the beat names too) must point at a known line with its exact caption.
            int fields = 0;
            foreach (var f in typeof(GameLines).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(GameLine)))
            {
                fields++;
                var gl = (GameLine)f.GetValue(null);
                if (!compiled.TryGetValue(gl.id ?? "", out var cap) || cap != gl.caption) r.Error("GameLines." + f.Name + " -> '" + gl.id + "' doesn't resolve to a line");
            }
            int outcomes = 0;
            foreach (var o in src.Choices)
            {
                if (!GameLines.TryGetOutcome(o.Station, o.Choice, out var oc)) { r.Error("GameLines has no outcome for " + o.Station + "/" + o.Choice); continue; }
                if (oc.line.id != o.LineId || oc.scene != o.Scene || oc.correct != o.Correct) r.Error("GameLines outcome " + o.Station + "/" + o.Choice + " = " + oc.line.id + "/" + oc.scene + "/" + oc.correct + ", stations.json = " + o.LineId + "/" + o.Scene + "/" + o.Correct);
                else outcomes++;
            }
            for (int s = 0; s < GameIds.Stations.Length; s++)
                foreach (var c in GameIds.Choices[s])
                    if (!GameLines.TryGetOutcome((GameStation)s, c, out _)) r.Error("GameLines has no outcome for GameIds choice " + GameIds.Stations[s] + "/" + c);
            r.Info("two-way id check: " + src.Lines.Count + " lines.json ids = " + same + " GameLines ids with identical captions, " + local +
                   " local-only, " + fields + " GameLine fields resolve, " + outcomes + "/" + src.Choices.Count + " outcomes = stations.json");
        }

        static void CheckClientStations(Source src, Report r)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(ClientStationsAsset);
            if (asset == null) { r.Warn("no " + ClientStationsAsset + " (client offline outcomes need it)"); return; }
            string a = Canonical(MiniJson.Parse(asset.text)), b = Canonical(src.StationsJson);
            if (a != b) r.Warn("the client's beeswax_stations.json differs from stations.json (lane C: cp hive/lines/stations.json unity/BeeswaxClient/Resources/beeswax_stations.json)");
            else r.Info("the client's beeswax_stations.json = stations.json");
        }

        static void CheckClips(Source src, Report r, bool strict)
        {
            Action<string> soft = strict ? (Action<string>)r.Error : r.Warn;
            var clips = ClipFiles();
            var usedAs = new Dictionary<string, string>(); // id -> the beat name / outcome that plays it
            for (int i = 0; i < Aliases.GetLength(0); i++) usedAs[Aliases[i, 1]] = "GameLines." + Aliases[i, 0];
            foreach (var o in src.Choices) usedAs[o.LineId] = "outcome " + GameIds.StationName(o.Station) + "/" + o.Choice;

            var missing = src.Lines.Where(l => !clips.ContainsKey(l.Id)).Select(l => l.Id).ToList();
            var orphans = clips.Keys.Where(id => !src.ById.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                soft(missing.Count + " lines.json id(s) without a clip (caption only): " + string.Join(", ", missing.Select(id => usedAs.TryGetValue(id, out var u) ? id + " (= " + u + ")" : id)));
            if (orphans.Count > 0) soft(orphans.Count + " clip(s) without a lines.json id: " + string.Join(", ", orphans));

            // Rendered from older text? The prerender manifest records the caption each clip was made from.
            var manifest = LoadManifest(r);
            int stale = 0, unknown = 0;
            if (manifest != null)
            {
                var staleIds = new List<string>();
                foreach (var line in src.Lines)
                {
                    if (!clips.ContainsKey(line.Id)) continue;
                    var m = manifest.O(line.Id);
                    if (m == null) { unknown++; continue; }
                    if (m.Str("caption") != line.Text) staleIds.Add(line.Id);
                }
                stale = staleIds.Count;
                if (stale > 0) soft(stale + " clip(s) rendered from older text (re-render: prerender.ts --only): " + string.Join(", ", staleIds));
                if (unknown > 0) r.Warn(unknown + " clip(s) not in the render manifest");
            }

            int ok = 0;
            foreach (var kv in clips)
            {
                if (kv.Value.Count > 1) { r.Error("clip " + kv.Key + " exists " + kv.Value.Count + " times (" + string.Join(", ", kv.Value) + "): Resources.Load picks one at random"); continue; }
                string path = kv.Value[0];
                if (Path.GetDirectoryName(path)?.Replace('\\', '/') != ClipFolder) { r.Error("clip " + path + " is in a subfolder: BeeswaxRoomVoice loads BeeswaxLines/<id> only"); continue; }
                var problems = BeeswaxLinesImporter.Problems(path);
                if (problems.Count > 0) r.Error("import settings of " + path + ": " + string.Join("; ", problems));
                else ok++;
            }
            r.Info("clips: " + clips.Count + " in " + ClipFolder + " for " + src.Lines.Count + " lines.json ids; " + missing.Count + " id(s) without a clip, " +
                   orphans.Count + " clip(s) without an id, " + stale + " rendered from older text");
            r.Info("import settings OK on " + ok + "/" + clips.Count + " clips (mono, normalize off, Vorbis q" + BeeswaxLinesImporter.VorbisQuality.ToString("0.00") +
                   ", Decompress On Load, Preload Audio Data, Load In Background; Android override " + BeeswaxLinesImporter.AndroidSampleRate + " Hz)");
            if (clips.Count > 0)
            {
                // One sample, so the log shows what the importer produced.
                var first = clips.First();
                var imp = AssetImporter.GetAtPath(first.Value[0]) as AudioImporter;
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(first.Value[0]);
                if (imp != null && clip != null)
                {
                    var a = imp.GetOverrideSampleSettings(BeeswaxLinesImporter.AndroidPlatform);
                    r.Info("sample " + first.Key + ": " + clip.length.ToString("0.00") + " s, " + clip.frequency + " Hz, " + clip.channels + " ch, " + clip.loadType +
                           ", preload " + clip.preloadAudioData + "; Android " + a.compressionFormat + " q" + a.quality.ToString("0.00") + " " + a.sampleRateSetting + " " + a.sampleRateOverride + " Hz");
                }
            }
        }

        static MiniJson.Obj LoadManifest(Report r)
        {
            string path = File.Exists(ManifestAsset) ? ManifestAsset : Path.Combine(Path.GetDirectoryName(VoiceOutDir()) ?? "", "manifest.json");
            if (!File.Exists(path)) { r.Warn("no render manifest (" + ManifestAsset + "): can't tell stale clips"); return null; }
            var root = MiniJson.Parse(File.ReadAllText(path)) as MiniJson.Obj;
            var lines = root?.O("lines");
            if (lines == null) r.Warn(path + " has no \"lines\"");
            else r.Info("render manifest " + path + " (" + lines.Items.Count + " lines, generated_at " + root.Str("generated_at") + ")");
            return lines;
        }

        /// <summary>Audio files in Resources/BeeswaxLines (and below), by file name without extension.</summary>
        static Dictionary<string, List<string>> ClipFiles()
        {
            var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(ClipFolder)) return d;
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { ClipFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string id = Path.GetFileNameWithoutExtension(path);
                if (!d.TryGetValue(id, out var list)) d[id] = list = new List<string>();
                list.Add(path);
            }
            return d;
        }

        // ------------------------------------------------------------------------------------------- helpers
        sealed class Report
        {
            public readonly List<string> Errors = new List<string>(), Warnings = new List<string>(), Infos = new List<string>();
            public void Error(string s) => Errors.Add(s);
            public void Warn(string s) => Warnings.Add(s);
            public void Info(string s) => Infos.Add(s);
        }

        /// <summary>room_t0_earth -> RoomT0Earth.</summary>
        static string Pascal(string id)
        {
            var sb = new StringBuilder();
            foreach (var part in id.Split('_'))
                if (part.Length > 0) sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            return sb.ToString();
        }

        /// <summary>The name of the const in `holder` whose value is `value`, or null.</summary>
        static string ConstName(Type holder, string value)
        {
            foreach (var f in holder.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral && f.FieldType == typeof(string) && (string)f.GetRawConstantValue() == value) return f.Name;
            return null;
        }

        static string Lit(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static IEnumerable<List<string>> Chunks(IEnumerable<string> items, int size)
        {
            var chunk = new List<string>(size);
            foreach (var item in items)
            {
                chunk.Add(item);
                if (chunk.Count == size) { yield return chunk; chunk = new List<string>(size); }
            }
            if (chunk.Count > 0) yield return chunk;
        }

        static string Canonical(object v)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case string s: return Lit(s);
                case List<object> a: return "[" + string.Join(",", a.Select(Canonical)) + "]";
                case MiniJson.Obj o: return "{" + string.Join(",", o.Items.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Lit(kv.Key) + ":" + Canonical(kv.Value))) + "}";
                default: return v.ToString();
            }
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        static string Short(string sha) => sha != null && sha.Length > 12 ? sha.Substring(0, 12) : sha;

        static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
