import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { INTENTS, WHATIF_TOPICS, type BeatName } from "../src/protocol";
import {
  ROUTER_SCHEMA,
  SYSTEM_PROMPT,
  cleanAnswer,
  keywordRoute,
  prewarm,
  route,
  setRouterClient,
  type RouterClient,
} from "../src/router";

type Call = { body: any; opts: any };

function reply(payload: unknown, extra: Record<string, unknown> = {}) {
  return {
    id: "msg_test",
    type: "message",
    role: "assistant",
    model: "claude-haiku-5-5",
    stop_reason: "end_turn",
    stop_details: null,
    content: [{ type: "text", text: typeof payload === "string" ? payload : JSON.stringify(payload) }],
    usage: { input_tokens: 60, output_tokens: 20, cache_read_input_tokens: 1500, cache_creation_input_tokens: 0 },
    ...extra,
  };
}

function mockClient(respond: (call: Call) => unknown) {
  const calls: Call[] = [];
  setRouterClient({
    beta: {
      messages: {
        create(body: any, opts: any) {
          const call = { body, opts };
          calls.push(call);
          return Promise.resolve().then(() => respond(call));
        },
      },
    },
  } as unknown as RouterClient);
  return calls;
}

// Fail closed: a test that forgot its mock must never reach the paid API.
const unmocked = {
  beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } },
} as unknown as RouterClient;

const ctx = (beat: BeatName = "whatif", recent: string[] = []) => ({ beat, recent });

beforeEach(() => {
  setRouterClient(unmocked);
  delete process.env.ROUTER_MODEL;
  delete process.env.DIRECTOR_MODEL;
});

afterEach(() => {
  setRouterClient(unmocked);
  delete process.env.ROUTER_MODEL;
  delete process.env.DIRECTOR_MODEL;
});

describe("keyword fast path", () => {
  const cases: Array<[string, BeatName, string | null]> = [
    // [transcript, beat, expected "intent/topic" or null = needs Claude]
    ["What if a fire starts?", "whatif", "what_if/fire"],
    ["what if a fire starts", "heat", "what_if/fire"],
    ["What if a fire breaks out?", "whatif", "what_if/fire"],
    ["What happens if the meadow burns?", "map", "what_if/fire"],
    ["what would happen if it rains", "whatif", "what_if/rain"],
    ["What if we plant clover?", "whatif", "what_if/flowers"],
    ["What happens if the farmer sprays pesticide?", "whatif", "what_if/spray"],
    ["what if winter comes", "whatif", "what_if/winter"],
    ["What'll happen if it snows?", "whatif", "what_if/winter"],
    ["What if it snowed in July?", "feed", "what_if/winter"],
    ["Show me snow on the hive.", "map", "what_if/winter"],
    ["Can you show me a wildfire?", "whatif", "what_if/fire"],
    ["Imagine a wildfire.", "whatif", "what_if/fire"],
    ["Put it out!", "whatif", "undo"],
    ["Quick, put out the fire!", "whatif", "undo"],
    ["Extinguish it.", "heat", "undo"],
    ["undo", "whatif", "undo"],
    ["Go back.", "escape", "undo"],
    ["Okay.", "feed", "ack"],
    ["Thank you, Beeswax.", "meet", "ack"],
    ["got it", "smoker", "ack"],
    ["", "whatif", "unknown"],
    ["  um... uh  ", "whatif", "unknown"],
    // bare topic words count only in the what-if beat, and never as a question
    ["A fire starts!", "whatif", "what_if/fire"],
    ["a fire starts?", "whatif", "what_if/fire"],
    ["Make it snow.", "whatif", "what_if/winter"],
    ["A fire starts!", "smoker", null],
    ["Why do bees hate fire?", "whatif", null],
    // the smoker is station 1's tool, never fire
    ["What if I use the smoker?", "smoker", null],
    ["What if I use the smoker?", "whatif", null],
    ["Smoker!", "whatif", null],
    ["more smoke", "whatif", null],
    // spraying water is a station 1 choice, not pesticide
    ["What if I spray water on them?", "smoker", null],
    ["what if we spray bug spray with water", "whatif", "what_if/spray"],
    // negated, mixed or extinguishing what-ifs need Claude
    ["What if it doesn't rain?", "whatif", null],
    ["What if the rain puts out the fire?", "whatif", null],
    ["what if we pour water on the fire", "whatif", null],
    ["Don't put it out!", "whatif", null],
    ["What if a hornet attacks?", "hornet", null],
    // off-list what-ifs go to Claude, which writes a custom prompt (keywords never return "custom")
    ["What if the bees were giant?", "whatif", null],
    ["What happens if a hot air balloon lands?", "escape", null],
    ["Show me the bees on the Moon.", "whatif", null],
    ["Make the bees giant.", "whatif", null],
    ["Show me how to use the smoker.", "smoker", null],
    // questions go to Claude for an answer
    ["Why does smoke calm the bees?", "smoker", null],
    ["How many bee species are there", "meet", null],
  ];
  for (const [text, beat, want] of cases) {
    test(`${JSON.stringify(text)} @${beat} -> ${want ?? "claude"}`, () => {
      const r = keywordRoute(text, beat);
      if (want === null) {
        expect(r).toBeNull();
        return;
      }
      const [intent, topic] = want.split("/");
      expect(r).toEqual({ intent: intent as any, topic: (topic ?? null) as any, answer: null });
    });
  }

  test("route() takes the keyword path without calling Claude", async () => {
    const calls = mockClient(() => reply({ intent: "unknown", topic: null, answer: null }));
    const r = await route("What if a fire starts?", ctx());
    expect(r).toMatchObject({ intent: "what_if", topic: "fire", answer: null, path: "keyword" });
    expect(r.ms).toBeLessThan(50);
    expect((await route("put it out", ctx())).intent).toBe("undo");
    expect((await route("", ctx())).intent).toBe("unknown");
    expect(calls.length).toBe(0);
  });
});

