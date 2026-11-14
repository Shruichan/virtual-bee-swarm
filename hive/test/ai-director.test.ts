import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import sharp from "sharp";
import {
  DIRECTOR_SCHEMA,
  FALLBACK_LINE,
  SYSTEM_PROMPT,
  judge,
  prewarm,
  setDirectorClient,
  type DirectorClient,
  type JudgeInput,
} from "../src/director";
import { lineText } from "../src/lines";
import { ITEMS, VERDICTS } from "../src/protocol";

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
    usage: { input_tokens: 40, output_tokens: 30, cache_read_input_tokens: 1100, cache_creation_input_tokens: 0 },
    ...extra,
  };
}

function mockClient(respond: (call: Call) => unknown) {
  const calls: Call[] = [];
  const client = {
    beta: {
      messages: {
        create(body: any, opts: any) {
          const call = { body, opts };
          calls.push(call);
          return Promise.resolve().then(() => respond(call));
        },
      },
    },
  } as unknown as DirectorClient;
  setDirectorClient(client);
  return calls;
}

const ok = (o: Record<string, unknown>) => () => reply({ seen: "a thing", confidence: 0.9, line: null, ...o });
const SMALL_JPEG = new Uint8Array(
  await sharp({ create: { width: 64, height: 48, channels: 3, background: { r: 200, g: 30, b: 40 } } }).jpeg().toBuffer(),
);
const input = (over: Partial<JudgeInput> = {}): JudgeInput => ({
  photo: SMALL_JPEG,
  mime: "image/jpeg",
  beat: "beeline",
  memory: { scanned: [] },
  ...over,
});

// Fail closed: bun loads hive/.env, so a test that forgot its mock must never reach the paid API.
const unmocked = {
  beta: { messages: { create: () => Promise.reject(new Error("unmocked Anthropic call in a test")) } },
} as unknown as DirectorClient;

beforeEach(() => {
  setDirectorClient(unmocked);
  delete process.env.DIRECTOR_MODEL;
});

afterEach(() => {
  setDirectorClient(unmocked);
  delete process.env.DIRECTOR_MODEL;
});

describe("schema + prompt", () => {
  test("schema uses the protocol enums and is strict", () => {
    const s = DIRECTOR_SCHEMA as any;
    expect(s.properties.item.enum).toEqual([...ITEMS]);
    expect(s.properties.verdict.enum).toEqual([...VERDICTS]);
    expect([...s.required].sort()).toEqual(["confidence", "item", "line", "seen", "verdict"]);
    expect(s.additionalProperties).toBe(false);
    expect(s.properties.line.anyOf).toEqual([{ type: "string" }, { type: "null" }]);
  });

  test("system prompt is long enough to cache and names every item and the four facts", () => {
    expect(SYSTEM_PROMPT.length / 4).toBeGreaterThan(700); // > 512-token cache minimum with margin
    for (const item of ITEMS) expect(SYSTEM_PROMPT).toContain(`${item}:`);
    expect(SYSTEM_PROMPT).toContain("forty-six degrees");
    expect(SYSTEM_PROMPT).toContain("more than half");
    expect(SYSTEM_PROMPT).toContain("Never say anything about figs");
  });
});

