// One button press, start to finish (the real-prop mode; v3 uses it later):
//   press -> "hold still" filler at once -> POST /glasses/press -> photo + ~3 s of listening in
//   parallel -> POST /glasses/scan -> play the verdict line.
// Presses while a scan is in flight are ignored. Every hop is timed and sent to the Hive.
import type {CameraModule} from "@mentra/miniapp/background"
import type {Config} from "./config"
import type {Ear} from "./ear"
import {HttpError, type Hive} from "./hive"
import type {Led} from "./led"
import {FILLERS} from "./lines"
import {
  SCAN_BEATS,
  type BeatName,
  type PressRequest,
  type PressResponse,
  type ScanRequest,
  type ScanResponse,
} from "./protocol"
import type {Transcript} from "./transcript"
import {describe, withTimeout, type Log} from "./util"

export interface ScanDeps {
  camera: Pick<CameraModule, "takePhoto" | "warmUp">
  hive: Hive
  ear: Ear
  led: Led
  transcript: Transcript
  config: Config
  /** Current beat from the bus, or null when we've never heard one. */
  beat: () => BeatName | null
  latency: (hop: string, ms: number) => void
  log: Log
  onBusy?: (busy: boolean) => void
  /** Filler line ids: [at the press, when the verdict stalls]. Default: the catalog's "filler" lines. */
  fillers?: readonly string[]
}

/** A failure that has a line to say about it. */
class ScanError extends Error {
  constructor(
    readonly lineId: string,
    message: string,
  ) {
    super(message)
  }
}

/**
 * HTTP 409 from /glasses/scan: the Hive lost this scan (restart, group reset, expiry) and can't start a new one
 * now. Its body is a ScanResponse naming the line to say ("ear_not_now"), not a network failure.
 */
function refusedLine(err: unknown): string | null {
  if (!(err instanceof HttpError) || err.status !== 409) return null
  try {
    const body = JSON.parse(err.body) as Partial<ScanResponse>
    return typeof body.line_id === "string" && body.line_id ? body.line_id : null
  } catch {
    return null
  }
}

export class Scanner {
  private seq = 0
  private busy = false
  private holdStill: string | null
  private holdStill2: string | null

  constructor(private d: ScanDeps) {
    const fillers = d.fillers ?? FILLERS
    this.holdStill = fillers[0] ?? null
    this.holdStill2 = fillers[1] ?? this.holdStill
  }

  get scanning(): boolean {
    return this.busy
  }

  /** Handle a short press. Never throws. */
  async press(button = "button"): Promise<void> {
    if (this.busy) {
      this.d.log("scan: press ignored, a scan is in flight")
      return
    }
    const id = ++this.seq
    const pressedAt = Date.now()
    this.setBusy(true)
    try {
      await withTimeout(this.run(id, pressedAt, button), this.d.config.giveUpMs, "scan")
    } catch (err) {
      this.d.log(`scan: ${describe(err)}`)
      this.d.led.off()
      const lineId = err instanceof ScanError ? err.lineId : "ear_scan_failed"
      void this.d.ear.play({line_id: lineId}, {interrupt: "fillers"})
    } finally {
      this.seq++ // a run that outlived giveUpMs must not talk any more
      this.setBusy(false)
    }
  }

  /** Called when a scan beat starts, so the first photo isn't a cold start. */
  warmCamera(): void {
    const {warmCamera, photo} = this.d.config
    if (!warmCamera || this.busy) return
    this.d.camera
      .warmUp({size: photo.size, durationMs: 90_000})
      .then(() => this.d.log("scan: camera warm"))
      .catch((err) => this.d.log(`scan: camera warm-up failed: ${describe(err)}`))
  }

