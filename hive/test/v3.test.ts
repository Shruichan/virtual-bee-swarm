// Concept v3: station choices, voice in (listen + mic frames -> stt -> router -> act), the future window, score.
// stt/router/director/tts are mocked (test/helpers.ts); the line, station and scene tables are the real ones.
import { afterEach, describe, expect, test } from "bun:test";
import { ACK_LINES, LINES, STATION_TABLE } from "../src/lines";
import { CHOICES, CORRECT_CHOICE, SCENES, STATIONS } from "../src/protocol";
import type { Hive } from "../src/server";
import { TestClient, makeHive, mockRoute, post, setBeat, TOKEN, type MockDeps } from "./helpers";

let hive: (Hive & { deps: MockDeps }) | null = null;
const clients: TestClient[] = [];

async function connect(role: "quest" | "board" | "monitor" | "glasses"): Promise<TestClient> {
  const c = await TestClient.connect(hive!, role);
  clients.push(c);
  return c;
}

afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  await hive?.stop();
  hive = null;
});

const idx = (c: TestClient, pred: (m: any) => boolean) => c.msgs.findIndex(pred);

describe("stations (choice.made)", () => {
  test("correct choice: line, fx and window scene to the Quest; choice rebroadcast to board/monitor; memory", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    quest.send({ type: "choice.made", station: "smoker", choice: "smoker", correct: true });
    expect(await quest.waitFor("say")).toEqual({
      type: "say",
      channel: "room",
      line_id: "room_smoker_smoker",
      tts_id: null,
      audio_url: null,
      caption: LINES.room_smoker_smoker!.text,
    });
    expect(await quest.waitFor("fx")).toEqual({ type: "fx", name: "swarm_calm" });
    expect(await quest.waitFor("orbis", (m) => m.scene === "smoker_settle")).toEqual({
      type: "orbis",
      state: "off",
      stage: SCENES.indexOf("smoker_settle"),
      scene: "smoker_settle",
      fallback_clip: "smoker_settle.mp4",
    });
    expect(idx(quest, (m) => m.type === "say")).toBeLessThan(idx(quest, (m) => m.type === "orbis")); // the line covers the morph
    expect(await board.waitFor("choice.made")).toEqual({ type: "choice.made", station: "smoker", choice: "smoker", correct: true });
    await monitor.waitFor("choice.made");
    expect(quest.of("choice.made")).toHaveLength(0); // not echoed to the Quest
    expect(hive.state.memory.solved).toEqual(["smoker"]);
    expect(hive.state.memory.choices.smoker).toEqual(["smoker"]);
    expect(hive.state.base_scene).toBe("smoker_settle");
  });

  test("wrong choice (store honey): grievance line + sick window; the Hive's answer key wins over the Quest's flag", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    quest.send({ type: "choice.made", station: "feed", choice: "store_honey", correct: true });
    expect((await quest.waitFor("say")).line_id).toBe("room_feed_store_honey");
    await quest.waitFor("orbis", (m) => m.scene === "feed_sick");
    expect((await board.waitFor("choice.made")).correct).toBe(false);
    await board.waitFor("log", (m) => m.text.includes("answer is sugar_syrup"));
    expect(quest.of("fx")).toHaveLength(0); // the feed station has no fx
    expect(hive.state.memory.wrong).toEqual([{ station: "feed", choice: "store_honey" }]);
    expect(hive.state.memory.solved).toEqual([]);

    quest.send({ type: "choice.made", station: "feed", choice: "sugar_syrup", correct: true });
    expect((await quest.waitFor("say", (m) => m.line_id !== "room_feed_store_honey")).line_id).toBe("room_feed_sugar_syrup");
    await quest.waitFor("orbis", (m) => m.scene === "feed_recover");
    expect(hive.state.memory.choices.feed).toEqual(["store_honey", "sugar_syrup"]);
    expect(hive.state.memory.solved).toEqual(["feed"]);
  });

  test("every station choice in the contract plays its line and scene", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    for (const station of STATIONS) {
      for (const choice of CHOICES[station] as readonly string[]) {
        quest.send({ type: "choice.made", station, choice, correct: CORRECT_CHOICE[station] === choice });
        const say = await quest.waitFor("say", (m) => m.line_id === `room_${station}_${choice}`);
        expect(say.caption.length).toBeGreaterThan(0);
        const scene = (STATION_TABLE[station]!.choices as Record<string, { scene: string }>)[choice]!.scene;
        await quest.waitFor("orbis", (m) => m.scene === scene && idx(quest, (x) => x === m) > idx(quest, (x) => x === say));
      }
    }
    expect(hive.state.memory.solved).toEqual([...STATIONS]);
  });

  test("choice.made is validated: quest only, known station and choice; a quick duplicate is ignored", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    board.send({ type: "choice.made", station: "smoker", choice: "smoker", correct: true });
    await board.waitFor("error", (m) => m.message.includes("quest"));
    quest.send({ type: "choice.made", station: "kitchen", choice: "smoker", correct: true });
    await quest.waitFor("error", (m) => m.message.includes("kitchen"));
    quest.send({ type: "choice.made", station: "smoker", choice: "sugar_syrup", correct: true });
    await quest.waitFor("error", (m) => m.message.includes("sugar_syrup"));
    quest.send({ type: "choice.made", station: "wasps", choice: "smaller_door" });
    quest.send({ type: "choice.made", station: "wasps", choice: "smaller_door" });
    await quest.waitFor("say");
    await Bun.sleep(50);
    expect(quest.of("say")).toHaveLength(1);
    expect(hive.state.memory.choices).toEqual({ wasps: ["smaller_door"] });
  });
});

