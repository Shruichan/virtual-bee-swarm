#!/usr/bin/env bun
// Plan B for ASK BEESWAX's canned answers (only if the voice freeze is lifted): render every ask_* line in
// lines/ask.json with the live voice to hive/out/unity/<id>.mp3 (paid, about 750 characters; existing files are
// skipped unless --force). Copy them into unity/.../Resources/BeeswaxLines/ so the card's chips work offline with
// audio. Plan A needs none of this: the Hive renders the same texts into memory at start (ask.warmAudio).
//   bun scripts/prerender-ask.ts [--force] [--dry-run]
import { existsSync, mkdirSync, statSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { ASK_CANNED } from "../src/ask";
import { TTS_MODEL, elevenLabsAvailable, ttsBytes } from "../src/tts";

const UNITY = join(resolve(import.meta.dir, ".."), "out", "unity");
const { values: args } = parseArgs({ options: { force: { type: "boolean", default: false }, "dry-run": { type: "boolean", default: false } } });

const jobs = Object.values(ASK_CANNED).filter((c) => {
  const file = join(UNITY, `${c.id}.mp3`);
  return args.force || !existsSync(file) || statSync(file).size === 0;
});
const chars = jobs.reduce((n, c) => n + c.text.length, 0);
console.log(`${jobs.length} ask lines to render (${chars} characters, ${TTS_MODEL})`);
if (args["dry-run"]) process.exit(0);
if (!elevenLabsAvailable()) throw new Error("set ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID");
mkdirSync(UNITY, { recursive: true });
for (const c of jobs) {
  const mp3 = await ttsBytes(c.text, { format: "mp3_44100_128", timeoutMs: 60_000 });
  writeFileSync(join(UNITY, `${c.id}.mp3`), mp3);
  console.log(`  ${c.id}.mp3  ${(mp3.length / 1024).toFixed(0)} KB  "${c.text}"`);
}
