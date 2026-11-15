// The window answers questions: Ask Beeswax shows the nearest recorded scene at once and, with the live stream up,
// Claude's checked prompt; Beeswax points at it after his answer; the moment ends on its own. A custom what-if with no
// live stream shows its nearest clip instead of nothing. Ask and window Claude calls are mocked; the relay is a stand-in.
import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import * as askModule from "../src/ask";
import { clearCaches, forget, setAskClient, type AskClient } from "../src/ask";
import { SCENE_TABLE } from "../src/lines";
import type { Hive, RouteResultLike } from "../src/server";
import * as windowModule from "../src/window";
import { POINTERS, matchWindow, setWindowClient, writePrompt, type WindowClient } from "../src/window";
import { TestClient, TOKEN, makeHive, mockRoute, setBeat, type MockDeps } from "./helpers";

type H = Hive & { deps: MockDeps };
let hive: H | null = null;
const clients: TestClient[] = [];
const relays: Array<ReturnType<typeof Bun.serve>> = [];
const connect = async (role: "quest" | "board") => {
  const c = await TestClient.connect(hive!, role);
  clients.push(c);
  return c;
};

const SETTING = "A sunny wildflower meadow with a white wooden beehive on a low stand in the center, with oak trees under a soft blue sky.";
const CAMERA = "Wide shot, eye-level, static camera, deep depth of field.";
const BARE = `A bare grey field with a white wooden beehive on a low stand in the center, under a flat white sky. Empty market stalls stand beside the hive with a few small grey stones. ${CAMERA}`;

const reply = (text: string) => ({
  id: "msg_test",
  type: "message",
  role: "assistant",
  model: "claude-haiku-5-5",
  stop_reason: "end_turn",
  stop_details: null,
  content: [{ type: "text", text }],
  usage: { input_tokens: 80, output_tokens: 30, cache_read_input_tokens: 0, cache_creation_input_tokens: 0 },
});
const answer = (text: string, fact_ids: string[]) => reply(JSON.stringify({ kind: "answer", canned_id: null, fact_ids, text }));

function mockAsk(text: string, facts: string[]): void {
  setAskClient({ beta: { messages: { create: () => Promise.resolve(answer(text, facts)) } } } as unknown as AskClient);
}
function mockWindow(prompt: string | null, delayMs = 30): { n: number } {
  const calls = { n: 0 };
  setWindowClient({
    beta: { messages: { create: () => (calls.n++, Bun.sleep(delayMs).then(() => reply(JSON.stringify({ prompt })))) } },
  } as unknown as WindowClient);
  return calls;
}
function mockWindowSeq(prompts: Array<string | null>, delayMs = 30): { n: number } {
  const calls = { n: 0 };
  setWindowClient({
    beta: { messages: { create: () => { const p = prompts[Math.min(calls.n++, prompts.length - 1)] ?? null; return Bun.sleep(delayMs).then(() => reply(JSON.stringify({ prompt: p }))); } } },
  } as unknown as WindowClient);
  return calls;
}
const never = { beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } } };

function fakeRelay() {
  const posts: Array<{ path: string; body: any }> = [];
  let status: Record<string, unknown> = { type: "orbis", state: "off", stage: 0, since: 1, fps: 0, prompt: "" };
  const set = (patch: Record<string, unknown>) => (status = { ...status, ...patch, since: (status.since as number) + 1 });
  const server = Bun.serve({
    port: 0,
    hostname: "127.0.0.1",
    async fetch(req) {
      if (req.headers.get("x-hive-token") !== TOKEN) return Response.json({ error: "bad token" }, { status: 401 });
      const path = new URL(req.url).pathname;
      if (req.method === "GET") return Response.json(status);
      const body = (await req.json()) as { prompt?: string; scene?: string };
      posts.push({ path, body });
      if (path === "/orbis/start") set({ state: "priming", prompt: body.prompt, scene: body.scene });
      else if (path === "/orbis/stage") set({ prompt: body.prompt, scene: body.scene });
      return Response.json({ ok: true, ...status });
    },
  });
  relays.push(server);
  return { posts, set, url: `http://127.0.0.1:${server.port}`, stages: () => posts.filter((p) => p.path === "/orbis/stage") };
}
async function goLive(board: TestClient, relay: ReturnType<typeof fakeRelay>): Promise<void> {
  board.send({ type: "orbis.request", want: "live" });
  await board.waitFor("orbis", (m) => m.state === "priming");
  relay.set({ state: "ready" });
  await board.waitFor("orbis", (m) => m.state === "ready");
}

beforeEach(() => {
  setAskClient(never as unknown as AskClient);
  setWindowClient(never as unknown as WindowClient);
  forget();
  clearCaches();
  windowModule.resetPointers();
  process.env.ASK_WARM_AUDIO = "0";
});
afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  if (hive) await hive.stop();
  hive = null;
  for (const r of relays.splice(0)) r.stop(true);
  setAskClient(never as unknown as AskClient);
  setWindowClient(null);
  delete process.env.ASK_WARM_AUDIO;
});

