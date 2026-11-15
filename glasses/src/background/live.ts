// Live Bee Scout: keep the glasses camera streaming to the laptop (MediaMTX) so the vision worker
// can see what the wearer looks at. Direct publish, no Mentra relay: the glasses connect straight to
// config.streamUrl, so the glasses and the laptop must share a network (the iPhone hotspot works).
import type {StreamModule} from "@mentra/miniapp/background"
import type {Config} from "./config"
import {describe} from "./util"

export type LiveState = "off" | "starting" | "live" | "failed"

export class LiveStream {
  state: LiveState = "off"
  onState: (state: LiveState, detail?: string) => void = () => {}
  private streamId: string | null = null
  private retry: ReturnType<typeof setTimeout> | null = null
  private stopped = false

  constructor(
    private readonly stream: Pick<StreamModule, "startStream" | "stop">,
    private readonly config: Config,
    private readonly log: (...args: unknown[]) => void,
  ) {}

  async start(): Promise<void> {
    this.stopped = false
    if (!this.config.streamUrl) {
      this.set("failed", "no streamUrl in src/config.local.ts")
      return
    }
    this.set("starting")
    const t0 = Date.now()
    try {
      const {width, height, fps, bitrate} = this.config.stream
      const res = await this.stream.startStream({
        direct: this.config.streamUrl,
        video: {width, height, fps, bitrate},
        sound: false,
      })
      this.streamId = res.streamId
      const v = res.resolvedConfig?.video
      this.set("live", `${res.status} in ${Date.now() - t0} ms${v ? ` (${v.width}x${v.height} @ ${v.fps} fps)` : ""}`)
    } catch (err) {
      this.set("failed", describe(err))
      // The glasses may still be joining Wi-Fi: try again in 5 s until stopped.
      if (!this.stopped) this.retry = setTimeout(() => void this.start(), 5000)
    }
  }

  async stop(): Promise<void> {
    this.stopped = true
    if (this.retry) clearTimeout(this.retry)
    this.retry = null
    if (this.state === "off") return
    try {
      await this.stream.stop(this.streamId ?? undefined)
    } catch (err) {
      this.log(`stream stop: ${describe(err)}`)
    }
    this.streamId = null
    this.set("off")
  }

  private set(state: LiveState, detail?: string): void {
    this.state = state
    // Never log the full URL's query; the host:port/path is what helps debugging.
    this.log(`stream ${state}${detail ? `: ${detail}` : ""}`)
    this.onState(state, detail)
  }
}
