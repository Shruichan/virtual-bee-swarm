#!/usr/bin/env bun
// Dev harness for the Live Bee Scout pages (no Hive, no vision worker, no glasses needed).
// Serves /scout and /scout/slides, a fake /scout/state + /scout/events SSE that replays a scripted demo
// loop, /scout/audio/<id>.mp3 from an audio folder, accepts POST /scout/slide, and fakes the vision
// worker API under /fake-vision (health, enrol, slides.json, placeholder slide images).
//
//   bun scripts/scout-dashboard-dev.ts                       # http://localhost:8243/
//   open "http://localhost:8243/scout?token=dev&stream=scouttest&vision=http://localhost:8243/fake-vision"
//   open "http://localhost:8243/scout/slides?token=dev&vision=http://localhost:8243/fake-vision"
//
// Flags: --port N (default 8243), --speed X (demo speed multiplier, default 1),
//        --audio-dir DIR (default hive/lines/scout_audio; files named <line_id>.mp3).
import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { scoutDashboardHtml, scoutSlidesHtml } from "../src/scout-dashboard";
import {
  SCOUT_BACKGROUND,
  SCOUT_LABELS,
  SCOUT_LINES,
  type ScoutAnswer,
  type ScoutClass,
  type ScoutEvent,
  type ScoutHop,
  type ScoutLabel,
  type ScoutLine,
  type ScoutSlide,
  type ScoutState,
  type VisionHealth,
  type VisionSlide,
} from "../src/scout-protocol";

function args(argv: string[]): Record<string, string> {
  const out: Record<string, string> = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    if (a.startsWith("--")) out[a.slice(2)] = argv[i + 1] && !argv[i + 1]!.startsWith("--") ? argv[++i]! : "1";
  }
  return out;
}
const opt = args(process.argv.slice(2));
const PORT = Number(opt.port ?? 8243);
const SPEED = Math.max(0.1, Number(opt.speed ?? 1));
const HIVE = resolve(import.meta.dir, "..");
const AUDIO_DIR = resolve(opt["audio-dir"] ?? join(HIVE, "lines", "scout_audio"));
const LINES: ScoutLine[] = await Bun.file(join(HIVE, "lines", "scout_lines.json")).json();
const TEXT = new Map(LINES.map((l) => [l.id, l.text]));

// ------------------------------------------------------------------ SSE fan-out
const enc = new TextEncoder();
const clients = new Set<ReadableStreamDefaultController<Uint8Array>>();
function send(c: ReadableStreamDefaultController<Uint8Array>, chunk: string) {
  try {
    c.enqueue(enc.encode(chunk));
  } catch {
    clients.delete(c);
  }
}
function emit(ev: ScoutEvent) {
  const chunk = `data: ${JSON.stringify(ev)}\n\n`;
  for (const c of clients) send(c, chunk);
  if (ev.type !== "frame") console.log(new Date().toISOString().slice(11, 19), ev.type, JSON.stringify(ev).slice(0, 110));
}
setInterval(() => {
  for (const c of clients) send(c, ": ping\n\n");
}, 15000);

// ------------------------------------------------------------------ fake game state + frames
const state: ScoutState = { phase: "intro", found: [], skipped: [], offer: null, current: null, started_at: null };
function pushState(patch: Partial<ScoutState>) {
  Object.assign(state, patch);
  emit({ type: "state", state: { ...state, found: [...state.found], skipped: [...state.skipped] } });
}

const CLASSES: ScoutClass[] = [...SCOUT_LABELS, SCOUT_BACKGROUND];
// What each object gets confused with, so the runner-up bar moves too.
const CONFUSE: Partial<Record<ScoutClass, ScoutClass>> = {
  beehive: "bee_hotel", bee_hotel: "beehive", honey_jar: "bug_spray", bug_spray: "honey_jar", native_flower: "pebble_dish", pebble_dish: "native_flower",
};
let target: ScoutClass = SCOUT_BACKGROUND;
const level = Object.fromEntries(CLASSES.map((c) => [c, 0.1])) as Record<ScoutClass, number>;
let fps = 7.5;
setInterval(() => {
  for (const c of CLASSES) {
    let goal = 0.06 + Math.random() * 0.08;
    if (c === target) goal = c === SCOUT_BACKGROUND ? 0.72 : 0.84;
    else if (c === SCOUT_BACKGROUND) goal = target === SCOUT_BACKGROUND ? 0.72 : 0.2;
    else if (CONFUSE[target] === c) goal = 0.38;
    level[c] = Math.min(0.99, Math.max(0, level[c] + (goal - level[c]) * 0.28 + (Math.random() - 0.5) * 0.06));
  }
  const top = CLASSES.map((label) => ({ label, score: Math.round(level[label] * 1000) / 1000 }))
    .sort((a, b) => b.score - a.score)
    .slice(0, 3);
  fps = Math.max(5, Math.min(9.5, fps + (Math.random() - 0.5) * 0.4));
  state.current = top[0]!;
  emit({ type: "frame", frame: { at: Date.now(), frame_ms: 38 + Math.random() * 22, fps, top, source: "rtsp://127.0.0.1:8554/scout" } });
}, 200);

