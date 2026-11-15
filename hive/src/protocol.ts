// HONEYMOON event contract: the one source of truth shared by the Hive, the Quest (Unity),
// the Beat Board, the monitor, and the Mentra miniapp. Change it only together with PLAN.md
// (log the change there) and unity/BeeswaxClient/BeeswaxProtocol.cs.
//
// v2 (concept v3 "Save the Hive"): stations + choices, voice in (mic audio -> transcript -> intent),
// Orbis window scenes, fx events. The v1 glasses scan messages stay for the later glasses mode.
// v2.1 (additive, still PROTOCOL_VERSION 2): operator ops (choice.request, fx.force, whatif.force),
// a score message, optional `want` on orbis.request, and custom what-ifs (topic "custom" + scene whatif_custom).
// v2.2 (additive, still PROTOCOL_VERSION 2): scenes forecast_peaceful and forecast_ravaged, the hook's forecast. The
// Quest asks for them during room_hook_1 (peaceful when the window opens, ravaged on "angry swarms"); appended, so
// every older scene keeps its stage index.

export const PROTOCOL_VERSION = 2;

// ---------------------------------------------------------------- roles & transport
// Bus: wss://<host>/bus?role=<Role>&token=<HIVE_TOKEN>
// Text frames are JSON messages (below). Binary frames are [kind: u8][payload...].
export type Role = "quest" | "board" | "monitor" | "glasses";

export const BINARY_KIND = {
  /** JPEG bytes of the latest glasses photo; belongs to the most recent `scan.photo`. */
  SCAN_PHOTO: 0x01,
  /** PCM16 little-endian mono chunk; belongs to the most recent `tts.start`. */
  TTS_PCM16: 0x02,
  /** Client -> Hive: PCM16 LE mono 16 kHz mic audio; belongs to the sender's open `listen.start`. */
  MIC_PCM16: 0x03,
} as const;
export type BinaryKind = (typeof BINARY_KIND)[keyof typeof BINARY_KIND];

// ---------------------------------------------------------------- beats (Unity owns the clock)
// Unity is the ONLY beat clock. It reports `beat.set`; the Hive/Board only send `beat.request`.
export const BEATS = [
  "pre", //     0  fitting, not on the clock
  "hook", //    1  0:00-0:15 hive on the real table, HONEYMOON-1 parked, "fix it or the swarm takes the Earth"
  "meet", //    2  0:15-0:35 Beeswax + the future window opens
  "smoker", //  3  0:35-1:05 station 1
  "feed", //    4  1:05-1:45 station 2 (tip the hive, then feed it)
  "wasps", //   5  1:45-2:20 station 3 (door size)
  "hornet", //  6  +30 s, only if ahead: fist + hum
  "heat", //    7  2:20-2:55 station 4
  "whatif", //  8  2:55-3:20 voice what-if (fire -> put it out)
  "escape", //  9  3:20-3:40 hexagons, door opens, score card
  "t0", //     10  3:40-4:15 TAKE ME TO THE MOON, Moon, crash back
  "map", //    11  "six more hives" locked-levels map
  "field", //  12  post-demo (glasses only, no Quest effects)
] as const;
export type BeatName = (typeof BEATS)[number];

/** Beats in which a glasses button press starts a scan (real-prop mode, later). Elsewhere: "not now". */
export const SCAN_BEATS: readonly BeatName[] = ["smoker", "feed", "wasps", "heat", "field"];

/** Beats in which voice is listened to (the what-if beat is the main one; any beat may ask questions). */
export const VOICE_BEATS: readonly BeatName[] = ["meet", "smoker", "feed", "wasps", "hornet", "heat", "whatif", "escape", "map", "field"];

// ---------------------------------------------------------------- stations & choices (v2)
export const STATIONS = ["smoker", "feed", "wasps", "heat"] as const;
export type Station = (typeof STATIONS)[number];

export const CHOICES = {
  smoker: ["swat", "spray_water", "smoker"],
  feed: ["store_honey", "sugar_syrup"],
  wasps: ["bigger_door", "smaller_door"],
  heat: ["deep_bowl", "pebble_dish", "nothing"],
} as const satisfies Record<Station, readonly string[]>;
export type Choice = (typeof CHOICES)[Station][number];

