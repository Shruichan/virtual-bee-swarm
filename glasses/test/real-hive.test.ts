// The glasses app against the REAL Hive (hive/src/server.ts + the real line, station and scene
// tables + the real intent router's keyword path), with a mocked Claude director, an offline Claude
// client for the router, no TTS and no STT. Catches contract drift the stub Hive in flow.test.ts can't.
// No internet, no paid API, no speaker: the photo comes from a local server.
import {afterAll, afterEach, beforeAll, expect, test} from "bun:test"
import {setQuiet} from "../../hive/src/bus"
import * as hiveLines from "../../hive/src/lines"
import {BEATS, type Channel, type Item, type Verdict} from "../../hive/src/protocol"
import {route, setRouterClient, type RouterClient} from "../../hive/src/router"
import {startHive, type Hive, type HiveDeps, type JudgeInputLike} from "../../hive/src/server"
import {start} from "../src/background/app"
import {Hive as HiveClient} from "../src/background/hive"
import {makeConfig} from "../src/background/config"
import {CAPTIONS, FILLERS, LINES} from "../src/background/lines"
import {fakeSession, sleep, until, type FakeOptions} from "./fakes"

setQuiet(true)
const TOKEN = "real-hive-token"
// 16x12 purple JPEG (sharp must be able to decode it)
const JPEG = Buffer.from(
  "/9j/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAAMABADASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAABAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCcAuEv/9k=",
  "base64",
)

const judgeCalls: JudgeInputLike[] = []
let hive: Hive
let photos: ReturnType<typeof Bun.serve>
let photoUrl = ""
type Rx = {json: any[]; bin: Uint8Array[]}
let quest: WebSocket
let monitor: WebSocket
let board: WebSocket
const rx: Record<"quest" | "monitor" | "board", Rx> = {quest: {json: [], bin: []}, monitor: {json: [], bin: []}, board: {json: [], bin: []}}

function client(role: "quest" | "monitor" | "board"): Promise<WebSocket> {
  const ws = new WebSocket(`ws://127.0.0.1:${hive.port}/bus?role=${role}&token=${TOKEN}`)
  ws.binaryType = "arraybuffer"
  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") rx[role].json.push(JSON.parse(ev.data))
    else rx[role].bin.push(new Uint8Array(ev.data as ArrayBuffer))
  }
  return new Promise((resolve) => (ws.onopen = () => resolve(ws)))
}

/** Calls that got past the router's keyword path (each one would be a paid Claude call in production). */
let modelCalls = 0
const offlineClaude = {
  beta: {
    messages: {
      create: async () => {
        modelCalls++
        throw new Error("offline in tests")
      },
    },
  },
} as unknown as RouterClient

function realDeps(): HiveDeps {
  return {
    judge: async (input) => {
      judgeCalls.push(input)
      return {item: "purple_bee_flower", verdict: "good", confidence: 0.9, seen: "purple salvia", line: null, ms: 5, model: "mock", fallback: false}
    },
    ttsPcm: async function* () {},
    ttsMp3: async () => new Uint8Array(),
    ttsAvailable: () => false,
    ttsSampleRate: 16000,
    verdictLine: (item: Item, verdict: Verdict, channel: Channel) => hiveLines.verdictLine(item, verdict, channel),
    lineText: hiveLines.lineText,
    lines: hiveLines.LINES,
    lineIdForText: hiveLines.lineIdForText,
    transcribe: async () => ({text: "", ms: 0, provider: "none", error: "stt offline in tests"}),
    route: (text, ctx, opts) => route(text, ctx, {...opts, timeoutMs: 500}),
    stations: hiveLines.STATION_TABLE,
    scenes: hiveLines.SCENE_TABLE,
    ackLines: hiveLines.ACK_LINES,
  }
}

const startRealHive = () => startHive({deps: realDeps(), port: 0, hostname: "127.0.0.1", token: TOKEN, heartbeatMs: 60_000, host: "test"})

