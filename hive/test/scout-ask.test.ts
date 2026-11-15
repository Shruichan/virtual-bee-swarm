// Ask Beeswax (scout-ask.ts): routing, the prompt, Claude streaming (fake client), the progressive mp3 endpoint
// (fake TTS), and barge-in against the real ScoutGame on a fake clock. No network: every API is faked.
import { afterEach, describe, expect, test } from "bun:test";
import { Hono } from "hono";
import {
  WAKE_WINDOW_MS,
  parseWake,
  ASK_FALLBACK,
  AskAudio,
  answerText,
  askContextFor,
  askRequest,
  buildAskMessages,
  clipWords,
  cleanSpoken,
  isEcho,
  looksLikeQuestion,
  MAX_ANSWER_WORDS,
  registerScoutAskRoutes,
  routeUtterance,
  SCOUT_ASK_SYSTEM,
  ScoutAsk,
  SentenceSplitter,
  setAskClient,
  type AnswerGame,
  type AskClient,
  type AskSay,
  type StreamTts,
} from "../src/scout-ask";
import { cardFacts } from "../src/scout-ask-prompt";
import { classifyAnswer, ScoutGame, registerScoutRoutes, type ScoutSay } from "../src/scout";
import { SCOUT_LABELS, SCOUT_STOP_ID, type ScoutEvent, type ScoutLine } from "../src/scout-protocol";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- routing

describe("routing (wake word only)", () => {
  const kind = (text: string, captions: string[] = [], awaitingQuestion = false) => routeUtterance(text, { classify: classifyAnswer, captions, awaitingQuestion }).kind;

  test("wake-word variants: detected anywhere, stripped before Claude", () => {
    const table: [string, string][] = [
      ["Hey Beeswax, why do bees need pebbles?", "why do bees need pebbles?"],
      ["hey bees wax what is foulbrood", "what is foulbrood"],
      ["Hey, bee's wax. How much honey?", "How much honey?"],
      ["Hay beeswax, tell me about honey", "tell me about honey"],
      ["A beeswax, why is it sticky", "why is it sticky"],
      ["hey beezwax how many flowers", "how many flowers"],
      ["Hey bees, can I touch it", "can I touch it"],
      ["Okay Beeswax, what's that", "what's that"],
      ["Hey Bs wax what is a bee hotel", "what is a bee hotel"],
      ["Hey, Bees Whacks! Why the pebbles?", "Why the pebbles?"],
      ["Beeswax, why", "why"],
      ["why is honey sticky, hey beeswax?", "why is honey sticky"],
      ["um so hey beeswax the honey jar", "um so the honey jar"],
    ];
    for (const [said, question] of table) {
      expect([said, parseWake(said)]).toEqual([said, { wake: true, rest: question }]);
      expect([said, routeUtterance(said, { classify: classifyAnswer, captions: [] })]).toEqual([said, { kind: "ask", question }]);
    }
  });

  test("only the name (or the name and fillers) is a wake: ack, then wait for the question", () => {
    for (const said of ["Hey Beeswax", "hey beeswax?", "Hay bees wax.", "okay beeswax um", "Hey Bee's Wax!", "Beeswax?", "hey bees"]) {
      expect([said, kind(said)]).toEqual([said, "wake"]);
    }
  });

  test("without the wake word nothing is answered, even clear questions", () => {
    for (const said of ["why do bees need pebbles", "so by the way, where's the judge view?", "Version that you're showing me currently?", "Like this?", "Hey, can you tell me a little bit more?", "Hey B, hello?", "the bees are cute", "yes", "no thanks"]) {
      expect([said, kind(said)]).toEqual([said, "game"]);
    }
  });

  test("after a bare wake, the next utterance is the question (not thanks / never mind / fillers)", () => {
    expect(routeUtterance("the honey jar", { classify: classifyAnswer, captions: [], awaitingQuestion: true })).toEqual({ kind: "ask", question: "the honey jar" });
    for (const s of ["never mind", "thanks", "um", "okay"]) expect([s, kind(s, [], true)]).toEqual([s, "game"]);
  });

  test("'Hey Beeswax, yes' is still the game's yes", () => {
    expect(routeUtterance("Hey Beeswax, yes please", { classify: classifyAnswer, captions: [] })).toEqual({ kind: "game", text: "yes please" });
  });

  test("Beeswax's own voice is an echo, including him saying his own name (with STT spelling his puns differently)", () => {
    const intro = "Hey class! I'm Beeswax, your unbeelievably charming and handsome bee assistant.";
    expect(kind("Hey class, I'm Beeswax, your unbelievably charming and handsome bee assistant.", [intro])).toBe("echo");
    expect(kind("I'm Beeswax", [intro])).toBe("echo");
    expect(kind("I'm Beeswax your unbelievably", [intro])).toBe("echo");
    expect(kind("Hey Beeswax", [intro])).toBe("wake"); // a real call while his intro plays is not an echo
    const caps = ["Bee fact found! Would you like to learn more?", "This is a bee hotel, a safe home for wild bees like mason bees."];
    expect(kind("would you like to learn more", caps)).toBe("echo");
    expect(kind("a safe home for wild bees like mason", caps)).toBe("echo");
    // the kid's own question that shares words with what he said is not an echo
    expect(kind("Hey Beeswax, what is a bee hotel", caps)).toBe("ask");
    expect(kind("Hey Beeswax, what is a bee hotel", [intro, ...caps])).toBe("ask");
    expect(isEcho("why", caps)).toBe(false);
  });

  test("looksLikeQuestion still reads question shapes (glasses mirror)", () => {
    expect(looksLikeQuestion("why do bees need pebbles")).toBe(true);
    expect(looksLikeQuestion("I like this one")).toBe(false);
  });
});

