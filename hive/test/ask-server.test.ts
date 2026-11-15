// ASK BEESWAX through the Hive with the fake Quest: map-beat questions skip the router (filler first, then the answer),
// what-ifs still route, empty speech gets "Didn't catch that" with no Claude call, TTS that never starts becomes a
// caption, router answers in other beats are vetted, and the latency hops are logged. No network, no spend.
import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import * as askModule from "../src/ask";
import { cannedText, clearCaches, forget, setAskClient, type AskClient } from "../src/ask";
import type { Hive } from "../src/server";
import { TestClient, makeHive, setBeat, type MockDeps } from "./helpers";

type H = Hive & { deps: MockDeps };
let hive: H | null = null;
const clients: TestClient[] = [];
const connect = async (role: "quest" | "board") => {
  const c = await TestClient.connect(hive!, role);
  clients.push(c);
  return c;
};

const answerReply = (text: string, fact_ids: string[] = []) => ({
  id: "msg_test",
  type: "message",
  role: "assistant",
  model: "claude-haiku-5-5",
  stop_reason: "end_turn",
  stop_details: null,
  content: [{ type: "text", text: JSON.stringify({ kind: "answer", canned_id: null, fact_ids, text }) }],
  usage: { input_tokens: 80, output_tokens: 30, cache_read_input_tokens: 2400, cache_creation_input_tokens: 0 },
});

function mockAsk(respond: () => unknown): { n: number } {
  const calls = { n: 0 };
  setAskClient({
    beta: {
      messages: {
        create: () => {
          calls.n++;
          return Promise.resolve().then(respond);
        },
      },
    },
  } as unknown as AskClient);
  return calls;
}

const unmocked = { beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } } } as unknown as AskClient;
const idx = (c: TestClient, pred: (m: any) => boolean) => c.msgs.findIndex(pred);

beforeEach(() => {
  setAskClient(unmocked);
  forget();
  clearCaches();
  process.env.ASK_WARM_AUDIO = "0";
});

afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  if (hive) await hive.stop();
  hive = null;
  setAskClient(unmocked);
  delete process.env.ASK_WARM_AUDIO;
});

