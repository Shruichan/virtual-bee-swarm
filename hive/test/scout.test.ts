// Live Bee Scout game: pure ScoutGame scenarios on a fake clock, plus the /scout routes on a real Hive.
import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { classifyAnswer, estimateMs, loadScoutLines, ScoutGame, ANSWER_MAX_MS, SKIP_REOFFER_MS, type ScoutSay } from "../src/scout";
import { SCOUT_STOP_ID, SCOUT_TEACHER_LINE, SCOUT_BOOTH_JUDGE_ORDER, SCOUT_BOOTH_LINES, SCOUT_BOOTH_ORDER, SCOUT_BOOTH_TUNING, SCOUT_LABELS, SCOUT_TUNING, type ScoutClass, type ScoutEvent, type ScoutLabel, type ScoutLine } from "../src/scout-protocol";
import { makeHive, TestClient, TOKEN } from "./helpers";

/** The real lines (both files): only for the "every id the game says exists" checks. Content checks live in scout-lines.test.ts. */
const REAL = loadScoutLines();

/** A fixed line set for the logic tests, so content edits (new _2 fact lines, rewrites) never break them.
 *  One fact line per object; the two-line-fact tests add a _2 themselves. */
function fixtureLines(): ScoutLine[] {
  const L: ScoutLine[] = [
    { id: "scout_intro_1", kind: "intro", text: "Intro one." },
    { id: "scout_intro_2", kind: "intro", text: "Intro two." },
    { id: "scout_chime", kind: "chime", text: "Bee fact found!" },
    { id: "scout_no_1", kind: "no", text: "No one." },
    { id: "scout_no_2", kind: "no", text: "No two." },
    { id: "scout_rude", kind: "rude", text: "Rude." },
    { id: "scout_silence", kind: "silence", text: "Silence." },
    { id: "scout_idle", kind: "idle", text: "Idle." },
    { id: "scout_done", kind: "done", text: "Done." },
    { id: SCOUT_BOOTH_LINES.intro, kind: "intro", text: "Booth intro." },
    { id: SCOUT_BOOTH_LINES.chimeAsk, kind: "chime", text: "Bee fact found! Would you like to learn more?" },
    { id: SCOUT_BOOTH_LINES.silenceYes, kind: "silence", text: "I'll take that as a yes." },
    { id: SCOUT_BOOTH_LINES.no, kind: "no", text: "Booth no." },
    { id: SCOUT_BOOTH_LINES.done, kind: "done", text: "Booth done." },
    { id: SCOUT_TEACHER_LINE, kind: "teacher", text: "Ask your teacher for another assignment!" },
  ];
  for (let n = 1; n <= 5; n++) L.push({ id: `scout_progress_${n}`, kind: "progress", text: `Progress ${n}.` });
  for (const [k, id] of Object.entries(SCOUT_BOOTH_LINES.dir)) L.push({ id, kind: "direction", text: `Direction ${k}.` });
  for (const l of SCOUT_LABELS) {
    // The hook ends in words that would read as "yes" if the mic heard it back (the echo test needs that).
    L.push({ id: `scout_hook_${l}`, kind: "hook", label: l, text: `Hook ${l}. Want to hear more?` });
    L.push({ id: `scout_fact_${l}`, kind: "fact", label: l, text: `Fact ${l}.` });
    L.push({ id: SCOUT_BOOTH_LINES.next(l), kind: "direction", label: l, text: `Go to ${l}.` });
  }
  return L;
}
const LINES = fixtureLines();
const T = SCOUT_TUNING;

function setup(opts: { durationMs?: number; extra?: ScoutLine[] } = {}) {
  let clock = 1_000_000;
  const says: ScoutSay[] = [];
  const events: ScoutEvent[] = [];
  const stops: number[] = [];
  const game = new ScoutGame({
    lines: [...LINES, ...(opts.extra ?? [])],
    now: () => clock,
    say: (s) => says.push(s),
    event: (e) => events.push(e),
    stop: () => stops.push(clock),
    durationMs: () => opts.durationMs ?? 1000,
    audioUrl: (id) => `http://hive/scout/audio/${id}.mp3`,
  });
  const ids = () => says.map((s) => s.line_id);
  const advance = (ms: number) => {
    // tick every 100 ms, like the route ticker
    for (let left = ms; left > 0; left -= 100) {
      clock += Math.min(100, left);
      game.tick();
    }
  };
  const frame = (top: { label: ScoutClass; score: number }[]) => game.frame({ at: clock, frame_ms: 30, fps: 10, top, source: "test" });
  /** Look at `label` for `ms` at 10 fps with a clear lead. */
  const look = (label: ScoutClass, ms: number, score = 0.85, runnerUp = 0.3) => {
    for (let t = 0; t <= ms; t += 100) {
      frame([
        { label, score },
        { label: "background", score: runnerUp },
      ]);
      if (t < ms) {
        clock += 100;
        game.tick();
      }
    }
  };
  const idle = () => advance(30_000); // let everything finish playing and the gap pass
  return { game, says, events, stops, ids, advance, frame, look, idle, now: () => clock };
}

/** "Start your adventure" on a setup() game: intro played and over, says and events cleared. */
function begin<S extends ReturnType<typeof setup>>(s: S): S {
  s.game.start();
  s.advance(20_000);
  s.says.length = 0;
  s.events.length = 0;
  return s;
}

/** Started, intro finished, gap passed. */
function ready(opts: { durationMs?: number } = {}) {
  const s = setup(opts);
  s.game.start();
  s.advance(2000 + T.gapMs + 100);
  s.says.length = 0;
  s.events.length = 0;
  return s;
}

describe("answer classification", () => {
  const table: [string, string][] = [
    ["yes", "yes"],
    ["Yes please", "yes"],
    ["yeah", "yes"],
    ["Yep!", "yes"],
    ["sure", "yes"],
    ["OK", "yes"],
    ["okay", "yes"],
    ["tell me more", "yes"],
    ["Go on.", "yes"],
    ["uh huh", "yes"],
    ["definitely", "yes"],
    ["no", "no"],
    ["No thanks", "no"],
    ["nope", "no"],
    ["nah", "no"],
    ["not now", "no"],
    ["maybe later", "no"],
    ["skip", "no"],
    ["pass", "no"],
    ["shut up", "rude"],
    ["Be quiet!", "rude"],
    ["go away", "rude"],
    ["I don't care", "rude"],
    ["I don’t care", "rude"],
    ["stop talking", "rude"],
    ["leave me alone", "rude"],
    ["buzz off", "rude"],
    ["no, shut up", "rude"],
    ["yes, go away", "rude"],
    ["please be quiet", "rude"],
    ["yes no", "no"],
    ["I know", "other"],
    ["what is that", "other"],
    ["", "other"],
    ["the snowboard", "other"],
  ];
  for (const [text, want] of table) test(`"${text}" -> ${want}`, () => expect(classifyAnswer(text)).toBe(want as any));
});

describe("lines file", () => {
  test("every line the game can say exists", () => {
    const have = new Set(REAL.map((l) => l.id));
    const need = ["scout_intro_1", "scout_intro_2", "scout_chime", "scout_no_1", "scout_no_2", "scout_rude", "scout_silence", "scout_idle", "scout_done"];
    for (const l of SCOUT_LABELS) need.push(`scout_hook_${l}`, `scout_fact_${l}`);
    for (let n = 1; n <= 5; n++) need.push(`scout_progress_${n}`);
    for (const id of need) expect(have.has(id)).toBe(true);
  });
  test("duration estimate", () => expect(estimateMs("x".repeat(144))).toBe(10_300));
});

