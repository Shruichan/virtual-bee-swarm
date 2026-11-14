// End to end against a local fake Hive: the REAL hive bus (hive/src/bus.ts) for the websocket,
// plus stub /glasses/* HTTP endpoints. The glasses side is the real app with a fake session.
// Nothing here touches the internet, a paid API, or a speaker.
import {afterAll, afterEach, beforeAll, expect, test} from "bun:test"
import {Bus as HiveBus, setQuiet} from "../../hive/src/bus"
import {SCAN_BEATS, BEATS} from "../../hive/src/protocol"
import {createState} from "../../hive/src/state"
import {start} from "../src/background/app"
import {makeConfig, type LocalConfig} from "../src/background/config"
import {fakeSession, sleep, until, type FakeOptions} from "./fakes"

const TOKEN = "test-token"
setQuiet(true)

const state = createState()
const hiveBus = new HiveBus(
  state,
  {
    lineText: (id) => (id === "ear_swarm_arrived" ? "They're on your flower. Don't panic." : ""),
    onVerdictForce() {},
    onPlea() {},
  },
  {token: TOKEN, host: "test", heartbeatMs: 60_000},
)

type Hit = {path: string; body: any; at: number}
let hits: Hit[] = []
let scanDelayMs = 0
let scanRefused = false // the real Hive answers 409 + a not-now ScanResponse when it lost the scan
const scanReply = {
  item: "purple_bee_flower",
  verdict: "good",
  line_id: "ear_verdict_purple", // not embedded in the placeholder lines -> TTS of the caption
  audio_url: null,
  caption: "Finally. Food.",
  path: "claude",
  ms: 0,
}

let server: ReturnType<typeof Bun.serve>
let base = ""
let quest: WebSocket

beforeAll(async () => {
  server = Bun.serve({
    port: 0,
    hostname: "127.0.0.1",
    websocket: hiveBus.websocket as any,
    async fetch(req, srv) {
      const url = new URL(req.url)
      if (url.pathname === "/bus") return hiveBus.upgrade(req, srv as any) ?? undefined
      if (url.searchParams.get("token") !== TOKEN) return new Response("bad token", {status: 401})
      const body = req.method === "POST" ? await req.json() : null
      hits.push({path: url.pathname, body, at: Date.now()})
      switch (url.pathname) {
        case "/glasses/press": {
          const allowed = SCAN_BEATS.includes(BEATS[state.beat]!)
          return Response.json({allowed, scan_id: allowed ? `s${hits.length}` : null, line_id: allowed ? null : "ear_not_now"})
        }
        case "/glasses/scan":
          await sleep(scanDelayMs)
          if (scanRefused) {
            const notNow = {item: "unclear", verdict: "roast", line_id: "ear_not_now", audio_url: null, caption: "", path: "operator", ms: 0}
            return Response.json(notNow, {status: 409})
          }
          return Response.json(scanReply)
        default:
          return Response.json({ok: true})
      }
    },
  })
  hiveBus.start()
  base = `http://127.0.0.1:${server.port}`
  quest = new WebSocket(`ws://127.0.0.1:${server.port}/bus?role=quest&token=${TOKEN}`)
  await new Promise((r) => (quest.onopen = r))
})

afterAll(() => {
  quest.close()
  hiveBus.stop()
  server.stop(true)
})

let app: {stop(): void} | null = null
afterEach(() => {
  app?.stop()
  app = null
  hits = []
  scanDelayMs = 0
  scanRefused = false
})

const TEST_CONFIG: Omit<LocalConfig, "hiveToken"> = {
  heardWindowMs: 300,
  heardLookbackMs: 500,
  stallMs: 2000,
  pressTimeoutMs: 1000,
  scanTimeoutMs: 2000,
  giveUpMs: 6000,
  photo: {timeoutMs: 2000},
}

async function boot(opts: FakeOptions = {}, cfg: Partial<LocalConfig> = {}) {
  const fake = fakeSession(opts)
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: [base], ...TEST_CONFIG, ...cfg}))
  await until(() => fake.status.last?.bus === "open", 5000, "bus open")
  await until(() => fake.status.last?.beat != null, 2000, "welcome")
  return fake
}

async function questBeat(fake: ReturnType<typeof fakeSession>, name: string) {
  quest.send(JSON.stringify({type: "beat.set", n: BEATS.indexOf(name as any), name}))
  await until(() => fake.status.last?.beat === name, 2000, `beat ${name}`)
}

