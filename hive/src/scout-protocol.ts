// Live Bee Scout (branch scout/live): the contract between the vision worker, the Hive's scout game,
// the glasses and the /scout dashboard. Kept out of protocol.ts on purpose: the Unity C# mirror test
// covers that file and the Quest game doesn't need any of this.
// Change this file only through the orchestrator (PLAN.md task S19), never from a lane branch.
//
//   glasses camera --RTMP--> MediaMTX (laptop) --RTSP--> vision worker --POST /scout/frame--> Hive scout game
//                                     \--WebRTC--> /scout dashboard <--SSE /scout/events-- Hive scout game
//   Hive scout game --bus say{channel:"ear", line_id, audio_url, caption}--> glasses ear + LED
//   glasses --POST /scout/utterance, /scout/button--> Hive scout game (yes / no answers)

/** The six booth objects. Order = the order the lines file and the dashboard list them. */
export const SCOUT_LABELS = ["beehive", "native_flower", "honey_jar", "pebble_dish", "bug_spray", "bee_hotel"] as const;
export type ScoutLabel = (typeof SCOUT_LABELS)[number];
/** The negative class: tables, people, walls, screens. Never triggers anything. */
export const SCOUT_BACKGROUND = "background" as const;
export type ScoutClass = ScoutLabel | typeof SCOUT_BACKGROUND;

export const SCOUT_LABEL_NAMES: Record<ScoutLabel, string> = {
  beehive: "Beehive",
  native_flower: "Native flower",
  honey_jar: "Store honey",
  pebble_dish: "Pebble water dish",
  bug_spray: "Bug spray",
  bee_hotel: "Bee hotel / bare dirt",
};

// ------------------------------------------------------------------ network layout (local laptop)

export const SCOUT_PORTS = {
  hive: 8241, //        scout/live Hive (8240 is the codex/mentra-live-build Hive, leave it alone)
  rtmp: 1935, //        MediaMTX: the glasses publish rtmp://<laptop-LAN-ip>:1935/scout
  rtsp: 8554, //        MediaMTX: the vision worker reads rtsp://127.0.0.1:8554/scout
  webrtc: 8889, //      MediaMTX: the dashboard plays http://<host>:8889/scout/ (iframe or WHEP)
  vision: 8242, //      vision worker's own small HTTP API (health, enrol)
} as const;
export const SCOUT_STREAM_PATH = "scout";

// ------------------------------------------------------------------ vision worker -> Hive

/** POST /scout/frame (token), one per processed frame, about 3-10 a second. */
export interface ScoutFrame {
  at: number; //         epoch ms the frame was read from the stream
  frame_ms: number; //   inference time for this frame
  fps: number; //        processed frames per second, rolling
  /** Best first, at most 3. score is calibrated 0..1 (cosine-similarity based, see hive/scout-vision). */
  top: { label: ScoutClass; score: number }[];
  /** Optional: the score for every class (six labels + background), so the dashboard's bars are all real. */
  scores?: Partial<Record<ScoutClass, number>>;
  source: string; //     "rtsp://..." | "file:..." | "webcam:0"
}

/** The vision worker's own API on SCOUT_PORTS.vision (CORS *, no token: it binds 127.0.0.1 + LAN only). */
export interface VisionHealth { ok: true; model: string; source: string; fps: number; refs: Record<string, number> }
/** POST /enrol: grab frames from the live source for `seconds` and save them as references for `label`. */
export interface VisionEnrolRequest { label: ScoutClass; seconds: number }
export interface VisionEnrolResponse { label: ScoutClass; saved: number; total: number }
/** GET /slides.json: test images for the /scout/slides screen test (held-out photos, not the enrolled
 *  ones). Each url is absolute, served by the worker itself at GET /img/<label>/<file>. */
export interface VisionSlide { label: ScoutLabel; url: string; credit?: string }

/** The /scout and /scout/slides pages come from hive/src/scout-dashboard.ts (dashboard lane) and are
 *  served by the scout game's routes (game lane). Pages read ?token= from their own URL. */

// ------------------------------------------------------------------ glasses -> Hive

