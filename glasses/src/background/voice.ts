// The glasses as a second mic: in a voice beat, each final transcription goes to
// POST /glasses/utterance (the Hive runs the same intent pipeline as Quest voice) and the ear
// line it answers with plays here. One request at a time; words heard meanwhile are joined
// and sent next. Noise, Beeswax's own lines picked up by the mic, and repeats are dropped.
// v2.1: a reply may carry topic "custom" (an unplanned what-if) and the Hive's `intent` broadcast a
// `custom_prompt`; the glasses only play the reply's line and never touch the window, so both pass through.
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
 * several, or run its short sentences together) counts as one when it is a whole line or a run of its
 * sentences/phrases ("Undo what?" heard back is an undo, "Rain?" a what-if), or has 2+ content words,
 * nearly all of them in one room line, and most of its words too. So a bare "rain" from the wearer is
 * dropped; "what if it rains" is not.
 * Unguarded, "Clover? ... More flowers" heard back is a what-if that replays the same line.
 * Words are compared without apostrophes, and a line's contractions also count spelled out: "Fire's out"
 * sounds exactly like "Fires out", and the Hive's whatif-beat keyword path reads "fires out" (or "fire is
 * out", "nothings on fire") as a NEW fire what-if, so the window would re-ignite right after "put it out".
 * A final that opens like the wearer's own question ("what if ...", "why ...", "how ...") is only matched
 * against lines that contain that opening: "what if the bees drown" asks about the deep-bowl line, it isn't it.
 * WEARER_PHRASES (below) must all pass; `roomGuardProblems()` checks that against the catalog.
 */