// ---------------------------------------------------------------- text

describe("spoken text", () => {
  test("SentenceSplitter emits each sentence as soon as it ends", () => {
    const s = new SentenceSplitter();
    expect(s.push("Bees can't")).toEqual([]);
    expect(s.push(" swim! So pebbles")).toEqual(["Bees can't swim!"]);
    expect(s.push(" are landing pads. Go look")).toEqual(["So pebbles are landing pads."]);
    expect(s.end()).toEqual(["Go look"]);
    expect(new SentenceSplitter().push("About 4.5 times. Yes")).toEqual(["About 4.5 times."]);
  });

  test("cleanSpoken drops markup, emoji, stage directions and links; clipWords caps on a sentence end", () => {
    expect(cleanSpoken('*buzzes* "Hi" class 🐝 (laughs) see https://x.org now')).toBe("Hi class see now");
    const long = `${"word ".repeat(30).trim()}. ${"more ".repeat(20).trim()}.`;
    expect(clipWords(long, MAX_ANSWER_WORDS).split(" ").length).toBe(30);
    expect(clipWords("one two three four", 2)).toBe("one two.");
  });
});

// ---------------------------------------------------------------- prompt + Claude

describe("prompt", () => {
  test("the system prompt carries every card's facts and the persona rules, with no per-question data", () => {
    const facts = cardFacts();
    for (const l of SCOUT_LABELS) {
      expect(facts[l].length).toBeGreaterThan(0);
      for (const f of facts[l]) expect(SCOUT_ASK_SYSTEM).toContain(f.text);
    }
    expect(SCOUT_ASK_SYSTEM).toContain("teacher");
    expect(SCOUT_ASK_SYSTEM).toMatch(/thirty-five words/);
    expect(SCOUT_ASK_SYSTEM).not.toContain("<question>");
  });

  test("buildAskMessages: the found card, its facts and sources, and the question as data", () => {
    const ctx = askContextFor("honey_jar", cardFacts(), ["Kid: what is that", "Beeswax: Honey!"]);
    const [m] = buildAskMessages("why <can't> bees eat it", ctx);
    expect(m!.role).toBe("user");
    const text = String(m!.content);
    expect(text).toContain("Store honey");
    expect(text).toContain("foulbrood");
    expect(text).toContain("source: https://");
    expect(text).toContain("Beeswax: Honey!");
    expect(text).toContain("<question>why can't bees eat it</question>");
    expect(String(buildAskMessages("hi", askContextFor(null))[0]!.content)).toContain("hasn't found a card yet");
  });
});

function fakeClient(chunks: string[], seen: unknown[] = []): AskClient {
  return {
    messages: {
      async create(body) {
        seen.push(body);
        return (async function* () {
          yield { type: "message_start", message: {} } as never;
          for (const text of chunks) yield { type: "content_block_delta", index: 0, delta: { type: "text_delta", text } } as never;
          yield { type: "message_delta", delta: { stop_reason: "end_turn" }, usage: {} } as never;
        })();
      },
    },
  };
}

