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
//   sound=1 start with the audio mirror armed (a browser still needs one click or key press first)
// Keys: M = sound on/off, F = full screen.
import { existsSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { SCOUT_BACKGROUND, SCOUT_LABELS, SCOUT_PORTS, SCOUT_STREAM_PATH, type ScoutLabel } from "./scout-protocol";

/** What the crowd reads: the names printed on the station cards (scout-vision/detect/cards.json). */
const SHOW_NAMES: Record<ScoutLabel, string> = {
  beehive: "Beehive",
  native_flower: "Sunflower",
  honey_jar: "Store honey",
  pebble_dish: "Bee water dish",
  bug_spray: "Bug spray",
  bee_hotel: "Bee hotel",
};
/** One short line under the name in the found reveal. */
const SHOW_TAGS: Record<ScoutLabel, string> = {
  beehive: "Home sweet home",
  native_flower: "Lunch for bees",
  honey_jar: "Yummy for you, risky for bees",
  pebble_dish: "A drinking fountain for bees",
  bug_spray: "Bad news for bees",
  bee_hotel: "A home for wild bees",
};
const CARD_FILE = (l: ScoutLabel) => `card-${l.replace(/_/g, "-")}.webp`;

const CONFIG = JSON.stringify({
  LABELS: SCOUT_LABELS,
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
  #sound { display: flex; align-items: center; gap: .5rem; padding: .55rem 1.2rem .55rem .9rem; border-radius: 99rem; border: 0; cursor: pointer;
    background: transparent; box-shadow: inset 0 0 0 .12rem rgba(85, 41, 15, .25); font-size: 1.15rem; font-weight: 700; color: var(--ink-soft); }
  #sound svg { width: 1.6rem; height: 1.6rem; }
  #sound .on { display: none; }
  #sound[aria-pressed="true"] { background: var(--brown); color: var(--paper); box-shadow: none; }
  #sound[aria-pressed="true"] .on { display: inline; } #sound[aria-pressed="true"] .off { display: none; }
  #sound:focus-visible { outline: .2rem solid var(--honey); outline-offset: .2rem; }

  /* ---------------------------------------------------------------- live layout */
  main { display: grid; grid-template-columns: minmax(0, 1fr) 30rem; gap: 2.2rem; padding: .8rem 2.2rem 2rem; min-height: 0; align-items: center; }
  .cam { min-width: 0; min-height: 0; height: 100%; display: flex; flex-direction: column; justify-content: center; gap: 1rem; }
  /* the camera is framed like one of the Beeswax stickers: white band, brown outline */
  #frame { position: relative; width: 100%; max-height: calc(100% - 4.6rem); aspect-ratio: 16 / 9; margin: 0 auto; border-radius: 1.6rem; overflow: hidden;
    background: #2b2018; border: .55rem solid var(--paper); box-shadow: 0 0 0 .16rem var(--brown), 0 1.4rem 2.8rem rgba(85, 41, 15, .22); }
  #frame video, #frame iframe { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; border: 0; background: #2b2018; }
  #boxes { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; z-index: 2; }
  #napping { position: absolute; inset: 0; z-index: 3; display: grid; place-content: center; justify-items: center; gap: .6rem; text-align: center;
    background: radial-gradient(circle at 50% 42%, #f2eee6, var(--greige) 80%); color: var(--ink-soft); font-size: 1.6rem; font-weight: 700; transition: opacity .6s; }
  #napping img { width: 13rem; animation: snooze 3.2s ease-in-out infinite; }
  @keyframes snooze { 50% { transform: translateY(.5rem) rotate(-2deg); } }
  .playing #napping { opacity: 0; pointer-events: none; }
  #pin { position: absolute; z-index: 4; top: 1rem; right: 1rem; display: flex; align-items: center; gap: .7rem; padding: .35rem 1.3rem .35rem .35rem;
    border-radius: 99rem; background: var(--paper); box-shadow: 0 0 0 .14rem var(--brown), 0 .5rem 1.4rem rgba(0, 0, 0, .3);
    font-size: 1.5rem; font-weight: 800; transform: scale(0); transform-origin: right center; transition: transform .45s cubic-bezier(.2, 1.5, .4, 1); }
  #pin.on { transform: scale(1); }
  #pin img { width: 3.4rem; height: 3.4rem; border-radius: 50%; object-fit: cover; }
  .under { display: flex; align-items: flex-start; gap: 1.4rem; min-height: 3.6rem; }
  .seeing { display: flex; align-items: center; gap: .6rem; padding-top: .7rem; font-size: 1.45rem; font-weight: 700; color: var(--ink-soft); white-space: nowrap; }
  .seeing i { width: .9rem; height: .9rem; border-radius: 50%; background: rgba(85, 41, 15, .3); }
  .playing .seeing i { background: #e0452b; box-shadow: 0 0 0 .3rem rgba(224, 69, 43, .2); animation: blink 1.6s ease-in-out infinite; }
  @keyframes blink { 50% { opacity: .35; } }
  /* the wearer's words: a little bubble rising out of their camera view */
  #you { position: relative; margin-left: auto; max-width: 62%; padding: .6rem 1.3rem .7rem; border-radius: 1.2rem; background: var(--sage-soft);
    box-shadow: 0 0 0 .12rem rgba(85, 41, 15, .16); font-size: 1.7rem; font-weight: 700; line-height: 1.2; transition: opacity .5s, transform .5s; }
  #you::before { content: ""; position: absolute; top: -.75rem; right: 2.4rem; border: .8rem solid transparent; border-top: 0; border-bottom-color: var(--sage-soft); }
  #you small { display: block; font-size: 1.1rem; font-weight: 700; color: #5d7553; }
  #you.gone { opacity: 0; transform: translateY(.6rem); }
  #you.fresh { animation: pop .5s cubic-bezier(.2, 1.6, .4, 1); }
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
  #cap span { display: inline-block; white-space: pre; opacity: 0; animation: word .35s ease-out forwards; }
  @keyframes word { from { opacity: 0; transform: translateY(.4rem); } to { opacity: 1; transform: none; } }
  #bubble.idle #cap { color: var(--ink-soft); font-size: 2.1rem; }

  /* ---------------------------------------------------------------- attract (nobody wearing the glasses) */
  #attract { display: none; }
  body.attract main { display: none; }
  body.attract .brand { opacity: 0; }
  body.attract #attract { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); align-items: center; gap: 2rem; padding: 0 5rem 3rem 3rem; min-height: 0; }
  .hero { width: min(100%, 52rem); aspect-ratio: 1; object-fit: contain; justify-self: center; animation: float 6s ease-in-out infinite;
    -webkit-mask-image: radial-gradient(circle at 52% 46%, #000 54%, transparent 68%); mask-image: radial-gradient(circle at 52% 46%, #000 54%, transparent 68%); }
  @keyframes float { 50% { transform: translateY(-1.2rem) rotate(1deg); } }
  .pitch h2 { margin: 0; font: 400 3.4rem/1 var(--display); color: var(--brown); text-shadow: .14rem .16rem 0 var(--butter); }
  .pitch h2.big { font-size: 6.2rem; line-height: .98; margin-bottom: 1.6rem; }
  .pitch p { margin: 0 0 2.4rem; max-width: 34rem; font-size: 2rem; line-height: 1.3; font-weight: 600; color: var(--ink-soft); }
  .hello { display: flex; align-items: center; gap: 1rem; }
  .hello img { width: 8.5rem; height: 8.5rem; flex: none; }
  .hello div { position: relative; padding: 1rem 1.4rem; border-radius: 1.4rem; background: var(--paper); box-shadow: 0 0 0 .14rem var(--brown);
    font-size: 1.7rem; font-weight: 700; line-height: 1.2; }
  .hello div::before { content: ""; position: absolute; left: -.95rem; top: 50%; margin-top: -.8rem; border: .8rem solid transparent; border-left: 0; border-right-color: var(--brown); border-right-width: .95rem; }
  .hello div::after { content: ""; position: absolute; left: -.72rem; top: 50%; margin-top: -.66rem; border: .66rem solid transparent; border-left: 0; border-right-color: var(--paper); border-right-width: .8rem; }

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
  <div class="tools">
    <div id="today" title="Bee facts found on this screen today">${DAISY_ICON}<span><b id="todayn">0</b> <span id="todayt">bee facts found today</span></span></div>
    <button id="sound" type="button" aria-pressed="false" title="Play Beeswax's voice on this screen (M)">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path class="on" d="M15.5 9a4 4 0 0 1 0 6M18.5 6.5a7.5 7.5 0 0 1 0 11"/><path class="off" d="M16 9.5l5 5M21 9.5l-5 5"/>
      </svg><span id="soundt">Sound off</span></button>
  </div>
</header>
<main>
  <section class="cam">
    <div id="frame">
      <video id="video" autoplay muted playsinline></video>
      <canvas id="boxes"></canvas>
      <div id="napping"><img src="/scout/assets/beeswax-sleep.webp" alt=""><div id="naptext">The glasses camera is waking up&hellip;</div></div>
      <div id="pin"><img id="pinimg" alt=""><span id="pinname"></span></div>
    </div>
    <div class="under">
      <div class="seeing"><i></i>What the glasses see</div>
      <div id="you" class="gone"><small>You said</small><span id="youq"></span></div>
    </div>
  </section>
  <aside class="bee">
    <div class="beeswax"><img id="bxi" src="/scout/assets/beeswax-side.webp" alt=""><span>Beeswax</span></div>
    <div id="bubble" class="idle"><p id="cap">Look right at something on the table. I&rsquo;ll tell you why it matters to bees.</p></div>
  </aside>
</main>
<section id="attract">
  <img class="hero" src="/scout/assets/logo.webp" alt="Beeswax the bee">
  <div class="pitch">
    <h2>Live Bee Scout</h2>
    <h2 class="big">Put on the glasses and look around.</h2>
    <p>Look at something on the table. Beeswax spots it and tells you why it matters to bees.</p>
    <div class="hello"><img src="/scout/assets/beeswax-wave.webp" alt=""><div>Hi, I&rsquo;m Beeswax. I live in the glasses!</div></div>
  </div>
</section>
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
  var lastActivity = 0, lastPlayingAt = 0;

  // ------------------------------------------------------------------ attract vs live
  function tickMode() {
    var now = Date.now();
    var live = playing || now - lastPlayingAt < 4000 || now - lastActivity < 20000;
    document.body.classList.toggle("attract", !live);
  }
  setInterval(tickMode, 1000);

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
    if (alpha <= 0 || !bx.list.length || !playing) { disp = {}; requestAnimationFrame(drawBoxes); return; }
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
  var revealTimer = null, outTimer = null, pinTimer = null, pinLabel = null;
  var REDUCED = matchMedia("(prefers-reduced-motion: reduce)").matches;
  function onFound(ev) {
    lastActivity = Date.now();
    bx.flash = { label: ev.label, until: Date.now() + 8000 };
    bumpToday();
    var rv = $("reveal");
    $("rvimg").src = ASSETS + (CFG.CARDS[ev.label] || "logo.webp");
    $("rvname").textContent = name(ev.label);
    $("rvtag").textContent = CFG.TAGS[ev.label] || "";
    rv.querySelectorAll(".petal").forEach(function (p) { p.remove(); });
    if (!REDUCED) for (var i = 0; i < 34; i++) {
      var p = el("i", "petal" + (i % 5 === 4 ? " dot" : ""));
      p.style.setProperty("--a", (i * (360 / 34) + Math.random() * 8) + "deg");
      p.style.setProperty("--d", "-" + (24 + Math.random() * 26) + "rem");
      p.style.setProperty("--spin", (Math.random() * 120 - 60) + "deg");
      p.style.animationDelay = (0.18 + Math.random() * 0.2) + "s";
      rv.appendChild(p);
    }
    rv.classList.remove("out", "on"); void rv.offsetWidth; rv.classList.add("on");
    clearTimeout(revealTimer); clearTimeout(outTimer);
    revealTimer = setTimeout(function () { rv.classList.add("out"); outTimer = setTimeout(function () { rv.classList.remove("on", "out"); }, 500); }, 4200);
    setPin(ev.label);
    hideYou();
  }
  function setPin(label) {
    pinLabel = label;
    $("pinimg").src = ASSETS + (CFG.CARDS[label] || "logo.webp");
    $("pinname").textContent = name(label);
    $("pin").classList.remove("on");
    clearTimeout(pinTimer);
    pinTimer = setTimeout(function () { $("pin").classList.add("on"); pinTimer = setTimeout(clearPin, 30000); }, 4400);
  }
  function clearPin() { pinLabel = null; $("pin").classList.remove("on"); }

  // ------------------------------------------------------------------ Beeswax says
  var speakTimer = null, idleTimer = null;
  var IDLE_LINE = "Look right at something on the table. I\\u2019ll tell you why it matters to bees.";
  function onSay(ev) {
    lastActivity = Date.now();
    var text = String(ev.caption || "").trim();
    if (!text) return; // a pure sound effect
    var b = $("bubble"), c = $("cap");
    b.classList.remove("idle");
    c.className = text.length > 95 ? "long" : "";
    c.textContent = "";
    text.split(/(\\s+)/).forEach(function (w, i) {
      if (!w) return;
      if (/^\\s+$/.test(w)) { c.appendChild(document.createTextNode(" ")); return; }
      var s = el("span", "", w); s.style.animationDelay = Math.min(i * 0.035, 1.4) + "s"; c.appendChild(s);
    });
    // Beeswax chatters for about as long as the line takes to say (~2.6 words a second)
    var words = text.split(/\\s+/).length, ms = Math.max(1500, words * 380);
    document.body.classList.add("speaking"); $("bxi").src = ASSETS + "beeswax-front.webp";
    clearTimeout(speakTimer);
    speakTimer = setTimeout(stopSpeaking, ms);
    clearTimeout(idleTimer);
    idleTimer = setTimeout(goIdle, ms + 25000);
    if (mirror) enqueue(ev);
  }
  function stopSpeaking() { document.body.classList.remove("speaking"); $("bxi").src = ASSETS + "beeswax-side.webp"; }
  function goIdle() { $("bubble").classList.add("idle"); var c = $("cap"); c.className = ""; c.textContent = IDLE_LINE; }

  // ------------------------------------------------------------------ the wearer said
  var youTimer = null;
  function onHeard(ev) {
    lastActivity = Date.now();
    var t = String(ev.text || "").trim();
    if (!t) return;
    $("youq").textContent = "\\u201c" + t + "\\u201d";
    var y = $("you"); y.classList.remove("gone"); restart(y, "fresh");
    clearTimeout(youTimer); youTimer = setTimeout(hideYou, 15000);
  }
  function hideYou() { $("you").classList.add("gone"); }

  function onState(st) {
    S = st;
    if (pinLabel && S.phase === "looking" && S.offer !== pinLabel) { clearTimeout(pinTimer); pinTimer = setTimeout(clearPin, 6000); }
  }

  // ------------------------------------------------------------------ audio mirror: Beeswax's lines on this screen
  var mirror = false, queue = [], cur = null;
  var audio = new Audio(); audio.preload = "auto";
  function enqueue(ev) {
    var urls = ["/scout/audio/" + encodeURIComponent(ev.line_id) + ".mp3?" + TQ];
    if (ev.audio_url && ev.audio_url.indexOf("/scout/audio/" + ev.line_id) < 0) urls.push(ev.audio_url);
    queue.push({ id: ev.line_id, urls: urls, at: Date.now(), n: 0 });
    if (!cur) playNext();
  }
  function playNext() {
    while (queue.length && Date.now() - queue[0].at > 20000) queue.shift(); // never fall far behind the wearer
    cur = queue.shift() || null;
    if (cur) load();
  }
  function load() { audio.src = cur.urls[cur.n]; var p = audio.play(); if (p && p.catch) p.catch(function () {}); }
  audio.addEventListener("ended", playNext);
  audio.addEventListener("error", function () {
    if (!cur || !audio.getAttribute("src")) return;
    if (cur.n + 1 < cur.urls.length) { cur.n++; load(); return; }
    playNext();
  });
  function setMirror(on) {
    mirror = on;
    $("sound").setAttribute("aria-pressed", on ? "true" : "false");
    $("soundt").textContent = on ? "Sound on" : "Sound off";
    if (!on) { queue = []; cur = null; audio.pause(); audio.removeAttribute("src"); }
  }
  $("sound").addEventListener("click", function () { setMirror(!mirror); });
  if (Q.get("sound") === "1") setMirror(true);

  // ------------------------------------------------------------------ Hive events (SSE, named events)
  function handle(ev) {
    if (!ev || !ev.type) return;
    switch (ev.type) {
      case "frame": if (ev.frame) onFrame(ev.frame); break;
      case "state": if (ev.state) onState(ev.state); break;
      case "found": onFound(ev); break;
      case "say": onSay(ev); break;
      case "heard": onHeard(ev); break;
    }
  }
  var es = null;
  function connect() {
    if (es) { try { es.close(); } catch (e) {} }
    es = new EventSource("/scout/events?" + TQ);
    es.onerror = function () { if (es.readyState === 2) setTimeout(connect, 3000); }; // closed for good: retry ourselves
    var parse = function (e) { try { handle(JSON.parse(e.data)); } catch (err) {} };
    es.onmessage = parse;
    ["frame", "found", "say", "heard", "state"].forEach(function (t) { es.addEventListener(t, parse); });
  }
  connect();

  // ------------------------------------------------------------------ video: WHEP straight from MediaMTX (same as /scout)
  var video = $("video"), pc = null, retryTimer = null, lastT = -1, lastMove = 0, playing = false, netFails = 0;
  var iframeMode = Q.get("player") === "iframe", fileVideo = Q.get("video");
  var playerUrl = WEBRTC + "/" + streamPath + "/";
  function setPlaying(on) {
    playing = on;
    if (on) lastPlayingAt = Date.now();
    document.body.classList.toggle("playing", on);
    $("naptext").textContent = "The glasses camera is waking up\\u2026";
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
    var ms = new MediaStream();
    p.addTransceiver("video", { direction: "recvonly" });
    p.addTransceiver("audio", { direction: "recvonly" });
    p.ontrack = function (e) { ms.addTrack(e.track); video.srcObject = ms; };
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
  }, 1000);
  if (iframeMode) {
    video.remove();
    var fr = el("iframe"); fr.src = playerUrl + "?controls=false&muted=true&autoplay=true"; fr.allow = "autoplay";
    $("frame").insertBefore(fr, $("frame").firstChild);
    setPlaying(true);
  } else if (fileVideo) {
    video.loop = true; video.src = fileVideo; video.play().catch(function () {});
  } else {
    startWhep();
  }

  addEventListener("keydown", function (e) {
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    if (e.key === "m" || e.key === "M") setMirror(!mirror);
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
