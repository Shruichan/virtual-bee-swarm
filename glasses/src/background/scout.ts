// Live Bee Scout (config.scoutMode): the glasses are the game's ears and buttons. The Hive's /scout game
// decides everything; here we only forward the start, short presses and every final transcript, and light
// the LED for the lines that matter (orange: "Bee fact found!", green: a fact).
// Which line a `say` is: the Hive puts the scout line id in tts_id (line_id stays null: no embedded clip),
// and the mp3 URL ends in /scout/audio/<id>.mp3 when there is one.
import type {LedModule} from "@mentra/miniapp/background"
import type {Hive} from "./hive"
import {SCOUT_STOP_ID} from "../../../hive/src/scout-protocol"
import {describe, type Log} from "./util"

export interface ScoutSayLike {
  line_id?: string | null
  tts_id?: string | null
  audio_url?: string | null
}

/** The scout line id of a bus `say`, or null when it isn't a scout line. */
export function scoutLineId(msg: ScoutSayLike): string | null {
  if (typeof msg.tts_id === "string" && msg.tts_id.startsWith("scout_")) return msg.tts_id
  const m = typeof msg.audio_url === "string" ? /\/scout\/audio\/(scout_[a-z0-9_]+)\.mp3(?:[?#].*)?$/.exec(msg.audio_url) : null
  return m ? m[1]! : null
}

/** The Hive's "cut the ear now" say: tts_id SCOUT_STOP_ID, no audio (ScoutGame.interrupt(), POST /scout/reset). */
export function isScoutStop(msg: ScoutSayLike): boolean {
  return msg.tts_id === SCOUT_STOP_ID
}

/** An Ask Beeswax answer (scout/ask): tts_id scout_ask_<id>. It cuts whatever the ear is playing. */
export function isAskLine(msg: ScoutSayLike): boolean {
  return typeof msg.tts_id === "string" && msg.tts_id.startsWith("scout_ask_")
}

// Question detection, a copy of hive/src/scout-ask.ts (looksLikeQuestion, isEcho): the Hive decides for real;
// this only stops Beeswax talking over the kid a round trip earlier.
const norm = (s: string) =>
  String(s ?? "")
    .toLowerCase()
    .replace(/[’']/g, "'")
    .replace(/[^a-z0-9' ]+/g, " ")
    .replace(/\s+/g, " ")
    .trim()
const LEAD_RX = /^(?:(?:um+|uh+|hm+|so|okay|ok|hey|hi|oh|wait|and|but|well|beeswax|bees wax|bee wax|mister beeswax|mr beeswax)\s+)+/
const START_RX =
  /^(?:why|how|what|what's|whats|who|who's|where|where's|when|which|whose|is|are|am|do|does|did|can|could|would|will|should|was|were|have|has|tell me|explain|i wonder|i want to know|i wanna know)\b/
const ANY_RX =
  /\b(?:why|how come|how do|how does|how did|how many|how much|how long|how big|what is|what's|what are|what do|what does|what happens|what if|tell me (?:more )?about|tell me why|explain|i wonder)\b|\b(?:for what|what for)$/
const NOT_A_QUESTION_RX =
  /^(?:(?:um+|uh+|hm+|mm+|ah+|er+|oh+|huh|so|well|beeswax)\s*)*(?:yes|yeah|yep|no|nope|nah|sure|thanks|thank you|thank you beeswax|ok(?:ay)?|cool|nice|great|awesome|got it|i'?m good|i'?m fine|all good|no questions?|nothing|not really|that'?s all|that'?s it|bye|goodbye|wow|yay)?(?: (?:thanks|thank you|beeswax|please))?$/

export function looksLikeQuestion(text: string): boolean {
  const raw = String(text ?? "")
  const t = norm(raw).replace(LEAD_RX, "")
  const words = t ? t.split(" ").length : 0
  if (words < 2) return false
  if (raw.includes("?")) return true
  if (/^(?:why|how come)\b/.test(t)) return true
  if (words < 3) return false
  return START_RX.test(t) || ANY_RX.test(t)
}

/** The mic heard Beeswax: the transcript sits inside a caption he spoke. */
export function isEchoOf(text: string, captions: readonly string[]): boolean {
  const t = norm(text)
  if (t.split(" ").length < 2) return false
  return captions.some((c) => ` ${norm(c)} `.includes(` ${t} `))
}

/** After "Got any questions?" (scout_any_q_*), the Hive treats the next utterance as a question for ~8 s after the line. */
const ANY_Q_WINDOW_MS = 11_000
const ECHO_WINDOW_MS = 25_000

export interface ScoutDeps {
  hive: Pick<Hive, "send">
  led: Pick<LedModule, "turnOn">
  log: Log
  /** Cut the ear (current line + queue) the moment a question is heard. */
  stopEar?: () => void
  /** Also stop on a partial transcript of 3+ words that looks like a question (config.scoutStopOnPartial). */
  stopOnPartial?: boolean
}

export class Scout {
  private started = false
  private ledFailures = 0
  private spoken: {text: string; at: number}[] = []
  private anyQUntil = 0
  /** Already stopped the ear for this utterance (partials repeat). */
  private stoppedThisTurn = false

  constructor(private d: ScoutDeps) {}

  /** The bus opened. Only the first time starts the game: a reconnect must not wipe progress. */
  busOpen(): void {
    if (this.started) return
    this.started = true
    this.d.log("scout: start")
    void this.d.hive.send("/scout/start", {at: Date.now()})
  }

  /** A short press: "yes" during an offer (the Hive decides). */
  button(): void {
    this.d.log("scout: button")
    void this.d.hive.send("/scout/button", {at: Date.now()})
  }

  /** The ear started a line (echo guard). */
  noteSaid(caption: string): void {
    if (!caption) return
    this.spoken.push({text: caption, at: Date.now()})
    if (this.spoken.length > 12) this.spoken.shift()
  }

  /** Would the Hive answer this as a question? (not Beeswax's own voice) */
  isQuestion(text: string): boolean {
    const now = Date.now()
    const recent = this.spoken.filter((s) => now - s.at <= ECHO_WINDOW_MS).map((s) => s.text)
    if (isEchoOf(text, recent)) return false
    if (now <= this.anyQUntil) {
      const t = norm(text)
      return !!t && !NOT_A_QUESTION_RX.test(t)
    }
    return looksLikeQuestion(text)
  }

  private stopFor(text: string, partial: boolean): void {
    if (this.stoppedThisTurn || !this.d.stopEar) return
    this.stoppedThisTurn = true
    this.d.log(`scout: question${partial ? " (partial)" : ""}, ear stopped: "${text.slice(0, 60)}"`)
    try {
      this.d.stopEar()
    } catch (err) {
      this.d.log(`scout stop: ${describe(err)}`)
    }
  }

  /** Every final transcript goes to the Hive (yes / no / rude for the game, questions for Ask Beeswax).
   *  A question cuts Beeswax off right here, before the Hive's answer arrives. */
  heard(data: {text?: string; isFinal?: boolean}): void {
    const text = (typeof data?.text === "string" ? data.text : "").trim().slice(0, 300)
    if (!data?.isFinal) {
      if (this.d.stopOnPartial && text && norm(text).split(" ").length >= 3 && this.isQuestion(text)) this.stopFor(text, true)
      return
    }
    if (text && this.isQuestion(text)) this.stopFor(text, false) // no-op when a partial already stopped it
    this.stoppedThisTurn = false // the next utterance may stop again
    if (!text) return
    this.d.log(`scout: heard "${text}"`)
    void this.d.hive.send("/scout/utterance", {text, at: Date.now()})
  }

  /** A `say` arrived on the bus: light the LED for the chime and the facts. */
  said(msg: ScoutSayLike): void {
    const id = scoutLineId(msg)
    if (id?.startsWith("scout_any_q")) this.anyQUntil = Date.now() + ANY_Q_WINDOW_MS
    if (id === "scout_chime") this.light({color: "orange", ontime: 300, offtime: 200, count: 4})
    else if (id?.startsWith("scout_fact_")) this.light({color: "green", ontime: 3000, offtime: 0, count: 1})
  }

  private light(opts: Parameters<LedModule["turnOn"]>[0]): void {
    if (this.ledFailures >= 3) return
    let p: Promise<unknown>
    try {
      p = this.d.led.turnOn(opts)
    } catch (err) {
      p = Promise.reject(err)
    }
    p.then(
      () => (this.ledFailures = 0),
      (err) => {
        this.ledFailures++
        this.d.log(`scout led: ${describe(err)}`)
      },
    )
  }
}