beforeAll(async () => {
  setRouterClient(offlineClaude)
  photos = Bun.serve({port: 0, hostname: "127.0.0.1", fetch: () => new Response(JPEG, {headers: {"content-type": "image/jpeg"}})})
  photoUrl = `http://127.0.0.1:${photos.port}/p.jpg`
  hive = startRealHive()
  quest = await client("quest")
  monitor = await client("monitor")
  board = await client("board")
})

afterAll(async () => {
  quest.close()
  monitor.close()
  board.close()
  await hive.stop()
  photos.stop(true)
  setRouterClient(null)
})

let app: {stop(): void} | null = null
afterEach(() => {
  app?.stop()
  app = null
  judgeCalls.length = 0
  modelCalls = 0
  for (const r of Object.values(rx)) (r.json.length = 0), (r.bin.length = 0)
})

async function boot(opts: FakeOptions = {}) {
  const fake = fakeSession({photoUrl, ...opts})
  const cfg = makeConfig({hiveToken: TOKEN, hosts: [hive.url], heardWindowMs: 300, heardLookbackMs: 500, giveUpMs: 8000})
  app = start(fake.session, cfg)
  await until(() => fake.status.last?.bus === "open" && fake.status.last.beat !== null, 5000, "bus + welcome")
  return fake
}

async function beat(fake: ReturnType<typeof fakeSession>, name: (typeof BEATS)[number]) {
  quest.send(JSON.stringify({type: "beat.set", n: BEATS.indexOf(name), name}))
  await until(() => fake.status.last?.beat === name, 2000, `beat ${name}`)
}

const spoken = (fake: ReturnType<typeof fakeSession>) => fake.audio.filter((a) => a.kind === "speak").map((a: any) => a.text)
/** Captions of the Hive's ear lines (whatever the catalog holds right now). */
const earTexts = () => Object.values(hiveLines.LINES).filter((l) => l.channel === "ear").map((l) => l.text)

test("real Hive: press -> photo + heard -> Quest gets started/photo/verdict, the ear gets the verdict", async () => {
  const fake = await boot({photoMs: 100})
  await beat(fake, "smoker")
  fake.press()
  await sleep(20)
  fake.say("what do you think of this one")
  await until(() => fake.status.last?.scanning === false && rx.quest.json.some((m) => m.type === "scan.verdict"), 5000, "verdict")
  await sleep(100)

  const filler = FILLERS[0]
  if (filler) expect(fake.audio[0]).toMatchObject(LINES[filler] ? {kind: "clip", line: filler} : {kind: "speak", text: CAPTIONS[filler]})
  const verdictId = hiveLines.verdictLine("purple_bee_flower", "good", "ear")
  if (verdictId) await until(() => spoken(fake).at(-1) === hiveLines.lineText(verdictId), 2000, "verdict line in the ear")
  expect(judgeCalls).toHaveLength(1)
  expect(judgeCalls[0]!.photo?.length).toBeGreaterThan(100) // the Hive fetched our photo_url
  expect(judgeCalls[0]!.heard).toBe("what do you think of this one")

  const types = rx.quest.json.map((m) => m.type)
  expect(types).toEqual(expect.arrayContaining(["scan.started", "scan.photo", "scan.verdict"]))
  expect(rx.quest.bin.length).toBe(1) // the JPEG after scan.photo
  const verdict = rx.quest.json.find((m) => m.type === "scan.verdict")
  expect(verdict).toMatchObject({item: "purple_bee_flower", verdict: "good", socket: 2, path: "claude"})

  // press_to_audio only exists when the verdict had something to play (v3's catalog may have no ear verdict lines)
  const want = ["press_ack", "photo", "scan_rtt", "press_to_verdict", ...(verdictId ? ["press_to_audio"] : [])]
  const hops = () => rx.monitor.json.filter((m) => m.type === "latency" && m.source === "glasses").map((m) => m.hop)
  await until(() => want.every((h) => hops().includes(h)), 3000, "latency on the monitor")
  await until(() => fake.status.last?.scanning === false, 2000, "scan done")
})