describe("matchWindow", () => {
  test.each([
    ["what happens if the bees disappear", "moon"],
    ["what would a world without bees look like", "moon"],
    ["do pesticides hurt you", "whatif_spray"],
    ["what are varroa mites", "feed_sick"],
    ["show me a wildflower meadow", "whatif_flowers"],
    ["how do I help, plant stuff?", "whatif_flowers"],
    ["which crops need you", "full_bloom"],
    ["does climate change hurt bees", "heat_stress"],
    ["do bees help after wildfires", "fire_out"],
  ])("%s -> %s", (said, scene) => expect(matchWindow(said)?.scene).toBe(scene as any));
  test("small talk leaves the window alone; the answer is the second chance", () => {
    expect(matchWindow("what's your name")).toBeNull();
    for (const q of ["what's your favorite food", "are you fired up", "is that a rainbow"]) expect(matchWindow(q)).toBeNull();
    expect(matchWindow("how can I help", "Plant native flowers that bloom from spring to fall.")?.scene).toBe("whatif_flowers");
  });
  test("pointer lines are short", () => {
    for (const l of [...POINTERS.bleak, ...POINTERS.green]) expect(l.length).toBeLessThanOrEqual(110);
  });
});

describe("writePrompt", () => {
  test("cleans Claude's prompt to the scene rules; null, junk and timeouts come back as null", async () => {
    mockWindow(BARE);
    expect((await writePrompt("why are bees disappearing", "moon")).prompt).toBe(BARE);
    mockWindow(`${SETTING} Bees fight off a bear with blood everywhere. ${CAMERA}`);
    expect(await writePrompt("x", null)).toMatchObject({ prompt: null, error: "bad_prompt" });
    mockWindow(null);
    expect((await writePrompt("what's your name", null)).prompt).toBeNull();
    mockWindow(BARE, 500);
    expect(await writePrompt("slow", null, { timeoutMs: 50 })).toMatchObject({ prompt: null, error: "timeout" });
  });
});

