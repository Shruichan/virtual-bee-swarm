// Live Bee Scout game: pure ScoutGame scenarios on a fake clock, plus the /scout routes on a real Hive.
import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { classifyAnswer, estimateMs, loadScoutLines, ScoutGame, IDLE_EVERY_MS, SKIP_REOFFER_MS, type ScoutSay } from "../src/scout";
import { SCOUT_LABELS, SCOUT_TUNING, type ScoutClass, type ScoutEvent, type ScoutLabel } from "../src/scout-protocol";
import { makeHive, TestClient, TOKEN } from "./helpers";

const LINES = loadScoutLines();
const T = SCOUT_TUNING;

function setup(opts: { durationMs?: number } = {}) {
  let clock = 1_000_000;
  const says: ScoutSay[] = [];
  const events: ScoutEvent[] = [];
  const game = new ScoutGame({
    lines: LINES,
    now: () => clock,
    say: (s) => says.push(s),
    event: (e) => events.push(e),
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
  return { game, says, events, ids, advance, frame, look, idle, now: () => clock };
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
    const have = new Set(LINES.map((l) => l.id));
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
    s.look("beehive", 600);
    s.frame([{ label: "beehive", score: 0.4 }]);
    s.advance(100);
    s.look("beehive", 600);
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
    const echo = hook.split(/[.!?]\s*/).filter(Boolean).at(-1)!; // "Want the gossip"
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

  test("idle nudge after idleMs of nothing, at most once a minute", () => {
    const s = ready(); // the intro ended 2 s after start; now = start + 2 s + gapMs + 100
    const firstIn = T.idleMs - T.gapMs - 100;
    s.advance(firstIn - 200);
    expect(s.ids()).toEqual([]);
    s.advance(300);
    expect(s.ids()).toEqual(["scout_idle"]);
    s.advance(IDLE_EVERY_MS - 400);
    expect(s.ids()).toEqual(["scout_idle"]);
    s.advance(500);
    expect(s.ids()).toEqual(["scout_idle", "scout_idle"]);
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

    // frames before /scout/start are allowed (dashboard testing without glasses)
    const t0 = Date.now();
    while (Date.now() - t0 < SCOUT_TUNING.steadyMs + 400) {
      const r = await post("/scout/frame", { at: Date.now(), frame_ms: 20, fps: 10, source: "test", top: [{ label: "honey_jar", score: 0.9 }, { label: "background", score: 0.2 }] });
      expect(r.status).toBe(200);
      await Bun.sleep(50);
    }
    await until(() => seen.some((e) => e.type === "found"), "found");
    expect(seen.find((e) => e.type === "found")).toMatchObject({ label: "honey_jar" });
    expect(seen.some((e) => e.type === "frame")).toBe(true);
    // ~5 frame events a second, not every frame
    expect(seen.filter((e) => e.type === "frame").length).toBeLessThan(12);
    await until(() => seen.filter((e) => e.type === "say").length >= 2, "say events");
    const chime = seen.find((e) => e.type === "say" && e.line_id === "scout_chime") as any;
    expect(chime.audio_url).toMatch(/\/scout\/audio\/scout_chime\.mp3$/);
    const hook = seen.find((e) => e.type === "say" && e.line_id === "scout_hook_honey_jar") as any;
    expect(hook.audio_url).toBe(null); // no mp3 on disk -> the glasses speak the caption

    const busSay = await glasses.waitFor("say", (m) => m.tts_id === "scout_hook_honey_jar");
    expect(busSay).toMatchObject({ channel: "ear", line_id: null, audio_url: null });
    expect(busSay.caption.toLowerCase()).toContain("honey");

    const ans = await (await post("/scout/utterance", { text: "yes please", at: Date.now() })).json();
    expect(ans).toMatchObject({ answer: "yes", phase: "telling" });
    await until(() => seen.some((e) => e.type === "say" && e.line_id === "scout_fact_honey_jar"), "fact");
    const state = await (await fetch(`${hive.url}/scout/state${q}`)).json();
    expect(state.found).toEqual(["honey_jar"]);

    expect((await post("/scout/slide", { label: "beehive", at: Date.now() })).status).toBe(200);
    await until(() => seen.some((e) => e.type === "slide"), "slide");
    expect((await post("/scout/slide", { label: "snowboard" })).status).toBe(400);

    const start = await (await post("/scout/start", { at: Date.now() })).json();
    expect(start.state).toMatchObject({ phase: "intro", found: [] });
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

  test("practice mode: the same object triggers again after the slide changes, never on the same slide", () => {
    const s = ready();
    s.game.setPractice(true);
    expect(s.game.state().practice).toBe(true);
    s.game.slide("honey_jar", s.now());
    s.look("honey_jar", 1000);
    s.game.utterance("yes");
    s.idle();
    s.events.length = 0;
    s.look("honey_jar", 1500); // same slide: no repeat
    expect(s.events.some((e) => e.type === "found")).toBe(false);
    s.game.slide(null, s.now());
    s.game.slide("honey_jar", s.now());
    s.look("honey_jar", 1000);
    expect(s.events.some((e) => e.type === "found" && e.label === "honey_jar")).toBe(true);
    s.game.utterance("yes");
    expect(s.game.state().found).toEqual(["honey_jar"]); // a repeat doesn't count twice
    expect(s.ids()).not.toContain("scout_progress_2");
  });
});
