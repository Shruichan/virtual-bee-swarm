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
  /** Optional: detected objects in this frame, coordinates normalized 0..1 (x,y = top-left). For the
   *  dashboard's bounding boxes; `top`/`scores` still drive the game. `via` = how it was found. */
  boxes?: ScoutBox[];
  source: string; //     "rtsp://..." | "file:..." | "webcam:0"
}

export interface ScoutBox { label: ScoutClass; score: number; x: number; y: number; w: number; h: number; via?: "detector" | "marker" | "refs" }

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
/** POST /scout/watchdog (token): the glasses ask how long ago the vision worker last posted a frame.
 *  null = no frame yet since the Hive started. The glasses restart their stream when it goes stale. */
export interface ScoutWatchdog { frame_age_ms: number | null }
/** POST /scout/start (token): the glasses entered scout mode (plays the intro, resets progress). */
export interface ScoutStart { at: number }

// ------------------------------------------------------------------ test harness -> Hive

/** POST /scout/slide (token): the /scout/slides page showed `label` (null = a blank gap) at `at`. */
export interface ScoutSlide { label: ScoutClass | null; at: number }

// ------------------------------------------------------------------ the game

/** answering: Ask Beeswax (scout/ask) is answering a question; frames are ignored until it ends. */
export type ScoutPhase = "intro" | "looking" | "offering" | "telling" | "done" | "answering";
export type ScoutAnswer = "yes" | "no" | "rude" | "other";

export interface ScoutState {
  phase: ScoutPhase;
  found: ScoutLabel[]; //              heard the full fact (said yes)
  skipped: ScoutLabel[]; //            offered and declined (offered again only after everything else)
  offer: ScoutLabel | null; //         the object currently being offered or told
  current: { label: ScoutClass; score: number } | null; // the latest frame's best guess
  started_at: number | null;
  /** Practice mode (screen tests): any object can trigger again once the slide changes. */
  practice?: boolean;
  /** Booth mode (judge demo): which mode, the station order, and the station Beeswax is waiting for. */
  mode?: ScoutMode;
  order?: ScoutLabel[];
  next?: ScoutLabel | null;
}

/** Why a steadily seen object did NOT trigger "Bee fact found". */
export type ScoutBlockReason = "already_found" | "skipped" | "busy" | "cooldown" | "not_next"; // not_next: booth, another station

/** POST /scout/practice (token): turn practice mode on or off. Returns ScoutState. */
export interface ScoutPractice { on: boolean }

// ------------------------------------------------------------------ interruptions, Ask Beeswax, teacher button

/** The bus `say` that tells the glasses to cut the ear (stop the current line, drop the queue):
 *  {type:"say", channel:"ear", line_id:null, tts_id:SCOUT_STOP_ID, audio_url:null, caption:""}. Glasses in scout mode
 *  call Ear.clear() on it and play nothing; older builds see an empty say and play nothing either. */
export const SCOUT_STOP_ID = "scout_stop";

/** POST /scout/teacher (token): Beeswax says SCOUT_TEACHER_LINE (the judge screen's button). Returns ScoutState. */
export const SCOUT_TEACHER_LINE = "scout_teacher";

// ------------------------------------------------------------------ booth mode (pitch/scout-booth.md)

/** demo: the free-roam game (hook, yes/no). practice: screen tests (chime + fact, no offer).
 *  booth: the scripted judge walk. Stations in a fixed order; only the expected-next object can trigger. */
export type ScoutMode = "demo" | "practice" | "booth";

/** POST /scout/mode (token): switch mode. `order` (booth only, optional) replaces the station order.
 *  Booth without `order` uses SCOUT_BOOTH_JUDGE_ORDER. Switching to booth restarts at the first station (no intro; POST /scout/reset plays it). Returns ScoutState.
 *  Env SCOUT_MODE=demo|practice|booth sets the startup default (SCOUT_PRACTICE=1 still means practice),
 *  SCOUT_ORDER=native_flower,honey_jar,... the booth order. */
export interface ScoutModeRequest { mode: ScoutMode; order?: ScoutLabel[] }

/** Operator keys (token), any mode unless noted. Each returns ScoutState.
 *   POST /scout/force {label?}  found right now for `label` (default: booth's next station), no dwell, no gap.
 *   POST /scout/yes             = the wearer said yes (only while offering).
 *   POST /scout/next            booth: skip to the next station (the current one counts as skipped).
 *   POST /scout/reset           start over: progress cleared, intro plays (= /scout/start). */