describe("steady detection", () => {
  test("a steady look finds the object: found event, chime, then hook", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    expect(s.game.state().phase).toBe("offering");
    expect(s.game.state().offer).toBe("beehive");
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_beehive"]);
    const found = s.events.find((e) => e.type === "found");
    expect(found).toMatchObject({ type: "found", label: "beehive" });
    expect((found as any).look_ms).toBeGreaterThanOrEqual(T.steadyMs);
    expect(s.says[1]!.audio_url).toBe("http://hive/scout/audio/scout_hook_beehive.mp3");
    const hops = s.events.filter((e) => e.type === "latency").map((e: any) => e.hop);
    expect(hops).toContain("frame_to_found");
    expect(hops).toContain("found_to_say");
    expect(s.events.some((e) => e.type === "say" && e.line_id === "scout_chime")).toBe(true);
  });

  test("shorter than steadyMs does nothing", () => {
    const s = ready();
    s.look("beehive", T.steadyMs - 200);
    expect(s.game.state().phase).toBe("looking");
    expect(s.says).toEqual([]);
  });

  test("flicker between labels never builds a steady run", () => {
    const s = ready();
    for (let i = 0; i < 40; i++) {
      s.frame([{ label: i % 2 ? "beehive" : "honey_jar", score: 0.9 }]);
      s.advance(100);
    }
    expect(s.says).toEqual([]);
  });

  test("one weak frame breaks the run", () => {
    const s = ready();
    s.look("beehive", T.steadyMs - 300);
    s.frame([{ label: "beehive", score: 0.4 }]);
    s.advance(100);
    s.look("beehive", T.steadyMs - 300);
    expect(s.says).toEqual([]);
    s.look("beehive", 400);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_beehive"]);
  });

  test("score gate: below minScore never counts", () => {
    const s = ready();
    s.look("beehive", 3000, T.minScore - 0.01, 0.1);
    expect(s.says).toEqual([]);
  });

  test("margin gate: a close runner-up never counts", () => {
    const s = ready();
    s.look("beehive", 3000, 0.8, 0.8 - T.minMargin + 0.01);
    expect(s.says).toEqual([]);
    s.look("beehive", T.steadyMs + 100, 0.8, 0.8 - T.minMargin - 0.02);
    expect(s.game.state().offer).toBe("beehive");
  });

  test("background never triggers, however confident", () => {
    const s = ready();
    s.look("background", 10_000, 0.99, 0.01);
    expect(s.says.filter((x) => x.line_id !== "scout_idle")).toEqual([]);
    expect(s.events.some((e) => e.type === "found")).toBe(false);
  });

  test("the latest frame's best guess is in the state", () => {
    const s = ready();
    s.frame([{ label: "bug_spray", score: 0.7 }]);
    expect(s.game.state().current).toEqual({ label: "bug_spray", score: 0.7 });
  });

  test("slide_to_found uses the last slide with that label", () => {
    const s = ready();
    s.game.slide("honey_jar", s.now());
    s.look("honey_jar", T.steadyMs + 100);
    const hop = s.events.find((e) => e.type === "latency" && e.hop === "slide_to_found") as any;
    expect(hop.ms).toBeGreaterThanOrEqual(T.steadyMs);
    expect(s.events.some((e) => e.type === "slide")).toBe(true);
  });
});

describe("offers and answers", () => {
  test("yes -> fact, then the progress line, then looking again", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    expect(s.game.utterance("yes please")).toBe("yes");
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_beehive", "scout_fact_beehive", "scout_progress_1"]);
    expect(s.game.state()).toMatchObject({ phase: "telling", found: ["beehive"], offer: "beehive" });
    expect(s.events.some((e) => e.type === "heard" && e.answer === "yes")).toBe(true);
    expect(s.events.some((e) => e.type === "latency" && e.hop === "answer_to_fact")).toBe(true);
    s.advance(4100); // 4 lines of 1 s
    expect(s.game.state().phase).toBe("looking");
    expect(s.game.state().offer).toBe(null);
  });

  test("frames while offering or telling are ignored", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    s.look("honey_jar", 2000);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_beehive"]);
    s.game.button();
    s.look("honey_jar", 1500); // still telling
    expect(s.ids()).not.toContain("scout_hook_honey_jar");
  });

  test("a found object stays silent", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    s.game.utterance("yeah");
    s.idle();
    s.says.length = 0;
    s.look("beehive", 5000);
    expect(s.ids()).toEqual([]);
  });

  test("button during an offer = yes; outside an offer = nothing", () => {
    const s = ready();
    expect(s.game.button()).toBe(false);
    expect(s.says).toEqual([]);
    s.look("pebble_dish", T.steadyMs + 100);
    expect(s.game.button()).toBe(true);
    expect(s.ids()).toContain("scout_fact_pebble_dish");
  });

  test("no -> a no line, skipped, looking", () => {
    const s = ready();
    s.look("bug_spray", T.steadyMs + 100);
    expect(s.game.utterance("no thanks")).toBe("no");
    expect(s.ids().at(-1)).toBe("scout_no_1");
    expect(s.game.state()).toMatchObject({ phase: "looking", skipped: ["bug_spray"], found: [], offer: null });
    // the two no lines take turns
    s.idle();
    s.look("honey_jar", T.steadyMs + 100);
    s.game.utterance("nah");
    expect(s.ids().at(-1)).toBe("scout_no_2");
  });

  test("rude -> the rude line, skipped", () => {
    const s = ready();
    s.look("bee_hotel", T.steadyMs + 100);
    expect(s.game.utterance("I don't care")).toBe("rude");
    expect(s.ids().at(-1)).toBe("scout_rude");
    expect(s.game.state().skipped).toEqual(["bee_hotel"]);
  });

  test("silence -> the silence line after offerTimeoutMs from the hook's end", () => {
    const s = ready();
    s.look("native_flower", T.steadyMs + 100);
    // chime + hook take 2 s, then offerTimeoutMs
    s.advance(2000 + T.offerTimeoutMs - 300);
    expect(s.game.state().phase).toBe("offering");
    s.advance(400);
    expect(s.ids().at(-1)).toBe("scout_silence");
    expect(s.game.state()).toMatchObject({ phase: "looking", skipped: ["native_flower"] });
  });

  test("'other' during an offer keeps waiting; utterances outside an offer are just heard", () => {
    const s = ready();
    expect(s.game.utterance("yes")).toBe("other");
    expect(s.says).toEqual([]);
    expect(s.events.find((e) => e.type === "heard")).toMatchObject({ answer: "other", text: "yes" });
    s.look("honey_jar", T.steadyMs + 100);
    expect(s.game.utterance("what is that")).toBe("other");
    expect(s.game.state().phase).toBe("offering");
    expect(s.game.utterance("go on")).toBe("yes");
  });

  test("the mic hearing Beeswax's own hook is not an answer", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    const hook = LINES.find((l) => l.id === "scout_hook_beehive")!.text;
    const echo = hook.split(/[.!?]\s*/).filter(Boolean).at(-1)!; // "Want to hear more": would read as yes
    expect(s.game.utterance(echo)).toBe("other");
    expect(s.game.state().phase).toBe("offering");
  });
});

describe("skips, gaps, idle, done", () => {
  test("a skipped object is re-offered only after 60 s", () => {
    const s = ready();
    s.look("bug_spray", T.steadyMs + 100);
    s.game.utterance("no");
    s.idle();
    s.says.length = 0;
    s.look("bug_spray", 3000);
    expect(s.ids().filter((id) => id !== "scout_idle")).toEqual([]);
    s.advance(SKIP_REOFFER_MS);
    s.says.length = 0;
    s.look("bug_spray", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_bug_spray"]);
  });

  test("a skipped object is re-offered right away once everything else is found", () => {
    const s = ready();
    s.look("bug_spray", T.steadyMs + 100);
    s.game.utterance("nope");
    for (const l of SCOUT_LABELS.filter((x) => x !== "bug_spray")) {
      s.idle();
      s.look(l, T.steadyMs + 100);
      s.game.utterance("yes");
    }
    expect(s.game.state().found.length).toBe(5);
    s.idle();
    s.says.length = 0;
    s.look("bug_spray", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_bug_spray"]);
  });

  test("gapMs after the previous line ended before the next chime", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    s.advance(2000); // chime + hook finish
    s.game.utterance("no"); // the no line ends 1 s from now
    s.says.length = 0;
    s.look("honey_jar", 1000 + T.gapMs - 300);
    expect(s.ids()).toEqual([]);
    s.look("honey_jar", 400);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_honey_jar"]);
  });

  test("no idle nudge, ever (removed)", () => {
    for (const mode of ["demo", "practice", "booth"] as const) {
      const s = setup();
      s.game.setMode(mode);
      s.game.start();
      s.advance(150_000);
      expect(s.ids()).not.toContain("scout_idle");
    }
  });

  test("no idle nudge before start", () => {
    const s = setup();
    s.advance(120_000);
    expect(s.says).toEqual([]);
  });

  test("start plays both intro lines, resets progress, then looks", () => {
    const s = setup();
    s.game.start();
    expect(s.ids()).toEqual(["scout_intro_1", "scout_intro_2"]);
    expect(s.game.state().phase).toBe("intro");
    s.look("beehive", 1500); // ignored during the intro
    expect(s.ids()).toEqual(["scout_intro_1", "scout_intro_2"]);
    s.advance(1000);
    expect(s.game.state().phase).toBe("looking");
    expect(s.game.state().started_at).not.toBe(null);
  });

  test("all six -> scout_done and phase done; a restart resets", () => {
    const s = ready();
    for (const [i, l] of SCOUT_LABELS.entries()) {
      s.idle();
      s.says.length = 0;
      s.look(l, T.steadyMs + 100);
      s.game.utterance("yes");
      expect(s.ids()).toEqual(["scout_chime", `scout_hook_${l}`, `scout_fact_${l}`, i < 5 ? `scout_progress_${i + 1}` : "scout_done"]);
    }
    expect(s.game.state()).toMatchObject({ phase: "done", found: [...SCOUT_LABELS], offer: null });
    s.idle();
    s.says.length = 0;
    s.look("beehive", 3000);
    expect(s.ids()).toEqual([]);
    s.game.start();
    expect(s.game.state()).toMatchObject({ phase: "intro", found: [], skipped: [] });
  });

  test("state events on every phase change", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    s.game.utterance("yes");
    s.advance(5000);
    const phases = s.events.filter((e) => e.type === "state").map((e: any) => e.state.phase);
    expect(phases).toEqual(["offering", "telling", "looking"]);
  });
});

