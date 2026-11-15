// End to end against a local fake Hive: the REAL hive bus (hive/src/bus.ts) for the websocket,
// plus stub /glasses/* HTTP endpoints. The glasses side is the real app with a fake session.
// Nothing here touches the internet, a paid API, or a speaker.
import {afterAll, afterEach, beforeAll, expect, test} from "bun:test"
import {Bus as HiveBus, setQuiet} from "../../hive/src/bus"
import {BEATS, SCAN_BEATS, VOICE_BEATS, type BusMessage} from "../../hive/src/protocol"
import {createState} from "../../hive/src/state"
import {start} from "../src/background/app"
import {makeConfig, type LocalConfig} from "../src/background/config"
import {CAPTIONS, FILLERS, LINES} from "../src/background/lines"
import {expectedEarLine, fakeSession, heardEarLine, sleep, until, type AudioOp, type FakeOptions} from "./fakes"

const TOKEN = "test-token"
setQuiet(true)

const state = createState()
const hiveBus = new HiveBus(state, {lineText: () => "", onVerdictForce() {}, onPlea() {}}, {token: TOKEN, host: "test", heartbeatMs: 60_000})

/** The Hive's "not now" ear line; falls back to a glasses-only id if the catalog renamed it. */
const NOT_NOW = CAPTIONS.ear_not_now ? "ear_not_now" : "ear_scan_failed"

type Hit = {path: string; body: any; at: number}
let hits: Hit[] = []
let scanDelayMs = 0
let scanRefused = false // the real Hive answers 409 + a not-now ScanResponse when it lost the scan
const scanReply = {
  item: "purple_bee_flower",
  verdict: "good",
  line_id: "ear_verdict_not_embedded", // not embedded -> TTS of the caption
  audio_url: null,
  caption: "Finally. Food.",
  path: "claude",
  ms: 0,
}
const FIRE_REPLY = {intent: "what_if", topic: "fire", line_id: null, audio_url: null, caption: "Don't. Say. That."}
let utteranceReply: unknown = FIRE_REPLY
let utteranceStatus = 200
let utteranceDelayMs = 0

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
          return Response.json({allowed, scan_id: allowed ? `s${hits.length}` : null, line_id: allowed ? null : NOT_NOW})
        }
        case "/glasses/scan":
          await sleep(scanDelayMs)
          if (scanRefused) {
            const notNow = {item: "unclear", verdict: "roast", line_id: NOT_NOW, audio_url: null, caption: "", path: "operator", ms: 0}
            return Response.json(notNow, {status: 409})
          }
          return Response.json(scanReply)
        case "/glasses/utterance":
          await sleep(utteranceDelayMs)
          return Response.json(utteranceReply, {status: utteranceStatus})
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
  utteranceReply = FIRE_REPLY
  utteranceStatus = 200
  utteranceDelayMs = 0
})

const TEST_CONFIG: Omit<LocalConfig, "hiveToken"> = {
  heardWindowMs: 300,
  heardLookbackMs: 500,
  stallMs: 2000,
  pressTimeoutMs: 1000,
  scanTimeoutMs: 2000,
  giveUpMs: 6000,
  utteranceTimeoutMs: 1500,
  photo: {timeoutMs: 2000},
}

async function boot(opts: FakeOptions = {}, cfg: Partial<LocalConfig> = {}) {
  const fake = fakeSession(opts)
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: [base], ...TEST_CONFIG, ...cfg}))
  await until(() => fake.status.last?.bus === "open", 5000, "bus open")
  await until(() => fake.status.last?.beat != null, 2000, "welcome")
  return fake
}

async function questBeat(fake: ReturnType<typeof fakeSession>, name: (typeof BEATS)[number]) {
  quest.send(JSON.stringify({type: "beat.set", n: BEATS.indexOf(name), name}))
  await until(() => fake.status.last?.beat === name, 2000, `beat ${name}`)
}

const paths = () => hits.map((h) => h.path)
const hops = () => hits.filter((h) => h.path === "/glasses/latency").map((h) => h.body.hop)
const utterances = () => hits.filter((h) => h.path === "/glasses/utterance").map((h) => h.body.text)
/** What a played op was: the line id of an embedded clip, the url, or the spoken words. */
const label = (a: AudioOp) => (a.kind === "clip" ? a.line : a.kind === "url" ? a.url : a.text)
/** How a filler id plays: its embedded clip, else Mentra TTS of its caption. */
const fillerLabel = (id: string) => (LINES[id] ? id : CAPTIONS[id]!)
const FILLER_1 = FILLERS[0] ? fillerLabel(FILLERS[0]) : null
const FILLER_2 = FILLERS[0] ? fillerLabel(FILLERS[1] ?? FILLERS[0]) : null

