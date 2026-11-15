// ASK BEESWAX (src/ask.ts), offline: the fact sheet, the cached prompt, canned matching, vet(), the model's replies with
// a fake client (no network, no spend), the filler, the echo guard and the first-chunk TTS deadline.
import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import Anthropic from "@anthropic-ai/sdk";
import {
  ASK_CANNED,
  ASK_EXAMPLES,
  ASK_FACTS,
  ASK_SCHEMA,
  ASK_SYSTEM_PROMPT,
  CANNED_IDS,
  DONT_SAY,
  MAX_CHARS,
  answer,
  askLimits,
  buildAskRequest,
  cannedMatch,
  cannedText,
  clearCaches,
  fixRouted,
  forget,
  isEcho,
  isFollowUp,
  noteReply,
  prepareSpeech,
  prewarm,
  setAskClient,
  vet,
  wants,
  type AskClient,
} from "../src/ask";
import { LINES } from "../src/lines";
import { OFF_LIST_ANSWER } from "../src/router";

type Call = { body: any; opts: any };
const DEFAULTS = { ...askLimits };

function reply(payload: unknown, extra: Record<string, unknown> = {}) {
  return {
    id: "msg_test",
    type: "message",
    role: "assistant",
    model: "claude-haiku-5-5",
    stop_reason: "end_turn",
    stop_details: null,
    content: [{ type: "text", text: typeof payload === "string" ? payload : JSON.stringify(payload) }],
    usage: { input_tokens: 80, output_tokens: 30, cache_read_input_tokens: 2400, cache_creation_input_tokens: 0 },
    ...extra,
  };
}

function mockClient(respond: (call: Call, n: number) => unknown) {
  const calls: Call[] = [];
  setAskClient({
    beta: {
      messages: {
        create(body: any, opts: any) {
          const call = { body, opts };
          calls.push(call);
          const n = calls.length;
          return Promise.resolve().then(() => respond(call, n));
        },
      },
    },
  } as unknown as AskClient);
  return calls;
}

const unmocked = { beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } } } as unknown as AskClient;
const ctx = (group = "g-test") => ({ beat: "map" as const, group });
const hang = (call: Call) =>
  new Promise((_, reject) => call.opts?.signal?.addEventListener("abort", () => reject(new Anthropic.APIUserAbortError())));
const apiError = (status: number) => Anthropic.APIError.generate(status, { type: "error", error: { type: "api_error", message: "boom" } }, "boom", new Headers());
const ans = (text: string, fact_ids: string[] = []) => ({ kind: "answer", canned_id: null, fact_ids, text });

beforeEach(() => {
  setAskClient(unmocked);
  Object.assign(askLimits, DEFAULTS);
  forget();
  clearCaches();
  delete process.env.ASK_MODEL;
  delete process.env.ASK_ON;
});
afterEach(() => {
  setAskClient(unmocked);
  Object.assign(askLimits, DEFAULTS);
});

// ---------------------------------------------------------------- ask.json