const paths = () => hits.map((h) => h.path)
const hops = () => hits.filter((h) => h.path === "/glasses/latency").map((h) => h.body.hop)

test("scan: hold-still first, then press, photo + heard in one scan, then the verdict", async () => {
  const fake = await boot({photoMs: 300})
  await questBeat(fake, "beeline")
  expect(fake.warmUps.length).toBeGreaterThan(0) // camera warmed when the scan beat started

  fake.press()
  await sleep(50)
  fake.say("it's a purple", false)
  await sleep(50)
  fake.say("it's a purple salvia")
  await until(() => fake.audio.some((a) => a.kind === "speak" && a.text === "Finally. Food."), 4000, "verdict audio")

  expect(fake.audio[0]).toMatchObject({kind: "clip", line: "ear_hold_still"})
  const press = hits.find((h) => h.path === "/glasses/press")!
  expect(fake.audio[0]!.at).toBeLessThanOrEqual(press.at) // the ear line didn't wait for the network
  expect(press.body).toMatchObject({button: "main", press_type: "short"})

  const scans = hits.filter((h) => h.path === "/glasses/scan")
  expect(scans).toHaveLength(1)
  expect(scans[0]!.body).toMatchObject({
    scan_id: expect.stringMatching(/^s\d+$/),
    photo_url: "https://photos.example/p1.jpg",
    pressed_at: press.body.pressed_at,
  })
  expect(scans[0]!.body.heard).toContain("salvia")

  await until(() => hops().includes("press_to_audio"), 2000, "latency hops")
  expect(hops()).toEqual(expect.arrayContaining(["press_ack", "photo", "scan_rtt", "press_to_verdict", "press_to_audio"]))
  expect(fake.leds[0]).toMatchObject({color: "orange"})
  expect(fake.leds).toContainEqual(expect.objectContaining({color: "green"}))
  await until(() => fake.status.last?.scanning === false, 1000, "scan done")
})

test("outside a scan beat: no hold-still, no photo, the Hive's line plays", async () => {
  const fake = await boot()
  await questBeat(fake, "hornet")
  fake.press()
  await until(() => fake.audio.some((a) => a.kind === "speak"), 3000, "not-now line")
  expect(fake.audio).toEqual([expect.objectContaining({kind: "speak", text: "Not now, I'm monologuing."})])
  expect(fake.photos).toHaveLength(0)
  expect(paths()).not.toContain("/glasses/scan")
})

test("scan refused with 409: says the Hive's line, not 'lost the signal'", async () => {
  const fake = await boot()
  await questBeat(fake, "roses")
  scanRefused = true
  fake.press()
  await until(() => fake.audio.some((a) => a.kind === "speak"), 4000, "refusal line")
  expect(fake.audio.filter((a) => a.kind === "speak")).toEqual([expect.objectContaining({text: "Not now, I'm monologuing."})])
  await until(() => fake.status.last?.scanning === false, 1000, "scan reset")
})

test("presses while a scan is in flight are ignored", async () => {
  const fake = await boot()
  await questBeat(fake, "roses")
  fake.press()
  fake.press()
  await sleep(30)
  fake.press()
  await until(() => paths().includes("/glasses/scan"), 4000, "scan")
  await sleep(100)
  expect(paths().filter((p) => p === "/glasses/press")).toHaveLength(1)
  expect(fake.photos).toHaveLength(1)
})

test("slow photo: the second hold-still line fills the gap", async () => {
  const fake = await boot({photoMs: 1200}, {stallMs: 500})
  await questBeat(fake, "offlist")
  fake.press()
  await until(() => fake.audio.some((a) => a.kind === "speak" && a.text === "Finally. Food."), 5000, "verdict")
  expect(fake.audio.map((a) => (a.kind === "clip" ? a.line : a.kind))).toEqual(["ear_hold_still", "ear_hold_still_2", "speak"])
})

test("photo in and nobody talking: scan goes now; mid-sentence: waits for the final", async () => {
  const fake = await boot({photoMs: 100}, {heardWindowMs: 2500})
  await questBeat(fake, "beeline")
  fake.press()
  await until(() => paths().includes("/glasses/scan"), 2000, "scan")
  const quiet = hits.find((h) => h.path === "/glasses/scan")!
  expect(quiet.at - quiet.body.pressed_at).toBeLessThan(1000) // didn't sit out the 2.5 s window
  expect(quiet.body.heard).toBeUndefined()
  await until(() => fake.status.last?.scanning === false, 1000, "scan done")

  hits = []
  fake.press()
  await sleep(20)
  fake.say("it's some", false)
  await sleep(400) // photo is in by now, but they're mid-sentence
  expect(paths()).not.toContain("/glasses/scan")
  fake.say("it's some clover")
  await until(() => paths().includes("/glasses/scan"), 1000, "scan after the final")
  expect(hits.find((h) => h.path === "/glasses/scan")!.body.heard).toBe("it's some clover")
})

