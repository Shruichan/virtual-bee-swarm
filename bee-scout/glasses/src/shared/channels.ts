/**
 * Typed channels between the background (glasses logic) and the phone UI (status page).
 * Both layers import this file; the bundler inlines it.
 */

export interface GlassesStatus {
  bus: "connecting" | "open" | "closed"
  host: string
  beat: string | null
  scanning: boolean
  tokenSet: boolean
  /** Phone ↔ MentraOS cloud (transcription and TTS need it). */
  cloud: string
  /** Glasses ↔ phone; null until the first report. */
  glasses: boolean | null
  lastLine: {id: string | null; caption: string; at: number} | null
  /** Last thing the wearer said that went to the Hive, and what it meant (null intent = no answer). */
  heard: {text: string; intent: string | null; at: number} | null
  /** Latest value per hop, ms. */
  latency: Record<string, number>
  /** Newest last. */
  log: string[]
}

export interface Channels {
  // background → UI
  status: GlassesStatus

  // UI → background
  press: {at: number} // same as a short button press (handy without glasses)
  replay: {at: number} // same as a long press
}

declare global {
  // eslint-disable-next-line no-var
  var mentra: import("@mentra/miniapp/ui").MentraTyped<Channels>
}
