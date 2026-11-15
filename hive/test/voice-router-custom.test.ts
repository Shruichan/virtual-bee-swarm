// v2.1 router: custom what-ifs (topic "custom" + a safe Orbis prompt, or null = refused), keyword precedence,
// and the ask fact guard. Anthropic is always mocked here (an unmocked call fails the test, never reaches the API).
import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import rawScenes from "../lines/orbis_scenes.json";
import { SCENES, type BeatName } from "../src/protocol";
import {
  CUSTOM_EXAMPLES,
  MAX_ANSWER_WORDS,
  OFF_LIST_ANSWER,
  ROUTER_SCHEMA,
  SCENE_CAMERA,
  SCENE_HIVE,
  SCENE_SETTING,
  SYSTEM_PROMPT,
  VETTED_FACTS,
  cleanAnswer,
  cleanScenePrompt,
  isWhatIfPhrase,
  keywordRoute,
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
    usage: { input_tokens: 60, output_tokens: 90, cache_read_input_tokens: 2500, cache_creation_input_tokens: 0 },
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

const unmocked = {
  beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } },
} as unknown as RouterClient;

const ctx = (beat: BeatName = "whatif", recent: string[] = []) => ({ beat, recent });
const custom = (custom_prompt: unknown) => reply({ intent: "what_if", topic: "custom", custom_prompt, answer: null });

beforeEach(() => {
  setRouterClient(unmocked);
  delete process.env.ROUTER_MODEL;
  delete process.env.DIRECTOR_MODEL;
});
afterEach(() => setRouterClient(unmocked));