test("photo failure still scans with what was heard", async () => {
  const fake = await boot({photoFails: true})
  await questBeat(fake, "field")
  fake.press()
  await sleep(30)
  fake.say("this is a rose")
  await until(() => paths().includes("/glasses/scan"), 4000, "scan")
  const scan = hits.find((h) => h.path === "/glasses/scan")!
  expect(scan.body.photo_url).toBeUndefined()
  expect(scan.body.heard).toBe("this is a rose")
})

test("speech-first mode answers from the words, then sends the photo for the polaroid", async () => {
  const fake = await boot({photoMs: 800}, {speechFirst: true, heardWindowMs: 2000})
  await questBeat(fake, "roses")
  fake.press()
  await sleep(50)
  fake.say("roses") // a final utterance ends the listening window early
  await until(() => fake.audio.some((a) => a.kind === "speak"), 3000, "verdict")
  const first = hits.filter((h) => h.path === "/glasses/scan")
  expect(first).toHaveLength(1)
  expect(first[0]!.body).toMatchObject({heard: "roses"})
  expect(first[0]!.body.photo_url).toBeUndefined()
  await until(() => hits.filter((h) => h.path === "/glasses/scan").length === 2, 3000, "photo follow-up")
  const second = hits.filter((h) => h.path === "/glasses/scan")[1]!
  expect(second.body).toMatchObject({scan_id: first[0]!.body.scan_id, photo_url: "https://photos.example/p1.jpg"})
  expect(fake.audio.filter((a) => a.kind === "speak")).toHaveLength(1)
})

test("ear lines from the bus play; long press replays the last one", async () => {
  const fake = await boot()
  quest.send(JSON.stringify({type: "swarm.arrived", socket: 2}))
  await until(() => fake.audio.length === 1, 3000, "swarm line")
  expect(fake.audio[0]).toMatchObject({kind: "speak", text: "They're on your flower. Don't panic."})
  fake.press("long")
  await until(() => fake.audio.length === 2, 2000, "replay")
  expect(fake.audio[1]).toMatchObject({kind: "speak", text: "They're on your flower. Don't panic."})
  fake.touch("double_tap")
  await until(() => fake.audio.length === 3, 2000, "touch replay")
})

test("plea: final transcripts during the plea beat go to /glasses/plea", async () => {
  const fake = await boot()
  await questBeat(fake, "plea")
  fake.say("please stay", false) // interim: not sent
  fake.say("please stay, my garden needs you")
  await until(() => paths().includes("/glasses/plea"), 2000, "plea")
  await questBeat(fake, "buffer")
  fake.say("and the clover") // within the grace window
  await until(() => paths().filter((p) => p === "/glasses/plea").length === 2, 2000, "grace plea")
  expect(hits.filter((h) => h.path === "/glasses/plea").map((h) => h.body.text)).toEqual([
    "please stay, my garden needs you",
    "and the clover",
  ])
})

test("failover: a dead first host falls through to the live one for HTTP and the bus", async () => {
  const fake = fakeSession()
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: ["http://127.0.0.1:9", base], ...TEST_CONFIG}))
  await until(() => fake.status.last?.bus === "open", 8000, "bus on the second host")
  expect(fake.status.last?.host).toBe(base)
  await questBeat(fake, "beeline")
  fake.press()
  await until(() => paths().includes("/glasses/scan"), 4000, "scan on the second host")
})

test("hive unreachable: says so and the next press works", async () => {
  const fake = fakeSession()
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: ["http://127.0.0.1:9"], ...TEST_CONFIG}))
  fake.press()
  await until(() => fake.audio.some((a) => a.kind === "speak"), 4000, "no-signal line")
  expect(fake.audio.at(-1)).toMatchObject({text: "I can't reach the hive. Check the hotspot."})
  await until(() => fake.status.last?.scanning === false, 1000, "scan reset")
  fake.press()
  await until(() => fake.audio.filter((a) => a.kind === "speak").length === 2, 4000, "second press handled")
})
