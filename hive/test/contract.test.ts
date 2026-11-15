// The contract across the repo: protocol.ts vs the Unity C# mirror, the data files every client reads, every line id
// referenced in code, and the real line catalog end to end (no fixture lines). No network, no keys.
import { afterEach, describe, expect, test } from "bun:test";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { LINES, lineIdForText, lineText, verdictLine } from "../src/lines";
import * as P from "../src/protocol";
import type { Hive } from "../src/server";
import { TestClient, makeHive, post, setBeat, type MockDeps } from "./helpers";

const REPO = resolve(import.meta.dir, "../..");
const read = (p: string) => readFileSync(join(REPO, p), "utf8");
const CS = read("unity/BeeswaxClient/Runtime/BeeswaxProtocol.cs");
const TS = read("hive/src/protocol.ts");

/** The body of the `{` at `open`, braces balanced. */
function bodyAt(src: string, open: number): string {
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    if (src[i] === "{") depth++;
    else if (src[i] === "}" && --depth === 0) return src.slice(open + 1, i);
  }
  return src.slice(open + 1);
}

/** "Class.Name" -> value of every const in the C# static classes (Scene.Meadow -> "meadow", BinaryKind.ScanPhoto -> "0x01"). */
const CONSTS = new Map<string, string>();
for (const m of CS.matchAll(/public static class (\w+)\s*\{/g)) {
  if (m[1] === "BeeswaxProtocol") continue;
  const body = bodyAt(CS, m.index! + m[0].length - 1);
  for (const c of body.matchAll(/(\w+)\s*=\s*"([^"]*)"/g)) CONSTS.set(`${m[1]}.${c[1]}`, c[2]!);
  for (const c of body.matchAll(/(\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*[,;]/g)) CONSTS.set(`${m[1]}.${c[1]}`, c[2]!);
}
const tok = (t: string) => (t.trim().startsWith('"') ? t.trim().slice(1, -1) : (CONSTS.get(t.trim()) ?? `?${t.trim()}`));
function csArray(name: string): string[] {
  const m = CS.match(new RegExp(`string\\[\\] ${name} =\\s*\\{([\\s\\S]*?)\\}`));
  if (!m) throw new Error(`no C# array ${name}`);
  return m[1]!.split(",").map((s) => s.trim()).filter(Boolean).map(tok);
}

describe("Unity C# mirror (unity/BeeswaxClient/Runtime/BeeswaxProtocol.cs)", () => {
  test("version, enums and sets match protocol.ts one to one", () => {
    expect(CS).toContain(`public const int Version = ${P.PROTOCOL_VERSION};`);
    const pairs: Array<[string, readonly string[]]> = [
      ["Beats", P.BEATS], ["ScanBeats", P.SCAN_BEATS], ["VoiceBeats", P.VOICE_BEATS], ["Stations", P.STATIONS],
      ["Scenes", P.SCENES], ["WhatIfTopics", P.WHATIF_TOPICS], ["Fx", P.FX], ["Intents", P.INTENTS],
      ["Items", P.ITEMS], ["Verdicts", P.VERDICTS],
    ];
    for (const [name, ts] of pairs) expect({ name, cs: csArray(name) }).toEqual({ name, cs: [...ts] });
  });

  test("CHOICES, CORRECT_CHOICE, SOCKET_OF and BINARY_KIND", () => {
    const choices = CS.match(/s_choices = new[\s\S]*?\};/)![0];
    const parsed: Record<string, string[]> = {};
    for (const m of choices.matchAll(/\{ (Station\.\w+), new\[\] \{([^}]*)\} \}/g)) parsed[tok(m[1]!)] = m[2]!.split(",").map(tok);
    expect(parsed).toEqual(Object.fromEntries(P.STATIONS.map((s) => [s, [...P.CHOICES[s]]])));
    const correct = CS.match(/s_correct = new[\s\S]*?\};/)![0];
    const pc: Record<string, string> = {};
    for (const m of correct.matchAll(/\{ (Station\.\w+), (Choice\.\w+) \}/g)) pc[tok(m[1]!)] = tok(m[2]!);
    expect(pc).toEqual({ ...P.CORRECT_CHOICE });
    for (const [item, socket] of Object.entries(P.SOCKET_OF)) {
      const key = [...CONSTS].find(([k, v]) => k.startsWith("Item.") && v === item)?.[0];
      expect({ item, mapped: CS.includes(`case ${key}: return ${socket};`) }).toEqual({ item, mapped: true });
    }
    for (const [k, v] of Object.entries(P.BINARY_KIND)) {
      const name = k.split("_").map((w) => w[0] + w.slice(1).toLowerCase()).join("");
      expect({ k, cs: Number(CONSTS.get(`BinaryKind.${name}`)) }).toEqual({ k, cs: v });
    }
  });

  test("every message type exists on both sides, and each C# class carries every field of its TS interface", () => {
    const tsTypes = new Set([...TS.matchAll(/type: "([a-z.]+)"/g)].map((m) => m[1]!));
    const csTypes = new Set([...CONSTS].filter(([k]) => k.startsWith("MsgType.")).map(([, v]) => v));
    expect([...csTypes].sort()).toEqual([...tsTypes].sort());
    let checked = 0;
    const src = TS.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
    for (const m of src.matchAll(/export interface (\w+) \{/g)) {
      const fields = [...bodyAt(src, m.index! + m[0].length - 1).matchAll(/(?:^|[;{\s])(\w+)\??:/g)].map((x) => x[1]!).filter((f) => f !== "type");
      const cls = CS.match(new RegExp(`class ${m[1]}\\b[^{]*\\{`));
      expect({ iface: m[1], inCs: Boolean(cls) }).toEqual({ iface: m[1], inCs: true });
      const body = bodyAt(CS, cls!.index! + cls![0].length - 1);
      for (const f of fields) expect({ iface: m[1], f, inCs: new RegExp(`\\b${f}\\b`).test(body) }).toEqual({ iface: m[1], f, inCs: true });
      checked++;
    }
    expect(checked).toBeGreaterThan(30);
  });
});

describe("data files", () => {
  test("Unity's offline station table is a copy of hive/lines/stations.json", () => {
    expect(JSON.parse(read("unity/BeeswaxClient/Resources/beeswax_stations.json"))).toEqual(JSON.parse(read("hive/lines/stations.json")));
  });

  test("every SCENES entry has a prompt and the clip name the relay and record_clips use; the relay accepts the id", () => {
    const scenes = JSON.parse(read("hive/lines/orbis_scenes.json")) as Record<string, { prompt?: string; clip?: string }>;
    for (const scene of P.SCENES) {
      expect({ scene, prompt: Boolean(scenes[scene]?.prompt?.trim()), clip: scenes[scene]?.clip }).toEqual({ scene, prompt: true, clip: `${scene}.mp4` });
      expect(scene).toMatch(/^[a-z0-9_]{1,40}$/); // relay.py SCENE_ID
    }
    for (const t of P.WHATIF_TOPICS) {
      expect(P.SCENES as readonly string[]).toContain(`whatif_${t}`);
      expect(LINES[`room_whatif_${t}`]?.channel).toBe("room");
    }
  });

  test("every line id written in code exists in hive/lines/lines.json", () => {
    // Optional ids the code checks for before using them (v1 leftovers that are fine to be missing).
    const OPTIONAL = new Set(["ear_swarm_arrived", "room_feed_lift_hint", "room_feed_rock_retry", "room_ask_anything"]); // Unity-only lines (scripts/render-voice.ts)
    const skip = new Set(["node_modules", "dist", "out", "__pycache__", "clips", ".venv"]);
    const files: string[] = [];
    const walk = (dir: string) => {
      for (const f of readdirSync(dir)) {
        if (skip.has(f)) continue;
        const p = join(dir, f);
        if (statSync(p).isDirectory()) walk(p);
        else if (/\.(ts|tsx|cs|py)$/.test(f) && !f.endsWith(".generated.ts")) files.push(p);
      }
    };
    for (const d of ["hive/src", "hive/scripts", "glasses/src", "unity/BeeswaxClient", "hive/orbis-relay"]) walk(join(REPO, d));
    const missing: string[] = [];
    for (const f of files) {
      for (const m of readFileSync(f, "utf8").matchAll(/(\.StartsWith\(\s*)?["'`]((?:room|ear)_[a-z0-9_]*[a-z0-9])["'`]/g)) {
        const id = m[2]!;
        const exists = m[1] ? Object.keys(LINES).some((line) => line.startsWith(id)) : Boolean(LINES[id]);
        if (!exists && !OPTIONAL.has(id)) missing.push(`${id} (${f.slice(REPO.length + 1)})`);
      }
    }
    expect(files.length).toBeGreaterThan(20);
    expect(missing).toEqual([]);
    // Built from parts in code: room_<station>_intro, room_<station>_<choice> (Unity's defaults), room_whatif_<topic>.
    for (const st of P.STATIONS) {
      expect(LINES[`room_${st}_intro`]?.channel).toBe("room");
      for (const ch of P.CHOICES[st]) expect(LINES[`room_${st}_${ch}`]?.channel).toBe("room");
    }
  });
});

// ---------------------------------------------------------------- the real catalog, end to end

let hive: (Hive & { deps: MockDeps }) | null = null;
const clients: TestClient[] = [];
async function connect(role: P.Role): Promise<TestClient> {
  const c = await TestClient.connect(hive!, role);
  clients.push(c);
  return c;
}
afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  await hive?.stop();
  hive = null;
});

/** A Hive on the shipped lines.json (helpers' mockDeps adds v1 verdict lines; this one doesn't). */
const realCatalog = () => makeHive({ lines: LINES, lineText, verdictLine, lineIdForText });

describe("glasses scan on the shipped catalog (v1 prop verdict lines are gone)", () => {
  test("a prop verdict still says something: the pre-rendered ack on the Quest, its caption in the ear", async () => {
    expect(verdictLine("red_rose", "bad", "room")).toBeNull();
    hive = realCatalog();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "feed", 4);
    let press = (await post(hive, "/glasses/press", { pressed_at: Date.now() })).json;
    let r = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "some red roses", pressed_at: Date.now() });
    expect(r.json).toMatchObject({ item: "red_rose", verdict: "bad", path: "speech", line_id: null, caption: "" }); // Beeswax says it
    expect(await quest.waitFor("scan.verdict")).toMatchObject({ item: "red_rose", line_id: "room_ack_denied", tts_id: null, caption: lineText("room_ack_denied") });

    // no Quest: the ear gets it (Mentra reads the caption; an mp3 when the live voice is up)
    quest.close();
    await Bun.sleep(30);
    press = (await post(hive, "/glasses/press", { pressed_at: Date.now() })).json;
    r = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "a bee hotel", pressed_at: Date.now() });
    expect(r.json).toMatchObject({ item: "bee_hotel", verdict: "good", line_id: null, audio_url: null, caption: lineText("room_ack_allow") });

    // the operator twin too; an unclear verdict uses the ear twin of the canned line
    board.send({ type: "verdict.force", item: "spray_bottle" });
    expect(await board.waitFor("scan.verdict", (m) => m.item === "spray_bottle")).toMatchObject({ caption: lineText("room_ack_grievance") });
    press = (await post(hive, "/glasses/press", { pressed_at: Date.now() })).json;
    hive.deps.judge = async () => ({ item: "unclear", verdict: "roast", confidence: 0.2, seen: "a blur", line: null, ms: 1, model: "mock", fallback: false });
    r = await post(hive, "/glasses/scan", { scan_id: press.scan_id, photo_url: undefined, heard: "uhh this thing", pressed_at: Date.now() });
    expect(r.json).toMatchObject({ item: "unclear", line_id: "ear_canned_unclear" });
  });
});

describe("hello", () => {
  test("a client on another protocol version is told once (error) and the Board sees a warning", async () => {
    hive = realCatalog();
    const board = await connect("board");
    const quest = await connect("quest");
    quest.send({ type: "hello", role: "quest", client: "old-unity", version: 1 });
    expect((await quest.waitFor("error")).message).toContain("protocol v1");
    await quest.waitFor("welcome");
    expect((await board.waitFor("log", (m) => m.text.includes("WARNING"))).text).toContain("old-unity");
    quest.send({ type: "hello", role: "quest", client: "old-unity", version: 1 }); // the 10 s keepalive hello
    await quest.waitFor("welcome", () => quest.of("welcome").length === 2);
    expect(quest.of("error")).toHaveLength(1);
    const ok = await connect("glasses");
    ok.send({ type: "hello", role: "glasses", client: "miniapp", version: P.PROTOCOL_VERSION });
    await ok.waitFor("welcome");
    expect(ok.of("error")).toHaveLength(0);
  });
});
