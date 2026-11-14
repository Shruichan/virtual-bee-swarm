// The glasses app, wired to a session. index.ts registers it; tests drive it with a fake session.
import type {TypedMiniappSession} from "@mentra/miniapp/background"
import type {Channels, GlassesStatus} from "../shared/channels"
import {CONFIG, type Config} from "./config"
import {Ear} from "./ear"
import {Bus, Hive} from "./hive"
import {Led} from "./led"
import {BEATS, SCAN_BEATS, type BeatName, type BusMessage, type LatencyRequest, type PleaRequest} from "./protocol"
import {Scanner} from "./scan"
import {Transcript} from "./transcript"
import {describe} from "./util"

export type Session = TypedMiniappSession<Channels>

/** Final transcripts this long after the plea beat ends still count (people finish sentences). */
const PLEA_GRACE_MS = 4000
const LOG_LINES = 40
/** Helps the speech-to-text with our props and words. */
const VOCABULARY = ["Beeswax", "salvia", "aster", "lavender", "clover", "rosemary", "bee hotel", "hornet", "roses"]

export function start(session: Session, config: Config = CONFIG): {stop(): void} {
  const status: GlassesStatus = {
    bus: "closed",
    host: config.hosts[0]!,
    beat: null,
    scanning: false,
    tokenSet: config.hiveToken.length > 0,
    cloud: "unknown",
    glasses: null,
    lastLine: null,
    latency: {},
    log: [],
  }
  const secrets = config.hiveToken ? [...new Set([config.hiveToken, encodeURIComponent(config.hiveToken)])] : []
  const push = () => session.ui.send("status", {...status, latency: {...status.latency}, log: [...status.log]})
  const log = (...args: unknown[]) => {
    let line = args.map((a) => (typeof a === "string" ? a : describe(a))).join(" ")
    // native fetch/WebSocket errors may quote the URL, and the token rides in its query
    for (const secret of secrets) line = line.split(secret).join("***")
    console.log(`[beeswax] ${line}`)
    status.log.push(`${new Date().toTimeString().slice(0, 8)} ${line}`)
    if (status.log.length > LOG_LINES) status.log.shift()
    push()
  }

  const hive = new Hive(config.hosts, config.hiveToken, log)
  const ear = new Ear(session.speaker, log)
  const led = new Led(session.led, log)
  const transcript = new Transcript()
  let beat: BeatName | null = null
  let pleaUntil = 0

  const latency = (hop: string, ms: number) => {
    const body: LatencyRequest = {hop, ms: Math.round(ms)}
    status.latency[hop] = body.ms
    log(`latency ${hop} ${body.ms} ms`)
    void hive.send("/glasses/latency", body)
  }

  const scanner = new Scanner({
    camera: session.camera,
    hive,
    ear,
    led,
    transcript,
    config,
    beat: () => beat,
    latency,
    log,
    onBusy: (busy) => {
      status.scanning = busy
      push()
    },
  })

  // UI first: the Mentra App holds UI requests until this handler returns.
  session.ui.onOpen(push)
  session.ui.on("press", () => void scanner.press("ui"))
  session.ui.on("replay", () => ear.replay())

  ear.onLine = (ref, caption) => {
    status.lastLine = {id: ref.line_id, caption, at: Date.now()}
    push()
  }

  const setBeat = (n: number) => {
    const name = BEATS[n]
    if (!name || name === beat) return
    const prev = beat
    beat = name
    status.beat = name
    log(`beat ${n} ${name}`)
    if (name === "plea") pleaUntil = Infinity
    else if (prev === "plea") pleaUntil = Date.now() + PLEA_GRACE_MS
    if (SCAN_BEATS.includes(name)) scanner.warmCamera()
  }

  const sendPlea = async (raw: string) => {
    const text = raw.trim()
    if (text.length < 3) return
    log(`plea: "${text}"`)
    const body: PleaRequest = {text}
    if (await hive.send("/glasses/plea", body)) return
    if (bus.send({type: "plea", text})) log("plea: sent over the bus instead")
  }

  const bus = new Bus(hive, log)
  bus.onState = (state) => {
    status.bus = state
    status.host = hive.host
    push()
  }
  bus.onMessage = (msg: BusMessage) => {
    switch (msg.type) {
      case "welcome":
        return setBeat(msg.beat)
      case "beat.set":
        return setBeat(msg.n)
      case "say":
        if (msg.channel === "ear") void ear.play({line_id: msg.line_id, audio_url: msg.audio_url, caption: msg.caption})
        return
      case "group.reset":
        log(`new group ${msg.group_id ?? ""}`)
        ear.clear()
        status.lastLine = null
        return push()
      case "error":
        return log(`hive says: ${msg.message}`)
    }
  }

  // One subscription a host doesn't support must not take the rest down with it.
  const sub = (what: string, subscribe: () => () => void): (() => void) => {
    try {
      return subscribe()
    } catch (err) {
      log(`can't subscribe to ${what}: ${describe(err)}`)
      return () => {}
    }
  }
  const offs = [
    sub("button", () =>
      session.input.onButtonPress((p) => {
        if (p.pressType === "long") ear.replay()
        else void scanner.press(p.buttonId || "button")
      }),
    ),
    sub("touch", () => session.input.onTouch(["double_tap", "long_press"], () => ear.replay())),
    sub("transcription", () =>
      session.transcription.forLanguage("en-US", (data) => {
        transcript.push(data)
        if (data.isFinal && Date.now() < pleaUntil) void sendPlea(data.text)
      }),
    ),
    sub("cloud status", () =>
      session.cloud.onStatusChanged((s) => {
        if (s.status === status.cloud) return
        status.cloud = s.status
        log(`cloud ${s.status}`)
      }),
    ),
    sub("glasses", () =>
      session.glasses.onConnection((c) => {
        status.glasses = c.connected
        log(`glasses ${c.connected ? "connected" : "disconnected"}${c.modelName ? ` (${c.modelName})` : ""}`)
      }),
    ),
  ]
  Promise.resolve()
    .then(() => session.transcription.configure({languageHints: ["en"], vocabulary: VOCABULARY}))
    .catch((err) => log(`transcription config: ${describe(err)}`))

  if (!status.tokenSet) log("no hiveToken in src/config.local.ts: the Hive will refuse us")
  bus.start()

  return {
    stop() {
      bus.stop()
      for (const off of offs) {
        try {
          off()
        } catch {}
      }
    },
  }
}