  private async run(id: number, pressedAt: number, button: string): Promise<void> {
    const {config: cfg, ear, led, hive, log} = this.d
    const live = () => this.seq === id
    const beat = this.d.beat()
    const scanBeat = beat !== null && SCAN_BEATS.includes(beat)
    const maybeScan = beat === null || scanBeat

    if (maybeScan) {
      this.filler(this.holdStill, "all")
      led.scanning()
    }
    // In a known scan beat the Hive will almost surely say yes, so don't wait for it to start
    // the slowest hop. If it says no, the photo is simply never sent anywhere.
    let photo = cfg.photoBeforeAck && scanBeat ? this.photo() : null

    let ack: PressResponse
    try {
      const req: PressRequest = {pressed_at: pressedAt, button, press_type: "short"}
      ack = await hive.post<PressResponse>("/glasses/press", req, cfg.pressTimeoutMs)
    } catch (err) {
      throw new ScanError("ear_no_signal", `press failed: ${describe(err)}`)
    }
    this.d.latency("press_ack", Date.now() - pressedAt)
    if (!live()) return

    if (!ack.allowed || !ack.scan_id) {
      log(`scan: not allowed in beat ${beat ?? "?"} (${ack.line_id ?? "no line"})`)
      led.off()
      if (ack.line_id) void ear.play({line_id: ack.line_id}, {interrupt: "all"})
      else ear.cancelFillers()
      return
    }
    if (!maybeScan) {
      // our beat was stale: the Hive says this is a scan
      this.filler(this.holdStill, "all")
      led.scanning()
    }

    const scanId = ack.scan_id
    photo ??= this.photo()
    let replied = false
    const stall = setTimeout(
      () => {
        if (!replied && live()) this.filler(this.holdStill2)
      },
      Math.max(0, pressedAt + cfg.stallMs - Date.now()),
    )

    try {
      const from = pressedAt - cfg.heardLookbackMs
      const deadline = pressedAt + cfg.heardWindowMs
      const listen = (needText: boolean) => this.d.transcript.listen(from, deadline, needText)

      // Speech-first: stop listening once they've said something and finished the sentence.
      // Otherwise the photo leads: once it's in, only wait for a sentence that's under way.
      let heard = cfg.speechFirst ? await listen(true) : ""
      if (heard) log(`scan: heard "${heard}"`)

      if (cfg.speechFirst && heard) {
        const reply = await this.scan({scan_id: scanId, heard, pressed_at: pressedAt}, pressedAt)
        replied = true
        // The photo still goes up for the Quest polaroid; its reply has nothing left to say.
        void photo.then((url) => {
          if (!url) return
          const req: ScanRequest = {scan_id: scanId, photo_url: url, pressed_at: pressedAt}
          void hive.send("/glasses/scan", req, cfg.scanTimeoutMs)
        })
        return this.answer(reply, pressedAt, live)
      }

      const photoUrl = await photo
      if (!live()) return
      if (!cfg.speechFirst) {
        heard = await listen(!photoUrl)
        if (heard) log(`scan: heard "${heard}"`)
      }
      const req: ScanRequest = {scan_id: scanId, pressed_at: pressedAt}
      if (photoUrl) req.photo_url = photoUrl
      if (heard) req.heard = heard
      const reply = await this.scan(req, pressedAt)
      replied = true
      this.answer(reply, pressedAt, live)
    } finally {
      clearTimeout(stall)
    }
  }

  private async scan(req: ScanRequest, pressedAt: number): Promise<ScanResponse> {
    const t0 = Date.now()
    try {
      const reply = await this.d.hive.post<ScanResponse>("/glasses/scan", req, this.d.config.scanTimeoutMs)
      this.d.latency("scan_rtt", Date.now() - t0)
      this.d.latency("press_to_verdict", Date.now() - pressedAt)
      this.d.log(`scan: ${reply.item}/${reply.verdict} via ${reply.path} (${reply.line_id ?? reply.audio_url ?? "tts"})`)
      return reply
    } catch (err) {
      throw new ScanError(refusedLine(err) ?? "ear_scan_failed", `scan failed: ${describe(err)}`)
    }
  }

  /** Queue the verdict line. Doesn't wait for it: the scan is over once the answer is in. */
  private answer(reply: ScanResponse, pressedAt: number, live: () => boolean): void {
    if (!live()) return
    this.d.led.verdict(reply.verdict)
    void this.d.ear
      .play({line_id: reply.line_id, audio_url: reply.audio_url, caption: reply.caption}, {interrupt: "fillers"})
      .then((started) => {
        if (started) this.d.latency("press_to_audio", started - pressedAt)
      })
  }

  private filler(lineId: string | null, interrupt: "all" | "none" = "none"): void {
    if (lineId) void this.d.ear.play({line_id: lineId}, {interrupt, filler: true})
  }

  /** Resolves with the photo URL, or null on failure/timeout. Never rejects. */
  private photo(): Promise<string | null> {
    const {photo: opts} = this.d.config
    const t0 = Date.now()
    const shot = this.d.camera.takePhoto({
      size: opts.size,
      compress: opts.compress,
      sound: opts.sound,
      saveToGallery: false,
      timeoutMs: opts.timeoutMs,
    })
    return withTimeout(shot, opts.timeoutMs + 1000, "photo").then(
      (p) => {
        this.d.latency("photo", Date.now() - t0)
        return p.photoUrl || null
      },
      (err) => {
        this.d.log(`scan: photo failed after ${Date.now() - t0} ms: ${describe(err)}`)
        return null
      },
    )
  }

  private setBusy(busy: boolean): void {
    this.busy = busy
    this.d.onBusy?.(busy)
  }
}
