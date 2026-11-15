// v2.1 custom what-ifs: route() -> what_if/custom + custom_prompt -> intent (with the prompt) -> the cover line -> the
// live window morphs to the prompt (scene whatif_custom), or the clips line when no stream is up -> undo goes back.
// Refusals: the router's null and the Hive's own final check. Also POST /board/custom (typed text, routed like speech).
// The router is mocked; the relay is a local stand-in with the real paths, token check and status shape.
import { afterEach, describe, expect, test } from "bun:test";
import { LINES, SCENE_TABLE } from "../src/lines";
import { CUSTOM_PROMPT_MAX, checkCustomPrompt, type Hive, type LineInfo, type RouteResultLike } from "../src/server";
import { TestClient, makeHive, mockRoute, post, setBeat, TOKEN, type MockDeps } from "./helpers";

let hive: (Hive & { deps: MockDeps }) | null = null;
const clients: TestClient[] = [];
const relays: Array<ReturnType<typeof Bun.serve>> = [];
async function connect(role: "quest" | "board" | "monitor" | "glasses"): Promise<TestClient> {
  const c = await TestClient.connect(hive!, role);
  clients.push(c);
  return c;
}
afterEach(async () => {
  for (const c of clients.splice(0)) c.close();
  await hive?.stop();
  hive = null;
  for (const r of relays.splice(0)) r.stop(true);
});

const SETTING = "A sunny wildflower meadow with a white wooden beehive on a low stand in the center, with oak trees under a soft blue sky.";
const CAMERA = "Wide shot, eye-level, static camera, deep depth of field.";
const CHEESE = `${SETTING} The hive is carved from a giant wheel of golden cheese and honeybees circle it. ${CAMERA}`;
const GIANT = `${SETTING} Giant honeybees as big as sheepdogs hover over the flowers and crowd around the hive entrance. ${CAMERA}`;

// The v2.1 lines, pinned here so these tests don't depend on the exact catalog (the lines builder owns lines.json).
const V21_LINES: LineInfo[] = [
  { id: "room_whatif_custom", channel: "room", text: "Hold on. I'm picturing it.", tags: ["whatif", "topic:custom"] },
  { id: "room_whatif_custom_2", channel: "room", text: "Not on the agenda. Picturing it now.", tags: ["whatif", "alt:room_whatif_custom"] },
  { id: "room_whatif_custom_clips", channel: "room", text: "I'm picturing it. The window isn't.", tags: ["whatif", "clips"] },
  { id: "room_whatif_refuse", channel: "room", text: "Grievance filed. Not that one.", tags: ["whatif", "refuse"] },
];
function v21Lines() {
  const lines: Record<string, LineInfo> = { ...LINES };
  for (const id of Object.keys(lines)) if (id.startsWith("room_whatif_custom") || id === "room_whatif_refuse") delete lines[id];
  for (const l of V21_LINES) lines[l.id] = l;
  return { lines, lineText: (id: string) => lines[id]?.text ?? "" };
}

/** The router stand-in: "what if ..." phrases it knows become custom what-ifs with these prompts (null = refuse). */
const routed: Array<{ text: string; ctx: unknown }> = [];
function customRouter(prompts: Record<string, string | null>) {
  routed.length = 0;
  return async (text: string, ctx: unknown): Promise<RouteResultLike> => {
    routed.push({ text, ctx });
    for (const [k, prompt] of Object.entries(prompts)) {
      if (text.includes(k)) return { intent: "what_if", topic: "custom", answer: null, custom_prompt: prompt, ms: 900, path: "claude" };
    }
    return mockRoute(text);
  };
}

