// The glasses app against the REAL Hive (hive/src/server.ts + the real line catalog), with a
// mocked Claude director and no TTS. Catches contract drift the stub Hive in flow.test.ts can't.
// No internet, no paid API, no speaker: the photo comes from a local server.
import {afterAll, afterEach, beforeAll, expect, test} from "bun:test"
import {setQuiet} from "../../hive/src/bus"
import * as hiveLines from "../../hive/src/lines"
import {BEATS, type Channel, type Item, type Verdict} from "../../hive/src/protocol"
import {startHive, type Hive, type JudgeInputLike} from "../../hive/src/server"
import {start} from "../src/background/app"
import {Hive as HiveClient} from "../src/background/hive"
import {makeConfig} from "../src/background/config"
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
const rx: Record<"quest" | "monitor", Rx> = {quest: {json: [], bin: []}, monitor: {json: [], bin: []}}

function client(role: "quest" | "monitor"): Promise<WebSocket> {
  const ws = new WebSocket(`ws://127.0.0.1:${hive.port}/bus?role=${role}&token=${TOKEN}`)
  ws.binaryType = "arraybuffer"
  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") rx[role].json.push(JSON.parse(ev.data))
    else rx[role].bin.push(new Uint8Array(ev.data as ArrayBuffer))
  }
  return new Promise((resolve) => (ws.onopen = () => resolve(ws)))
}

beforeAll(async () => {
  photos = Bun.serve({port: 0, hostname: "127.0.0.1", fetch: () => new Response(JPEG, {headers: {"content-type": "image/jpeg"}})})
  photoUrl = `http://127.0.0.1:${photos.port}/p.jpg`
  hive = startHive({
    deps: {
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
    },
    port: 0,
    hostname: "127.0.0.1",
    token: TOKEN,
    heartbeatMs: 60_000,
    host: "test",
  })
  quest = await client("quest")
  monitor = await client("monitor")
})

afterAll(async () => {
  quest.close()
  monitor.close()
  await hive.stop()
  photos.stop(true)
})

let app: {stop(): void} | null = null
afterEach(() => {
  app?.stop()
  app = null
  judgeCalls.length = 0
  for (const r of Object.values(rx)) (r.json.length = 0), (r.bin.length = 0)
})

async function boot(opts: FakeOptions = {}) {
  const fake = fakeSession({photoUrl, ...opts})
  const cfg = makeConfig({hiveToken: TOKEN, hosts: [hive.url], heardWindowMs: 300, heardLookbackMs: 500, giveUpMs: 8000})
  app = start(fake.session, cfg)
  await until(() => fake.status.last?.bus === "open" && fake.status.last.beat !== null, 5000, "bus + welcome")
  return fake
}

async function beat(fake: ReturnType<typeof fakeSession>, name: string) {
  quest.send(JSON.stringify({type: "beat.set", n: BEATS.indexOf(name as any), name}))
  await until(() => fake.status.last?.beat === name, 2000, `beat ${name}`)
}

const spoken = (fake: ReturnType<typeof fakeSession>) => fake.audio.filter((a) => a.kind === "speak").map((a: any) => a.text)

test("real Hive: press -> photo + heard -> Quest gets started/photo/verdict, the ear gets the verdict", async () => {
  const fake = await boot({photoMs: 100})
  await beat(fake, "beeline")
  fake.press()
  await sleep(20)
  fake.say("what do you think of this one")
  await until(() => spoken(fake).length > 0, 5000, "verdict line")

  expect(fake.audio[0]).toMatchObject({kind: "clip", line: "ear_hold_still"})
  expect(spoken(fake)).toEqual([hiveLines.lineText("ear_verdict_purple_bee_flower")])
  expect(judgeCalls).toHaveLength(1)
  expect(judgeCalls[0]!.photo?.length).toBeGreaterThan(100) // the Hive fetched our photo_url
  expect(judgeCalls[0]!.heard).toBe("what do you think of this one")

  const types = rx.quest.json.map((m) => m.type)
  expect(types).toEqual(expect.arrayContaining(["scan.started", "scan.photo", "scan.verdict"]))
  expect(rx.quest.bin.length).toBe(1) // the JPEG after scan.photo
  const verdict = rx.quest.json.find((m) => m.type === "scan.verdict")
  expect(verdict).toMatchObject({item: "purple_bee_flower", verdict: "good", socket: 2, path: "claude"})

  await until(() => rx.monitor.json.filter((m) => m.type === "latency").length >= 5, 3000, "latency on the monitor")
  const hops = rx.monitor.json.filter((m) => m.type === "latency" && m.source === "glasses").map((m) => m.hop)
  expect(hops).toEqual(expect.arrayContaining(["press_ack", "photo", "scan_rtt", "press_to_verdict", "press_to_audio"]))
  await until(() => fake.status.last?.scanning === false, 2000, "scan done")
})

test("real Hive: outside scan beats the Hive's line id is spoken (ear_practice in pre, ear_not_now later)", async () => {
  const fake = await boot()
  await beat(fake, "pre")
  fake.press()
  await until(() => spoken(fake).length === 1, 3000, "practice line")
  expect(spoken(fake)).toEqual([hiveLines.lineText("ear_practice")])

  await beat(fake, "hornet")
  fake.press()
  await until(() => spoken(fake).length === 2, 3000, "not-now line")
  expect(spoken(fake)[1]).toBe(hiveLines.lineText("ear_not_now"))
  expect(fake.photos).toHaveLength(0)
})

test("real Hive: plea goes to the Quest; swarm.arrived plays the landing line in the ear", async () => {
  const fake = await boot()
  await beat(fake, "plea")
  fake.say("please stay for my garden")
  await until(() => rx.quest.json.some((m) => m.type === "plea"), 3000, "plea on the Quest")
  expect(rx.quest.json.find((m) => m.type === "plea")).toEqual({type: "plea", text: "please stay for my garden"})

  quest.send(JSON.stringify({type: "swarm.arrived", socket: 2}))
  await until(() => spoken(fake).length === 1, 3000, "landing line")
  expect(spoken(fake)).toEqual([hiveLines.lineText("ear_swarm_arrived")])
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

test("real Hive: a scan it lost outside a scan beat is a 409 whose body names the line scan.ts plays", async () => {
  const fake = await boot()
  await beat(fake, "hornet")
  const res = await fetch(`${hive.url}/glasses/scan?token=${TOKEN}`, {
    method: "POST",
    headers: {"content-type": "application/json"},
    body: JSON.stringify({scan_id: "gone", heard: "roses", pressed_at: Date.now()}),
  })
  expect(res.status).toBe(409)
  expect(await res.json()).toMatchObject({line_id: "ear_not_now"})
})
