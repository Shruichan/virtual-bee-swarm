// Contract checker for protocol v2.1 (PROTOCOL_VERSION 2): who may send each bus message and who receives it, the
// HTTP routes PLAN.md's "Event contract" lists, the meaning of the data files (stations.json, lines.json,
// orbis_scenes.json), and the clip-less scene across the Hive, the relay and Unity. No network, no keys.
import { afterEach, describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { LINES } from "../src/lines";
import * as P from "../src/protocol";
import type { Hive } from "../src/server";
import { TOKEN, TestClient, makeHive, type MockDeps } from "./helpers";

const REPO = resolve(import.meta.dir, "../..");
const read = (p: string) => readFileSync(join(REPO, p), "utf8");
const TS = read("hive/src/protocol.ts");
const ALL_TYPES = [...new Set([...TS.matchAll(/type: "([a-z.]+)"/g)].map((m) => m[1]!))].sort();
const ROLES: readonly P.Role[] = ["quest", "board", "monitor", "glasses"];

/** Client -> Hive messages and the roles the Hive takes them from (protocol.ts comments; "any" = every role). */
const FROM: Record<string, readonly P.Role[] | "any"> = {
  hello: "any",
  "beat.set": ["quest"],
  "beat.request": ["board"],
  mode: ["board"],
  "group.reset": ["board"],
  "hum.set": ["board"],
  "verdict.force": ["board"],
  "say.force": ["board"],
  "orbis.request": ["board", "quest"],
  "choice.made": ["quest"],
  "choice.request": ["board"],
  "fx.force": ["board"],
  "whatif.force": ["board"],
  "listen.start": ["quest", "glasses"],
  "listen.end": ["quest", "glasses"],
  "swarm.arrived": ["quest"],
  "hornet.cooked": ["quest"],
  say: ["quest"], // Unity cues ear asides through the Hive
  plea: ["glasses", "board"],
  latency: "any",
  log: "any",
  error: "any",
};
/** Hive -> client only: a client sending one is never relayed to the others. */
const HIVE_ONLY = ["welcome", "scan.started", "scan.photo", "scan.verdict", "tts.start", "tts.end", "orbis", "transcript", "intent", "fx", "score"];

/** A minimal valid message of each client -> Hive type. */
const SAMPLE: Record<string, Record<string, unknown>> = {
  hello: { type: "hello", client: "contract", version: P.PROTOCOL_VERSION },
  "beat.set": { type: "beat.set", n: 1, name: "hook" },
  "beat.request": { type: "beat.request", next: true },
  mode: { type: "mode", mode: "duo" },
  "group.reset": { type: "group.reset" },
  "hum.set": { type: "hum.set", value: 0.5 },
  "verdict.force": { type: "verdict.force", item: "bee_hotel" },
  "say.force": { type: "say.force", line_id: "room_ack_grievance", channel: "room" },
  "orbis.request": { type: "orbis.request", scene: "meadow" },
  "choice.made": { type: "choice.made", station: "smoker", choice: "smoker", correct: true },
  "choice.request": { type: "choice.request", station: "feed", choice: "sugar_syrup" },
  "fx.force": { type: "fx.force", name: "swarm_calm" },
  "whatif.force": { type: "whatif.force", topic: "rain" },
  "listen.start": { type: "listen.start", source: "quest", mode: "ptt" },
  "listen.end": { type: "listen.end", source: "quest", cancel: true },
  "swarm.arrived": { type: "swarm.arrived", socket: 2 },
  "hornet.cooked": { type: "hornet.cooked", overshoot: false },
  say: { type: "say", channel: "ear", line_id: "ear_hint_whatif", tts_id: null, audio_url: null, caption: "" },
  plea: { type: "plea", text: "please save the bees" },
  latency: { type: "latency", hop: "contract.hop", ms: 3 },
  log: { type: "log", text: "contract log" },
  error: { type: "error", message: "contract error" },
};

let hive: (Hive & { deps: MockDeps }) | null = null;
const clients: TestClient[] = [];
async function connectAll(): Promise<Record<P.Role, TestClient>> {
  const out = {} as Record<P.Role, TestClient>;
  for (const role of ROLES) {
    out[role] = await TestClient.connect(hive!, role);
    clients.push(out[role]);
  }
  return out;
}
afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  await hive?.stop();
  hive = null;
});

