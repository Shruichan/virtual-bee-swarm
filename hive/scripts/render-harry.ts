#!/usr/bin/env bun
// One voice for Beeswax (dartiros): re-render clips in the recorded voice (Unity BeeswaxLines/_manifest.json voice_id,
// "Harry", with its model) straight into Unity's Resources/BeeswaxLines. Paid: needs ELEVENLABS_API_KEY, and refuses
// to run if ELEVENLABS_VOICE_ID is a different voice. Text comes from lines/lines.json, lines/ask.json, or the
// Unity-only lines in the game C# (new GameLine / RideLines.Get).
//   bun scripts/render-harry.ts --check            list clips that aren't 44.1 kHz / 128 kbps (the other voice) and lines with no clip
//   bun scripts/render-harry.ts --dry-run          what the default list would render (ids, characters)
//   bun scripts/render-harry.ts                    render the default list (the 12 off-voice clips + the 20 caption-only lines)
//   bun scripts/render-harry.ts --ids a,b,c        render just these
// Then open the Unity project once (or run the scene build) so the new clips get their .meta files, and commit both.
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

/** Clips rendered by the old local "younger" voice (24 kHz / 32 kbps), and lines that never got a clip. */
const OFF_VOICE = [
  "room_hook_1", "room_smoker_banana", "room_wasps_arrive", "room_feed_lift_hint", "room_feed_rock_retry", "room_pal_show_me",
  "room_meet_hey_beeswax", "room_t0_fake_moon", "ask_bananas", "ask_drone_sting", "ask_help_most", "ask_wildfire",
];
const NO_CLIP = [
  "room_pal_boop_blush", "room_pal_boop_file", "room_pal_boop_giggle", "room_pal_dance", "room_pal_highfive", "room_pal_honey_caps",
  "room_pal_honey_help", "room_pal_honey_offer", "room_pal_honey_winter", "room_pal_palm", "room_pal_pet", "room_pal_wave",
  "room_ask_anything", "ask_killing", "ask_sting_help", "ask_offlist", "ask_people", "ask_stall", "ask_not_heard", "ask_offline",
];

const { values: args } = parseArgs({
  options: {
    ids: { type: "string" },
    check: { type: "boolean", default: false },
    "dry-run": { type: "boolean", default: false },
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

if (args.check) {
  const off: string[] = [];
  for (const id of new Set([...Object.keys(manifest.lines), ...OFF_VOICE])) {
    const file = join(CLIPS, `${id}.mp3`);
    if (!existsSync(file)) continue;
    const f = mp3Format(file);
    if (!f || f.hz !== 44100 || f.kbps !== 128) off.push(`${id} (${f ? `${f.hz} Hz ${f.kbps} kbps` : "unreadable"})`);
  }
  const missing = NO_CLIP.filter((id) => !existsSync(join(CLIPS, `${id}.mp3`)));
  console.log(`voice ${manifest.voice_id}, model ${manifest.model}`);
  console.log(`${off.length} clips not in the recorded format:${off.map((x) => `\n  ${x}`).join("")}`);
  console.log(`${missing.length} lines with no clip:${missing.map((x) => `\n  ${x}`).join("")}`);
  process.exit(0);
}

const ids = args.ids ? args.ids.split(",").map((s) => s.trim()).filter(Boolean) : [...OFF_VOICE, ...NO_CLIP];
const jobs = ids.map((id) => ({ id, text: textOf(id) }));
const unknown = jobs.filter((j) => !j.text).map((j) => j.id);
if (unknown.length) throw new Error(`no text for: ${unknown.join(", ")}`);
const chars = jobs.reduce((n, j) => n + j.text!.length, 0);
console.log(`${jobs.length} clips (${chars} characters) in voice ${manifest.voice_id} with ${manifest.model}`);
if (args["dry-run"]) {
  for (const j of jobs) console.log(`  ${j.id}  "${j.text}"`);
  process.exit(0);
}
if (!elevenLabsAvailable()) throw new Error("set ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID (hive/.env) first");
const voice = process.env.ELEVENLABS_VOICE_ID!.trim();
if (voice !== manifest.voice_id) throw new Error(`ELEVENLABS_VOICE_ID is ${voice}, but the recorded lines are ${manifest.voice_id}: set it to that first`);

for (const j of jobs) {
  const mp3 = await ttsBytes(j.text!, { format: "mp3_44100_128", model: manifest.model, voiceId: manifest.voice_id, timeoutMs: 60_000 });
  writeFileSync(join(CLIPS, `${j.id}.mp3`), mp3);
  const duration_s = Math.round((mp3.length / 16000) * 100) / 100; // 128 kbps = 16 kB per second
  const entry = manifest.lines[j.id];
  if (entry) Object.assign(entry, { caption: j.text, duration_s });
  else manifest.lines[j.id] = { channel: "room", caption: j.text, beat: null, tags: [], duration_s, mp3: `unity/${j.id}.mp3`, pcm: null };
  writeFileSync(MANIFEST, `${JSON.stringify(manifest, null, 2)}\n`); // after every clip: a failed run never leaves stale entries
  console.log(`  ${j.id}.mp3  ${(mp3.length / 1024).toFixed(0)} KB  "${j.text}"`);
}
console.log(`Done. Open the Unity project once so new clips get .meta files, then commit Resources/BeeswaxLines.`);