// ---------------------------------------------------------------- routes on a real Hive

describe("/scout routes", () => {
  let hive: ReturnType<typeof makeHive>;
  let glasses: TestClient;
  let audioDir = "";
  const q = `?token=${TOKEN}`;

  beforeAll(async () => {
    audioDir = mkdtempSync(join(tmpdir(), "scout-audio-"));
    writeFileSync(join(audioDir, "scout_chime.mp3"), new Uint8Array([0x49, 0x44, 0x33, 9]));
    writeFileSync(join(audioDir, "manifest.json"), JSON.stringify({ scout_chime: { ms: 900 } }));
    process.env.SCOUT_AUDIO_DIR = audioDir;
    hive = makeHive({}, { prewarmOnStart: false });
    glasses = await TestClient.connect(hive, "glasses");
  });
  afterAll(async () => {
    glasses.close();
    await hive.stop();
    delete process.env.SCOUT_AUDIO_DIR;
    rmSync(audioDir, { recursive: true, force: true });
  });

  const post = (path: string, body: unknown, headers: Record<string, string> = { "x-hive-token": TOKEN }) =>
    fetch(`${hive.url}${path}`, { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });

  test("token: required on POST and GET, accepted as ?token= or x-hive-token", async () => {
    expect((await post("/scout/frame", { top: [] }, {})).status).toBe(401);
    expect((await post("/scout/frame", { top: [] }, { "x-hive-token": "nope" })).status).toBe(401);
    expect((await fetch(`${hive.url}/scout/state`)).status).toBe(401);
    expect((await fetch(`${hive.url}/scout`)).status).toBe(401);
    expect((await fetch(`${hive.url}/scout/state${q}`)).status).toBe(200);
    expect((await post("/scout/frame", { top: [] })).status).toBe(200);
    const page = await fetch(`${hive.url}/scout${q}`);
    expect(page.status).toBe(200);
    expect(page.headers.get("content-type")).toContain("text/html");
    expect((await fetch(`${hive.url}/scout/slides${q}`)).status).toBe(200);
    const pre = await fetch(`${hive.url}/scout/frame`, { method: "OPTIONS", headers: { origin: "http://x", "access-control-request-method": "POST" } });
    expect(pre.headers.get("access-control-allow-origin")).toBe("*");
  });

  test("frames -> SSE state/frame/found/say events; the glasses get the say on the bus", async () => {
    const res = await fetch(`${hive.url}/scout/events${q}`);
    expect(res.headers.get("content-type")).toContain("text/event-stream");
    const reader = res.body!.getReader();
    const dec = new TextDecoder();
    let buf = "";
    const seen: ScoutEvent[] = [];
    const pump = (async () => {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) return;
        buf += dec.decode(value);
        let i: number;
        while ((i = buf.indexOf("\n\n")) >= 0) {
          const chunk = buf.slice(0, i);
          buf = buf.slice(i + 2);
          const data = chunk.split("\n").find((l) => l.startsWith("data: "));
          if (data) seen.push(JSON.parse(data.slice(6)));
        }
      }
    })();
    const until = async (cond: () => boolean, what: string) => {
      const t0 = Date.now();
      while (!cond()) {
        if (Date.now() - t0 > 4000) throw new Error(`timed out waiting for ${what}: ${JSON.stringify(seen.map((e) => e.type))}`);
        await Bun.sleep(20);
      }
    };
    await until(() => seen.some((e) => e.type === "state"), "state on connect");

    // waiting (Hive just started): frames still reach the dashboards, but nothing fires and nothing is said
    expect(seen.find((e) => e.type === "state")).toMatchObject({ state: { phase: "waiting" } });
    const t0 = Date.now();
    while (Date.now() - t0 < SCOUT_TUNING.steadyMs + 400) {
      const r = await post("/scout/frame", { at: Date.now(), frame_ms: 20, fps: 10, source: "test", top: [{ label: "honey_jar", score: 0.9 }, { label: "background", score: 0.2 }] });
      expect(r.status).toBe(200);
      await Bun.sleep(50);
    }
    expect(seen.some((e) => e.type === "found" || e.type === "say")).toBe(false);
    // the glasses' bus-connect POST /scout/start changes nothing
    expect((await (await post("/scout/start", { at: Date.now() })).json()).state).toMatchObject({ phase: "waiting" });
    // "Start your adventure" -> intro; the operator's force (with a label) fires during the intro
    expect(await (await post("/scout/reset", {})).json()).toMatchObject({ phase: "intro" });
    expect((await post("/scout/force", { label: "honey_jar" })).status).toBe(200);
    await until(() => seen.some((e) => e.type === "found"), "found");
    expect(seen.find((e) => e.type === "found")).toMatchObject({ label: "honey_jar" });
    expect(seen.some((e) => e.type === "frame")).toBe(true);
    // ~5 frame events a second, not every frame
    expect(seen.filter((e) => e.type === "frame").length).toBeLessThan(12);
    await until(() => seen.filter((e) => e.type === "say").length >= 2, "say events");
    const chime = seen.find((e) => e.type === "say" && e.line_id === "scout_chime") as any;
    expect(chime.audio_url).toMatch(/\/scout\/audio\/scout_chime\.mp3\?ms=900$/); // the length rides along (scout/ask)
    const hook = seen.find((e) => e.type === "say" && e.line_id === "scout_hook_honey_jar") as any;
    expect(hook.audio_url).toBe(null); // no mp3 on disk -> the glasses speak the caption

    const busSay = await glasses.waitFor("say", (m) => m.tts_id === "scout_hook_honey_jar");
    expect(busSay).toMatchObject({ channel: "ear", line_id: null, audio_url: null });
    expect(busSay.caption.length).toBeGreaterThan(0); // the real Hive loads the real lines

    const ans = await (await post("/scout/utterance", { text: "yes please", at: Date.now() })).json();
    expect(ans).toMatchObject({ answer: "yes", phase: "telling" });
    await until(() => seen.some((e) => e.type === "say" && e.line_id === "scout_fact_honey_jar"), "fact");
    const state = await (await fetch(`${hive.url}/scout/state${q}`)).json();
    expect(state.found).toEqual(["honey_jar"]);

    expect((await post("/scout/slide", { label: "beehive", at: Date.now() })).status).toBe(200);
    await until(() => seen.some((e) => e.type === "slide"), "slide");
    expect((await post("/scout/slide", { label: "snowboard" })).status).toBe(400);

    // "Restart your journey": back to waiting, badges cleared
    expect(await (await post("/scout/stop", {})).json()).toMatchObject({ ok: true, phase: "waiting", found: [] });
    await reader.cancel();
    await pump.catch(() => {});
  });

  test("button and bad bodies", async () => {
    expect((await post("/scout/utterance", {})).status).toBe(400);
    expect((await post("/scout/frame", { nope: 1 })).status).toBe(400);
    const b = await (await post("/scout/button", { at: Date.now() })).json();
    expect(b.ok).toBe(true);
  });

  test("/scout/audio serves safe names only, no token needed", async () => {
    const ok = await fetch(`${hive.url}/scout/audio/scout_chime.mp3`);
    expect(ok.status).toBe(200);
    expect(ok.headers.get("content-type")).toBe("audio/mpeg");
    expect(new Uint8Array(await ok.arrayBuffer())).toEqual(new Uint8Array([0x49, 0x44, 0x33, 9]));
    expect((await fetch(`${hive.url}/scout/audio/scout_nope.mp3`)).status).toBe(404);
    for (const bad of ["..%2Fscout_lines.json", "..%2F..%2Fpackage.json", "manifest.json", "Scout_Chime.mp3", "%2Fetc%2Fpasswd", "a.mp3.exe"]) {
      const r = await fetch(`${hive.url}/scout/audio/${bad}`);
      expect([400, 404]).toContain(r.status);
    }
    const trav = await fetch(`${hive.url}/scout/audio/../../package.json`);
    expect(trav.status).not.toBe(200);
  });
});