describe("voice (listen.start + MIC_PCM16 + listen.end)", () => {
  test('"what if a fire starts": transcript, intent, the line, fire window, embers after the morph; "put it out": relief + embers_stop', async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    await setBeat(quest, board, "whatif", 8);
    quest.say("what if a fire starts");
    expect(await quest.waitFor("transcript")).toEqual({ type: "transcript", text: "what if a fire starts", final: true, source: "quest", ms: 12, stt: "mock" });
    expect(await board.waitFor("intent")).toEqual({ type: "intent", name: "what_if", topic: "fire", text: "what if a fire starts", path: "keyword", ms: 3 });
    const say = await quest.waitFor("say");
    expect(say.line_id).toBe("room_whatif_fire");
    expect(say.caption).toContain("Don't. Say. That.");
    expect(await quest.waitFor("fx")).toEqual({ type: "fx", name: "embers", delay_ms: 2500 });
    expect((await quest.waitFor("orbis", (m) => m.scene === "whatif_fire")).fallback_clip).toBe("whatif_fire.mp4");
    expect(idx(quest, (m) => m.type === "say")).toBeLessThan(idx(quest, (m) => m.type === "orbis" && m.scene === "whatif_fire"));
    await monitor.waitFor("transcript");
    await monitor.waitFor("intent");
    expect(hive.deps.routeCalls[0]).toEqual({ text: "what if a fire starts", ctx: { beat: "whatif", recent: [] } });
    for (const hop of ["voice.stt", "voice.route", "voice.total"]) {
      expect((await board.waitFor("latency", (m) => m.hop === hop)).source).toBe("hive");
    }

    quest.say("put it out");
    expect((await quest.waitFor("say", (m) => m.line_id !== "room_whatif_fire")).line_id).toBe("room_whatif_out");
    await quest.waitFor("fx", (m) => m.name === "embers_stop");
    await quest.waitFor("orbis", (m) => m.scene === "fire_out");
    expect(hive.deps.routeCalls[1]!.ctx.recent).toEqual(["what if a fire starts"]);

    quest.say("go back");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_back");
    await quest.waitFor("orbis", (m) => m.scene === "meadow");
    quest.say("undo");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_nothing");
    await Bun.sleep(50);
    expect(quest.of("fx").map((m) => m.name)).toEqual(["embers", "embers_stop"]);
    expect(hive.state.memory.whatifs).toEqual(["fire"]);
    expect(hive.state.scene).toBe("meadow");
  });

  test("undo after a non-fire what-if returns to the last station scene", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "heat", 7);
    quest.send({ type: "choice.made", station: "heat", choice: "pebble_dish", correct: true });
    await quest.waitFor("orbis", (m) => m.scene === "heat_drink");
    quest.say("what if it snows all winter");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_winter");
    expect((await quest.waitFor("say", (m) => m.line_id === "room_whatif_winter")).caption).toContain("shiver");
    quest.say("change it back, undo");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_back");
    await quest.waitFor("orbis", (m) => m.scene === "heat_drink" && idx(quest, (x) => x === m) > idx(quest, (x) => x.scene === "whatif_winter"));
    expect(quest.of("fx").map((m) => m.name)).toEqual(["hive_glow_off"]); // no embers outside the fire
  });

  test("ask: live room TTS answer when the voice is up, a canned acknowledgement when it is down", async () => {
    hive = makeHive({ ttsAvailable: () => true });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.say("why do bees make honey?");
    const say = await quest.waitFor("say");
    expect(say).toMatchObject({ channel: "room", line_id: null, caption: "We make honey. It's a whole thing." });
    expect(say.tts_id).toBeString();
    expect((await quest.waitFor("tts.start")).tts_id).toBe(say.tts_id);
    await quest.waitFor("tts.end", (m) => m.tts_id === say.tts_id);
    expect(hive.deps.ttsCalls).toEqual(["We make honey. It's a whole thing."]);
    for (const c of clients.splice(0)) c.close();
    await hive.stop();

    hive = makeHive(); // TTS down
    const quest2 = await connect("quest");
    const board2 = await connect("board");
    await setBeat(quest2, board2, "whatif", 8);
    quest2.say("why do bees make honey?");
    const ack = await quest2.waitFor("say");
    expect(ACK_LINES).toContain(ack.line_id);
    expect(quest2.of("tts.start")).toHaveLength(0);
  });

  test("the router's context has the station choices (in order with what was said); the answer reaches the board", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "feed", 4);
    quest.send({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    await quest.waitFor("say");
    quest.say("why was that bad?");
    await quest.waitFor("say", () => quest.of("say").length > 1);
    expect(hive.deps.routeCalls[0]!.ctx).toEqual({
      beat: "feed",
      recent: ["(event: the player chose store honey at the feed station, a wrong choice)"],
    });
    quest.send({ type: "choice.made", station: "feed", choice: "sugar_syrup", correct: true });
    await quest.waitFor("say", () => quest.of("say").length > 2);
    quest.say("okay");
    await quest.waitFor("say", () => quest.of("say").length > 3);
    expect(hive.deps.routeCalls[1]!.ctx.recent).toEqual([
      "(event: the player chose store honey at the feed station, a wrong choice)",
      "why was that bad?",
      "(event: the player chose sugar syrup at the feed station, the right choice)",
    ]);
    // TTS is down, so Beeswax acks; the operator still sees what Claude answered.
    const log = await board.waitFor("log", (m) => m.text.startsWith('voice quest: "why was that bad?"'));
    expect(log.text).toContain(' answer: "');
  });

  test("unknown: a canned acknowledgement, never the same one twice in a row", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "smoker", 3);
    let last = "";
    for (let i = 0; i < 6; i++) {
      quest.say(`banana number ${i}`);
      await quest.waitFor("say", () => quest.of("say").length > i);
      const say = quest.of("say")[i];
      expect(ACK_LINES).toContain(say.line_id);
      expect(say.line_id).not.toBe(last);
      last = say.line_id;
    }
    expect((await board.waitFor("intent")).name).toBe("unknown");
    expect(hive.state.scene).toBe("meadow");
  });

  test("outside the voice beats the Hive transcribes and routes but does not act", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    quest.say("what if a fire starts"); // beat 0: pre
    await board.waitFor("intent");
    await board.waitFor("log", (m) => m.text.includes("not acting"));
    await Bun.sleep(50);
    expect(quest.of("say")).toHaveLength(0);
    expect(quest.of("fx")).toHaveLength(0);
    expect(hive.state.scene).toBe("meadow");
  });

  test("a cancelled listen drops its audio; stray frames and a lone listen.end are harmless", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.sendMic(new Uint8Array([1, 2, 3, 4])); // no listen open
    quest.send({ type: "listen.end", source: "quest" });
    quest.send({ type: "listen.start", source: "quest", mode: "ptt" });
    quest.sendMic(new TextEncoder().encode("what if a fire starts "));
    quest.send({ type: "listen.end", source: "quest", cancel: true });
    await board.waitFor("log", (m) => m.text.includes("cancelled"));
    await Bun.sleep(80);
    expect(hive.deps.sttCalls).toHaveLength(0);
    expect(board.of("transcript")).toHaveLength(0);
    expect(quest.of("say")).toHaveLength(0);

    quest.say("what if it rains"); // the next listen still works
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_rain");
  });

  test("the per-client buffer is capped; listens are per client; only quest/glasses may listen", async () => {
    hive = makeHive({}, { listenMaxBytes: 64 });
    const quest = await connect("quest");
    const glasses = await connect("glasses");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.send({ type: "listen.start", source: "quest", mode: "vad" });
    glasses.send({ type: "listen.start", source: "glasses", mode: "ptt" });
    for (let i = 0; i < 10; i++) quest.sendMic(new Uint8Array(20).fill(65)); // 200 bytes of "A"
    glasses.sendMic(new TextEncoder().encode("what if it rains"));
    quest.send({ type: "listen.end", source: "quest" });
    glasses.send({ type: "listen.end", source: "glasses" });
    await board.waitFor("log", (m) => m.text.includes("capped"));
    await board.waitFor("transcript", (m) => m.source === "quest");
    await board.waitFor("transcript", (m) => m.source === "glasses");
    const sizes = hive.deps.sttCalls.map((c) => c.length).sort((a, b) => a - b);
    expect(sizes).toEqual([16, 64]);
    expect(new TextDecoder().decode(hive.deps.sttCalls.find((c) => c.length === 16)!)).toBe("what if it rains");

    board.send({ type: "listen.start", source: "quest", mode: "ptt" });
    await board.waitFor("error", (m) => m.message.includes("listen.start"));
    board.sendMic(new Uint8Array([1, 2]));
    await Bun.sleep(30);
    expect(hive.deps.sttCalls).toHaveLength(2);
  });

  test("the default cap is 15 s of 16 kHz PCM16", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.send({ type: "listen.start", source: "quest", mode: "ptt" });
    const second = new Uint8Array(32000);
    for (let i = 0; i < 17; i++) quest.sendMic(second);
    quest.send({ type: "listen.end", source: "quest" });
    await board.waitFor("transcript");
    expect(hive.deps.sttCalls[0]!.length).toBe(15 * 32000);
  });

  test("a listen with no listen.end is finished by the watchdog; a client that drops mid-listen is forgotten", async () => {
    hive = makeHive({}, { listenWatchdogMs: 120 });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.send({ type: "listen.start", source: "quest", mode: "vad" });
    quest.sendMic(new TextEncoder().encode("what if it rains"));
    await quest.waitFor("transcript", (m) => m.text === "what if it rains", 1500);

    const quest2 = await connect("quest");
    quest2.send({ type: "listen.start", source: "quest", mode: "ptt" });
    quest2.sendMic(new TextEncoder().encode("what if a fire starts "));
    await Bun.sleep(20);
    quest2.close();
    await Bun.sleep(250);
    expect(hive.deps.sttCalls).toHaveLength(1);
    expect((await (await fetch(`${hive.url}/health`)).json()).listening).toBe(0);
  });

  test("speech-to-text failures and a hung router never break the pipeline", async () => {
    hive = makeHive(
      {
        transcribe: async (pcm) => {
          const t = new TextDecoder().decode(pcm).trim();
          if (t === "boom") throw new Error("stt exploded");
          if (t === "hang") return new Promise(() => {});
          return { text: t, ms: 5, provider: "mock" };
        },
        route: (text) => (text.includes("slow") ? new Promise(() => {}) : Promise.resolve(mockRoute(text))),
      },
      { sttTimeoutMs: 50, routeTimeoutMs: 50 },
    );
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.say("boom");
    expect((await board.waitFor("transcript")).text).toBe("");
    quest.say("hang");
    await board.waitFor("transcript", () => board.of("transcript").length > 1, 3000);
    await board.waitFor("log", (m) => m.text.includes("heard nothing") && m.text.includes("timed out"));
    expect(board.of("intent")).toHaveLength(0);
    // the router hangs: the Hive's keyword fallback still catches the fire
    quest.say("slow fire please");
    expect(await board.waitFor("intent", () => true, 3000)).toMatchObject({ name: "what_if", topic: "fire", path: "keyword" });
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_fire");
  });

  test("glasses voice over the bus with no Quest: the reply goes to the ear (an mp3 when TTS is up)", async () => {
    hive = makeHive({ ttsAvailable: () => true });
    const glasses = await connect("glasses");
    const board = await connect("board");
    board.send({ type: "beat.request", n: 8 }); // no Quest: the Hive applies it
    await board.waitFor("beat.set");
    glasses.say("what if it rains", "glasses");
    const say = await glasses.waitFor("say");
    expect(say.channel).toBe("ear");
    expect(say.caption).toBe(LINES.room_whatif_rain!.text);
    expect(say.audio_url).toMatch(/\/audio\/[0-9a-f-]+\.mp3$/);
    await board.waitFor("say", (m) => m.channel === "ear");
    expect((await board.waitFor("orbis", (m) => m.scene === "whatif_rain")).fallback_clip).toBe("whatif_rain.mp4");
  });
});

