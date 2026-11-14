// The event contract lives in the Hive (hive/src/protocol.ts) and is bundled in from there,
// so a contract change that breaks the glasses fails `bun run typecheck` / `bun run build`.
export {BEATS, PROTOCOL_VERSION, SCAN_BEATS} from "../../../hive/src/protocol"
export type {
  BeatName,
  BusMessage,
  PressRequest,
  PressResponse,
  ScanRequest,
  ScanResponse,
  PleaRequest,
  LatencyRequest,
  Verdict,
} from "../../../hive/src/protocol"
