// A stand-in for the Mentra session: records what the miniapp asks the glasses to do and lets
// tests press buttons and speak. No hardware, no network, no sound.
import type {Session} from "../src/background/app"
import type {GlassesStatus} from "../src/shared/channels"
import {LINES} from "../src/background/lines"

export type AudioOp = {kind: "clip"; line: string} | {kind: "url"; url: string} | {kind: "speak"; text: string}

export interface FakeOptions {
  photoMs?: number
  photoFails?: boolean
  photoUrl?: string
  clipPlayMs?: number
  /** Names clip writes by matching their PCM against this map (default: the shipped lines). */
  lines?: Record<string, {pcm: string}>
}

export function fakeSession(opts: FakeOptions = {}) {
  const audio: (AudioOp & {at: number})[] = []
  const photos: number[] = []
  const warmUps: unknown[] = []
  const leds: unknown[] = []
  const streams: {op: "start" | "stop"; opts?: unknown; at: number}[] = []
  const status: {last: GlassesStatus | null} = {last: null}
  const handlers: {
    button?: (p: {buttonId: string; pressType: "short" | "long"}) => void
    touch?: (t: {kind: string}) => void
    transcript?: (d: {text: string; isFinal: boolean}) => void
  } = {}
  const lines = opts.lines ?? LINES
  // A clip may arrive in several writes: name it by its first one.
  const lineOf = (b64: string) => Object.keys(lines).find((k) => lines[k]!.pcm.startsWith(b64)) ?? `pcm:${b64.length}`

  const session = {
    ui: {
      send(channel: string, payload: GlassesStatus) {
        if (channel === "status") status.last = payload
      },
      onOpen: () => () => {},
      on: () => () => {},
    },
    speaker: {
      async createStream() {
        let aborted = false
        let writes = 0
        return {
          async writeBase64(b64: string) {
            if (writes++ === 0) audio.push({kind: "clip", line: lineOf(b64), at: Date.now()})
            return {bufferedMs: 0}
          },
          async close() {
            if (!aborted) await new Promise((r) => setTimeout(r, opts.clipPlayMs ?? 20))
            return {}
          },
          async abort() {
            aborted = true
          },
        }
      },
      async play(o: {audioUrl: string}) {
        audio.push({kind: "url", url: o.audioUrl, at: Date.now()})
      },
      async speak(text: string) {
        audio.push({kind: "speak", text, at: Date.now()})
        return {completed: true}
      },
      stop() {},
      onStateChange: () => () => {},
    },
    led: {
      async turnOn(o: unknown) {
        leds.push(o)
        return {state: "success", requestId: "x"}
      },
      async turnOff() {
        leds.push("off")
        return {state: "success", requestId: "x"}
      },
    },
    camera: {
      async takePhoto() {
        photos.push(Date.now())
        await new Promise((r) => setTimeout(r, opts.photoMs ?? 50))
        if (opts.photoFails) throw {code: "INTERNAL", message: "capture failed"}
        return {requestId: "r1", photoUrl: opts.photoUrl ?? "https://photos.example/p1.jpg", mimeType: "image/jpeg", size: 1234}
      },
      async warmUp(o: unknown) {
        warmUps.push(o)
      },
    },
    stream: {
      async startStream(o: unknown) {
        streams.push({op: "start", opts: o, at: Date.now()})
        return {streamId: "stream-1", status: "streaming", resolvedConfig: {video: {width: 1280, height: 720, fps: 15}}}
      },
      async stop() {
        streams.push({op: "stop", at: Date.now()})
      },
    },
    input: {
      onButtonPress(h: typeof handlers.button) {
        handlers.button = h
        return () => {}
      },
      onTouch(_g: unknown, h: typeof handlers.touch) {
        handlers.touch = h
        return () => {}
      },
    },
    transcription: {
      forLanguage(_lang: string, h: typeof handlers.transcript) {
        handlers.transcript = h
        return () => {}
      },
      configure: async () => {},
    },
    cloud: {onStatusChanged: () => () => {}},
    glasses: {onConnection: () => () => {}},
    on: () => () => {},
  }

  return {
    session: session as unknown as Session,
    audio,
    photos,
    warmUps,
    leds,
    streams,
    status,
    press: (pressType: "short" | "long" = "short") => handlers.button!({buttonId: "main", pressType}),
    say: (text: string, isFinal = true) => handlers.transcript!({text, isFinal}),
    touch: (kind: string) => handlers.touch!({kind}),
  }
}

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

export async function until(cond: () => boolean, ms = 3000, what = "condition"): Promise<void> {
  const t0 = Date.now()
  while (!cond()) {
    if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`)
    await sleep(10)
  }
}