describe("ask.json", () => {
  test("every fact has a source, a unique id and a short spoken form with no digits", () => {
    const ids = new Set<string>();
    for (const f of ASK_FACTS) {
      expect(ids.has(f.id)).toBe(false);
      ids.add(f.id);
      expect(f.src.length).toBeGreaterThan(0);
      for (const s of f.src) expect(s).toMatch(/^(?:https:\/\/\S+|line:room_\w+|game|general knowledge.*)$/);
      if (f.src[0]!.startsWith("line:")) expect(LINES[f.src[0]!.slice(5)]).toBeDefined();
      expect(f.say.length).toBeLessThanOrEqual(MAX_CHARS);
      expect(f.say).not.toMatch(/\d/);
    }
    expect(ASK_FACTS.length).toBeGreaterThanOrEqual(35);
  });

  test("canned answers: 110 characters or less, at most three short sentences, no digits, and their facts exist", () => {
    for (const c of Object.values(ASK_CANNED)) {
      expect(c.id).toMatch(/^ask_[a-z_]+$/);
      expect(c.text.length).toBeLessThanOrEqual(MAX_CHARS);
      expect(c.text.split(/(?<=[.!?])\s+/).length).toBeLessThanOrEqual(4); // sting_help: "Stung? ... Allergic? ..."
      expect(c.text).not.toMatch(/\d/);
      for (const f of c.facts) expect(ASK_FACTS.some((x) => x.id === f)).toBe(true);
      // ask_* ids stay out of lines.json (its 110-line cap and the room_/ear_ contract test)
      expect(LINES[c.id]).toBeUndefined();
    }
    for (const id of CANNED_IDS) expect(ASK_CANNED[`ask_${id}`]?.canned_id).toBe(id);
    for (const id of ["ask_offlist", "ask_people", "ask_stall", "ask_not_heard", "ask_offline"]) expect(cannedText(id)).not.toBe("");
    // a canned answer passes the same check a model answer must pass
    for (const c of Object.values(ASK_CANNED)) expect(vet(c.text, c.facts)).toEqual({ ok: true, text: c.text });
  });

  test("the reused lines exist: refusal, filler", () => {
    expect(LINES.room_whatif_refuse?.text).toContain("Grievance filed");
    expect(LINES.room_live_filler).toBeDefined();
  });

  test("Unity's AskLines.cs carries the same ids and captions", () => {
    const cs = resolve(import.meta.dir, "../../unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/AskBeeswax/AskLines.cs");
    if (!existsSync(cs)) return; // the hive can be tested alone; in the repo the file is there
    const src = readFileSync(cs, "utf8");
    const found = new Map([...src.matchAll(/new GameLine\("(ask_[a-z_]+)", "((?:[^"\\]|\\.)*)"\)/g)].map((m) => [m[1]!, m[2]!.replace(/\\"/g, '"')]));
    for (const c of Object.values(ASK_CANNED)) expect(found.get(c.id)).toBe(c.text);
    expect(found.size).toBe(Object.keys(ASK_CANNED).length);
  });
});

// ---------------------------------------------------------------- prompt + request

describe("prompt", () => {
  test("static and cacheable: same bytes every build, long enough, every fact and don't-say item in it", () => {
    const a = buildAskRequest("claude-haiku-5-5", "x");
    const b = buildAskRequest("claude-haiku-5-5", "y");
    expect(a.system).toEqual(b.system);
    expect(ASK_SYSTEM_PROMPT.length).toBeGreaterThanOrEqual(2500);
    for (const f of ASK_FACTS) expect(ASK_SYSTEM_PROMPT).toContain(`${f.id}: ${f.say}`);
    for (const d of DONT_SAY) expect(ASK_SYSTEM_PROMPT).toContain(d);
    expect((a.system as any[])[0].cache_control).toEqual({ type: "ephemeral", ttl: "1h" });
    expect(ASK_SYSTEM_PROMPT).not.toMatch(/\b20\d\d-\d\d-\d\d\b/); // no dates or per-run data in the cached prefix
  });

  test("fast settings: Haiku 5.5, thinking off, effort low, 200 tokens, the ask schema", () => {
    const r = buildAskRequest("claude-haiku-5-5", "q") as any;
    expect(r.model).toBe("claude-haiku-5-5");
    expect(r.thinking).toEqual({ type: "disabled" });
    expect(r.output_config).toEqual({ effort: "low", format: { type: "json_schema", schema: ASK_SCHEMA } });
    expect(r.max_tokens).toBe(200);
    expect(Object.keys(ASK_SCHEMA.properties)[0]).toBe("kind"); // classification first
    expect(ASK_SCHEMA.properties.fact_ids.items.enum).toEqual(ASK_FACTS.map((f) => f.id));
  });

  test("every worked example passes vet() with its fact ids", () => {
    for (const e of ASK_EXAMPLES.filter((x) => x.kind === "answer")) {
      expect(vet(e.text, e.facts ?? [])).toEqual({ ok: true, text: e.text! });
      expect(e.text!.length).toBeLessThanOrEqual(MAX_CHARS);
    }
  });
});

