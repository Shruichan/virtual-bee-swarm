// v2.1 operator ops on the bus: choice.request, fx.force, whatif.force, optional `want` on orbis.request, and the
// score card. stt/router/director/tts are mocked (test/helpers.ts); line, station and scene tables are the real ones.
import { afterEach, describe, expect, test } from "bun:test";
import { LINES } from "../src/lines";
import { SCENES } from "../src/protocol";
import type { Hive } from "../src/server";
import { TestClient, makeHive, setBeat, type MockDeps } from "./helpers";

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
const health = async () => (await fetch(`${hive!.url}/health`)).json() as Promise<any>;

describe("choice.request (Board -> Hive -> Quest)", () => {
  test("with a Quest: forwarded to the Quest only; the outcome waits for Unity's choice.made", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    board.send({ type: "choice.request", station: "feed", choice: "store_honey" });
    expect(await quest.waitFor("choice.request")).toEqual({ type: "choice.request", station: "feed", choice: "store_honey" });
    expect((await board.waitFor("log", (m) => m.text.startsWith("operator choice.request"))).text).toContain("feed: store_honey -> quest");
    await Bun.sleep(40);
    expect(quest.of("say")).toHaveLength(0); // Unity plays the line when it applies the choice
    expect(hive.state.memory.wrong).toEqual([]);
    expect(monitor.of("choice.request")).toHaveLength(0);
    expect(board.of("choice.request")).toHaveLength(0);

    // Unity applied it and reports choice.made as usual: line, window, grievance
    quest.send({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    expect((await quest.waitFor("say")).line_id).toBe("room_feed_store_honey");
    await quest.waitFor("orbis", (m) => m.scene === "feed_sick");
    await board.waitFor("choice.made");
    expect(hive.state.memory.wrong).toEqual([{ station: "feed", choice: "store_honey" }]);
  });

  test("with no Quest: the Hive plays the outcome itself (line, fx, window, memory) and the observers see choice.made", async () => {
    hive = makeHive();
    const board = await connect("board");
    const monitor = await connect("monitor");
    board.send({ type: "choice.request", station: "smoker", choice: "smoker" });
    expect(await board.waitFor("say")).toMatchObject({ channel: "room", line_id: "room_smoker_smoker", caption: LINES.room_smoker_smoker!.text });
    expect(await board.waitFor("fx")).toEqual({ type: "fx", name: "swarm_calm" });
    await board.waitFor("orbis", (m) => m.scene === "smoker_settle");
    expect(await board.waitFor("choice.made")).toEqual({ type: "choice.made", station: "smoker", choice: "smoker", correct: true });
    await monitor.waitFor("choice.made");
    expect((await board.waitFor("log", (m) => m.text.startsWith("choice smoker"))).text).toContain("operator: no Quest");
    expect(hive.state.memory.solved).toEqual(["smoker"]);

    board.send({ type: "choice.request", station: "feed", choice: "store_honey" });
    await board.waitFor("orbis", (m) => m.scene === "feed_sick");
    expect(hive.state.memory.wrong).toEqual([{ station: "feed", choice: "store_honey" }]);
    expect((await health()).grievances).toBe(1);
  });

  test("validated: board only, a known station and a choice of that station", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    quest.send({ type: "choice.request", station: "feed", choice: "sugar_syrup" });
    expect((await quest.waitFor("error")).message).toContain("only accepted from the board");
    board.send({ type: "choice.request", station: "kitchen", choice: "smoker" });
    expect((await board.waitFor("error")).message).toContain("unknown station kitchen");
    board.send({ type: "choice.request", station: "feed", choice: "smoker" });
    await board.waitFor("error", (m) => m.message.includes("unknown choice smoker for feed"));
    await Bun.sleep(30);
    expect(quest.of("choice.request")).toHaveLength(0);
  });
});

