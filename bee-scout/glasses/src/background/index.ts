/**
 * Beeswax in your ear: the glasses half of HONEYMOON (concept v3: Save the Hive).
 *
 * Background layer (no DOM, no Node). It keeps a bus connection to the Hive, plays ear lines,
 * sends what the wearer says in voice beats to the Hive (voice.ts: the second mic), runs the
 * button scan for the later real-prop mode (scan.ts), and reports status to the phone UI.
 *   short press  -> scan (station beats only; elsewhere the Hive answers "not now")
 *   long press   -> replay the last line (also touch double tap / long press)
 */
import {registerMiniapp} from "@mentra/miniapp/background"
import type {Channels} from "../shared/channels"
import {start} from "./app"

const g = globalThis as {__beeswax?: {stop(): void}}

registerMiniapp<Channels>((session) => {
  // A dev reload can re-evaluate this bundle in the same context: stop the old sockets/timers.
  g.__beeswax?.stop()
  g.__beeswax = start(session)
})
