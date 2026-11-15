// Claude prompt-cache warm-up (router + director prewarm): at start, again prewarmEveryMs after the last one finished
// (50 min in production; the cache lasts 1 h), and on group.reset when the last one is older than prewarmStaleMs (45 min).
// deps.prewarm is a mock; no network.
import { afterEach, describe, expect, test } from "bun:test";
import type { Hive } from "../src/server";
import { TestClient, makeHive, type MockDeps } from "./helpers";

let hive: (Hive & { deps: MockDeps }) | null = null;
const clients: TestClient[] = [];
afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  await hive?.stop();
  hive = null;
});

function counter(ms = 0) {
  const calls: number[] = [];
  const prewarm = async () => {
    calls.push(Date.now());
    if (ms) await Bun.sleep(ms);
  };
  return { calls, prewarm };
}

async function resetGroup(): Promise<void> {
  const board = await TestClient.connect(hive!, "board");
  clients.push(board);
  board.send({ type: "group.reset" });
  await board.waitFor("group.reset");
  await Bun.sleep(20);
}

describe("prompt-cache warm-up", () => {
  test("warms at start, then again every prewarmEveryMs after the last warm-up finished; stop() ends it", async () => {
    const w = counter(10);
    hive = makeHive({ prewarm: w.prewarm }, { prewarmEveryMs: 50 });
    expect(w.calls).toHaveLength(1); // at start
    await Bun.sleep(240);
    expect(w.calls.length).toBeGreaterThanOrEqual(3);
    for (let i = 1; i < w.calls.length; i++) expect(w.calls[i]! - w.calls[i - 1]!).toBeGreaterThanOrEqual(50 + 10 - 2); // from the END of the last
    const h = await (await fetch(`${hive.url}/health`)).json();
    expect(typeof h.warmed_s_ago).toBe("number");
    await hive.stop();
    const n = w.calls.length;
    await Bun.sleep(120);
    expect(w.calls.length).toBe(n);
    hive = null;
  });

  test("group.reset re-warms only when the last warm-up is stale", async () => {
    const fresh = counter();
    hive = makeHive({ prewarm: fresh.prewarm }, { prewarmEveryMs: 3_600_000, prewarmStaleMs: 3_600_000 });
    await Bun.sleep(10);
    await resetGroup();
    expect(fresh.calls).toHaveLength(1); // only the start one: still warm
    await hive.stop();
    hive = null;

    const stale = counter();
    hive = makeHive({ prewarm: stale.prewarm }, { prewarmEveryMs: 3_600_000, prewarmStaleMs: 5 });
    await Bun.sleep(30);
    await resetGroup();
    expect(stale.calls).toHaveLength(2); // start + the reset (older than prewarmStaleMs)
  });

  test("never warmed (prewarmOnStart false): the first group.reset warms; a reset during a warm-up doesn't start another", async () => {
    const w = counter(80);
    hive = makeHive({ prewarm: w.prewarm }, { prewarmOnStart: false, prewarmEveryMs: 3_600_000 });
    await Bun.sleep(20);
    expect(w.calls).toHaveLength(0);
    await resetGroup(); // warm-up starts (takes 80 ms)
    expect(w.calls).toHaveLength(1);
    await resetGroup(); // still running: joins it
    expect(w.calls).toHaveLength(1);
  });

  test("a failing or hanging prewarm never breaks the Hive, and the timer keeps going", async () => {
    let n = 0;
    hive = makeHive(
      {
        prewarm: async () => {
          n++;
          throw new Error("no credentials");
        },
      },
      { prewarmEveryMs: 30 },
    );
    await Bun.sleep(120);
    expect(n).toBeGreaterThanOrEqual(3);
    expect((await fetch(`${hive.url}/health`)).status).toBe(200);
  });

  test("no prewarm dependency (keywords-only router): nothing scheduled, /health says never warmed", async () => {
    hive = makeHive({}, { prewarmEveryMs: 10 });
    await resetGroup();
    const h = await (await fetch(`${hive.url}/health`)).json();
    expect(h.warmed_s_ago).toBeNull();
  });
});
