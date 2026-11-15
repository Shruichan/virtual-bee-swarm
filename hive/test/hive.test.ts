import { afterAll, afterEach, beforeAll, describe, expect, test } from "bun:test";
import sharp from "sharp";
import { SCENE_TABLE } from "../src/lines";
import { BINARY_KIND, PROTOCOL_VERSION, SCENES } from "../src/protocol";
import type { Hive } from "../src/server";
import { TestClient, makeHive, post, setBeat, TOKEN, type MockDeps } from "./helpers";

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

// A local "Mentra" photo host: a 2000x1500 JPEG, plus a slow and a broken URL.
let photoServer: ReturnType<typeof Bun.serve>;
let photoUrl = "";
beforeAll(async () => {
  const jpeg = await sharp({ create: { width: 2000, height: 1500, channels: 3, background: { r: 120, g: 60, b: 200 } } })
    .jpeg()
    .toBuffer();
  photoServer = Bun.serve({
    port: 0,
    hostname: "127.0.0.1",
    async fetch(req) {
      const path = new URL(req.url).pathname;
      if (path === "/slow.jpg") await Bun.sleep(3000);
      if (path === "/broken.jpg") return new Response("nope", { status: 403 });
      return new Response(new Uint8Array(jpeg), { headers: { "content-type": "image/jpeg" } });
    },
  });
  photoUrl = `http://127.0.0.1:${photoServer.port}/photo.jpg`;
});
afterAll(() => photoServer.stop(true));

describe("http basics", () => {
  test("GET /health", async () => {
    hive = makeHive();
    await connect("quest");
    const h = await (await fetch(`${hive.url}/health`)).json();
    expect(h.ok).toBe(true);
    expect(h.version).toBe(PROTOCOL_VERSION);
    expect(h.clients).toEqual({ quest: 1, board: 0, monitor: 0, glasses: 0 });
    expect(h.beat).toBe(0);
    expect(typeof h.group_id).toBe("string");
  });

  test("token is enforced on the bus, glasses API, board, and lines", async () => {
    hive = makeHive();
    expect((await fetch(`${hive.url}/bus?role=quest&token=wrong`)).status).toBe(401);
    expect((await fetch(`${hive.url}/bus?role=pilot&token=${TOKEN}`)).status).toBe(400);
    expect((await post(hive, "/glasses/press", { pressed_at: Date.now() }, null)).status).toBe(401);
    expect((await post(hive, "/glasses/press", { pressed_at: Date.now() }, "wrong")).status).toBe(401);
    const viaHeader = await fetch(`${hive.url}/glasses/press`, {
      method: "POST",
      headers: { "x-hive-token": TOKEN, "content-type": "application/json" },
      body: "{}",
    });
    expect(viaHeader.status).toBe(200);
    expect((await fetch(`${hive.url}/board`)).status).toBe(401);
    expect((await fetch(`${hive.url}/board?token=${TOKEN}`)).status).toBe(200);
    expect((await fetch(`${hive.url}/monitor?token=${TOKEN}`)).status).toBe(200);
    const lines = await (await fetch(`${hive.url}/lines.json?token=${TOKEN}`)).json();
    expect(lines.ear_not_now.text).toBe("Not now, I'm monologuing.");
  });

  test("bad JSON never crashes: HTTP 400, bus error reply, socket stays open", async () => {
    hive = makeHive();
    expect((await post(hive, "/glasses/scan", "{nope")).status).toBe(400);
    expect((await post(hive, "/glasses/plea", { text: "" })).status).toBe(400);
    const board = await connect("board");
    board.ws.send("{nope");
    expect((await board.waitFor("error")).message).toBe("bad JSON");
    board.send({ type: "mystery" });
    await board.waitFor("error", (m) => m.message.includes("unhandled"));
    board.send({ type: "hello", role: "board", client: "t", version: 1 });
    await board.waitFor("welcome");
  });
});