test("v3 beats: scan and voice beats are real beats; the station beats scan", () => {
  for (const b of [...SCAN_BEATS, ...VOICE_BEATS]) expect(BEATS).toContain(b)
  expect(SCAN_BEATS).toEqual(expect.arrayContaining(["smoker", "feed", "wasps", "heat"]))
  expect(VOICE_BEATS).toContain("whatif")
  expect(BEATS as readonly string[]).not.toContain("plea")
})

test("scan: hold-still first, then press, photo + heard in one scan, then the verdict", async () => {
  const fake = await boot({photoMs: 300})
  await questBeat(fake, "smoker")
  expect(fake.warmUps.length).toBeGreaterThan(0) // camera warmed when the scan beat started

  fake.press()
  await sleep(50)
  fake.say("it's the syrup", false)
  await sleep(50)
  fake.say("it's the syrup bottle")
  await until(() => fake.audio.some((a) => a.kind === "speak" && a.text === "Finally. Food."), 4000, "verdict audio")

  const press = hits.find((h) => h.path === "/glasses/press")!
  if (FILLER_1) {
    expect(label(fake.audio[0]!)).toBe(FILLER_1)
    expect(fake.audio[0]!.at).toBeLessThanOrEqual(press.at) // the ear line didn't wait for the network
  }
  expect(press.body).toMatchObject({button: "main", press_type: "short"})

  const scans = hits.filter((h) => h.path === "/glasses/scan")
  expect(scans).toHaveLength(1)
  expect(scans[0]!.body).toMatchObject({
    scan_id: expect.stringMatching(/^s\d+$/),
    photo_url: "https://photos.example/p1.jpg",
    pressed_at: press.body.pressed_at,
  })
  expect(scans[0]!.body.heard).toContain("syrup bottle")
  expect(utterances()).toEqual([]) // words during a scan belong to the scan, not the voice pipeline

  await until(() => hops().includes("press_to_audio"), 2000, "latency hops")
  expect(hops()).toEqual(expect.arrayContaining(["press_ack", "photo", "scan_rtt", "press_to_verdict", "press_to_audio"]))
  expect(fake.leds[0]).toMatchObject({color: "orange"})
  expect(fake.leds).toContainEqual(expect.objectContaining({color: "green"}))
  await until(() => fake.status.last?.scanning === false, 1000, "scan done")
})

test("outside a scan beat: no hold-still, no photo, the Hive's line plays", async () => {
  const fake = await boot()
  await questBeat(fake, "hook")
  fake.press()
  await until(() => fake.audio.some((audio) => heardEarLine(audio, NOT_NOW)), 3000, "not-now line")
  expect(fake.audio).toEqual([expect.objectContaining(expectedEarLine(NOT_NOW))])
  expect(fake.photos).toHaveLength(0)
  expect(paths()).not.toContain("/glasses/scan")
})

test("scan refused with 409: says the Hive's line, not 'lost the signal'", async () => {
  const fake = await boot()
  await questBeat(fake, "feed")
  scanRefused = true
  fake.press()
  await until(() => fake.audio.some((audio) => heardEarLine(audio, NOT_NOW)), 4000, "refusal line")
  await until(() => fake.status.last?.scanning === false, 1000, "scan reset")
  if (NOT_NOW !== "ear_scan_failed") expect(fake.audio.some((audio) => heardEarLine(audio, "ear_scan_failed"))).toBe(false)
})

test("presses while a scan is in flight are ignored", async () => {
  const fake = await boot()
  await questBeat(fake, "feed")
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
  await questBeat(fake, "wasps")
  fake.press()
  await until(() => fake.audio.some((a) => a.kind === "speak" && a.text === "Finally. Food."), 5000, "verdict")
  const expected = FILLER_1 && FILLER_2 ? [FILLER_1, FILLER_2, "Finally. Food."] : ["Finally. Food."]
  expect(fake.audio.map(label)).toEqual(expected)
})