/** A stand-in for hive/orbis-relay. */
function fakeRelay() {
  const posts: Array<{ path: string; body: any }> = [];
  let status: Record<string, unknown> = { type: "orbis", state: "off", stage: 0, since: 1, fps: 0, prompt: "" };
  const set = (patch: Record<string, unknown>) => (status = { ...status, ...patch, since: (status.since as number) + 1 });
  const server = Bun.serve({
    port: 0,
    hostname: "127.0.0.1",
    async fetch(req) {
      if (req.headers.get("x-hive-token") !== TOKEN) return Response.json({ error: "bad token" }, { status: 401 });
      const path = new URL(req.url).pathname;
      if (req.method === "GET" && path === "/orbis/status") return Response.json(status);
      const body = (await req.json()) as { prompt?: string; scene?: string };
      posts.push({ path, body });
      if (path === "/orbis/start") set({ state: "priming", prompt: body.prompt, scene: body.scene });
      else if (path === "/orbis/stage") set({ prompt: body.prompt, scene: body.scene });
      else if (path === "/orbis/stop") set({ state: "ended" });
      return Response.json({ ok: true, ...status });
    },
  });
  relays.push(server);
  return { posts, set, url: `http://127.0.0.1:${server.port}`, stages: () => posts.filter((p) => p.path === "/orbis/stage") };
}

/** Board turns the stream on and the relay reports its first frame. */
async function goLive(board: TestClient, relay: ReturnType<typeof fakeRelay>): Promise<void> {
  board.send({ type: "orbis.request", want: "live" });
  await board.waitFor("orbis", (m) => m.state === "priming");
  relay.set({ state: "ready" });
  await board.waitFor("orbis", (m) => m.state === "ready");
}

describe("custom what-if on the live window", () => {
  test("intent carries the prompt; cover line; the relay morphs to it on whatif_custom keeping the last clip; undo goes back", async () => {
    const relay = fakeRelay();
    hive = makeHive(
      { ...v21Lines(), route: customRouter({ cheese: CHEESE, giant: GIANT }) },
      { orbisRelayUrl: relay.url, orbisPollMs: 40, embersDelayMs: 100 },
    );
    const quest = await connect("quest");
    const board = await connect("board");
    const monitor = await connect("monitor");
    await setBeat(quest, board, "whatif", 8);
    await goLive(board, relay);
    quest.say("what if a fire starts"); // a planned one first: the custom what-if replaces the burning meadow
    await quest.waitFor("orbis", (m) => m.scene === "whatif_fire");

    quest.say("what if the hive was made of cheese");
    const intent = await board.waitFor("intent", (m) => m.topic === "custom");
    expect(intent).toEqual({
      type: "intent", name: "what_if", topic: "custom", text: "what if the hive was made of cheese", path: "claude", ms: 900, custom_prompt: CHEESE,
    });
    expect((await monitor.waitFor("intent", (m) => m.topic === "custom")).custom_prompt).toBe(CHEESE);
    const say = await quest.waitFor("say", (m) => m.line_id?.startsWith("room_whatif_custom"));
    expect(say).toMatchObject({ line_id: "room_whatif_custom", caption: "Hold on. I'm picturing it." });
    const shown = await quest.waitFor("orbis", (m) => m.scene === "whatif_custom");
    expect(shown).toMatchObject({ state: "ready", stage: 15, fallback_clip: "whatif_fire.mp4" }); // no clip of its own: keep the last one
    expect(quest.msgs.indexOf(say)).toBeLessThan(quest.msgs.indexOf(shown)); // the line covers the 2-4 s morph
    await quest.waitFor("fx", (m) => m.name === "embers_stop");
    await Bun.sleep(40);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: CHEESE, scene: "whatif_custom" });
    let h = await (await fetch(`${hive.url}/health`)).json();
    expect(h.orbis).toMatchObject({ scene: "whatif_custom", custom_prompt: CHEESE });
    expect((await board.waitFor("log", (m) => m.text.includes("custom what-if on the live window"))).text).toContain("wheel of golden cheese");

    // a second one: same scene, new prompt (re-staged), and the cover line's alternate
    quest.say("what if giant bees showed up");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_custom_2");
    await board.waitFor("orbis", (m) => m.scene === "whatif_custom" && board.of("orbis").filter((x) => x.scene === "whatif_custom").length === 2);
    await Bun.sleep(40);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: GIANT, scene: "whatif_custom" });
    expect(quest.of("orbis").filter((m) => m.scene === "whatif_custom").every((m) => m.fallback_clip === "whatif_fire.mp4")).toBe(true);

    // undo: back to the last real scene (the meadow), with its own prompt
    quest.say("go back");
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_back");
    await quest.waitFor("orbis", (m) => m.scene === "meadow" && quest.msgs.indexOf(m) > quest.msgs.indexOf(shown));
    await Bun.sleep(40);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: SCENE_TABLE.meadow!.prompt, scene: "meadow" });
    h = await (await fetch(`${hive.url}/health`)).json();
    expect(h.orbis.custom_prompt).toBeUndefined();
    expect(hive.state.memory.whatifs).toEqual(["fire", "custom", "custom"]);
    expect(relay.posts.map((p) => p.path)).toEqual(["/orbis/start", "/orbis/stage", "/orbis/stage", "/orbis/stage", "/orbis/stage"]);
  });

  test("the Board's LIVE on a custom scene starts the stream with the custom prompt; a group reset forgets it", async () => {
    const relay = fakeRelay();
    hive = makeHive({ ...v21Lines(), route: customRouter({ cheese: CHEESE }) }, { orbisRelayUrl: relay.url, orbisPollMs: 40 });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    await goLive(board, relay);
    quest.say("what if the hive was made of cheese");
    await quest.waitFor("orbis", (m) => m.scene === "whatif_custom");
    board.send({ type: "orbis.request", want: "clips" });
    await board.waitFor("orbis", (m) => m.state === "ended");
    relay.set({ state: "off" });
    await Bun.sleep(60);
    board.send({ type: "orbis.request", want: "live" });
    await board.waitFor("orbis", (m) => m.state === "priming" && board.of("orbis").filter((x) => x.state === "priming").length === 2);
    await Bun.sleep(40);
    const start = relay.posts.filter((p) => p.path === "/orbis/start").at(-1)!;
    expect(start.body).toEqual({ prompt: CHEESE, seed: SCENE_TABLE.whatif_custom?.seed ?? SCENE_TABLE.meadow!.seed, scene: "whatif_custom" });

    board.send({ type: "group.reset" });
    await quest.waitFor("orbis", (m) => m.scene === "meadow" && quest.of("group.reset").length === 1);
    expect(hive.state.custom_prompt).toBeNull();
    await Bun.sleep(40);
    expect(relay.stages().at(-1)!.body.scene).toBe("meadow");
  });
});

