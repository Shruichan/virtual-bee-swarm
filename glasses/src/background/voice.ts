// The glasses as a second mic: in a voice beat, each final transcription goes to
// POST /glasses/utterance (the Hive runs the same intent pipeline as Quest voice) and the ear
// line it answers with plays here. One request at a time; words heard meanwhile are joined
// and sent next. Noise, Beeswax's own lines picked up by the mic, and repeats are dropped.
import type {Config} from "./config"
import type {Ear} from "./ear"
import {HttpError, type Hive} from "./hive"
import {ROOM_TEXTS} from "./lines"
import {VOICE_BEATS, type BeatName, type IntentName, type UtteranceRequest, type UtteranceResponse} from "./protocol"
import {describe, type Log} from "./util"

export interface Heard {
  text: string
  /** null = the Hive didn't answer. */
  intent: IntentName | null
  at: number
}

export interface VoiceDeps {
  hive: Hive
  ear: Ear
  config: Config
  beat: () => BeatName | null
  /** A button scan is in flight: what's said now is the scan's "heard", not a question. */
  scanning: () => boolean
  latency: (hop: string, ms: number) => void
  log: Log
  onHeard?: (heard: Heard) => void
  /** Beeswax's room lines (default: the catalog's). */
  roomTexts?: readonly string[]
}

/** Boosts the speech-to-text toward our words. */
export const VOCABULARY = ["Beeswax", "smoker", "syrup", "sugar syrup", "hive", "wasps", "hornet", "pebbles", "fire", "put it out"]

const FILLER_WORDS = new Set([
  "a", "ah", "aha", "eh", "er", "erm", "hey", "hi", "hm", "hmm", "huh", "like", "mhm", "mm", "mmm", "no", "oh",
  "ok", "okay", "so", "the", "uh", "uhh", "uhm", "um", "umm", "wow", "yeah", "yep", "yes",
])
const MIN_LETTERS = 4
/**
 * A final this soon after one of Beeswax's ear lines ends may be the mic hearing that line. Kept
 * short on purpose: a wearer repeating a line ("what if a fire starts?") finishes later than this.
 */
const ECHO_TAIL_MS = 2000
/** A line whose end was never reported stops counting after this. */
const ECHO_MAX_MS = 60_000
/** Share of a final's words found in one recent line for it to count as that line's echo. */
const ECHO_OVERLAP = 0.7
/**
 * The glasses never learn when the Quest plays a room line, so a final (Mentra may cut a line into
 * several) counts as one when it is a whole line or a sentence/phrase of one ("Undo what?" heard back
 * is an undo, "Rain?" a what-if), or has 2+ content words, nearly all of them in one room line, and
 * most of its words too. So a bare "rain" from the wearer is dropped; "what if it rains" is not. Unguarded, "Clover? ... More flowers" heard back is a what-if that replays
 * the same line. The scripted wearer phrases ("what if a fire starts", "put it out", ...) pass.
 */
const ROOM_ECHO_OVERLAP = 0.8
const ROOM_ECHO_ALL_WORDS = 0.6
const ROOM_ECHO_MIN_CONTENT = 2
const FUNCTION_WORDS = new Set(
  (
    "a an the and or but if then so of to in on at by for with from up out off over into about as than " +
    "i me my mine we us our you your yours you're it it's its they them their he she his her this that these those there here " +
    "i'm i'll i've we're we'll they're that's what's there's don't doesn't didn't can't won't isn't aren't " +
    "is are was were be been being am do does did have has had will would can could should shall may might must " +
    "what who whom where when why how which not no yes now just very really too also all some any more much one like " +
    "okay ok oh hey um uh well get got let let's go"
  ).split(" "),
)
const contentWords = (w: string[]) => w.filter((x) => !FUNCTION_WORDS.has(x))
const DUPLICATE_MS = 3000
/** A reply this long after the words were heard is stale: the moment has passed. */
const MAX_REPLY_AGE_MS = 12_000
const MAX_TEXT = 500

export function words(text: string): string[] {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9']+/g, " ")
    .split(" ")
    .map((w) => w.replace(/^'+|'+$/g, ""))
    .filter(Boolean)
}

/** Too short to mean anything, or only fillers ("uh", "okay", "hmm"). */
export function isNoise(text: string): boolean {
  const w = words(text)
  return w.join("").replace(/[^a-z]/g, "").length < MIN_LETTERS || w.every((x) => FILLER_WORDS.has(x))
}

export class Voice {
  private inFlight = false
  private pending: {text: string; at: number} | null = null
  private last = {key: "", at: 0}
  private said: {caption: string; words: Set<string>; at: number; end: number}[] = []
  private missingRoute = false
  private stopped = false
  /** Bumped by reset(): a reply to the previous group's words is not played. */
  private gen = 0

  private room: {all: Set<string>; content: Set<string>}[]
  private roomKeys = new Set<string>()

  constructor(private d: VoiceDeps) {
    const texts = d.roomTexts ?? ROOM_TEXTS
    this.room = texts.map((t) => words(t)).map((w) => ({all: new Set(w), content: new Set(contentWords(w))}))
    for (const t of texts) {
      for (const part of [t, ...t.split(/[.?!:;]+/), ...t.split(/[.?!:;,]+/)]) {
        const key = words(part).join(" ")
        if (key) this.roomKeys.add(key)
      }
    }
  }