export const CORRECT_CHOICE: Record<Station, Choice> = {
  smoker: "smoker",
  feed: "sugar_syrup",
  wasps: "smaller_door",
  heat: "pebble_dish",
};

// ---------------------------------------------------------------- the future window (Orbis scenes)
// Each scene has a live Orbis prompt (hive/lines/orbis_scenes.json) AND a recorded fallback clip.
export const SCENES = [
  "meadow", //          start: a living meadow, the hive in it
  "smoker_settle", //   smoker: calm, bees head out to forage
  "smoker_angry", //    swat / spray water: the swarm gets angrier
  "feed_sick", //       store honey: the colony gets sick (mid-stream shift)
  "feed_recover", //    sugar syrup: recovers
  "wasps_raid", //      bigger door: wasps get in
  "wasps_leave", //     smaller door: wasps give up
  "heat_stress", //     nothing / deep bowl: hot, struggling, drowning risk
  "heat_drink", //      pebble dish: bees land and drink
  "whatif_fire", //     "what if a fire starts?"
  "fire_out", //        "put it out"
  "whatif_rain", //     COULD
  "whatif_flowers", //  COULD ("what if we plant clover?")
  "whatif_spray", //    COULD (pesticide drift)
  "whatif_winter", //   COULD
  "whatif_custom", //   an unplanned what-if: Claude writes a safe prompt (no recorded clip; clips mode keeps the last clip)
  "full_bloom", //      escape
  "moon", //            t0: grey dust, Earth rising
  "forecast_peaceful", // hook: a sunny Paris park with the Eiffel Tower (the window opens on it)
  "forecast_ravaged", //  hook, on "angry swarms": red sky, swarms, a giant laser-eyed bee topples the tower
] as const;
export type Scene = (typeof SCENES)[number];
export const WHATIF_TOPICS = ["fire", "rain", "flowers", "spray", "winter", "custom"] as const;
export type WhatIfTopic = (typeof WHATIF_TOPICS)[number];

/** Unity effect cues the Hive can trigger (Unity owns how they look). */
export const FX = ["embers", "embers_stop", "swarm_calm", "swarm_angry", "hive_glow_red", "hive_glow_off"] as const;
export type FxName = (typeof FX)[number];

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
export interface BeatSet {
  type: "beat.set";
  n: number;
  name: BeatName;
  /** Quest → Hive, Escape and later: the frozen station timer, in seconds. */
  score_seconds?: number;
}
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
/** Board|Quest → Hive: switch the window layer and/or scene; Hive → all: current state.
 *  Frames: wss://<host>/orbis/frames. `fallback_clip` = clip name for `scene` (Unity plays it when live is down). */
export interface OrbisRequest { type: "orbis.request"; want?: "live" | "clips" | "off"; stage?: number; scene?: Scene }
//   v2.1: `want` is optional. A request carrying `scene`/`stage` only switches the scene; only `want` changes live/clips/off.
export interface OrbisStatus {
  type: "orbis";
  state: OrbisState;
  stage: number;
  scene: Scene | null;
  fallback_clip: string | null;
  /** v2.3 (additive): show this scene's recorded clip for this many seconds before live frames, even while they flow
   *  (a question's window moment: the clip answers at once, the live stream catches up with a cut). */
  clip_first_s?: number;
}

/** Anyone → Hive → board/monitor: one measured hop. */
export interface Latency { type: "latency"; hop: string; ms: number; source: Role | "hive" }

// ---------------------------------------------------------------- v2: stations, voice, scenes, fx
/** Quest → Hive (authoritative): the player made a choice at a station. Hive -> window scene +
 *  `say` (room line from hive/lines/stations.json) + memory; rebroadcast to board/monitor. */
export interface ChoiceMade { type: "choice.made"; station: Station; choice: Choice; correct: boolean }