describe("request shape", () => {
  test("haiku: thinking off, low effort, 1h cached system, json_schema, image first, no fallbacks", async () => {
    const calls = mockClient(ok({ item: "non_plant_object", verdict: "roast", line: "A badge. Motion denied." }));
    await judge(input({ heard: "it's my <b>badge</b>", memory: { scanned: ["red_rose"] } }));
    const { body, opts } = calls[0]!;
    expect(body.model).toBe("claude-haiku-5-5");
    expect(body.thinking).toEqual({ type: "disabled" });
    expect(body.output_config.effort).toBe("low");
    expect(body.output_config.format).toEqual({ type: "json_schema", schema: DIRECTOR_SCHEMA });
    expect(body.system).toEqual([{ type: "text", text: SYSTEM_PROMPT, cache_control: { type: "ephemeral", ttl: "1h" } }]);
    expect(body.fallbacks).toBeUndefined();
    expect(body.betas).toBeUndefined();
    expect(body.temperature).toBeUndefined();
    expect(body.tool_choice).toBeUndefined();
    const [img, txt] = body.messages[0].content;
    expect(img.type).toBe("image");
    expect(img.source.type).toBe("base64");
    expect(txt.type).toBe("text");
    expect(txt.text).toContain("<heard>it's my bbadge/b</heard>");
    expect(txt.text).toContain("Already scanned this round: red_rose.");
    expect(txt.text).toContain("Beat: beeline.");
    expect(opts.signal).toBeInstanceOf(AbortSignal);
  });

  test("sonnet: between_tools thinking + server-side fallback beta", async () => {
    process.env.DIRECTOR_MODEL = "claude-sonnet-5-5";
    const calls = mockClient(ok({ item: "red_rose", verdict: "bad" }));
    const r = await judge(input());
    const { body } = calls[0]!;
    expect(body.model).toBe("claude-sonnet-5-5");
    expect(body.thinking).toEqual({ type: "between_tools" });
    expect(body.betas).toEqual(["server-side-fallback-2026-07-01"]);
    expect(body.fallbacks).toBe("default");
    expect(body.output_config.effort).toBe("low");
    expect(r.model).toBe("claude-haiku-5-5"); // reports the model that actually served (mock says haiku)
  });

  test("opts.model overrides env", async () => {
    process.env.DIRECTOR_MODEL = "claude-sonnet-5-5";
    const calls = mockClient(ok({ item: "red_rose", verdict: "bad" }));
    await judge(input(), { model: "claude-haiku-5-5" });
    expect(calls[0]!.body.model).toBe("claude-haiku-5-5");
  });

  test("big photos are downscaled to <= 1024 px JPEG", async () => {
    const big = await sharp({ create: { width: 2400, height: 1800, channels: 3, background: { r: 120, g: 40, b: 200 } } })
      .png()
      .toBuffer();
    const calls = mockClient(ok({ item: "purple_bee_flower", verdict: "good" }));
    await judge(input({ photo: new Uint8Array(big), mime: "image/png" }));
    const img = calls[0]!.body.messages[0].content[0];
    expect(img.source.media_type).toBe("image/jpeg");
    const meta = await sharp(Buffer.from(img.source.data, "base64")).metadata();
    expect(meta.width).toBe(1024);
    expect(meta.height).toBe(768);
  });

  test("small upright JPEGs are sent as-is (no re-encode)", async () => {
    const calls = mockClient(ok({ item: "red_rose", verdict: "bad" }));
    await judge(input());
    const img = calls[0]!.body.messages[0].content[0];
    expect(img.source.media_type).toBe("image/jpeg");
    expect(img.source.data).toBe(Buffer.from(SMALL_JPEG).toString("base64"));
  });

  test("undecodable photo: raw bytes if the type is allowed, else speech only", async () => {
    const calls = mockClient(ok({ item: "unclear", verdict: "roast" }));
    await judge(input({ photo: new Uint8Array([1, 2, 3]), mime: "image/jpeg" }));
    expect(calls[0]!.body.messages[0].content[0].type).toBe("image");
    await judge(input({ photo: new Uint8Array([1, 2, 3]), mime: "image/heic", heard: "a rose" }));
    expect(calls[1]!.body.messages[0].content.map((b: any) => b.type)).toEqual(["text"]);
  });

  test("speech only (no photo) still asks the model", async () => {
    const calls = mockClient(ok({ item: "purple_bee_flower", verdict: "good" }));
    const r = await judge(input({ photo: undefined, heard: "a purple salvia" }));
    expect(r.fallback).toBe(false);
    const content = calls[0]!.body.messages[0].content;
    expect(content.length).toBe(1);
    expect(content[0].text).toContain("No photo this time");
  });
});