describe("answerText (fake Claude)", () => {
  afterEach(() => setAskClient(null));

  test("streams Haiku with thinking off and the cached persona prompt, returns clean spoken text", async () => {
    const seen: unknown[] = [];
    setAskClient(fakeClient(["Great question, class! *smiles* ", "Bees can't swim. 🐝"], seen));
    const out = await answerText("why pebbles", askContextFor("pebble_dish"));
    expect(out).toBe("Great question, class! Bees can't swim.");
    const body = seen[0] as ReturnType<typeof askRequest>;
    expect(body.model).toBe("claude-haiku-5-5");
    expect(body.stream).toBe(true);
    expect(body.thinking).toEqual({ type: "disabled" });
    expect((body.system as { text: string }[])[0]!.text).toBe(SCOUT_ASK_SYSTEM);
  });

  test("a refusal throws (the live path then says the fallback line)", async () => {
    setAskClient({
      messages: {
        async create() {
          return (async function* () {
            yield { type: "message_delta", delta: { stop_reason: "refusal" }, usage: {} } as never;
          })();
        },
      },
    });
    await expect(answerText("x", askContextFor(null))).rejects.toThrow("refusal");
  });
});

// ---------------------------------------------------------------- the orchestrator + endpoint

/** Text deltas one by one, with an optional pause before each. */
function fakeLlm(deltas: string[], pauseMs = 0) {
  return async function* () {
    for (const d of deltas) {
      if (pauseMs) await sleep(pauseMs);
      yield d;
    }
  };
}

/** One 4000-byte "mp3" chunk per sentence; `gate` holds every chunk after the first until released. */
function fakeTts(opts: { gate?: Promise<void>; sentences?: string[] } = {}): StreamTts {
  return async function* (sentences) {
    let n = 0;
    for await (const s of sentences) {
      opts.sentences?.push(s);
      if (n++ > 0 && opts.gate) await opts.gate;
      yield new Uint8Array(4000).fill(n);
    }
  };
}

function fakeGame(calls: string[]): AnswerGame {
  return {
    state: () => ({ phase: "telling", found: [], skipped: [], offer: null, current: null, started_at: 1 }),
    interrupt: () => void calls.push("interrupt"),
    beginAnswer: () => (calls.push("begin"), true),
    endAnswer: (ms = 0) => void calls.push(`end:${ms}`),
  };
}