describe("glasses utterance (POST /glasses/utterance)", () => {
  test("with the Quest connected Beeswax answers in the room and the ear stays quiet", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    const r = await post(hive, "/glasses/utterance", { text: "  what if it rains ", at: Date.now() - 300 });
    expect(r.status).toBe(200);
    expect(r.json).toEqual({ intent: "what_if", topic: "rain", line_id: null, audio_url: null, caption: "" });
    expect((await quest.waitFor("say")).line_id).toBe("room_whatif_rain");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_rain");
    const t = await board.waitFor("transcript");
    expect(t).toMatchObject({ text: "what if it rains", source: "glasses", stt: "glasses" });
    expect(t.ms).toBeGreaterThanOrEqual(300);
    await board.waitFor("latency", (m) => m.hop === "utterance.total");
  });

  test("without a Quest the ear gets the reply; outside voice beats it gets 'not now'; bad input is a 400", async () => {
    hive = makeHive();
    const board = await connect("board");
    let r = await post(hive, "/glasses/utterance", { text: "what if a fire starts", at: Date.now() });
    expect(r.json).toEqual({ intent: "what_if", topic: "fire", line_id: "ear_not_now", audio_url: null, caption: "Not now, I'm monologuing." });
    board.send({ type: "beat.request", n: 12 }); // field: glasses only, no window changes
    await board.waitFor("beat.set");
    r = await post(hive, "/glasses/utterance", { text: "banana", at: Date.now() });
    expect(r.json.intent).toBe("unknown");
    expect(r.json.audio_url).toBeNull();
    expect(Object.values(LINES).some((l) => l.text === r.json.caption && l.tags?.includes("ack"))).toBe(true);
    r = await post(hive, "/glasses/utterance", { text: "what if a fire starts", at: "yesterday" });
    expect(r.json).toMatchObject({ intent: "what_if", topic: "fire", line_id: null, caption: LINES.room_whatif_fire!.text });
    expect(hive.state.scene).toBe("meadow"); // no window in the field
    expect((await post(hive, "/glasses/utterance", { text: "   " })).status).toBe(400);
    expect((await post(hive, "/glasses/utterance", "{nope")).status).toBe(400);
    expect((await post(hive, "/glasses/utterance", { text: "hi" }, "wrong")).status).toBe(401);
  });
});

