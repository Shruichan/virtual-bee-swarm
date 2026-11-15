// Live Bee Scout on the glasses: the real app with a fake session, against the REAL hive bus and stub
// /scout/* + /glasses/* HTTP endpoints. No internet, no speaker.
import {afterAll, afterEach, beforeAll, describe, expect, test} from "bun:test"
import {Bus as HiveBus, setQuiet} from "../../hive/src/bus"
import {createState} from "../../hive/src/state"
import {start} from "../src/background/app"
import {makeConfig, type LocalConfig} from "../src/background/config"
import {Scout, scoutLineId} from "../src/background/scout"
import {fakeSession, sleep, until} from "./fakes"

const TOKEN = "test-token"
setQuiet(true)

const state = createState()
const hiveBus = new HiveBus(state, {lineText: () => "", onVerdictForce() {}, onPlea() {}}, {token: TOKEN, host: "test", heartbeatMs: 60_000})

type Hit = {path: string; body: any}
let hits: Hit[] = []
let server: ReturnType<typeof Bun.serve>
let base = ""

beforeAll(() => {
  server = Bun.serve({
    port: 0,
    hostname: "127.0.0.1",
    websocket: hiveBus.websocket as any,
    async fetch(req, srv) {
      const url = new URL(req.url)
      if (url.pathname === "/bus") return hiveBus.upgrade(req, srv as any) ?? undefined
      if (url.searchParams.get("token") !== TOKEN) return new Response("bad token", {status: 401})
      const body = req.method === "POST" ? await req.json().catch(() => null) : null
      hits.push({path: url.pathname, body})
      if (url.pathname === "/glasses/press") return Response.json({allowed: false, scan_id: null, line_id: null})
      if (url.pathname === "/glasses/utterance") return Response.json({intent: "unknown", topic: null, line_id: null, audio_url: null, caption: ""})
      return Response.json({ok: true})
    },
  })
  hiveBus.start()
  base = `http://127.0.0.1:${server.port}`
})

afterAll(() => {
  hiveBus.stop()
  server.stop(true)
})

let app: {stop(): void} | null = null
afterEach(() => {
  app?.stop()
  app = null
  hits = []
})

const paths = () => hits.map((h) => h.path)

async function boot(cfg: Partial<LocalConfig> = {}) {
  const fake = fakeSession()
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: [base], fieldGuideMode: false, ...cfg}))
  await until(() => fake.status.last?.bus === "open", 5000, "bus open")
  return fake
}

const sayOnBus = (tts_id: string | null, caption: string, audio_url: string | null = null) =>
  hiveBus.broadcast({type: "say", channel: "ear", line_id: null, tts_id, audio_url, caption}, ["glasses"])

