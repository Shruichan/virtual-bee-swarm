#!/usr/bin/env bun
// One voice for Beeswax (dartiros): every scripted clip in Unity's Resources/BeeswaxLines in ONE ElevenLabs voice, the
// one in ELEVENLABS_VOICE_ID (hive/.env; nothing is hard-coded). Paid: needs ELEVENLABS_API_KEY. Model and format come
// from _manifest.json (eleven_multilingual_v2, mp3 44.1 kHz 128 kbps). Text comes from lines/lines.json, lines/ask.json,
// or the Unity-only lines in the game C# (new GameLine / RideLines.Get).
//   bun scripts/render-voice.ts --check       which clips are off-format (another voice) and which room lines have no clip
//   bun scripts/render-voice.ts --dry-run     what a run would render (ids, characters)
//   bun scripts/render-voice.ts               same voice as the manifest: render the off-format clips + the missing ones
//                                             a NEW voice: refuses unless --all (re-renders every clip, so no line is left
//                                             in the old voice) and then records the new voice_id in the manifest
//   bun scripts/render-voice.ts --ids a,b,c   just these (in the manifest's voice only)
// Then open the Unity project once (or run the scene build) so new clips get .meta files, and commit Resources/BeeswaxLines.
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { ASK_CANNED } from "../src/ask";
import { LINES } from "../src/lines";
import { elevenLabsAvailable, ttsBytes } from "../src/tts";

const REPO = resolve(import.meta.dir, "../..");
const CLIPS = join(REPO, "unity/VirtualBeeSwarm/Assets/Resources/BeeswaxLines");
const UNITY_SRC = join(REPO, "unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm");
const MANIFEST = join(CLIPS, "_manifest.json");


const { values: args } = parseArgs({
  options: {
    ids: { type: "string" },
    check: { type: "boolean", default: false },
    "dry-run": { type: "boolean", default: false },
    all: { type: "boolean", default: false },
  },
});

type Manifest = { generated_at?: string; model: string; voice_id: string; lines: Record<string, Record<string, unknown>> };
const manifest = JSON.parse(readFileSync(MANIFEST, "utf8")) as Manifest;