describe("practice mode and blocked reasons", () => {
  test("normal mode: a found object stays silent and says why (already_found)", () => {
    const s = ready();
    s.look("honey_jar", 1000);
    expect(s.events.some((e) => e.type === "found" && e.label === "honey_jar")).toBe(true);
    s.game.utterance("yes");
    s.idle();
    s.events.length = 0;
    s.look("honey_jar", 1500);
    expect(s.events.some((e) => e.type === "found")).toBe(false);
    expect(s.events.find((e) => e.type === "blocked")).toMatchObject({ label: "honey_jar", reason: "already_found" });
    expect(s.events.filter((e) => e.type === "blocked")).toHaveLength(1); // once per look, not per frame
  });

  test("normal mode: another object seen during an offer is reported as busy", () => {
    const s = ready();
    s.look("honey_jar", 1000);
    s.events.length = 0;
    s.look("bug_spray", 500);
    expect(s.events.find((e) => e.type === "blocked")).toMatchObject({ label: "bug_spray", reason: "busy" });
  });

  test("no rediscovery: a card whose fact played never fires again (slide changes, time, other cards), until reset", () => {
    for (const mode of ["practice", "demo"] as const) {
      const s = ready();
      s.game.setMode(mode);
      s.game.slide("honey_jar", s.now());
      s.look("honey_jar", T.steadyMs);
      if (mode === "demo") s.game.utterance("yes");
      expect(s.ids()).toContain("scout_fact_honey_jar");
      s.advance(10_000);
      s.look("bug_spray", T.steadyMs); // another card in between
      if (mode === "demo") s.game.utterance("yes");
      s.advance(10_000);
      s.game.slide(null, s.now());
      s.game.slide("honey_jar", s.now()); // a new slide of the same card
      s.events.length = 0;
      s.says.length = 0;
      s.look("background", 5000, 0.9, 0.1); // out of view for a while
      s.advance(60_000);
      s.look("honey_jar", 3000);
      expect(s.events.some((e) => e.type === "found")).toBe(false);
      expect(s.events.find((e) => e.type === "blocked")).toMatchObject({ label: "honey_jar", reason: "already_found" });
      expect(s.says).toEqual([]);
      expect(s.game.force("honey_jar")).toBe(false); // not even forced
      s.game.start(); // POST /scout/reset frees it
      s.advance(2000 + T.gapMs + 100);
      s.says.length = 0;
      s.look("honey_jar", T.steadyMs);
      expect(s.ids()[0]).toBe("scout_chime");
    }
  });

  test("practice mode: found -> chime + fact straight away, no hook, no offer, then looking", () => {
    const s = ready();
    s.game.setPractice(true);
    s.events.length = 0;
    s.look("beehive", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_beehive"]);
    expect(s.game.state()).toMatchObject({ phase: "telling", offer: "beehive", found: ["beehive"] });
    expect(s.events.find((e) => e.type === "found")).toMatchObject({ label: "beehive" });
    const hops = s.events.filter((e) => e.type === "latency").map((e: any) => e.hop);
    expect(hops).toEqual(expect.arrayContaining(["frame_to_found", "found_to_say"]));
    expect(s.game.utterance("yes")).toBe("other"); // nothing to answer
    expect(s.game.button()).toBe(false);
    s.advance(2000); // chime + fact, 1 s each
    expect(s.game.state()).toMatchObject({ phase: "looking", offer: null, found: ["beehive"] });
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_beehive"]);
  });

  test("practice badges: six distinct cards -> the finale once, after the sixth fact; still looking afterwards", () => {
    const s = ready();
    s.game.setPractice(true);
    const order = [...SCOUT_LABELS];
    for (const l of order.slice(0, 5)) {
      s.advance(3000);
      s.look(l, T.steadyMs);
    }
    s.advance(3000);
    s.look(order[0]!, 3000); // an earlier card again: silent (no rediscovery), no new badge
    expect(s.game.state().found).toEqual(order.slice(0, 5));
    expect(s.ids()).not.toContain("scout_done");
    s.advance(3000);
    s.says.length = 0;
    s.look(order[5]!, T.steadyMs);
    expect(s.ids()).toEqual(["scout_chime", `scout_fact_${order[5]}`, "scout_done"]);
    expect(s.game.state().found).toEqual(order);
    expect(s.events.filter((e) => e.type === "state").at(-1)).toMatchObject({ state: { found: order } });
    s.advance(3000);
    expect(s.game.state().phase).toBe("looking"); // practice keeps going
    s.says.length = 0;
    s.look(order[1]!, T.steadyMs + 1000); // no rediscovery, no second finale
    expect(s.ids()).toEqual([]);
    s.game.start(); // reset clears the badges
    expect(s.game.state().found).toEqual([]);
  });

  test("practice mode: 1 s gap after a line, not gapMs", () => {
    const s = ready();
    s.game.setPractice(true);
    s.look("beehive", T.steadyMs); // fires on the last frame
    s.advance(2000); // chime + fact end now
    s.says.length = 0;
    s.look("bug_spray", 950); // steady already (>= steadyMs), but the 1 s gap has not passed
    expect(s.ids()).toEqual([]);
    s.look("bug_spray", 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_bug_spray"]);
  });

  test("POST /scout/reset logic: start() clears the practice memory", () => {
    const s = ready();
    s.game.setPractice(true);
    s.look("beehive", T.steadyMs);
    s.advance(3000);
    s.game.start();
    s.advance(2000 + 1000 + 100); // intro lines + practice gap
    s.says.length = 0;
    s.look("beehive", T.steadyMs);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_beehive"]);
  });

  test("demo mode is unchanged after practice is switched off", () => {
    const s = ready();
    s.game.setPractice(true);
    s.game.setPractice(false);
    s.look("beehive", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_beehive"]);
    expect(s.game.state().phase).toBe("offering");
  });
});

describe("SCOUT_PRACTICE env", () => {
  test("SCOUT_PRACTICE=1 starts the Hive in practice mode; unset = demo mode", async () => {
    const prev = process.env.SCOUT_PRACTICE;
    try {
      process.env.SCOUT_PRACTICE = "1";
      const on = makeHive({}, { prewarmOnStart: false });
      const s1 = await (await fetch(`${on.url}/scout/state?token=${TOKEN}`)).json();
      await on.stop();
      expect(s1.practice).toBe(true);
      delete process.env.SCOUT_PRACTICE;
      const off = makeHive({}, { prewarmOnStart: false });
      const s2 = await (await fetch(`${off.url}/scout/state?token=${TOKEN}`)).json();
      await off.stop();
      expect(s2.practice).toBe(false);
    } finally {
      if (prev === undefined) delete process.env.SCOUT_PRACTICE;
      else process.env.SCOUT_PRACTICE = prev;
    }
  });
});

// ---------------------------------------------------------------- booth mode (pitch/scout-booth.md)

const B = SCOUT_BOOTH_TUNING;
type Box = { label: ScoutClass; score: number; x: number; y: number; w: number; h: number; via?: "detector" | "marker" | "refs" };
/** A box centered at (cx, cy), w x h, normalized. */
const box = (label: ScoutClass, cx = 0.5, cy = 0.5, w = 0.2, h = 0.25, score = 0.8, via?: Box["via"]): Box => ({
  label,
  score,
  x: cx - w / 2,
  y: cy - h / 2,
  w,
  h,
  ...(via ? { via } : {}),
});

function boothReady(order?: ScoutLabel[]) {
  const s = setup();
  s.game.setMode("booth", order);
  s.game.start();
  s.advance(1000 + B.gapMs + 100); // one intro line, then the booth gap
  s.says.length = 0;
  s.events.length = 0;
  const see = (boxes: Box[], ms: number, top: { label: ScoutClass; score: number }[] = [{ label: "background", score: 0.5 }]) => {
    for (let t = 0; t <= ms; t += 100) {
      s.game.frame({ at: s.now(), frame_ms: 30, fps: 10, top, boxes, source: "test" });
      if (t < ms) s.advance(100);
    }
  };
  return { ...s, see };
}

describe("booth lines", () => {
  test("every booth line id has a placeholder; the booth file is merged in", () => {
    const have = new Map(REAL.map((l) => [l.id, l]));
    const need: string[] = [SCOUT_BOOTH_LINES.intro, SCOUT_BOOTH_LINES.chimeAsk, SCOUT_BOOTH_LINES.silenceYes, SCOUT_BOOTH_LINES.no, SCOUT_BOOTH_LINES.rude, SCOUT_BOOTH_LINES.done];
    need.push(...Object.values(SCOUT_BOOTH_LINES.dir), ...SCOUT_LABELS.map((l) => SCOUT_BOOTH_LINES.next(l)));
    for (const id of need) expect(have.get(id)?.text, id).toBeTruthy();
    expect(have.has("scout_hook_beehive")).toBe(true); // the main file is still there
  });
});

describe("booth mode", () => {
  /** A station's lines: "Bee fact found!", the fact, then the direction to `next` (or the finale). */
  const station = (l: ScoutLabel, next: ScoutLabel | null) => ["scout_chime", `scout_fact_${l}`, next ? `scout_next_${next}` : "scout_booth_done"];

  test("mode, order and next in the state; start plays the booth intro", () => {
    const s = setup();
    s.game.setMode("booth");
    expect(s.game.state()).toMatchObject({ mode: "booth", practice: false, next: "native_flower", order: [...SCOUT_BOOTH_JUDGE_ORDER] });
    expect(SCOUT_BOOTH_JUDGE_ORDER).toEqual(["native_flower", "honey_jar", "bug_spray", "bee_hotel"]);
    s.game.setMode("booth", SCOUT_BOOTH_ORDER); // all six only when asked
    expect(s.game.state().order).toEqual([...SCOUT_BOOTH_ORDER]);
    s.game.setMode("booth");
    expect(s.game.state().order).toEqual([...SCOUT_BOOTH_JUDGE_ORDER]);
    s.game.start();
    expect(s.ids()).toEqual(["scout_booth_intro"]);
    expect(s.game.state().phase).toBe("intro");
    s.game.setMode("demo");
    expect(s.game.state()).toMatchObject({ mode: "demo", next: null });
  });

  test("only the expected-next object triggers, and goes straight into the fact (no ask, no offer)", () => {
    const s = boothReady();
    s.look("honey_jar", 3000); // a later station, steady and clear
    expect(s.says).toEqual([]);
    expect(s.events.find((e) => e.type === "blocked")).toMatchObject({ label: "honey_jar", reason: "not_next" });
    s.look("native_flower", T.steadyMs + 100);
    expect(s.ids()).toEqual(station("native_flower", "honey_jar"));
    expect(s.game.state()).toMatchObject({ phase: "telling", offer: "native_flower", found: ["native_flower"], next: "honey_jar" });
    expect(s.events.find((e) => e.type === "found")).toMatchObject({ label: "native_flower" });
    s.advance(3000);
    expect(s.game.state()).toMatchObject({ phase: "looking", offer: null, next: "honey_jar" });
  });

  test("booth never offers: yes, the button and silence do nothing", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs + 100);
    expect(s.game.utterance("yes")).toBe("other");
    expect(s.game.button()).toBe(false);
    s.advance(10_000);
    expect(s.ids()).toEqual(station("native_flower", "honey_jar"));
  });

  test("a neighbour slightly ahead in the scores still loses to the expected object", () => {
    const s = boothReady();
    for (let t = 0; t <= T.steadyMs + 100; t += 100) {
      s.frame([
        { label: "honey_jar", score: 0.8 },
        { label: "native_flower", score: 0.74 },
      ]);
      s.advance(100);
    }
    expect(s.ids()[1]).toBe("scout_fact_native_flower");
  });

  test("boxes: the most central expected box wins; a lone edge box only cues", () => {
    const s = boothReady();
    s.see([box("native_flower", 0.1), box("honey_jar", 0.5), box("native_flower", 0.52)], T.steadyMs + 100);
    expect(s.ids().slice(0, 2)).toEqual(["scout_chime", "scout_fact_native_flower"]);

    const t = boothReady();
    t.see([box("native_flower", 0.1), box("honey_jar", 0.5)], 3000, [{ label: "native_flower", score: 0.9 }]);
    expect(t.ids()).toEqual(["scout_dir_left"]); // seen at the frame edge: guide, don't trigger
    expect(t.game.state().phase).toBe("looking");
  });

  test("an AprilTag (via marker) counts at any score", () => {
    const s = boothReady();
    s.see([box("native_flower", 0.5, 0.5, 0.15, 0.15, 0.3, "marker")], T.steadyMs + 100);
    expect(s.ids().slice(0, 2)).toEqual(["scout_chime", "scout_fact_native_flower"]);
  });

  test("after the last station: fact, scout_booth_done, phase done", () => {
    const s = boothReady(["honey_jar", "bug_spray"]);
    s.look("honey_jar", T.steadyMs + 100);
    s.advance(3000 + B.gapMs);
    s.says.length = 0;
    s.look("bug_spray", T.steadyMs + 100);
    expect(s.ids()).toEqual(station("bug_spray", null));
    expect(s.game.state()).toMatchObject({ phase: "done", next: null, found: ["honey_jar", "bug_spray"] });
    s.advance(20_000);
    s.look("native_flower", 2000);
    expect(s.ids().at(-1)).toBe("scout_booth_done");
  });

  test("judge order by default: the finale lands after station 4 (bee_hotel)", () => {
    const s = boothReady();
    for (const l of SCOUT_BOOTH_JUDGE_ORDER) {
      expect(s.game.state().next).toBe(l);
      expect(s.game.force()).toBe(true);
      s.advance(5000);
    }
    expect(s.ids().slice(-2)).toEqual(["scout_fact_bee_hotel", "scout_booth_done"]);
    expect(s.game.state()).toMatchObject({ phase: "done", next: null });
  });

  test("the booth gap: 1.5 s after the station lines end", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs); // fires on the last frame; chime + fact + direction end 3 s later
    s.advance(3000);
    s.says.length = 0;
    s.look("honey_jar", B.gapMs - 100);
    expect(s.ids()).toEqual([]);
    s.look("honey_jar", 200);
    expect(s.ids()).toEqual(station("honey_jar", "bug_spray"));
  });

  test("direction cues: left, right, closer, up; at most one every 8 s", () => {
    const s = boothReady();
    s.see([box("native_flower", 0.15)], 500);
    expect(s.ids()).toEqual(["scout_dir_left"]);
    s.see([box("native_flower", 0.85)], B.cueEveryMs - 1000);
    expect(s.ids()).toEqual(["scout_dir_left"]);
    s.see([box("native_flower", 0.85)], 1000);
    expect(s.ids()).toEqual(["scout_dir_left", "scout_dir_right"]);
    s.advance(B.cueEveryMs);
    s.see([box("native_flower", 0.5, 0.5, 0.04, 0.05)], 200);
    expect(s.ids().at(-1)).toBe("scout_dir_closer");
    s.advance(B.cueEveryMs);
    s.see([box("native_flower", 0.5, 0.1)], 200); // top edge: the camera aims below the gaze, so lift the head
    expect(s.ids().at(-1)).toBe("scout_dir_up");
    expect(s.events.some((e) => e.type === "found")).toBe(false);
  });

  test("no cue for other stations' boxes, and none over Beeswax's own lines", () => {
    const s = boothReady();
    s.see([box("honey_jar", 0.1)], 2000);
    expect(s.says).toEqual([]);
    s.look("native_flower", T.steadyMs + 100);
    s.see([box("honey_jar", 0.1)], 500); // the next station, at the edge, while the fact plays
    expect(s.ids()).toEqual(station("native_flower", "honey_jar"));
  });

  test("lost: expected object unseen for 10 s -> scout_dir_lost, at most every 10 s", () => {
    const s = boothReady();
    s.advance(B.lostMs - 1700);
    expect(s.says).toEqual([]);
    s.advance(500);
    expect(s.ids()).toEqual(["scout_dir_lost"]);
    s.advance(B.lostMs);
    expect(s.ids()).toEqual(["scout_dir_lost"]);
    s.advance(1500);
    expect(s.ids()).toEqual(["scout_dir_lost", "scout_dir_lost"]);
  });

  test("seeing the expected object holds the lost nudge off", () => {
    const s = boothReady();
    for (let i = 0; i < 4; i++) {
      s.advance(5000);
      s.see([box("native_flower", 0.5, 0.5, 0.2, 0.2, 0.3)], 0); // a weak sighting, not enough to trigger
    }
    expect(s.ids().filter((id) => id === "scout_dir_lost")).toEqual([]);
  });

  test("operator: force (next / given), next, reset; a bare force while busy is refused", () => {
    const s = boothReady();
    expect(s.game.force()).toBe(true); // the expected station, no dwell
    expect(s.ids()).toEqual(station("native_flower", "honey_jar"));
    expect(s.events.find((e) => e.type === "found")).toMatchObject({ label: "native_flower", look_ms: 0 });
    expect(s.game.state()).toMatchObject({ phase: "telling", found: ["native_flower"], next: "honey_jar" });
    expect(s.game.force()).toBe(false); // double press while the fact plays: no skipped station
    expect(s.game.state().next).toBe("honey_jar");
    s.advance(4000);
    expect(s.game.skipStation()).toBe(true);
    expect(s.game.state()).toMatchObject({ next: "bug_spray", skipped: ["honey_jar"], phase: "looking" });
    expect(s.game.force("bee_hotel")).toBe(true); // jump ahead
    expect(s.game.state()).toMatchObject({ offer: null, found: ["native_flower", "bee_hotel"], phase: "done", next: null });
    s.game.start(); // reset
    expect(s.game.state()).toMatchObject({ phase: "intro", found: [], skipped: [], next: "native_flower" });
    expect(s.ids().at(-1)).toBe("scout_booth_intro");
  });

  test("next past the last station -> done; force works in demo mode too", () => {
    const s = boothReady(["beehive"]);
    expect(s.game.skipStation()).toBe(true);
    expect(s.game.state()).toMatchObject({ phase: "done", next: null });
    expect(s.game.skipStation()).toBe(false);
    s.game.setMode("booth", ["beehive", "honey_jar"]); // switching to booth restarts at the first station
    expect(s.game.state()).toMatchObject({ phase: "looking", next: "beehive", found: [], skipped: [] });
    const d = ready();
    expect(d.game.force("pebble_dish")).toBe(true);
    expect(d.ids()).toEqual(["scout_chime", "scout_hook_pebble_dish"]);
  });
});