test("photo in and nobody talking: scan goes now; mid-sentence: waits for the final", async () => {
  const fake = await boot({photoMs: 100}, {heardWindowMs: 2500})
  await questBeat(fake, "smoker")
  fake.press()
  await until(() => paths().includes("/glasses/scan"), 2000, "scan")
  const quiet = hits.find((h) => h.path === "/glasses/scan")!
  expect(quiet.at - quiet.body.pressed_at).toBeLessThan(1000) // didn't sit out the 2.5 s window
  expect(quiet.body.heard).toBeUndefined()
  await until(() => fake.status.last?.scanning === false, 1000, "scan done")

  hits = []
  fake.press()
  await sleep(20)
  fake.say("it's the pebble", false)
  await sleep(400) // photo is in by now, but they're mid-sentence
  expect(paths()).not.toContain("/glasses/scan")
  fake.say("it's the pebble dish")
  await until(() => paths().includes("/glasses/scan"), 1000, "scan after the final")
  expect(hits.find((h) => h.path === "/glasses/scan")!.body.heard).toBe("it's the pebble dish")
})

test("photo failure still scans with what was heard", async () => {
  const fake = await boot({photoFails: true})
  await questBeat(fake, "field")
  fake.press()
  await sleep(30)
  fake.say("this is a honey jar")
  await until(() => paths().includes("/glasses/scan"), 4000, "scan")
  const scan = hits.find((h) => h.path === "/glasses/scan")!
  expect(scan.body.photo_url).toBeUndefined()
  expect(scan.body.heard).toBe("this is a honey jar")
})

test("speech-first mode answers from the words, then sends the photo for the polaroid", async () => {
  const fake = await boot({photoMs: 800}, {speechFirst: true, heardWindowMs: 2000})
  await questBeat(fake, "heat")
  fake.press()
  await sleep(50)
  fake.say("pebbles") // a final utterance ends the listening window early
  await until(() => fake.audio.some((a) => a.kind === "speak" && a.text === "Finally. Food."), 3000, "verdict")
  const first = hits.filter((h) => h.path === "/glasses/scan")
  expect(first).toHaveLength(1)
  expect(first[0]!.body).toMatchObject({heard: "pebbles"})
  expect(first[0]!.body.photo_url).toBeUndefined()
  await until(() => hits.filter((h) => h.path === "/glasses/scan").length === 2, 3000, "photo follow-up")
  const second = hits.filter((h) => h.path === "/glasses/scan")[1]!
  expect(second.body).toMatchObject({scan_id: first[0]!.body.scan_id, photo_url: "https://photos.example/p1.jpg"})
  expect(fake.audio.filter((a) => a.kind === "speak" && a.text === "Finally. Food.")).toHaveLength(1)
})

test("ear lines from the bus play; long press replays the last one", async () => {
  const fake = await boot()
  hiveBus.say("ear", null, {caption: "Smoke, not swatting. Noted for the minutes."})
  await until(() => fake.audio.length === 1, 3000, "ear line")
  expect(fake.audio[0]).toMatchObject({kind: "speak", text: "Smoke, not swatting. Noted for the minutes."})
  fake.press("long")
  await until(() => fake.audio.length === 2, 2000, "replay")
  expect(fake.audio[1]).toMatchObject({kind: "speak", text: "Smoke, not swatting. Noted for the minutes."})
  fake.touch("double_tap")
  await until(() => fake.audio.length === 3, 2000, "touch replay")
})