describe("fx.force (Board -> Hive -> Quest)", () => {
  test("goes to the Quest with a copy for board/monitor; unknown fx and other senders are refused", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    board.send({ type: "fx.force", name: "embers" });
    expect(await quest.waitFor("fx")).toEqual({ type: "fx", name: "embers" });
    await monitor.waitFor("fx", (m) => m.name === "embers");
    expect((await board.waitFor("log", (m) => m.text.startsWith("operator fx"))).text).toBe("operator fx embers (path operator)");
    board.send({ type: "fx.force", name: "fireworks" });
    expect((await board.waitFor("error")).message).toContain("unknown fx fireworks");
    quest.send({ type: "fx.force", name: "embers_stop" });
    expect((await quest.waitFor("error")).message).toContain("only accepted from the board");
    await Bun.sleep(30);
    expect(quest.of("fx").map((m) => m.name)).toEqual(["embers"]);
  });
});

describe("whatif.force (the operator twin of a voice what-if)", () => {
  test("same act() path as the voice: line, window, embers; undo picks out/back/nothing; logged as path operator", async () => {
    hive = makeHive({}, { embersDelayMs: 1500 });
    const quest = await connect("quest");
    const board = await connect("board");
    // beat 0 (pre) is not a voice beat: the operator acts anyway
    board.send({ type: "whatif.force", topic: "fire" });
    expect((await quest.waitFor("say")).line_id).toBe("room_whatif_fire");
    expect(await quest.waitFor("fx")).toEqual({ type: "fx", name: "embers", delay_ms: 1500 });
    expect((await quest.waitFor("orbis", (m) => m.scene === "whatif_fire")).fallback_clip).toBe("whatif_fire.mp4");
    expect((await board.waitFor("log", (m) => m.text.startsWith("operator what-if"))).text).toContain("operator what-if fire (path operator");
    expect(hive.state.memory.whatifs).toEqual(["fire"]);

    board.send({ type: "whatif.force", undo: true });
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_out");
    await quest.waitFor("fx", (m) => m.name === "embers_stop");
    await quest.waitFor("orbis", (m) => m.scene === "fire_out");
    board.send({ type: "whatif.force", undo: true, topic: "rain" }); // undo wins over a topic
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_back");
    await quest.waitFor("orbis", (m) => m.scene === "meadow" && quest.of("orbis").indexOf(m) > 0);
    board.send({ type: "whatif.force", undo: true });
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_nothing");
    expect(board.of("intent")).toHaveLength(0); // operator ops are not voice: no transcript, no intent
    expect(board.of("transcript")).toHaveLength(0);
    expect(hive.deps.routeCalls).toHaveLength(0);
  });

  test("with no Quest the line still reaches the board/monitor (rehearsal); custom needs /board/custom; validated", async () => {
    hive = makeHive();
    const board = await connect("board");
    const glasses = await connect("glasses");
    board.send({ type: "whatif.force", topic: "rain" });
    expect(await board.waitFor("say")).toMatchObject({ channel: "room", line_id: "room_whatif_rain" });
    await board.waitFor("orbis", (m) => m.scene === "whatif_rain");
    board.send({ type: "whatif.force", topic: "custom" });
    expect((await board.waitFor("error")).message).toContain("POST /board/custom");
    board.send({ type: "whatif.force", topic: "volcano" });
    await board.waitFor("error", (m) => m.message.includes("needs undo: true or a topic"));
    board.send({ type: "whatif.force" });
    await board.waitFor("error", (m) => board.of("error").length === 3);
    glasses.send({ type: "whatif.force", topic: "fire" });
    expect((await glasses.waitFor("error")).message).toContain("only accepted from the board");
    await Bun.sleep(30);
    expect(hive.state.memory.whatifs).toEqual(["rain"]);
  });
});

