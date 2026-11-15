// Live Bee Scout, judge-facing booth screen (GET /scout/show?token=...). The operator view stays at /scout
// (scout-dashboard.ts); this page shows only what the crowd needs: the glasses camera with detection boxes,
// the "Bee fact found!" moment, what Beeswax says and what the wearer said. Artwork comes from
// hive/scout-assets/ via GET /scout/assets/<file> (no token). Self-contained otherwise: no build step.
// Page query params (same as /scout):
//   token   the Hive token (passed on to /scout/events, /scout/state)
//   stream  the MediaMTX path to play (default "scout")
//   player  "iframe" = MediaMTX's own player page instead of the built-in WHEP player
//   webrtc  MediaMTX WebRTC base URL (default http://<page host>:8889)
//   video   play this video URL instead of the stream (dev harness only)
// Sound: off on every load. The page makes no sound at all until someone turns it on (button or M); the
// camera <video> never carries audio (no audio track is requested, and it stays muted). The glasses speak regardless.
//   boxes=1 draw the detection boxes (boxes=0 hides them); default off, B toggles, the choice is remembered here
// Keys: M = Mac sound on/off, G = glasses sound on/off (POST /scout/glasses-mute), B = boxes on/off, F = full screen, Esc = close the lessons panel.
import { existsSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { SCOUT_BACKGROUND, SCOUT_BOOTH_ORDER, SCOUT_LABELS, SCOUT_PORTS, SCOUT_STREAM_PATH, type ScoutLabel } from "./scout-protocol";

/** What the crowd reads: the names printed on the station cards (scout-vision/detect/cards.json). */
const SHOW_NAMES: Record<ScoutLabel, string> = {
  beehive: "Beehive",
  native_flower: "Sunflower",
  honey_jar: "Honeycomb",
  pebble_dish: "Water dish",
  bug_spray: "Bug spray",
  bee_hotel: "Bee hotel",
};
/** One short line under the name in the found reveal. */
const SHOW_TAGS: Record<ScoutLabel, string> = {
  beehive: "Home sweet home",
  native_flower: "Lunch for bees",
  honey_jar: "Where bees keep their honey",
  pebble_dish: "A drinking fountain for bees",
  bug_spray: "Bad news for bees",
  bee_hotel: "A home for wild bees",
};
const CARD_FILE = (l: ScoutLabel) => `card-${l.replace(/_/g, "-")}.webp`;

const CONFIG = JSON.stringify({
  LABELS: SCOUT_LABELS,
  SLOTS: SCOUT_BOOTH_ORDER, // badge case order = the walking order
  NAMES: SHOW_NAMES,
  TAGS: SHOW_TAGS,
  CARDS: Object.fromEntries(SCOUT_LABELS.map((l) => [l, CARD_FILE(l)])),
  BACKGROUND: SCOUT_BACKGROUND,
  PORTS: SCOUT_PORTS,
  STREAM: SCOUT_STREAM_PATH,
});

// The page background: the daisies and sage sprigs from the logo, scattered thinly.
function daisy(cx: number, cy: number, r: number, rot: number, outline = ""): string {
  let petals = "";
  for (let i = 0; i < 10; i++) {
    petals += `<ellipse cx="${cx}" cy="${cy - r * 0.62}" rx="${r * 0.2}" ry="${r * 0.46}" transform="rotate(${rot + i * 36} ${cx} ${cy})"/>`;
  }
  return `<g fill="#fbfaf6"${outline ? ` stroke="${outline}" stroke-width="3"` : ""}>${petals}</g><circle cx="${cx}" cy="${cy}" r="${r * 0.24}" fill="#e7b46a"/>`;
}
function sprig(x: number, y: number, rot: number, s: number): string {
  const leaf = (lx: number, ly: number, a: number) => `<ellipse cx="${lx}" cy="${ly}" rx="2.6" ry="6" transform="rotate(${a} ${lx} ${ly})"/>`;
  return `<g transform="translate(${x} ${y}) rotate(${rot}) scale(${s})" fill="#9db394" stroke="#9db394"><path d="M0 0 Q 14 -18 36 -30" fill="none" stroke-width="1.6"/>${leaf(8, -12, -20)}${leaf(14, -4, 70)}${leaf(20, -21, -10)}${leaf(27, -14, 75)}${leaf(33, -32, 10)}</g>`;
}
const BG_SVG =
  `<svg xmlns="http://www.w3.org/2000/svg" width="420" height="420" viewBox="0 0 420 420"><g opacity=".6">` +
  daisy(70, 80, 34, 8) + daisy(300, 60, 15, 20) + daisy(350, 300, 30, 2) + daisy(150, 330, 14, 30) + daisy(220, 200, 9, 0) + daisy(30, 250, 10, 14) +
  sprig(170, 70, -10, 1.1) + sprig(40, 180, 40, 0.9) + sprig(250, 360, -30, 1) + sprig(330, 170, 60, 0.8) +
  `</g></svg>`;
const BG_URL = `url("data:image/svg+xml,${encodeURIComponent(BG_SVG)}")`;
const LESSON_ICONS: Record<string, string> = {
  bees: `<svg viewBox="0 0 64 64"><ellipse cx="32" cy="36" rx="17" ry="14" fill="#f2c230" stroke="#55290f" stroke-width="3"/><path d="M25 23v26M36 22v28" stroke="#55290f" stroke-width="5"/><ellipse cx="24" cy="18" rx="9" ry="6" fill="#fff" stroke="#55290f" stroke-width="2.5" transform="rotate(-25 24 18)"/><ellipse cx="40" cy="17" rx="9" ry="6" fill="#fff" stroke="#55290f" stroke-width="2.5" transform="rotate(25 40 17)"/></svg>`,
  butterflies: `<svg viewBox="0 0 64 64"><path d="M32 30C22 12 6 14 10 28c3 9 14 8 22 4zM32 30c10-18 26-16 22-2-3 9-14 8-22 4zM32 34c-8 2-16 6-14 16 2 7 12 2 14-8zM32 34c8 2 16 6 14 16-2 7-12 2-14-8z" fill="#e58f5a" stroke="#55290f" stroke-width="2.5" stroke-linejoin="round"/><path d="M32 22v26M32 22l-5-8M32 22l5-8" stroke="#55290f" stroke-width="3" stroke-linecap="round"/></svg>`,
  compost: `<svg viewBox="0 0 64 64"><path d="M14 40h36l-4 16H18z" fill="#9b6a43" stroke="#55290f" stroke-width="2.5" stroke-linejoin="round"/><path d="M32 40V24" stroke="#55290f" stroke-width="3" stroke-linecap="round"/><path d="M32 30c-12 0-16-8-16-14 10 0 16 4 16 14zM32 26c0-10 6-15 16-15 0 8-6 15-16 15z" fill="#8fa886" stroke="#55290f" stroke-width="2.5" stroke-linejoin="round"/></svg>`,
  water: `<svg viewBox="0 0 64 64"><path d="M32 8C24 22 16 30 16 40a16 16 0 0 0 32 0c0-10-8-18-16-32z" fill="#7fb3d5" stroke="#55290f" stroke-width="3" stroke-linejoin="round"/><path d="M24 42a8 8 0 0 0 7 8" stroke="#fff" stroke-width="3" fill="none" stroke-linecap="round"/></svg>`,
  birds: `<svg viewBox="0 0 64 64"><path d="M12 38c0-12 10-20 22-20 8 0 12 4 14 8l8 2-8 4c0 12-10 20-22 20-8 0-14-6-14-14z" fill="#d9822b" stroke="#55290f" stroke-width="3" stroke-linejoin="round"/><path d="M20 38c6 6 14 6 20 0" stroke="#55290f" stroke-width="2.5" fill="none" stroke-linecap="round"/><circle cx="40" cy="26" r="2.6" fill="#55290f"/></svg>`,
};
const LOCK_ICON = `<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10.5" width="14" height="10" rx="2.5" fill="currentColor"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5" fill="none" stroke="currentColor" stroke-width="2.4"/></svg>`;
const LESSONS = [
  ["butterflies", "Butterflies and moths"],
  ["compost", "Composting"],
  ["water", "Clean water"],
  ["birds", "Backyard birds"],
]
  .map(([icon, title]) => `<li class="lesson locked"><span class="lic">${LESSON_ICONS[icon!]}</span><b>${title}</b><small>${LOCK_ICON}Coming soon: your teacher can assign this</small></li>`)
  .join("");
const DAISY_ICON = `<svg viewBox="0 0 100 100" aria-hidden="true">${daisy(50, 50, 44, 0, "#c9a77f")}</svg>`;

// NOTE: the inline script avoids backticks and "${" so it can live inside this template string.
const SHOW = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Live Bee Scout</title>
<link rel="icon" href="/scout/assets/beeswax-front.webp">
<link rel="preload" href="/scout/assets/bagel-fat-one.woff2" as="font" type="font/woff2" crossorigin>
<link rel="preload" href="/scout/assets/baloo-2.woff2" as="font" type="font/woff2" crossorigin>
<style>
  @font-face { font-family: "Bagel Fat One"; src: url(/scout/assets/bagel-fat-one.woff2) format("woff2"); font-weight: 400; font-display: swap; }
  @font-face { font-family: "Baloo 2"; src: url(/scout/assets/baloo-2.woff2) format("woff2"); font-weight: 500 800; font-display: swap; }
  :root {
    --greige: #d4d0c9;   /* the logo's backdrop, exactly, so the hero bee sits on the page itself */
    --paper: #fffdf8;    /* daisy petals */
    --butter: #f5dca6;   /* Beeswax's body */
    --honey: #e3a83c;    /* daisy hearts, the found moment */
    --brown: #55290f;    /* stripes, all text */
    --rust: #b4561f;     /* antennae */
    --sage: #8fa886;     /* sprigs */
    --sage-soft: #e3eadb;
    --ink-soft: #7c5a43;
    --display: "Bagel Fat One", "Baloo 2", ui-rounded, "Arial Rounded MT Bold", sans-serif;
    --text: "Baloo 2", ui-rounded, "Arial Rounded MT Bold", system-ui, sans-serif;
  }
  * { box-sizing: border-box; }
  html { font-size: min(1.85vh, 1.04vw); }
  html, body { margin: 0; height: 100%; overflow: hidden; }
  body { background: ${BG_URL} 0 0 / 30rem 30rem, var(--greige); color: var(--brown); font: 600 1rem/1.25 var(--text);
    display: grid; grid-template-rows: auto minmax(0, 1fr); }
  img { display: block; }
  button { font: inherit; color: inherit; }

  /* ---------------------------------------------------------------- header */
  header { display: flex; align-items: center; gap: 1.2rem; padding: 1.4rem 2.2rem .6rem; }
  .brand { display: flex; align-items: center; gap: 1rem; transition: opacity .5s; }
  .badge { width: 4.4rem; height: 4.4rem; border-radius: 50%; object-fit: cover; border: .3rem solid var(--paper);
    box-shadow: 0 0 0 .14rem var(--brown), 0 .4rem 1rem rgba(85, 41, 15, .18); }
  h1 { margin: 0; font: 400 2.6rem/1 var(--display); letter-spacing: .01em; color: var(--brown); text-shadow: .12rem .14rem 0 var(--butter); }
  .tools { margin-left: auto; display: flex; align-items: center; gap: .9rem; }
  #today { display: flex; align-items: center; gap: .55rem; padding: .45rem 1.2rem .45rem .5rem; border-radius: 99rem; background: var(--paper);
    box-shadow: 0 0 0 .12rem rgba(85, 41, 15, .14); font-size: 1.25rem; font-weight: 700; color: var(--ink-soft); }
  #today svg { width: 2.4rem; height: 2.4rem; }
  #today b { font: 800 1.7rem/1 var(--text); color: var(--brown); margin-right: .1rem; }
  #today.zero { visibility: hidden; }
  #today.bump svg { animation: spinpop .9s cubic-bezier(.2, 1.6, .4, 1); }
  @keyframes spinpop { from { transform: rotate(-120deg) scale(.4); } }
  /* the badge case: six slots, faint silhouettes until earned */
  #case { flex: 1; display: flex; justify-content: center; min-width: 0; transition: opacity .5s; }
  body.attract #case { opacity: 0; }
  #case ol { display: flex; gap: .75rem; margin: 0; padding: .5rem .9rem; list-style: none; border-radius: 99rem;
    background: rgba(255, 253, 248, .5); box-shadow: inset 0 0 0 .12rem rgba(85, 41, 15, .12); }
  .slot { position: relative; width: 4.9rem; height: 4.9rem; border-radius: 50%; border: .18rem dashed rgba(85, 41, 15, .3); }
  .slot > img { position: absolute; inset: .3rem; width: calc(100% - .6rem); height: calc(100% - .6rem); border-radius: 50%; object-fit: cover;
    filter: grayscale(1) contrast(.7); opacity: .2; }
  .slot.got { border-color: transparent; }
  .slot.got > img { display: none; }
  .slot .medal { position: absolute; left: -.18rem; top: -.18rem; --s: 4.9rem; }
  .slot .medal.pop .disc { animation: medalpop .6s cubic-bezier(.2, 1.8, .4, 1); }
  @keyframes medalpop { 0% { transform: scale(1.5); } 100% { transform: none; } }
  /* a badge: gold rim, white ring, the card photo, a shine and two ribbon tails */
  .medal { position: relative; width: var(--s); height: var(--s); flex: none; }
  .medal .rib { position: absolute; left: 27%; width: 46%; top: 60%; height: 50%; background: linear-gradient(90deg, #c4632a 0 50%, #9c4718 50%);
    clip-path: polygon(0 0, 100% 0, 100% 100%, 75% 82%, 50% 100%, 25% 82%, 0 100%); }
  .medal .disc { position: absolute; inset: 0; border-radius: 50%; padding: 9%;
    background: conic-gradient(from 210deg, #fbe3a1, #e3a83c 25%, #b97a1e 45%, #f5cf74 65%, #e3a83c 85%, #fbe3a1);
    box-shadow: 0 0 0 calc(var(--s) * .028) var(--brown), 0 calc(var(--s) * .06) calc(var(--s) * .12) rgba(85, 41, 15, .28); }
  .medal img { display: block; width: 100%; height: 100%; border-radius: 50%; object-fit: cover; box-shadow: 0 0 0 calc(var(--s) * .035) var(--paper); }
  .medal .disc::after { content: ""; position: absolute; inset: 9%; border-radius: 50%; pointer-events: none;
    background: linear-gradient(140deg, rgba(255, 255, 255, .6) 0 18%, rgba(255, 255, 255, 0) 38%); }
  .medal.fly { position: fixed; z-index: 30; transform-origin: 0 0; transition: transform .9s cubic-bezier(.55, -0.25, .3, 1); }
  .medal.fly .disc { animation: medalin .55s cubic-bezier(.2, 1.6, .4, 1); }
  .medal.fly .disc::after { animation: shine 1s ease-out .3s; }
  .medal.fly .rib { animation: ribin .5s ease-out .25s backwards; }
  @keyframes medalin { from { transform: scale(.7) rotate(-20deg); } }
  @keyframes shine { from { transform: rotate(-120deg); opacity: 0; } 50% { opacity: 1; } }
  @keyframes ribin { from { transform: translateY(-40%); opacity: 0; } }
  /* two sound switches, stacked: this computer and the glasses */
  .sw { display: flex; flex-direction: column; gap: .35rem; }
  .tog { display: flex; align-items: center; gap: .45rem; padding: .3rem .95rem .3rem .7rem; border-radius: 99rem; border: 0; cursor: pointer; white-space: nowrap;
    background: rgba(255, 253, 248, .55); box-shadow: inset 0 0 0 .12rem rgba(85, 41, 15, .25); font-size: 1.05rem; font-weight: 700; color: var(--ink-soft); }
  .tog svg { width: 1.3rem; height: 1.3rem; flex: none; }
  .tog .on { display: none; }
  .tog b { font-weight: 800; color: var(--brown); }
  .tog[aria-pressed="true"] { background: var(--brown); color: var(--paper); box-shadow: none; }
  .tog[aria-pressed="true"] b { color: var(--butter); }
  .tog[aria-pressed="true"] .on { display: inline; } .tog[aria-pressed="true"] .off { display: none; }
  .tog:disabled { cursor: default; opacity: .5; }
  .tog:focus-visible { outline: .2rem solid var(--honey); outline-offset: .2rem; }

  /* ---------------------------------------------------------------- live layout */
  main { display: grid; grid-template-columns: minmax(0, 1fr) 30rem; gap: 2.2rem; padding: .8rem 2.2rem 2rem; min-height: 0; align-items: center; }
  .cam { min-width: 0; min-height: 0; height: 100%; display: flex; flex-direction: column; justify-content: center; gap: 1rem; }
  /* the camera is framed like one of the Beeswax stickers: white band, brown outline */
  #frame { position: relative; width: 100%; max-height: calc(100% - 4.6rem); aspect-ratio: 16 / 9; margin: 0 auto; border-radius: 1.6rem; overflow: hidden;
    background: #2b2018; border: .55rem solid var(--paper); box-shadow: 0 0 0 .16rem var(--brown), 0 1.4rem 2.8rem rgba(85, 41, 15, .22); }
  #frame video, #frame iframe { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; border: 0; background: #2b2018; }
  #hint { position: absolute; z-index: 4; left: 1rem; bottom: 1rem; padding: .3rem .9rem; border-radius: 99rem; background: rgba(43, 32, 24, .6); color: #fffdf8;
    font-size: 1.05rem; font-weight: 700; opacity: 0; transition: opacity .3s; pointer-events: none; }
  #hint.on { opacity: 1; }
  #boxes { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; z-index: 2; }
  #napping { position: absolute; inset: 0; z-index: 3; display: grid; place-content: center; justify-items: center; gap: .6rem; text-align: center;
    background: radial-gradient(circle at 50% 42%, #f2eee6, var(--greige) 80%); color: var(--ink-soft); font-size: 1.6rem; font-weight: 700; transition: opacity .6s; }
  #napping img { width: 13rem; animation: snooze 3.2s ease-in-out infinite; }
  @keyframes snooze { 50% { transform: translateY(.5rem) rotate(-2deg); } }
  .playing #napping { opacity: 0; pointer-events: none; }
  .under { display: flex; align-items: flex-start; gap: 1.4rem; min-height: 3.6rem; }
  .quiet { display: flex; align-items: center; gap: .45rem; margin-top: .4rem; padding: .45rem .9rem .45rem .7rem; border: 0; border-radius: 99rem; cursor: pointer;
    background: transparent; color: var(--ink-soft); font-size: 1.15rem; font-weight: 700; opacity: .7; white-space: nowrap; }
  .quiet svg { width: 1.3rem; height: 1.3rem; }
  .quiet:hover, .quiet:focus-visible { opacity: 1; background: rgba(255, 253, 248, .6); }
  .quiet.confirm { opacity: 1; background: var(--paper); color: var(--rust); box-shadow: 0 0 0 .12rem var(--rust); }
  button:focus-visible { outline: .2rem solid var(--honey); outline-offset: .2rem; }
  #more { align-self: flex-end; margin-top: 1.3rem; padding: .7rem 1.5rem; border: 0; border-radius: 99rem; cursor: pointer; background: var(--butter);
    color: var(--brown); font-size: 1.4rem; font-weight: 800; box-shadow: 0 0 0 .14rem var(--brown), 0 .3rem 0 var(--brown); }
  #more:active { transform: translateY(.2rem); box-shadow: 0 0 0 .14rem var(--brown), 0 .1rem 0 var(--brown); }
  /* "Ask Beeswax": the wearer's question sits above Beeswax, his answer fills the big bubble */
  #ask { position: relative; align-self: flex-end; max-width: 88%; margin-bottom: 1rem; padding: .6rem 1.3rem .7rem; border-radius: 1.2rem; background: var(--sage-soft);
    box-shadow: 0 0 0 .12rem rgba(85, 41, 15, .16); font-size: 1.7rem; font-weight: 700; line-height: 1.2; transition: opacity .4s; }
  #ask::after { content: ""; position: absolute; bottom: -.75rem; right: 2.4rem; border: .8rem solid transparent; border-bottom: 0; border-top-color: var(--sage-soft); }
  #ask small { display: block; font-size: 1.1rem; color: #5d7553; }
  #ask.gone { display: none; }
  #ask.fresh { animation: pop .5s cubic-bezier(.2, 1.6, .4, 1); }
  .asking .beeswax img { width: 7.5rem; height: 7.5rem; }
  @keyframes blink { 50% { opacity: .35; } }
  /* the wearer's words: a little bubble rising out of their camera view */
  @keyframes pop { from { transform: scale(.8); } }

  /* Beeswax and his speech bubble */
  .bee { align-self: center; display: flex; flex-direction: column; align-items: flex-start; min-height: 0; }
  .beeswax { display: flex; align-items: flex-end; gap: .6rem; margin-left: 1rem; }
  .beeswax img { width: 10rem; height: 10rem; transform-origin: 50% 90%; }
  .speaking .beeswax img { animation: chatter .5s ease-in-out infinite alternate; }
  @keyframes chatter { from { transform: translateY(0) rotate(-3deg); } to { transform: translateY(-.5rem) rotate(3deg); } }
  .beeswax span { margin-bottom: 1.4rem; font: 400 1.9rem/1 var(--display); color: var(--brown); text-shadow: .1rem .12rem 0 var(--butter); }
  #bubble { position: relative; width: 100%; margin-top: 1.1rem; padding: 1.6rem 1.9rem 1.8rem; border-radius: 2rem; background: var(--paper);
    box-shadow: 0 0 0 .16rem var(--brown), 0 1rem 2.4rem rgba(85, 41, 15, .16); }
  #bubble::before, #bubble::after { content: ""; position: absolute; left: 4.2rem; border: solid transparent; border-top: 0; }
  #bubble::before { top: -1.55rem; border-width: 0 1.25rem 1.55rem; border-bottom-color: var(--brown); margin-left: -.18rem; }
  #bubble::after { top: -1.3rem; border-width: 0 1.07rem 1.32rem; border-bottom-color: var(--paper); }
  #cap { margin: 0; font-size: 2.45rem; font-weight: 700; line-height: 1.2; }
  #cap.long { font-size: 2.1rem; }
  #cap.xlong { font-size: 1.8rem; }
  #bubble { max-height: 30rem; overflow: hidden; }
  #bubble.thinking #cap::after { content: "…"; animation: blink 1s ease-in-out infinite; }
  #cap span { display: inline-block; white-space: pre; opacity: 0; animation: word .35s ease-out forwards; }
  #cap span.seen { opacity: 1; animation: none; }
  @keyframes word { from { opacity: 0; transform: translateY(.4rem); } to { opacity: 1; transform: none; } }
  #bubble.idle #cap { color: var(--ink-soft); font-size: 2.1rem; }

  /* ---------------------------------------------------------------- attract (nobody wearing the glasses) */
  #attract { display: none; }
  body.attract main { display: none; }
  body.attract .brand { opacity: 0; }
  body.attract #attract { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); align-items: center; gap: 2rem; padding: 0 5rem 2rem 3rem; min-height: 0; }
  .hero { width: min(100%, 52rem); aspect-ratio: 1; object-fit: contain; justify-self: center; animation: float 6s ease-in-out infinite;
    -webkit-mask-image: radial-gradient(circle at 52% 46%, #000 54%, transparent 68%); mask-image: radial-gradient(circle at 52% 46%, #000 54%, transparent 68%); }
  @keyframes float { 50% { transform: translateY(-1.2rem) rotate(1deg); } }
  .pitch h2 { margin: 0; font: 400 3.4rem/1 var(--display); color: var(--brown); text-shadow: .14rem .16rem 0 var(--butter); }
  .pitch h2.big { font-size: 4.8rem; line-height: .98; margin-bottom: 1.2rem; }
  .pitch p { margin: 0 0 1.4rem; max-width: 34rem; font-size: 2rem; line-height: 1.3; font-weight: 600; color: var(--ink-soft); }
  .hello { display: flex; align-items: center; gap: 1rem; }
  #go { margin-top: 1.8rem; padding: 1.1rem 2.8rem 1.2rem; border: 0; border-radius: 99rem; cursor: pointer; background: var(--honey); color: var(--brown);
    font: 400 2.6rem/1 var(--display); box-shadow: 0 0 0 .2rem var(--brown), 0 .45rem 0 var(--brown), 0 1rem 2rem rgba(85, 41, 15, .25);
    animation: nudge 2.6s ease-in-out infinite; }
  #go:hover { background: #ecb54c; }
  #go:active { transform: translateY(.3rem); box-shadow: 0 0 0 .2rem var(--brown), 0 .15rem 0 var(--brown); animation: none; }
  @keyframes nudge { 0%, 70%, 100% { transform: none; } 80% { transform: translateY(-.35rem) rotate(-1deg); } 90% { transform: translateY(0) rotate(1deg); } }
  .hello img { width: 7rem; height: 7rem; flex: none; }
  .hello div { position: relative; padding: 1rem 1.4rem; border-radius: 1.4rem; background: var(--paper); box-shadow: 0 0 0 .14rem var(--brown);
    font-size: 1.7rem; font-weight: 700; line-height: 1.2; }
  .hello div::before { content: ""; position: absolute; left: -.95rem; top: 50%; margin-top: -.8rem; border: .8rem solid transparent; border-left: 0; border-right-color: var(--brown); border-right-width: .95rem; }
  .hello div::after { content: ""; position: absolute; left: -.72rem; top: 50%; margin-top: -.66rem; border: .66rem solid transparent; border-left: 0; border-right-color: var(--paper); border-right-width: .8rem; }

  /* ---------------------------------------------------------------- "Learn about something else": the teacher panel */
  #teacher { position: fixed; inset: 0; z-index: 15; display: grid; place-items: center; visibility: hidden; cursor: pointer; }
  #teacher .wash { position: absolute; inset: 0; background: rgba(212, 208, 201, .9); backdrop-filter: blur(.4rem); opacity: 0; transition: opacity .3s; }
  #teacher.on { visibility: visible; }
  #teacher.on .wash { opacity: 1; }
  .tbox { position: relative; width: min(92vw, 96rem); display: flex; flex-direction: column; align-items: center; gap: 2.4rem; transform: translateY(2rem); opacity: 0; transition: transform .4s cubic-bezier(.2, 1.4, .4, 1), opacity .3s; }
  #teacher.on .tbox { transform: none; opacity: 1; }
  .tsay { display: flex; align-items: center; gap: 1.4rem; }
  .tsay img { width: 10rem; height: 10rem; flex: none; animation: chatter .5s ease-in-out 6 alternate; }
  .tbub { position: relative; padding: 1.3rem 2rem; border-radius: 1.8rem; background: var(--paper); box-shadow: 0 0 0 .16rem var(--brown), 0 1rem 2rem rgba(85, 41, 15, .15); }
  .tbub::before { content: ""; position: absolute; left: -1.2rem; top: 50%; margin-top: -1rem; border: 1rem solid transparent; border-left: 0; border-right: 1.2rem solid var(--brown); }
  .tbub::after { content: ""; position: absolute; left: -.92rem; top: 50%; margin-top: -.82rem; border: .82rem solid transparent; border-left: 0; border-right: 1rem solid var(--paper); }
  #tcap { margin: 0; font: 400 3.2rem/1.05 var(--display); color: var(--brown); }
  .lessons { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 1.2rem; width: 100%; }
  .lesson { display: flex; flex-direction: column; align-items: center; text-align: center; gap: .5rem; padding: 1.4rem 1rem 1.2rem; border-radius: 1.6rem; background: var(--paper);
    box-shadow: 0 0 0 .14rem rgba(85, 41, 15, .25); }
  .lic { width: 6.4rem; height: 6.4rem; display: grid; place-items: center; border-radius: 50%; background: var(--butter); }
  .lic svg { width: 4.4rem; height: 4.4rem; }
  .lesson b { font-size: 1.6rem; font-weight: 800; line-height: 1.1; }
  .lesson small { display: flex; align-items: flex-start; justify-content: center; gap: .3rem; font-size: 1.1rem; font-weight: 700; line-height: 1.2; color: var(--ink-soft); }
  .lesson small svg { width: 1.1rem; height: 1.1rem; flex: none; margin-top: .05rem; }
  .lesson.locked { background: rgba(255, 253, 248, .7); }
  .lesson.locked .lic { background: #e6e1d8; filter: grayscale(.55); opacity: .85; }
  .lesson.now { box-shadow: 0 0 0 .2rem var(--honey), 0 .8rem 1.6rem rgba(227, 168, 60, .25); }
  .lesson.now small { color: var(--rust); }
  .tfoot { margin: 0; font-size: 1.5rem; font-weight: 700; color: var(--ink-soft); text-align: center; }
  #teacher .lesson { opacity: 0; transform: translateY(1rem); }
  #teacher.on .lesson { animation: up .4s ease-out forwards; }
  #teacher.on .lesson:nth-child(2) { animation-delay: .08s; } #teacher.on .lesson:nth-child(3) { animation-delay: .16s; }
  #teacher.on .lesson:nth-child(4) { animation-delay: .24s; } #teacher.on .lesson:nth-child(5) { animation-delay: .32s; }

  /* ---------------------------------------------------------------- all six: the finale */
  #celebrate { position: fixed; inset: 0; z-index: 25; display: grid; place-items: center; pointer-events: none; overflow: hidden; visibility: hidden; }
  #celebrate .wash { position: absolute; inset: 0; background: radial-gradient(circle at 50% 50%, rgba(255, 249, 235, .97), rgba(212, 208, 201, .95) 75%); opacity: 0; transition: opacity .4s; }
  #celebrate .rays { position: absolute; width: 150vmax; height: 150vmax; left: 50%; top: 52%; margin: -75vmax 0 0 -75vmax;
    background: repeating-conic-gradient(rgba(245, 220, 166, .7) 0 7deg, transparent 7deg 20deg);
    -webkit-mask-image: radial-gradient(circle, #000 10%, transparent 50%); mask-image: radial-gradient(circle, #000 10%, transparent 50%); opacity: 0; transition: opacity .5s; }
  #celebrate.on { visibility: visible; }
  #celebrate.on .wash, #celebrate.on .rays { opacity: 1; }
  #celebrate.on .rays { animation: spin 22s linear infinite; }
  #celebrate.out .cb { animation: away .5s ease-in forwards; } #celebrate.out .wash, #celebrate.out .rays { opacity: 0; }
  .cb { position: relative; display: flex; flex-direction: column; align-items: center; text-align: center; }
  .cblogo { width: 12rem; height: 12rem; border-radius: 50%; object-fit: cover; border: .6rem solid var(--paper); box-shadow: 0 0 0 .2rem var(--brown), 0 1rem 2rem rgba(85, 41, 15, .25);
    transform: scale(0); }
  #celebrate h2 { margin: 1.4rem 0 2rem; font: 400 6.2rem/1 var(--display); color: var(--brown); text-shadow: .22rem .26rem 0 var(--honey); opacity: 0; transform: scale(.4) rotate(-5deg); }
  #cbrow { display: flex; gap: 2rem; }
  #cbrow .medal { --s: 9.5rem; opacity: 0; transform: translateY(2rem) scale(.6); }
  #celebrate p { margin: 3.4rem 0 0; font-size: 2.2rem; font-weight: 700; color: var(--ink-soft); opacity: 0; }
  #celebrate.on .cblogo { animation: cardin .7s cubic-bezier(.2, 1.5, .4, 1) .1s forwards; }
  #celebrate.on h2 { animation: stamp .6s cubic-bezier(.2, 1.7, .4, 1) .35s forwards; }
  #celebrate.on #cbrow .medal { animation: up .5s cubic-bezier(.2, 1.6, .4, 1) forwards; }
  #celebrate.on p { animation: up .5s ease-out 1.6s forwards; }

  /* ---------------------------------------------------------------- the found moment */
  #reveal { position: fixed; inset: 0; z-index: 20; display: grid; place-items: center; pointer-events: none; overflow: hidden; visibility: hidden; }
  #reveal .wash { position: absolute; inset: 0; background: radial-gradient(circle at 50% 52%, rgba(255, 249, 235, .94), rgba(212, 208, 201, .9) 70%);
    opacity: 0; transition: opacity .35s; }
  #reveal .rays { position: absolute; width: 150vmax; height: 150vmax; left: 50%; top: 52%; margin: -75vmax 0 0 -75vmax;
    background: repeating-conic-gradient(rgba(245, 220, 166, .75) 0 7deg, transparent 7deg 20deg);
    -webkit-mask-image: radial-gradient(circle, #000 8%, transparent 46%); mask-image: radial-gradient(circle, #000 8%, transparent 46%); opacity: 0; transition: opacity .5s; }
  #reveal .rv { position: relative; display: flex; flex-direction: column; align-items: center; text-align: center; }
  #reveal h2 { margin: 0; font: 400 6.6rem/1 var(--display); color: var(--brown); text-shadow: .22rem .26rem 0 var(--honey);
    transform: scale(.3) rotate(-6deg); opacity: 0; }
  .rvcard { position: relative; margin: 1.8rem 0 1.4rem; width: 19rem; height: 19rem; border-radius: 50%; overflow: hidden; border: .8rem solid var(--paper);
    box-shadow: 0 0 0 .2rem var(--brown), 0 1.4rem 3rem rgba(85, 41, 15, .3); transform: scale(0) rotate(-14deg); background: var(--butter); }
  .rvcard img { width: 100%; height: 100%; object-fit: cover; }
  #rvname { font: 800 4rem/1 var(--text); color: var(--brown); opacity: 0; transform: translateY(1rem); }
  #rvtag { margin-top: .5rem; font-size: 2rem; font-weight: 700; color: var(--rust); opacity: 0; transform: translateY(1rem); }
  #reveal.on { visibility: visible; }
  #reveal.on .wash { opacity: 1; }
  #reveal.on .rays { opacity: 1; animation: spin 18s linear infinite; }
  #reveal.on h2 { animation: stamp .6s cubic-bezier(.2, 1.7, .4, 1) .05s forwards; }
  #reveal.on .rvcard { animation: cardin .75s cubic-bezier(.2, 1.5, .4, 1) .22s forwards; }
  #reveal.on #rvname { animation: up .45s ease-out .55s forwards; }
  #reveal.on #rvtag { animation: up .45s ease-out .7s forwards; }
  #reveal.out .rv { animation: away .45s ease-in forwards; }
  #reveal.out .wash, #reveal.out .rays { opacity: 0; }
  @keyframes spin { to { transform: rotate(360deg); } }
  @keyframes stamp { to { transform: none; opacity: 1; } }
  @keyframes cardin { to { transform: rotate(-4deg); } }
  @keyframes up { to { opacity: 1; transform: none; } }
  @keyframes away { to { opacity: 0; transform: scale(.85) translateY(-2rem); } }
  .petal { position: absolute; left: 50%; top: 52%; width: 1.5rem; height: 3.6rem; margin: -1.8rem 0 0 -.75rem; border-radius: 50%; background: var(--paper);
    box-shadow: 0 0 0 .1rem rgba(85, 41, 15, .12); opacity: 0; animation: fly 1.5s cubic-bezier(.12, .7, .3, 1) forwards; }
  .petal.dot { width: 1.3rem; height: 1.3rem; margin: -.65rem 0 0 -.65rem; background: var(--honey); box-shadow: none; }
  @keyframes fly { 0% { opacity: 1; transform: rotate(var(--a)) translateY(-4rem) scale(.4); }
    80% { opacity: 1; } 100% { opacity: 0; transform: rotate(calc(var(--a) + var(--spin))) translateY(var(--d)) scale(1); } }

  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { animation-duration: .01ms !important; animation-iteration-count: 1 !important; transition-duration: .01ms !important; }
    .petal { display: none; }
  }
</style>
</head>
<body class="attract">
<header>
  <div class="brand"><img class="badge" src="/scout/assets/logo.webp" alt=""><h1>Live Bee Scout</h1></div>
  <div id="case" aria-label="Badges earned"><ol id="slots"></ol></div>
  <div class="tools">
    <div id="today" title="Bee facts found on this screen today">${DAISY_ICON}<span><b id="todayn">0</b> <span id="todayt">bee facts found today</span></span></div>
    <div class="sw">
      <button id="sound" class="tog" type="button" aria-pressed="false" title="Beeswax's voice on this computer (M)">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path class="on" d="M15.5 9a4 4 0 0 1 0 6M18.5 6.5a7.5 7.5 0 0 1 0 11"/><path class="off" d="M16 9.5l5 5M21 9.5l-5 5"/>
      </svg><span>Mac sound: <b id="soundt">off</b></span></button>
      <button id="gsound" class="tog" type="button" aria-pressed="true" title="Beeswax's voice in the glasses (G)" disabled>
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path class="on" d="M15.5 9a4 4 0 0 1 0 6M18.5 6.5a7.5 7.5 0 0 1 0 11"/><path class="off" d="M16 9.5l5 5M21 9.5l-5 5"/>
      </svg><span>Glasses sound: <b id="gsoundt">&hellip;</b></span></button>
    </div>
  </div>
</header>
<main>
  <section class="cam">
    <div id="frame">
      <video id="video" autoplay muted playsinline></video>
      <canvas id="boxes"></canvas>
      <div id="hint" aria-live="polite"></div>
      <div id="napping"><img src="/scout/assets/beeswax-sleep.webp" alt=""><div id="naptext">Camera connecting&hellip;</div></div>
    </div>
    <div class="under">
      <button id="restart" class="quiet" type="button" title="Start the walk over: clears what was found">
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4.5 12a7.5 7.5 0 1 0 2.4-5.5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/><path d="M4 3.5v4.5h4.5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg><span id="restartt">Restart your journey</span></button>
    </div>
  </section>
  <aside class="bee">
    <div id="ask" class="gone"><small>You asked</small><span id="askq"></span></div>
    <div class="beeswax"><img id="bxi" src="/scout/assets/beeswax-side.webp" alt=""><span>Beeswax</span></div>
    <div id="bubble" class="idle"><p id="cap">Look right at something on the table. I&rsquo;ll tell you why it matters to bees.</p></div>
    <button id="more" type="button">Learn about something else</button>
  </aside>
</main>
<section id="attract">
  <img class="hero" src="/scout/assets/logo.webp" alt="Beeswax the bee">
  <div class="pitch">
    <h2>Live Bee Scout</h2>
    <h2 class="big">Put on the glasses and look around.</h2>
    <p>Look at something on the table. Beeswax spots it and tells you why it matters to bees.</p>
    <div class="hello"><img src="/scout/assets/beeswax-wave.webp" alt=""><div>Hi, I&rsquo;m Beeswax. I live in the glasses!</div></div>
    <button id="go" type="button">Start your adventure</button>
  </div>
</section>
<div id="teacher" role="dialog" aria-modal="true" aria-labelledby="tcap">
  <div class="wash"></div>
  <div class="tbox">
    <div class="tsay"><img src="/scout/assets/beeswax-front.webp" alt=""><div class="tbub"><p id="tcap">Ask your teacher for another assignment!</p></div></div>
    <ul class="lessons">
      <li class="lesson now"><span class="lic">${LESSON_ICONS.bees}</span><b>Bees</b><small>Lesson one: you&rsquo;re doing it now</small></li>
      ${LESSONS}
    </ul>
    <p class="tfoot">Bees are lesson one. A teacher can assign any topic, and Beeswax helps you explore it.</p>
  </div>
</div>
<div id="celebrate" aria-live="polite">
  <div class="wash"></div><div class="rays"></div>
  <div class="cb">
    <img class="cblogo" src="/scout/assets/logo.webp" alt="">
    <h2>Bee Scout badge earned!</h2>
    <div id="cbrow"></div>
    <p>You found all six. Beeswax is proud of you.</p>
  </div>
</div>
<div id="reveal" aria-live="polite">
  <div class="wash"></div><div class="rays"></div>
  <div class="rv">
    <h2>Bee fact found!</h2>
    <div class="rvcard"><img id="rvimg" alt=""></div>
    <div id="rvname"></div>
    <div id="rvtag"></div>
  </div>
</div>
<script>
(function () {
  "use strict";
  var CFG = __CONFIG__;
  var Q = new URLSearchParams(location.search);
  var token = Q.get("token") || "";
  var host = location.hostname || "localhost";
  var streamPath = Q.get("stream") || CFG.STREAM;
  var WEBRTC = (Q.get("webrtc") || ("http://" + host + ":" + CFG.PORTS.webrtc)).replace(/\\/+$/, "");
  var TQ = "token=" + encodeURIComponent(token);
  var ASSETS = "/scout/assets/";
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function name(l) { return CFG.NAMES[l] || String(l || "").replace(/_/g, " "); }
  function pct(s) { return Math.round(Math.max(0, Math.min(1, s || 0)) * 100) + "%"; }
  function restart(node, cls) { node.classList.remove(cls); void node.offsetWidth; node.classList.add(cls); }
  CFG.LABELS.forEach(function (l) { var i = new Image(); i.src = ASSETS + CFG.CARDS[l]; }); // warm the cache for the reveal
  ["beeswax-front.webp", "beeswax-side.webp"].forEach(function (f) { var i = new Image(); i.src = ASSETS + f; });

  var S = { phase: null, offer: null, found: [] };
  var lastActivity = 0, lastPlayingAt = 0, started = false, startAt = 0;

  // ------------------------------------------------------------------ attract vs live
  // The page always opens on the landing screen. "Start your adventure" switches to the live view for good
  // (no automatic return) and starts a fresh journey; "Restart your journey" brings the landing screen back.
  function tickMode() { document.body.classList.toggle("attract", !started); }
  function startAdventure() {
    if (started || Date.now() - startAt < 1500) return;
    started = true; startAt = Date.now(); lastActivity = Date.now();
    resetPage();
    fetch("/scout/reset?" + TQ, { method: "POST" }).catch(function () {}); // badges cleared, the intro plays in the glasses
    tickMode();
    if (!playing && !iframeMode && !fileVideo) retry(0); // reconnect the camera right away
  }
  function showLanding() { started = false; tickMode(); setTimeout(function () { $("go").focus(); }, 50); }
  $("go").addEventListener("click", startAdventure);

  // ------------------------------------------------------------------ facts found today (this screen, this browser)
  function dayKey() { var d = new Date(); return "lbs-found-" + d.getFullYear() + "-" + (d.getMonth() + 1) + "-" + d.getDate(); }
  function getToday() { try { return parseInt(localStorage.getItem(dayKey()) || "0", 10) || 0; } catch (e) { return memToday; } }
  var memToday = 0;
  function showToday(n) { $("today").classList.toggle("zero", !n); $("todayn").textContent = String(n); $("todayt").textContent = n === 1 ? "bee fact found today" : "bee facts found today"; }
  function bumpToday() {
    var n = getToday() + 1; memToday = n;
    try { localStorage.setItem(dayKey(), String(n)); } catch (e) {}
    showToday(n); restart($("today"), "bump");
  }
  showToday(getToday());

  // ------------------------------------------------------------------ detection boxes over the video
  var BOXCOL = { beehive: "#e3a83c", native_flower: "#f2c230", honey_jar: "#d9822b", pebble_dish: "#7fb3d5", bug_spray: "#e0604a", bee_hotel: "#9cc08a" };
  var bx = { list: [], at: 0, flash: null }, disp = {};
  var BOX_HOLD_MS = 600, BOX_FADE_MS = 400;
  var bc = $("boxes"), g = bc.getContext("2d");
  // Boxes are off unless asked for: ?boxes=1 / ?boxes=0, or B (remembered in this browser).
  var showBoxes = false;
  try { showBoxes = localStorage.getItem("lbs-boxes") === "1"; } catch (e) {}
  if (Q.get("boxes") === "1") showBoxes = true; else if (Q.get("boxes") === "0") showBoxes = false;
  var hintTimer = null;
  function setBoxes(on, hint) {
    showBoxes = !!on;
    try { localStorage.setItem("lbs-boxes", showBoxes ? "1" : "0"); } catch (e) {}
    if (!hint) return;
    var h = $("hint"); h.textContent = showBoxes ? "Boxes on" : "Boxes off"; h.classList.add("on");
    clearTimeout(hintTimer); hintTimer = setTimeout(function () { h.classList.remove("on"); }, 2000);
  }
  if (Q.get("boxes") === "1" || Q.get("boxes") === "0") setBoxes(showBoxes, false);
  function vrect(W, H) {
    var vw = video.videoWidth || 16, vh = video.videoHeight || 9, k = Math.min(W / vw, H / vh);
    return { x: (W - vw * k) / 2, y: (H - vh * k) / 2, w: vw * k, h: vh * k };
  }
  function rrect(x, y, w, h, r) { g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r); g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath(); }
  function onFrame(fr) {
    if (Array.isArray(fr.boxes)) {
      bx.list = fr.boxes.filter(function (b) { return b && b.label !== CFG.BACKGROUND && b.w > 0 && b.h > 0; });
      bx.at = Date.now();
    }
  }
  function drawBoxes() {
    var d = devicePixelRatio || 1, W = bc.clientWidth * d, H = bc.clientHeight * d;
    if (bc.width !== Math.round(W) || bc.height !== Math.round(H)) { bc.width = Math.round(W); bc.height = Math.round(H); }
    g.clearRect(0, 0, bc.width, bc.height);
    var now = Date.now(), age = now - bx.at, alpha = age < BOX_HOLD_MS ? 1 : Math.max(0, 1 - (age - BOX_HOLD_MS) / BOX_FADE_MS);
    if (alpha <= 0 || !bx.list.length || !playing || !showBoxes) { disp = {}; requestAnimationFrame(drawBoxes); return; }
    var r = vrect(bc.width, bc.height), rem = parseFloat(getComputedStyle(document.documentElement).fontSize) * d, seen = {}, count = {};
    var flash = bx.flash && now < bx.flash.until ? bx.flash.label : null, tags = [];
    bx.list.forEach(function (b) {
      count[b.label] = (count[b.label] || 0) + 1;
      var key = b.label + ":" + count[b.label];
      var t = { x: r.x + b.x * r.w, y: r.y + b.y * r.h, w: b.w * r.w, h: b.h * r.h }, p = disp[key];
      if (!p) p = disp[key] = t; else { p.x += (t.x - p.x) * 0.35; p.y += (t.y - p.y) * 0.35; p.w += (t.w - p.w) * 0.35; p.h += (t.h - p.h) * 0.35; }
      seen[key] = 1;
      var gold = flash === b.label, c = gold ? "#f5c04a" : BOXCOL[b.label] || "#f5dca6", lw = (gold ? 0.42 : 0.3) * rem, rad = 0.9 * rem;
      g.save(); g.globalAlpha = alpha;
      if (gold) { g.shadowColor = "rgba(245, 192, 74, .95)"; g.shadowBlur = 1.8 * rem; }
      g.strokeStyle = "#fffdf8"; g.lineWidth = lw + 0.3 * rem; rrect(p.x, p.y, p.w, p.h, rad); g.stroke(); // white sticker edge, readable on any scene
      g.shadowBlur = 0;
      g.strokeStyle = c; g.lineWidth = lw; rrect(p.x, p.y, p.w, p.h, rad); g.stroke();
      if (gold) { g.fillStyle = "rgba(245, 192, 74, .14)"; rrect(p.x, p.y, p.w, p.h, rad); g.fill(); }
      g.restore();
      tags.push({ b: b, p: p, gold: gold, c: c });
    });
    tags.sort(function (a, b2) { return (a.gold ? 1 : 0) - (b2.gold ? 1 : 0); }); // labels after all boxes, the found one on top
    tags.forEach(function (tg) {
      var b = tg.b, p = tg.p, gold = tg.gold, c = tg.c;
      g.save(); g.globalAlpha = alpha;
      var txt = (gold ? "Bee fact found! " : "") + name(b.label) + "  " + pct(b.score);
      var fs = Math.round((gold ? 1.55 : 1.35) * rem);
      g.font = "800 " + fs + "px 'Baloo 2', ui-rounded, sans-serif";
      var tw = g.measureText(txt).width + fs * 1.1, th = fs * 1.6, ty = p.y - th - 0.5 * rem;
      if (ty < r.y + 0.3 * rem) ty = p.y + 0.5 * rem; // no room above: tag inside the box
      var tx = Math.min(Math.max(p.x, r.x + 0.3 * rem), r.x + r.w - tw - 0.3 * rem);
      g.fillStyle = "#fffdf8"; rrect(tx - 0.15 * rem, ty - 0.15 * rem, tw + 0.3 * rem, th + 0.3 * rem, th / 2 + 0.15 * rem); g.fill();
      g.fillStyle = c; rrect(tx, ty, tw, th, th / 2); g.fill();
      g.fillStyle = "#3d1d0a"; g.textBaseline = "middle"; g.fillText(txt, tx + fs * 0.55, ty + th / 2 + 0.1 * rem);
      g.restore();
    });
    for (var k in disp) if (!seen[k]) delete disp[k];
    requestAnimationFrame(drawBoxes);
  }

  // ------------------------------------------------------------------ the found moment
  var revealTimer = null, outTimer = null;
  var REDUCED = matchMedia("(prefers-reduced-motion: reduce)").matches;
  function onFound(ev) {
    lastActivity = Date.now();
    if (!started) { if (earn(ev.label)) fillSlot(ev.label, false); return; } // landing screen: no overlays
    bx.flash = { label: ev.label, until: Date.now() + 8000 };
    bumpToday(); hideTeacher();
    var rv = $("reveal");
    $("rvimg").src = ASSETS + (CFG.CARDS[ev.label] || "logo.webp");
    $("rvname").textContent = name(ev.label);
    $("rvtag").textContent = CFG.TAGS[ev.label] || "";
    burst(rv, 34, 0.18);
    cancelFly();
    rv.classList.remove("out", "on"); void rv.offsetWidth; rv.classList.add("on");
    clearTimeout(revealTimer); clearTimeout(outTimer);
    revealUntil = Date.now() + 4700;
    revealTimer = setTimeout(function () { rv.classList.add("out"); outTimer = setTimeout(function () { rv.classList.remove("on", "out"); }, 500); }, 4200);
    if (earn(ev.label)) flyBadge(ev.label); // first time: the card becomes a badge and flies into the case
  }
  function burst(box, n, delay) {
    box.querySelectorAll(".petal").forEach(function (p) { p.remove(); });
    if (REDUCED) return;
    for (var i = 0; i < n; i++) {
      var p = el("i", "petal" + (i % 5 === 4 ? " dot" : ""));
      p.style.setProperty("--a", (i * (360 / n) + Math.random() * 8) + "deg");
      p.style.setProperty("--d", "-" + (24 + Math.random() * 26) + "rem");
      p.style.setProperty("--spin", (Math.random() * 120 - 60) + "deg");
      p.style.animationDelay = (delay + Math.random() * 0.2) + "s";
      box.appendChild(p);
    }
  }

  // ------------------------------------------------------------------ badges: state.found + found events, one per card
  var earned = [], journey = null, revealUntil = 0;
  function medal(label, size) {
    var m = el("div", "medal"); m.style.setProperty("--s", size + "px"); m.title = name(label);
    m.appendChild(el("i", "rib"));
    var d = el("span", "disc"), im = el("img"); im.src = ASSETS + (CFG.CARDS[label] || "logo.webp"); im.alt = name(label);
    d.appendChild(im); m.appendChild(d);
    return m;
  }
  CFG.SLOTS.forEach(function (l) {
    var li = el("li", "slot"); li.id = "slot_" + l; li.title = name(l);
    var im = el("img"); im.src = ASSETS + CFG.CARDS[l]; im.alt = ""; li.appendChild(im);
    $("slots").appendChild(li);
  });
  function earn(label) { if (!label || earned.indexOf(label) >= 0) return false; earned.push(label); return true; }
  function fillSlot(label, pop) {
    var li = $("slot_" + label);
    if (!li || li.classList.contains("got")) return;
    li.classList.add("got");
    var m = medal(label, li.offsetWidth || 64); m.style.removeProperty("--s");
    if (pop) m.classList.add("pop");
    li.appendChild(m);
  }
  var flyTimers = [], flyEl = null;
  function cancelFly() {
    flyTimers.forEach(clearTimeout); flyTimers = [];
    if (flyEl) { flyEl.remove(); flyEl = null; }
    earned.forEach(function (l) { fillSlot(l, false); }); // a badge cut short by the next find still lands
    $("reveal").querySelector(".rvcard").style.visibility = "";
  }
  function flyBadge(label) {
    var card = $("reveal").querySelector(".rvcard");
    flyTimers.push(setTimeout(function () { // the card turns into a badge...
      var r = card.getBoundingClientRect(), w = card.offsetWidth, x = r.left + r.width / 2 - w / 2, y = r.top + r.height / 2 - w / 2;
      var m = flyEl = medal(label, w); m.classList.add("fly"); m.style.left = x + "px"; m.style.top = y + "px";
      document.body.appendChild(m); card.style.visibility = "hidden";
      flyTimers.push(setTimeout(function () { // ...and flies into its slot
        var slot = $("slot_" + label), sr = slot.getBoundingClientRect();
        m.style.transform = "translate(" + (sr.left - x) + "px, " + (sr.top - y) + "px) scale(" + (sr.width / w) + ")";
        flyTimers.push(setTimeout(function () {
          m.remove(); flyEl = null; card.style.visibility = ""; fillSlot(label, true);
          if (earned.length >= CFG.SLOTS.length) flyTimers.push(setTimeout(celebrate, 400));
        }, 950));
      }, 1150));
    }, 2200));
  }
  function clearCase() {
    cancelFly(); earned = []; celebrated = false; hideCelebrate();
    CFG.SLOTS.forEach(function (l) { var li = $("slot_" + l); li.classList.remove("got"); var m = li.querySelector(".medal"); if (m) m.remove(); });
  }

  // ------------------------------------------------------------------ all six: "Bee Scout badge earned!"
  var celebrated = false, celebTimer = null, celebOut = null;
  function celebrate() {
    if (celebrated || !started) return;
    var wait = revealUntil - Date.now();
    if (wait > 0) { celebTimer = setTimeout(celebrate, wait); return; }
    celebrated = true; lastActivity = Date.now(); hideTeacher();
    var row = $("cbrow"); row.textContent = "";
    var list = earned.length ? earned : CFG.SLOTS;
    list.forEach(function (l, i) { var m = medal(l, 0); m.style.removeProperty("--s"); m.style.animationDelay = (0.8 + i * 0.12) + "s"; row.appendChild(m); });
    var c = $("celebrate"); burst(c, 44, 0.5);
    c.classList.remove("out", "on"); void c.offsetWidth; c.classList.add("on");
    clearTimeout(celebTimer); clearTimeout(celebOut);
    celebTimer = setTimeout(function () { c.classList.add("out"); celebOut = setTimeout(hideCelebrate, 500); }, 8000);
  }
  function hideCelebrate() { clearTimeout(celebTimer); clearTimeout(celebOut); $("celebrate").classList.remove("on", "out"); }

  // ------------------------------------------------------------------ Beeswax says
  var speakTimer = null, idleTimer = null;
  var IDLE_LINE = "Look right at something on the table. I\\u2019ll tell you why it matters to bees.";
  var curLine = null, curText = "";
  function wordList(t) { return t ? t.split(/\\s+/) : []; }
  // Draws the caption word by word. keep = leading words already on screen (they stay put, no animation).
  function render(c, text, keep) {
    c.textContent = "";
    var i = 0, k = 0;
    text.split(/(\\s+)/).forEach(function (w) {
      if (!w) return;
      if (/^\\s+$/.test(w)) { c.appendChild(document.createTextNode(" ")); return; }
      var sp = el("span", i < keep ? "seen" : "", w);
      if (i >= keep) sp.style.animationDelay = Math.min(k++ * 0.035, 1.4) + "s";
      i++; c.appendChild(sp);
    });
    c.className = text.length > 160 ? "xlong" : text.length > 95 ? "long" : "";
  }
  // The Hive hands the glasses a whole sequence at once (intro 1-3, fact + follow-up) and the ear plays it in order.
  // So captions queue here too: each stays up for about as long as it takes to say (the Hive's own estimate),
  // then the next one shows. Ask answers skip the queue (the wake word cuts the ear) and grow in place.
  var capQ = [], capTimer = null, lastEnq = "";
  function sayMs(text) { return Math.round((text.length / 14.4) * 1000 + 300) + 300; }
  function clearCaps() { capQ = []; clearTimeout(capTimer); capTimer = null; }
  function pumpCaps() {
    if (capTimer) return;
    var ev = capQ.shift();
    if (!ev) return;
    showLine(ev);
    capTimer = setTimeout(function () { capTimer = null; pumpCaps(); }, sayMs(String(ev.caption || "")));
  }
  function onSay(ev) {
    lastActivity = Date.now();
    var id = String(ev.line_id || ""), text = String(ev.caption || "").trim();
    // mirror audio (only when sound is on); an Ask answer's re-sent caption doesn't replay (scout/ask parity: any
    // other line said twice in a row, e.g. the teacher button pressed twice, plays twice like on the glasses)
    if (id !== lastEnq || id.indexOf("scout_ask_") !== 0) { lastEnq = id; enqueue(ev); }
    if (id === "scout_teacher") { showTeacher(text); return; }
    if (id.indexOf("scout_done") === 0) celebrate(); // the finale lines play under "Bee Scout badge earned!"
    if (!text) return; // a pure sound effect: nothing to show
    if (id.indexOf("scout_ask_") === 0) {
      clearCaps();
      if (pendingQ && Date.now() - pendingQ.at < 4000) showAsk(pendingQ.text, false);
      pendingQ = null;
      showLine(ev); return;
    }
    if (id === "scout_intro_1") clearCaps(); // a new journey starts: drop what's left of the old one
    capQ.push(ev);
    var total = 0; capQ.forEach(function (e) { total += sayMs(String(e.caption || "")); });
    while (capQ.length > 1 && total > 40000) total -= sayMs(String(capQ.shift().caption || "")); // never fall far behind the ear
    pumpCaps();
  }
  var askBase = null;
  function showLine(ev) {
    var id = String(ev.line_id || ""), text = String(ev.caption || "").trim();
    var b = $("bubble"), c = $("cap"), isAsk = id.indexOf("scout_ask_") === 0, update = false;
    if (isAsk && askBase && id === askBase && curLine === askBase) update = true; // the same answer, caption grown
    else if (isAsk && askBase && id.indexOf(askBase + "_") === 0 && curLine && curLine.indexOf(askBase) === 0) { update = true; text = curText + " " + text; } // its next sentence
    else askBase = isAsk ? id : null;
    if (update && text === curText) return;
    if (!isAsk) endAsk();
    b.classList.remove("idle", "thinking");
    var keep = 0;
    if (update) { var a = wordList(curText), n = wordList(text); while (keep < a.length - 1 && a[keep] === n[keep]) keep++; }
    render(c, text, keep);
    b.scrollTop = b.scrollHeight; // a long answer: keep its newest words in view
    curLine = id; curText = text;
    var ms = sayMs(update ? text.slice(Math.min(text.length, curText.length)) || text : text);
    document.body.classList.add("speaking"); $("bxi").src = ASSETS + "beeswax-front.webp";
    clearTimeout(speakTimer);
    speakTimer = setTimeout(stopSpeaking, ms);
    clearTimeout(idleTimer);
    idleTimer = setTimeout(goIdle, ms + 25000);
    if (isAsk) { asking = true; document.body.classList.add("asking"); clearTimeout(askTimer); askTimer = setTimeout(endAsk, ms + 20000); }
  }
  function stopSpeaking() { document.body.classList.remove("speaking"); $("bxi").src = ASSETS + "beeswax-side.webp"; }
  function goIdle() { endAsk(); var b = $("bubble"); b.classList.add("idle"); b.classList.remove("thinking"); var c = $("cap"); c.className = ""; c.textContent = IDLE_LINE; curLine = null; curText = ""; }

  // ------------------------------------------------------------------ the wearer asked (only "Hey Beeswax..." questions)
  // Ordinary chatter the mic hears is never shown. A heard question shows when the Ask lane marks it
  // (question: true) or, until that lands, when a scout_ask_* answer follows within 4 s.
  var askTimer = null, asking = false, pendingQ = null;
  var WAKE = /^\\s*(?:(?:hey|hi|hay|okay|ok)[\\s,]+)?bees?[\\s-]?wax\\b[\\s,.!?:;-]*/i;
  function cleanQ(t) { t = String(t || "").replace(WAKE, "").trim(); return t ? t.charAt(0).toUpperCase() + t.slice(1) : ""; }
  function quote(t) { return "\\u201c" + t + "\\u201d"; }
  function onHeard(ev) {
    lastActivity = Date.now();
    var t = cleanQ(ev.text);
    pendingQ = null;
    if (!t) return;
    if (ev.question === true) { clearCaps(); showAsk(t, true); return; }
    pendingQ = { text: t, at: Date.now() }; // shown only if Beeswax answers it
  }
  function showAsk(t, thinking) {
    $("askq").textContent = quote(t);
    var a = $("ask"); a.classList.remove("gone"); restart(a, "fresh");
    asking = true; document.body.classList.add("asking");
    clearTimeout(askTimer);
    if (!thinking) return;
    var b = $("bubble"); b.classList.remove("idle"); b.classList.add("thinking");
    var c = $("cap"); c.className = ""; c.textContent = "Hmm, let me think"; curLine = null; curText = "";
    askTimer = setTimeout(function () { if ($("bubble").classList.contains("thinking")) goIdle(); else endAsk(); }, 12000);
  }
  function endAsk() {
    clearTimeout(askTimer);
    if (!asking) return;
    asking = false; $("ask").classList.add("gone"); document.body.classList.remove("asking");
  }

  // ------------------------------------------------------------------ "Learn about something else": teachers can assign any topic
  var teacherTimer = null;
  var TEACHER_LINE = "Ask your teacher for another assignment!";
  function showTeacher(text) {
    lastActivity = Date.now();
    if (!started) return;
    $("tcap").textContent = text || TEACHER_LINE;
    var t = $("teacher");
    if (!t.classList.contains("on")) t.classList.add("on"); // already open (button, then its scout_teacher line): just keep it up
    clearTimeout(teacherTimer); teacherTimer = setTimeout(hideTeacher, 10000);
  }
  function hideTeacher() { clearTimeout(teacherTimer); $("teacher").classList.remove("on"); }
  $("more").addEventListener("click", function () {
    showTeacher("");
    fetch("/scout/teacher?" + TQ, { method: "POST" }).catch(function () {}); // Beeswax says it in the wearer's ear too
  });
  $("teacher").addEventListener("click", hideTeacher);

  // ------------------------------------------------------------------ restart: POST /scout/reset, a second tap confirms
  var confirmTimer = null;
  function unconfirm() { clearTimeout(confirmTimer); $("restart").classList.remove("confirm"); $("restartt").textContent = "Restart your journey"; }
  $("restart").addEventListener("click", function () {
    var btn = $("restart");
    if (!btn.classList.contains("confirm")) {
      btn.classList.add("confirm"); $("restartt").textContent = "Tap again to restart";
      clearTimeout(confirmTimer); confirmTimer = setTimeout(unconfirm, 3500); return;
    }
    unconfirm();
    fetch("/scout/stop?" + TQ, { method: "POST" }).catch(function () {}); // cut the ear, back to the silent waiting phase
    resetPage();
    showLanding();
  });
  function resetPage() {
    memToday = 0; try { localStorage.setItem(dayKey(), "0"); } catch (e) {}
    showToday(0);
    clearTimeout(revealTimer); clearTimeout(outTimer); $("reveal").classList.remove("on", "out");
    bx.flash = null;
    clearCaps(); pendingQ = null; hideTeacher(); stopSpeaking(); goIdle(); clearCase();
  }

  var firstState = true;
  function onState(st) {
    S = st;
    glassesState(st);
    if (firstState) { firstState = false; if (st.phase && st.phase !== "waiting" && !started) { started = true; tickMode(); } } // opened mid-journey
    var found = Array.isArray(st.found) ? st.found : [];
    // a new journey (reset, start, mode switch): the case empties
    if ((journey !== null && st.started_at && st.started_at !== journey) || (st.phase === "intro" && !found.length)) clearCase();
    if (st.started_at) journey = st.started_at;
    found.forEach(function (l) { if (earn(l)) fillSlot(l, false); });
  }

  // ------------------------------------------------------------------ audio mirror: Beeswax's lines on this screen
  // The only sound this page can make. Off on every load; every path checks the switch again right before playing.
  var mirror = false, queue = [], cur = null;
  var audio = new Audio(); audio.preload = "none"; audio.muted = true;
  function enqueue(ev) {
    if (!mirror) return;
    // scout/ask parity: an Ask Beeswax answer has no /scout/audio file, so go straight to its streamed mp3
    var ask = String(ev.line_id || "").indexOf("scout_ask_") === 0 && ev.audio_url;
    var urls = ask ? [ev.audio_url] : ["/scout/audio/" + encodeURIComponent(ev.line_id) + ".mp3?" + TQ];
    if (!ask && ev.audio_url && ev.audio_url.indexOf("/scout/audio/" + ev.line_id) < 0) urls.push(ev.audio_url);
    queue.push({ id: ev.line_id, urls: urls, at: Date.now(), n: 0 });
    if (!cur) playNext();
  }
  // scout/ask parity: Beeswax was cut off (ScoutEvent "stop": barge-in, reset): drop what is playing and queued, like the glasses ear
  function stopAudio() {
    queue = []; cur = null;
    if (audio.getAttribute("src")) { audio.pause(); audio.removeAttribute("src"); audio.load(); }
  }
  function playNext() {
    if (!mirror) { queue = []; cur = null; return; }
    // scout/ask parity: the glasses play every queued line in order (up to 120 s behind), so the Mac does too
    while (queue.length && Date.now() - queue[0].at > 120000) queue.shift();
    cur = queue.shift() || null;
    if (cur) load();
  }
  function load() {
    if (!mirror || !cur) return;
    audio.muted = false; audio.src = cur.urls[cur.n];
    var p = audio.play(); if (p && p.catch) p.catch(function () {});
  }
  audio.addEventListener("ended", playNext);
  audio.addEventListener("play", function () { if (!mirror) { audio.pause(); audio.muted = true; } });
  audio.addEventListener("error", function () {
    if (!mirror || !cur || !audio.getAttribute("src")) return;
    if (cur.n + 1 < cur.urls.length) { cur.n++; load(); return; }
    playNext();
  });
  function setMirror(on) {
    mirror = !!on;
    var b = $("sound");
    b.setAttribute("aria-pressed", mirror ? "true" : "false");
    b.title = (mirror ? "Mac sound on: Beeswax's voice plays on this computer. Click or press M to mute." : "Mac sound off: this computer is silent. Click or press M to hear Beeswax.");
    $("soundt").textContent = mirror ? "on" : "off";
    if (!mirror) { queue = []; cur = null; audio.pause(); audio.muted = true; audio.removeAttribute("src"); audio.load(); }
  }
  $("sound").addEventListener("click", function () { setMirror(!mirror); });
  setMirror(false); // always start silent

  // ------------------------------------------------------------------ glasses sound: POST /scout/glasses-mute, state.glasses_muted
  // Unavailable (disabled) until the Hive reports glasses_muted, or when the route 404s.
  var gMuted = null, gAvail = false, gBusy = false;
  function renderG() {
    var b = $("gsound");
    b.disabled = !gAvail || gBusy;
    b.setAttribute("aria-pressed", gAvail && gMuted === false ? "true" : "false");
    $("gsoundt").textContent = !gAvail ? "unavailable" : gMuted ? "off" : "on";
    b.title = !gAvail ? "This Hive can't mute the glasses yet" : gMuted ? "Glasses are silent. Click or press G to let Beeswax speak in the glasses." : "Beeswax speaks in the glasses. Click or press G to mute them.";
  }
  function glassesState(st) { if (st && typeof st.glasses_muted === "boolean") { gMuted = st.glasses_muted; gAvail = true; renderG(); } }
  function toggleGlasses() {
    if (!gAvail || gBusy) return;
    var want = !gMuted;
    gBusy = true; gMuted = want; renderG();
    fetch("/scout/glasses-mute?" + TQ, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ muted: want }) })
      .then(function (r) {
        if (r.status === 404) { gAvail = false; return null; }
        if (!r.ok) throw new Error(String(r.status));
        return r.json().catch(function () { return null; });
      })
      .then(function (j) { if (j) glassesState(j.state || j); })
      .catch(function () { gMuted = !want; })
      .then(function () { gBusy = false; renderG(); });
  }
  $("gsound").addEventListener("click", toggleGlasses);
  fetch("/scout/state?" + TQ, { cache: "no-store" }).then(function (r) { return r.ok ? r.json() : null; }).then(function (st) { glassesState(st); renderG(); }).catch(function () { renderG(); });

  // ------------------------------------------------------------------ Hive events (SSE, named events)
  function handle(ev) {
    if (!ev || !ev.type) return;
    switch (ev.type) {
      case "frame": if (ev.frame) onFrame(ev.frame); break;
      case "state": if (ev.state) onState(ev.state); break;
      case "found": onFound(ev); break;
      case "say": onSay(ev); break;
      case "stop": stopAudio(); clearCaps(); break; // scout/ask parity: the glasses ear was cut, so is this screen
      case "heard": onHeard(ev); break;
    }
  }
  // Chrome keeps at most 6 HTTP/1.1 connections per host, and every open SSE stream holds one for good.
  // With a few /scout and /scout/show tabs open, a new tab to the same Hive never loads. So a hidden tab
  // hands its stream back after 10 s and reconnects the moment it is shown again.
  var es = null, retryEs = null, hideTimer = null;
  function disconnect() { clearTimeout(retryEs); if (es) { try { es.close(); } catch (e) {} es = null; } }
  function connect() {
    disconnect();
    var me = es = new EventSource("/scout/events?" + TQ);
    me.onerror = function () { if (me === es && me.readyState === 2) retryEs = setTimeout(connect, 3000); }; // closed for good: retry ourselves
    var parse = function (e) { try { handle(JSON.parse(e.data)); } catch (err) {} };
    me.onmessage = parse;
    ["frame", "found", "say", "heard", "state", "stop"].forEach(function (t) { me.addEventListener(t, parse); }); // stop: scout/ask parity
  }
  document.addEventListener("visibilitychange", function () {
    clearTimeout(hideTimer);
    if (document.hidden) hideTimer = setTimeout(disconnect, 10000);
    else if (!es) connect();
  });
  window.__esState = function () { return es ? es.readyState : "closed"; }; // for the dev harness checks
  if (!document.hidden) connect(); // opened in a background tab: connects when first shown

  // ------------------------------------------------------------------ video: WHEP straight from MediaMTX (same as /scout)
  var video = $("video"), pc = null, retryTimer = null, lastT = -1, lastMove = 0, playing = false, netFails = 0;
  var iframeMode = Q.get("player") === "iframe", fileVideo = Q.get("video"), whepAt = 0;
  video.muted = true; video.volume = 0;
  ["play", "volumechange", "loadedmetadata"].forEach(function (t) { video.addEventListener(t, function () { if (!video.muted) video.muted = true; }); });
  var playerUrl = WEBRTC + "/" + streamPath + "/";
  function setPlaying(on) {
    playing = on;
    if (on) lastPlayingAt = Date.now();
    document.body.classList.toggle("playing", on);
    $("naptext").textContent = "Camera connecting\\u2026";
    tickMode();
  }
  function stopPc() { if (pc) { pc.ontrack = pc.onconnectionstatechange = null; try { pc.close(); } catch (e) {} pc = null; } }
  function retry(ms) { stopPc(); if (retryTimer) clearTimeout(retryTimer); retryTimer = setTimeout(startWhep, ms); }
  function waitIce(p) {
    return new Promise(function (res) {
      if (p.iceGatheringState === "complete") return res();
      var t = setTimeout(res, 1500);
      p.addEventListener("icegatheringstatechange", function () { if (p.iceGatheringState === "complete") { clearTimeout(t); res(); } });
    });
  }
  function startWhep() {
    retryTimer = null; stopPc();
    var p = pc = new RTCPeerConnection();
    whepAt = Date.now();
    var ms = new MediaStream();
    p.addTransceiver("video", { direction: "recvonly" });
    p.ontrack = function (e) {
      if (e.track.kind !== "video") { e.track.stop(); return; }
      ms.addTrack(e.track); video.srcObject = ms; video.muted = true;
      var pl = video.play(); if (pl && pl.catch) pl.catch(function () {}); // don't rely on the autoplay attribute alone
    };
    p.onconnectionstatechange = function () {
      if (p !== pc) return;
      var st = p.connectionState;
      if (st === "failed" || st === "closed" || st === "disconnected") { setPlaying(false); retry(2000); }
    };
    p.createOffer().then(function (o) { return p.setLocalDescription(o); }).then(function () { return waitIce(p); }).then(function () {
      return fetch(WEBRTC + "/" + streamPath + "/whep", { method: "POST", headers: { "Content-Type": "application/sdp" }, body: p.localDescription.sdp });
    }).then(function (r) {
      netFails = 0;
      if (r.status === 404 || r.status === 400) { setPlaying(false); retry(3000); return null; }
      if (!r.ok) throw new Error("whep " + r.status);
      return r.text();
    }).then(function (sdp) {
      if (!sdp || p !== pc) return;
      return p.setRemoteDescription({ type: "answer", sdp: sdp });
    }).catch(function () {
      if (p !== pc) return;
      netFails++;
      setPlaying(false);
      retry(Math.min(8000, 2000 * netFails));
    });
  }
  setInterval(function () { // frames moving = playing; stuck for 5 s = the publisher went away
    if (iframeMode) return;
    var t = video.currentTime, now = Date.now();
    if (t !== lastT && video.readyState >= 2) { lastT = t; lastMove = now; lastPlayingAt = now; if (!playing) setPlaying(true); }
    else if (playing && now - lastMove > 5000) { setPlaying(false); if (!fileVideo) retry(500); }
    // WHEP answered but no frame ever arrived (stream not ready yet, a stuck session): try again
    else if (!playing && pc && !retryTimer && !fileVideo && now - whepAt > 8000) retry(0);
  }, 1000);
  if (iframeMode) {
    video.remove();
    var fr = el("iframe"); fr.src = playerUrl + "?controls=false&muted=true&autoplay=true"; // no autoplay delegation: the player can only autoplay muted
    $("frame").insertBefore(fr, $("frame").firstChild);
    setPlaying(true);
  } else if (fileVideo) {
    video.loop = true; video.src = fileVideo; video.play().catch(function () {});
  } else {
    startWhep();
  }

  addEventListener("keydown", function (e) {
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    if (!started && (e.key === "Enter" || e.key === " ")) { if (!(e.target && e.target.tagName === "BUTTON")) { e.preventDefault(); startAdventure(); } return; }
    if (e.key === "m" || e.key === "M") setMirror(!mirror);
    else if (e.key === "b" || e.key === "B") setBoxes(!showBoxes, true);
    else if (e.key === "g" || e.key === "G") toggleGlasses();
    else if (e.key === "Escape") hideTeacher();
    else if (e.key === "f" || e.key === "F") { if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen().catch(function () {}); }
  });
  requestAnimationFrame(drawBoxes);
  tickMode();
})();
</script>
</body>
</html>`;

export function scoutShowHtml(): string {
  return SHOW.replace("__CONFIG__", () => CONFIG);
}

// ------------------------------------------------------------------ GET /scout/assets/<file>: the page's artwork + fonts
const ASSET_DIR = fileURLToPath(new URL("../scout-assets/", import.meta.url));
const ASSET_FILE = /^[a-z0-9][a-z0-9_-]{0,63}\.(webp|png|jpg|svg|woff2)$/;
const ASSET_TYPES: Record<string, string> = { webp: "image/webp", png: "image/png", jpg: "image/jpeg", svg: "image/svg+xml", woff2: "font/woff2" };

/** Static file from hive/scout-assets/ (flat folder, safe names only). Public: just artwork. */
export function scoutAssetResponse(file: string): Response {
  const m = ASSET_FILE.exec(file);
  if (!m) return new Response("bad file name", { status: 400 });
  const path = join(ASSET_DIR, file);
  if (!existsSync(path)) return new Response("not found", { status: 404 });
  return new Response(Bun.file(path), { headers: { "content-type": ASSET_TYPES[m[1]!]!, "cache-control": "public, max-age=3600" } });
}