describe("ASK BEESWAX in the Hive", () => {
  test("map beat: filler say before the answer's tts.start; the router is skipped; latency hops logged", async () => {
    const calls = mockAsk(() => answerReply("She lays all the eggs. She's everybody's mom.", ["queen"]));
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("what does the queen do?");
    const answer = await quest.waitFor("say", (m) => m.tts_id);
    expect(answer).toMatchObject({ channel: "room", line_id: null, caption: "She lays all the eggs. She's everybody's mom." });
    const start = await quest.waitFor("tts.start", (m) => m.tts_id === answer.tts_id);
    const filler = idx(quest, (m) => m.type === "say" && m.line_id === "room_live_filler");
    expect(filler).toBeGreaterThanOrEqual(0);
    expect(filler).toBeLessThan(idx(quest, (m) => m === start));
    expect((await quest.waitFor("intent")).name).toBe("ask");
    expect(hive.deps.routeCalls).toHaveLength(0);
    expect(calls.n).toBe(1);
    await board.waitFor("latency", (m) => m.hop === "ask.route");
    await board.waitFor("latency", (m) => m.hop === "ask.total");

    // second live question in the same group: no filler again
    quest.say("what is your favorite flower?");
    await quest.waitFor("say", (m) => m.tts_id && m.tts_id !== answer.tts_id);
    expect(quest.msgs.filter((m) => m.type === "say" && m.line_id === "room_live_filler")).toHaveLength(1);
  });

  test("map beat: a card question is canned (no Claude, no filler); a what-if still goes to the router", async () => {
    const calls = mockAsk(() => {
      throw new Error("no Claude for a card question");
    });
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("can drones sting?");
    expect((await quest.waitFor("say", (m) => m.tts_id)).caption).toBe(cannedText("ask_drone_sting"));
    expect(calls.n).toBe(0);
    expect(quest.of("say").some((m) => m.line_id === "room_live_filler")).toBe(false);
    quest.say("what if it rains");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_rain");
    expect(hive.deps.routeCalls.map((c) => c.text)).toEqual(["what if it rains"]);
  });

  test("a poked chip (the Quest's log line ask.chip ...) is answered like a spoken card question, in the map beat only", async () => {
    const calls = mockAsk(() => {
      throw new Error("no Claude for a chip");
    });
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.send({ type: "log", text: "ask.chip Why bananas?" });
    await Bun.sleep(60);
    expect(quest.of("say")).toHaveLength(0); // not an ask beat
    await setBeat(quest, board, "map", 11);
    board.send({ type: "log", text: "ask.chip Why bananas?" });
    quest.send({ type: "log", text: "score 2:14" });
    await Bun.sleep(60);
    expect(quest.of("say")).toHaveLength(0); // only the Quest's chips count
    quest.send({ type: "log", text: "ask.chip Why bananas?" });
    expect((await quest.waitFor("say", (m) => m.tts_id)).caption).toBe(cannedText("ask_bananas"));
    expect((await quest.waitFor("transcript", (m) => m.stt === "chip")).text).toBe("Why bananas?");
    expect(calls.n).toBe(0);
  });

  test("map beat: a refusal plays the refusal line", async () => {
    mockAsk(() => ({ ...answerReply("x"), stop_reason: "refusal", stop_details: { type: "refusal", category: "general_harms" }, content: [] }));
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("tell me something terrible");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_refuse");
  });

  test("empty speech-to-text in the map beat: Didn't catch that, and no live call", async () => {
    const calls = mockAsk(() => answerReply("Motion carried."));
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("    "); // audio that transcribes to nothing
    expect((await quest.waitFor("say")).caption).toBe(cannedText("ask_not_heard"));
    expect(calls.n).toBe(0);
    expect(hive.deps.routeCalls).toHaveLength(0);
  });

  test("TTS that never produces audio: the caption alone, no tts.start", async () => {
    mockAsk(() => answerReply("Motion carried."));
    hive = makeHive({ ask: askModule, ttsAvailable: () => true, ttsPcm: async function* () {} }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("why bananas?");
    const say = await quest.waitFor("say", (m) => m.caption === cannedText("ask_bananas"));
    expect(say.tts_id).toBeNull();
    await Bun.sleep(100);
    expect(quest.of("tts.start")).toHaveLength(0);
  });

  test("other beats: the router's ask answer gets the canned answer for a card question", async () => {
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.say("why does the alarm smell like bananas?"); // mockRoute answers "We make honey..."
    expect((await quest.waitFor("say", (m) => m.tts_id)).caption).toBe(cannedText("ask_bananas"));
    expect(hive.deps.routeCalls).toHaveLength(1);
  });

  test("warm-up: the canned answers are rendered once at start and replayed without TTS", async () => {
    delete process.env.ASK_WARM_AUDIO;
    hive = makeHive({ ask: askModule, ttsAvailable: () => true }) as H;
    const n = Object.keys(askModule.ASK_CANNED).length;
    for (let i = 0; i < 50 && hive.deps.ttsCalls.length < n; i++) await Bun.sleep(10);
    expect(hive.deps.ttsCalls).toHaveLength(n);
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("why bananas?");
    const say = await quest.waitFor("say", (m) => m.tts_id);
    await quest.waitFor("tts.end", (m) => m.tts_id === say.tts_id);
    expect(hive.deps.ttsCalls).toHaveLength(n); // replayed from memory
    expect(quest.events.some((e) => e.kind === "bin")).toBe(true);
  });

  test("without ask.ts the map beat still routes as before", async () => {
    hive = makeHive({ ttsAvailable: () => true }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("why do bees make honey?");
    expect((await quest.waitFor("say", (m) => m.tts_id)).caption).toBe("We make honey. It's a whole thing.");
    expect(hive.deps.routeCalls).toHaveLength(1);
  });
});