describe("ScoutAsk + GET /scout/ask/<id>.mp3", () => {
  test("say goes out at the first audio byte; the mp3 streams while it grows, then serves whole", async () => {
    const calls: string[] = [];
    const says: AskSay[] = [];
    const events: ScoutEvent[] = [];
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const sentences: string[] = [];
    const ask = new ScoutAsk({
      game: fakeGame(calls),
      base: () => "http://hive",
      say: (s) => says.push(s),
      event: (e) => events.push(e),
      llm: fakeLlm(["Great question, class! ", "Bees can't swim. ", "Go look!"]),
      tts: fakeTts({ gate, sentences }),
      mode: "progressive",
    });
    const app = new Hono();
    registerScoutAskRoutes(app, ask);
    ask.event({ type: "found", label: "pebble_dish", score: 1, at: 0, look_ms: 0 });

    const done = ask.ask("why do bees need pebbles");
    while (!says.length) await sleep(1);
    expect(calls.slice(0, 2)).toEqual(["interrupt", "begin"]);
    const say = says[0]!;
    expect(say.line_id).toMatch(/^scout_ask_[a-z0-9]+$/);
    const id = say.line_id.slice("scout_ask_".length);
    expect(say.audio_url).toBe(`http://hive/scout/ask/${id}.mp3`);

    // the glasses fetch while the rest is still being made: the first chunk arrives now
    const res = await app.request(`/scout/ask/${id}.mp3`);
    expect(res.headers.get("content-type")).toBe("audio/mpeg");
    expect(res.headers.get("content-length")).toBeNull();
    const reader = res.body!.getReader();
    const first = await reader.read();
    expect(first.value!.length).toBe(4000);
    release();
    let total = first.value!.length;
    for (let r = await reader.read(); !r.done; r = await reader.read()) total += r.value.length;
    expect(total).toBe(12_000);

    const st = await done;
    expect(sentences).toEqual(["Great question, class!", "Bees can't swim.", "Go look!"]);
    expect(st.answer).toBe("Great question, class! Bees can't swim. Go look!");
    expect(st.label).toBe("pebble_dish");
    expect(st.first_served_ms).not.toBeNull();
    expect(says.length).toBe(1); // one say to the glasses, the full caption only goes to the dashboard
    expect(events.filter((e) => e.type === "say").at(-1)).toMatchObject({ caption: st.answer });
    expect(calls.at(-1)).toMatch(/^end:\d+$/);

    // fetched again once complete: a plain file
    const again = await app.request(`/scout/ask/${id}.mp3`);
    expect(again.headers.get("content-length")).toBe("12000");
    expect((await again.arrayBuffer()).byteLength).toBe(12_000);
    expect((await app.request("/scout/ask/nope.mp3")).status).toBe(404);
    expect((await app.request("/scout/ask/..%2Fx.mp3")).status).toBe(400);
  });

  test("whole mode waits for the complete mp3; sentence mode sends one finished mp3 per sentence", async () => {
    const whole: AskSay[] = [];
    const a = new ScoutAsk({ game: fakeGame([]), base: () => "h", say: (s) => whole.push(s), llm: fakeLlm(["One. ", "Two."]), tts: fakeTts(), mode: "whole" });
    const st = await a.ask("why");
    expect(whole.length).toBe(1);
    expect(whole[0]!.caption).toBe("One. Two.");
    expect(a.audio(st.id)!.done).toBe(true);

    const segs: AskSay[] = [];
    const b = new ScoutAsk({
      game: fakeGame([]),
      base: () => "h",
      say: (s) => segs.push(s),
      llm: fakeLlm(["One. ", "Two."]),
      renderSentence: async (t) => new Uint8Array(t.length * 100),
      mode: "sentence",
    });
    const st2 = await b.ask("why");
    expect(segs.map((s) => s.caption)).toEqual(["One.", "Two."]);
    expect(segs.map((s) => s.line_id)).toEqual([`scout_ask_${st2.id}`, `scout_ask_${st2.id}_2`]);
    expect(b.audio(`${st2.id}_2`)!.bytes).toBe(400);
  });

  test("Claude down: the fallback line is still spoken; TTS down: the caption goes out with no audio", async () => {
    const says: AskSay[] = [];
    const broken = async function* (): AsyncGenerator<string> {
      throw new Error("api_529");
    };
    const a = new ScoutAsk({ game: fakeGame([]), base: () => "h", say: (s) => says.push(s), llm: broken, tts: fakeTts(), mode: "progressive" });
    const st = await a.ask("why");
    expect(st.answer).toBe(ASK_FALLBACK);
    expect(st.error).toContain("api_529");
    expect(says[0]!.audio_url).not.toBeNull();

    const quiet: AskSay[] = [];
    const deadTts: StreamTts = async function* () {
      throw new Error("no key");
    };
    const b = new ScoutAsk({ game: fakeGame([]), base: () => "h", say: (s) => quiet.push(s), llm: fakeLlm(["Hi class."]), tts: deadTts, mode: "progressive" });
    await b.ask("why");
    expect(quiet).toEqual([{ line_id: expect.stringMatching(/^scout_ask_/), caption: "Hi class.", audio_url: null }]);
  });

  test("a new question cuts the answer in progress", async () => {
    const says: AskSay[] = [];
    const a = new ScoutAsk({ game: fakeGame([]), base: () => "h", say: (s) => says.push(s), llm: fakeLlm(["One. ", "Two. ", "Three."], 15), tts: fakeTts(), mode: "progressive" });
    const first = a.ask("why one");
    await sleep(5);
    const second = await a.ask("why two");
    const st1 = await first;
    expect(st1.error).toContain("interrupted");
    expect(second.answer).toBe("One. Two. Three.");
  });
});

// ---------------------------------------------------------------- barge-in against the real game

