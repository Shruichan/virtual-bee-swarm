// Line alternates (lines.json tags "alt:<base id>"): the fire what-if and "put it out" take turns with their alternates
// within one run ("...Again?!" only makes sense the second time), and start over on the base line at the hook beat and
// on group.reset. Uses the real line table; stt/router/tts are mocked (test/helpers.ts).
import { afterEach, describe, expect, test } from "bun:test";
import { LINES } from "../src/lines";
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

const alts = (id: string) => Object.values(LINES).filter((l) => l.tags?.includes(`alt:${id}`)).map((l) => l.id);

describe("line alternates", () => {
  test("the real table has alternates for the fire and put-it-out lines, and each keeps the fire line's opening", () => {
    expect(alts("room_whatif_fire").length).toBeGreaterThan(0);
    expect(alts("room_whatif_out").length).toBeGreaterThan(0);
    for (const id of alts("room_whatif_fire")) expect(LINES[id]!.text).toStartWith("Don't. Say. That.");
  });

  test("fire and put-it-out rotate through their alternates in one run, then start over at hook and on group.reset", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const fires = ["room_whatif_fire", ...alts("room_whatif_fire")];
    const outs = ["room_whatif_out", ...alts("room_whatif_out")];
    const said: string[] = [];
    const fireThenOut = async () => {
      const n = quest.of("say").length;
      board.send({ type: "whatif.force", topic: "fire" });
      await quest.waitFor("say", () => quest.of("say").length > n);
      board.send({ type: "whatif.force", undo: true });
      await quest.waitFor("say", () => quest.of("say").length > n + 1);
      said.push(...quest.of("say").slice(n).map((m) => m.line_id as string));
    };
    for (let i = 0; i < fires.length + 1; i++) await fireThenOut();
    const expected: string[] = [];
    for (let i = 0; i < fires.length + 1; i++) expected.push(fires[i % fires.length]!, outs[i % outs.length]!);
    expect(said).toEqual(expected);

    // a new run (hook) starts on the base lines again
    await setBeat(quest, board, "hook", 1);
    said.length = 0;
    await fireThenOut();
    expect(said).toEqual(["room_whatif_fire", "room_whatif_out"]);
    await fireThenOut();
    expect(said.slice(2)).toEqual([fires[1]!, outs[1]!]);

    // and so does a new group
    board.send({ type: "group.reset" });
    await board.waitFor("group.reset");
    said.length = 0;
    await fireThenOut();
    expect(said).toEqual(["room_whatif_fire", "room_whatif_out"]);
  });

  test("a topic with no alternates always says its own line", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    expect(alts("room_whatif_rain")).toEqual([]);
    for (let i = 0; i < 2; i++) {
      const n = quest.of("say").length;
      board.send({ type: "whatif.force", topic: "rain" });
      await quest.waitFor("say", () => quest.of("say").length > n); // waitFor returns the first match: read the newest
      expect(quest.of("say").at(-1).line_id).toBe("room_whatif_rain");
    }
  });
});