test("v2.1 bus messages (operator twins, score, custom what-if intent) are ignored; the ear keeps working", async () => {
  const fake = await boot()
  await questBeat(fake, "whatif")
  const statusBefore = {beat: fake.status.last?.beat, lastLine: fake.status.last?.lastLine, heard: fake.status.last?.heard}
  const v21: BusMessage[] = [
    {type: "choice.request", station: "feed", choice: "sugar_syrup"},
    {type: "fx.force", name: "embers"},
    {type: "whatif.force", topic: "custom"},
    {type: "whatif.force", undo: true},
    {type: "score", elapsed_s: 167, text: "Hive saved in 2:47.", grievances: 2},
    {type: "intent", name: "what_if", topic: "custom", text: "what if a meteor hits", path: "claude", ms: 900, custom_prompt: "A meteor streaks over the meadow"},
    {type: "intent", name: "what_if", topic: "custom", text: "what if something awful", path: "claude", ms: 900, custom_prompt: null},
    {type: "orbis.request", scene: "whatif_custom"}, // v2.1: no `want`
    {type: "orbis", state: "ready", stage: 0, scene: "whatif_custom", fallback_clip: null},
    {type: "transcript", text: "what if a meteor hits", final: true, source: "quest", ms: 300, stt: "test"},
  ]
  for (const m of v21) expect(hiveBus.broadcast(m, ["glasses"])).toBe(1)
  hiveBus.broadcast({type: "brand.new.thing", x: 1} as unknown as BusMessage, ["glasses"]) // newer than this build
  await sleep(150)
  expect(fake.audio).toEqual([])
  expect(utterances()).toEqual([])
  expect({beat: fake.status.last?.beat, lastLine: fake.status.last?.lastLine, heard: fake.status.last?.heard}).toEqual(statusBefore)
  expect(fake.status.last?.bus).toBe("open")
  expect(fake.status.last?.log.filter((l) => l.includes("handler error"))).toEqual([])

  hiveBus.say("ear", null, {caption: "Still here. Still filing."})
  await until(() => fake.audio.length === 1, 2000, "ear line after v2.1 traffic")
  expect(fake.audio[0]).toMatchObject({kind: "speak", text: "Still here. Still filing."})
})

// ------------------------------------------------------------------ voice (the second mic)

test("voice: an off-list what-if (v2.1 topic custom) goes to the Hive like any other; its reply plays", async () => {
  utteranceReply = {intent: "what_if", topic: "custom", line_id: null, audio_url: null, caption: "Grievance filed. Not that one.", custom_prompt: null}
  const fake = await boot()
  await questBeat(fake, "whatif")
  fake.say("What if a meteor hits the meadow?")
  await until(() => fake.audio.some((a) => a.kind === "speak"), 3000, "custom reply audio")
  expect(utterances()).toEqual(["What if a meteor hits the meadow?"])
  expect(fake.audio.map(label)).toEqual(["Grievance filed. Not that one."])
  expect(fake.status.last?.heard).toMatchObject({text: "What if a meteor hits the meadow?", intent: "what_if"})
  expect(fake.status.last?.log.some((l) => l.includes("-> what_if/custom"))).toBe(true)
})

test("voice: a final in a voice beat goes to /glasses/utterance and the reply plays (caption -> TTS)", async () => {
  const fake = await boot()
  await questBeat(fake, "whatif")
  fake.say("what if a", false) // interim: not sent
  await sleep(50)
  expect(utterances()).toEqual([])
  const before = Date.now()
  fake.say("What if a fire starts?")
  await until(() => fake.audio.some((a) => a.kind === "speak"), 3000, "reply audio")
  expect(fake.audio.map(label)).toEqual(["Don't. Say. That."])

  const hit = hits.find((h) => h.path === "/glasses/utterance")!
  expect(hit.body).toEqual({text: "What if a fire starts?", at: expect.any(Number)})
  expect(hit.body.at).toBeGreaterThanOrEqual(before)
  await until(() => hops().includes("utterance_to_audio"), 2000, "voice latency")
  expect(hops()).toEqual(expect.arrayContaining(["utterance_rtt", "utterance_to_audio"]))
  expect(fake.status.last?.heard).toMatchObject({text: "What if a fire starts?", intent: "what_if"})
})

test("voice: the reply plays its embedded clip first, else its audio_url", async () => {
  const embedded = Object.keys(LINES)[0]
  const fake = await boot()
  await questBeat(fake, "smoker")
  if (embedded) {
    utteranceReply = {intent: "ask", topic: null, line_id: embedded, audio_url: "https://x/a.mp3", caption: "words"}
    fake.say("why the smoker though")
    await until(() => fake.audio.length === 1, 3000, "clip reply")
    expect(fake.audio[0]).toMatchObject({kind: "clip", line: embedded})
  }
  utteranceReply = {intent: "ask", topic: null, line_id: "live_9", audio_url: "https://hive.example/audio/live_9.mp3", caption: "Live words"}
  fake.say("what does the smoke do")
  await until(() => fake.audio.some((a) => a.kind === "url"), 3000, "url reply")
  expect(fake.audio.at(-1)).toMatchObject({kind: "url", url: "https://hive.example/audio/live_9.mp3"})
})

