// A short rolling memory of what the glasses heard, so a scan can ask "what did they say
// around the press?" without opening a new transcription stream (which would miss the start).
import type {TranscriptionData} from "@mentra/miniapp/background"

interface Utterance {
  text: string
  at: number
}

const KEEP_MS = 30_000
/** An interim result older than this was abandoned by the recognizer, not a sentence in progress. */
const INTERIM_STALE_MS = 2000

export class Transcript {
  private finals: Utterance[] = []
  private interim: Utterance | null = null
  private listeners = new Set<() => void>()

  push(data: TranscriptionData, now = Date.now()): void {
    const text = (data.text ?? "").trim()
    if (data.isFinal) {
      this.interim = null
      if (text) this.finals.push({text, at: now})
      this.finals = this.finals.filter((f) => now - f.at < KEEP_MS)
    } else {
      this.interim = text ? {text, at: now} : null
    }
    for (const cb of this.listeners) cb()
  }

  /** Someone is mid-sentence (a recent interim result hasn't been finalized yet). */
  get speaking(): boolean {
    return this.interim !== null && Date.now() - this.interim.at < INTERIM_STALE_MS
  }

  /** Everything said (final or still in progress) since `t0`. */
  since(t0: number): string {
    const parts = this.finals.filter((f) => f.at >= t0).map((f) => f.text)
    if (this.interim && this.interim.at >= t0) parts.push(this.interim.text)
    return parts.join(" ").trim()
  }

  /**
   * Resolve with `since(from)` as soon as nobody is mid-sentence (and, with `needText`, something
   * has been said), or at `deadline` whatever the state.
   */
  listen(from: number, deadline: number, needText: boolean): Promise<string> {
    return new Promise((resolve) => {
      const check = () => {
        const text = this.since(from)
        if (Date.now() < deadline && (this.speaking || (needText && !text))) return
        clearTimeout(timer)
        this.listeners.delete(check)
        resolve(text)
      }
      const timer = setTimeout(check, Math.max(0, deadline - Date.now()))
      this.listeners.add(check)
      check()
    })
  }
}