describe("glasses ask without a Quest", () => {
  test("TTS down: the ear gets Claude's answer as a caption (Mentra reads it), not a canned ack", async () => {
    hive = makeHive();
    const board = await connect("board");
    board.send({ type: "beat.request", n: 8 }); // no Quest: the Hive is the clock
    await board.waitFor("beat.set");
    const r = await post(hive, "/glasses/utterance", { text: "why do bees make honey?", at: Date.now() });
    expect(r.json).toEqual({ intent: "ask", topic: null, line_id: null, audio_url: null, caption: "We make honey. It's a whole thing." });
  });
});

describe("window, beats and score", () => {
  test("operator twin of the fire: say.force + orbis.request{scene}; the Hive adds embers and embers_stop", async () => {
    hive = makeHive({}, { embersDelayMs: 2000 });
    const quest = await connect("quest");
    const board = await connect("board");
    board.send({ type: "say.force", line_id: "room_whatif_fire", channel: "room" });
    board.send({ type: "orbis.request", want: "clips", scene: "whatif_fire" });
    expect((await quest.waitFor("say")).line_id).toBe("room_whatif_fire");
    expect(await quest.waitFor("fx")).toEqual({ type: "fx", name: "embers", delay_ms: 2000 });
    expect(await quest.waitFor("orbis.request")).toEqual({ type: "orbis.request", want: "clips", scene: "whatif_fire", stage: SCENES.indexOf("whatif_fire") });
    await quest.waitFor("orbis", (m) => m.scene === "whatif_fire");
    board.send({ type: "orbis.request", want: "clips", scene: "whatif_fire" }); // same scene again: no second embers
    board.send({ type: "orbis.request", want: "clips", scene: "fire_out" });
    await quest.waitFor("fx", (m) => m.name === "embers_stop");
    await quest.waitFor("orbis", (m) => m.scene === "fire_out");
    expect(quest.of("fx").map((m) => m.name)).toEqual(["embers", "embers_stop"]);
    expect(hive.state.base_scene).toBe("meadow");
  });

  test("hook forecast: the Quest's forecast_peaceful then forecast_ravaged reach the window with their clips; the base scene stays the meadow", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "hook", 1);
    await board.waitFor("orbis", (m) => m.scene === "meadow");
    quest.send({ type: "orbis.request", scene: "forecast_peaceful" }); // the window opens on peaceful Paris
    expect(await board.waitFor("orbis", (m) => m.scene === "forecast_peaceful")).toMatchObject({
      fallback_clip: "forecast_peaceful.mp4",
      stage: SCENES.indexOf("forecast_peaceful"),
    });
    quest.send({ type: "orbis.request", scene: "forecast_ravaged" }); // on "angry swarms"
    expect((await board.waitFor("orbis", (m) => m.scene === "forecast_ravaged")).fallback_clip).toBe("forecast_ravaged.mp4");
    expect(hive.state.base_scene).toBe("meadow"); // an undo or a custom what-if never lands back on the forecast
    expect(SCENES.indexOf("moon")).toBe(17); // appended: the older scenes keep their stage numbers
    await setBeat(quest, board, "meet", 2);
    const seen = board.of("orbis").length;
    quest.send({ type: "orbis.request", scene: "meadow" }); // meet: back to the meadow
    await board.waitFor("orbis", (m) => m.scene === "meadow" && board.of("orbis").indexOf(m) >= seen);
    expect(hive.state.scene).toBe("meadow");
  });

  test("the clock starts at smoker, escape blooms, and the window reaches the Moon at liftoff", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    await setBeat(quest, board, "hook", 1);
    expect(hive.state.clock_start).toBeNull(); // v3.1: the intro is off the clock
    await setBeat(quest, board, "meet", 2);
    expect(hive.state.clock_start).toBeNull();
    const before = Date.now();
    await setBeat(quest, board, "smoker", 3);
    expect(hive.state.clock_start).toBeGreaterThanOrEqual(before);
    expect((await board.waitFor("log", (m) => m.text.startsWith("score clock started"))).text).toBe("score clock started at smoker");
    hive.state.clock_start = Date.now() - 167_000; // pretend the run took 2:47
    quest.send({ type: "choice.made", station: "smoker", choice: "smoker", correct: true });
    await quest.waitFor("orbis", (m) => m.scene === "smoker_settle");
    await setBeat(quest, board, "escape", 9);
    expect((await board.waitFor("log", (m) => m.text.startsWith("score: "))).text).toBe("score: Hive saved in 2:47");
    await monitor.waitFor("log", (m) => m.text.startsWith("score: "));
    expect((await quest.waitFor("orbis", (m) => m.scene === "full_bloom")).fallback_clip).toBe("full_bloom.mp4");
    const h = await (await fetch(`${hive.url}/health`)).json();
    expect(h.score).toEqual({ elapsed_s: 167, text: "Hive saved in 2:47", grievances: 0 }); // v2.1: the score card
    expect(h.solved).toEqual(["smoker"]);
    quest.send({ type: "beat.set", n: 9, name: "escape" }); // Unity reconnect re-sends its beat
    await setBeat(quest, board, "t0", 10);
    expect(hive.state.scene).toBe("full_bloom"); // Beeswax is still boarding and the player can pull the launch lever
    quest.send({ type: "orbis.request", scene: "moon" }); // Unity fires this cue at liftoff
    await quest.waitFor("orbis", (m) => m.scene === "moon");
    expect(board.of("log").filter((m) => m.text.startsWith("score: "))).toHaveLength(1);
    await setBeat(quest, board, "map", 11);
    await quest.waitFor("orbis", (m) => m.scene === "full_bloom" && idx(quest, (x) => x === m) > idx(quest, (x) => x.scene === "moon"));
  });

  test("a Board-only rehearsal can still cue the Moon by jumping to t0", async () => {
    hive = makeHive();
    const board = await connect("board");
    board.send({ type: "beat.request", n: 10 });
    expect((await board.waitFor("beat.set", (m) => m.n === 10)).name).toBe("t0");
    await board.waitFor("orbis", (m) => m.scene === "moon");
  });

  test("group.reset clears the v3 memory, stops the embers and puts the window back on the meadow", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.say("what if a fire starts");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_fire");
    quest.send({ type: "listen.start", source: "quest", mode: "ptt" }); // left open
    quest.sendMic(new Uint8Array([1, 2]));
    await Bun.sleep(20);
    board.send({ type: "group.reset" });
    await quest.waitFor("group.reset");
    await quest.waitFor("fx", (m) => m.name === "embers_stop");
    await quest.waitFor("orbis", (m) => m.scene === "meadow");
    expect(hive.state.memory.whatifs).toEqual([]);
    expect(hive.state.scene).toBe("meadow");
    quest.send({ type: "listen.end", source: "quest" }); // its listen was dropped by the reset
    await Bun.sleep(50);
    expect(hive.deps.sttCalls).toHaveLength(1);
  });

  test("/health reports voice in/out, the window and the score; /board and /monitor render the v3 pages", async () => {
    hive = makeHive();
    const h = await (await fetch(`${hive.url}/health`)).json();
    expect(h.stt).toEqual({ elevenlabs: false, whisper: true, provider: "whisper" });
    expect(h.orbis).toEqual({ state: "off", stage: 0, scene: "meadow", want: "clips", relay: null });
    expect(h.score).toBeNull();
    const board = await (await fetch(`${hive.url}/board?token=${TOKEN}`)).text();
    for (const s of ["PUT IT OUT", "room_whatif_out", "sugar_syrup", "whatif_fire", "voice.route"]) expect(board).toContain(s);
    expect(board).not.toContain("__CONFIG__");
    const monitor = await (await fetch(`${hive.url}/monitor?token=${TOKEN}`)).text();
    expect(monitor).toContain("you said");
    expect(monitor).not.toContain("__CONFIG__");
    const stations = await (await fetch(`${hive.url}/stations.json?token=${TOKEN}`)).json();
    expect(stations.feed.choices.store_honey.scene).toBe("feed_sick");
    expect((await fetch(`${hive.url}/stations.json`)).status).toBe(401);
  });
});