/** Client (quest|glasses) → Hive: start/stop a voice utterance. Between them the client sends binary
 *  MIC_PCM16 frames (16 kHz mono). mode "ptt" = held button/pinch; "vad" = client-side voice detection. */
export interface ListenStart { type: "listen.start"; source: "quest" | "glasses"; mode: "ptt" | "vad"; wake_phrase?: boolean }
export interface ListenEnd { type: "listen.end"; source: "quest" | "glasses"; cancel?: boolean }
/** Hive → all: speech-to-text result. */
export interface Transcript { type: "transcript"; text: string; final: boolean; source: "quest" | "glasses"; ms: number; stt: string }

export const INTENTS = ["what_if", "undo", "ask", "ack", "unknown"] as const;
export type IntentName = (typeof INTENTS)[number];
/** Hive → all: what the player meant. what_if -> window scene + fx + line; undo ("put it out") -> back;
 *  ask -> live Beeswax answer (tts); ack/unknown -> a canned acknowledgement. */
export interface Intent {
  type: "intent"; name: IntentName; topic: WhatIfTopic | null; text: string; path: "keyword" | "claude"; ms: number;
  /** topic "custom" only: the safe Orbis prompt Claude wrote, or null when it refused (Beeswax says the refusal line). */
  custom_prompt?: string | null;
}

/** Hive → Quest: play an effect. A later `embers_stop` cancels any `embers` still waiting on its delay. */
export interface Fx { type: "fx"; name: FxName; delay_ms?: number; duration_ms?: number }

/** Board → Hive → Quest (v2.1): operator twin of a player's station choice. Unity applies it exactly as if the player
 *  chose it (hexagon, swarm, line) and then reports `choice.made` as usual; Unity may ignore it outside that station's beat. */
export interface ChoiceRequest { type: "choice.request"; station: Station; choice: Choice }
/** Board → Hive → Quest (v2.1): force an effect. */
export interface FxForce { type: "fx.force"; name: FxName }
/** Board → Hive (v2.1): operator twin of a voice what-if (topic) or "put it out" (undo: true); runs the same act() path. */
export interface WhatIfForce { type: "whatif.force"; topic?: WhatIfTopic; undo?: boolean }
/** Hive → all (v2.1): the run's score when the escape beat starts. */
export interface Score { type: "score"; elapsed_s: number; text: string; grievances: number }
export interface LogMsg { type: "log"; text: string; source?: Role }
export interface ErrorMsg { type: "error"; message: string }

export type BusMessage =
  | Hello | Welcome | BeatSet | BeatRequest | ModeMsg | GroupReset
  | ScanStarted | ScanPhoto | ScanVerdict | SwarmArrived | HornetCooked | Plea
  | Say | TtsStart | TtsEnd | HumSet | VerdictForce | SayForce
  | OrbisRequest | OrbisStatus | Latency | LogMsg | ErrorMsg
  | ChoiceMade | ListenStart | ListenEnd | Transcript | Intent | Fx
  | ChoiceRequest | FxForce | WhatIfForce | Score;

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

/** POST /glasses/utterance: a final transcription from the glasses' own speech-to-text (same intent
 *  pipeline as Quest voice). Reply: the ear line to play, if any. */
export interface UtteranceRequest { text: string; at: number }
export interface UtteranceResponse { intent: IntentName; topic: WhatIfTopic | null; line_id: string | null; audio_url: string | null; caption: string }

// ---------------------------------------------------------------- Claude schemas
/** What the intent router model must return (structured output). Keyword fast path runs first. */
export interface RouterOutput {
  intent: IntentName;
  topic: WhatIfTopic | null; // only for what_if
  answer: string | null; //    only for ask: <= 25 words, Beeswax's voice, only vetted facts
  custom_prompt?: string | null; // v2.1, only for topic "custom": a safe Orbis scene prompt, or null to refuse
}

/** What the scan model must return (structured output). The Hive maps it to sockets/lines. */
export interface DirectorOutput {
  item: Item;
  verdict: Verdict;
  confidence: number;
  seen: string; //  <= 8 words
  line: string | null; // <= 20 words, ONLY for items without a pre-rendered verdict line
}