export interface ScoutForce { label?: ScoutLabel }

/** The walking order from the run-of-show: four judge stations, then the two optional ones. */
export const SCOUT_BOOTH_ORDER: readonly ScoutLabel[] = ["native_flower", "honey_jar", "bug_spray", "bee_hotel", "pebble_dish", "beehive"];
/** Judge mode: the first four stations. Booth mode uses this unless an `order` is passed (POST /scout/mode, SCOUT_ORDER),
 *  so scout_booth_done lands after station 4. Pass SCOUT_BOOTH_ORDER for all six. */
export const SCOUT_BOOTH_JUDGE_ORDER: readonly ScoutLabel[] = SCOUT_BOOTH_ORDER.slice(0, 4);

export const SCOUT_BOOTH_TUNING = {
  offerTimeoutMs: 4000, // no answer to scout_chime_ask -> scout_silence_yes, then the fact (silence = yes)
  gapMs: 1500, //          after a station's lines end, before the next chime (direction cues don't count)
  cueEveryMs: 8000, //     at most one direction cue (scout_dir_*) this often
  lostMs: 10_000, //       expected object not seen for this long -> scout_dir_lost
  // Direction cues from the expected object's most central box (normalized 0..1, 1280x720 stream):
  leftX: 0.375, //         box center x below this -> scout_dir_left
  rightX: 0.625, //        above this -> scout_dir_right
  topY: 0.25, //           box center y above this line (top of frame; the camera aims ~15 deg below gaze) -> scout_dir_up (lift your head)
  minW: 0.0625, //         box narrower than this (80 px) -> scout_dir_closer
} as const;

/** Booth line ids. Placeholders live in hive/lines/scout_lines_booth.json (loaded after scout_lines.json; it wins
 *  on an id clash). An empty text skips the line (e.g. when the facts already end with the direction). */
export const SCOUT_BOOTH_LINES = {
  intro: "scout_booth_intro",
  chimeAsk: "scout_chime_ask", //                  "Bee fact found! Would you like to learn more?" (one line)
  silenceYes: "scout_silence_yes", //              "I'll take that as a yes."
  no: "scout_booth_no", //                         short reply to "no", then on to the next station anyway
  rude: "scout_rude", //                           shared with demo mode
  next: (l: ScoutLabel) => `scout_next_${l}`, //   direction to station `l` ("One big step to your left.")
  done: "scout_booth_done", //                     after the last station's fact
  dir: { left: "scout_dir_left", right: "scout_dir_right", closer: "scout_dir_closer", up: "scout_dir_up", lost: "scout_dir_lost" },
} as const;

/** Hive -> dashboard over GET /scout/events (SSE, token). GET /scout/state returns ScoutState. */
export type ScoutEvent =
  | { type: "frame"; frame: ScoutFrame } //                                    throttled to ~5 a second
  | { type: "found"; label: ScoutLabel; score: number; at: number; look_ms: number } // steady match: chime + hook
  | { type: "say"; line_id: string; caption: string; audio_url: string | null; at: number }
  | { type: "heard"; text: string; answer: ScoutAnswer; at: number }
  | { type: "state"; state: ScoutState }
  | { type: "latency"; hop: ScoutHop; ms: number }
  | { type: "slide"; label: ScoutClass | null; at: number }
  /** Beeswax was cut off (ScoutGame.interrupt(), POST /scout/reset): the glasses ear dropped what it was playing. */
  | { type: "stop"; at: number }
  /** An object was seen steadily (>= steadyMs) but stayed silent, once per look. */
  | { type: "blocked"; label: ScoutLabel; reason: ScoutBlockReason; score: number; at: number };

/** slide_to_found: slide shown -> steady match. frame_to_found: first frame of the run -> found.
 *  found_to_say: found -> the hook was sent to the glasses. answer_to_fact: "yes" heard -> fact sent. */
export type ScoutHop = "slide_to_found" | "frame_to_found" | "found_to_say" | "answer_to_fact";

/** Game timing and thresholds. The scout game owns the logic; these are the knobs. */
export const SCOUT_TUNING = {
  steadyMs: 500, //        the same label must lead for this long before it counts
  markerSteadyMs: 250, //  ...or this long when a `boxes` entry with via "marker" (AprilTag) backs it: a tag is certain
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
  kind: "intro" | "chime" | "hook" | "fact" | "no" | "rude" | "silence" | "idle" | "progress" | "done" | "direction" | "teacher";
  label?: ScoutLabel;
  source?: string; // the URL backing the fact (facts only)
}
