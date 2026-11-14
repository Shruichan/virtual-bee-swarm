// HONEYMOON event contract: the one source of truth shared by the Hive, the Quest (Unity),
// the Beat Board, the monitor, and the Mentra miniapp. Change it only together with PLAN.md
// (log the change there) and unity/BeeswaxClient/BeeswaxProtocol.cs.

export const PROTOCOL_VERSION = 1;

// ---------------------------------------------------------------- roles & transport
// Bus: wss://<host>/bus?role=<Role>&token=<HIVE_TOKEN>
// Text frames are JSON messages (below). Binary frames are [kind: u8][payload...].
export type Role = "quest" | "board" | "monitor" | "glasses";

export const BINARY_KIND = {
  /** JPEG bytes of the latest glasses photo; belongs to the most recent `scan.photo`. */
  SCAN_PHOTO: 0x01,
  /** PCM16 little-endian mono chunk; belongs to the most recent `tts.start`. */
  TTS_PCM16: 0x02,
} as const;
export type BinaryKind = (typeof BINARY_KIND)[keyof typeof BINARY_KIND];

// ---------------------------------------------------------------- beats (Unity owns the clock)
// Unity is the ONLY beat clock. It reports `beat.set`; the Hive/Board only send `beat.request`.
export const BEATS = [
  "pre", //        0  hook + fitting (not on the clock)
  "meet", //       1  0:00-0:15 swarm onto palm
  "roses", //      2  0:15-1:05 roses scan + Bee Vision
  "beeline", //    3  1:05-1:40 purple flower scan + beeline
  "offlist", //    4  1:40-2:10 off-list roast (only if ahead)
  "hornet", //     5  2:10-3:00 hornet + hum
  "plea", //       6  3:00-3:15 one plea
  "buffer", //     7  3:15-3:50
  "t0", //         8  3:50-4:30 launch, Moon, Earthrise, crash back
  "splashdown", // 9  4:30-4:50 ear callback + seed packet
  "field", //     10  post-demo Field Mode (glasses only, no Quest effects)
] as const;
export type BeatName = (typeof BEATS)[number];

/** Beats in which a glasses button press starts a scan. Elsewhere: "not now" line. */
export const SCAN_BEATS: readonly BeatName[] = ["roses", "beeline", "offlist", "field"];

export type Mode = "duo" | "solo";

// ---------------------------------------------------------------- scan vocabulary
// Claude may only answer with these enums; Unity only executes these enums.
export const ITEMS = [
  "red_rose",
  "purple_bee_flower",
  "clover_or_rosemary",
  "water_dish",
  "bee_hotel",
  "spray_bottle",
  "plastic_flower",
  "other_bee_plant", // a real flowering plant bees would use (not one of our props)
  "other_plant", //     a real plant with little/no bee value (lawn grass, double-petal ornamentals)
  "non_plant_object", // badge, coffee, phone, shoe...
  "person", //          never roast people's appearance
  "unclear",
] as const;
export type Item = (typeof ITEMS)[number];

export const VERDICTS = ["good", "bad", "dangerous", "roast"] as const;
export type Verdict = (typeof VERDICTS)[number];

export type ScanPath = "claude" | "speech" | "operator";

/** Mat sockets (1-4) for the pre-placed pots. Items not on the mat have no socket. */
export const SOCKET_OF: Partial<Record<Item, 1 | 2 | 3 | 4>> = {
  red_rose: 1,
  purple_bee_flower: 2,
  clover_or_rosemary: 3,
  water_dish: 4,
};

// ---------------------------------------------------------------- messages
export type Channel = "ear" | "room";

/** Sent by any client right after connecting. */
export interface Hello { type: "hello"; role: Role; client: string; version: number }
/** Hive → client after hello. */
export interface Welcome { type: "welcome"; group_id: string; beat: number; mode: Mode; server_time: number; host: string }

/** Quest → Hive (authoritative): Unity's beat machine entered beat n. Hive rebroadcasts to all. */
export interface BeatSet { type: "beat.set"; n: number; name: BeatName }
/** Board/Hive → Quest: please go to beat n (or the next beat). Unity may ignore it. */
export interface BeatRequest { type: "beat.request"; n?: number; next?: boolean }

export interface ModeMsg { type: "mode"; mode: Mode }
/** Board → Hive: start a new judging group (clears game memory). Hive rebroadcasts with the new id. */
export interface GroupReset { type: "group.reset"; group_id?: string }