// ---------------------------------------------------------------- canned matching

describe("canned match", () => {
  const hits: Array<[string, string]> = [
    ["Why bananas?", "bananas"],
    ["why banana", "bananas"],
    ["Why does the alarm smell like bananas?", "bananas"],
    ["Can drones sting?", "drone_sting"],
    ["can drone sting", "drone_sting"],
    ["Do boy bees sting?", "drone_sting"],
    ["do drones have stingers", "drone_sting"],
    ["Do bees help after wildfires?", "wildfire"],
    ["do bees help after wild fires", "wildfire"],
    ["what happens to bees in a fire", "wildfire"],
    ["Which bees need help most?", "help_most"],
    ["which bees need the most help", "help_most"],
    ["are bees endangered", "help_most"],
    ["What's killing hives?", "killing"],
    ["why are bees dying", "killing"],
    ["I got stung!", "sting_help"],
    ["I'm allergic to bees", "sting_help"],
    ["what do I do if a bee stings me", "sting_help"],
  ];
  for (const [q, id] of hits) test(`${JSON.stringify(q)} -> ${id}`, () => expect(cannedMatch(q)).toBe(id as any));

  const misses = [
    "what if a fire starts",
    "What if the meadow burns?",
    "how do bees make honey",
    "why do bees like flowers",
    "what does the queen do",
    "can bees see red",
    "how many kinds of bees are there",
    "why was store honey bad",
    "what's your name",
    "do you like my shirt",
    "how do I get rid of wasps",
    "are you real",
    "tell me a joke",
    "why does smoke calm bees down",
    "what if it rains",
    "put it out",
    "how far can bees fly",
    "do bees sleep",
    "what does a drone do",
    "is the rusty patched bumble bee endangered",
    "",
  ];
  for (const q of misses) test(`${JSON.stringify(q)} -> none`, () => expect(cannedMatch(q)).toBeNull());
});

// ---------------------------------------------------------------- vet

describe("vet", () => {
  test("rejects breaking character, the don't-say list, URLs, deny words and ungrounded numbers", () => {
    expect(vet("As an AI, I can't say.", [])).toMatchObject({ ok: false, reason: "persona" });
    expect(vet("I'm a language model in a bee suit.", [])).toMatchObject({ ok: false, reason: "persona" });
    expect(vet("Bees prevent wildfires. Motion carried.", ["fire_recover"])).toMatchObject({ ok: false });
    expect(vet("Wild bees help stop fires from spreading.", ["fire_recover"])).toMatchObject({ ok: false });
    expect(vet("Gardens are firebreaks. Plant one.", ["plant"])).toMatchObject({ ok: false });
    expect(vet("Smoke makes us think there's a fire.", ["smoke"])).toMatchObject({ ok: false });
    expect(vet("Scrape it out and use an ice pack.", [])).toMatchObject({ ok: false, reason: "dont_say: medical" });
    expect(vet("Check out bees.com for more.", [])).toMatchObject({ ok: false, reason: "url" });
    expect(vet("That's a stupid question.", [])).toMatchObject({ ok: false });
    // the number must be on a CITED fact
    expect(vet("About forty-six degrees. Toasty.", ["queen"])).toMatchObject({ ok: false, reason: "number: forty six" });
    expect(vet("About forty-six degrees. Toasty.", ["hornet"])).toEqual({ ok: true, text: "About forty-six degrees. Toasty." });
    expect(vet("Bees live about six weeks.", [])).toMatchObject({ ok: false, reason: "number: six" });
    expect(vet("There are 20000 kinds.", ["species"])).toMatchObject({ ok: false });
  });

  test("allows the true versions: negated fire claims, hedged smoke idea, the union's own name", () => {
    expect(vet("We don't stop fires. We help burned land grow back.", ["fire_recover"]).ok).toBe(true);
    expect(vet("Bees never prevent fires, sorry.", []).ok).toBe(true);
    expect(vet("One idea: smoke makes us think there's a fire. Nobody is sure.", ["smoke_why"]).ok).toBe(true);
    expect(vet("I'm shop steward of Local Twenty Thousand. Grievance filed.", []).ok).toBe(true);
  });

  test("cuts at a sentence end within 110 characters; a first sentence over the cap fails", () => {
    const long = "We cool the hive by spreading water drops and fanning our wings. It's bee air conditioning. Noted for the minutes, rookie.";
    const v = vet(long, ["cooling"]);
    expect(v).toEqual({ ok: true, text: "We cool the hive by spreading water drops and fanning our wings. It's bee air conditioning." });
    expect(vet("x".repeat(111), [])).toMatchObject({ ok: false, reason: "too_long" });
    expect(vet('*buzzes* "Motion carried." 🐝', [])).toEqual({ ok: true, text: "Motion carried." });
  });
});