test("voice: noise, repeats, Beeswax's own echo, and non-voice beats never reach the Hive", async () => {
  const fake = await boot()
  await questBeat(fake, "hook") // not a voice beat
  fake.say("what if a fire starts")
  await sleep(100)
  expect(utterances()).toEqual([])

  await questBeat(fake, "whatif")
  for (const noise of ["uh", "Okay.", "hmm yeah", "um, so", "hi", "   ", "?!"]) fake.say(noise)
  await sleep(100)
  expect(utterances()).toEqual([])

  hiveBus.say("ear", null, {caption: "Grievance filed. Spores last for decades."})
  await until(() => fake.audio.length === 1, 2000, "ear line")
  fake.say("grievance filed spores last for decades") // the glasses mic hearing Beeswax
  await sleep(100)
  expect(utterances()).toEqual([])

  fake.say("put it out")
  fake.say("Put it out!") // the recognizer repeating itself
  await until(() => utterances().length === 1, 2000, "one utterance")
  await sleep(200)
  expect(utterances()).toEqual(["put it out"])
})

test("voice: the wearer repeating a line after Beeswax finished it is not an echo", async () => {
  const fake = await boot()
  await questBeat(fake, "whatif")
  hiveBus.say("ear", null, {caption: "Go on. Ask me: what if a fire starts?"})
  await until(() => fake.audio.length === 1, 2000, "ear line")
  await sleep(2200) // past the echo tail
  fake.say("what if a fire starts")
  await until(() => utterances().length === 1, 2000, "utterance")
  expect(utterances()).toEqual(["what if a fire starts"])
})

test("voice: words heard while a request is in flight are joined and sent next", async () => {
  utteranceDelayMs = 300
  const fake = await boot()
  await questBeat(fake, "whatif")
  fake.say("what if it rains")
  await sleep(50)
  fake.say("on the hive")
  fake.say("all week")
  await until(() => utterances().length === 2, 3000, "second utterance")
  expect(utterances()).toEqual(["what if it rains", "on the hive all week"])
  await until(() => fake.audio.length === 2, 3000, "both replies")
})

test("voice: a Hive without the route, an error, or junk JSON is logged, never crashes; the next one works", async () => {
  const fake = await boot()
  await questBeat(fake, "escape")
  utteranceStatus = 404
  utteranceReply = {error: "not found"}
  fake.say("what if we plant clover")
  await until(() => fake.status.last?.log.some((l) => l.includes("no /glasses/utterance")) ?? false, 2000, "404 logged")
  expect(fake.status.last?.heard).toMatchObject({text: "what if we plant clover", intent: null})

  utteranceStatus = 500
  fake.say("what if it snows")
  await until(() => fake.status.last?.log.some((l) => l.includes("what if it snows\" failed")) ?? false, 3000, "500 logged")

  utteranceStatus = 200
  utteranceReply = ["not", "an", "object"]
  fake.say("tell me about wasps")
  await until(() => utterances().includes("tell me about wasps"), 2000, "junk reply")
  await sleep(100)
  expect(fake.audio).toEqual([]) // nothing to play, nothing played

  utteranceReply = FIRE_REPLY
  fake.say("what if a fire starts")
  await until(() => fake.audio.some((a) => a.kind === "speak"), 3000, "recovered")
})

test("voice: off in config sends nothing", async () => {
  const fake = await boot({}, {voice: false})
  await questBeat(fake, "whatif")
  fake.say("what if a fire starts")
  await sleep(100)
  expect(utterances()).toEqual([])
})

test("failover: a dead first host falls through to the live one for HTTP and the bus", async () => {
  const fake = fakeSession()
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: ["http://127.0.0.1:9", base], ...TEST_CONFIG}))
  await until(() => fake.status.last?.bus === "open", 8000, "bus on the second host")
  expect(fake.status.last?.host).toBe(base)
  await questBeat(fake, "smoker")
  fake.press()
  await until(() => paths().includes("/glasses/scan"), 4000, "scan on the second host")
})

test("hive unreachable: says so and the next press works", async () => {
  const fake = fakeSession()
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: ["http://127.0.0.1:9"], ...TEST_CONFIG}))
  fake.press()
  await until(() => fake.audio.some((audio) => heardEarLine(audio, "ear_no_signal")), 4000, "no-signal line")
  await until(() => fake.status.last?.scanning === false, 1000, "scan reset")
  fake.press()
  await until(() => fake.audio.filter((audio) => heardEarLine(audio, "ear_no_signal")).length === 2, 4000, "second press handled")
  fake.say("what if a fire starts") // beat unknown (no bus): not sent anywhere, no crash
  await sleep(50)
})