describe("claude path", () => {
  test("schema uses the protocol enums and is strict", () => {
    const s = ROUTER_SCHEMA as any;
    expect(s.properties.intent.enum).toEqual([...INTENTS]);
    expect(s.properties.topic.anyOf).toEqual([{ type: "string", enum: [...WHATIF_TOPICS] }, { type: "null" }]);
    expect(s.properties.answer.anyOf).toEqual([{ type: "string" }, { type: "null" }]);
    expect(s.properties.custom_prompt.anyOf).toEqual([{ type: "string" }, { type: "null" }]);
    expect([...s.required].sort()).toEqual(["answer", "custom_prompt", "intent", "topic"]);
    expect(s.additionalProperties).toBe(false);
  });

  test("system prompt is cacheable, names every intent/topic, and carries the vetted facts", () => {
    expect(SYSTEM_PROMPT.length / 4).toBeGreaterThan(700); // > 512-token cache minimum with margin
    for (const i of INTENTS) expect(SYSTEM_PROMPT).toContain(`- ${i}:`);
    for (const t of WHATIF_TOPICS) expect(SYSTEM_PROMPT).toContain(`  - ${t}:`);
    for (const fact of ["bananas", "foulbrood", "fewer guards are needed", "~46 °C", "landing spots", "more than half"]) {
      expect(SYSTEM_PROMPT).toContain(fact);
    }
    expect(SYSTEM_PROMPT).toContain("rate hives, not people");
    expect(SYSTEM_PROMPT).not.toMatch(/\d{4}-\d{2}-\d{2}/); // nothing per-request in the cached prefix
  });

  test("request: haiku, thinking off, low effort, 1h cached system, json_schema, beat + recent + transcript", async () => {
    const calls = mockClient(() => reply({ intent: "ask", topic: null, answer: "Smoke hides our alarm signal. Calm bees. Noted for the minutes." }));
    const r = await route("why did the <b>smoke</b> work", ctx("smoker", ["choice smoker: smoker (correct)", "a".repeat(400)]));
    expect(r).toEqual({
      intent: "ask",
      topic: null,
      answer: "Smoke hides our alarm signal. Calm bees. Noted for the minutes.",
      ms: r.ms,
      path: "claude",
    });
    const { body, opts } = calls[0]!;
    expect(body.model).toBe("claude-haiku-5-5");
    expect(body.thinking).toEqual({ type: "disabled" });
    expect(body.output_config).toEqual({ effort: "low", format: { type: "json_schema", schema: ROUTER_SCHEMA } });
    expect(body.system).toEqual([{ type: "text", text: SYSTEM_PROMPT, cache_control: { type: "ephemeral", ttl: "1h" } }]);
    expect(body.tool_choice).toBeUndefined();
    expect(body.tools).toBeUndefined();
    expect(body.temperature).toBeUndefined();
    expect(body.fallbacks).toBeUndefined();
    const text = body.messages[0].content[0].text as string;
    expect(text).toContain("Beat: smoker.");
    expect(text).toContain("(1) choice smoker: smoker (correct)");
    expect(text).toContain(`(2) ${"a".repeat(160)} `.trimEnd());
    expect(text).not.toContain("a".repeat(161));
    expect(text).toContain("<said>why did the bsmoke/b work</said>");
    expect(opts.signal).toBeInstanceOf(AbortSignal);
  });

  test("ROUTER_MODEL beats DIRECTOR_MODEL; sonnet gets between_tools + server-side fallback", async () => {
    process.env.DIRECTOR_MODEL = "claude-haiku-5-5";
    process.env.ROUTER_MODEL = "claude-sonnet-5-5";
    const calls = mockClient(() => reply({ intent: "ack", topic: null, answer: null }));
    await route("sure thing friend whatever you say", ctx());
    expect(calls[0]!.body.model).toBe("claude-sonnet-5-5");
    expect(calls[0]!.body.thinking).toEqual({ type: "between_tools" });
    expect(calls[0]!.body.betas).toEqual(["server-side-fallback-2026-07-01"]);
    delete process.env.ROUTER_MODEL;
    process.env.DIRECTOR_MODEL = "claude-sonnet-5-5";
    await route("sure thing friend whatever you say", ctx());
    expect(calls[1]!.body.model).toBe("claude-sonnet-5-5");
  });

  test("Claude-routed what-if keeps its topic", async () => {
    mockClient(() => reply({ intent: "what_if", topic: "rain", answer: "ignored" }));
    expect(await route("what if the sky opens up and pours", ctx())).toMatchObject({
      intent: "what_if",
      topic: "rain",
      answer: null,
      path: "claude",
    });
  });

  test("field rules: topic only for what_if, answer only for ask (non-empty, <= 25 words)", async () => {
    mockClient(() => reply({ intent: "what_if", topic: null, answer: null }));
    expect(await route("what if aliens land", ctx())).toMatchObject({ intent: "unknown", topic: null });
    mockClient(() => reply({ intent: "ack", topic: "fire", answer: "Sure." }));
    expect(await route("right on", ctx())).toMatchObject({ intent: "ack", topic: null, answer: null });
    mockClient(() => reply({ intent: "ask", topic: null, answer: "   " }));
    expect(await route("why", ctx())).toMatchObject({ intent: "unknown", answer: null });
    const long = Array.from({ length: 40 }, (_, i) => `word${i}`).join(" ");
    mockClient(() => reply({ intent: "ask", topic: "winter", answer: `"${long}"` }));
    const r = await route("tell me everything", ctx());
    expect(r.topic).toBeNull();
    expect(r.answer!.split(" ").length).toBe(25);
    expect(r.answer!.endsWith(".")).toBe(true);
    expect(r.answer).not.toContain('"');
  });

  test("cleanAnswer strips emoji, stage directions and markdown", () => {
    expect(cleanAnswer("*buzzes angrily* Grievance filed! 🐝 **Syrup** only. (sighs)")).toBe("Grievance filed! Syrup only.");
    expect(cleanAnswer(null)).toBe("");
  });
});

