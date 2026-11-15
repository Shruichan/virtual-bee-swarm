#!/usr/bin/env bun
// Render the Live Bee Scout lines (lines/scout_lines.json + lines/scout_lines_booth.json) to
// lines/scout_audio/<id>.mp3 + manifest.json. An id may live in only one of the two files.
//   bun scripts/scout-render.ts                 ElevenLabs (needs ELEVENLABS_API_KEY + ELEVENLABS_VOICE_ID, from the
//                                               env or hive/.env); same model as prerender.ts (TTS_MODEL). The manifest
//                                               names the voice from VOICE_NAMES, or --voice-name
//   bun scripts/scout-render.ts --voice-id ID   override ELEVENLABS_VOICE_ID for this run
//   bun scripts/scout-render.ts --placeholder   macOS `say` + ffmpeg, dev only (never overwrites a Harry render
//                                               of unchanged text unless --all)
//   bun scripts/scout-render.ts --all           re-render every line, not just changed ones
//   bun scripts/scout-render.ts --only id,id    re-render just these ids
// Output: mp3, mono, 44.1 kHz, 64 kbps, loudness-normalized to -16 LUFS. Chime lines get a two-note ding in
// front of the voice. manifest.json: { "<id>": { ms, voice, chars, hash } }. The game uses ms for timing.
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import type { ScoutLine } from "../src/scout-protocol";

const HIVE = resolve(import.meta.dir, "..");
export const SCOUT_LINES_FILE = join(HIVE, "lines", "scout_lines.json");
export const SCOUT_BOOTH_LINES_FILE = join(HIVE, "lines", "scout_lines_booth.json");
export const SCOUT_AUDIO_DIR = join(HIVE, "lines", "scout_audio");
export const SCOUT_MANIFEST_FILE = join(SCOUT_AUDIO_DIR, "manifest.json");

/** Bump to force a re-render of everything (e.g. the ding or the encode settings changed). */
const RENDER_VERSION = 3;
const PLACEHOLDER_VOICES = ["Rocko (English (US))", "Ralph", "Fred"];
const PLACEHOLDER_RATE = 185; // words per minute, close to Harry's 14.4 characters a second

/** ElevenLabs voice ids -> the name written to the manifest. Voice ids aren't secret; the API key is. */
export const VOICE_NAMES: Record<string, string> = { QDutJflVBVtxM2TBSenF: "beeswax-cute-sassy" };
export const PLACEHOLDER = "placeholder";
/** "placeholder", or the ElevenLabs voice's name (e.g. "beeswax-cute-sassy", "harry"). */
export type ScoutVoice = string;
export interface ManifestEntry { ms: number; voice: ScoutVoice; chars: number; hash: string }
export type ScoutManifest = Record<string, ManifestEntry>;

/** What the audio for a line depends on, apart from the voice. */
export function lineHash(line: Pick<ScoutLine, "kind" | "text">): string {
  const chime = line.kind === "chime" ? "|ding" : "";
  return createHash("sha1").update(`v${RENDER_VERSION}|${line.text}${chime}`).digest("hex").slice(0, 12);
}

export function readLinesFile(file: string): ScoutLine[] {
  return existsSync(file) ? (JSON.parse(readFileSync(file, "utf8")) as ScoutLine[]) : [];
}

/** Both lines files, demo first. Throws on an id defined twice (the game would silently pick the booth one). */
export function readScoutLines(): ScoutLine[] {
  const all = [...readLinesFile(SCOUT_LINES_FILE), ...readLinesFile(SCOUT_BOOTH_LINES_FILE)];
  const seen = new Set<string>();
  for (const l of all) {
    if (seen.has(l.id)) throw new Error(`line id ${l.id} is defined twice across scout_lines*.json`);
    seen.add(l.id);
  }
  return all;
}

export function readManifest(): ScoutManifest {
  return existsSync(SCOUT_MANIFEST_FILE) ? (JSON.parse(readFileSync(SCOUT_MANIFEST_FILE, "utf8")) as ScoutManifest) : {};
}