describe("bus directions (v2.1)", () => {
  test("every message type in protocol.ts is classified: client -> Hive (with its senders) or Hive -> client only", () => {
    expect([...Object.keys(FROM), ...HIVE_ONLY].sort()).toEqual(ALL_TYPES);
    expect(Object.keys(SAMPLE).sort()).toEqual(Object.keys(FROM).sort());
  });

  test("each client -> Hive type is accepted from its senders and refused, by name, from every other role", async () => {
    hive = makeHive();
    const c = await connectAll();
    for (const [type, from] of Object.entries(FROM)) {
      const senders = from === "any" ? ROLES : from;
      for (const role of ROLES) {
        const before = c[role].of("error").length;
        c[role].send(type === "hello" ? { ...SAMPLE[type], role } : SAMPLE[type]!);
        await Bun.sleep(15);
        const errs = c[role].of("error").slice(before).map((m) => m.message as string);
        if (senders.includes(role)) expect({ type, role, errs }).toEqual({ type, role, errs: [] });
        else expect({ type, role, refused: errs.some((e) => e.includes(type)) }).toEqual({ type, role, refused: true });
      }
    }
  });

  test("a Hive -> client message sent by a client is never relayed (orbis included: only the Hive sets the window)", async () => {
    hive = makeHive();
    const c = await connectAll();
    await Bun.sleep(20);
    const seen = Object.fromEntries(ROLES.map((r) => [r, c[r].msgs.length]));
    const fakes: Record<string, Record<string, unknown>> = {
      welcome: { type: "welcome", group_id: "g-fake", beat: 9, mode: "solo", server_time: 1, host: "x" },
      "scan.started": { type: "scan.started", scan_id: "s-fake" },
      "scan.photo": { type: "scan.photo", scan_id: "s-fake" },
      "scan.verdict": { type: "scan.verdict", scan_id: "s-fake", item: "red_rose", verdict: "bad" },
      "tts.start": { type: "tts.start", tts_id: "t-fake", sample_rate: 16000, caption: "x" },
      "tts.end": { type: "tts.end", tts_id: "t-fake" },
      orbis: { type: "orbis", state: "ready", stage: 9, scene: "whatif_fire", fallback_clip: "whatif_fire.mp4" },
      transcript: { type: "transcript", text: "fake", final: true, source: "quest", ms: 1, stt: "x" },
      intent: { type: "intent", name: "what_if", topic: "fire", text: "fake", path: "keyword", ms: 1 },
      fx: { type: "fx", name: "embers" },
      score: { type: "score", elapsed_s: 1, text: "Hive saved in 0:01", grievances: 0 },
    };
    expect(Object.keys(fakes).sort()).toEqual([...HIVE_ONLY].sort());
    for (const role of ROLES) for (const m of Object.values(fakes)) c[role].send(m);
    await Bun.sleep(120);
    for (const role of ROLES) {
      const got = c[role].msgs.slice(seen[role]).map((m) => m.type).filter((t) => HIVE_ONLY.includes(t));
      expect({ role, relayed: got }).toEqual({ role, relayed: [] });
    }
    // The window report is logged for the observers instead (the sender is left out).
    expect((await c.board.waitFor("log", (m) => /quest reported window ready on whatif_fire/.test(m.text))).source).toBe("quest");
    expect(hive.state.scene).toBe("meadow");
  });

  test("who hears what: the v2.1 operator ops, the window and the score", async () => {
    hive = makeHive();
    const c = await connectAll();
    const mark = () => Object.fromEntries(ROLES.map((r) => [r, c[r].msgs.length])) as Record<P.Role, number>;
    const typesSince = (m: Record<P.Role, number>, role: P.Role) => c[role].msgs.slice(m[role]).map((x) => x.type as string);

    // fx.force: the Quest (and board/monitor) get a plain fx; nobody gets fx.force; the glasses get nothing
    let m = mark();
    c.board.send({ type: "fx.force", name: "hive_glow_red" });
    await c.quest.waitFor("fx", (x) => x.name === "hive_glow_red");
    await c.monitor.waitFor("fx", (x) => x.name === "hive_glow_red");
    await Bun.sleep(30);
    for (const r of ROLES) expect(typesSince(m, r)).not.toContain("fx.force");
    expect(typesSince(m, "glasses")).not.toContain("fx");

    // choice.request with a Quest: the Quest only
    m = mark();
    c.board.send({ type: "choice.request", station: "wasps", choice: "smaller_door" });
    expect(await c.quest.waitFor("choice.request")).toEqual({ type: "choice.request", station: "wasps", choice: "smaller_door" });
    await Bun.sleep(30);
    for (const r of ["board", "monitor", "glasses"] as const) expect(typesSince(m, r)).not.toContain("choice.request");

    // orbis.request{scene} (no want): the Quest gets it with the Hive's current mode; everyone gets the new orbis{}
    m = mark();
    c.board.send({ type: "orbis.request", scene: "heat_drink" });
    expect(await c.quest.waitFor("orbis.request")).toEqual({ type: "orbis.request", want: "clips", scene: "heat_drink", stage: P.SCENES.indexOf("heat_drink") });
    for (const r of ROLES) await c[r].waitFor("orbis", (x) => x.scene === "heat_drink");
    await Bun.sleep(30);
    for (const r of ["board", "monitor", "glasses"] as const) expect(typesSince(m, r)).not.toContain("orbis.request");

    // whatif.force: the usual say/orbis/fx, but no transcript, intent or whatif.force anywhere
    m = mark();
    c.board.send({ type: "whatif.force", topic: "fire" });
    await c.quest.waitFor("orbis", (x) => x.scene === "whatif_fire");
    await c.quest.waitFor("fx", (x) => x.name === "embers");
    await Bun.sleep(30);
    for (const r of ROLES) for (const t of ["transcript", "intent", "whatif.force"]) expect(typesSince(m, r)).not.toContain(t);

    // score: everyone, at the escape beat
    c.quest.send({ type: "beat.set", n: P.BEATS.indexOf("hook"), name: "hook" });
    await c.board.waitFor("beat.set", (x) => x.name === "hook");
    c.quest.send({ type: "beat.set", n: P.BEATS.indexOf("escape"), name: "escape" });
    for (const r of ROLES) {
      const s = await c[r].waitFor("score");
      expect(Object.keys(s).sort()).toEqual(["elapsed_s", "grievances", "text", "type"]);
    }
  });
});