// ------------------------------------------------------------------ the scripted demo loop
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms / SPEED));
const jitter = (lo: number, hi: number) => Math.round(lo + Math.random() * (hi - lo));
function say(id: string) {
  emit({ type: "say", line_id: id, caption: TEXT.get(id) ?? "", audio_url: `/scout/audio/${id}.mp3`, at: Date.now() });
}
function latency(hop: ScoutHop, ms: number) {
  emit({ type: "latency", hop, ms });
}

interface Beat { label: ScoutLabel; reply: string; answer: ScoutAnswer; slide?: boolean }
const BEATS: Beat[] = [
  { label: "beehive", reply: "yeah", answer: "yes" },
  { label: "native_flower", reply: "sure, go on", answer: "yes" },
  { label: "honey_jar", reply: "nah", answer: "no" },
  { label: "pebble_dish", reply: "yes please", answer: "yes", slide: true },
  { label: "bug_spray", reply: "shut up", answer: "rude" },
  { label: "bee_hotel", reply: "uh, what was that", answer: "other" },
  { label: "honey_jar", reply: "okay, fine", answer: "yes", slide: true },
  { label: "bug_spray", reply: "yes", answer: "yes" },
  { label: "bee_hotel", reply: "yeah tell me", answer: "yes" },
];

async function demo() {
  for (;;) {
    target = SCOUT_BACKGROUND;
    pushState({ phase: "intro", found: [], skipped: [], offer: null, started_at: Date.now() });
    say(SCOUT_LINES.intro[0]!);
    await sleep(4500);
    say(SCOUT_LINES.intro[1]!);
    await sleep(3500);
    pushState({ phase: "looking" });
    for (const b of BEATS) {
      target = SCOUT_BACKGROUND;
      await sleep(jitter(2200, 3200));
      let slideAt = 0;
      if (b.slide) {
        slideAt = Date.now();
        emit({ type: "slide", label: b.label, at: slideAt });
      }
      const lookStart = Date.now();
      target = b.label;
      await sleep(jitter(1500, 2100));
      const look = Date.now() - lookStart;
      emit({ type: "found", label: b.label, score: state.current?.score ?? 0.8, at: Date.now(), look_ms: look });
      latency("frame_to_found", look);
      if (slideAt) latency("slide_to_found", Date.now() - slideAt);
      const t0 = Date.now();
      await sleep(jitter(90, 220));
      say(SCOUT_LINES.chime);
      latency("found_to_say", Date.now() - t0);
      pushState({ phase: "offering", offer: b.label });
      await sleep(1300);
      say(SCOUT_LINES.hook(b.label));
      await sleep(3000);
      if (b.answer === "other") {
        emit({ type: "heard", text: b.reply, answer: "other", at: Date.now() });
        await sleep(2500);
        say(SCOUT_LINES.silence);
      } else {
        emit({ type: "heard", text: b.reply, answer: b.answer, at: Date.now() });
      }
      if (b.answer === "yes") {
        const h = Date.now();
        await sleep(jitter(250, 600));
        latency("answer_to_fact", Date.now() - h);
        pushState({ phase: "telling" });
        say(SCOUT_LINES.fact(b.label));
        await sleep(6500);
        const found = [...state.found, b.label];
        pushState({ found, skipped: state.skipped.filter((l) => l !== b.label) });
        say(found.length >= SCOUT_LABELS.length ? SCOUT_LINES.done : SCOUT_LINES.progress(found.length));
        await sleep(found.length >= SCOUT_LABELS.length ? 0 : 2500);
      } else {
        await sleep(400);
        if (b.answer === "no") say(SCOUT_LINES.no[0]!);
        if (b.answer === "rude") say(SCOUT_LINES.rude);
        pushState({ phase: "looking", offer: null, skipped: state.skipped.includes(b.label) ? state.skipped : [...state.skipped, b.label] });
        await sleep(2500);
      }
      if (state.found.length < SCOUT_LABELS.length) pushState({ phase: "looking", offer: null });
    }
    target = SCOUT_BACKGROUND;
    pushState({ phase: "done", offer: null });
    await sleep(12000);
  }
}