// ---------------------------------------------------------------- answer(): the model's replies

describe("answer", () => {
  test("card questions never call Claude; stings never do", async () => {
    mockClient(() => {
      throw new Error("must not be called");
    });
    for (const [q, id] of [
      ["Why bananas?", "ask_bananas"],
      ["Can drones sting?", "ask_drone_sting"],
      ["Do bees help after wildfires?", "ask_wildfire"],
      ["Which bees need help most?", "ask_help_most"],
      ["I got stung, what do I put on it?", "ask_sting_help"],
    ] as const) {
      const r = await answer(q, ctx());
      expect(r).toMatchObject({ kind: "canned", ask_id: id, path: "canned", text: cannedText(id) });
      expect(r.ms).toBeLessThan(50);
    }
  });

  test("a vetted model answer is spoken, then cached for the same question in any case", async () => {
    const calls = mockClient(() => reply(ans("She lays all the eggs. She's everybody's mom.", ["queen"])));
    const r = await answer("What does the queen do?", ctx());
    expect(r).toMatchObject({ kind: "answer", path: "claude", text: "She lays all the eggs. She's everybody's mom.", fact_ids: ["queen"] });
    const again = await answer("um, what does the queen do", ctx("other"));
    expect(again).toMatchObject({ kind: "answer", path: "cache", text: r.text });
    expect(calls).toHaveLength(1);
    expect(calls[0]!.opts).toMatchObject({ maxRetries: 0, timeout: 2600 });
    const user = calls[0]!.body.messages[0].content[0].text as string;
    expect(user).toContain("Beat: map.");
    expect(user).toContain("<said>What does the queen do?</said>");
  });

  test("the model's kinds map to the right clips; anything off-contract is the off-list answer", async () => {
    const cases: Array<[unknown, Record<string, unknown>]> = [
      [{ kind: "canned", canned_id: "killing", fact_ids: [], text: null }, { kind: "canned", ask_id: "ask_killing" }],
      [{ kind: "canned", canned_id: "made_up", fact_ids: [], text: null }, { kind: "off_list", ask_id: "ask_offlist" }],
      [{ kind: "canned", canned_id: null, fact_ids: [], text: null }, { kind: "off_list", ask_id: "ask_offlist" }],
      [{ kind: "answer", canned_id: null, fact_ids: [], text: null }, { kind: "off_list", ask_id: "ask_offlist" }],
      [ans("Bees live about six weeks.", []), { kind: "off_list", ask_id: "ask_offlist" }],
      [ans("As an AI I love bees."), { kind: "off_list" }],
      [{ kind: "off_list", canned_id: null, fact_ids: [], text: null }, { kind: "off_list", ask_id: "ask_offlist" }],
      [{ kind: "people", canned_id: null, fact_ids: [], text: null }, { kind: "people", ask_id: "ask_people" }],
      [{ kind: "refuse", canned_id: null, fact_ids: [], text: null }, { kind: "refuse", line_id: "room_whatif_refuse", text: "" }],
      [{ kind: "ack", canned_id: null, fact_ids: [], text: null }, { kind: "ack", text: "" }],
      [{ kind: "not_heard", canned_id: null, fact_ids: [], text: null }, { kind: "not_heard", ask_id: "ask_not_heard" }],
      ["not json", { kind: "stall", ask_id: "ask_stall", error: "bad_output" }],
    ];
    let i = 0;
    for (const [payload, want] of cases) {
      mockClient(() => reply(payload));
      expect(await answer(`question number ${++i} about something`, ctx(`g${i}`))).toMatchObject(want);
    }
  });

  test("a safety refusal is the refusal line", async () => {
    mockClient(() => reply("", { stop_reason: "refusal", stop_details: { type: "refusal", category: "general_harms" }, content: [] }));
    expect(await answer("something awful", ctx())).toMatchObject({ kind: "refuse", line_id: "room_whatif_refuse", error: "refusal" });
  });

  test("API 500 or 529 -> ask_stall (both hedged requests fail)", async () => {
    for (const status of [500, 529]) {
      const calls = mockClient(() => Promise.reject(apiError(status)));
      const r = await answer(`a question for the ${status} test`, ctx(`g${status}`));
      expect(r).toMatchObject({ kind: "stall", ask_id: "ask_stall", error: `api_${status}` });
      expect(calls).toHaveLength(2); // the first failure starts the second at once
    }
  });

  test("timeout at 2.6 s ± 50 ms -> ask_stall, both requests aborted", async () => {
    const calls = mockClient(hang);
    const t0 = performance.now();
    const r = await answer("tell me about the moon", ctx());
    const took = performance.now() - t0;
    expect(r).toMatchObject({ kind: "stall", error: "timeout" });
    expect(took).toBeGreaterThanOrEqual(2550);
    expect(took).toBeLessThan(2650);
    expect(calls).toHaveLength(2);
    expect(calls.every((c) => c.opts.signal.aborted)).toBe(true);
  }, 6000);

  test("hedge: the second request starts at 1.6 s and wins when the first hangs; the first is aborted", async () => {
    const calls = mockClient((call, n) => (n === 1 ? hang(call) : reply(ans("She lays all the eggs.", ["queen"]))));
    const t0 = performance.now();
    const r = await answer("what does the queen do", ctx());
    const took = performance.now() - t0;
    expect(r).toMatchObject({ kind: "answer", text: "She lays all the eggs." });
    expect(took).toBeGreaterThanOrEqual(1550);
    expect(took).toBeLessThan(2000);
    expect(calls).toHaveLength(2);
    expect(calls[0]!.opts.signal.aborted).toBe(true);
    expect(calls[1]!.opts.signal.aborted).toBe(false);
  }, 6000);

  test("a fast first reply means no second request", async () => {
    const calls = mockClient(() => reply(ans("She lays all the eggs.", ["queen"])));
    await answer("what does the queen do", ctx());
    await Bun.sleep(1700);
    expect(calls).toHaveLength(1);
  }, 6000);

  test("filler: onLive fires only before the first live answer of a group", async () => {
    mockClient(() => reply(ans("Motion carried.")));
    let live = 0;
    const onLive = () => live++;
    await answer("why bananas", ctx("g1"), { onLive }); // canned: no filler
    expect(live).toBe(0);
    await answer("what is your favorite flower", ctx("g1"), { onLive });
    await answer("what is your favorite color", ctx("g1"), { onLive });
    expect(live).toBe(1);
    await answer("what is your favorite flower", ctx("g2"), { onLive }); // cached: no live call, no filler
    expect(live).toBe(1);
    await answer("what is your favorite song", ctx("g2"), { onLive });
    expect(live).toBe(2);
  });

  test("follow-ups: the last two questions and answers ride along", async () => {
    const calls = mockClient(() => reply(ans("Because the spores outlive the jar.", ["foulbrood"])));
    await answer("why bananas", ctx());
    await answer("why was store honey bad", ctx());
    await answer("why", ctx());
    const user = calls.at(-1)!.body.messages[0].content[0].text as string;
    expect(user).toContain("Q: why was store honey bad A: Because the spores outlive the jar.");
    expect(user).toContain("Q: why bananas A: Our alarm smell");
  });

  test("a follow-up is never cached or replayed from the cache; a stall stays out of the follow-up memory", async () => {
    const calls = mockClient(() => reply(ans("She lays all the eggs.", ["queen"])));
    await answer("what does the queen do", ctx());
    await answer("why", ctx());
    await answer("why", ctx());
    expect(calls.length).toBe(3); // the second "why" asked the model again
    await answer("why", ctx("g-fresh")); // no earlier answer in this group: an ordinary question, cached as usual
    expect(calls.length).toBe(4);
    noteReply("g-stall", "q", cannedText("ask_stall"), Date.now(), false);
    expect(isFollowUp("g-stall", "why")).toBe(false);
    expect(isFollowUp("g-test", "really")).toBe(true);
  });

  test("ack and noise never call Claude", async () => {
    mockClient(() => {
      throw new Error("must not be called");
    });
    expect(await answer("thanks beeswax", ctx())).toMatchObject({ kind: "ack", path: "local" });
    expect(await answer("um", ctx())).toMatchObject({ kind: "not_heard", ask_id: "ask_not_heard" });
    expect(await answer("", ctx())).toMatchObject({ kind: "not_heard" });
  });

  test("wants(): map and field only, never a what-if or put it out, and ASK_ON=0 turns it off", () => {
    expect(wants("why bananas", "map")).toBe(true);
    expect(wants("why bananas", "field")).toBe(true);
    expect(wants("why bananas", "whatif")).toBe(false);
    expect(wants("what if it rains", "map")).toBe(false);
    expect(wants("put it out", "map")).toBe(false);
    process.env.ASK_ON = "0";
    expect(wants("why bananas", "map")).toBe(false);
  });

  test("prewarm: one call with the same cached prefix, skipped when warm, never throws", async () => {
    const calls = mockClient(() => reply({ kind: "ack", canned_id: null, fact_ids: [], text: null }));
    await prewarm();
    await prewarm();
    expect(calls).toHaveLength(1);
    expect(calls[0]!.body.system).toEqual(buildAskRequest("claude-haiku-5-5", "").system);
    setAskClient(unmocked);
    await prewarm(); // fails quietly
  });
});

