// Ear lines the miniapp can play: clips embedded in the bundle plus a caption for every id.
//  - lines.generated.ts: base64 PCM16 clips written by hive/scripts/prerender.ts (`bun run lines`
//    copies it here). Both shapes work: the prerender's `LINE_PCM16: {id: base64}` and the
//    placeholder's `LINES: {id: {pcm, ms, caption}}`.
//  - hive/lines/lines.json: the Hive's line catalog, so an id the Hive sends without a caption
//    (PressResponse, say.force) can still be spoken by Mentra TTS when it isn't embedded. It is
//    bundled at build time: rebuild after the catalog changes. No line ids are hardcoded here
//    beyond the two glasses-only fallbacks below.
import catalog from "../../../hive/lines/lines.json"
import * as generated from "./lines.generated"

export interface Clip {
  pcm: string
  ms: number
  caption: string
}
export type Lines = Record<string, Clip>

/** Lines only the glasses say (the Hive can't be reached to name them). The catalog wins if it has them. */
const LOCAL_CAPTIONS: Record<string, string> = {
  ear_no_signal: "I can't reach the hive. Check the hotspot.",
  ear_scan_failed: "Lost the signal. Press again.",
}

const RATES = [16000, 24000, 48000] as const
type Rate = (typeof RATES)[number]

const gen = generated as unknown as Record<string, unknown>
const rate = gen.SAMPLE_RATE as Rate
export const SAMPLE_RATE: Rate = RATES.includes(rate) ? rate : 16000

/** Duration of base64 PCM16 mono audio. */
export function pcmMs(b64: string, sampleRate: number = SAMPLE_RATE): number {
  const pad = b64.endsWith("==") ? 2 : b64.endsWith("=") ? 1 : 0
  const bytes = (b64.length / 4) * 3 - pad
  return Math.round((bytes / 2 / sampleRate) * 1000)
}

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v)

interface Entry {
  id: string
  text: string
  tags: string[]
}

/** One channel's entries of the catalog: an array of {id, channel, text, tags?} (or {lines: [...]}); bad entries skipped. */
export function catalogEntries(data: unknown, channel: "ear" | "room"): Entry[] {
  const list = Array.isArray(data) ? data : isObj(data) && Array.isArray(data.lines) ? data.lines : []
  const out: Entry[] = []
  for (const e of list) {
    if (!isObj(e) || e.channel !== channel || typeof e.id !== "string" || typeof e.text !== "string" || !e.text.trim()) continue
    const tags = Array.isArray(e.tags) ? e.tags.filter((t): t is string => typeof t === "string") : []
    out.push({id: e.id, text: e.text, tags})
  }
  return out
}

export const earEntries = (data: unknown): Entry[] => catalogEntries(data, "ear")

const EAR = earEntries(catalog)

/** What Beeswax says on the Quest. The glasses mic can hear it across the table (voice.ts drops those finals). */
export const ROOM_TEXTS: string[] = catalogEntries(catalog, "room").map((e) => e.text)

/** Caption per ear line id: the Hive's catalog, plus the glasses-only lines. */
export const CAPTIONS: Record<string, string> = {...LOCAL_CAPTIONS}
const seen = new Set<string>()
for (const e of EAR) {
  if (seen.has(e.id)) continue // the Hive keeps the first of a duplicate id; so do we
  seen.add(e.id)
  CAPTIONS[e.id] = e.text
}

/**
 * The scan's "hold still" fillers: ear lines tagged "filler" in the catalog, in catalog order
 * (the first plays at the press, the second if the verdict stalls). Empty = no filler line.
 */
export const FILLERS: string[] = EAR.filter((e) => e.tags.includes("filler")).map((e) => e.id)

/** Clips from a lines.generated module, whichever shape it has. */
export function parseClips(mod: Record<string, unknown>): Lines {
  const out: Lines = {}
  const raw = mod.LINE_PCM16 ?? mod.LINES ?? mod.default
  if (!isObj(raw)) return out
  for (const [id, v] of Object.entries(raw)) {
    const pcm = typeof v === "string" ? v : isObj(v) && typeof v.pcm === "string" ? v.pcm : ""
    if (!pcm) continue
    const caption = isObj(v) && typeof v.caption === "string" && v.caption ? v.caption : (CAPTIONS[id] ?? "")
    out[id] = {pcm, ms: pcmMs(pcm), caption}
  }
  return out
}

/** Embedded clips by line id. */
export const LINES: Lines = parseClips(gen)