describe("the window reacts to Ask Beeswax", () => {
  test("clips: the nearest scene goes up before the answer, Beeswax points at it, and the moment ends", async () => {
    mockAsk("We pollinate a lot of your food. No bees, fewer crops.", ["crops"]);
    hive = makeHive({ ask: askModule, window: windowModule }, { askWindowHoldMs: 250 }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom");
    quest.say("what would a world without bees look like?");
    const shown = await quest.waitFor("orbis", (m) => m.scene === "moon");
    expect(shown.fallback_clip).toBe(SCENE_TABLE.moon!.clip);
    expect(shown.clip_first_s).toBeGreaterThan(0); // Unity shows the clip first, even with live frames flowing
    const said = await quest.waitFor("say", (m) => typeof m.caption === "string" && m.caption.includes("Tuesday"));
    expect(said.caption.endsWith(POINTERS.bleak[0]!)).toBe(true);
    expect(quest.msgs.findIndex((m) => m.type === "orbis" && m.scene === "moon")).toBeLessThan(quest.msgs.indexOf(said));
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom" && quest.msgs.indexOf(m) > quest.msgs.indexOf(said), 1500);
  });

  test("small talk: the window stays put and there is no pointer", async () => {
    mockAsk("Beeswax. Shop steward. Local Twenty Thousand.", ["beeswax"]);
    hive = makeHive({ ask: askModule, window: windowModule }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom");
    quest.say("who are you?");
    const said = await quest.waitFor("say", (m) => typeof m.caption === "string" && m.caption.includes("Shop steward"));
    expect(said.caption).not.toContain("Look");
    expect(quest.msgs.some((m) => m.type === "orbis" && m.scene !== "full_bloom" && quest.msgs.indexOf(m) > 0 && m.scene !== "meadow")).toBe(false);
  });

  test("live: the nearest clip at once, the relay morphs to Claude's checked prompt (not the nearest scene first)", async () => {
    mockAsk("We pollinate a lot of your food. No bees, fewer crops.", ["crops"]);
    const calls = mockWindow(BARE, 80);
    const relay = fakeRelay();
    hive = makeHive({ ask: askModule, window: windowModule }, { orbisRelayUrl: relay.url, orbisPollMs: 40, askWindowHoldMs: 400 }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await goLive(board, relay);
    const before = relay.stages().length;
    quest.say("why are the bees disappearing?");
    await quest.waitFor("orbis", (m) => m.scene === "moon");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_custom");
    await Bun.sleep(60);
    expect(calls.n).toBe(1);
    expect(relay.stages().slice(before).map((p) => p.body)).toEqual([{ prompt: BARE, scene: "whatif_custom", reset: true }]);
    // The moment ends with a cut back to the base scene.
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom" && quest.msgs.indexOf(m) > quest.msgs.findIndex((x) => x.scene === "whatif_custom"), 1500);
    await Bun.sleep(60);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: SCENE_TABLE.full_bloom!.prompt, scene: "full_bloom", reset: true });
  });

  test("live, Claude has nothing in time: the relay cuts to the nearest scene's own prompt", async () => {
    mockAsk("We pollinate a lot of your food. No bees, fewer crops.", ["crops"]);
    mockWindow(BARE, 400);
    const relay = fakeRelay();
    hive = makeHive({ ask: askModule, window: windowModule }, { orbisRelayUrl: relay.url, orbisPollMs: 40, askWindowMs: 60 }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await goLive(board, relay);
    const before = relay.stages().length;
    quest.say("why are the bees disappearing?");
    await quest.waitFor("orbis", (m) => m.scene === "moon");
    await Bun.sleep(250);
    expect(relay.stages().slice(before).map((p) => p.body)).toEqual([{ prompt: SCENE_TABLE.moon!.prompt, scene: "moon", reset: true }]);
  });
});

describe("the live window never gets stuck", () => {
  test("live small talk: Claude has nothing, the window and the relay stay as they are", async () => {
    mockAsk("Beeswax. Shop steward. Local Twenty Thousand.", ["beeswax"]);
    const calls = mockWindow(null);
    const relay = fakeRelay();
    hive = makeHive({ ask: askModule, window: windowModule }, { orbisRelayUrl: relay.url, orbisPollMs: 40 }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await goLive(board, relay);
    const before = relay.stages().length;
    const orbisBefore = quest.msgs.filter((m) => m.type === "orbis").length;
    quest.say("who are you?");
    await quest.waitFor("say", (m) => typeof m.caption === "string" && m.caption.includes("Shop steward"));
    await Bun.sleep(150);
    expect(calls.n).toBe(1);
    expect(relay.stages().length).toBe(before);
    expect(quest.msgs.filter((m) => m.type === "orbis").slice(orbisBefore).every((m) => m.scene === "full_bloom")).toBe(true);
  });

  test("live Q1 cut, then small talk Q2: the moment still ends and the relay cuts back to the base scene", async () => {
    mockAsk("We pollinate a lot of your food. No bees, fewer crops.", ["crops"]);
    const calls = mockWindowSeq([BARE, null]);
    const relay = fakeRelay();
    hive = makeHive(
      { ask: askModule, window: windowModule },
      { orbisRelayUrl: relay.url, orbisPollMs: 40, askWindowHoldMs: 500, askWindowGapMs: 0 },
    ) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await goLive(board, relay);
    quest.say("why are the bees disappearing?");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_custom");
    await Bun.sleep(60);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: BARE, scene: "whatif_custom", reset: true });
    mockAsk("Beeswax. Shop steward. Local Twenty Thousand.", ["beeswax"]);
    quest.say("who are you?");
    await quest.waitFor("say", (m) => typeof m.caption === "string" && m.caption.includes("Shop steward"));
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom" && quest.msgs.indexOf(m) > quest.msgs.findIndex((x) => x.scene === "whatif_custom"), 1500);
    await Bun.sleep(60);
    expect(calls.n).toBe(2);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: SCENE_TABLE.full_bloom!.prompt, scene: "full_bloom", reset: true });
  });

  test("a canned answer keeps its exact text (and its cached audio): no pointer on it", async () => {
    hive = makeHive({ ask: askModule, window: windowModule }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("why are the bees disappearing?");
    await quest.waitFor("orbis", (m) => m.scene === "moon");
    const said = await quest.waitFor("say", (m) => typeof m.caption === "string" && m.caption.length > 0);
    expect(said.caption).not.toContain("Tuesday");
  });

  test("the clip holds past Claude's deadline, so the old live picture never flashes before the cut", async () => {
    mockAsk("We pollinate a lot of your food. No bees, fewer crops.", ["crops"]);
    mockWindow(null);
    hive = makeHive({ ask: askModule, window: windowModule }, { askWindowMs: 3500 }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("why are the bees disappearing?");
    const shown = await quest.waitFor("orbis", (m) => m.scene === "moon");
    expect(shown.clip_first_s).toBeGreaterThanOrEqual(3.5 + 1.5);
  });
});

describe("custom what-if without a live stream", () => {
  test("shows the nearest clip with the picturing line; undo goes back", async () => {
    const PROMPT = `${SETTING} The meadow turns bare and grey around the quiet hive. ${CAMERA}`;
    const route = async (text: string): Promise<RouteResultLike> =>
      text.includes("disappear") ? { intent: "what_if", topic: "custom", answer: null, custom_prompt: PROMPT, ms: 5, path: "claude" } : mockRoute(text);
    hive = makeHive({ window: windowModule, route }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.say("what if all the bees disappeared");
    await quest.waitFor("say", (m) => typeof m.line_id === "string" && m.line_id.startsWith("room_whatif_custom") && m.line_id !== "room_whatif_custom_clips");
    await quest.waitFor("orbis", (m) => m.scene === "moon");
    quest.say("put it out");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_back");
    await quest.waitFor("orbis", (m) => m.scene === "meadow");
  });
});