const ROOM_ECHO_OVERLAP = 0.8
const ROOM_ECHO_ALL_WORDS = 0.6
const ROOM_ECHO_MIN_CONTENT = 2
const noApostrophe = (w: string) => w.replace(/'/g, "")
const FUNCTION_WORDS = new Set(
  (
    "a an the and or but if then so of to in on at by for with from up out off over into about as than " +
    "i me my mine we us our you your yours you're it it's its they them their he she his her this that these those there here " +
    "i'm i'll i've we're we'll they're that's what's there's don't doesn't didn't can't won't isn't aren't cannot " +
    "is are was were be been being am do does did have has had will would can could should shall may might must " +
    "what who whom where when why how which not no yes now just very really too also all some any more much one like " +
    "okay ok oh hey um uh well get got let let's go"
  )
    .split(" ")
    .map(noApostrophe),
)
const contentWords = (w: string[]) => w.filter((x) => !FUNCTION_WORDS.has(x))
/** Contractions spelled out the way speech-to-text may write them (on words()' lowercase, plain-apostrophe text). */
const SPELLED_OUT: [RegExp, string][] = [
  [/\bcan't\b/g, "cannot"], [/\bwon't\b/g, "will not"], [/n't\b/g, " not"], [/'re\b/g, " are"], [/'m\b/g, " am"],
  [/'ll\b/g, " will"], [/'ve\b/g, " have"], [/'s\b/g, " is"],
]
/** The guard's words for a heard final: words() without apostrophes ("fire's" and "fires" are one word). */
const guardWords = (text: string): string[] => words(text).map(noApostrophe).filter(Boolean)
/** The guard's spellings of a catalog text: as written (no apostrophes) and, if it differs, spelled out. */
function spellings(text: string): string[][] {
  const w = words(text)
  const asWritten = w.map(noApostrophe).filter(Boolean)
  const spelled = SPELLED_OUT.reduce((s, [rx, to]) => s.replace(rx, to), w.join(" ")).split(" ").map(noApostrophe).filter(Boolean)
  return spelled.join(" ") === asWritten.join(" ") ? [asWritten] : [asWritten, spelled]
}
/** How the wearer opens a question of their own (see above). */
const WEARER_LEADS = ["what if", "what happens", "what would", "what about", "why", "how", "tell me", "show me", "show us", "imagine", "suppose", "can you", "could you"].map((l) => l.split(" "))
const DUPLICATE_MS = 3000
/** A reply this long after the words were heard is stale: the moment has passed. */
const MAX_REPLY_AGE_MS = 12_000
const MAX_TEXT = 500

export function words(text: string): string[] {
  return text
    .toLowerCase()
    .replace(/[\u2018\u2019\u02bc`]/g, "'") // typographic apostrophes (don’t) are plain ones
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

/** Sentence and phrase breaks inside a line's text (… and dashes too, for typographic catalog edits). */
const PHRASE_BREAK = /(?:[.?!:;,\u2026\u2013\u2014]|\s--?\s)+/

/**
 * Beeswax's room lines, recognized in what the glasses mic heard (rules above). Built from line texts:
 * the catalog's are bundled at build time, so a lines.json edit counts only after a rebuild (dev
 * reloads on a glasses save; a release install needs another `bun run release`).
 */
export class RoomGuard {
  private lines: {text: string; all: Set<string>; content: Set<string>; spelled: string[]}[] = []
  private keys = new Map<string, string>()

  constructor(texts: readonly string[]) {
    for (const text of texts) {
      const forms = spellings(text)
      if (!forms[0]!.length) continue
      const all = forms.flat()
      this.lines.push({text, all: new Set(all), content: new Set(contentWords(all)), spelled: forms.map((f) => ` ${f.join(" ")} `)})
      // Every run of consecutive sentences/phrases: Mentra may cut a line up or run its short
      // sentences together ("Don't. Say. That." comes back as "don't say that").
      const pieces = text.split(PHRASE_BREAK).map(spellings).filter((p) => p[0]!.length)
      for (let i = 0; i < pieces.length; i++) {
        for (let j = i; j < pieces.length; j++) {
          for (const form of [0, 1]) {
            const key = pieces.slice(i, j + 1).flatMap((p) => p[form] ?? p[0]!).join(" ")
            if (!this.keys.has(key)) this.keys.set(key, text)
          }
        }
      }
    }
  }

  /** The room line `text` came from, or null when it's the wearer's own words. */
  match(text: string): string | null {
    const w = guardWords(text)
    const exact = this.keys.get(w.join(" "))
    if (exact) return exact
    const content = contentWords(w)
    if (content.length < ROOM_ECHO_MIN_CONTENT) return null
    const lead = WEARER_LEADS.find((l) => l.every((x, i) => w[i] === x))
    const opening = lead ? ` ${lead.join(" ")} ` : null
    const line = this.lines.find(
      (l) =>
        (!opening || l.spelled.some((s) => s.includes(opening))) &&
        content.filter((x) => l.content.has(x)).length / content.length >= ROOM_ECHO_OVERLAP &&
        w.filter((x) => l.all.has(x)).length / w.length >= ROOM_ECHO_ALL_WORDS,
    )
    return line?.text ?? null
  }
}

/**
 * What the wearer is scripted (or likely) to say: the room-line guard must let every one through.
 * v2.1 adds custom what-ifs, so off-list "what if ..." questions are here too.
 */
export const WEARER_PHRASES: readonly string[] = [
  "what if a fire starts", "what if a fire starts?", "what happens if a fire starts", "a fire starts", "put it out", "put it out!",
  "put the fire out", "put out the fire", "stop the fire", "is the fire out", "go back", "undo that", "undo",
  "what if it rains", "make it rain", "what if it snows", "what if winter comes", "what if we plant clover",
  "what if we plant more flowers", "what if they spray pesticides", "what if there's a wildfire", "what if we make the door bigger",
  "why are the bees angry", "tell me about the queen", "how do bees cool the hive", "on the hive all week",
  // v2.1 custom what-ifs (off the list: Claude writes the window prompt, or Beeswax refuses)
  "what if a meteor hits", "what if a meteor hits the meadow", "what if a bear finds the hive", "what if the queen dies",
  "what if a tornado comes", "what if there's a drought", "what if it floods", "what if the hive gets too hot",
  "what if we build a city here", "what if aliens land", "what if the bees go to the moon", "what if we move to the moon",
  "what if the window catches fire", "what if the meadow turns into a desert", "what if night falls", "what if it's spring",
  // follow-ups on what Beeswax just said, in its words (the guard's wearer-lead rule)
  "what if the bees drown", "what if the drones get kicked out", "what if the union votes to stay", "why do bees drown in deep water",
  "why are the girls furious", "why is a light hive bad", "how do the girls keep the queen warm",
]

/** How speech-to-text may write a line: "'s" without the apostrophe (homophones) and contractions spelled out. */
export function asrSpellings(text: string): string[] {
  return [text.replace(/(\w)['’]s\b/gi, "$1s"), SPELLED_OUT.reduce((s, [rx, to]) => s.replace(rx, to), words(text).join(" "))]
}

/**
 * What a catalog edit breaks in the room-line guard: wearer phrases a room line would now swallow, and
 * room lines it no longer catches. Empty = fine. build.ts warns with these; units.test.ts fails on them.
 */
export function roomGuardProblems(texts: readonly string[] = ROOM_TEXTS, wearer: readonly string[] = WEARER_PHRASES): string[] {
  const guard = new RoomGuard(texts)
  const out: string[] = []
  for (const t of wearer) {
    const line = guard.match(t)
    if (line) out.push(`the wearer can no longer say "${t}": room line "${line}" swallows it`)
  }
  for (const t of texts) {
    if (words(t).length < 2) continue
    if (guard.match(t) === null) out.push(`room line not caught: "${t}"`)
    else for (const v of asrSpellings(t)) if (guard.match(v) === null) out.push(`room line not caught as speech-to-text may write it: "${v}"`)
  }
  return out
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

  private room: RoomGuard

  constructor(private d: VoiceDeps) {
    this.room = new RoomGuard(d.roomTexts ?? ROOM_TEXTS)
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
    const w = new Set(guardWords(caption))
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
    const w = guardWords(text)
    return this.said.some((s) => w.filter((x) => s.words.has(x)).length / w.length >= ECHO_OVERLAP)
  }

  private isRoomLine(text: string): boolean {
    return this.room.match(text) !== null
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