function lines(): ScoutLine[] {
  const L: ScoutLine[] = [
    { id: "scout_intro_1", kind: "intro", text: "Intro." },
    { id: "scout_intro_2", kind: "intro", text: "Intro two." },
    { id: "scout_chime", kind: "chime", text: "Bee fact found!" },
  ];
  for (const l of SCOUT_LABELS) {
    L.push({ id: `scout_hook_${l}`, kind: "hook", label: l, text: `Hook ${l}. Want more?` });
    L.push({ id: `scout_fact_${l}`, kind: "fact", label: l, text: `Fact ${l}.` });
    L.push({ id: `scout_fact_${l}_2`, kind: "fact", label: l, text: `Second fact ${l}.` });
  }
  return L;
}

describe("barge-in (real ScoutGame, fake clock)", () => {
  test("a question during a fact cuts Beeswax off, blocks detections while answering, and ends with the audio", async () => {
    let clock = 1_000_000;
    const says: ScoutSay[] = [];
    const stops: number[] = [];
    const events: ScoutEvent[] = [];
    let ask: ScoutAsk;
    const game = new ScoutGame({
      lines: lines(),
      now: () => clock,
      say: (s) => says.push(s),
      stop: () => stops.push(clock),
      event: (e) => {
        events.push(e);
        ask?.event(e);
      },
      durationMs: () => 3000,
      audioUrl: (id) => `http://hive/scout/audio/${id}.mp3`,
    });
    const asked: AskSay[] = [];
    ask = new ScoutAsk({
      game,
      now: () => clock,
      base: () => "http://hive",
      say: (s) => asked.push(s),
      llm: fakeLlm(["Bees can't swim. ", "So they land on pebbles."]),
      tts: fakeTts(), // 2 chunks x 4000 bytes = 1000 ms of 64 kbit/s mp3
      mode: "progressive",
    });
    const advance = (ms: number) => {
      for (let left = ms; left > 0; left -= 100) {
        clock += Math.min(100, left);
        game.tick();
      }
    };
    const look = (label: "pebble_dish" | "honey_jar", ms: number) => {
      for (let t = 0; t <= ms; t += 100) {
        game.frame({ at: clock, frame_ms: 30, fps: 10, top: [{ label, score: 0.9 }, { label: "background", score: 0.2 }], source: "t" });
        clock += 100;
        game.tick();
      }
    };

    game.setPractice(true); // a steady look plays the chime + the two fact lines straight away
    look("pebble_dish", 700);
    expect(game.state().phase).toBe("telling");
    expect(says.map((s) => s.line_id)).toContain("scout_fact_pebble_dish");
    advance(1000); // mid-fact

    const text = "Hey Beeswax, why do bees need pebbles";
    expect(ask.route(text, classifyAnswer)).toEqual({ kind: "ask", question: "why do bees need pebbles" });
    expect(ask.route("why do bees need pebbles", classifyAnswer)).toEqual({ kind: "game", text: "why do bees need pebbles" });
    const st = await ask.ask("why do bees need pebbles");
    expect(stops.length).toBe(1); // the glasses were told to cut the ear
    expect(events.some((e) => e.type === "stop")).toBe(true);
    expect(st.label).toBe("pebble_dish");
    expect(asked.length).toBe(1);
    expect(game.state().phase).toBe("answering");

    // another card in view while answering: nothing fires
    const before = says.length;
    look("honey_jar", 800);
    expect(says.length).toBe(before);
    expect(game.state().phase).toBe("answering");

    // 1000 ms of audio + ~700 ms player start were left when the answer finished; 900 ms already passed above
    advance(700);
    expect(game.state().phase).toBe("answering");
    advance(200);
    expect(game.state().phase).toBe("looking");
  });

  test("a bare 'Hey Beeswax' cuts Beeswax off, says the ack, and the next utterance within the window is the question", () => {
    let clock = 5000;
    const calls: string[] = [];
    const says: AskSay[] = [];
    const a = new ScoutAsk({
      game: fakeGame(calls),
      now: () => clock,
      base: () => "h",
      say: (s) => says.push(s),
      llm: fakeLlm([]),
      tts: fakeTts(),
      line: (id) => (id === "scout_wake_ack" ? { caption: "Yes? I'm all antennae!", audio_url: "h/scout/audio/scout_wake_ack.mp3", ms: 1500 } : null),
    });
    expect(a.route("Hey Beeswax", classifyAnswer)).toEqual({ kind: "wake" });
    a.wake();
    expect(calls).toEqual(["interrupt", "begin", `end:${700 + 1500 + WAKE_WINDOW_MS}`]);
    expect(says).toEqual([{ line_id: "scout_wake_ack", caption: "Yes? I'm all antennae!", audio_url: "h/scout/audio/scout_wake_ack.mp3" }]);
    expect(a.route("yes I'm all antennae", classifyAnswer)).toEqual({ kind: "echo" }); // its own ack heard back
    expect(a.route("the honey jar", classifyAnswer)).toEqual({ kind: "ask", question: "the honey jar" });
    clock += 700 + 1500 + WAKE_WINDOW_MS + 1;
    expect(a.route("the honey jar", classifyAnswer)).toEqual({ kind: "game", text: "the honey jar" });

    // no ack line yet: the fallback caption goes out (the glasses speak it)
    const b = new ScoutAsk({ game: fakeGame([]), base: () => "h", say: (s) => says.push(s), llm: fakeLlm([]), tts: fakeTts() });
    b.wake();
    expect(says.at(-1)).toEqual({ line_id: "scout_wake_ack", caption: "Yes? I'm all antennae!", audio_url: null });
  });
});

