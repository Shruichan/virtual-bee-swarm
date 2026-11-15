#!/usr/bin/env bun
// Design Beeswax's voice with the ElevenLabs Voice Design API (paid; needs ELEVENLABS_API_KEY).
//   bun scripts/voice-design.ts                      generate previews -> out/voice-previews/preview_<n>.mp3
//   bun scripts/voice-design.ts --v3 --seed 7        same, with the eleven_ttv_v3 design model / a fixed seed
//   bun scripts/voice-design.ts --pick 2 --write-env create the voice from preview 2, print its voice_id,
//                                                    and set ELEVENLABS_VOICE_ID in hive/.env
// Listen to the previews yourself; this script never plays audio.
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { ElevenLabsClient } from "@elevenlabs/elevenlabs-js";

const HIVE = resolve(import.meta.dir, "..");
const OUT = join(HIVE, "out", "voice-previews");
const INDEX = join(OUT, "previews.json");
const ENV_FILE = join(HIVE, ".env");

const VOICE_DESCRIPTION =
  "Original cartoon character voice: a loud, petty, fast-talking bee who is a union shop steward. " +
  "Adult male, gravelly and slightly buzzy with a nasal edge, big Brooklyn-ish street energy, rapid-fire comedic " +
  "delivery, exasperated and theatrical, always about to file a grievance. Punchy animated-film energy. " +
  "Clean studio recording, no background noise or music. Not an impression of any real person or existing character.";

// 100-1000 characters, in his voice, so the previews show the range we need.
const SAMPLE_TEXT =
  "Oh. Hi. Beeswax. Shop steward, Local Twenty Thousand. Four things are killing my hive. Fix them, or the union " +
  "moves us to the Moon. Store-bought honey? Grievance filed! That stuff can carry spores that wipe out a hive. " +
  "Motion carried. Noted for the minutes. I keep a spreadsheet, pal. One jar left. My personal jar. TAKE ME TO THE MOON!";

interface PreviewIndex {
  created_at: string;
  model: string;
  description: string;
  previews: { n: number; generated_voice_id: string; file: string; duration_secs: number }[];
}

const { values: args } = parseArgs({
  options: {
    pick: { type: "string" },
    "write-env": { type: "boolean", default: false },
    v3: { type: "boolean", default: false },
    seed: { type: "string" },
    guidance: { type: "string" },
    name: { type: "string", default: "Beeswax (HONEYMOON)" },
    help: { type: "boolean", short: "h", default: false },
  },
});

function die(msg: string): never {
  console.error(msg);
  process.exit(1);
}

function client(): ElevenLabsClient {
  const apiKey = process.env.ELEVENLABS_API_KEY?.trim();
  if (!apiKey) die("ELEVENLABS_API_KEY is not set (put it in hive/.env; bun loads it when run from hive/).");
  return new ElevenLabsClient({ apiKey, timeoutInSeconds: 180, maxRetries: 2 });
}

async function design(): Promise<void> {
  const model = args.v3 ? "eleven_ttv_v3" : "eleven_multilingual_ttv_v2";
  const seed = args.seed ? Number(args.seed) : undefined;
  const guidanceScale = args.guidance ? Number(args.guidance) : undefined;
  const eleven = client();
  console.log(`Designing voice with ${model}${seed !== undefined ? ` seed=${seed}` : ""}...`);
  const res = await eleven.textToVoice.design({
    voiceDescription: VOICE_DESCRIPTION,
    text: SAMPLE_TEXT,
    modelId: model,
    outputFormat: "mp3_44100_128",
    ...(seed !== undefined && Number.isFinite(seed) ? { seed } : {}),
    ...(guidanceScale !== undefined && Number.isFinite(guidanceScale) ? { guidanceScale } : {}),
  });
  if (!res.previews?.length) die("Voice Design returned no previews.");
  mkdirSync(OUT, { recursive: true });
  const index: PreviewIndex = { created_at: new Date().toISOString(), model, description: VOICE_DESCRIPTION, previews: [] };
  res.previews.forEach((p, i) => {
    const file = join(OUT, `preview_${i + 1}.mp3`);
    writeFileSync(file, Buffer.from(p.audioBase64, "base64"));
    index.previews.push({ n: i + 1, generated_voice_id: p.generatedVoiceId, file, duration_secs: p.durationSecs });
    console.log(`  ${i + 1}: ${file} (${p.durationSecs.toFixed(1)} s)`);
  });
  writeFileSync(INDEX, JSON.stringify(index, null, 2));
  console.log(`\nListen, then: bun scripts/voice-design.ts --pick <n> --write-env`);
}

/** Set KEY=value in an env file, keeping every other line as is. */
function setEnvVar(file: string, key: string, value: string): void {
  const text = existsSync(file) ? readFileSync(file, "utf8") : "";
  const line = `${key}=${value}`;
  const rx = new RegExp(`^${key}=.*$`, "m");
  const next = rx.test(text) ? text.replace(rx, line) : `${text}${text && !text.endsWith("\n") ? "\n" : ""}${line}\n`;
  writeFileSync(file, next);
}

async function pick(n: number): Promise<void> {
  if (!existsSync(INDEX)) die(`No ${INDEX}. Generate previews first.`);
  const index = JSON.parse(readFileSync(INDEX, "utf8")) as PreviewIndex;
  const chosen = index.previews.find((p) => p.n === n);
  if (!chosen) die(`No preview ${n}. Have: ${index.previews.map((p) => p.n).join(", ")}`);
  const voice = await client().textToVoice.create({
    voiceName: args.name!,
    voiceDescription: index.description,
    generatedVoiceId: chosen.generated_voice_id,
    labels: { project: "honeymoon", character: "beeswax" },
    playedNotSelectedVoiceIds: index.previews.filter((p) => p.n !== n).map((p) => p.generated_voice_id),
  });
  console.log(`voice_id: ${voice.voiceId}`);
  if (args["write-env"]) {
    setEnvVar(ENV_FILE, "ELEVENLABS_VOICE_ID", voice.voiceId);
    console.log(`Wrote ELEVENLABS_VOICE_ID to ${ENV_FILE}`);
  } else {
    console.log(`Add to hive/.env: ELEVENLABS_VOICE_ID=${voice.voiceId}  (or re-run with --write-env)`);
  }
}

async function main(): Promise<void> {
  if (args.help) {
    console.log(readFileSync(import.meta.path, "utf8").split("\n").slice(1, 8).join("\n"));
    return;
  }
  if (args.pick !== undefined) {
    const n = Number(args.pick);
    if (!Number.isInteger(n) || n < 1) die("--pick takes a preview number (1, 2, 3...)");
    await pick(n);
  } else {
    await design();
  }
}

if (import.meta.main) {
  main().catch((e) => die(`voice-design failed: ${e instanceof Error ? e.message : String(e)}`));
}