describe("bus", () => {
  test("hello -> welcome", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    quest.send({ type: "hello", role: "quest", client: "unity", version: PROTOCOL_VERSION });
    const w = await quest.waitFor("welcome");
    expect(w.group_id).toBe(hive.state.group_id);
    expect(w.beat).toBe(0);
    expect(w.mode).toBe("solo");
    expect(w.host).toBe("test");
    expect(Math.abs(w.server_time - Date.now())).toBeLessThan(2000);
  });

  test("beat.set from the quest is rebroadcast; from the board it is refused", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    quest.send({ type: "beat.set", n: 3, name: "smoker" });
    expect(await board.waitFor("beat.set")).toEqual({ type: "beat.set", n: 3, name: "smoker" });
    await monitor.waitFor("beat.set", (m) => m.n === 3);
    expect(hive.state.beat).toBe(3);
    expect(quest.of("beat.set")).toHaveLength(0); // not echoed to the sender

    board.send({ type: "beat.set", n: 6, name: "hornet" });
    await board.waitFor("error", (m) => m.message.includes("quest"));
    expect(hive.state.beat).toBe(3);
  });

  test("beat.request goes to the quest; with no quest the Hive applies it", async () => {
    hive = makeHive();
    const board = await connect("board");
    board.send({ type: "beat.request", n: 12 });
    expect((await board.waitFor("beat.set")).name).toBe("field");
    expect(hive.state.beat).toBe(12);

    const quest = await connect("quest");
    board.send({ type: "beat.request", next: true });
    expect(await quest.waitFor("beat.request")).toEqual({ type: "beat.request", next: true });
    board.send({ type: "beat.request", n: 3 });
    expect((await quest.waitFor("beat.request", (m) => m.n === 3)).n).toBe(3);
    expect(hive.state.beat).toBe(12); // unchanged until Unity reports beat.set
  });

  test("mode, hum.set and group.reset", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    board.send({ type: "mode", mode: "duo" });
    await quest.waitFor("mode", (m) => m.mode === "duo");
    expect(hive.state.mode).toBe("duo");
    board.send({ type: "hum.set", value: 7 });
    expect((await quest.waitFor("hum.set")).value).toBe(1);

    hive.state.memory.scanned.push("red_rose");
    const old = hive.state.group_id;
    board.send({ type: "group.reset" });
    const r = await quest.waitFor("group.reset");
    expect(r.group_id).not.toBe(old);
    expect(hive.state.group_id).toBe(r.group_id);
    expect(hive.state.memory.scanned).toEqual([]);
  });

  test("swarm.arrived -> ear say on the glasses (once per landing)", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const glasses = await connect("glasses");
    const board = await connect("board");
    quest.send({ type: "swarm.arrived", socket: 2 });
    quest.send({ type: "swarm.arrived", socket: 2 });
    const say = await glasses.waitFor("say");
    expect(say).toEqual({
      type: "say",
      channel: "ear",
      line_id: "ear_swarm_arrived",
      tts_id: null,
      audio_url: null,
      caption: "They're on your flower. Don't panic.",
    });
    await board.waitFor("swarm.arrived");
    await Bun.sleep(50);
    expect(glasses.of("say")).toHaveLength(1);
    expect(quest.of("say")).toHaveLength(0); // ear lines never play on the Quest
  });

  test("say.force routes room lines to the Quest and ear lines to the glasses", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const glasses = await connect("glasses");
    const board = await connect("board");
    board.send({ type: "say.force", line_id: "room_verdict_red_rose", channel: "room" });
    board.send({ type: "say.force", line_id: "ear_not_now", channel: "ear" });
    expect((await quest.waitFor("say")).line_id).toBe("room_verdict_red_rose");
    expect((await glasses.waitFor("say")).line_id).toBe("ear_not_now");
    await Bun.sleep(30);
    expect(quest.of("say")).toHaveLength(1);
    expect(glasses.of("say")).toHaveLength(1);
  });

  test("hornet.cooked records the hornet and cues the ear aside once", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const glasses = await connect("glasses");
    quest.send({ type: "hornet.cooked", overshoot: true });
    expect((await glasses.waitFor("say")).line_id).toBe("ear_aside_hornet");
    expect(hive.state.memory.hornet_overshoot).toBe(true);
    expect(hive.state.memory.hornet_done).toBe(true);
    quest.send({ type: "hornet.cooked", overshoot: false });
    await Bun.sleep(50);
    expect(glasses.of("say")).toHaveLength(1);
  });

  test("latency and log go to board and monitor", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    quest.send({ type: "latency", hop: "beat.machine", ms: 12.4, source: "quest" });
    expect(await board.waitFor("latency")).toEqual({ type: "latency", hop: "beat.machine", ms: 12, source: "quest" });
    await monitor.waitFor("latency");
    expect((await post(hive, "/glasses/latency", { hop: "photo", ms: 4100 })).status).toBe(200);
    expect((await board.waitFor("latency", (m) => m.hop === "photo")).source).toBe("glasses");
  });
});