  /** A transcription event from the glasses. Never throws. */
  heard(data: {text?: string; isFinal?: boolean}): void {
    try {
      this.take(data)
    } catch (err) {
      this.d.log(`voice: ${describe(err)}`)
    }
  }

  private take(data: {text?: string; isFinal?: boolean}): void {
    if (this.stopped || !data?.isFinal || !this.d.config.voice) return
    const text = (typeof data.text === "string" ? data.text : "").trim().slice(0, MAX_TEXT)
    if (!text) return
    const beat = this.d.beat()
    if (!beat || !VOICE_BEATS.includes(beat)) return
    if (this.d.scanning()) return
    if (isNoise(text)) return this.d.log(`voice: ignored "${text}"`)
    if (this.isEcho(text)) return this.d.log(`voice: ignored echo "${text}"`)
    if (this.isRoomLine(text)) return this.d.log(`voice: ignored Beeswax's room line "${text}"`)
    const now = Date.now()
    const key = words(text).join(" ")
    if (key === this.last.key && now - this.last.at < DUPLICATE_MS) return
    this.last = {key, at: now}
    if (this.inFlight) {
      this.pending = this.pending ? {text: `${this.pending.text} ${text}`, at: this.pending.at} : {text, at: now}
      return
    }
    void this.send(text, now)
  }

  /** Beeswax started saying this in the ear (the glasses mic may hear it back). */
  noteSaid(caption: string): void {
    this.prune()
    const w = new Set(words(caption))
    if (w.size) this.said.push({caption, words: w, at: Date.now(), end: Infinity})
  }

  /** ...and finished it. */
  noteDone(caption: string): void {
    for (let i = this.said.length - 1; i >= 0; i--) {
      const s = this.said[i]!
      if (s.caption === caption && s.end === Infinity) return void (s.end = Date.now())
    }
  }

  /** New judging group: forget what was said and drop queued words. */
  reset(): void {
    this.gen++
    this.pending = null
    this.said = []
    this.last = {key: "", at: 0}
  }

  stop(): void {
    this.stopped = true
    this.pending = null
  }

  private prune(): void {
    const now = Date.now()
    this.said = this.said.filter((s) => (s.end === Infinity ? now - s.at < ECHO_MAX_MS : now - s.end < ECHO_TAIL_MS))
  }

  private isEcho(text: string): boolean {
    this.prune()
    const w = words(text)
    return this.said.some((s) => w.filter((x) => s.words.has(x)).length / w.length >= ECHO_OVERLAP)
  }

  private isRoomLine(text: string): boolean {
    const w = words(text)
    if (this.roomKeys.has(w.join(" "))) return true
    const content = contentWords(w)
    if (content.length < ROOM_ECHO_MIN_CONTENT) return false
    return this.room.some((line) => {
      return (
        content.filter((x) => line.content.has(x)).length / content.length >= ROOM_ECHO_OVERLAP &&
        w.filter((x) => line.all.has(x)).length / w.length >= ROOM_ECHO_ALL_WORDS
      )
    })
  }

  private async send(text: string, at: number): Promise<void> {
    const {hive, ear, config, log} = this.d
    const gen = this.gen
    this.inFlight = true
    try {
      const body: UtteranceRequest = {text, at}
      const t0 = Date.now()
      let raw: unknown
      try {
        raw = await hive.post<UtteranceResponse>("/glasses/utterance", body, config.utteranceTimeoutMs)
      } catch (err) {
        if (err instanceof HttpError && err.status === 404) {
          if (!this.missingRoute) log("voice: this Hive has no /glasses/utterance yet")
          this.missingRoute = true
        } else {
          log(`voice: "${text}" failed: ${describe(err)}`)
        }
        this.d.onHeard?.({text, intent: null, at})
        return
      }
      this.missingRoute = false
      this.d.latency("utterance_rtt", Date.now() - t0)
      const reply = parseReply(raw)
      log(`voice: "${text}" -> ${reply.intent ?? "?"}${reply.topic ? `/${reply.topic}` : ""} (${reply.line_id ?? reply.audio_url ?? (reply.caption ? "tts" : "no line")})`)
      this.d.onHeard?.({text, intent: reply.intent, at})
      if (this.stopped || gen !== this.gen || (!reply.line_id && !reply.audio_url && !reply.caption)) return
      if (Date.now() - at > MAX_REPLY_AGE_MS) return log("voice: reply came too late, not playing it")
      void ear
        .play({line_id: reply.line_id, audio_url: reply.audio_url, caption: reply.caption}, {interrupt: "fillers"})
        .then((started) => {
          if (started) this.d.latency("utterance_to_audio", started - at)
        })
    } finally {
      this.inFlight = false
      const next = this.pending
      this.pending = null
      if (next && !this.stopped) void this.send(next.text, next.at)
    }
  }
}

/** The Hive's reply, tolerating missing or mistyped fields. */
function parseReply(raw: unknown): {intent: IntentName | null; topic: string | null; line_id: string | null; audio_url: string | null; caption: string} {
  const r = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : {}
  const str = (v: unknown) => (typeof v === "string" && v ? v : null)
  return {
    intent: str(r.intent) as IntentName | null,
    topic: str(r.topic),
    line_id: str(r.line_id),
    audio_url: str(r.audio_url),
    caption: str(r.caption) ?? "",
  }
}