// ------------------------------------------------------------------ fake vision worker (under /fake-vision)
const refs: Record<string, number> = Object.fromEntries(CLASSES.map((c, i) => [c, c === "bee_hotel" ? 0 : 8 + ((i * 7) % 13)]));
const COLORS: Record<ScoutLabel, string> = {
  beehive: "#d9952b", native_flower: "#b76ad6", honey_jar: "#f2c14e", pebble_dish: "#6aa6c9", bug_spray: "#d9534f", bee_hotel: "#8a6a45",
};
function slideList(origin: string): VisionSlide[] {
  return SCOUT_LABELS.flatMap((label) => [1, 2].map((n) => ({ label, url: `${origin}/fake-vision/img/${label}/${n}.svg`, credit: `placeholder ${n}` })));
}
function slideSvg(label: ScoutLabel, n: number): string {
  const c = COLORS[label];
  return `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="${n === 1 ? 800 : 1200}" viewBox="0 0 1200 ${n === 1 ? 800 : 1200}">
<rect width="100%" height="100%" fill="#1d1a14"/><circle cx="600" cy="${n === 1 ? 380 : 560}" r="${n === 1 ? 260 : 380}" fill="${c}"/>
<text x="600" y="${n === 1 ? 740 : 1120}" font-family="Helvetica, Arial" font-size="64" font-weight="700" fill="#f4ead6" text-anchor="middle">${label} (${n})</text></svg>`;
}

const CORS = { "Access-Control-Allow-Origin": "*", "Access-Control-Allow-Headers": "Content-Type", "Access-Control-Allow-Methods": "GET, POST, OPTIONS" };
const json = (body: unknown, init: ResponseInit = {}) => Response.json(body, { ...init, headers: { ...CORS, ...(init.headers ?? {}) } });

Bun.serve({
  port: PORT,
  idleTimeout: 0, // SSE stays open
  async fetch(req) {
    const url = new URL(req.url);
    const p = url.pathname;
    if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: CORS });
    if (p === "/") return Response.redirect(`/scout?token=dev&stream=scouttest&vision=${encodeURIComponent(url.origin + "/fake-vision")}`, 302);
    if (p === "/scout") return new Response(scoutDashboardHtml(), { headers: { "Content-Type": "text/html; charset=utf-8" } });
    if (p === "/scout/slides") return new Response(scoutSlidesHtml(), { headers: { "Content-Type": "text/html; charset=utf-8" } });
    if (p === "/scout/state") return json(state);
    if (p === "/scout/events") {
      let ctl: ReadableStreamDefaultController<Uint8Array>;
      const stream = new ReadableStream<Uint8Array>({
        start(c) {
          ctl = c;
          clients.add(c);
          send(c, `retry: 2000\n\ndata: ${JSON.stringify({ type: "state", state } satisfies ScoutEvent)}\n\n`);
        },
        cancel() {
          clients.delete(ctl);
        },
      });
      return new Response(stream, { headers: { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", Connection: "keep-alive" } });
    }
    const audio = p.match(/^\/scout\/audio\/([a-z0-9_]+)\.mp3$/);
    if (audio) {
      const f = join(AUDIO_DIR, `${audio[1]}.mp3`);
      return existsSync(f) ? new Response(Bun.file(f), { headers: { "Content-Type": "audio/mpeg" } }) : new Response("no audio", { status: 404 });
    }
    if (p === "/scout/slide" && req.method === "POST") {
      const s = (await req.json()) as ScoutSlide;
      emit({ type: "slide", label: s.label, at: s.at });
      return json({ ok: true });
    }
    if (p === "/fake-vision/health") {
      return json({ ok: true, model: "fake-dinov2-small", source: "rtsp://127.0.0.1:8554/scout", fps, refs } satisfies VisionHealth);
    }
    if (p === "/fake-vision/enrol" && req.method === "POST") {
      const r = (await req.json()) as { label: ScoutClass; seconds: number };
      await new Promise((ok) => setTimeout(ok, (r.seconds || 5) * 1000));
      const saved = Math.round((r.seconds || 5) * 6);
      refs[r.label] = (refs[r.label] ?? 0) + saved;
      return json({ label: r.label, saved, total: refs[r.label] });
    }
    if (p === "/fake-vision/slides.json") return json(slideList(url.origin));
    const img = p.match(/^\/fake-vision\/img\/([a-z_]+)\/(\d)\.svg$/);
    if (img && (SCOUT_LABELS as readonly string[]).includes(img[1]!)) {
      return new Response(slideSvg(img[1] as ScoutLabel, Number(img[2])), { headers: { ...CORS, "Content-Type": "image/svg+xml" } });
    }
    return new Response("not found", { status: 404 });
  },
});

console.log(`scout dashboard dev harness on http://localhost:${PORT}/  (speed x${SPEED}, audio from ${AUDIO_DIR}${existsSync(AUDIO_DIR) ? "" : " [missing: audio will 404]"})`);
console.log(`  dashboard: http://localhost:${PORT}/scout?token=dev&stream=scouttest&vision=http://localhost:${PORT}/fake-vision`);
console.log(`  slides:    http://localhost:${PORT}/scout/slides?token=dev&vision=http://localhost:${PORT}/fake-vision`);
demo();