function unityLines(): Map<string, string> {
  const out = new Map<string, string>();
  const rx = /(?:new GameLine|\bGet)\(\s*"((?:room|ask)_[a-z0-9_]+)"\s*,\s*"((?:[^"\\]|\\.)*)"/g;
  for (const f of readdirSync(UNITY_SRC, { recursive: true }) as string[]) {
    if (!f.endsWith(".cs")) continue;
    for (const m of readFileSync(join(UNITY_SRC, f), "utf8").matchAll(rx)) if (!out.has(m[1]!)) out.set(m[1]!, m[2]!.replace(/\\"/g, '"'));
  }
  return out;
}
const UNITY_TEXT = unityLines();

function textOf(id: string): string | null {
  return LINES[id]?.text ?? ASK_CANNED[id]?.text ?? UNITY_TEXT.get(id) ?? null;
}

/** MPEG-1 Layer III sample rate and bitrate of the first frame (after any ID3 tag), or null. */
function mp3Format(file: string): { hz: number; kbps: number } | null {
  const b = readFileSync(file);
  let i = 0;
  if (b.length > 10 && b.toString("latin1", 0, 3) === "ID3") i = 10 + ((b[6]! << 21) | (b[7]! << 14) | (b[8]! << 7) | b[9]!);
  const BITRATES = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
  const RATES: Record<number, number[]> = { 3: [44100, 48000, 32000], 2: [22050, 24000, 16000], 0: [11025, 12000, 8000] };
  const BITRATES_V2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
  for (; i + 4 < b.length; i++) {
    if (b[i] !== 0xff || (b[i + 1]! & 0xe0) !== 0xe0) continue;
    const version = (b[i + 1]! >> 3) & 3, layer = (b[i + 1]! >> 1) & 3;
    const br = (b[i + 2]! >> 4) & 15, sr = (b[i + 2]! >> 2) & 3;
    if (layer !== 1 || version === 1 || br === 0 || br === 15 || sr === 3) continue;
    return { hz: RATES[version]![sr]!, kbps: (version === 3 ? BITRATES : BITRATES_V2)[br]! };
  }
  return null;
}

function clipIds(): string[] {
  return readdirSync(CLIPS).filter((f) => f.endsWith(".mp3")).map((f) => f.slice(0, -4));
}
function offFormat(): string[] {
  return clipIds().filter((id) => {
    const f = mp3Format(join(CLIPS, `${id}.mp3`));
    return !f || f.hz !== 44100 || f.kbps !== 128;
  });
}
/** Room lines Beeswax says on the Quest that have no clip yet (ear-only lines belong to the glasses). */
function missing(): string[] {
  const have = new Set(clipIds());
  const ids = new Set<string>([
    ...Object.values(LINES).filter((l) => l.channel === "room").map((l) => l.id),
    ...Object.keys(ASK_CANNED),
    ...UNITY_TEXT.keys(),
  ]);
  return [...ids].filter((id) => !have.has(id) && textOf(id));
}

const voice = process.env.ELEVENLABS_VOICE_ID?.trim() || "";
const newVoice = Boolean(voice) && voice !== manifest.voice_id;
if (args.check) {
  const off = offFormat(), none = missing();
  console.log(`manifest voice ${manifest.voice_id}, model ${manifest.model}; ELEVENLABS_VOICE_ID ${voice || "(unset)"}${newVoice ? " (a different voice: run with --all)" : ""}`);
  console.log(`${off.length} clips off-format (another voice):${off.map((x) => `
  ${x}`).join("")}`);
  console.log(`${none.length} room lines with no clip:${none.map((x) => `
  ${x}`).join("")}`);
  process.exit(0);
}
if (newVoice && !args.all) throw new Error(`ELEVENLABS_VOICE_ID ${voice} isn't the clips' voice ${manifest.voice_id}: run with --all to re-render every clip in it, so Beeswax keeps one voice`);
if (newVoice && args.ids) throw new Error("--ids only in the manifest's own voice (a new voice needs --all)");
const ids = args.ids ? args.ids.split(",").map((s) => s.trim()).filter(Boolean)
  : args.all ? [...new Set([...clipIds(), ...missing()])].filter((id) => textOf(id))
  : [...new Set([...offFormat(), ...missing()])];
const jobs = ids.map((id) => ({ id, text: textOf(id) }));
const unknown = jobs.filter((j) => !j.text).map((j) => j.id);
if (unknown.length) throw new Error(`no text for: ${unknown.join(", ")}`);
const chars = jobs.reduce((n, j) => n + j.text!.length, 0);
const target = voice || manifest.voice_id;
console.log(`${jobs.length} clips (${chars} characters) in voice ${target} with ${manifest.model}`);
if (args["dry-run"]) {
  for (const j of jobs) console.log(`  ${j.id}  "${j.text}"`);
  process.exit(0);
}
if (!elevenLabsAvailable()) throw new Error("set ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID (hive/.env) first");
if (newVoice) {
  manifest.voice_id = voice; // the whole set moves to the new voice (every clip is in the list)
  manifest.generated_at = new Date().toISOString();
}

for (const j of jobs) {
  const mp3 = await ttsBytes(j.text!, { format: "mp3_44100_128", model: manifest.model, voiceId: target, timeoutMs: 60_000 });
  writeFileSync(join(CLIPS, `${j.id}.mp3`), mp3);
  const duration_s = Math.round((mp3.length / 16000) * 100) / 100; // 128 kbps = 16 kB per second
  const entry = manifest.lines[j.id];
  if (entry) Object.assign(entry, { caption: j.text, duration_s });
  else manifest.lines[j.id] = { channel: "room", caption: j.text, beat: null, tags: [], duration_s, mp3: `unity/${j.id}.mp3`, pcm: null };
  writeFileSync(MANIFEST, `${JSON.stringify(manifest, null, 2)}\n`); // after every clip: a failed run never leaves stale entries
  console.log(`  ${j.id}.mp3  ${(mp3.length / 1024).toFixed(0)} KB  "${j.text}"`);
}
console.log(`Done. Open the Unity project once so new clips get .meta files, then commit Resources/BeeswaxLines.`);
