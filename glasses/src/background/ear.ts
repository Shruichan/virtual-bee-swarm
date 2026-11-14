// Everything Beeswax says in the glasses goes through here: one line at a time, in order.
// Each line plays from the best source it has: the embedded clip, then the Hive's audio_url,
// then Mentra TTS of the caption (a different voice, so it's the last resort).
import type {SpeakerModule} from "@mentra/miniapp/background"
import {CAPTIONS, LINES, SAMPLE_RATE, type Lines} from "./lines"
import {deferred, describe, withTimeout, type Deferred, type Log} from "./util"

export interface LineRef {
  line_id: string | null
  audio_url?: string | null
  caption?: string | null
}

/** "fillers" cuts only hold-still fillers; "all" cuts whatever is playing or queued. */
export type Interrupt = "none" | "fillers" | "all"

type Source = {kind: "clip"; pcm: string; ms: number} | {kind: "url"; url: string} | {kind: "speak"; text: string}

interface Job {
  ref: LineRef
  label: string
  caption: string
  sources: Source[]
  filler: boolean
  queuedAt: number
  started: Deferred<number | null>
  startedAt: number | null
  cancelled: boolean
  stop?: () => void
}

type Speaker = Pick<SpeakerModule, "createStream" | "play" | "speak" | "stop" | "onStateChange">

/**
 * First write of a clip: 300 ms of PCM16 at 16 kHz, cut on a 6-byte boundary so both halves stay
 * whole base64 groups and whole samples. The host holds a write's reply while more than 2 s is
 * buffered, so one big write would resolve seconds after the audio started.
 */
const FIRST_CHUNK_B64 = 12_800

/** A queued line that waited longer than this is stale (e.g. the scene moved on). */
const MAX_QUEUE_WAIT_MS = 10_000

export class Ear {
  /** Last non-filler line, for the replay gesture. */
  last: LineRef | null = null
  /** Called when a non-filler line starts (UI status). */
  onLine: (ref: LineRef, caption: string) => void = () => {}
  /** Called when any line (filler or not) starts and when it ends, with its caption (voice echo guard). */
  onSpeak: (caption: string) => void = () => {}
  onSpeakEnd: (caption: string) => void = () => {}

  private queue: Job[] = []
  private current: Job | null = null

  constructor(
    private speaker: Speaker,
    private log: Log,
    private lines: Lines = LINES,
  ) {}

  hasClip(lineId: string): boolean {
    return Boolean(this.lines[lineId]?.pcm)
  }

  /**
   * Queue a line. Resolves with Date.now() when its audio starts, or null if it was dropped,
   * cut, or failed. Never rejects.
   */
  play(ref: LineRef, opts: {interrupt?: Interrupt; filler?: boolean} = {}): Promise<number | null> {
    const line = ref.line_id ? this.lines[ref.line_id] : undefined
    const caption = ref.caption || line?.caption || (ref.line_id && CAPTIONS[ref.line_id]) || ""
    const sources: Source[] = []
    if (line?.pcm) sources.push({kind: "clip", pcm: line.pcm, ms: line.ms})
    if (ref.audio_url) sources.push({kind: "url", url: ref.audio_url})
    if (caption) sources.push({kind: "speak", text: caption})
    if (!sources.length) {
      this.log(`ear: nothing to play for ${ref.line_id ?? "(no line_id)"}`)
      return Promise.resolve(null)
    }

    if (opts.interrupt === "all") this.cancel(() => true)
    else if (opts.interrupt === "fillers") this.cancel((j) => j.filler)

    const job: Job = {
      ref,
      label: ref.line_id ?? caption.slice(0, 24),
      caption,
      sources,
      filler: opts.filler ?? false,
      queuedAt: Date.now(),
      started: deferred(),
      startedAt: null,
      cancelled: false,
    }
    this.queue.push(job)
    this.pump()
    return job.started.promise
  }

  replay(): void {
    if (this.last) void this.play(this.last, {interrupt: "all"})
    else this.log("ear: nothing to replay yet")
  }

  /** Cut the hold-still fillers, playing or queued. */
  cancelFillers(): void {
    this.cancel((j) => j.filler)
  }

  /** Cut everything (group reset). */
  clear(): void {
    this.cancel(() => true)
    this.last = null
  }

  private cancel(match: (job: Job) => boolean): void {
    this.queue = this.queue.filter((job) => {
      if (!match(job)) return true
      job.cancelled = true
      job.started.resolve(null)
      return false
    })
    const job = this.current
    if (job && match(job)) {
      job.cancelled = true
      this.current = null // detach: the next line starts now, not when this one settles
      job.stop?.()
      job.started.resolve(null)
    }
  }

  private pump(): void {
    if (this.current) return
    const job = this.queue.shift()
    if (!job) return
    if (Date.now() - job.queuedAt > MAX_QUEUE_WAIT_MS) {
      this.log(`ear: dropped stale ${job.label}`)
      job.started.resolve(null)
      return this.pump()
    }
    this.current = job
    if (job.caption) this.onSpeak(job.caption)
    if (!job.filler) {
      this.last = job.ref
      this.onLine(job.ref, job.caption)
    }
    void this.render(job).finally(() => {
      job.started.resolve(null)
      if (job.caption) this.onSpeakEnd(job.caption)
      if (this.current === job) {
        this.current = null
        this.pump()
      }
    })
  }

  /** Try each source until one plays. Once audio has started, never fall through (no double play). */
  private async render(job: Job): Promise<void> {
    for (const src of job.sources) {
      if (job.cancelled) return
      try {
        await withTimeout(this.renderSource(job, src), limitFor(src), `ear ${job.label} (${src.kind})`)
        return
      } catch (err) {
        this.log(`ear: ${job.label} via ${src.kind} failed: ${describe(err)}`)
        if (job.startedAt !== null) {
          if (this.current === job) job.stop?.()
          return
        }
      }
    }
  }

  private async renderSource(job: Job, src: Source): Promise<void> {
    const markStarted = (at = Date.now()) => {
      if (job.startedAt !== null || job.cancelled) return
      job.startedAt = at
      job.started.resolve(at)
    }

    if (src.kind === "clip") {
      job.stop = undefined
      const writer = await this.speaker.createStream({sampleRate: SAMPLE_RATE, stopOtherAudio: true})
      job.stop = () => void writer.abort().catch(() => {})
      if (job.cancelled) return job.stop()
      await writer.writeBase64(src.pcm.slice(0, FIRST_CHUNK_B64))
      markStarted() // the host starts playback once its jitter buffer fills, or at close()
      if (job.cancelled) return
      if (src.pcm.length > FIRST_CHUNK_B64) await writer.writeBase64(src.pcm.slice(FIRST_CHUNK_B64))
      if (job.cancelled) return
      await writer.close()
      return
    }

    const calledAt = Date.now()
    job.stop = () => this.speaker.stop()
    const off = this.speaker.onStateChange((e) => {
      if (e.state === "playing") markStarted()
    })
    try {
      if (src.kind === "url") await this.speaker.play({audioUrl: src.url, stopOtherAudio: true})
      else await this.speaker.speak(src.text, {stopOtherAudio: true})
      markStarted(calledAt) // host that never reported "playing"
    } finally {
      off()
    }
  }
}

function limitFor(src: Source): number {
  if (src.kind === "clip") return src.ms + 5000
  return src.kind === "url" ? 45_000 : 30_000
}