describe("scout mode", () => {
  const SCOUT = {scoutMode: true, streamUrl: "rtmp://127.0.0.1:1935/scout"}

  test("bus open -> POST /scout/start once; the camera stream starts", async () => {
    const fake = await boot(SCOUT)
    await until(() => paths().includes("/scout/start"), 2000, "/scout/start")
    expect(typeof hits.find((h) => h.path === "/scout/start")!.body.at).toBe("number")
    await until(() => fake.streams.some((s) => s.op === "start"), 2000, "stream start")
    expect((fake.streams[0]!.opts as any).direct).toBe(SCOUT.streamUrl)
    await sleep(100)
    expect(paths().filter((p) => p === "/scout/start").length).toBe(1)
  })

  test("short press -> /scout/button (not the scanner); long press only replays", async () => {
    const fake = await boot(SCOUT)
    await until(() => paths().includes("/scout/start"), 2000, "/scout/start")
    fake.press("short")
    await until(() => paths().includes("/scout/button"), 2000, "/scout/button")
    fake.press("long")
    await sleep(150)
    expect(paths().filter((p) => p === "/scout/button").length).toBe(1)
    expect(paths()).not.toContain("/glasses/press")
    expect(paths()).not.toContain("/glasses/scan")
    expect(fake.photos).toEqual([])
  })

  test("every final transcript -> /scout/utterance; interims and /glasses/utterance never", async () => {
    const fake = await boot(SCOUT)
    await until(() => paths().includes("/scout/start"), 2000, "/scout/start")
    fake.say("yes ple", false)
    fake.say("yes please")
    fake.say("no") // a short filler word Voice would drop: the game still needs it
    await until(() => paths().filter((p) => p === "/scout/utterance").length === 2, 2000, "two utterances")
    expect(hits.filter((h) => h.path === "/scout/utterance").map((h) => h.body.text)).toEqual(["yes please", "no"])
    await sleep(100)
    expect(paths()).not.toContain("/glasses/utterance")
  })

  test("the ear plays scout says; LED orange for the chime, green for a fact, nothing for the rest", async () => {
    const fake = await boot(SCOUT)
    sayOnBus("scout_chime", "Bee fact found!")
    sayOnBus("scout_hook_beehive", "A hive. Union headquarters. Want the gossip?")
    await until(() => fake.audio.length >= 2, 3000, "chime + hook audio")
    expect(fake.audio.map((a) => (a.kind === "speak" ? a.text : a.kind))).toEqual(["Bee fact found!", "A hive. Union headquarters. Want the gossip?"])
    expect(fake.leds).toEqual([{color: "orange", ontime: 300, offtime: 200, count: 4}])

    const url = `${base}/scout/audio/scout_fact_beehive.mp3`
    sayOnBus(null, "One queen lays the colony's eggs.", url) // id from the URL alone
    sayOnBus("scout_progress_1", "One down. Five to go.")
    await until(() => fake.audio.length >= 4, 3000, "fact + progress audio")
    expect(fake.audio[2]).toMatchObject({kind: "url", url})
    expect(fake.leds).toEqual([
      {color: "orange", ontime: 300, offtime: 200, count: 4},
      {color: "green", ontime: 3000, offtime: 0, count: 1},
    ])
  })
})

describe("non-scout mode is unchanged", () => {
  test("press goes to the scanner, no /scout calls, no stream", async () => {
    const fake = await boot({scoutMode: false})
    await sleep(100)
    fake.press("short")
    await until(() => paths().includes("/glasses/press"), 2000, "/glasses/press")
    fake.say("yes please")
    sayOnBus("scout_chime", "Bee fact found!")
    await until(() => fake.audio.length >= 1, 3000, "audio")
    await sleep(100)
    expect(paths().some((p) => p.startsWith("/scout/"))).toBe(false)
    expect(fake.streams).toEqual([])
    expect(fake.leds.some((l) => (l as any)?.color === "orange" && (l as any).count === 4)).toBe(false)
  })
})

describe("Scout unit", () => {
  test("scoutLineId", () => {
    expect(scoutLineId({tts_id: "scout_chime"})).toBe("scout_chime")
    expect(scoutLineId({tts_id: null, audio_url: "https://h/scout/audio/scout_fact_bee_hotel.mp3"})).toBe("scout_fact_bee_hotel")
    expect(scoutLineId({tts_id: "abc", audio_url: "https://h/audio/123.mp3"})).toBe(null)
    expect(scoutLineId({})).toBe(null)
  })

  test("busOpen starts only once (a reconnect keeps progress)", () => {
    const sent: string[] = []
    const s = new Scout({hive: {send: async (p: string) => (sent.push(p), true)}, led: {turnOn: async () => ({}) as any}, log: () => {}})
    s.busOpen()
    s.busOpen()
    s.heard({text: "  ", isFinal: true})
    s.heard({text: "sure", isFinal: false})
    expect(sent).toEqual(["/scout/start"])
  })

  test("LED failures are swallowed and it gives up after three", async () => {
    let calls = 0
    const s = new Scout({hive: {send: async () => true}, led: {turnOn: () => (calls++, Promise.reject(new Error("no led")))}, log: () => {}})
    for (let i = 0; i < 5; i++) {
      s.said({tts_id: "scout_chime"})
      await sleep(5)
    }
    expect(calls).toBe(3)
  })
})