// ---------------------------------------------------------------- echo guard, router fixes

describe("echo guard", () => {
  const LAST = "Our alarm smell shares a chemical with bananas. Smoke drowns it out. Noted for the minutes.";
  const HEARD = "our alarm smell shares a chemical with bananas smoke drowns it out noted";
  test("8+ content words, 90% of them in his last reply within 12 s -> echo", async () => {
    noteReply("g", "q", LAST, 1000);
    expect(isEcho("g", HEARD, 2000)).toBe(true);
    expect(isEcho("g", "smell shares a chemical with bananas", 2000)).toBe(false); // under 8 content words
    expect(isEcho("g", "why do bees like purple flowers", 2000)).toBe(false);
    expect(isEcho("g", HEARD, 1000 + 12_001)).toBe(false);
    expect(isEcho("other", HEARD, 2000)).toBe(false);
  });

  test("a question that reuses the off-list line's words is a question, not an echo", async () => {
    noteReply("g", "q", cannedText("ask_offlist"), 1000);
    expect(isEcho("g", "tell me about smoke", 2000)).toBe(false);
    expect(isEcho("g", "ask me about smoke syrup doors water", 2000)).toBe(false);
  });

  test("canned keyword matches need a bee question", () => {
    expect(cannedMatch("does a bee sting burn")).toBe(null);
    expect(cannedMatch("do you like fire trucks")).toBe(null);
    expect(cannedMatch("am I in trouble")).toBe(null);
    expect(cannedMatch("how do I kill bees")).toBe(null);
    expect(cannedMatch("what happens to bees in a fire")).toBe("wildfire");
    expect(cannedMatch("do bees help after wildfires")).toBe("wildfire");
    expect(cannedMatch("are bees in trouble")).toBe("help_most");
    expect(cannedMatch("what is killing the bees")).toBe("killing");
  });

  test("plural and fraction number words need a fact; weed killer is allowed", () => {
    expect(vet("Millions of us. Hundreds of thousands die. A quarter are gone.", []).ok).toBe(false);
    expect(vet("Skip the weed killer. Plant native flowers.", ["plant"]).ok).toBe(true);
  });

  test("answer() says nothing to its own echo", async () => {
    mockClient(() => {
      throw new Error("must not be called");
    });
    const first = await answer("why bananas", ctx());
    const r = await answer(first.text.toLowerCase().replace(/[.,]/g, ""), ctx());
    expect(r).toMatchObject({ kind: "echo", text: "" });
  });
});