/** Bun loads .env from the cwd only; also read hive/.env so the script works from anywhere. */
function loadHiveEnv(): void {
  const file = join(HIVE, ".env");
  if (!existsSync(file)) return;
  for (const raw of readFileSync(file, "utf8").split("\n")) {
    const m = raw.match(/^\s*(?:export\s+)?([A-Z0-9_]+)\s*=\s*(.*?)\s*$/);
    if (!m || process.env[m[1]!]) continue;
    process.env[m[1]!] = m[2]!.replace(/^(['"])(.*)\1$/, "$2");
  }
}

function run(cmd: string[]): string {
  const p = Bun.spawnSync(cmd, { stdout: "pipe", stderr: "pipe" });
  if (p.exitCode !== 0) throw new Error(`${cmd[0]} failed: ${p.stderr.toString().trim().split("\n").slice(-3).join(" | ")}`);
  return p.stdout.toString();
}

function durationMs(file: string): number {
  const out = run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file]);
  return Math.round(Number(out.trim()) * 1000);
}

function pickSayVoice(): string {
  const have = run(["say", "-v", "?"]);
  return PLACEHOLDER_VOICES.find((v) => have.includes(`${v} `)) ?? "Alex";
}

const TARGET_LUFS = -16;
const LOUDNORM = `loudnorm=I=${TARGET_LUFS}:TP=-1.5:LRA=11`;
const ENCODE = ["-ac", "1", "-ar", "44100", "-codec:a", "libmp3lame", "-b:a", "64k"];

/** Integrated loudness (LUFS) of a file. */
function lufs(file: string): number {
  const p = Bun.spawnSync(["ffmpeg", "-hide_banner", "-i", file, "-af", "loudnorm=print_format=json", "-f", "null", "-"], { stderr: "pipe" });
  const m = p.stderr.toString().match(/"input_i"\s*:\s*"(-?[\d.]+)"/);
  return m ? Number(m[1]) : NaN;
}

/** encode(), then one gain correction when single-pass loudnorm misses (it does on very short clips). */
function encodeLevel(input: string, out: string, ding: boolean): void {
  encode(input, out, ding);
  const off = TARGET_LUFS - lufs(out);
  if (Number.isFinite(off) && Math.abs(off) > 0.7) encode(input, out, ding, off);
}

/** Any audio file -> the shipped mp3 format, with the two-note ding in front when `ding`. */
function encode(input: string, out: string, ding: boolean, gainDb = 0): void {
  const voiceFx = gainDb ? `${LOUDNORM},volume=${gainDb.toFixed(1)}dB,alimiter=limit=0.89:level=false` : LOUDNORM;
  if (!ding) {
    run(["ffmpeg", "-y", "-v", "error", "-i", input, "-af", voiceFx, ...ENCODE, out]);
    return;
  }
  // E6 then A6, about 0.3 s together, each with a quick decay, then a short breath before the voice.
  const fmt = "aformat=sample_rates=44100:channel_layouts=mono";
  const graph = [
    `sine=f=1318.5:d=0.12,${fmt},volume=0.35,afade=t=out:st=0.02:d=0.10[n1]`,
    `sine=f=1760:d=0.2,${fmt},volume=0.35,afade=t=out:st=0.03:d=0.17[n2]`,
    `anullsrc=r=44100:cl=mono:d=0.08,${fmt}[gap]`,
    `[0:a]aresample=44100,${fmt}[v]`,
    `[v]${voiceFx},aresample=44100,${fmt}[vn]`,
    `[n1][n2][gap][vn]concat=n=4:v=0:a=1[out]`,
  ].join(";");
  run(["ffmpeg", "-y", "-v", "error", "-i", input, "-filter_complex", graph, "-map", "[out]", ...ENCODE, out]);
}

async function renderEleven(text: string, tmp: string): Promise<string> {
  const { ttsBytes, TTS_MODEL } = await import("../src/tts");
  const file = join(tmp, "eleven.mp3");
  writeFileSync(file, await ttsBytes(text, { format: "mp3_44100_64", model: TTS_MODEL, timeoutMs: 60_000 }));
  return file;
}

function renderSay(text: string, voice: string, tmp: string): string {
  const file = join(tmp, "say.aiff");
  run(["say", "-v", voice, "-r", String(PLACEHOLDER_RATE), "-o", file, text]);
  return file;
}

async function main(): Promise<void> {
  const { values: args } = parseArgs({
    options: {
      placeholder: { type: "boolean", default: false },
      all: { type: "boolean", default: false },
      only: { type: "string" },
      "voice-id": { type: "string" },
      "voice-name": { type: "string" },
    },
  });
  loadHiveEnv();
  if (args["voice-id"]) process.env.ELEVENLABS_VOICE_ID = args["voice-id"];
  const voiceId = process.env.ELEVENLABS_VOICE_ID?.trim() ?? "";
  const elevenReady = Boolean(process.env.ELEVENLABS_API_KEY?.trim() && voiceId);
  const voice: ScoutVoice = args.placeholder ? PLACEHOLDER : (args["voice-name"] ?? VOICE_NAMES[voiceId] ?? "harry");
  if (voice !== PLACEHOLDER && !elevenReady) {
    throw new Error("set ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID (env or hive/.env), or pass --placeholder");
  }

  const lines = readScoutLines();
  const only = args.only?.split(",").map((s) => s.trim()).filter(Boolean);
  const unknown = only?.filter((id) => !lines.some((l) => l.id === id)) ?? [];
  if (unknown.length) throw new Error(`unknown line ids: ${unknown.join(", ")}`);

  mkdirSync(SCOUT_AUDIO_DIR, { recursive: true });
  const old = readManifest();
  const manifest: ScoutManifest = {};
  const jobs: ScoutLine[] = [];
  for (const line of lines) {
    if (!line.text.trim()) continue; // empty text = the line is switched off: no audio
    const prev = old[line.id];
    const file = join(SCOUT_AUDIO_DIR, `${line.id}.mp3`);
    const fresh = prev && existsSync(file) && prev.hash === lineHash(line);
    // A placeholder run never replaces a current real-voice render; a real-voice run replaces everything else.
    const keep = fresh && (prev.voice === voice || (voice === PLACEHOLDER && prev.voice !== PLACEHOLDER));
    if (prev && existsSync(file)) manifest[line.id] = prev;
    if (only ? only.includes(line.id) : args.all || !keep) jobs.push(line);
  }

  const sayVoice = voice === PLACEHOLDER ? pickSayVoice() : "";
  console.log(`${lines.length} lines, ${jobs.length} to render (${voice}${sayVoice ? `: ${sayVoice}` : ""})`);
  const tmp = mkdtempSync(join(tmpdir(), "scout-render-"));
  const failed: string[] = [];
  // Same text in two ids (demo and booth copies) -> one render, copied, so both sound identical.
  const rendered = new Map<string, string>();
  try {
    for (const line of jobs) {
      const out = join(SCOUT_AUDIO_DIR, `${line.id}.mp3`);
      const twin = rendered.get(lineHash(line));
      if (twin) {
        copyFileSync(join(SCOUT_AUDIO_DIR, `${twin}.mp3`), out);
        manifest[line.id] = { ...manifest[twin]!, chars: line.text.length };
        console.log(`  ${line.id}  copied`);
        continue;
      }
      try {
        const raw = voice === PLACEHOLDER ? renderSay(line.text, sayVoice, tmp) : await renderEleven(line.text, tmp);
        encodeLevel(raw, out, line.kind === "chime");
        manifest[line.id] = { ms: durationMs(out), voice, chars: line.text.length, hash: lineHash(line) };
        rendered.set(lineHash(line), line.id);
        console.log(`  ${line.id}  ${manifest[line.id]!.ms} ms`);
      } catch (e) {
        failed.push(line.id);
        console.error(`  FAILED ${line.id}: ${e instanceof Error ? e.message : String(e)}`);
      }
    }
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }

  // Drop audio for ids that left the lines files or were switched off.
  const ids = new Set(lines.filter((l) => l.text.trim()).map((l) => l.id));
  for (const id of Object.keys(manifest)) if (!ids.has(id)) delete manifest[id];
  for (const f of readdirSync(SCOUT_AUDIO_DIR)) {
    if (f.endsWith(".mp3") && !ids.has(f.slice(0, -4))) rmSync(join(SCOUT_AUDIO_DIR, f));
  }
  const ordered: ScoutManifest = {};
  for (const l of lines) if (manifest[l.id]) ordered[l.id] = manifest[l.id]!;
  writeFileSync(SCOUT_MANIFEST_FILE, `${JSON.stringify(ordered, null, 2)}\n`);

  const bytes = readdirSync(SCOUT_AUDIO_DIR).reduce((n, f) => n + statSync(join(SCOUT_AUDIO_DIR, f)).size, 0);
  console.log(`Wrote lines/scout_audio/manifest.json (${Object.keys(ordered).length} lines, ${(bytes / 1e6).toFixed(2)} MB)`);
  if (failed.length) {
    console.error(`${failed.length} renders failed: ${failed.join(", ")}. Re-run to retry just those.`);
    process.exit(1);
  }
}

if (import.meta.main) {
  main().catch((e) => {
    console.error(`scout-render failed: ${e instanceof Error ? e.message : String(e)}`);
    process.exit(1);
  });
}