// ---------------------------------------------------------------- the /scout routes

describe("/scout/utterance routing (registerScoutRoutes with fakes)", () => {
  test("a question is answered by Ask Beeswax (bus say scout_ask_*), a yes still goes to the game", async () => {
    const sent: Record<string, unknown>[] = [];
    const bus = { broadcast: (m: Record<string, unknown>) => void sent.push(m) } as never;
    const app = new Hono();
    let stopped = false;
    const game = registerScoutRoutes(app, {
      bus,
      authed: (c) => c.req.query("token") === "t",
      base: () => "http://hive",
      stopped: () => stopped,
      ask: { llm: fakeLlm(["Hi class. ", "Bees can't swim."]), tts: fakeTts(), mode: "progressive" },
    });
    const post = (path: string, body: unknown) =>
      app.request(`${path}?token=t`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    try {
      const plain = await (await post("/scout/utterance", { text: "why do bees need pebbles", at: Date.now() })).json();
      expect(plain.routed).toBeUndefined(); // no wake word: only shown as heard
      expect(sent.some((m) => m.tts_id === SCOUT_STOP_ID)).toBe(false); // and Beeswax is not cut off
      const r = await (await post("/scout/utterance", { text: "Hey Beeswax, why do bees need pebbles?", at: Date.now() })).json();
      expect(r.routed).toBe("ask");
      for (let i = 0; i < 100 && !sent.some((m) => String(m.tts_id).startsWith("scout_ask_")); i++) await sleep(5);
      const say = sent.find((m) => String(m.tts_id).startsWith("scout_ask_"))!;
      expect(say).toMatchObject({ type: "say", channel: "ear", line_id: null });
      expect(sent.some((m) => m.tts_id === SCOUT_STOP_ID)).toBe(true); // interrupt() cut the ear first
      const url = String(say.audio_url).replace("http://hive", "");
      const mp3 = await app.request(url); // public, no token
      expect(mp3.status).toBe(200);
      expect((await app.request("/scout/ask-stats")).status).toBe(401);
      expect((await app.request("/scout/ask-stats?token=t")).status).toBe(200);

      const y = await (await post("/scout/utterance", { text: "yes" })).json();
      expect(y.routed).toBeUndefined();
      expect(game.state().phase).toBeDefined();
    } finally {
      stopped = true;
    }
  });

  test("off without ElevenLabs (tests strip the key) unless forced", async () => {
    const app = new Hono();
    let stopped = false;
    registerScoutRoutes(app, { bus: { broadcast: () => {} } as never, authed: () => true, base: () => "h", stopped: () => stopped });
    const r = await (await app.request("/scout/utterance", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ text: "hey beeswax why do bees sting" }) })).json();
    expect(r.routed).toBeUndefined();
    stopped = true;
  });
});

test("AskAudio readers each start at byte 0", async () => {
  const a = new AskAudio("x");
  a.push(new Uint8Array([1, 2]));
  const r1 = a.response();
  a.push(new Uint8Array([3]));
  a.finish();
  expect([...new Uint8Array(await r1.arrayBuffer())]).toEqual([1, 2, 3]);
  expect([...new Uint8Array(await a.response().arrayBuffer())]).toEqual([1, 2, 3]);
});