describe("custom what-if without a live stream (clips)", () => {
  test("no relay call, the window keeps its clip, and Beeswax says the clips line instead of the cover line", async () => {
    const relay = fakeRelay(); // configured but the stream is off
    hive = makeHive({ ...v21Lines(), route: customRouter({ cheese: CHEESE }) }, { orbisRelayUrl: relay.url, orbisPollMs: 40 });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "feed", 4);
    quest.send({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    await quest.waitFor("orbis", (m) => m.scene === "feed_sick");
    quest.say("what if the hive was made of cheese");
    expect((await board.waitFor("intent")).custom_prompt).toBe(CHEESE);
    expect((await quest.waitFor("say", (m) => m.line_id !== "room_feed_store_honey")).line_id).toBe("room_whatif_custom_clips");
    await board.waitFor("log", (m) => m.text.includes("no live stream (clips)"));
    await Bun.sleep(60);
    expect(quest.of("say").map((m) => m.line_id)).toEqual(["room_feed_store_honey", "room_whatif_custom_clips"]);
    expect(quest.of("orbis").some((m) => m.scene === "whatif_custom")).toBe(false);
    expect(hive.state.scene).toBe("feed_sick");
    expect(relay.posts).toEqual([]);
    expect(hive.state.memory.whatifs).toEqual(["custom"]);
  });

  test("with no relay configured at all, and with the lines missing, it falls back to acknowledgements", async () => {
    hive = makeHive({ route: customRouter({ cheese: CHEESE, nope: null }), lines: { ...LINES, room_whatif_custom_clips: undefined as never, room_whatif_refuse: undefined as never } });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    quest.say("what if the hive was made of cheese");
    expect((await quest.waitFor("say")).line_id).toBe("room_ack_tabled");
    quest.say("what if nope");
    await quest.waitFor("say", () => quest.of("say").length === 2);
    expect(quest.of("say")[1].line_id).toBe("room_ack_grievance");
  });
});

describe("refusals: nothing unvetted reaches the relay", () => {
  test("the router's null and the Hive's own check (URL, too long, denylist) all become the refusal line", async () => {
    const relay = fakeRelay();
    const prompts: Record<string, string | null> = {
      "a bad idea": null,
      "a link": `${SETTING} A banner reads https://example.com over the hive. ${CAMERA}`,
      "a long one": `${SETTING} ${"Honeybees dance over the clover. ".repeat(20)}${CAMERA}`,
      "a gross one": `${SETTING} Blood drips from the hive entrance. ${CAMERA}`,
      "a sneaky one": `${SETTING} Bl​ood drips from the hive entrance. ${CAMERA}`,
      "people": `${SETTING} A crowd of people screaming as the meadow burns. ${CAMERA}`,
    };
    hive = makeHive({ ...v21Lines(), route: customRouter(prompts) }, { orbisRelayUrl: relay.url, orbisPollMs: 40 });
    const quest = await connect("quest");
    const board = await connect("board");
    await setBeat(quest, board, "whatif", 8);
    await goLive(board, relay);
    const before = relay.posts.length;
    const reasons: string[] = [];
    for (const [i, k] of Object.keys(prompts).entries()) {
      quest.say(`what if ${k}`);
      const intent = await board.waitFor("intent", (m) => m.text === `what if ${k}`);
      expect({ k, custom_prompt: intent.custom_prompt }).toEqual({ k, custom_prompt: null });
      await quest.waitFor("say", () => quest.of("say").length === i + 1);
      expect(quest.of("say")[i].line_id).toBe("room_whatif_refuse");
      reasons.push((await board.waitFor("log", (m) => m.text.includes(`"what if ${k}"`))).text);
    }
    expect(reasons[0]).toContain("REFUSED (the router refused)");
    expect(reasons[1]).toContain("REFUSED (blocked by the Hive: contains a URL)");
    expect(reasons[2]).toContain("too long");
    expect(reasons[3]).toContain('denylist: "blood"');
    expect(reasons[4]).toContain('denylist: "blood"');
    expect(reasons[5]).toContain("denylist");
    await Bun.sleep(60);
    expect(relay.posts.length).toBe(before);
    expect(quest.of("orbis").some((m) => m.scene === "whatif_custom")).toBe(false);
    expect(hive.state.custom_prompt).toBeNull();
  });
});

describe("POST /board/custom (typed what-if)", () => {
  test("token-gated; routed like speech (transcript stt=board, intent) and acted on in any beat; answers with the prompt", async () => {
    const relay = fakeRelay();
    hive = makeHive(
      { ...v21Lines(), route: customRouter({ cheese: CHEESE, "a link": `${SETTING} Visit www.bees.example now. ${CAMERA}` }) },
      { orbisRelayUrl: relay.url, orbisPollMs: 40 },
    );
    const quest = await connect("quest");
    const board = await connect("board");
    expect((await post(hive, "/board/custom", { text: "what if cheese" }, null)).status).toBe(401);
    expect((await post(hive, "/board/custom", { text: "what if cheese" }, "wrong")).status).toBe(401);
    expect((await post(hive, "/board/custom", { text: "   " })).status).toBe(400);
    expect((await post(hive, "/board/custom", "{nope")).status).toBe(400);

    // beat 0 (pre) is not a voice beat, but the operator's text acts anyway; clips mode -> the clips line
    let r = await post(hive, "/board/custom", { text: "what if the hive was made of cheese" });
    expect(r).toEqual({
      status: 200,
      json: { ok: true, intent: "what_if", topic: "custom", path: "claude", ms: 900, custom_prompt: CHEESE, blocked: null },
    });
    expect(await board.waitFor("transcript")).toEqual({ type: "transcript", text: "what if the hive was made of cheese", final: true, source: "quest", ms: 0, stt: "board" });
    expect((await board.waitFor("intent")).custom_prompt).toBe(CHEESE);
    expect((await quest.waitFor("say")).line_id).toBe("room_whatif_custom_clips");
    expect((await board.waitFor("log", (m) => m.text.startsWith("operator text"))).text).toContain("(path operator)");
    expect(routed.at(-1)).toEqual({ text: "what if the hive was made of cheese", ctx: { beat: "pre", recent: [] } });

    // live: the typed one morphs the window too
    await goLive(board, relay);
    r = await post(hive, "/board/custom", { text: "what if the hive was made of cheese" });
    expect(r.json.custom_prompt).toBe(CHEESE);
    await quest.waitFor("orbis", (m) => m.scene === "whatif_custom");
    await Bun.sleep(40);
    expect(relay.stages().at(-1)!.body).toEqual({ prompt: CHEESE, scene: "whatif_custom" });

    // a planned topic typed in works like the voice; a blocked prompt is reported back
    r = await post(hive, "/board/custom", { text: "what if a fire starts" });
    expect(r.json).toEqual({ ok: true, intent: "what_if", topic: "fire", path: "keyword", ms: 3 });
    await quest.waitFor("orbis", (m) => m.scene === "whatif_fire");
    r = await post(hive, "/board/custom", { text: "what if we add a link" });
    expect(r.json).toMatchObject({ topic: "custom", custom_prompt: null, blocked: "blocked by the Hive: contains a URL" });
    await quest.waitFor("say", (m) => m.line_id === "room_whatif_refuse");
    r = await post(hive, "/board/custom", { text: "why do bees make honey?" });
    expect(r.json).toEqual({ ok: true, intent: "ask", topic: null, path: "claude", ms: 40, answer: "We make honey. It's a whole thing." });
  });

  test("with no Quest the reply still shows on the board (room caption); glasses-free", async () => {
    hive = makeHive({ ...v21Lines(), route: customRouter({ cheese: CHEESE }) });
    const board = await connect("board");
    const r = await post(hive, "/board/custom", { text: "what if the hive was made of cheese" });
    expect(r.json.custom_prompt).toBe(CHEESE);
    expect(await board.waitFor("say")).toMatchObject({ channel: "room", line_id: "room_whatif_custom_clips" });
  });
});

describe("checkCustomPrompt (the Hive's final local check)", () => {
  test("passes scene prompts like the router's, cleaned; bee phrases with a denied word are fine", () => {
    for (const p of [
      CHEESE,
      GIANT,
      `A moonlit wildflower meadow with a white wooden beehive on a low stand in the center, with oak trees under a starry night sky. Fireflies drift over the grass. ${CAMERA}`,
      `A parched golden meadow with a white wooden beehive on a low stand. Cracked dry earth and wilted brown flowers surround the hive while a few honeybees search the stems. ${CAMERA}`,
      `${SETTING} A swarm of killer bees circles a murder hornet nest in the oak tree. ${CAMERA}`,
      `${SETTING} Children toss seed bombs and wildflowers bloom around the hive. ${CAMERA}`,
    ]) {
      expect(checkCustomPrompt(p)).toEqual({ ok: true, prompt: p });
    }
    expect(checkCustomPrompt(`  A meadow\n\twith   a hive.​  `)).toEqual({ ok: true, prompt: "A meadow with a hive." });
    expect(checkCustomPrompt("x".repeat(CUSTOM_PROMPT_MAX))).toMatchObject({ ok: true });
  });

  test("refuses non-strings, empty, over 600 chars, URLs, and violent/sexual/hateful/harm-to-people words", () => {
    const refused = (p: unknown) => {
      const r = checkCustomPrompt(p);
      return r.ok ? null : r.reason;
    };
    expect(refused(null)).toBe("no prompt");
    expect(refused(42)).toBe("no prompt");
    expect(refused(" \n ")).toBe("empty prompt");
    expect(refused("x".repeat(CUSTOM_PROMPT_MAX + 1))).toContain("too long");
    for (const p of ["see https://example.com", "HTTP://X.Y", "go to www.bees", "visit bees.dev today", "javascript:alert(1)"]) {
      expect({ p, r: refused(p) }).toEqual({ p, r: "contains a URL" });
    }
    for (const p of [
      "The farmer kills the bees", "a bloody meadow", "a man with a GUN", "bombs fall on the hive", "a naked gardener",
      "a nazi flag on the hive", "racist graffiti", "a hive full of corpses", "people trapped in the fire", "the fire burns everyone",
      "a child is hurt by bees", "bees attack the players", "self-harm", "a war between hives", "Ｂｌｏｏｄ in fullwidth",
    ]) {
      expect({ p, r: refused(p)?.startsWith("denylist") }).toEqual({ p, r: true });
    }
    // near misses that are fine
    for (const p of ["a warm meadow", "rapeseed fields in bloom", "Essex countryside", "the hive is a gorgeous gold", "dead flowers in a drought"]) {
      expect({ p, r: refused(p) }).toEqual({ p, r: null });
    }
  });
});