test("real Hive: outside scan beats the line the Hive names is spoken from the catalog (pre, then hornet)", async () => {
  const fake = await boot()
  await beat(fake, "pre")
  fake.press()
  await until(() => spoken(fake).length === 1, 3000, "pre line")
  expect(earTexts()).toContain(spoken(fake)[0])

  await beat(fake, "hornet")
  fake.press()
  await until(() => spoken(fake).length === 2, 3000, "not-now line")
  expect(earTexts()).toContain(spoken(fake)[1])
  expect(fake.photos).toHaveLength(0)
})

test("real Hive: a board say.force on the ear channel plays in the glasses", async () => {
  const fake = await boot()
  const line = Object.values(hiveLines.LINES).find((l) => l.channel === "ear" && !l.tags?.includes("filler"))
  if (!line) return console.warn("SKIP: the Hive's catalog has no ear lines")
  board.send(JSON.stringify({type: "say.force", line_id: line.id, channel: "ear"}))
  await until(() => fake.audio.length === 1, 3000, "forced ear line")
  expect(fake.audio[0]).toMatchObject(LINES[line.id] ? {kind: "clip", line: line.id} : {kind: "speak", text: line.text})
})

test("real Hive: /glasses/utterance answers in the UtteranceResponse shape and refuses empty text", async () => {
  const post = (body: unknown) =>
    fetch(`${hive.url}/glasses/utterance?token=${TOKEN}`, {method: "POST", headers: {"content-type": "application/json"}, body: JSON.stringify(body)})
  expect((await post({text: "", at: Date.now()})).status).toBe(400)
  expect((await post({text: "   "})).status).toBe(400)
  const res = await post({text: "put it out", at: Date.now()})
  expect(res.status).toBe(200)
  const body = (await res.json()) as Record<string, unknown>
  expect(Object.keys(body).sort()).toEqual(["audio_url", "caption", "intent", "line_id", "topic"])
  expect(body.intent).toBe("undo")
  expect(typeof body.caption).toBe("string")
  const bad = await fetch(`${hive.url}/glasses/utterance?token=nope`, {method: "POST", headers: {"content-type": "application/json"}, body: "{}"})
  expect(bad.status).toBe(401)
})

test("real Hive + Quest: a what-if from the glasses mic routes by keyword; Beeswax answers in the room, not the ear", async () => {
  const fake = await boot()
  await beat(fake, "whatif")
  for (const r of Object.values(rx)) (r.json.length = 0), (r.bin.length = 0)
  fake.say("What if a fire starts?")
  await until(() => fake.status.last?.heard?.text === "What if a fire starts?", 8000, "utterance answered")
  expect(fake.status.last!.heard!.intent).toBe("what_if")
  expect(modelCalls).toBe(0) // the keyword fast path: no model call

  const mon = () => rx.monitor.json
  await until(() => mon().some((m) => m.type === "intent"), 2000, "intent on the monitor")
  expect(mon().find((m) => m.type === "transcript")).toMatchObject({text: "What if a fire starts?", final: true, source: "glasses"})
  expect(mon().find((m) => m.type === "intent")).toMatchObject({name: "what_if", topic: "fire", path: "keyword"})
  if (hiveLines.LINES.room_whatif_fire) {
    await until(() => rx.quest.json.some((m) => m.type === "say"), 2000, "room line on the Quest")
    expect(rx.quest.json.find((m) => m.type === "say")).toMatchObject({channel: "room", line_id: "room_whatif_fire"})
  }
  await until(() => rx.quest.json.some((m) => m.type === "orbis" && m.scene === "whatif_fire"), 2000, "window catches fire")
  await sleep(150)
  expect(fake.audio).toEqual([]) // one line, one device: the room line is the Quest's

  // The glasses mic hearing that room line across the table must not become another utterance.
  const roomSay = rx.quest.json.find((m) => m.type === "say" && m.caption)
  if (roomSay) {
    const before = rx.monitor.json.filter((m) => m.type === "transcript").length
    for (const piece of [roomSay.caption, ...String(roomSay.caption).split(/[.?!]+/)]) if (piece.trim()) fake.say(piece)
    await sleep(300)
    expect(rx.monitor.json.filter((m) => m.type === "transcript").length).toBe(before)
  }

  fake.say("Put it out!")
  await until(() => fake.status.last?.heard?.text === "Put it out!", 8000, "undo answered")
  expect(fake.status.last!.heard!.intent).toBe("undo")
  await until(() => rx.quest.json.some((m) => m.type === "orbis" && m.scene === "fire_out"), 2000, "fire out")
  expect(modelCalls).toBe(0)
  expect(fake.audio).toEqual([])
})