describe("scan pipeline", () => {
  test("press outside a scan beat is not allowed (practice line in pre)", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    let r = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    expect(r.json).toEqual({ allowed: false, scan_id: null, line_id: "ear_practice" });
    await setBeat(quest, board, "hornet", 6);
    r = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    expect(r.json).toEqual({ allowed: false, scan_id: null, line_id: "ear_not_now" });
    expect(quest.of("scan.started")).toHaveLength(0);
  });

  test("press during the smoker station broadcasts scan.started; a second press is de-duplicated", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    await setBeat(quest, board, "smoker", 3);
    const r = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    expect(r.json.allowed).toBe(true);
    expect(r.json.scan_id).toBeString();
    expect((await quest.waitFor("scan.started")).scan_id).toBe(r.json.scan_id);
    await board.waitFor("scan.started");
    await monitor.waitFor("scan.started");

    const again = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    expect(again.json).toEqual({ allowed: false, scan_id: null, line_id: "ear_not_now" });
    await Bun.sleep(30);
    expect(quest.of("scan.started")).toHaveLength(1);
  });

  test('scan with heard "lavender" -> speech verdict purple_bee_flower on socket 2, no Claude call', async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "feed", 4);
    const pressed_at = Date.now();
    const { json: press } = await post(hive, "/glasses/press", { pressed_at });
    const { status, json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "it's lavender", pressed_at });
    expect(status).toBe(200);
    expect(json).toMatchObject({
      item: "purple_bee_flower",
      verdict: "good",
      line_id: "ear_verdict_purple_bee_flower",
      audio_url: null,
      path: "speech",
    });
    const v = await quest.waitFor("scan.verdict");
    expect(v).toMatchObject({
      scan_id: press.scan_id,
      item: "purple_bee_flower",
      verdict: "good",
      socket: 2,
      line_id: "room_verdict_purple_bee_flower",
      tts_id: null,
      path: "speech",
    });
    expect(v.ms).toBeGreaterThanOrEqual(0);
    expect(v.ms).toBeLessThan(1000);
    expect(hive.deps.judgeCalls).toHaveLength(0);
    expect(hive.state.memory.scanned).toEqual(["purple_bee_flower"]);
    expect(hive.state.memory.first_item).toBe("purple_bee_flower");
    expect(hive.state.scan_in_flight).toBeNull();

    // retrying the same scan is idempotent
    const retry = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "roses", pressed_at });
    expect(retry.json.item).toBe("purple_bee_flower");
    expect(quest.of("scan.verdict")).toHaveLength(1);
  });

  test("photo scan: downscaled JPEG framed as [0x01][jpeg] after scan.photo; Claude gets the photo", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const monitor = await connect("monitor");
    const board = await connect("board");
    await setBeat(quest, board, "feed", 4);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, photo_url: photoUrl, pressed_at: Date.now() });
    expect(json).toMatchObject({ item: "purple_bee_flower", path: "claude" });

    for (const c of [quest, monitor]) {
      const photoMsg = await c.waitFor("scan.photo");
      expect(photoMsg).toEqual({ type: "scan.photo", scan_id: press.scan_id, width: 1024, height: 768 });
      const idx = c.events.findIndex((e) => e.kind === "json" && e.msg.type === "scan.photo");
      const bin = c.events[idx + 1];
      expect(bin?.kind).toBe("bin");
      const data = (bin as { data: Uint8Array }).data;
      expect(data[0]).toBe(BINARY_KIND.SCAN_PHOTO);
      expect([data[1], data[2]]).toEqual([0xff, 0xd8]); // JPEG SOI
      const meta = await sharp(data.subarray(1)).metadata();
      expect([meta.width, meta.height]).toEqual([1024, 768]);
    }
    expect(board.events.some((e) => e.kind === "bin")).toBe(false);
    expect(hive.deps.judgeCalls).toHaveLength(1);
    expect(hive.deps.judgeCalls[0]!.mime).toBe("image/jpeg");
    expect(hive.deps.judgeCalls[0]!.photo!.length).toBeGreaterThan(100);
    expect(hive.deps.judgeCalls[0]!.beat).toBe("feed");
  });

  test("speech verdict does not wait for a slow photo; the polaroid still develops", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "smoker", 3);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const t0 = Date.now();
    const slowUrl = photoUrl.replace("photo.jpg", "slow.jpg");
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "roses", photo_url: slowUrl, pressed_at: t0 });
    expect(Date.now() - t0).toBeLessThan(1000);
    expect(json).toMatchObject({ item: "red_rose", verdict: "bad", path: "speech" });
    expect((await quest.waitFor("scan.verdict")).line_id).toBe("room_verdict_red_rose");
    await quest.waitFor("scan.photo", () => true, 5000);
  });

  test("broken photo and no transcript -> canned unclear line, no crash", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "wasps", 5);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", {
      scan_id: press.scan_id,
      photo_url: photoUrl.replace("photo.jpg", "broken.jpg"),
      pressed_at: Date.now(),
    });
    expect(json.item).toBe("unclear");
    const v = await quest.waitFor("scan.verdict");
    expect(v).toMatchObject({ item: "unclear", line_id: "room_canned_unclear", tts_id: null });
    expect(hive.deps.judgeCalls).toHaveLength(0);
  });

  test("judge that hangs past the timeout -> fallback verdict", async () => {
    hive = makeHive({ judge: () => new Promise(() => {}) }, { directorTimeoutMs: 100 });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "wasps", 5);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "my shoe", pressed_at: Date.now() });
    expect(json).toMatchObject({ item: "unclear", verdict: "roast", path: "claude" });
    expect((await quest.waitFor("scan.verdict")).line_id).toBe("room_canned_unclear");
  });

  test("off-list roast streams live PCM to the Quest: tts.start, aligned [0x02] chunks, tts.end", async () => {
    hive = makeHive({
      ttsAvailable: () => true,
      judge: async () => ({ item: "non_plant_object", verdict: "roast", confidence: 0.9, seen: "a coffee cup", line: "Coffee. Bitter, like my grievances.", ms: 3, model: "mock", fallback: false }),
    });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "wasps", 5);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "my coffee", pressed_at: Date.now() });
    expect(json).toMatchObject({ item: "non_plant_object", line_id: null, audio_url: null }); // the Quest speaks, not the ear
    const v = await quest.waitFor("scan.verdict");
    expect(v.tts_id).toBeString();
    expect(v.socket).toBeNull();
    expect(v.caption).toBe("Coffee. Bitter, like my grievances.");
    const start = await quest.waitFor("tts.start");
    expect(start).toEqual({ type: "tts.start", tts_id: v.tts_id, sample_rate: 16000, caption: "Coffee. Bitter, like my grievances." });
    await quest.waitFor("tts.end", (m) => m.tts_id === v.tts_id);
    const s = quest.events.findIndex((e) => e.kind === "json" && e.msg.type === "tts.start");
    const e = quest.events.findIndex((e) => e.kind === "json" && e.msg.type === "tts.end");
    const chunks = quest.events.slice(s + 1, e).map((ev) => (ev as { data: Uint8Array }).data);
    expect(chunks.every((c) => c[0] === BINARY_KIND.TTS_PCM16 && (c.length - 1) % 2 === 0)).toBe(true);
    expect(chunks.flatMap((c) => [...c.subarray(1)])).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
    expect(hive.deps.ttsCalls).toEqual(["Coffee. Bitter, like my grievances."]);
  });

  test("room TTS streams are queued one at a time, and a throwing TTS still ends its stream", async () => {
    hive = makeHive({
      ttsPcm: async function* (text: string) {
        if (text === "boom") throw new Error("elevenlabs down");
        await Bun.sleep(150);
        yield new Uint8Array([9, 9]);
      },
    });
    const quest = await connect("quest");
    const a = hive.bus.enqueueRoomTts("t-a", "first", 16000, () => hive!.deps.ttsPcm("first"));
    const b = hive.bus.enqueueRoomTts("t-b", "boom", 16000, () => hive!.deps.ttsPcm("boom"));
    const c = hive.bus.enqueueRoomTts("t-c", "third", 16000, () => hive!.deps.ttsPcm("third"));
    await Promise.all([a, b, c]);
    await quest.waitFor("tts.end", (m) => m.tts_id === "t-c");
    const order = quest.msgs.filter((m) => m.type.startsWith("tts.")).map((m) => `${m.type}:${m.tts_id}`);
    expect(order).toEqual(["tts.start:t-a", "tts.end:t-a", "tts.start:t-b", "tts.end:t-b", "tts.start:t-c", "tts.end:t-c"]);
  });

  test("group reset cancels stalled and queued room voice so the next group can speak", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const blocked = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    const active = hive.bus.enqueueRoomTts("old-active", "old active", 16000, async function* () {
      yield new Uint8Array([1, 1]);
      blocked.resolve();
      await release.promise;
      yield new Uint8Array([2, 2]);
    });
    let discarded = false;
    const queued = hive.bus.enqueueRoomTts("old-queued", "old queued", 16000, async function* () {
      yield new Uint8Array([3, 3]);
    }, () => { discarded = true; });
    let next: Promise<void> | undefined;

    try {
      await blocked.promise;
      board.send({ type: "group.reset" });
      await quest.waitFor("group.reset");
      const resetIndex = quest.events.findIndex((e) => e.kind === "json" && e.msg.type === "group.reset");
      next = hive.bus.enqueueRoomTts("new", "new group", 16000, async function* () {
        yield new Uint8Array([4, 4]);
      });
      await quest.waitFor("tts.end", (m) => m.tts_id === "new");
      expect(discarded).toBe(true);
      release.resolve();
      await Promise.all([active, queued, next]);
      const afterReset = quest.events.slice(resetIndex + 1);
      expect(afterReset.filter((e) => e.kind === "json" && e.msg.type.startsWith("tts."))
        .map((e) => e.kind === "json" && `${e.msg.type}:${e.msg.tts_id}`)).toEqual(["tts.start:new", "tts.end:new"]);
      expect(afterReset.filter((e) => e.kind === "bin").map((e) => e.kind === "bin" && [...e.data])).toEqual([
        [BINARY_KIND.TTS_PCM16, 4, 4],
      ]);
    } finally {
      release.resolve();
      await Promise.all([active, queued, next]);
    }
  });

  test("live line with no Quest connected goes to the ear as an mp3 at /audio/<id>.mp3", async () => {
    hive = makeHive({
      ttsAvailable: () => true,
      judge: async () => ({ item: "non_plant_object", verdict: "roast", confidence: 0.9, seen: "a badge", line: "A badge. Very official. Denied.", ms: 3, model: "mock", fallback: false }),
    });
    const board = await connect("board");
    board.send({ type: "beat.request", n: 12 }); // Field Mode, no Quest
    await board.waitFor("beat.set");
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    expect(press.allowed).toBe(true);
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "my badge", pressed_at: Date.now() });
    expect(json.caption).toBe("A badge. Very official. Denied.");
    expect(json.audio_url).toMatch(/\/audio\/[0-9a-f-]+\.mp3$/);
    const mp3 = await fetch(json.audio_url.replace(/^https?:\/\/[^/]+/, hive.url));
    expect(mp3.status).toBe(200);
    expect(mp3.headers.get("content-type")).toBe("audio/mpeg");
    expect([...new Uint8Array(await mp3.arrayBuffer())].slice(0, 3)).toEqual([0x49, 0x44, 0x33]);
    expect((await fetch(`${hive.url}/audio/nope.mp3`)).status).toBe(404);
  });

  test("Field Mode: no Quest effects, glasses still get their answer", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "field", 12);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "bug spray", pressed_at: Date.now() });
    expect(json).toMatchObject({ item: "spray_bottle", verdict: "dangerous", line_id: "ear_verdict_spray_bottle" });
    expect((await board.waitFor("scan.verdict")).line_id).toBeNull(); // no room line in the field
    await post(hive, "/glasses/plea", { text: "Bees make my tea taste better" });
    await board.waitFor("plea");
    await Bun.sleep(30);
    expect(quest.msgs.filter((m) => m.type.startsWith("scan.") || m.type === "plea")).toEqual([]);
  });

  test("verdict.force with no press: scan.started + operator verdict, ear line over the bus", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    const glasses = await connect("glasses");
    board.send({ type: "verdict.force", item: "purple_bee_flower" });
    await quest.waitFor("scan.started");
    const v = await quest.waitFor("scan.verdict");
    expect(v).toMatchObject({ item: "purple_bee_flower", verdict: "good", socket: 2, path: "operator", confidence: 1 });
    expect((await glasses.waitFor("say")).line_id).toBe("ear_verdict_purple_bee_flower");
    board.send({ type: "verdict.force", item: "teapot" });
    await board.waitFor("error", (m) => m.message.includes("teapot"));
  });

  test("verdict.force beats a slow Claude: the waiting glasses get the operator answer, late verdict dropped", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    hive = makeHive({
      judge: async () => {
        await gate;
        return { item: "red_rose", verdict: "bad", confidence: 0.9, seen: "roses", line: null, ms: 9000, model: "mock", fallback: false };
      },
    });
    const quest = await connect("quest");
    const board = await connect("board");
    const glasses = await connect("glasses");
    await setBeat(quest, board, "feed", 4);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const scanP = post(hive, "/glasses/scan", { scan_id: press.scan_id, photo_url: photoUrl, pressed_at: Date.now() });
    await Bun.sleep(300); // Claude is "thinking"
    board.send({ type: "verdict.force", item: "purple_bee_flower" });
    const { json } = await scanP;
    expect(json).toMatchObject({ item: "purple_bee_flower", path: "operator", line_id: "ear_verdict_purple_bee_flower" });
    release();
    await Bun.sleep(50);
    expect(quest.of("scan.verdict").map((m) => m.path)).toEqual(["operator"]);
    expect(glasses.of("say")).toHaveLength(0); // the HTTP answer carried the ear line: no double play
    expect(hive.state.memory.scanned).toEqual(["purple_bee_flower"]);
  });

  test("a scan whose press was lost acts as its own press", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "smoker", 3);
    const { json } = await post(hive, "/glasses/scan", { scan_id: "made-up", heard: "salvia", pressed_at: Date.now() });
    expect(json.item).toBe("purple_bee_flower");
    await quest.waitFor("scan.started");
    await quest.waitFor("scan.verdict");
  });

  test("plea is forwarded to the Quest and remembered", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const monitor = await connect("monitor");
    const r = await post(hive, "/glasses/plea", { text: "  My grandma keeps bees  " });
    expect(r.json).toEqual({ ok: true });
    expect(await quest.waitFor("plea")).toEqual({ type: "plea", text: "My grandma keeps bees" });
    await monitor.waitFor("plea");
    expect(hive.state.memory.pleas).toEqual(["My grandma keeps bees"]);
  });
});