describe("orbis.request: want is optional (v2.1)", () => {
  test("a scene/stage request only switches the scene; a bare want changes the mode; neither, or a bad want, is refused", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    board.send({ type: "orbis.request", scene: "feed_sick" });
    expect(await quest.waitFor("orbis.request")).toEqual({ type: "orbis.request", want: "clips", scene: "feed_sick", stage: SCENES.indexOf("feed_sick") });
    await quest.waitFor("orbis", (m) => m.scene === "feed_sick");
    board.send({ type: "orbis.request", stage: SCENES.indexOf("moon") });
    await quest.waitFor("orbis", (m) => m.scene === "moon");
    board.send({ type: "orbis.request", want: "off" });
    expect(await quest.waitFor("orbis.request", (m) => !m.scene)).toEqual({ type: "orbis.request", want: "off" });
    expect((await health()).orbis.want).toBe("off");
    board.send({ type: "orbis.request", want: "live", scene: "heat_drink" }); // a want next to a scene changes nothing
    await quest.waitFor("orbis", (m) => m.scene === "heat_drink");
    expect((await health()).orbis.want).toBe("off");
    quest.send({ type: "orbis.request", scene: "full_bloom" }); // the Quest may switch scenes too
    await board.waitFor("orbis", (m) => m.scene === "full_bloom");

    board.send({ type: "orbis.request" });
    expect((await board.waitFor("error")).message).toContain("needs want");
    board.send({ type: "orbis.request", want: null, scene: "" });
    await board.waitFor("error", () => board.of("error").length === 2);
    board.send({ type: "orbis.request", want: "maybe", scene: "moon" });
    await board.waitFor("error", (m) => m.message.includes("live|clips|off") && !m.message.includes("needs"));
    expect(hive.state.scene).toBe("full_bloom");
  });
});

