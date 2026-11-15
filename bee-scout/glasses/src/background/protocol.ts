// The event contract lives in the Hive (hive/src/protocol.ts) and is bundled in from there,
// so a contract change that breaks the glasses fails `bun run typecheck` / `bun run build`.
// v2.1 (PROTOCOL_VERSION still 2, additive): the glasses ignore choice.request, fx.force,
// whatif.force and score, and Intent.custom_prompt (see app.ts); an utterance reply may name
// topic "custom", which the glasses only log.
export {BEATS, PROTOCOL_VERSION, SCAN_BEATS, VOICE_BEATS} from "../../../hive/src/protocol"
export type {
  BeatName,
  BusMessage,
  IntentName,
  PressRequest,
  PressResponse,
  ScanRequest,
  ScanResponse,
  LatencyRequest,
  UtteranceRequest,
  UtteranceResponse,
  Verdict,
} from "../../../hive/src/protocol"