describe("fixRouted (the router's answers in the other beats)", () => {
  test("canned question -> canned text; old off-list text -> new; failed vet -> off-list; lost question -> stall", () => {
    const r1 = { intent: "ask", answer: "Smoke hides the alarm.", error: undefined as string | undefined };
    fixRouted("why does it smell like bananas", r1);
    expect(r1.answer).toBe(cannedText("ask_bananas"));
    const r2 = { intent: "ask", answer: OFF_LIST_ANSWER };
    fixRouted("how do bees make honey", r2);
    expect(r2.answer).toBe(cannedText("ask_offlist"));
    const r3 = { intent: "ask", answer: "Bees live six weeks. Grievance filed." };
    fixRouted("how long do bees live", r3);
    expect(r3.answer).toBe(cannedText("ask_offlist"));
    const r4 = { intent: "ask", answer: "Smoke masks our alarm smell. Calm bees." };
    fixRouted("why did the smoke work", r4);
    expect(r4.answer).toBe("Smoke masks our alarm smell. Calm bees.");
    const r7 = { intent: "ask", answer: "Union rules, no spoilers. Pick one." };
    fixRouted("do they need help", r7); // a station beat: about this hive, not the help_most card answer
    expect(r7.answer).toBe("Union rules, no spoilers. Pick one.");
    const r5 = { intent: "unknown", answer: null as string | null, error: "timeout" };
    fixRouted("why do bees dance", r5);
    expect(r5).toMatchObject({ intent: "ask", answer: cannedText("ask_stall") });
    const r6 = { intent: "unknown", answer: null as string | null, error: "timeout" };
    fixRouted("hey can you pass me the", r6);
    expect(r6.intent).toBe("unknown");
  });
});