describe("booth routes", () => {
  test("SCOUT_MODE=booth + SCOUT_ORDER at startup; /scout/mode, force, yes, next, reset over HTTP", async () => {
    const prev = { m: process.env.SCOUT_MODE, o: process.env.SCOUT_ORDER };
    process.env.SCOUT_MODE = "booth";
    process.env.SCOUT_ORDER = "honey_jar,bug_spray";
    const hive = makeHive({}, { prewarmOnStart: false });
    try {
      const post = (path: string, body: unknown = {}, token = TOKEN) =>
        fetch(`${hive.url}${path}?token=${token}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      const st = await (await fetch(`${hive.url}/scout/state?token=${TOKEN}`)).json();
      expect(st).toMatchObject({ mode: "booth", order: ["honey_jar", "bug_spray"], next: "honey_jar" });

      // boxes ride through POST /scout/frame (validated) to the dashboards' frame events
      const sse = (await fetch(`${hive.url}/scout/events?token=${TOKEN}`)).body!.getReader();
      let feed = "";
      const pump = (async () => {
        for (let r = await sse.read(); !r.done; r = await sse.read()) feed += new TextDecoder().decode(r.value);
      })().catch(() => {});
      await Bun.sleep(250); // past the frame-event throttle
      await post("/scout/frame", { at: Date.now(), top: [{ label: "background", score: 0.9 }], boxes: [{ label: "honey_jar", score: 0.3, x: 0.4, y: 0.4, w: 0.2, h: 0.2, via: "marker" }, { label: "nope", score: 1, x: 0, y: 0, w: 1, h: 1 }] });
      const frameEvent = async () => {
        for (let i = 0; i < 100; i++) {
          const line = feed.split("\n").find((l) => l.startsWith("data: ") && l.includes('"type":"frame"'));
          if (line) return JSON.parse(line.slice(6));
          await Bun.sleep(10);
        }
        return null;
      };
      expect((await frameEvent())?.frame.boxes).toEqual([{ label: "honey_jar", score: 0.3, x: 0.4, y: 0.4, w: 0.2, h: 0.2, via: "marker" }]);
      await sse.cancel();
      await pump;
      expect((await post("/scout/force")).status).toBe(409); // waiting: silent until reset
      await post("/scout/reset");
      expect((await post("/scout/force", {}, "nope")).status).toBe(401);
      expect((await post("/scout/mode", { mode: "party" })).status).toBe(400);
      expect((await post("/scout/mode", { mode: "booth", order: ["honey_jar", "honey_jar"] })).status).toBe(400);
      expect((await post("/scout/mode", { mode: "booth", order: ["snowboard"] })).status).toBe(400);
      expect(await (await post("/scout/mode", { mode: "booth", order: ["native_flower", "bee_hotel"] })).json()).toMatchObject({ mode: "booth", next: "native_flower" });

      expect((await post("/scout/force", { label: "snowboard" })).status).toBe(400);
      expect((await post("/scout/force")).status).toBe(409); // a bare force during the intro is refused
      expect(await (await post("/scout/force", { label: "native_flower" })).json()).toMatchObject({ ok: true, phase: "telling", found: ["native_flower"], next: "bee_hotel" });
      expect((await post("/scout/yes")).status).toBe(409); // booth never offers
      expect(await (await post("/scout/next")).json()).toMatchObject({ ok: true, phase: "done", next: null });
      expect(await (await post("/scout/reset")).json()).toMatchObject({ ok: true, phase: "intro", next: "native_flower", found: [] });
    } finally {
      await hive.stop();
      for (const [k, v] of [["SCOUT_MODE", prev.m], ["SCOUT_ORDER", prev.o]] as const) {
        if (v === undefined) delete process.env[k];
        else process.env[k] = v;
      }
    }
  });
});

// ---------------------------------------------------------------- AprilTag markers: certain, fire after markerSteadyMs

describe("marker boxes (via marker)", () => {
  const tag = (label: ScoutClass, cx = 0.5) => box(label, cx, 0.5, 0.15, 0.15, 0.3, "marker");
  /** Frames at 10 fps carrying `boxes`, `ms` long. */
  const feed = (s: ReturnType<typeof setup>, boxes: Box[], ms: number, top: { label: ScoutClass; score: number }[]) => {
    for (let t = 0; t <= ms; t += 100) {
      s.game.frame({ at: s.now(), frame_ms: 30, fps: 10, top, boxes, source: "test" });
      if (t < ms) s.advance(100);
    }
  };

  test("tuning: steadyMs 500, markerSteadyMs 250", () => {
    expect(T.steadyMs).toBe(500);
    expect(T.markerSteadyMs).toBe(250);
  });

  test("demo: a tag-backed winner fires after markerSteadyMs, a plain one only after steadyMs", () => {
    const s = ready();
    feed(s, [tag("honey_jar")], T.markerSteadyMs - 100, [{ label: "honey_jar", score: 0.9 }]);
    expect(s.says).toEqual([]);
    feed(s, [tag("honey_jar")], 100, [{ label: "honey_jar", score: 0.9 }]);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_honey_jar"]);

    const p = ready();
    p.look("honey_jar", T.markerSteadyMs + 100);
    expect(p.says).toEqual([]);
  });

  test("demo: a tag counts even when the class scores don't", () => {
    const s = ready();
    feed(s, [tag("bug_spray")], T.markerSteadyMs + 50, [{ label: "background", score: 0.9 }]);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_bug_spray"]);
  });

  test("demo: a tag for a different label than the clear winner doesn't speed it up", () => {
    const s = ready();
    feed(s, [tag("bug_spray", 0.9)], T.markerSteadyMs + 100, [{ label: "honey_jar", score: 0.9 }]);
    expect(s.says).toEqual([]);
  });

  test("demo: tags still respect the gap and already-found", () => {
    const s = ready();
    feed(s, [tag("beehive")], T.markerSteadyMs + 50, [{ label: "background", score: 0.9 }]);
    s.game.utterance("yes"); // fact + progress, 4 lines in all
    s.advance(4000);
    s.says.length = 0;
    feed(s, [tag("beehive")], 2000, [{ label: "background", score: 0.9 }]); // already found
    expect(s.says).toEqual([]);
    feed(s, [tag("honey_jar")], T.gapMs - 2000 - 400, [{ label: "background", score: 0.9 }]); // gap not over
    expect(s.says).toEqual([]);
    feed(s, [tag("honey_jar")], 600, [{ label: "background", score: 0.9 }]);
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_honey_jar"]);
  });

  test("practice: tag fires fast, but not twice on the same slide", () => {
    const s = ready();
    s.game.setPractice(true);
    s.game.slide("pebble_dish", s.now());
    feed(s, [tag("pebble_dish")], T.markerSteadyMs + 50, [{ label: "pebble_dish", score: 0.9 }]);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_pebble_dish"]);
    s.advance(3000);
    s.says.length = 0;
    feed(s, [tag("pebble_dish")], 2000, [{ label: "pebble_dish", score: 0.9 }]);
    expect(s.says).toEqual([]);
  });

  test("booth: an expected-next tag fires after markerSteadyMs; another station's tag never does", () => {
    const s = boothReady();
    s.see([tag("honey_jar")], 2000, [{ label: "background", score: 0.9 }]);
    expect(s.says).toEqual([]);
    s.see([tag("native_flower")], T.markerSteadyMs - 100, [{ label: "background", score: 0.9 }]);
    expect(s.says).toEqual([]);
    s.see([tag("native_flower")], 100, [{ label: "background", score: 0.9 }]);
    expect(s.ids().slice(0, 2)).toEqual(["scout_chime", "scout_fact_native_flower"]);
  });
});

// ---------------------------------------------------------------- two-line facts

describe("two-line facts (scout_fact_<label>_2)", () => {
  function twoLine(mode: "demo" | "practice" | "booth") {
    let clock = 1_000_000;
    const says: string[] = [];
    const lines = [...LINES, { id: "scout_fact_native_flower_2", kind: "fact" as const, label: "native_flower" as const, text: "Second half of the flower fact." }];
    const game = new ScoutGame({ lines, now: () => clock, say: (x) => says.push(x.line_id), durationMs: () => 1000 });
    game.setMode(mode);
    game.start(); // "Start your adventure"
    clock += 20_000;
    game.tick();
    says.length = 0;
    return { game, says, tick: (ms: number) => ((clock += ms), game.tick()) };
  }

  test("demo: fact, its second line, then progress; busy-until covers both", () => {
    const g = twoLine("demo");
    g.game.force("native_flower");
    const before = g.game.busyUntil;
    g.game.utterance("yes");
    expect(g.says).toEqual(["scout_chime", "scout_hook_native_flower", "scout_fact_native_flower", "scout_fact_native_flower_2", "scout_progress_1"]);
    expect(g.game.busyUntil - before).toBe(3000);
    g.tick(2900 + 2000); // chime + hook + 3 lines = 5 s
    expect(g.game.state().phase).toBe("telling");
    g.tick(200);
    expect(g.game.state().phase).toBe("looking");
  });

  test("practice and booth say both lines too; a label without _2 says one", () => {
    const p = twoLine("practice");
    p.game.force("native_flower");
    expect(p.says).toEqual(["scout_chime", "scout_fact_native_flower", "scout_fact_native_flower_2"]);
    p.tick(5000);
    p.game.force("honey_jar");
    expect(p.says.slice(3)).toEqual(["scout_chime", "scout_fact_honey_jar"]);

    const b = twoLine("booth");
    b.game.force();
    expect(b.says).toEqual(["scout_chime", "scout_fact_native_flower", "scout_fact_native_flower_2", "scout_next_honey_jar"]);
  });
});

// ---------------------------------------------------------------- Ask Beeswax API, interrupt, teacher

describe("answering (Ask Beeswax API)", () => {
  test("beginAnswer: phase answering, frames ignored, one at a time; endAnswer(audioMs) waits for the audio", () => {
    const s = ready();
    s.game.setPractice(true);
    expect(s.game.beginAnswer()).toBe(true);
    expect(s.game.state().phase).toBe("answering");
    expect(s.game.beginAnswer()).toBe(false);
    s.look("honey_jar", 3000);
    expect(s.says).toEqual([]);
    expect(s.events.find((e) => e.type === "blocked")).toMatchObject({ label: "honey_jar", reason: "busy" });
    s.game.endAnswer(2000);
    s.look("honey_jar", 1900);
    expect(s.game.state().phase).toBe("answering");
    expect(s.says).toEqual([]);
    s.advance(200);
    expect(s.game.state().phase).toBe("looking");
    s.look("honey_jar", 1000 + T.steadyMs); // the practice gap after the answer, then a fresh look
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_honey_jar"]);
  });

  test("endAnswer() with no audio left goes straight back to looking; outside an answer it is a no-op", () => {
    const s = ready();
    s.game.endAnswer();
    expect(s.game.state().phase).toBe("looking");
    s.game.beginAnswer();
    s.game.endAnswer();
    expect(s.game.state().phase).toBe("looking");
  });

  test("beginAnswer during a demo offer drops the offer without skipping it", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100);
    expect(s.game.state().phase).toBe("offering");
    s.game.beginAnswer();
    expect(s.game.state()).toMatchObject({ phase: "answering", offer: null, skipped: [] });
    s.advance(T.offerTimeoutMs + 3000);
    expect(s.ids()).not.toContain("scout_silence");
  });

  test("an answer that never ends gives up after ANSWER_MAX_MS", () => {
    const s = ready();
    s.game.beginAnswer();
    s.advance(ANSWER_MAX_MS - 200);
    expect(s.game.state().phase).toBe("answering");
    s.advance(300);
    expect(s.game.state().phase).toBe("looking");
  });

  test("interrupt mid-fact: stop to the glasses, stop event, busy-until now, back to looking", () => {
    const s = ready();
    s.game.setPractice(true);
    s.look("bug_spray", T.steadyMs);
    expect(s.game.state().phase).toBe("telling");
    s.advance(300);
    s.game.interrupt();
    expect(s.stops).toEqual([s.now()]);
    expect(s.events.some((e) => e.type === "stop")).toBe(true);
    expect(s.game.busyUntil).toBe(s.now());
    expect(s.game.state().phase).toBe("looking");
    // interrupt, then answer: the usual Ask Beeswax sequence
    s.game.interrupt();
    expect(s.game.beginAnswer()).toBe(true);
    expect(s.game.state().phase).toBe("answering");
  });

  test("interrupt during an offer keeps the offer, timed out from now", () => {
    const s = ready();
    s.look("beehive", T.steadyMs + 100); // chime + hook: 2 s
    s.game.interrupt();
    expect(s.game.state().phase).toBe("offering");
    s.advance(T.offerTimeoutMs + 100);
    expect(s.ids().at(-1)).toBe("scout_silence");
  });

  test("teacher() says scout_teacher", () => {
    const s = ready();
    s.game.teacher();
    expect(s.ids()).toEqual([SCOUT_TEACHER_LINE]);
    expect(s.says[0]!.caption).toBe("Ask your teacher for another assignment!");
  });
});

describe("teacher + reset routes", () => {
  test("POST /scout/teacher says it on the bus; POST /scout/reset cuts the ear, then the intro", async () => {
    const hive = makeHive({}, { prewarmOnStart: false });
    const glasses = await TestClient.connect(hive, "glasses");
    try {
      const post = (path: string) => fetch(`${hive.url}${path}?token=${TOKEN}`, { method: "POST", headers: { "content-type": "application/json" }, body: "{}" });
      expect((await fetch(`${hive.url}/scout/teacher`, { method: "POST" })).status).toBe(401);
      expect((await post("/scout/teacher")).status).toBe(409); // silent while waiting for "Start your adventure"
      await post("/scout/reset");
      expect((await post("/scout/teacher")).status).toBe(200);
      const t = await glasses.waitFor("say", (m) => m.tts_id === SCOUT_TEACHER_LINE);
      expect(t.caption.length).toBeGreaterThan(0);
      expect(await (await post("/scout/reset")).json()).toMatchObject({ ok: true, phase: "intro" });
      const stop = await glasses.waitFor("say", (m) => m.tts_id === SCOUT_STOP_ID);
      expect(stop).toMatchObject({ channel: "ear", line_id: null, audio_url: null, caption: "" });
      await glasses.waitFor("say", (m) => typeof m.tts_id === "string" && m.tts_id.startsWith("scout_intro"));
    } finally {
      glasses.close();
      await hive.stop();
    }
  });
});

// ---------------------------------------------------------------- any-questions, multi-line intro and finale

describe("quiet after facts (any-q lines loaded but never said), empty chime, multi-line intro / finale", () => {
  const EXTRA: ScoutLine[] = [
    { id: "scout_any_q_1", kind: "any_q", text: "Any questions?" },
    { id: "scout_any_q_2", kind: "any_q", text: "Questions? Ask away." },
    { id: "scout_intro_3", kind: "intro", text: "Intro three." },
    { id: "scout_done_2", kind: "done", text: "Done two." },
    { id: "scout_done_3", kind: "done", text: "Done three." },
    { id: "scout_booth_intro_2", kind: "intro", text: "Booth intro two." },
    { id: "scout_booth_done_2", kind: "done", text: "Booth done two." },
  ];

  test("practice: no any-q line after facts, even when the lines exist", () => {
    const s = setup({ extra: EXTRA });
    s.game.setPractice(true);
    begin(s);
    s.game.force("beehive");
    s.advance(5000);
    s.game.force("honey_jar");
    s.advance(5000);
    s.game.force("bug_spray");
    expect(s.ids()).toEqual([
      "scout_chime", "scout_fact_beehive",
      "scout_chime", "scout_fact_honey_jar",
      "scout_chime", "scout_fact_bug_spray",
    ]);
  });

  test("an empty scout_chime is skipped (the chime folded into the fact opener)", () => {
    const s = setup({ extra: [{ id: "scout_chime", kind: "chime", text: "" }] });
    s.game.setPractice(true);
    begin(s);
    s.look("beehive", T.steadyMs);
    expect(s.ids()).toEqual(["scout_fact_beehive"]);
    expect(s.events.some((e) => e.type === "found")).toBe(true);
  });

  test("practice: the sixth badge -> fact, then every finale line", () => {
    const s = setup({ extra: EXTRA });
    s.game.setPractice(true);
    begin(s);
    for (const l of SCOUT_LABELS) {
      s.says.length = 0;
      s.game.force(l);
      s.advance(8000);
    }
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_bee_hotel", "scout_done", "scout_done_2", "scout_done_3"]);
  });

  test("booth: fact, direction; the last station plays every booth finale line", () => {
    const s = setup({ extra: EXTRA });
    s.game.setMode("booth", ["honey_jar", "beehive"]);
    begin(s);
    s.game.force();
    s.advance(6000);
    s.game.force();
    expect(s.ids()).toEqual([
      "scout_chime", "scout_fact_honey_jar", "scout_next_beehive",
      "scout_chime", "scout_fact_beehive", "scout_booth_done", "scout_booth_done_2",
    ]);
  });

  test("intros play every line that exists, in order", () => {
    const s = setup({ extra: EXTRA });
    s.game.start();
    expect(s.ids()).toEqual(["scout_intro_1", "scout_intro_2", "scout_intro_3"]);
    s.says.length = 0;
    s.game.setMode("booth");
    s.game.start();
    expect(s.ids()).toEqual(["scout_booth_intro", "scout_booth_intro_2"]);
  });

  test("demo: the finale plays every done line", () => {
    const s = begin(setup({ extra: EXTRA }));
    for (const l of SCOUT_LABELS) {
      s.says.length = 0;
      s.game.force(l);
      s.game.utterance("yes");
      s.advance(10_000);
    }
    expect(s.ids()).toEqual(["scout_chime", "scout_hook_bee_hotel", "scout_fact_bee_hotel", "scout_done", "scout_done_2", "scout_done_3"]);
  });
});

// ---------------------------------------------------------------- glasses mute

describe("glasses mute", () => {
  test("setGlassesMuted: state field, the stop on mute (once), game lines and timing unchanged", () => {
    const s = ready();
    s.game.setPractice(true);
    expect(s.game.state().glasses_muted).toBe(false);
    s.look("beehive", T.steadyMs);
    s.game.setGlassesMuted(true);
    expect(s.game.state().glasses_muted).toBe(true);
    expect(s.stops.length).toBe(1);
    s.game.setGlassesMuted(true);
    expect(s.stops.length).toBe(1);
    expect(s.events.filter((e) => e.type === "state").at(-1)).toMatchObject({ state: { glasses_muted: true } });
    // the game still says (the routes drop it before the bus) and still waits for the lines
    expect(s.game.state().phase).toBe("telling");
    s.advance(2000);
    expect(s.game.state().phase).toBe("looking");
    s.game.setGlassesMuted(false);
    expect(s.stops.length).toBe(1);
  });

  test("SCOUT_GLASSES_MUTED=1 starts muted; a real Hive sends the glasses nothing while muted", async () => {
    const prev = process.env.SCOUT_GLASSES_MUTED;
    process.env.SCOUT_GLASSES_MUTED = "1";
    const hive = makeHive({}, { prewarmOnStart: false });
    const glasses = await TestClient.connect(hive, "glasses");
    try {
      const post = (path: string, body: unknown = {}) => fetch(`${hive.url}${path}?token=${TOKEN}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      expect((await (await fetch(`${hive.url}/scout/state?token=${TOKEN}`)).json()).glasses_muted).toBe(true);
      await post("/scout/reset"); // starts the journey: the intro says are muted too
      await post("/scout/teacher");
      await Bun.sleep(100);
      expect(glasses.events.some((e) => e.kind === "json" && e.msg.type === "say" && e.msg.tts_id !== SCOUT_STOP_ID)).toBe(false);
      await post("/scout/glasses-mute", { muted: false });
      await post("/scout/teacher");
      await glasses.waitFor("say", (m) => m.tts_id === SCOUT_TEACHER_LINE);
    } finally {
      glasses.close();
      await hive.stop();
      if (prev === undefined) delete process.env.SCOUT_GLASSES_MUTED;
      else process.env.SCOUT_GLASSES_MUTED = prev;
    }
  });
});

// ---------------------------------------------------------------- waiting until "Start your adventure"

describe("waiting (silent until POST /scout/reset)", () => {
  test("a fresh game waits: no detections, no lines, no answers, no teacher, no force", () => {
    for (const mode of ["demo", "practice", "booth"] as const) {
      const s = setup();
      s.game.setMode(mode);
      expect(s.game.state().phase).toBe("waiting");
      expect(s.game.isStarted()).toBe(false);
      s.look(mode === "booth" ? "native_flower" : "honey_jar", 5000);
      s.advance(120_000);
      expect(s.events.some((e) => e.type === "found" || e.type === "blocked")).toBe(false);
      expect(s.events.some((e) => e.type === "frame")).toBe(true); // the dashboards still see the camera
      expect(s.game.utterance("yes")).toBe("other");
      expect(s.game.button()).toBe(false);
      expect(s.game.force()).toBe(false);
      expect(s.game.force("bug_spray")).toBe(false);
      expect(s.game.teacher()).toBe(false);
      expect(s.game.beginAnswer()).toBe(false);
      expect(s.game.skipStation()).toBe(false);
      expect(s.says).toEqual([]);
      s.game.start();
      expect(s.game.state().phase).toBe("intro");
      expect(s.game.isStarted()).toBe(true);
    }
  });

  test("stopJourney (POST /scout/stop): cuts the ear, clears badges, waits silently again", () => {
    const s = ready();
    s.game.setPractice(true);
    s.look("beehive", T.steadyMs);
    expect(s.game.state().found).toEqual(["beehive"]);
    s.game.stopJourney();
    expect(s.stops.length).toBe(1);
    expect(s.game.state()).toMatchObject({ phase: "waiting", found: [], offer: null, started_at: null });
    s.says.length = 0;
    s.look("honey_jar", 3000);
    s.advance(60_000);
    expect(s.says).toEqual([]);
    s.game.start(); // "Start your adventure" again
    s.advance(3000);
    s.says.length = 0;
    s.look("beehive", T.steadyMs);
    expect(s.ids()[1]).toBe("scout_fact_beehive"); // badges were cleared, so it fires again
  });
});
