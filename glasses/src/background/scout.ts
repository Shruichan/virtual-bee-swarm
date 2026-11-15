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
  caption?: string | null
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

// Wake word + echo check, a copy of hive/src/scout-ask.ts (WAKE_RX, isEcho): the Hive decides for real; this only
// stops Beeswax talking over the kid a round trip earlier. Beeswax only listens when called by name.
const norm = (s: string) =>
  String(s ?? "")
    .toLowerCase()
    .replace(/[’']/g, "'")
    .replace(/[^a-z0-9' ]+/g, " ")
    .replace(/\s+/g, " ")
    .trim()
const SEP = "[\\s,.!?;:\\-–—]+"
const NAME = "b(?:ee|e)?(?:['’]?s|z)?[\\s\\-]*(?:wax|wacks|whacks|wax's)"
const LEAD = "(?:(?:um+|uh+|hm+|mm+|ah+|er+|oh+|okay|ok|so|well|and|wait)" + SEP + ")*"
/** A greeting + the name, only at the START of the utterance (after fillers). The bare name never wakes him: STT
 *  hears his chime "Bee fact found!" as "Beeswax found!", and "... what Beeswax was saying" is chatter. */
const GREETING = "(?:hey|hay|hi|hello|okay|ok|a|yo)"
const WAKE_RX = new RegExp(
  `^[\\s,.!?;:\\-–—]*${LEAD}(?:${GREETING}${SEP}${NAME}|(?:hey|hay)${SEP}bees?)(?=$|[\\s,.!?;:\\-–—])`,
  "i",
)
const CALL_RX = new RegExp(`\\b${GREETING}${SEP}${NAME}\\b|\\b(?:hey|hay)${SEP}bees?\\b`, "i")
const EDGE_RX = /^[\s,.!?;:\-–—]+|[\s,.;:\-–—]+$/g
const FILLER_ONLY_RX = /^(?:(?:um+|uh+|hm+|mm+|ah+|er+|oh+|huh|so|well|hey|hi|hello|okay|ok|please|there)\s*)*$/

/** "Hey Beeswax" (greeting + name, and their speech-to-text spellings) at the start of the utterance. */
export function hasWake(text: string): boolean {
  return WAKE_RX.test(String(text ?? ""))
}

/** Only the call, nothing after it ("Hey Beeswax?"). */
function bareWake(text: string): boolean {
  const m = WAKE_RX.exec(String(text ?? ""))
  return !!m && FILLER_ONLY_RX.test(norm(String(text).slice(m.index + m[0].length).replace(EDGE_RX, "")))
}

/** His chime as STT writes it ("Beeswax found", "Be fact found", "Bee facts found") -> one spelling. */
const echoNorm = (s: string) => norm(s).replace(/\b(?:bee'?s ?wax|bees ?wax|bee ?facts?|be ?facts?|b ?facts?) found\b/g, "bee fact found")

/** The mic heard Beeswax (glasses or Mac speakers): inside a caption, a shared run of 4+ words, or 60%+ of its words
 *  his. An explicit "hey beeswax" his line doesn't contain is the wearer. Anything starting "beeswax found" is his chime. */
export function isEchoOf(text: string, captions: readonly string[]): boolean {
  if (/^(?:bee'?s ?wax|bees ?wax) found\b/.test(norm(text))) return true
  const t = echoNorm(text)
  const ws = t ? t.split(" ") : []
  const n = ws.length
  if (n < 2) return false
  const call = CALL_RX.test(text)
  return captions.some((cap) => {
    const c = echoNorm(cap)
    if (!c || (call && !CALL_RX.test(cap))) return false
    if (` ${c} `.includes(` ${t} `)) return true
    for (let i = 0; i + 4 <= n; i++) if (` ${c} `.includes(` ${ws.slice(i, i + 4).join(" ")} `)) return true
    if (n < 5) return false // short utterances share words with his lines by chance ("the honey jar")
    const words = new Set(c.split(" "))
    return ws.filter((w) => words.has(w)).length / n >= 0.6
  })
}

/** A line can come back as a transcript this long after it stops playing. */
const ECHO_TAIL_MS = 2000
/** A line that never reports its end stops counting after this long; and any line said this recently counts,
 *  wherever it played (the Mac's speakers play it later than the glasses). */
const ECHO_MAX_MS = 30_000

export interface ScoutDeps {
  hive: Pick<Hive, "send">
  led: Pick<LedModule, "turnOn">
  log: Log
  /** Cut the ear (current line + queue) the moment the wearer calls "Hey Beeswax". */
  stopEar?: () => void
  /** Also stop on a partial transcript that already has the wake word (config.scoutStopOnPartial). */
  stopOnPartial?: boolean
}

export class Scout {
  private started = false
  private ledFailures = 0
  /** What Beeswax said: when it reached us, and until when it may still be playing in this ear. */
  private spoken: {text: string; at: number; until: number}[] = []
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
    this.spoken.push({text: caption, at: Date.now(), until: Date.now() + ECHO_MAX_MS})
    if (this.spoken.length > 24) this.spoken.shift()
  }

  /** The ear finished (or cut) a line: it can echo only a moment longer. */
  noteDone(caption: string): void {
    const end = Date.now() + ECHO_TAIL_MS
    for (const s of this.spoken) if (s.text === caption) s.until = Math.min(s.until, end)
  }

  /** Is the wearer calling Beeswax ("Hey Beeswax ...", not his own voice heard back)? Only then is he cut off. */
  callsBeeswax(text: string): boolean {
    if (!hasWake(text)) return false
    const now = Date.now()
    // a bare "Hey Beeswax" only against what plays in this ear now (his tips say "Just say Hey Beeswax", and that
    // is when a kid says it back); anything longer against all he said in the last 30 s
    const bare = bareWake(text)
    const caps = this.spoken.filter((s) => now <= s.until || (!bare && now - s.at <= ECHO_MAX_MS)).map((s) => s.text)
    return !isEchoOf(text, caps)
  }

  private stopFor(text: string, partial: boolean): void {
    if (this.stoppedThisTurn || !this.d.stopEar) return
    this.stoppedThisTurn = true
    this.d.log(`scout: wake word${partial ? " (partial)" : ""}, ear stopped: "${text.slice(0, 60)}"`)
    try {
      this.d.stopEar()
    } catch (err) {
      this.d.log(`scout stop: ${describe(err)}`)
    }
  }

  /** Every final transcript goes to the Hive (yes / no for the game, "Hey Beeswax ..." for Ask Beeswax, the rest is
   *  only shown on the judge screen). The wake word cuts Beeswax off right here, before the Hive's answer arrives. */
  heard(data: {text?: string; isFinal?: boolean}): void {
    const text = (typeof data?.text === "string" ? data.text : "").trim().slice(0, 300)
    if (!data?.isFinal) {
      if (this.d.stopOnPartial && text && this.callsBeeswax(text)) this.stopFor(text, true)
      return
    }
    if (text && this.callsBeeswax(text)) this.stopFor(text, false) // no-op when a partial already stopped it
    this.stoppedThisTurn = false // the next utterance may stop again
    if (!text) return
    this.d.log(`scout: heard "${text}"`)
    void this.d.hive.send("/scout/utterance", {text, at: Date.now()})
  }

  /** A `say` arrived on the bus: light the LED for the chime and the facts. */
  said(msg: ScoutSayLike): void {
    const id = scoutLineId(msg)
    if (typeof msg.caption === "string" && msg.caption) {
      this.spoken.push({text: msg.caption, at: Date.now(), until: 0}) // said (it may play on the Mac first)
      if (this.spoken.length > 24) this.spoken.shift()
    }
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
