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

export interface ScoutDeps {
  hive: Pick<Hive, "send">
  led: Pick<LedModule, "turnOn">
  log: Log
}

export class Scout {
  private started = false
  private ledFailures = 0

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

  /** Every final transcript goes to the Hive, which reads yes / no / rude. */
  heard(data: {text?: string; isFinal?: boolean}): void {
    if (!data?.isFinal) return
    const text = (typeof data.text === "string" ? data.text : "").trim().slice(0, 300)
    if (!text) return
    this.d.log(`scout: heard "${text}"`)
    void this.d.hive.send("/scout/utterance", {text, at: Date.now()})
  }

  /** A `say` arrived on the bus: light the LED for the chime and the facts. */
  said(msg: ScoutSayLike): void {
    const id = scoutLineId(msg)
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