/** POST /scout/utterance (token): every final transcript while the glasses are in scout mode. */
export interface ScoutUtterance { text: string; at: number }
/** POST /scout/button (token): a short press on the glasses = "yes" (backup when voice fails). */
export interface ScoutButton { at: number }
/** POST /scout/start (token): the glasses entered scout mode (plays the intro, resets progress). */
export interface ScoutStart { at: number }

// ------------------------------------------------------------------ test harness -> Hive

/** POST /scout/slide (token): the /scout/slides page showed `label` (null = a blank gap) at `at`. */
export interface ScoutSlide { label: ScoutClass | null; at: number }

// ------------------------------------------------------------------ the game

export type ScoutPhase = "intro" | "looking" | "offering" | "telling" | "done";
export type ScoutAnswer = "yes" | "no" | "rude" | "other";

export interface ScoutState {
  phase: ScoutPhase;
  found: ScoutLabel[]; //              heard the full fact (said yes)
  skipped: ScoutLabel[]; //            offered and declined (offered again only after everything else)
  offer: ScoutLabel | null; //         the object currently being offered or told
  current: { label: ScoutClass; score: number } | null; // the latest frame's best guess
  started_at: number | null;
}

/** Hive -> dashboard over GET /scout/events (SSE, token). GET /scout/state returns ScoutState. */
export type ScoutEvent =
  | { type: "frame"; frame: ScoutFrame } //                                    throttled to ~5 a second
  | { type: "found"; label: ScoutLabel; score: number; at: number; look_ms: number } // steady match: chime + hook
  | { type: "say"; line_id: string; caption: string; audio_url: string | null; at: number }
  | { type: "heard"; text: string; answer: ScoutAnswer; at: number }
  | { type: "state"; state: ScoutState }
  | { type: "latency"; hop: ScoutHop; ms: number }
  | { type: "slide"; label: ScoutClass | null; at: number };

/** slide_to_found: slide shown -> steady match. frame_to_found: first frame of the run -> found.
 *  found_to_say: found -> the hook was sent to the glasses. answer_to_fact: "yes" heard -> fact sent. */
export type ScoutHop = "slide_to_found" | "frame_to_found" | "found_to_say" | "answer_to_fact";

/** Game timing and thresholds. The scout game owns the logic; these are the knobs. */
export const SCOUT_TUNING = {
  steadyMs: 900, //        the same label must lead for this long before it counts
  minScore: 0.6, //        and score at least this
  minMargin: 0.12, //      and beat the runner-up by this much
  offerTimeoutMs: 6000, // no answer to "Want the rest?" -> scout_silence, treated as no
  gapMs: 4000, //          at least this long between one line ending and the next chime
  idleMs: 20000, //        nothing found for this long while looking -> scout_idle (at most once a minute)
} as const;

// ------------------------------------------------------------------ lines

/** Line ids in hive/lines/scout_lines.json. Audio is served at GET /scout/audio/<id>.mp3. */
export const SCOUT_LINES = {
  intro: ["scout_intro_1", "scout_intro_2"],
  chime: "scout_chime", //                       the "Bee fact found!" sting (voice + sound), before every hook
  hook: (l: ScoutLabel) => `scout_hook_${l}`, // one line, ends with a question
  fact: (l: ScoutLabel) => `scout_fact_${l}`, // the deeper fact after "yes"
  no: ["scout_no_1", "scout_no_2"],
  rude: "scout_rude", //                         "be quiet", "go away", "shut up"
  silence: "scout_silence", //                   no answer within offerTimeoutMs
  idle: "scout_idle",
  progress: (found: number) => `scout_progress_${found}`, // after facts 1..5 ("Three down. Three to go.")
  done: "scout_done", //                         all six: the contract
} as const;

/** One entry in hive/lines/scout_lines.json. */
export interface ScoutLine {
  id: string;
  text: string; //   what Beeswax says (caption); "" for a pure sound effect
  kind: "intro" | "chime" | "hook" | "fact" | "no" | "rude" | "silence" | "idle" | "progress" | "done";
  label?: ScoutLabel;
  source?: string; // the URL backing the fact (facts only)
}