describe("score (v2.1)", () => {
  test("escape broadcasts {score} to everyone with the grievances; /health.score; re-entering escape re-sends the frozen card", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    const glasses = await connect("glasses");
    await setBeat(quest, board, "hook", 1);
    await setBeat(quest, board, "smoker", 3); // v3.1: the clock starts at the first station
    hive.state.clock_start = Date.now() - 167_000; // the run took 2:47
    quest.send({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    quest.send({ type: "choice.made", station: "feed", choice: "sugar_syrup", correct: true });
    quest.send({ type: "choice.made", station: "wasps", choice: "bigger_door", correct: false });
    await board.waitFor("choice.made", () => board.of("choice.made").length === 3);
    expect((await health()).grievances).toBe(2);
    expect((await health()).score).toBeNull();

    await setBeat(quest, board, "escape", 9);
    const card = { type: "score", elapsed_s: 167, text: "Hive saved in 2:47", grievances: 2 };
    for (const c of [quest, board, monitor, glasses]) expect(await c.waitFor("score")).toEqual(card);
    const bloom = await quest.waitFor("orbis", (m) => m.scene === "full_bloom");
    expect(idx(quest, "score")).toBeLessThan(quest.msgs.indexOf(bloom)); // the card comes with the escape, before the bloom
    expect((await board.waitFor("log", (m) => m.text.startsWith("score: "))).text).toBe("score: Hive saved in 2:47");
    let h = await health();
    expect(h.score).toEqual({ elapsed_s: 167, text: "Hive saved in 2:47", grievances: 2 });
    expect(h.score_text).toBe("Hive saved in 2:47");

    // the card is frozen: a later wrong pick and a later return to escape don't change it
    await setBeat(quest, board, "t0", 10);
    quest.send({ type: "choice.made", station: "heat", choice: "nothing", correct: false });
    await board.waitFor("choice.made", () => board.of("choice.made").length === 4);
    await setBeat(quest, board, "escape", 9);
    await quest.waitFor("score", () => quest.of("score").length === 2);
    expect(quest.of("score")[1]).toEqual(card);
    expect(board.of("log").filter((m) => m.text.startsWith("score: "))).toHaveLength(1);
    h = await health();
    expect(h.score.grievances).toBe(2);
    expect(h.grievances).toBe(3);

    // a replay from the hook clears the clock and the card (the next smoker starts a new clock); a group reset clears everything
    await setBeat(quest, board, "hook", 1);
    expect((await health()).score).toBeNull();
    expect(hive.state.clock_start).toBeNull();
    board.send({ type: "group.reset" });
    await quest.waitFor("group.reset");
    h = await health();
    expect(h).toMatchObject({ score: null, score_text: null, grievances: 0 });
  });

  test("the first Quest timer replaces the Hive estimate and later reports cannot move the score", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "smoker", 3);
    hive.state.clock_start = Date.now() - 120_000;
    quest.send({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    await board.waitFor("choice.made");
    await setBeat(quest, board, "escape", 9);
    expect((await board.waitFor("score")).elapsed_s).toBeGreaterThan(100);

    quest.send({ type: "choice.made", station: "heat", choice: "nothing", correct: false });
    await board.waitFor("choice.made", () => board.of("choice.made").length === 2);
    quest.send({ type: "beat.set", n: 9, name: "escape", score_seconds: 78 });
    expect(await board.waitFor("score", (m) => m.elapsed_s === 78)).toEqual({
      type: "score", elapsed_s: 78, text: "Hive saved in 1:18", grievances: 1,
    });

    quest.send({ type: "beat.set", n: 10, name: "t0", score_seconds: 200 });
    await board.waitFor("beat.set", (m) => m.n === 10);
    expect((await health()).score).toEqual({ elapsed_s: 78, text: "Hive saved in 1:18", grievances: 1 });

    await setBeat(quest, board, "hook", 1);
    quest.send({ type: "beat.set", n: 11, name: "map", score_seconds: 42 });
    expect(await board.waitFor("score", (m) => m.elapsed_s === 42)).toMatchObject({ text: "Hive saved in 0:42" });
  });

  test.each([["escape", 9], ["t0", 10], ["map", 11], ["field", 12]] as const)(
    "a Quest can recover the authoritative score after reconnecting at %s without a Hive clock",
    async (name, n) => {
      hive = makeHive();
      const quest = await connect("quest");
      const board = await connect("board");
      // Simulate the Hive already seeing this beat but never seeing the station timer start.
      await setBeat(quest, board, name, n);
      expect(hive.state.clock_start).toBeNull();
      quest.send({ type: "beat.set", n, name, score_seconds: 0 });
      expect(await board.waitFor("score")).toEqual({
        type: "score", elapsed_s: 0, text: "Hive saved in 0:00", grievances: 0,
      });
    },
  );

  test("a score on a station beat or an invalid finale score leaves the legacy clock in charge", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    quest.send({ type: "beat.set", n: 3, name: "smoker", score_seconds: 5 });
    await board.waitFor("beat.set", (m) => m.n === 3);
    expect((await health()).score).toBeNull();
    hive.state.clock_start = Date.now() - 60_000;
    quest.send({ type: "beat.set", n: 9, name: "escape", score_seconds: -1 });
    expect((await board.waitFor("score")).elapsed_s).toBeGreaterThan(50);
  });

  test("Board-only rehearsal still measures the Hive clock", async () => {
    hive = makeHive();
    const board = await connect("board");
    board.send({ type: "beat.request", n: 3 });
    await board.waitFor("beat.set", (m) => m.n === 3);
    hive.state.clock_start = Date.now() - 60_000;
    board.send({ type: "beat.request", n: 9, score_seconds: 5 });
    expect((await board.waitFor("score")).elapsed_s).toBeGreaterThan(50);
  });

  test("escape with no running clock sends no score and says why", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "escape", 9);
    expect((await board.waitFor("log", (m) => m.text.startsWith("no score"))).text).toContain("clock never started");
    await Bun.sleep(30);
    expect(quest.of("score")).toHaveLength(0);
    expect((await health()).score).toBeNull();
    // hook and meet don't start the clock (v3.1)
    await setBeat(quest, board, "hook", 1);
    await setBeat(quest, board, "meet", 2);
    expect(hive.state.clock_start).toBeNull();
    await setBeat(quest, board, "escape", 9); // so an intro-only run still has no card
    await board.waitFor("log", () => board.of("log").filter((m) => m.text.startsWith("no score")).length === 2);
    expect(quest.of("score")).toHaveLength(0);
    // the first station beat does, even when a Board jump skipped smoker
    await setBeat(quest, board, "feed", 4);
    expect((await board.waitFor("log", (m) => m.text.startsWith("score clock started"))).text).toBe("score clock started at feed");
    expect(hive.state.clock_start).not.toBeNull();
    hive.state.clock_start = Date.now() - 61_000;
    await setBeat(quest, board, "escape", 9);
    expect(await quest.waitFor("score")).toEqual({ type: "score", elapsed_s: 61, text: "Hive saved in 1:01", grievances: 0 });
  });
});

function idx(c: TestClient, type: string): number {
  return c.msgs.findIndex((m) => m.type === type);
}
