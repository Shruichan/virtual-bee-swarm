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
//        --audio-dir DIR (default hive/lines/scout_audio; files named <line_id>.mp3),
//        --mode demo|slides (default demo). slides = no script: the fake glasses look at whatever /scout/slides
//          shows and a small copy of the game's rules decides found / blocked (busy, cooldown, already found,
//          declined); honey_jar is declined once, --weak LABEL (default bug_spray) is never seen clearly.
//        --practice starts with practice mode on (also POST /scout/practice {on}).
//        --mode quiet = no script: nothing happens until POST /dev/target {label|null} (what the fake glasses look at)
//          or POST /dev/emit <ScoutEvent> (sent as is). For screenshots of /scout/show.
//        --video FILE serves FILE at /fake-cam.mp4 (H.264) so the pages can play it with &video=/fake-cam.mp4.
//
// Judge screen: http://localhost:8244/scout/show?token=dev&video=/fake-cam.mp4  (bun scripts/scout-dashboard-dev.ts --port 8244 --video ...)
import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { scoutDashboardHtml, scoutSlidesHtml } from "../src/scout-dashboard";
import { scoutAssetResponse, scoutShowHtml } from "../src/scout-show";
import {
  SCOUT_BACKGROUND,
  SCOUT_LABELS,
  SCOUT_LINES,
  type ScoutAnswer,
  type ScoutBlockReason,
  type ScoutBox,
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
const MODE = opt.mode === "slides" ? "slides" : opt.mode === "quiet" ? "quiet" : "demo";
const VIDEO = opt.video ? resolve(opt.video) : null;
const WEAK = new Set(String(opt.weak ?? "bug_spray").split(","));
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
  const chunk = `event: ${ev.type}\ndata: ${JSON.stringify(ev)}\n\n`; // named events, like the real Hive
  for (const c of clients) send(c, chunk);
  if (ev.type !== "frame") console.log(new Date().toISOString().slice(11, 19), ev.type, JSON.stringify(ev).slice(0, 110));
}
setInterval(() => {
  for (const c of clients) send(c, ": ping\n\n");
}, 15000);

// ------------------------------------------------------------------ fake game state + frames
const state: ScoutState = { phase: MODE === "slides" ? "looking" : "intro", found: [], skipped: [], offer: null, current: null, started_at: MODE === "slides" ? Date.now() : null, practice: opt.practice === "1" };
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
    const weak = MODE === "slides" && WEAK.has(target);
    if (c === target) goal = c === SCOUT_BACKGROUND ? 0.72 : weak ? 0.5 : 0.84;
    else if (c === SCOUT_BACKGROUND) goal = target === SCOUT_BACKGROUND ? 0.72 : weak ? 0.44 : 0.2;
    else if (CONFUSE[target] === c) goal = weak ? 0.42 : 0.38;
    level[c] = Math.min(0.99, Math.max(0, level[c] + (goal - level[c]) * 0.28 + (Math.random() - 0.5) * 0.06));
  }
  const all = CLASSES.map((label) => ({ label, score: Math.round(level[label] * 1000) / 1000 })).sort((a, b) => b.score - a.score);
  const top = all.slice(0, 3);
  const scores = Object.fromEntries(all.map((x) => [x.label, x.score]));
  // Fake detector boxes: the object the wearer looks at drifts around the frame (head movement), the look-alike
  // shows up as a smaller second box when it scores high enough.
  const t = Date.now();
  const boxes: ScoutBox[] = [];
  const box = (label: ScoutClass, cx: number, cy: number, w: number, h: number) =>
    boxes.push({ label, score: Math.round(level[label] * 1000) / 1000, x: Math.max(0, cx - w / 2), y: Math.max(0, cy - h / 2), w, h, via: "detector" });
  if (target !== SCOUT_BACKGROUND && level[target] > 0.35) {
    box(target, 0.5 + 0.17 * Math.sin(t / 1700), 0.52 + 0.09 * Math.sin(t / 2300), 0.3 + 0.02 * Math.sin(t / 900), 0.44 + 0.02 * Math.sin(t / 1100));
    const other = CONFUSE[target];
    if (other && level[other] > 0.3) box(other, 0.22 + 0.05 * Math.sin(t / 2100), 0.3 + 0.04 * Math.sin(t / 1500), 0.16, 0.22);
  }
  fps = Math.max(5, Math.min(9.5, fps + (Math.random() - 0.5) * 0.4));
  state.current = top[0]!;
  emit({ type: "frame", frame: { at: Date.now() - jitter(110, 190), frame_ms: 38 + Math.random() * 22, fps, top, scores, boxes, source: "rtsp://127.0.0.1:8554/scout" } });
  if (MODE === "slides") slideGame(top);
  if (++tick % 4 === 0) logDetection(top);
}, 200);