describe("fallbacks (never throws)", () => {
  const expectUnknown = (r: Awaited<ReturnType<typeof route>>, error: string) => {
    expect(r).toMatchObject({ intent: "unknown", topic: null, answer: null, path: "claude", error });
  };

  test("timeout -> unknown on time, request aborted", async () => {
    const calls = mockClient(() => new Promise(() => {}));
    const t0 = performance.now();
    const r = await route("tell me about the moon rocket", ctx(), { timeoutMs: 150 });
    expect(performance.now() - t0).toBeLessThan(1000);
    expectUnknown(r, "timeout");
    expect(calls[0]!.opts.signal.aborted).toBe(true);
  });

  test("default timeout is 3500 ms", async () => {
    mockClient(() => new Promise(() => {}));
    const t0 = performance.now();
    const r = await route("tell me about the moon rocket", ctx());
    const took = performance.now() - t0;
    expect(took).toBeGreaterThanOrEqual(3400);
    expect(took).toBeLessThan(4500);
    expectUnknown(r, "timeout");
  }, 8000);

  test("refusal", async () => {
    mockClient(() => reply("", { stop_reason: "refusal", stop_details: { type: "refusal", category: "general_harms" }, content: [] }));
    expectUnknown(await route("something odd", ctx()), "refusal");
  });

  test("garbage, wrong enums and empty output", async () => {
    for (const payload of ["not json {", "[]", JSON.stringify({ intent: "dance", topic: null, answer: null })]) {
      mockClient(() => reply(payload));
      expectUnknown(await route("hmm hello there", ctx()), "bad_output");
    }
    mockClient(() => reply("", { stop_reason: "max_tokens", content: [] }));
    expectUnknown(await route("hmm hello there", ctx()), "bad_output");
  });

  test("client errors, sync and async", async () => {
    mockClient(() => {
      throw new Error("socket hang up");
    });
    expectUnknown(await route("hmm hello there", ctx()), "error");
    setRouterClient({ beta: { messages: { create: () => { throw new TypeError("sync boom"); } } } } as unknown as RouterClient);
    expectUnknown(await route("hmm hello there", ctx()), "error");
  });

  test("bad inputs don't throw", async () => {
    mockClient(() => reply({ intent: "unknown", topic: null, answer: null }));
    expect((await route(undefined as unknown as string, ctx())).intent).toBe("unknown");
    expect((await route(42 as unknown as string, undefined as any)).intent).toBe("unknown");
    expect((await route("tell me a joke", { beat: "nope" as BeatName, recent: "x" as unknown as string[] })).intent).toBe("unknown");
    expect((await route("what if a fire starts", null as any)).topic).toBe("fire");
  });
});

describe("prewarm", () => {
  test("same cached prefix and format, then skips while warm; never throws", async () => {
    const calls = mockClient(() => reply({ intent: "ack", topic: null, answer: null }));
    await prewarm();
    await prewarm();
    expect(calls.length).toBe(1);
    expect(calls[0]!.body.system[0].cache_control).toEqual({ type: "ephemeral", ttl: "1h" });
    expect(calls[0]!.body.output_config.format.type).toBe("json_schema");
    expect(calls[0]!.body.max_tokens).toBeGreaterThan(0);
    mockClient(() => {
      throw new Error("no credentials");
    });
    await expect(prewarm()).resolves.toBeUndefined();
  });
});