describe("integration fixes (verifier)", () => {
  test("with the Quest speaking the fallback, the glasses get no caption to read aloud in Mentra's voice", async () => {
    hive = makeHive({ judge: () => Promise.reject(new Error("no key")) });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "wasps", 5);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "my shoe", pressed_at: Date.now() });
    expect(json).toMatchObject({ item: "unclear", line_id: null, audio_url: null, caption: "" });
    expect((await quest.waitFor("scan.verdict")).line_id).toBe("room_canned_unclear");
  });

  test("with no live voice (TTS down), the glasses still get the roast caption as a last resort", async () => {
    hive = makeHive({
      judge: async () => ({ item: "non_plant_object", verdict: "roast", confidence: 0.9, seen: "a mug", line: "A mug. Zero nectar.", ms: 3, model: "mock", fallback: false }),
    });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "wasps", 5);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const { json } = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "my mug", pressed_at: Date.now() });
    expect(json).toMatchObject({ line_id: null, audio_url: null, caption: "A mug. Zero nectar." });
    expect(await quest.waitFor("scan.verdict")).toMatchObject({ line_id: null, tts_id: null });
  });

  test("speech-first: a second /glasses/scan with the photo develops the polaroid without re-judging", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "feed", 4);
    const { json: press } = await post(hive, "/glasses/press", { pressed_at: Date.now() });
    const first = await post(hive, "/glasses/scan", { scan_id: press.scan_id, heard: "salvia", pressed_at: Date.now() });
    expect(first.json).toMatchObject({ item: "purple_bee_flower", path: "speech" });
    const again = await post(hive, "/glasses/scan", { scan_id: press.scan_id, photo_url: photoUrl, pressed_at: Date.now() });
    expect(again.json).toEqual(first.json);
    expect((await quest.waitFor("scan.photo")).scan_id).toBe(press.scan_id);
    await quest.waitEvent((e) => e.kind === "bin" && e.data[0] === BINARY_KIND.SCAN_PHOTO);
    expect(hive.deps.judgeCalls).toHaveLength(0);
    expect(quest.of("scan.verdict")).toHaveLength(1);
  });

  test("a late photo never overwrites the polaroid of a newer scan", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "smoker", 3);
    const slowUrl = photoUrl.replace("photo.jpg", "slow.jpg"); // 3 s
    const a = (await post(hive, "/glasses/press", { pressed_at: Date.now() })).json;
    await post(hive, "/glasses/scan", { scan_id: a.scan_id, heard: "roses", photo_url: slowUrl, pressed_at: Date.now() });
    const b = (await post(hive, "/glasses/press", { pressed_at: Date.now() })).json;
    expect(b.allowed).toBe(true);
    await Bun.sleep(3300);
    expect(quest.of("scan.photo").map((m) => m.scan_id)).not.toContain(a.scan_id);
  });

  test("operator verdict on an off-list item plays a canned acknowledgement instead of silence", async () => {
    hive = makeHive();
    const quest = await connect("quest");
    const board = await connect("board");
    board.send({ type: "verdict.force", item: "other_bee_plant" });
    const v = await quest.waitFor("scan.verdict");
    expect(v).toMatchObject({ item: "other_bee_plant", verdict: "good", line_id: "room_ack_allow", tts_id: null, path: "operator" });
  });

  test("orbis.request steers the relay (start, morph, stop) with scene prompts; its state is mirrored as orbis{}", async () => {
    // A stand-in for hive/orbis-relay: same paths, token check and status shape (plus extras the Hive must drop).
    const posts: Array<{ path: string; body: any }> = [];
    let status: Record<string, unknown> = { type: "orbis", state: "off", stage: 0, since: 1, fps: 0, fallback_clip: "moon_0.mp4", prompt: "" };
    const set = (patch: Record<string, unknown>) => (status = { ...status, ...patch, since: (status.since as number) + 1 });
    const relay = Bun.serve({
      port: 0,
      hostname: "127.0.0.1",
      async fetch(req) {
        if (req.headers.get("x-hive-token") !== TOKEN) return Response.json({ error: "bad token" }, { status: 401 });
        const path = new URL(req.url).pathname;
        if (req.method === "GET" && path === "/orbis/status") return Response.json(status);
        const body = (await req.json()) as { prompt?: string };
        posts.push({ path, body });
        if (path === "/orbis/start") set({ state: "priming", prompt: body.prompt });
        else if (path === "/orbis/stage") set({ prompt: body.prompt });
        else if (path === "/orbis/stop") set({ state: "ended" });
        return Response.json({ ok: true, ...status });
      },
    });
    try {
      hive = makeHive({}, { orbisRelayUrl: `http://127.0.0.1:${relay.port}/`, orbisPollMs: 50 });
      const quest = await connect("quest");
      const board = await connect("board");
      const meadow = SCENE_TABLE.meadow!;
      board.send({ type: "orbis.request", want: "live" });
      expect(await quest.waitFor("orbis.request")).toEqual({ type: "orbis.request", want: "live" });
      expect(await quest.waitFor("orbis", (m) => m.state === "priming")).toEqual({
        type: "orbis",
        state: "priming",
        stage: 0,
        scene: "meadow",
        fallback_clip: "meadow.mp4",
      });
      set({ state: "ready" }); // first frame: only the poll can see this
      await board.waitFor("orbis", (m) => m.state === "ready");

      // a scene switch with the current mode morphs the running stream to that scene's prompt
      board.send({ type: "orbis.request", want: "live", scene: "whatif_rain" });
      expect(await quest.waitFor("orbis.request", (m) => m.scene)).toEqual({ type: "orbis.request", want: "live", scene: "whatif_rain", stage: SCENES.indexOf("whatif_rain") });
      await quest.waitFor("orbis", (m) => m.scene === "whatif_rain" && m.fallback_clip === "whatif_rain.mp4");
      // the quest may switch scenes too; a stale "clips" alongside a scene does NOT stop the stream
      quest.send({ type: "orbis.request", want: "clips", scene: "feed_sick" });
      await board.waitFor("orbis", (m) => m.scene === "feed_sick");
      expect(hive.state.scene).toBe("feed_sick");
      board.send({ type: "orbis.request", want: "live" }); // already live on this scene: nothing to do
      await board.waitFor("log", (m) => m.text.includes("already ready"));
      board.send({ type: "orbis.request", want: "clips" }); // a bare mode request always applies
      await quest.waitFor("orbis", (m) => m.state === "ended");
      await Bun.sleep(30);
      expect(posts.map((p) => p.path)).toEqual(["/orbis/start", "/orbis/stage", "/orbis/stage", "/orbis/stop"]);
      expect(posts[0]!.body).toEqual({ prompt: meadow.prompt, seed: meadow.seed, scene: "meadow" });
      expect(posts[1]!.body).toEqual({ prompt: SCENE_TABLE.whatif_rain!.prompt, scene: "whatif_rain" });
      expect(posts[2]!.body.scene).toBe("feed_sick");
      const h = await (await fetch(`${hive.url}/health`)).json();
      expect(h.orbis).toMatchObject({ state: "ended", scene: "feed_sick", want: "clips" });

      // no stream: a scene switch never opens a billed session, but Unity still gets the clip
      board.send({ type: "orbis.request", want: "clips", scene: "heat_drink" });
      await quest.waitFor("orbis", (m) => m.scene === "heat_drink" && m.state === "ended");
      await Bun.sleep(30);
      expect(posts).toHaveLength(4);

      board.send({ type: "orbis.request", want: "live", stage: 1.5 });
      expect((await board.waitFor("error")).message).toContain("stage");
      board.send({ type: "orbis.request", want: "clips", scene: "mars" });
      await board.waitFor("error", (m) => m.message.includes("mars"));
      board.send({ type: "orbis.request", want: "clips", stage: 99 });
      await board.waitFor("error", (m) => m.message.includes("99"));
      const glasses = await connect("glasses");
      glasses.send({ type: "orbis.request", want: "clips", scene: "moon" });
      await glasses.waitFor("error", (m) => m.message.includes("board or the quest"));
    } finally {
      relay.stop(true);
    }
  });
});
