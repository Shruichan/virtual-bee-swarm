import {LOCAL} from "../config.local"

export interface PhotoConfig {
  size: "low" | "medium" | "high" | "max"
  /** JPEG quality before upload: none=Q95, low=Q88, medium=Q78, high=Q60. Smaller = faster over BLE. */
  compress: "none" | "low" | "medium" | "high"
  /** Glasses shutter sound. Off by default: the ear line already says "hold still". */
  sound: boolean
  timeoutMs: number
}

export interface Config {
  /** Hive hosts, most preferred first. The venue laptop is primary on demo day. */
  hosts: string[]
  hiveToken: string
  /** Let the glasses scan plants and pollinators outside the Quest game's scan beats. */
  fieldGuideMode: boolean
  photo: PhotoConfig
  /** Pre-warm the camera when the app starts in field-guide mode. Turn off if photos misbehave. */
  warmCamera: boolean
  /** In a scan beat, start the photo at the press instead of after the Hive's ack. */
  photoBeforeAck: boolean
  /**
   * Scan-gate fallback: POST what the judge said as soon as they finish a sentence, play that
   * verdict, then POST the photo alone (same scan_id) so the Quest polaroid still develops.
   */
  speechFirst: boolean
  /** Longest wait for speech after a press, and how far back before it to include speech. */
  heardWindowMs: number
  heardLookbackMs: number
  /** Play the second hold-still filler if no verdict has played this long after the press. */
  stallMs: number
  pressTimeoutMs: number
  scanTimeoutMs: number
  /** Hard ceiling for one scan; after it the next press works again. */
  giveUpMs: number
  /** Send final transcriptions in voice beats to POST /glasses/utterance and play the reply. */
  voice: boolean
  /** Per host try for /glasses/utterance. */
  utteranceTimeoutMs: number
  /**
   * Live Bee Scout (scout/live): stream the camera to `streamUrl` the whole time, and route answers
   * and button presses to the Hive's /scout game instead of the photo scanner.
   */
  scoutMode: boolean
  /** Where the glasses publish video, e.g. rtmp://<laptop-LAN-ip>:1935/scout (MediaMTX). */
  streamUrl: string
  stream: {width: number; height: number; fps: number; bitrate: number}
  /** Scout: also cut Beeswax on a partial transcript (3+ words) that looks like a question, not only on a final one. */
  scoutStopOnPartial: boolean
}

export type LocalConfig = Partial<Omit<Config, "photo" | "stream">> & {
  hiveToken: string
  photo?: Partial<PhotoConfig>
  stream?: Partial<Config["stream"]>
}

export const DEFAULTS: Config = {
  hosts: ["https://beeswax-venue.drori.dev", "https://beeswax.drori.dev"],
  hiveToken: "",
  fieldGuideMode: true,
  photo: {size: "low", compress: "medium", sound: false, timeoutMs: 10_000},
  warmCamera: true,
  photoBeforeAck: true,
  speechFirst: false,
  heardWindowMs: 3000,
  heardLookbackMs: 1500,
  stallMs: 4000,
  pressTimeoutMs: 2500,
  scanTimeoutMs: 12_000,
  giveUpMs: 25_000,
  voice: true,
  utteranceTimeoutMs: 6000,
  scoutMode: false,
  streamUrl: "",
  stream: {width: 1280, height: 720, fps: 10, bitrate: 1_200_000},
  scoutStopOnPartial: false,
}

export function makeConfig(local: LocalConfig): Config {
  const hosts = (local.hosts ?? DEFAULTS.hosts).map((h) => h.trim().replace(/\/+$/, "")).filter(Boolean)
  return {
    ...DEFAULTS,
    ...local,
    hosts: hosts.length ? hosts : DEFAULTS.hosts,
    photo: {...DEFAULTS.photo, ...local.photo},
    stream: {...DEFAULTS.stream, ...local.stream},
  }
}

export const CONFIG: Config = makeConfig(LOCAL)
