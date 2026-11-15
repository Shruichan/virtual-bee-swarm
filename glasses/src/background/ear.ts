// Everything Beeswax says in the glasses goes through here: one line at a time, in order.
// Each line plays from the best source it has: the embedded clip, then the Hive's audio_url,
// then Mentra TTS of the caption (a different voice, so it's the last resort).
import type {SpeakerModule} from "@mentra/miniapp/background"
import {CAPTIONS, LINES, SAMPLE_RATE, type Lines} from "./lines"
import {deferred, describe, sleep, TimeoutError, withTimeout, type Deferred, type Log} from "./util"

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
  /** Longest a url / speak source may take (null = the default limit). */
  maxMs: number | null
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

/** A queued line that waited longer than this is stale (e.g. the scene moved on). Scout mode raises it: its lines
 *  come as sequences (intro, chime + fact + fact + any-questions) that must all play in order, and the Hive's
 *  scout_stop is what drops them. */
const MAX_QUEUE_WAIT_MS = 10_000

/** Between two url/speak plays (the host's player needs a moment to let go of the last one). */
export const PLAY_GAP_MS = 200
/** Wait at most this long for the previous play request to settle. */
const SETTLE_MAX_MS = 800
/** No "playing" this long after play(): the host stalled; stop and retry once (hosts that report states only). */
export const STALL_MS = 4000

export interface EarOptions {
  maxQueueWaitMs?: number
}

/** A rough upper bound for how long a caption takes to say (mp3s run ~14 chars/s): url/speak playback past this
 *  is treated as finished, so a host that never reports the end can't stall the queue. */
export function lineLimitMs(caption: string): number {
  return Math.round(((caption.length / 14.4) * 1000 + 300) * 1.5 + 6000)
}

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

  private maxQueueWaitMs: number
  /** url/speak: when the last one ended, its still-unsettled request, and whether the host reports states. */
  private lastEndAt = 0
  private pending: Promise<unknown> | null = null
  private statesSeen = false

  constructor(
    private speaker: Speaker,
    private log: Log,
    private lines: Lines = LINES,
    opts: EarOptions = {},
  ) {
    this.maxQueueWaitMs = opts.maxQueueWaitMs ?? MAX_QUEUE_WAIT_MS
  }

  hasClip(lineId: string): boolean {
    return Boolean(this.lines[lineId]?.pcm)
  }

  /**
   * Queue a line. Resolves with Date.now() when its audio starts, or null if it was dropped,
   * cut, or failed. Never rejects.
   */
  play(ref: LineRef, opts: {interrupt?: Interrupt; filler?: boolean; maxMs?: number} = {}): Promise<number | null> {
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
      maxMs: opts.maxMs ?? null,
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

  /** Cut whatever is playing or queued, keep the replay line (a question barged in). */
  stop(): void {
    this.cancel(() => true)
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
    if (Date.now() - job.queuedAt > this.maxQueueWaitMs) {
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
        const limit = src.kind !== "clip" && job.maxMs ? job.maxMs : limitFor(src)
        await withTimeout(this.renderSource(job, src), limit, `ear ${job.label} (${src.kind})`)
        return
      } catch (err) {
        const timedOut = err instanceof TimeoutError
        this.log(`ear: ${job.label} via ${src.kind} ${timedOut ? "took too long, moving on" : `failed: ${describe(err)}`}`)
        // A url/speak that ran out of time most likely played (the host just never said it was done): never play it
        // again from the next source (that was a double play in the phone's voice), and don't cut it either.
        if (timedOut && src.kind !== "clip") return
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

    // Seen live: a url play started the moment the previous one finished never played (intro_2, every time). So:
    // let the previous request settle, leave a short gap, and retry once if the host never reports "playing".
    if (this.pending) await Promise.race([this.pending, sleep(SETTLE_MAX_MS)])
    const gap = PLAY_GAP_MS - (Date.now() - this.lastEndAt)
    if (gap > 0) await sleep(gap)
    const firstAfterIdle = Date.now() - this.lastEndAt > 1000
    for (let attempt = 0; attempt < 2; attempt++) {
      if (job.cancelled) return
      const calledAt = Date.now()
      job.stop = () => this.speaker.stop()
      this.log(`ear: play ${job.label} (${src.kind}${attempt ? ", retry" : ""})`)
      // The host resolves play()/speak() when playback ends, but sometimes never does (seen live: the queue stalled
      // 45 s, then everything behind it was dropped). A "stopped"/"idle" after "playing" also counts as the end.
      let sawPlaying = false
      let endedByState!: () => void
      const ended = new Promise<"stopped">((r) => (endedByState = () => r("stopped")))
      let stallTimer: ReturnType<typeof setTimeout> | undefined
      // Only a host that reports states can be caught stalling (no "playing" in time); otherwise the line limit applies.
      const stalled = new Promise<"stall">((r) => {
        if (this.statesSeen) stallTimer = setTimeout(() => !sawPlaying && r("stall"), STALL_MS)
      })
      const off = this.speaker.onStateChange((e) => {
        if (e.state === "playing") {
          sawPlaying = true
          this.statesSeen = true
          markStarted()
        } else if (sawPlaying && (e.state === "stopped" || e.state === "idle")) endedByState()
      })
      let how: "result" | "stopped" | "stall"
      try {
        // stopOtherAudio only when starting after a pause: back to back, our own previous line is already over
        const opts = {stopOtherAudio: attempt === 0 && firstAfterIdle}
        const done = src.kind === "url" ? this.speaker.play({audioUrl: src.url, ...opts}) : this.speaker.speak(src.text, opts)
        this.pending = Promise.resolve(done).catch(() => {})
        how = await Promise.race([Promise.resolve(done).then(() => "result" as const), ended, stalled])
      } finally {
        clearTimeout(stallTimer)
        off()
        this.lastEndAt = Date.now()
      }
      if (how === "stall" && attempt === 0) {
        this.log(`ear: ${job.label} never started playing, retrying`)
        try {
          this.speaker.stop()
        } catch {}
        await sleep(PLAY_GAP_MS)
        continue
      }
      markStarted(calledAt) // host that never reported "playing"
      this.log(`ear: end ${job.label} after ${Date.now() - calledAt} ms (${how})`)
      return
    }
  }
}

function limitFor(src: Source): number {
  if (src.kind === "clip") return src.ms + 5000
  return src.kind === "url" ? 45_000 : 30_000
}