/** Glasses button pressed inside a scan beat. Quest: blank polaroid + flash, Beeswax whips around. */
export interface ScanStarted { type: "scan.started"; scan_id: string }
/** Followed immediately by one binary SCAN_PHOTO frame with the JPEG. Quest: polaroid develops. */
export interface ScanPhoto { type: "scan.photo"; scan_id: string; width?: number; height?: number }
export interface ScanVerdict {
  type: "scan.verdict";
  scan_id: string;
  item: Item;
  verdict: Verdict;
  confidence: number; // 0..1
  seen: string; //       <= 8 words, shown on the monitor ("Beeswax saw: ...")
  socket: 1 | 2 | 3 | 4 | null; // where the beeline goes (null = no beeline / U-turn)
  line_id: string | null; //      pre-rendered line to play (room channel on the Quest)
  tts_id: string | null; //       live line (off-list roast) streamed after this message
  caption: string;
  path: ScanPath;
  ms: number; // press -> verdict
}

/** Quest → Hive → glasses: the swarm landed on a socket (glasses plays the landing buzz). */
export interface SwarmArrived { type: "swarm.arrived"; socket: number }
/** Quest → Hive: hornet defeated. overshoot = they kept humming past 48 °C. */
export interface HornetCooked { type: "hornet.cooked"; overshoot: boolean }
/** Hive → Quest: the glasses judge's plea (shown on Beeswax's clipboard). */
export interface Plea { type: "plea"; text: string }

/** Play a line. Room lines play ONLY from Beeswax on the Quest; ear lines ONLY in the glasses. */
export interface Say {
  type: "say";
  channel: Channel;
  line_id: string | null; // pre-rendered (shipped in the Unity build / miniapp bundle)
  tts_id: string | null; //  live: room → streamed PCM on the bus; ear → audio_url
  audio_url: string | null; // ear lines that aren't embedded: https://<host>/audio/<id>.mp3
  caption: string;
}
/** Live room line: followed by binary TTS_PCM16 frames, then tts.end. */
export interface TtsStart { type: "tts.start"; tts_id: string; sample_rate: number; caption: string }
export interface TtsEnd { type: "tts.end"; tts_id: string }

/** Board → Hive → Quest: operator override of the hum thermometer (0..1). */
export interface HumSet { type: "hum.set"; value: number }
/** Board → Hive: force a scan verdict for an item (the operator twin of a live scan). */
export interface VerdictForce { type: "verdict.force"; item: Item }
/** Board → Hive: force-play a line. */
export interface SayForce { type: "say.force"; line_id: string; channel: Channel }

export type OrbisState = "off" | "priming" | "ready" | "failed" | "ended";
/** Board → Hive: switch the window layer; Hive → all: current state. Frames: wss://<host>/orbis/frames */
export interface OrbisRequest { type: "orbis.request"; want: "live" | "clips" | "off"; stage?: number }
export interface OrbisStatus { type: "orbis"; state: OrbisState; stage: number; fallback_clip: string | null }

/** Anyone → Hive → board/monitor: one measured hop. */
export interface Latency { type: "latency"; hop: string; ms: number; source: Role }
export interface LogMsg { type: "log"; text: string; source?: Role }
export interface ErrorMsg { type: "error"; message: string }

export type BusMessage =
  | Hello | Welcome | BeatSet | BeatRequest | ModeMsg | GroupReset
  | ScanStarted | ScanPhoto | ScanVerdict | SwarmArrived | HornetCooked | Plea
  | Say | TtsStart | TtsEnd | HumSet | VerdictForce | SayForce
  | OrbisRequest | OrbisStatus | Latency | LogMsg | ErrorMsg;

// ---------------------------------------------------------------- glasses HTTP API
// All glasses calls carry ?token=<HIVE_TOKEN> (or header x-hive-token).

/** POST /glasses/press: button pressed (sent BEFORE taking the photo). */
export interface PressRequest { pressed_at: number; button?: string; press_type?: "short" | "long" }
export interface PressResponse {
  allowed: boolean; //          false outside scan beats or while a scan is in flight
  scan_id: string | null;
  line_id: string | null; //    e.g. "ear_not_now" when not allowed
}

/** POST /glasses/scan: the photo (signed Mentra URL) and/or what the judge said. */
export interface ScanRequest { scan_id: string; photo_url?: string; heard?: string; pressed_at: number }
export interface ScanResponse {
  item: Item;
  verdict: Verdict;
  line_id: string | null; //   ear line to play from the miniapp bundle (if any)
  audio_url: string | null; // live ear line (mp3) if not embedded
  caption: string;
  path: ScanPath;
  ms: number;
}

/** POST /glasses/plea: final transcription during the plea beat. */
export interface PleaRequest { text: string }
/** POST /glasses/latency: miniapp-side hop timings. */
export interface LatencyRequest { hop: string; ms: number }

// ---------------------------------------------------------------- Claude director schema
/** What the scan model must return (structured output). The Hive maps it to sockets/lines. */
export interface DirectorOutput {
  item: Item;
  verdict: Verdict;
  confidence: number;
  seen: string; //  <= 8 words
  line: string | null; // <= 20 words, ONLY for items without a pre-rendered verdict line
}