describe("output rules", () => {
  test("off-list item keeps a live line, clipped to 20 words", async () => {
    const long = Array.from({ length: 30 }, (_, i) => `word${i}`).join(" ");
    mockClient(ok({ item: "non_plant_object", verdict: "roast", line: long, seen: "one two three four five six seven eight nine ten", confidence: 3 }));
    const r = await judge(input());
    expect(r.fallback).toBe(false);
    expect(r.item).toBe("non_plant_object");
    expect(r.line!.split(" ").length).toBe(20);
    expect(r.line!.endsWith(".")).toBe(true);
    expect(r.seen.split(" ").length).toBeLessThanOrEqual(8);
    expect(r.confidence).toBe(1);
    expect(r.usage?.cache_read).toBe(1100);
    expect(r.ms).toBeGreaterThanOrEqual(0);
  });

  test("props get their fixed verdict and no live line", async () => {
    mockClient(ok({ item: "red_rose", verdict: "good", line: "Lovely roses!" }));
    const r = await judge(input());
    expect(r.verdict).toBe("bad");
    expect(r.line).toBeNull();
  });

  test("a person only ever gets the friendly deflection", async () => {
    mockClient(ok({ item: "person", verdict: "roast", seen: "tall man in a hat", line: "Nice hat, buddy." }));
    const r = await judge(input());
    expect(r.item).toBe("person");
    expect(r.line).toBe(lineText("room_verdict_person"));
    expect(r.seen).toBe("a person");
  });

  test("off-list item with no line gets a canned line", async () => {
    mockClient(ok({ item: "other_bee_plant", verdict: "good", line: null }));
    const r = await judge(input());
    expect(r.line).toBe(lineText("room_ack_allow"));
  });
});

describe("fallbacks (never throws)", () => {
  const expectFallback = (r: Awaited<ReturnType<typeof judge>>, error: string) => {
    expect(r.fallback).toBe(true);
    expect(r.item).toBe("unclear");
    expect(r.verdict).toBe("roast");
    expect(r.line).toBe(FALLBACK_LINE);
    expect(r.error).toBe(error);
  };

  test("timeout aborts the request and falls back on time", async () => {
    const calls = mockClient(() => new Promise(() => {}));
    const t0 = performance.now();
    const r = await judge(input(), { timeoutMs: 150 });
    expect(performance.now() - t0).toBeLessThan(1000);
    expectFallback(r, "timeout");
    expect(calls[0]!.opts.signal.aborted).toBe(true);
  });

  test("refusal", async () => {
    mockClient(() => reply("", { stop_reason: "refusal", stop_details: { type: "refusal", category: "general_harms" }, content: [] }));
    expectFallback(await judge(input()), "refusal");
  });

  test("garbage, wrong enums, and empty output", async () => {
    mockClient(() => reply("not json at all {"));
    expectFallback(await judge(input()), "bad_output");
    mockClient(ok({ item: "banana", verdict: "good" }));
    expectFallback(await judge(input()), "bad_output");
    mockClient(ok({ item: "red_rose", verdict: "meh" }));
    expectFallback(await judge(input()), "bad_output");
    mockClient(() => reply("[]"));
    expectFallback(await judge(input()), "bad_output");
    mockClient(() => reply("", { stop_reason: "max_tokens", content: [] }));
    expectFallback(await judge(input()), "bad_output");
  });

  test("client errors", async () => {
    mockClient(() => {
      throw new Error("socket hang up");
    });
    expectFallback(await judge(input()), "error");
    setDirectorClient({
      beta: { messages: { create: () => { throw new TypeError("sync boom"); } } },
    } as unknown as DirectorClient);
    expectFallback(await judge(input()), "error");
  });

  test("no photo and no speech: no API call", async () => {
    const calls = mockClient(ok({ item: "red_rose", verdict: "bad" }));
    expectFallback(await judge(input({ photo: undefined, heard: "   " })), "no_input");
    expect(calls.length).toBe(0);
  });

  test("bad input objects don't throw", async () => {
    mockClient(ok({ item: "red_rose", verdict: "bad" }));
    expect((await judge(undefined as unknown as JudgeInput)).fallback).toBe(true);
    expect((await judge({ beat: "roses", memory: null } as unknown as JudgeInput)).fallback).toBe(true);
  });
});

describe("prewarm", () => {
  test("sends the same cached prefix and format, no image, and skips when warm", async () => {
    const calls = mockClient(ok({ item: "unclear", verdict: "roast" }));
    await prewarm();
    expect(calls.length).toBe(1);
    const { body } = calls[0]!;
    expect(body.max_tokens).toBeGreaterThan(0); // max_tokens:0 is rejected with output_config.format
    expect(body.system[0].cache_control).toEqual({ type: "ephemeral", ttl: "1h" });
    expect(body.output_config.format.type).toBe("json_schema");
    expect(body.thinking).toEqual({ type: "disabled" });
    expect(body.messages[0].content.every((b: any) => b.type === "text")).toBe(true);
    await prewarm();
    expect(calls.length).toBe(1);
  });

  test("never throws", async () => {
    mockClient(() => {
      throw new Error("no credentials");
    });
    await expect(prewarm()).resolves.toBeUndefined();
  });
});
