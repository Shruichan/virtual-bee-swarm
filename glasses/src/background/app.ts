// The glasses app, wired to a session. index.ts registers it; tests drive it with a fake session.
import type {TypedMiniappSession} from "@mentra/miniapp/background"
import type {Channels, GlassesStatus} from "../shared/channels"
import {CONFIG, type Config} from "./config"
import {Ear} from "./ear"
import {Bus, Hive} from "./hive"
import {Led} from "./led"
import {LiveStream} from "./live"
import {BEATS, SCAN_BEATS, type BeatName, type BusMessage, type LatencyRequest} from "./protocol"
import {Scanner} from "./scan"
import {Scout} from "./scout"
import {Transcript} from "./transcript"
import {describe} from "./util"
import {VOCABULARY, Voice} from "./voice"

export type Session = TypedMiniappSession<Channels>

const LOG_LINES = 40

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
    heard: null,
    latency: {},
    log: [],
  }
  const secrets = config.hiveToken ? [...new Set([config.hiveToken, encodeURIComponent(config.hiveToken)])] : []
  const push = () => session.ui.send("status", {...status, latency: {...status.latency}, log: [...status.log]})
  // Scout mode: mirror the log to the Hive so the laptop sees it (skip the Hive client's own lines to avoid loops).
  let mirror: ((line: string) => void) | null = null
  const log = (...args: unknown[]) => {
    let line = args.map((a) => (typeof a === "string" ? a : describe(a))).join(" ")
    // native fetch/WebSocket errors may quote the URL, and the token rides in its query
    for (const secret of secrets) line = line.split(secret).join("***")
    console.log(`[beeswax] ${line}`)
    status.log.push(`${new Date().toTimeString().slice(0, 8)} ${line}`)
    if (status.log.length > LOG_LINES) status.log.shift()
    push()
    mirror?.(line)
  }
  const hive = new Hive(config.hosts, config.hiveToken, log)
  if (config.scoutMode)
    mirror = (line) => {
      if (!line.startsWith("hive") && !line.startsWith("bus")) void hive.send("/glasses/event", {kind: "log", line})
    }
  const ear = new Ear(session.speaker, log)
  const led = new Led(session.led, log)
  const transcript = new Transcript()
  let beat: BeatName | null = null

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
  // In scout mode the camera streams the whole time; a photo warm-up would fight it for the camera.
  if (config.fieldGuideMode && !config.scoutMode) scanner.warmCamera()
  const live = config.scoutMode ? new LiveStream(session.stream, config, log) : null
  // Mirror the stream state to the Hive log so the laptop can see it without the phone's status page.
  if (live) live.onState = (state, detail) => void hive.send("/glasses/event", {kind: "stream", state, detail: detail ?? null})
  // Scout mode: presses and final transcripts go to the Hive's /scout game, not the scanner or Voice.
  const scout = config.scoutMode ? new Scout({hive, led: session.led, log}) : null

  const voice = new Voice({
    hive,
    ear,
    config,
    beat: () => beat,
    scanning: () => scanner.scanning,
    latency,
    log,
    onHeard: (heard) => {
      status.heard = heard
      push()
    },
  })

  // UI first: the Mentra App holds UI requests until this handler returns.
  session.ui.onOpen(push)
  session.ui.on("press", () => (scout ? scout.button() : void scanner.press("ui")))
  session.ui.on("replay", () => ear.replay())

  ear.onLine = (ref, caption) => {
    status.lastLine = {id: ref.line_id, caption, at: Date.now()}
    push()
  }
  ear.onSpeak = (caption) => voice.noteSaid(caption)
  ear.onSpeakEnd = (caption) => voice.noteDone(caption)

  const setBeat = (n: unknown) => {
    const name = typeof n === "number" ? BEATS[n] : undefined
    if (!name || name === beat) return
    beat = name
    status.beat = name
    log(`beat ${n} ${name}`)
    if (SCAN_BEATS.includes(name)) scanner.warmCamera()
  }

  const bus = new Bus(hive, log)
  bus.onState = (state) => {
    status.bus = state
    status.host = hive.host
    push()
    if (state === "open") scout?.busOpen()
  }
  bus.onMessage = (msg: BusMessage) => {
    switch (msg.type) {
      case "welcome":
        return setBeat(msg.beat)
      case "beat.set":
        return setBeat(msg.n)
      case "say":
        if (msg.channel !== "ear") return
        scout?.said(msg)
        void ear.play({line_id: msg.line_id, audio_url: msg.audio_url, caption: msg.caption})
        return
      case "group.reset":
        log(`new group ${msg.group_id ?? ""}`)
        ear.clear()
        voice.reset()
        status.lastLine = null
        status.heard = null
        return push()
      case "error":
        return log(`hive says: ${msg.message}`)
      // The Quest's, Board's and monitor's business, not the ear's. Named here so a contract rename
      // fails typecheck. v2.1: operator twins (choice.request, fx.force, whatif.force), the score card,
      // and intent.custom_prompt (an unplanned what-if's window prompt) are all ignored.
      case "intent":
      case "transcript":
      case "choice.request":
      case "fx.force":
      case "whatif.force":
      case "score":
      case "orbis.request":
      case "orbis":
        return
      default:
        return // anything else, including message types newer than this build
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
        else if (scout) scout.button()
        else void scanner.press(p.buttonId || "button")
      }),
    ),
    sub("touch", () => session.input.onTouch(["double_tap", "long_press"], () => ear.replay())),
    sub("transcription", () =>
      session.transcription.forLanguage("en-US", (data) => {
        transcript.push(data)
        if (scout) scout.heard(data)
        else voice.heard(data)
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
  if (live) {
    void live.start()
    live.watch(async () => (await hive.post<{frame_age_ms: number | null}>("/scout/watchdog", {}, 3000)).frame_age_ms)
  }

  return {
    stop() {
      if (live) void live.stop()
      voice.stop()
      bus.stop()
      for (const off of offs) {
        try {
          off()
        } catch {}
      }
    },
  }
}