// ------------------------------------------------------------------ fake vision /debug/recent (the dashboard's detection log)
interface DebugEntry { t: number; ocr: string[]; ocr_label: ScoutLabel | null; sift: { label: ScoutLabel; inliers: number }[]; final_label: ScoutClass; score: number; img: string }
const OCR_WORDS: Partial<Record<ScoutLabel, string[]>> = {
  bug_spray: ["OFF!", "DEEP", "WOODS"], honey_jar: ["PURE", "HONEY"], bee_hotel: ["BEE", "HOTEL"], native_flower: ["SUNFLOWER", "SEEDS"],
};
const LOOKALIKE: Partial<Record<ScoutLabel, ScoutLabel>> = { native_flower: "beehive" }; // "sunflower read as beehive"
const debugLog: DebugEntry[] = [];
let tick = 0;
function logDetection(top: { label: ScoutClass; score: number }[]) {
  const best = top[0]!;
  const seen = (best.label === SCOUT_BACKGROUND ? target : best.label) as ScoutClass;
  const label = seen === SCOUT_BACKGROUND ? null : (seen as ScoutLabel);
  const words = label && Math.random() < 0.8 ? OCR_WORDS[label] ?? [] : [];
  const other = label ? LOOKALIKE[label] ?? (CONFUSE[label] as ScoutLabel | undefined) : undefined;
  const flip = !!label && !!other && words.length > 0 && Math.random() < 0.3; // SIFT disagrees with what the text says
  const sift = label && other ? [{ label: flip ? other : label, inliers: 20 + Math.floor(Math.random() * 40) }, { label: flip ? label : other, inliers: 4 + Math.floor(Math.random() * 14) }] : [];
  const final: ScoutClass = flip ? other! : best.label; // a flip is the misclassification the log is for
  // img is relative to the vision worker's root, like the real one (/debug/img/<file>)
  debugLog.unshift({ t: Date.now(), ocr: words, ocr_label: words.length ? label : null, sift, final_label: final, score: best.score, img: `/debug/img/${Date.now()}_${final}.svg` });
  debugLog.length = Math.min(debugLog.length, 30);
}

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
    for (const [i, b] of BEATS.entries()) {
      target = SCOUT_BACKGROUND;
      await sleep(jitter(1200, 1800));
      if (i === 1) {
        // the wearer glances back at the beehive: seen steadily, silent by design
        target = "beehive";
        await sleep(1500);
        emit({ type: "blocked", label: "beehive", reason: "already_found", score: state.current?.score ?? 0.8, at: Date.now() });
        target = SCOUT_BACKGROUND;
      }
      await sleep(jitter(1000, 1400));
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
      await sleep(1500);
      const other = CONFUSE[b.label];
      if (i === 3 && other && other !== SCOUT_BACKGROUND) emit({ type: "blocked", label: other as ScoutLabel, reason: "busy", score: 0.71, at: Date.now() });
      await sleep(1500);
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

// ------------------------------------------------------------------ slides mode: a small copy of the game's rules
const TUNE = { steadyMs: 900, minScore: 0.6, minMargin: 0.12, gapMs: 4000 };
const game = {
  busyUntil: 0,
  run: null as { label: ScoutLabel; since: number } | null,
  lastFind: null as { label: ScoutLabel; at: number } | null,
  lastSlide: null as { label: ScoutClass | null; at: number; recv: number } | null,
  blockedKey: "",
};
function eligible(l: ScoutLabel): boolean {
  if (state.practice) {
    const lf = game.lastFind;
    return !(lf && lf.label === l && (!game.lastSlide || game.lastSlide.recv <= lf.at));
  }
  if (state.found.includes(l)) return false;
  return !state.skipped.includes(l) || SCOUT_LABELS.every((x) => x === l || state.found.includes(x));
}
function blocked(label: ScoutLabel, reason: ScoutBlockReason, score: number, episode: string) {
  const key = `${reason}:${label}:${episode}`;
  if (key === game.blockedKey) return;
  game.blockedKey = key;
  emit({ type: "blocked", label, reason, score, at: Date.now() });
}
function slideGame(top: { label: ScoutClass; score: number }[]) {
  const now = Date.now();
  const best = top[0];
  const counts = !!best && best.label !== SCOUT_BACKGROUND && best.score >= TUNE.minScore && best.score - (top[1]?.score ?? 0) >= TUNE.minMargin;
  if (state.phase !== "looking") {
    game.run = null;
    if (counts && best!.label !== state.offer) blocked(best!.label as ScoutLabel, "busy", best!.score, `${state.phase}:${game.busyUntil}`);
    return;
  }
  if (!counts) {
    game.run = null;
    return;
  }
  const l = best!.label as ScoutLabel;
  if (!game.run || game.run.label !== l) game.run = { label: l, since: now };
  const look = now - game.run.since;
  if (look < TUNE.steadyMs) return;
  const reason: ScoutBlockReason | null = !eligible(l)
    ? state.found.includes(l) || state.practice ? "already_found" : "skipped"
    : now < game.busyUntil + TUNE.gapMs ? "cooldown" : null;
  if (reason) blocked(l, reason, best!.score, String(game.run.since));
  else void offer(l, best!.score, look);
}
async function offer(l: ScoutLabel, score: number, look: number) {
  const now = Date.now();
  game.lastFind = { label: l, at: now };
  game.run = null;
  state.phase = "offering";
  state.offer = l;
  emit({ type: "found", label: l, score, at: now, look_ms: look });
  pushState({});
  latency("frame_to_found", look);
  if (game.lastSlide?.label === l) latency("slide_to_found", now - game.lastSlide.at);
  await sleep(120);
  say(SCOUT_LINES.chime);
  latency("found_to_say", Date.now() - now);
  await sleep(1300);
  say(SCOUT_LINES.hook(l));
  await sleep(2600);
  const yes = !(l === "honey_jar" && !state.skipped.includes(l) && !state.found.includes(l));
  emit({ type: "heard", text: yes ? "yeah" : "nah", answer: yes ? "yes" : "no", at: Date.now() });
  if (yes) {
    const h = Date.now();
    await sleep(300);
    latency("answer_to_fact", Date.now() - h);
    pushState({ phase: "telling" });
    say(SCOUT_LINES.fact(l));
    await sleep(5500);
    const repeat = state.found.includes(l);
    const found = repeat ? state.found : [...state.found, l];
    pushState({ found, skipped: state.skipped.filter((x) => x !== l) });
    if (!repeat && found.length < SCOUT_LABELS.length) {
      say(SCOUT_LINES.progress(found.length));
      await sleep(2000);
    }
  } else {
    await sleep(300);
    say(SCOUT_LINES.no[0]!);
    pushState({ skipped: [...state.skipped, l] });
    await sleep(1500);
  }
  game.busyUntil = Date.now();
  pushState({ phase: "looking", offer: null });
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
    if (p === "/scout/show") return new Response(scoutShowHtml(), { headers: { "Content-Type": "text/html; charset=utf-8" } });
    const asset = p.match(/^\/scout\/assets\/([^/]+)$/);
    if (asset) return scoutAssetResponse(asset[1]!);
    if (p === "/fake-cam.mp4" && VIDEO) return new Response(Bun.file(VIDEO), { headers: { "Content-Type": "video/mp4" } });
    if (p === "/dev/target" && req.method === "POST") {
      const b = (await req.json()) as { label?: ScoutClass | null };
      target = b.label ?? SCOUT_BACKGROUND;
      return json({ ok: true, target });
    }
    if (p === "/dev/emit" && req.method === "POST") {
      const ev = (await req.json()) as ScoutEvent;
      if (ev.type === "state") Object.assign(state, ev.state);
      emit(ev);
      return json({ ok: true });
    }
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
      if (MODE === "slides") {
        game.lastSlide = { label: s.label, at: s.at, recv: Date.now() };
        setTimeout(() => (target = s.label ?? SCOUT_BACKGROUND), 500); // stream + vision lag
      }
      return json({ ok: true });
    }
    if (p === "/scout/practice" && req.method === "POST") {
      const b = (await req.json()) as { on?: unknown };
      if (typeof b.on !== "boolean") return json({ error: "on (boolean) required" }, { status: 400 });
      pushState({ practice: b.on });
      return json(state);
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
    if (p === "/fake-vision/debug/recent") return json(debugLog.slice(0, 12));
    const dimg = p.match(/^\/fake-vision\/debug\/img\/\d+_([a-z_]+)\.svg$/);
    if (dimg) {
      const l = dimg[1]!;
      const c = (COLORS as Record<string, string>)[l] ?? "#3a3428";
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#2a2620"/><rect x="${90 + Math.random() * 40}" y="${30 + Math.random() * 20}" width="110" height="110" rx="14" fill="${c}"/><text x="10" y="22" font-family="Helvetica" font-size="13" fill="#bbb">${l}</text></svg>`;
      return new Response(svg, { headers: { ...CORS, "Content-Type": "image/svg+xml" } });
    }
    const img = p.match(/^\/fake-vision\/img\/([a-z_]+)\/(\d)\.svg$/);
    if (img && (SCOUT_LABELS as readonly string[]).includes(img[1]!)) {
      return new Response(slideSvg(img[1] as ScoutLabel, Number(img[2])), { headers: { ...CORS, "Content-Type": "image/svg+xml" } });
    }
    return new Response("not found", { status: 404 });
  },
});

console.log(`scout dashboard dev harness on http://localhost:${PORT}/  (mode ${MODE}, speed x${SPEED}, audio from ${AUDIO_DIR}${existsSync(AUDIO_DIR) ? "" : " [missing: audio will 404]"})`);
console.log(`  dashboard: http://localhost:${PORT}/scout?token=dev&stream=scouttest&vision=http://localhost:${PORT}/fake-vision`);
console.log(`  judge:     http://localhost:${PORT}/scout/show?token=dev${VIDEO ? "&video=/fake-cam.mp4" : ""}`);
console.log(`  slides:    http://localhost:${PORT}/scout/slides?token=dev&vision=http://localhost:${PORT}/fake-vision`);
if (MODE === "demo") demo();