describe("HTTP routes (PLAN.md Event contract)", () => {
  test("open, token-gated and missing routes answer as documented", async () => {
    hive = makeHive();
    const get = async (path: string, token: string | null = null) =>
      (await fetch(`${hive!.url}${path}${token ? `${path.includes("?") ? "&" : "?"}token=${token}` : ""}`)).status;
    const postJson = async (path: string, body: unknown, token: string | null = null) =>
      (await fetch(`${hive!.url}${path}${token ? `?token=${token}` : ""}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) })).status;

    expect(await get("/health")).toBe(200); // open: Unity, the miniapp and the pages poll it
    const h = (await (await fetch(`${hive.url}/health`)).json()) as any;
    expect(h.version).toBe(P.PROTOCOL_VERSION);
    expect(Object.keys(h.orbis).sort()).toEqual(["relay", "scene", "stage", "state", "want"]);
    for (const p of ["/lines.json", "/stations.json", "/board", "/monitor"]) {
      expect({ p, open: await get(p) }).toEqual({ p, open: 401 });
      expect({ p, authed: await get(p, TOKEN) }).toEqual({ p, authed: 200 });
    }
    expect(await postJson("/board/custom", { text: "what if it rains" })).toBe(401);
    for (const p of ["/glasses/press", "/glasses/scan", "/glasses/utterance", "/glasses/plea", "/glasses/latency", "/glasses/event"]) {
      expect({ p, open: await postJson(p, {}) }).toEqual({ p, open: 401 });
    }
    expect(await postJson("/glasses/event", { hello: 1 }, TOKEN)).toBe(200);
    expect(await postJson("/glasses/latency", { hop: "contract", ms: 1 }, TOKEN)).toBe(200);
    expect(await get("/audio/nope.mp3")).toBe(404);
    expect(await get("/bus")).toBe(400); // no role
  });
});

describe("data files mean what the contract says", () => {
  test("stations.json: STATIONS x CHOICES, correct = CORRECT_CHOICE, room lines that exist, SCENES, FX", () => {
    const st = JSON.parse(read("hive/lines/stations.json"));
    expect(Object.keys(st).sort()).toEqual([...P.STATIONS].sort());
    for (const s of P.STATIONS) {
      expect(Object.keys(st[s]).sort()).toEqual(["choices", "intro_line"]);
      expect(LINES[st[s].intro_line]?.channel).toBe("room");
      expect(Object.keys(st[s].choices).sort()).toEqual([...P.CHOICES[s]].sort());
      for (const [choice, o] of Object.entries<any>(st[s].choices)) {
        const where = `${s}.${choice}`;
        expect({ where, keys: Object.keys(o).every((k) => ["correct", "line_id", "scene", "fx"].includes(k)) }).toEqual({ where, keys: true });
        expect({ where, correct: o.correct }).toEqual({ where, correct: P.CORRECT_CHOICE[s] === choice });
        expect({ where, line: LINES[o.line_id]?.channel }).toEqual({ where, line: "room" });
        expect({ where, scene: (P.SCENES as readonly string[]).includes(o.scene) }).toEqual({ where, scene: true });
        if (o.fx !== undefined) expect({ where, fx: (P.FX as readonly string[]).includes(o.fx) }).toEqual({ where, fx: true });
        expect({ where, clip: o.scene !== "whatif_custom" }).toEqual({ where, clip: true }); // a choice needs a recorded clip
      }
    }
  });

  test("lines.json: unique ids whose prefix is the channel, known beats, alt:/topic: tags that resolve", () => {
    const raw = JSON.parse(read("hive/lines/lines.json")) as Array<{ id: string; channel: P.Channel; beat?: string; tags?: string[] }>;
    expect(new Set(raw.map((l) => l.id)).size).toBe(raw.length);
    for (const l of raw) {
      expect({ id: l.id, prefix: l.id.startsWith(`${l.channel}_`) }).toEqual({ id: l.id, prefix: true });
      if (l.beat !== undefined) expect({ id: l.id, beat: (P.BEATS as readonly string[]).includes(l.beat) }).toEqual({ id: l.id, beat: true });
      for (const t of l.tags ?? []) {
        if (t.startsWith("alt:")) expect({ id: l.id, t, base: LINES[t.slice(4)]?.channel }).toEqual({ id: l.id, t, base: l.channel });
        if (t.startsWith("topic:")) expect({ id: l.id, t, ok: (P.WHATIF_TOPICS as readonly string[]).includes(t.slice(6)) }).toEqual({ id: l.id, t, ok: true });
      }
    }
    // The v2.1 custom what-if lines the Hive picks by id.
    for (const id of ["room_whatif_custom", "room_whatif_custom_clips", "room_whatif_refuse"]) expect(LINES[id]?.channel).toBe("room");
  });

  test("orbis_scenes.json holds exactly SCENES; whatif_custom is the only scene without a recorded clip, on every side", () => {
    const sc = JSON.parse(read("hive/lines/orbis_scenes.json")) as Record<string, unknown>;
    expect(Object.keys(sc).filter((k) => !k.startsWith("_")).sort()).toEqual([...P.SCENES].sort());
    expect(TS).toMatch(/"whatif_custom", \/\/.*no recorded clip/);
    const relay = read("hive/orbis-relay/relay.py").match(/CLIPLESS_SCENES = frozenset\(\{([^}]*)\}\)/);
    expect(relay?.[1]?.split(",").map((s) => s.trim().replace(/"/g, ""))).toEqual(["whatif_custom"]);
    expect(read("unity/BeeswaxClient/Runtime/BeeswaxProtocol.cs")).toContain("IsScene(scene) && scene != Scene.WhatIfCustom");
    expect(read("hive/src/server.ts")).toMatch(/scene === "whatif_custom" \? \(customClip \?\? clipOf\(state\.base_scene\)\)/);
  });
});
