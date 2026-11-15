// Live Bee Scout game: pure ScoutGame scenarios on a fake clock, plus the /scout routes on a real Hive.
import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { classifyAnswer, estimateMs, loadScoutLines, ScoutGame, IDLE_EVERY_MS, SKIP_REOFFER_MS, type ScoutSay } from "../src/scout";
import { SCOUT_BOOTH_JUDGE_ORDER, SCOUT_BOOTH_LINES, SCOUT_BOOTH_ORDER, SCOUT_BOOTH_TUNING, SCOUT_LABELS, SCOUT_TUNING, type ScoutClass, type ScoutEvent, type ScoutLabel } from "../src/scout-protocol";
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
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_honey_jar"]);
    s.idle();
    s.events.length = 0;
    s.look("honey_jar", 1500); // same slide: no repeat
    expect(s.events.some((e) => e.type === "found")).toBe(false);
    s.game.slide(null, s.now());
    s.game.slide("honey_jar", s.now());
    s.look("honey_jar", 1000);
    expect(s.events.some((e) => e.type === "found" && e.label === "honey_jar")).toBe(true);
    expect(s.game.state().found).toEqual([]); // practice never touches progress
    expect(s.ids().filter((id) => id.startsWith("scout_progress") || id === "scout_done")).toEqual([]);
  });

  test("practice mode: found -> chime + fact straight away, no hook, no offer, then looking", () => {
    const s = ready();
    s.game.setPractice(true);
    s.events.length = 0;
    s.look("beehive", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_beehive"]);
    expect(s.game.state()).toMatchObject({ phase: "telling", offer: "beehive", found: [] });
    expect(s.events.find((e) => e.type === "found")).toMatchObject({ label: "beehive" });
    const hops = s.events.filter((e) => e.type === "latency").map((e: any) => e.hop);
    expect(hops).toEqual(expect.arrayContaining(["frame_to_found", "found_to_say"]));
    expect(s.game.utterance("yes")).toBe("other"); // nothing to answer
    expect(s.game.button()).toBe(false);
    s.advance(2000); // chime + fact, 1 s each
    expect(s.game.state()).toMatchObject({ phase: "looking", offer: null, found: [] });
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_beehive"]);
  });

  test("practice mode: six objects never reach scout_done", () => {
    const s = ready();
    s.game.setPractice(true);
    for (const l of SCOUT_LABELS) {
      s.advance(3000);
      s.look(l, T.steadyMs + 100);
    }
    s.advance(3000);
    expect(s.ids().filter((id) => id.startsWith("scout_fact_")).length).toBe(6);
    expect(s.ids()).not.toContain("scout_done");
    expect(s.game.state().phase).toBe("looking");
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

  test("practice mode: 8 s of looking away lets the same object fire again without a slide change", () => {
    const s = ready();
    s.game.setPractice(true);
    s.look("pebble_dish", T.steadyMs + 100);
    s.advance(2000);
    s.says.length = 0;
    s.look("background", 5000, 0.9, 0.1); // away, but not long enough
    s.look("pebble_dish", 2000);
    expect(s.ids()).toEqual([]);
    s.look("background", 8100, 0.9, 0.1); // away 8 s
    s.look("pebble_dish", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_pebble_dish"]);
  });

  test("practice mode: another class on top for 8 s also counts as away", () => {
    const s = ready();
    s.game.setPractice(true);
    s.look("bee_hotel", T.steadyMs + 100);
    s.advance(2000);
    s.look("native_flower", 8100, 0.5, 0.1); // top-1 but too weak to fire: still away from bee_hotel
    expect(s.ids()).not.toContain("scout_fact_native_flower");
    s.says.length = 0;
    s.look("bee_hotel", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime", "scout_fact_bee_hotel"]);
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
    const have = new Map(LINES.map((l) => [l.id, l]));
    const need: string[] = [SCOUT_BOOTH_LINES.intro, SCOUT_BOOTH_LINES.chimeAsk, SCOUT_BOOTH_LINES.silenceYes, SCOUT_BOOTH_LINES.no, SCOUT_BOOTH_LINES.rude, SCOUT_BOOTH_LINES.done];
    need.push(...Object.values(SCOUT_BOOTH_LINES.dir), ...SCOUT_LABELS.map((l) => SCOUT_BOOTH_LINES.next(l)));
    for (const id of need) expect(have.get(id)?.text, id).toBeTruthy();
    expect(have.get("scout_chime_ask")!.text).toContain("learn more");
    expect(have.has("scout_hook_beehive")).toBe(true); // the main file is still there
  });
});

describe("booth mode", () => {
  test("mode, order and next in the state; start plays the one booth intro", () => {
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

  test("only the expected-next object triggers: chime_ask, then offering", () => {
    const s = boothReady();
    s.look("honey_jar", 3000); // a later station, steady and clear
    expect(s.says).toEqual([]);
    expect(s.events.find((e) => e.type === "blocked")).toMatchObject({ label: "honey_jar", reason: "not_next" });
    s.look("native_flower", T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime_ask"]);
    expect(s.game.state()).toMatchObject({ phase: "offering", offer: "native_flower" });
    expect(s.events.find((e) => e.type === "found")).toMatchObject({ label: "native_flower" });
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
    expect(s.ids()).toEqual(["scout_chime_ask"]);
    expect(s.game.state().offer).toBe("native_flower");
  });

  test("boxes: the most central expected box wins; a lone edge box only cues", () => {
    const s = boothReady();
    s.see([box("native_flower", 0.1), box("honey_jar", 0.5), box("native_flower", 0.52)], T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime_ask"]);

    const t = boothReady();
    t.see([box("native_flower", 0.1), box("honey_jar", 0.5)], 3000, [{ label: "native_flower", score: 0.9 }]);
    expect(t.ids()).toEqual(["scout_dir_left"]); // seen at the frame edge: guide, don't trigger
    expect(t.game.state().phase).toBe("looking");
  });

  test("an AprilTag (via marker) counts at any score", () => {
    const s = boothReady();
    s.see([box("native_flower", 0.5, 0.5, 0.15, 0.15, 0.3, "marker")], T.steadyMs + 100);
    expect(s.ids()).toEqual(["scout_chime_ask"]);
  });

  test("yes -> fact -> direction to the next station -> looking for it", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs + 100);
    expect(s.game.utterance("yes")).toBe("yes");
    expect(s.ids()).toEqual(["scout_chime_ask", "scout_fact_native_flower", "scout_next_honey_jar"]);
    expect(s.game.state()).toMatchObject({ phase: "telling", found: ["native_flower"], next: "honey_jar" });
    s.advance(3000);
    expect(s.game.state()).toMatchObject({ phase: "looking", offer: null, next: "honey_jar" });
  });

  test("the glasses button is yes", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs + 100);
    expect(s.game.button()).toBe(true);
    expect(s.ids()).toContain("scout_fact_native_flower");
  });

  test("silence counts as yes after 4 s from the end of the ask", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs + 100);
    s.advance(1000 + B.offerTimeoutMs - 300);
    expect(s.game.state().phase).toBe("offering");
    s.advance(400);
    expect(s.ids()).toEqual(["scout_chime_ask", "scout_silence_yes", "scout_fact_native_flower", "scout_next_honey_jar"]);
    expect(s.game.state()).toMatchObject({ found: ["native_flower"], next: "honey_jar" });
  });

  test("no -> short reply, then on to the next station anyway; go away -> rude, on", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs + 100);
    expect(s.game.utterance("no thanks")).toBe("no");
    expect(s.ids()).toEqual(["scout_chime_ask", "scout_booth_no", "scout_next_honey_jar"]);
    expect(s.game.state()).toMatchObject({ found: [], skipped: ["native_flower"], next: "honey_jar" });
    s.advance(3000 + B.gapMs);
    s.says.length = 0;
    s.look("honey_jar", T.steadyMs + 100);
    expect(s.game.utterance("go away")).toBe("rude");
    expect(s.ids()).toEqual(["scout_chime_ask", "scout_rude", "scout_next_bug_spray"]);
    expect(s.game.state().next).toBe("bug_spray");
  });

  test("judge order by default: the finale lands after station 4 (bee_hotel)", () => {
    const s = boothReady();
    for (const l of SCOUT_BOOTH_JUDGE_ORDER) {
      expect(s.game.state().next).toBe(l);
      s.game.force();
      s.game.button();
      s.advance(5000);
    }
    expect(s.ids().slice(-2)).toEqual(["scout_fact_bee_hotel", "scout_booth_done"]);
    expect(s.game.state()).toMatchObject({ phase: "done", next: null });
  });

  test("after the last station: fact, scout_booth_done, phase done", () => {
    const s = boothReady(["honey_jar", "bug_spray"]);
    expect(s.game.state().next).toBe("honey_jar");
    s.look("honey_jar", T.steadyMs + 100);
    s.game.utterance("sure");
    s.advance(3000 + B.gapMs);
    s.says.length = 0;
    s.look("bug_spray", T.steadyMs + 100);
    s.game.utterance("yes");
    expect(s.ids()).toEqual(["scout_chime_ask", "scout_fact_bug_spray", "scout_booth_done"]);
    expect(s.game.state()).toMatchObject({ phase: "done", next: null, found: ["honey_jar", "bug_spray"] });
    s.advance(20_000);
    s.look("native_flower", 2000);
    expect(s.ids().at(-1)).toBe("scout_booth_done");
  });

  test("the booth gap: 1.5 s after the station lines end", () => {
    const s = boothReady();
    s.look("native_flower", T.steadyMs);
    s.game.utterance("yes"); // ask + fact + direction end 3 s after the find
    s.advance(3000);
    s.says.length = 0;
    s.look("honey_jar", B.gapMs - 100);
    expect(s.ids()).toEqual([]);
    s.look("honey_jar", 200);
    expect(s.ids()).toEqual(["scout_chime_ask"]);
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
    s.see([box("native_flower", 0.1)], 500); // while offering
    expect(s.ids()).toEqual(["scout_chime_ask"]);
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

  test("operator: force (next / given), yes, next, reset", () => {
    const s = boothReady();
    expect(s.game.force()).toBe(true); // the expected station, no dwell
    expect(s.ids()).toEqual(["scout_chime_ask"]);
    expect(s.game.state()).toMatchObject({ phase: "offering", offer: "native_flower" });
    expect(s.events.find((e) => e.type === "found")).toMatchObject({ label: "native_flower", look_ms: 0 });
    expect(s.game.button(undefined, "(operator yes)")).toBe(true);
    expect(s.game.state()).toMatchObject({ found: ["native_flower"], next: "honey_jar" });
    s.advance(4000);
    expect(s.game.skipStation()).toBe(true);
    expect(s.game.state()).toMatchObject({ next: "bug_spray", skipped: ["honey_jar"], phase: "looking" });
    expect(s.game.force("bee_hotel")).toBe(true); // jump ahead
    expect(s.game.state()).toMatchObject({ offer: "bee_hotel", next: "bee_hotel" });
    expect(s.game.force("bee_hotel")).toBe(false); // already offering it
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

      // boxes ride through POST /scout/frame: a centered AprilTag box alone triggers the expected station (fresh Hive: no gap)
      let found = false;
      const t0 = Date.now();
      while (!found && Date.now() - t0 < 6000) {
        await post("/scout/frame", { at: Date.now(), top: [{ label: "background", score: 0.9 }], boxes: [{ label: "honey_jar", score: 0.3, x: 0.4, y: 0.4, w: 0.2, h: 0.2, via: "marker" }, { label: "nope", score: 1, x: 0, y: 0, w: 1, h: 1 }] });
        found = (await (await fetch(`${hive.url}/scout/state?token=${TOKEN}`)).json()).phase === "offering";
        await Bun.sleep(80);
      }
      expect(found).toBe(true);
      expect((await post("/scout/force", {}, "nope")).status).toBe(401);
      expect((await post("/scout/mode", { mode: "party" })).status).toBe(400);
      expect((await post("/scout/mode", { mode: "booth", order: ["honey_jar", "honey_jar"] })).status).toBe(400);
      expect((await post("/scout/mode", { mode: "booth", order: ["snowboard"] })).status).toBe(400);
      expect(await (await post("/scout/mode", { mode: "booth", order: ["native_flower", "bee_hotel"] })).json()).toMatchObject({ mode: "booth", next: "native_flower" });

      expect((await post("/scout/yes")).status).toBe(409); // nothing offered yet
      expect((await post("/scout/force", { label: "snowboard" })).status).toBe(400);
      expect(await (await post("/scout/force")).json()).toMatchObject({ ok: true, phase: "offering", offer: "native_flower" });
      expect(await (await post("/scout/yes")).json()).toMatchObject({ ok: true, found: ["native_flower"], next: "bee_hotel" });
      expect(await (await post("/scout/next")).json()).toMatchObject({ ok: true, phase: "done", next: null });
      expect(await (await post("/scout/reset")).json()).toMatchObject({ ok: true, phase: "intro", next: "native_flower" });

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
    expect(s.ids()).toEqual(["scout_chime_ask"]);
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
    b.game.button();
    expect(b.says).toEqual(["scout_chime_ask", "scout_fact_native_flower", "scout_fact_native_flower_2", "scout_next_honey_jar"]);
  });
});