// The same rules ai-lines.test.ts applies to the planned scenes in orbis_scenes.json.
function expectScenePrompt(p: string) {
  const sentences = p.split(/(?<=[.!?])\s+/).filter(Boolean);
  expect(sentences.length).toBeGreaterThanOrEqual(1);
  expect(sentences.length).toBeLessThanOrEqual(3);
  expect(p.split(/\s+/).filter(Boolean).length).toBeLessThan(100);
  expect(p.endsWith(SCENE_CAMERA)).toBe(true);
  expect(p.split(SCENE_CAMERA).length).toBe(2); // exactly once
  expect(p).toContain(SCENE_HIVE);
  expect(/\b(no|not|never|without|none|nothing|nobody|cannot)\b|n't\b/i.test(p)).toBe(false);
  expect(/\b(kill|killed|dead|death|dying|die|blood|attack|burned|burning|burns|destroy|hurt|injur)/i.test(p)).toBe(false);
}

describe("scene framing matches hive/lines/orbis_scenes.json", () => {
  const scenes = rawScenes as Record<string, { prompt: string; seed: number; clip: string }>;

  test("every scene has the hive and ends with the camera; the setting sentence is the shared one", () => {
    for (const scene of SCENES) {
      expect({ scene, hive: scenes[scene]!.prompt.includes(SCENE_HIVE) }).toEqual({ scene, hive: true });
      expect({ scene, camera: scenes[scene]!.prompt.endsWith(SCENE_CAMERA) }).toEqual({ scene, camera: true });
    }
    const sharing = SCENES.filter((s) => scenes[s]!.prompt.startsWith(SCENE_SETTING));
    expect(sharing.length).toBeGreaterThanOrEqual(3);
    expect(sharing).toContain("whatif_custom");
  });

  test("whatif_custom: the router's own default scene prompt passes the prompt rules", () => {
    const p = scenes.whatif_custom!.prompt;
    expect(cleanScenePrompt(p)).toBe(p);
    expectScenePrompt(p);
    expect(scenes.whatif_custom!.seed).toBe(scenes.meadow!.seed);
  });
});

describe("cleanScenePrompt", () => {
  test("the worked examples in the system prompt pass unchanged", () => {
    for (const [name, p] of Object.entries(CUSTOM_EXAMPLES)) {
      expect({ name, same: cleanScenePrompt(p) === p }).toEqual({ name, same: true });
      expectScenePrompt(p);
      expect(SYSTEM_PROMPT).toContain(p);
    }
  });

  test("repairs the framing: camera re-added once and last, the setting added when the hive is missing", () => {
    const change = "A rainbow arcs over the meadow while honeybees fly through its colors.";
    expect(cleanScenePrompt(`${SCENE_SETTING} ${change}`)).toBe(`${SCENE_SETTING} ${change} ${SCENE_CAMERA}`);
    expect(cleanScenePrompt(`${SCENE_CAMERA} ${SCENE_SETTING} ${change} Wide shot, static camera.`)).toBe(
      `${SCENE_SETTING} ${change} ${SCENE_CAMERA}`,
    );
    expect(cleanScenePrompt(change)).toBe(`${SCENE_SETTING} ${change} ${SCENE_CAMERA}`);
    expect(cleanScenePrompt(`A rainbow meadow with a hive. ${change}`)).toBe(`${SCENE_SETTING} ${change} ${SCENE_CAMERA}`);
    // quotes, markdown, emoji and a missing final period
    expect(cleanScenePrompt(`"**${SCENE_SETTING}** 🐝 A rainbow arcs over the meadow"`)).toBe(
      `${SCENE_SETTING} A rainbow arcs over the meadow. ${SCENE_CAMERA}`,
    );
    // at most three sentences: setting, change, camera
    const out = cleanScenePrompt(`${SCENE_SETTING} ${change} A second change happens. A third one too.`)!;
    expectScenePrompt(out);
    expect(out).toBe(`${SCENE_SETTING} ${change} ${SCENE_CAMERA}`);
  });

  test("drops what it can't make safe: negations, violence, too long, not a string", () => {
    for (const bad of [
      `${SCENE_SETTING} The hive entrance has no bees at all.`,
      `${SCENE_SETTING} The flowers can't grow anymore.`,
      `${SCENE_SETTING} A bear attacks the hive.`,
      `${SCENE_SETTING} Dead bees lie on the grass.`,
      `${SCENE_SETTING} The meadow is burning.`,
      `${SCENE_SETTING} ${"Honeybees dance over the flowers and ".repeat(12)}around the hive.`,
      `${SCENE_SETTING} ${"Extraordinarily iridescent honeybees ".repeat(14)}hover.`, // < 100 words but > 600 chars
      "",
      "   ",
      null,
      42,
    ]) {
      expect({ bad, out: cleanScenePrompt(bad) }).toEqual({ bad, out: null });
    }
  });
});

describe("custom what-ifs (Claude path)", () => {
  test("schema: custom_prompt is a required string-or-null; topic enum includes custom", () => {
    const s = ROUTER_SCHEMA as any;
    expect(s.properties.custom_prompt.anyOf).toEqual([{ type: "string" }, { type: "null" }]);
    expect(s.required).toContain("custom_prompt");
    expect(s.properties.topic.anyOf[0].enum).toContain("custom");
    expect(Object.keys(s.properties)).toEqual(["intent", "topic", "custom_prompt", "answer"]); // prompt decided before the answer
  });

  test("an off-list what-if goes to Claude and comes back as topic custom with the safe prompt", async () => {
    const calls = mockClient(() => custom(CUSTOM_EXAMPLES.giant));
    const r = await route("What if the bees were giant?", ctx());
    expect(r).toEqual({ intent: "what_if", topic: "custom", answer: null, custom_prompt: CUSTOM_EXAMPLES.giant, ms: r.ms, path: "claude" });
    expectScenePrompt(r.custom_prompt!);
    expect(calls.length).toBe(1);
    const { body } = calls[0]!;
    expect(body.output_config.format).toEqual({ type: "json_schema", schema: ROUTER_SCHEMA });
    expect(body.system).toEqual([{ type: "text", text: SYSTEM_PROMPT, cache_control: { type: "ephemeral", ttl: "1h" } }]);
    expect(body.messages[0].content[0].text).toContain("<said>What if the bees were giant?</said>");
  });

  test("Claude's prompt is cleaned before it leaves the router", async () => {
    mockClient(() => custom(`  "${SCENE_SETTING}\n\nA rainbow arcs over the hive"  `));
    const r = await route("show me a rainbow", ctx("map"));
    expect(r.custom_prompt).toBe(`${SCENE_SETTING} A rainbow arcs over the hive. ${SCENE_CAMERA}`);
    expect(r.error).toBeUndefined();
  });

  test("refusal -> custom_prompt null (Beeswax says the refusal line), not unknown", async () => {
    mockClient(() => custom(null));
    const r = await route("what if a famous pop star visits the hive", ctx());
    expect(r).toEqual({ intent: "what_if", topic: "custom", answer: null, custom_prompt: null, ms: r.ms, path: "claude" });
    mockClient(() => custom(""));
    expect(await route("what if something gross happened", ctx())).toMatchObject({ topic: "custom", custom_prompt: null });
  });

  test("a prompt that breaks the rules is dropped to null and flagged bad_prompt", async () => {
    mockClient(() => custom(`${SCENE_SETTING} A bear attacks the hive.`));
    const r = await route("what if a bear visits", ctx());
    expect(r).toMatchObject({ intent: "what_if", topic: "custom", custom_prompt: null, error: "bad_prompt" });
  });

  test("an API refusal of a what-if is a refused custom what-if; of anything else, unknown", async () => {
    const refused = () => reply("", { stop_reason: "refusal", stop_details: { type: "refusal", category: "general_harms" }, content: [] });
    mockClient(refused);
    expect(await route("what if something awful happened to the hive", ctx())).toMatchObject({
      intent: "what_if",
      topic: "custom",
      answer: null,
      custom_prompt: null,
      path: "claude",
      error: "refusal",
    });
    mockClient(refused);
    expect(await route("tell me something awful", ctx())).toMatchObject({ intent: "unknown", topic: null, error: "refusal" });
    expect((await route("tell me something awful", ctx())).custom_prompt).toBeUndefined();
  });

  test("custom_prompt only rides on topic custom", async () => {
    mockClient(() => reply({ intent: "what_if", topic: "rain", custom_prompt: CUSTOM_EXAMPLES.giant, answer: null }));
    const rain = await route("what if the sky opens up and pours", ctx());
    expect(rain).toMatchObject({ intent: "what_if", topic: "rain" });
    expect("custom_prompt" in rain).toBe(false);
    mockClient(() => reply({ intent: "ask", topic: "custom", custom_prompt: CUSTOM_EXAMPLES.giant, answer: "Grievance filed." }));
    const ask = await route("why are you so loud", ctx());
    expect(ask).toEqual({ intent: "ask", topic: null, answer: "Grievance filed.", ms: ask.ms, path: "claude" });
  });

  test("timeout on a custom what-if is still unknown (no refusal line for a slow network)", async () => {
    const calls = mockClient(() => new Promise(() => {}));
    const r = await route("what if the bees were giant", ctx(), { timeoutMs: 120 });
    expect(r).toMatchObject({ intent: "unknown", topic: null, answer: null, path: "claude", error: "timeout" });
    expect("custom_prompt" in r).toBe(false);
    expect(calls[0]!.opts.signal.aborted).toBe(true);
  });
});

describe("keyword precedence", () => {
  test("planned topics never reach Claude, even when phrased as show me", async () => {
    const calls = mockClient(() => custom(CUSTOM_EXAMPLES.giant));
    for (const [text, topic] of [
      ["What if a fire starts?", "fire"],
      ["show me rain", "rain"],
      ["what if it snowed in July", "winter"],
      ["what happens if we plant sunflowers", "flowers"],
    ] as const) {
      expect(await route(text, ctx("whatif"))).toMatchObject({ intent: "what_if", topic, path: "keyword" });
    }
    expect(calls.length).toBe(0);
  });

  test("the keyword path never returns custom", () => {
    for (const t of ["what if the bees were giant", "show me a unicorn", "what if it turned purple", "make it night"]) {
      for (const beat of ["whatif", "smoker", "map"] as BeatName[]) expect(keywordRoute(t, beat)?.topic ?? null).not.toBe("custom");
    }
  });

  test('"smoker" never means fire', () => {
    for (const t of ["What if I use the smoker?", "show me the smoker", "smoker!", "more smoke please", "what if the smoker smokes a lot", "imagine a huge smoker"]) {
      for (const beat of ["smoker", "whatif", "heat"] as BeatName[]) {
        expect({ t, beat, r: keywordRoute(t, beat)?.topic ?? null }).toEqual({ t, beat, r: null });
      }
    }
  });

  test("isWhatIfPhrase", () => {
    for (const t of ["What if the bees were giant?", "What happens if a cow walks by", "Show me the moon", "imagine a rainbow"]) {
      expect(isWhatIfPhrase(t)).toBe(true);
    }
    for (const t of ["why is the hive light", "okay", "", 7 as unknown as string]) expect(isWhatIfPhrase(t)).toBe(false);
  });
});

describe("ask fact guard", () => {
  // PLAN.md concept v3 "### Facts (all true, each tied to an action)" bullets.
  const plan = readFileSync(new URL("../../PLAN.md", import.meta.url), "utf8");
  const section = plan.split(/^### Facts \(all true[^\n]*\n/m)[1]?.split(/^#{2,3} /m)[0] ?? "";
  const planFacts = section
    .split("\n")
    .filter((l) => l.startsWith("- "))
    .map((l) => l.slice(2).trim());

  test("the cached system prompt carries PLAN.md's vetted fact list verbatim", () => {
    expect(planFacts.length).toBeGreaterThanOrEqual(5);
    expect([...VETTED_FACTS] as string[]).toEqual(planFacts);
    for (const fact of planFacts) expect(SYSTEM_PROMPT).toContain(`- ${fact}`);
  });

  test("the guard instructions and the off-list answer are in the prompt", () => {
    expect(SYSTEM_PROMPT).toContain("Use the vetted fact list below first");
    expect(SYSTEM_PROMPT).toContain("Never use a number, date, name, place or study that is not on the list");
    expect(SYSTEM_PROMPT).toContain("Be generous with common bee knowledge");
    expect(SYSTEM_PROMPT).toContain(`answer exactly: ${OFF_LIST_ANSWER}`);
    expect(SYSTEM_PROMPT).toContain("At most thirty words");
    expect(SYSTEM_PROMPT).toContain("## Vetted fact list (use these first; the only source of numbers)");
    expect(SYSTEM_PROMPT).not.toMatch(/\d{4}-\d{2}-\d{2}/); // nothing per-request in the cached prefix
  });

  test("the off-list answer survives cleanAnswer and fits the word cap", async () => {
    expect(cleanAnswer(OFF_LIST_ANSWER)).toBe(OFF_LIST_ANSWER);
    expect(OFF_LIST_ANSWER.split(" ").length).toBeLessThanOrEqual(MAX_ANSWER_WORDS);
    mockClient(() => reply({ intent: "ask", topic: null, custom_prompt: null, answer: OFF_LIST_ANSWER }));
    expect(await route("how many bees are on earth right now", ctx("meet"))).toMatchObject({ intent: "ask", answer: OFF_LIST_ANSWER, path: "claude" });
  });

  test("a long answer is clipped to 30 words", async () => {
    const long = `${"Smoke masks our alarm signal and makes us gorge on honey. ".repeat(4)}`;
    mockClient(() => reply({ intent: "ask", topic: null, custom_prompt: null, answer: long }));
    const r = await route("why does smoke work", ctx("smoker", ["choice smoker: smoker (correct)"]));
    expect(r.answer!.split(" ").length).toBeLessThanOrEqual(MAX_ANSWER_WORDS);
  });
});