test("real Hive, no Quest: the glasses alone hear Beeswax's answer in the ear (keyword and offline-Claude paths)", async () => {
  const solo = startRealHive()
  const board = new WebSocket(`ws://127.0.0.1:${solo.port}/bus?role=board&token=${TOKEN}`)
  try {
    await new Promise((resolve, reject) => ((board.onopen = resolve), (board.onerror = reject)))
    const fake = fakeSession({photoUrl})
    app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: [solo.url]}))
    await until(() => fake.status.last?.bus === "open" && fake.status.last.beat !== null, 5000, "bus + welcome")
    board.send(JSON.stringify({type: "beat.request", n: BEATS.indexOf("whatif")})) // no Quest: the Hive is the clock
    await until(() => fake.status.last?.beat === "whatif", 2000, "beat whatif")

    fake.say("What if a fire starts?")
    await until(() => fake.audio.length === 1, 4000, "ear answer")
    const text = hiveLines.lineText("room_whatif_fire")
    const twin = text ? hiveLines.lineIdForText(text, "ear") : null
    if (twin && LINES[twin]) expect(fake.audio[0]).toMatchObject({kind: "clip", line: twin})
    else if (text) expect(fake.audio[0]).toMatchObject({kind: "speak", text: twin ? CAPTIONS[twin] : text})
    expect(modelCalls).toBe(0)

    // Not a keyword line: the router asks Claude (offline here), falls back to unknown, and Beeswax acks.
    fake.say("Tell me about the queen")
    await until(() => fake.status.last?.heard?.text === "Tell me about the queen", 6000, "unknown answered")
    expect(fake.status.last!.heard!.intent).toBe("unknown")
    expect(modelCalls).toBe(1)
    const acks = hiveLines.ACK_LINES.map((id) => hiveLines.lineText(id))
    if (acks.length) {
      await until(() => fake.audio.length === 2, 2000, "ack in the ear")
      expect(acks).toContain((fake.audio[1] as {text?: string}).text ?? "")
    }
  } finally {
    board.close()
    app?.stop()
    app = null
    await solo.stop()
  }
})

test("real Hive: a wrong token is refused and the ear says so", async () => {
  const fake = fakeSession({photoUrl})
  app = start(fake.session, makeConfig({hiveToken: "wrong", hosts: [hive.url], giveUpMs: 5000}))
  fake.press()
  await until(() => spoken(fake).length === 1, 4000, "no-signal line")
  expect(spoken(fake)).toEqual(["I can't reach the hive. Check the hotspot."])
  expect(fake.status.last?.log.some((l) => l.includes("HTTP 401"))).toBe(true)
  expect(fake.status.last?.log.some((l) => l.includes("wrong"))).toBe(false) // token never logged
})

test("real Hive: the primary-host probe asks a route the Hive really serves", async () => {
  const urls: string[] = []
  const client = new HiveClient([hive.url], TOKEN, () => {}, (url, init) => (urls.push(url), fetch(url, init)))
  expect(await client.probe(0)).toBe(true)
  expect((await fetch(urls[0]!)).status).toBe(200) // not a 404 that only happens to be "< 500"
})

test("real Hive: a scan it lost outside a scan beat is a 409 whose body names a line the glasses can say", async () => {
  const fake = await boot()
  await beat(fake, "hornet")
  const res = await fetch(`${hive.url}/glasses/scan?token=${TOKEN}`, {
    method: "POST",
    headers: {"content-type": "application/json"},
    body: JSON.stringify({scan_id: "gone", heard: "roses", pressed_at: Date.now()}),
  })
  expect(res.status).toBe(409)
  const body = (await res.json()) as {line_id?: string}
  expect(CAPTIONS[body.line_id ?? ""]).toBeTruthy()
})
