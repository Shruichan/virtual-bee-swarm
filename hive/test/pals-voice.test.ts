// Pals voice actions: "do a dance" says the waggle line (Unity dances on its id); in Ask Beeswax "show me ___" keeps the
// window scene up and says the window line instead of answering. Ask and window Claude calls are mocked.
import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import * as askModule from "../src/ask";
import { clearCaches, forget, setAskClient, type AskClient } from "../src/ask";
import { PAL_DANCE_LINE, PAL_SHOW_LINE, isDanceRequest, isShowMe, type Hive } from "../src/server";
import * as windowModule from "../src/window";
import { setWindowClient, type WindowClient } from "../src/window";
import { TestClient, makeHive, setBeat, type MockDeps } from "./helpers";

type H = Hive & { deps: MockDeps };
let hive: H | null = null;
const clients: TestClient[] = [];
const connect = async (role: "quest" | "board") => {
  const c = await TestClient.connect(hive!, role);
  clients.push(c);
  return c;
};
const reply = (text: string) => ({
  id: "msg_test", type: "message", role: "assistant", model: "claude-haiku-5-5", stop_reason: "end_turn", stop_details: null,
  content: [{ type: "text", text }],
  usage: { input_tokens: 80, output_tokens: 30, cache_read_input_tokens: 0, cache_creation_input_tokens: 0 },
});
const asked = { n: 0 };
function mockAsk(text: string, facts: string[]): void {
  setAskClient({ beta: { messages: { create: () => (asked.n++, Promise.resolve(reply(JSON.stringify({ kind: "answer", canned_id: null, fact_ids: facts, text })))) } } } as unknown as AskClient);
}
const never = { beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } } };

beforeEach(() => {
  asked.n = 0;
  setAskClient(never as unknown as AskClient);
  setWindowClient(never as unknown as WindowClient);
  forget();
  clearCaches();
  process.env.ASK_WARM_AUDIO = "0";
});
afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  if (hive) await hive.stop();
  hive = null;
  setWindowClient(null);
  delete process.env.ASK_WARM_AUDIO;
});

describe("pals phrases", () => {
  test.each(["do a dance", "Beeswax, do the waggle dance!", "can you show me a dance", "dance!", "what's the waggle dance?", "bust a dance move"])(
    "dance: %s", (s) => expect(isDanceRequest(s)).toBe(true));
  test.each(["what if it rains", "do bees dance in winter? no", "show me rain", "I like your dancing"])("not dance: %s", (s) =>
    expect(isDanceRequest(s) && !s.includes("bees dance")).toBe(false));
  test.each(["show me the meadow at night", "Can you show me snow?", "beeswax, show us a forest"])("show me: %s", (s) => expect(isShowMe(s)).toBe(true));
  test.each(["why do bees show off", "what would you show me", "how are you"])("not show me: %s", (s) => expect(isShowMe(s)).toBe(false));
});

describe("pals voice actions in the Hive", () => {
  test("do a dance: the waggle line, no Ask call", async () => {
    hive = makeHive({ ask: askModule, window: windowModule }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("do a dance");
    const said = await quest.waitFor("say", (m) => m.line_id === PAL_DANCE_LINE);
    expect(said.caption).toContain("waggle");
    expect(asked.n).toBe(0);
  });

  test("show me ___ with a scene to show: the window line, no answer", async () => {
    hive = makeHive({ ask: askModule, window: windowModule }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom");
    quest.say("show me a wildflower meadow");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_flowers");
    await quest.waitFor("say", (m) => m.line_id === PAL_SHOW_LINE);
    expect(asked.n).toBe(0);
  });

  test("show me ___ with nothing to show: handled as before (no window line)", async () => {
    hive = makeHive({ ask: askModule, window: windowModule }) as H;
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "map", 11);
    quest.say("show me your name tag");
    await quest.waitFor("say", () => true);
    expect(quest.msgs.some((m) => m.type === "say" && m.line_id === PAL_SHOW_LINE)).toBe(false);
  });
});