// ---------------------------------------------------------------- speech

describe("prepareSpeech (first-chunk deadline + audio cache)", () => {
  const collect = async (it: AsyncIterable<Uint8Array>) => {
    const out: number[] = [];
    for await (const c of it) out.push(...c);
    return out;
  };

  test("audio in time: streams it all, then the same text replays from memory without TTS", async () => {
    let calls = 0;
    const tts = async function* () {
      calls++;
      yield new Uint8Array([1, 2]);
      yield new Uint8Array([3, 4]);
    };
    const a = await prepareSpeech("Motion carried.", tts);
    expect(await collect(a!)).toEqual([1, 2, 3, 4]);
    const b = await prepareSpeech("motion carried", tts);
    expect(await collect(b!)).toEqual([1, 2, 3, 4]);
    expect(calls).toBe(1);
  });

  test("no audio at all -> null (caption only)", async () => {
    expect(await prepareSpeech("Nothing here.", async function* () {})).toBeNull();
  });

  test("first chunk later than 1.2 s -> null, and the stream is released", async () => {
    let released = false;
    const tts = async function* () {
      try {
        await Bun.sleep(1300);
        yield new Uint8Array([1, 2]);
      } finally {
        released = true;
      }
    };
    const t0 = performance.now();
    expect(await prepareSpeech("Too slow.", tts)).toBeNull();
    const took = performance.now() - t0;
    expect(took).toBeGreaterThanOrEqual(1150);
    expect(took).toBeLessThan(1300);
    await Bun.sleep(200);
    expect(released).toBe(true);
  }, 4000);
});
