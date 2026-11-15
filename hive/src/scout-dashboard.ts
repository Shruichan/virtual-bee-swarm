// Live Bee Scout pages, served by the scout game's routes (game lane):
//   GET /scout?token=...         the booth dashboard: what the glasses stream, what the AI sees, what Beeswax says
//   GET /scout/slides?token=...  the screen test: one held-out photo at a time, for the wearer to look at
// Both are self-contained (no build step, no external deps). Page query params:
//   token   the Hive token (passed on to /scout/events, /scout/state, /scout/slide, /scout/audio)
//   stream  the MediaMTX path to play (default "scout"; "scouttest" for ffmpeg test patterns)
//   player  "iframe" = MediaMTX's own player page instead of the built-in WHEP player
//   webrtc  MediaMTX WebRTC base URL (default http://<page host>:8889)
//   vision  vision worker base URL (default http://<page host>:8242)
//   (slides only) auto=1 starts in auto mode (default manual: Space = next), labels=1 starts with the label tag on
import {
  SCOUT_BACKGROUND,
  SCOUT_LABEL_NAMES,
  SCOUT_LABELS,
  SCOUT_PORTS,
  SCOUT_STREAM_PATH,
  SCOUT_TUNING,
} from "./scout-protocol";

const CONFIG = JSON.stringify({
  LABELS: SCOUT_LABELS,
  NAMES: { ...SCOUT_LABEL_NAMES, [SCOUT_BACKGROUND]: "Background" },
  BACKGROUND: SCOUT_BACKGROUND,
  PORTS: SCOUT_PORTS,
  STREAM: SCOUT_STREAM_PATH,
  MIN_SCORE: SCOUT_TUNING.minScore,
});

// NOTE: the inline scripts avoid backticks and "${" so they can live inside these template strings.
const DASHBOARD = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bee Scout Live</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Cpath d='M12 1l10 5.5v11L12 23 2 17.5v-11z' fill='%23f6b72e'/%3E%3C/svg%3E">
<style>
  :root {
    --bg: #110d08; --panel: rgba(36, 27, 16, .82); --panel2: #2a2014; --line: #3d2f1c;
    --text: #fbf3e2; --dim: #b2a184; --mute: #75684f;
    --honey: #f6b72e; --amber: #ffd36b; --deep: #c9831a; --meadow: #8fd16f;
    --sky: #6cb8ff; --bad: #ef6a57; --plum: #c39bff;
  }
  * { box-sizing: border-box; }
  html { font-size: min(1.85vh, 1.04vw); }
  html, body { margin: 0; height: 100%; overflow: hidden; }
  body {
    background:
      radial-gradient(ellipse 70% 60% at 18% 0%, rgba(246, 183, 46, .16), transparent 70%),
      radial-gradient(ellipse 60% 50% at 100% 100%, rgba(143, 209, 111, .10), transparent 70%),
      url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='56' height='97' viewBox='0 0 56 97'%3E%3Cpath d='M28 1 L55 16.5 L55 47.5 L28 63 L1 47.5 L1 16.5 Z M28 63 L28 97' fill='none' stroke='%23f6b72e' stroke-opacity='.05' stroke-width='1.2'/%3E%3C/svg%3E"),
      var(--bg);
    color: var(--text);
    font: 1rem/1.3 -apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", Roboto, "Helvetica Neue", sans-serif;
    display: grid; grid-template-rows: auto minmax(0, 1fr) auto;
  }
  button { font-family: inherit; }
  .num { font-variant-numeric: tabular-nums; }
  .label { font-size: .82rem; font-weight: 800; letter-spacing: .16em; text-transform: uppercase; color: var(--honey); }
  .card { background: var(--panel); border: 1px solid var(--line); border-radius: 1rem; padding: .9rem 1.1rem; min-width: 0; }
  .cardhead { display: flex; align-items: baseline; justify-content: space-between; gap: 1rem; margin-bottom: .5rem; }
  .cardhead .meta { color: var(--dim); font-size: .9rem; font-weight: 600; white-space: nowrap; }

  /* header */
  header { display: flex; align-items: center; gap: 1.4rem; padding: .9rem 1.6rem .8rem; }
  .brand { display: flex; align-items: center; gap: .8rem; min-width: 0; }
  .logo { width: 2.6rem; height: 2.9rem; clip-path: polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%);
    background: linear-gradient(160deg, var(--amber), var(--deep)); display: grid; place-items: center; flex: none; }
  .logo i { width: 1.1rem; height: 1.25rem; clip-path: polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%); background: #2a1a05; }
  .brand h1 { margin: 0; font-size: 1.9rem; font-weight: 900; letter-spacing: .02em; line-height: 1; white-space: nowrap; }
  .brand h1 b { color: var(--honey); }
  .brand small { display: block; color: var(--dim); font-size: .9rem; font-weight: 600; margin-top: .25rem; white-space: nowrap; }
  #phase { margin-left: auto; display: flex; align-items: center; gap: .7rem; padding: .5rem 1.1rem; border-radius: 999px;
    background: var(--panel2); border: 1px solid var(--line); white-space: nowrap; }
  #phase b { font-size: 1.15rem; font-weight: 900; letter-spacing: .12em; text-transform: uppercase; color: var(--honey); }
  #phase span { color: var(--dim); font-weight: 600; }
  #phase.offering b, #phase.telling b { color: var(--amber); }
  #phase.done b { color: var(--meadow); }
  #clock { font-size: 1.7rem; font-weight: 800; color: var(--text); min-width: 5.2rem; text-align: right; }
  #clock small { display: block; font-size: .7rem; letter-spacing: .16em; color: var(--dim); font-weight: 800; text-align: right; }

  /* main grid */
  main { display: grid; grid-template-columns: minmax(0, 1.5fr) minmax(0, 1fr); gap: 1.1rem; padding: 0 1.6rem; min-height: 0; }
  .left, .right { display: flex; flex-direction: column; gap: .9rem; min-height: 0; min-width: 0; }

  /* video */
  #vidwrap { position: relative; flex: 1 1 auto; min-height: 0; border-radius: 1.1rem; overflow: hidden; background: #050403;
    border: 1px solid var(--line); box-shadow: 0 1.2rem 3rem rgba(0,0,0,.45); }
  #vidwrap video, #vidwrap iframe { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; border: 0; background: #050403; }
  #vidwrap.playing { border-color: rgba(246, 183, 46, .45); }
  .ov { position: absolute; z-index: 3; }
  #livetag { top: 1rem; left: 1rem; display: flex; align-items: center; gap: .55rem; padding: .45rem .9rem; border-radius: 999px;
    background: rgba(0,0,0,.62); font-weight: 800; letter-spacing: .1em; font-size: .9rem; text-transform: uppercase; }
  #livetag i { width: .8rem; height: .8rem; border-radius: 50%; background: var(--mute); }
  #vidwrap.playing #livetag i { background: #ff3b30; box-shadow: 0 0 .8rem #ff3b30; animation: pulse 1.4s ease-in-out infinite; }
  @keyframes pulse { 50% { opacity: .35; } }
  #slidetag { top: 1rem; right: 1rem; padding: .45rem .9rem; border-radius: 999px; background: rgba(195, 155, 255, .9); color: #1b0f2e;
    font-weight: 800; font-size: .9rem; display: none; }
  #slidetag.on { display: block; }
  #micbtn { top: 1rem; right: 1rem; display: none; background: rgba(0,0,0,.6); color: #fff; }
  #micbtn.on { display: block; }
  #slidetag.on ~ #micbtn { top: 3.6rem; }
  #guess { left: 1rem; bottom: 1rem; display: flex; align-items: center; gap: 1rem; padding: .7rem 1.3rem .7rem .8rem; border-radius: 1rem;
    background: rgba(10, 7, 3, .8); border: 1px solid rgba(255,255,255,.08); backdrop-filter: blur(6px); max-width: calc(100% - 2rem); }
  #guess .ring { --p: 0; width: 3.8rem; height: 3.8rem; border-radius: 50%; flex: none; display: grid; place-items: center;
    background: conic-gradient(var(--honey) calc(var(--p) * 1%), rgba(255,255,255,.12) 0); }
  #guess .ring b { width: 3rem; height: 3rem; border-radius: 50%; background: #120d06; display: grid; place-items: center; font-size: 1.02rem; font-weight: 900; }
  #guess .what small { display: block; font-size: .78rem; letter-spacing: .16em; font-weight: 800; text-transform: uppercase; color: var(--dim); }
  #guess .what span { display: block; font-size: 1.9rem; font-weight: 900; line-height: 1.1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  #guess.bg .what span { color: var(--dim); }
  #guess.bg .ring { background: conic-gradient(var(--mute) calc(var(--p) * 1%), rgba(255,255,255,.1) 0); }
  #guess.strong .what span { color: var(--amber); }
  #guess.none { display: none; }
  #nostream { position: absolute; inset: 0; z-index: 2; display: grid; place-items: center; text-align: center; padding: 2rem;
    background: radial-gradient(circle at 50% 45%, #241a0e, #070503 75%); }
  #nostream.off { display: none; }
  #nostream .hexbig { width: 6rem; height: 6.8rem; margin: 0 auto 1.2rem; clip-path: polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%);
    background: linear-gradient(160deg, #3a2a14, #1d150b); display: grid; place-items: center; animation: breathe 2.4s ease-in-out infinite; }
  #nostream .hexbig i { width: 2.2rem; height: 2.2rem; border-radius: 50%; border: .35rem solid var(--honey); opacity: .8; }
  @keyframes breathe { 50% { transform: scale(1.06); filter: brightness(1.3); } }
  #nostream h2 { margin: 0 0 .5rem; font-size: 2.1rem; font-weight: 900; }
  #nostream p { margin: .2rem 0; color: var(--dim); font-size: 1.05rem; }
  #nostream code { color: var(--amber); font: 600 1rem ui-monospace, Menlo, monospace; }
  #nostream a { color: var(--honey); }

  /* detection boxes + latency readout */
  #boxes { position: absolute; inset: 0; width: 100%; height: 100%; z-index: 2; pointer-events: none; }
  #latro { right: 1rem; bottom: 1rem; padding: .55rem .9rem; border-radius: .8rem; background: rgba(10, 7, 3, .8); border: 1px solid rgba(255,255,255,.08);
    backdrop-filter: blur(6px); min-width: 12.5rem; }
  #latro small { display: block; font-size: .68rem; font-weight: 800; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: .15rem; }
  #latro div { display: flex; justify-content: space-between; gap: 1rem; font-size: .9rem; font-weight: 600; color: var(--dim); }
  #latro b { color: var(--meadow); font-weight: 900; font-size: 1.05rem; }
  #latro b.slow { color: var(--amber); } #latro b.none { color: var(--mute); }
  #bigclock { position: fixed; z-index: 30; top: 1rem; right: 1.6rem; display: none; padding: 1rem 1.6rem; border-radius: 1rem; background: #000; border: 2px solid var(--honey);
    text-align: center; box-shadow: 0 1rem 3rem rgba(0,0,0,.7); }
  #bigclock.on { display: block; }
  #bigclock b { display: block; font: 800 4.6rem/1 ui-monospace, Menlo, monospace; color: #fff; letter-spacing: .02em; }
  #bigclock span { display: block; margin-top: .5rem; color: var(--dim); font-size: .9rem; max-width: 30rem; }
  /* with boxes on screen the gold box carries the moment; the banner shrinks to one pill where the guess chip sits */
  #found.compact { place-items: end start; padding: 0 0 1rem 1rem; }
  #found.compact .dim, #found.compact .rays, #found.compact .bit, #found.compact .obj, #found.compact .sub { display: none; }
  #found.compact .box { transform-origin: left bottom; }
  #found.compact .kicker { font-size: 1.45rem; padding: .45rem 1.2rem; }

  /* found banner */
  #found { position: absolute; inset: 0; z-index: 5; display: grid; place-items: center; pointer-events: none; opacity: 0; transition: opacity .35s; overflow: hidden; }
  #found.on { opacity: 1; }
  #found .dim { position: absolute; inset: 0; background: radial-gradient(circle, rgba(40, 24, 2, .55), rgba(0,0,0,.78)); }
  #found .rays { position: absolute; width: 160%; aspect-ratio: 1; left: -30%; top: 50%; margin-top: -80%;
    background: repeating-conic-gradient(rgba(255, 211, 107, .16) 0 6deg, transparent 6deg 18deg);
    -webkit-mask: radial-gradient(circle, #000 0, transparent 60%); mask: radial-gradient(circle, #000 0, transparent 60%); }
  #found.on .rays { animation: spin 14s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  #found .box { position: relative; text-align: center; transform: scale(.6); transition: transform .5s cubic-bezier(.2, 1.6, .4, 1); }
  #found.on .box { transform: scale(1); }
  #found .kicker { display: inline-block; padding: .5rem 1.6rem; border-radius: 999px; background: linear-gradient(180deg, var(--amber), var(--honey));
    color: #2a1802; font-size: 2.6rem; font-weight: 900; letter-spacing: .06em; text-transform: uppercase; box-shadow: 0 0 3rem rgba(246, 183, 46, .7); }
  #found .obj { margin-top: 1rem; font-size: 4.6rem; font-weight: 900; line-height: 1.05; color: #fff; text-shadow: 0 .3rem 1.6rem rgba(0,0,0,.8); }
  #found .sub { margin-top: .6rem; font-size: 1.25rem; font-weight: 700; color: var(--amber); }
  .bit { position: absolute; width: 1.3rem; height: 1.5rem; clip-path: polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%);
    left: 50%; top: 50%; animation: burst 1.6s cubic-bezier(.1, .7, .3, 1) forwards; }
  @keyframes burst { from { transform: translate(-50%, -50%) scale(.3); opacity: 1; }
    to { transform: translate(calc(-50% + var(--dx)), calc(-50% + var(--dy))) rotate(var(--r)) scale(1); opacity: 0; } }

  /* progress tiles */
  .tilesrow { flex: none; }
  .tilesrow .cardhead { margin-bottom: .45rem; padding: 0 .2rem; }
  .tiles { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: .6rem; }
  .tile { position: relative; border-radius: .9rem; padding: .7rem .7rem .6rem; background: var(--panel); border: 2px solid var(--line);
    min-height: 5.2rem; display: flex; flex-direction: column; justify-content: space-between; transition: background .5s, border-color .5s, opacity .5s, transform .3s; }
  .tile .nm { font-size: 1.12rem; font-weight: 800; line-height: 1.12; padding-right: 1rem; }
  .tile .st { font-size: .74rem; font-weight: 800; letter-spacing: .12em; text-transform: uppercase; color: var(--mute); margin-top: .3rem; }
  .tile .ix { position: absolute; top: .45rem; right: .6rem; font-size: .8rem; font-weight: 800; color: var(--mute); }
  .tile.found { background: linear-gradient(160deg, var(--amber), var(--honey) 60%, var(--deep)); border-color: var(--amber); color: #241502;
    box-shadow: 0 0 1.6rem rgba(246, 183, 46, .35); }
  .tile.found .st, .tile.found .ix { color: #4a2d05; }
  .tile.skipped { opacity: .5; border-style: dashed; }
  .tile.skipped .nm { color: var(--dim); }
  .tile.offer { border-color: var(--amber); transform: translateY(-.2rem); animation: glow 1.2s ease-in-out infinite; }
  .tile.offer .st { color: var(--amber); }
  .tile.found.offer .st { color: #4a2d05; }
  .tile.pop { animation: pop .7s cubic-bezier(.2, 1.8, .4, 1); }
  @keyframes glow { 50% { box-shadow: 0 0 1.4rem rgba(255, 211, 107, .55); } }
  @keyframes pop { 0% { transform: scale(.85); } 100% { transform: scale(1); } }

  /* latency strip */
  .lat { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .6rem; flex: none; }
  .lat .card { padding: .55rem .9rem; }
  .lat .hop { font-size: .74rem; font-weight: 800; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .lat .v { font-size: 1.55rem; font-weight: 900; line-height: 1.15; }
  .lat .v.none { color: var(--mute); }
  .lat .md { font-size: .82rem; color: var(--dim); font-weight: 600; white-space: nowrap; }
  .lat .v.fresh { animation: flashv 1s ease-out; }
  @keyframes flashv { from { color: var(--amber); } }

  /* says */
  #saycard { border-color: rgba(108, 184, 255, .35); background: linear-gradient(160deg, rgba(30, 44, 62, .55), var(--panel) 60%); flex: none; }
  #saycard .label { color: var(--sky); }
  #caption { font-size: 1.75rem; font-weight: 800; line-height: 1.22; min-height: 2.44em; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
  #caption.empty { color: var(--mute); font-weight: 600; font-style: italic; font-size: 1.4rem; }
  #caption.fresh { animation: rise .45s ease-out; }
  @keyframes rise { from { opacity: 0; transform: translateY(.5rem); } }
  #kind { display: inline-block; padding: .15rem .6rem; border-radius: 999px; background: rgba(108, 184, 255, .18); color: var(--sky); font-size: .74rem;
    font-weight: 800; letter-spacing: .12em; text-transform: uppercase; vertical-align: .3rem; margin-right: .5rem; }
  #kind:empty { display: none; }
  #prev { color: var(--dim); font-size: .95rem; margin-top: .35rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-height: 1.2em; }
  .mirror { display: flex; align-items: center; gap: .9rem; margin-top: .6rem; }
  #mirrorbtn { flex: none; display: flex; align-items: center; gap: .5rem; cursor: pointer; border: 2px solid var(--sky); background: transparent; color: var(--sky);
    font-size: .95rem; font-weight: 800; line-height: 1; letter-spacing: .04em; padding: .55rem .9rem; border-radius: 999px; }
  #mirrorbtn svg { width: 1.2rem; height: 1.2rem; }
  #mirrorbtn.on { background: var(--sky); color: #06182b; }
  #mirrorbtn:not(.on) { animation: nudge 2.4s ease-in-out infinite; }
  @keyframes nudge { 50% { box-shadow: 0 0 1.2rem rgba(108, 184, 255, .5); } }
  #meter { flex: 1; height: 2.4rem; min-width: 0; display: block; }
  #audnote { font-size: .8rem; color: var(--dim); font-weight: 600; white-space: nowrap; min-width: 0; overflow: hidden; text-overflow: ellipsis; max-width: 10rem; }

  /* heard */
  #heardcard { flex: none; border-color: rgba(143, 209, 111, .3); }
  #heardcard .label { color: var(--meadow); }
  .heardrow { display: flex; align-items: center; gap: 1rem; }
  #heard { flex: 1; min-width: 0; font-size: 1.6rem; font-weight: 800; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  #heard.empty { color: var(--mute); font-weight: 600; font-style: italic; font-size: 1.3rem; }
  #heard.fresh { animation: rise .45s ease-out; }
  #answer { flex: none; padding: .4rem 1rem; border-radius: 999px; font-size: 1rem; font-weight: 900; letter-spacing: .12em; text-transform: uppercase;
    background: var(--panel2); color: var(--mute); }
  #answer.yes { background: var(--meadow); color: #0d2207; } #answer.no { background: #8b9bb0; color: #121a24; }
  #answer.rude { background: var(--bad); color: #2b0703; } #answer.other { background: var(--plum); color: #1d0f33; }

  /* scores */
  #scorecard { flex: none; }
  #scorecard .cardhead { margin-bottom: .8rem; }
  .bars { display: flex; flex-direction: column; gap: .32rem; position: relative; }
  .bar { display: grid; grid-template-columns: 11rem minmax(0, 1fr) 3.4rem; align-items: center; gap: .7rem; }
  .bar .n { font-size: .98rem; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--dim); }
  .bar .t { position: relative; height: 1.15rem; border-radius: .6rem; background: rgba(255,255,255,.07); overflow: hidden; }
  .bar .f { position: absolute; left: 0; top: 0; bottom: 0; width: 0; border-radius: .6rem; background: linear-gradient(90deg, var(--deep), var(--honey));
    transition: width .22s linear; }
  .bar .p { text-align: right; font-size: 1rem; font-weight: 800; color: var(--dim); }
  .bar.lead .n, .bar.lead .p { color: var(--text); }
  .bar.lead .f { background: linear-gradient(90deg, var(--honey), var(--amber)); box-shadow: 0 0 .8rem rgba(255, 211, 107, .6); }
  .bar.bgc .f { background: linear-gradient(90deg, #4a4234, #7a6f5c); }
  .bar.bgc .n { font-style: italic; }
  .bar.unk .f { opacity: .35; }
  .bar.unk .p { color: var(--mute); font-weight: 600; }
  .thr { position: absolute; top: -.2rem; bottom: -.2rem; width: 0; border-left: 2px dashed rgba(255, 211, 107, .5); pointer-events: none; }
  .thr span { position: absolute; top: -1.05rem; left: -1.6rem; width: 3.2rem; text-align: center; font-size: .62rem; font-weight: 800; letter-spacing: .08em; color: rgba(255, 211, 107, .75); }
  #scorecard.stale .bars { opacity: .35; }
  #stalenote { display: none; color: var(--bad); font-weight: 700; font-size: .9rem; }
  #scorecard.stale #stalenote { display: inline; }
  #scorecard.stale .meta { display: none; }

  /* timeline */
  #tlcard { flex: 1 1 0; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
  #timeline { list-style: none; margin: 0; padding: 0; overflow: hidden; flex: 1; min-height: 0;
    -webkit-mask: linear-gradient(#000 70%, transparent); mask: linear-gradient(#000 70%, transparent); }
  #timeline li { display: grid; grid-template-columns: 4.6rem 5.6rem minmax(0, 1fr); gap: .6rem; align-items: baseline; padding: .28rem 0;
    border-bottom: 1px solid rgba(255,255,255,.05); animation: rise .35s ease-out; }
  #timeline time { color: var(--mute); font-size: .85rem; font-weight: 600; }
  #timeline .tg { justify-self: start; font-size: .68rem; font-weight: 900; letter-spacing: .1em; text-transform: uppercase; padding: .12rem .5rem; border-radius: 999px;
    background: var(--panel2); color: var(--dim); white-space: nowrap; }
  #timeline .tx { font-size: .98rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  #timeline .found .tg { background: var(--honey); color: #2a1802; } #timeline .found .tx { color: var(--amber); font-weight: 800; }
  #timeline .say .tg { background: rgba(108, 184, 255, .2); color: var(--sky); }
  #timeline .heard .tg { background: rgba(143, 209, 111, .2); color: var(--meadow); }
  #timeline .blocked .tg { background: rgba(246, 183, 46, .15); color: var(--amber); }
  #timeline .slide .tg { background: rgba(195, 155, 255, .2); color: var(--plum); }
  #timeline .latency .tx { color: var(--dim); }
  #timeline .empty { color: var(--mute); font-style: italic; display: block; border: 0; }

  /* footer health */
  footer { display: flex; align-items: center; gap: .7rem; padding: .7rem 1.6rem .8rem; min-width: 0; }
  .chip { display: flex; align-items: center; gap: .5rem; padding: .35rem .8rem; border-radius: 999px; background: var(--panel2); border: 1px solid var(--line);
    font-size: .86rem; font-weight: 700; white-space: nowrap; color: var(--dim); flex: none; }
  .chip i { width: .65rem; height: .65rem; border-radius: 50%; background: var(--bad); box-shadow: 0 0 .5rem var(--bad); }
  .chip.ok i { background: var(--meadow); box-shadow: 0 0 .5rem var(--meadow); }
  .chip.warn i { background: var(--honey); box-shadow: 0 0 .5rem var(--honey); }
  .chip b { color: var(--text); }
  #refs { flex: 1 1 auto; min-width: 0; color: var(--mute); font-size: .82rem; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  #refs b { color: var(--dim); }
  #refs .zero, #refs .zero b { color: var(--bad); }
  button.ghost { cursor: pointer; background: transparent; border: 1px solid var(--line); color: var(--dim); font-size: .86rem; font-weight: 700; line-height: 1;
    padding: .45rem .8rem; border-radius: 999px; flex: none; }
  button.ghost:hover { color: var(--text); border-color: var(--honey); }

  /* practice switch */
  #practice { display: flex; align-items: center; gap: .6rem; cursor: pointer; background: var(--panel2); border: 1px solid var(--line); color: var(--dim);
    font-size: .95rem; font-weight: 800; padding: .45rem .9rem .45rem .5rem; border-radius: 999px; white-space: nowrap; margin-left: auto; }
  #practice i { position: relative; width: 2.4rem; height: 1.35rem; border-radius: 999px; background: #4a3d29; flex: none; transition: background .2s; }
  #practice i::after { content: ""; position: absolute; top: .18rem; left: .2rem; width: 1rem; height: 1rem; border-radius: 50%; background: #d8ccb4; transition: left .2s; }
  #practice.on { border-color: var(--meadow); color: var(--text); }
  #practice.on i { background: var(--meadow); }
  #practice.on i::after { left: 1.2rem; background: #fff; }
  #practice.err { border-color: var(--bad); }
  #phase { margin-left: 0; }

  /* slide check (screen test) */
  #checks { display: none; flex: 0 0 46%; min-height: 0; flex-direction: column; overflow: hidden; }
  body.slidemode #checks { display: flex; }
  body.slidemode .tilesrow, body.slidemode .lat { display: none; }
  #checks .cardhead { margin-bottom: .35rem; }
  #checksum { font-size: 1.15rem; font-weight: 900; color: var(--text); white-space: nowrap; }
  #checksum small { color: var(--dim); font-weight: 700; font-size: .85rem; margin-left: .5rem; }
  .crow { display: grid; grid-template-columns: 4.4rem 9.6rem 10.6rem minmax(0, 1fr); gap: .7rem; align-items: center; padding: .3rem 0;
    border-bottom: 1px solid rgba(255,255,255,.05); font-size: 1rem; }
  .crow > * { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
  .crow.head { font-size: .7rem; font-weight: 800; letter-spacing: .12em; text-transform: uppercase; color: var(--mute); padding: 0 0 .25rem; }
  .crow time { color: var(--mute); font-size: .85rem; font-weight: 600; }
  .crow .shw { font-weight: 800; }
  .crow .saw { color: var(--dim); font-weight: 600; }
  .crow .saw.hit { color: var(--text); }
  .crow.blank { font-size: .85rem; padding: .12rem 0; }
  .crow.blank .shw, .crow.blank .saw { color: var(--mute); font-weight: 600; font-style: italic; }
  .crow.live { background: linear-gradient(90deg, rgba(246, 183, 46, .1), transparent); }
  .res { display: flex; align-items: center; gap: .5rem; }
  .res b { flex: none; display: inline-flex; align-items: center; padding: .14rem .6rem; border-radius: 999px; font-size: .85rem; font-weight: 900; }
  .res span { color: var(--dim); font-size: .88rem; font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
  .res.ok b { background: var(--meadow); color: #0d2207; }
  .res.wrong b { background: var(--bad); color: #2b0703; }
  .res.miss b { background: rgba(239, 106, 87, .2); color: var(--bad); }
  .res.blocked b { background: rgba(246, 183, 46, .2); color: var(--amber); }
  .res.pend b { background: var(--panel2); color: var(--dim); }
  #checklist { flex: 1; min-height: 0; overflow: hidden; -webkit-mask: linear-gradient(#000 80%, transparent); mask: linear-gradient(#000 80%, transparent); }
  #checklist .none { color: var(--mute); font-style: italic; padding: .6rem 0; }
  #checksbtn.on { color: var(--text); border-color: var(--honey); }

  /* detection log (vision worker /debug/recent) */
  #dlog { position: fixed; left: 1.6rem; right: 1.6rem; bottom: 3.7rem; height: 52vh; z-index: 15; display: none; flex-direction: column; gap: .6rem;
    background: rgba(24, 18, 10, .97); border: 1px solid var(--honey); border-radius: 1rem; padding: .8rem 1rem 1rem; box-shadow: 0 -1rem 3rem rgba(0,0,0,.6); }
  #dlog.on { display: flex; }
  #dlog .cardhead { margin: 0; align-items: center; }
  #dlstat { color: var(--dim); font-size: .85rem; font-weight: 600; margin-right: auto; margin-left: 1rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  #dlstat.err { color: var(--bad); }
  .legend { display: flex; gap: .5rem; font-size: .75rem; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; white-space: nowrap; }
  .legend i { font-style: normal; padding: .15rem .55rem; border-radius: 999px; border: 2px solid; }
  .legend .lf { border-color: var(--amber); color: var(--amber); } .legend .ld { border-color: var(--bad); color: var(--bad); }
  #dlgrid { flex: 1; min-height: 0; display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); grid-template-rows: repeat(2, minmax(0, 1fr)); gap: .6rem; }
  #dlgrid .none { grid-column: 1 / -1; grid-row: 1 / -1; display: grid; place-items: center; color: var(--mute); font-style: italic; font-size: 1.1rem; text-align: center; }
  .dl { border: 2px solid var(--line); border-radius: .7rem; overflow: hidden; display: flex; flex-direction: column; min-height: 0; min-width: 0; background: var(--panel2); }
  .dl .th { position: relative; flex: 1; min-height: 0; background: #000; }
  .dl .th img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
  .dl .th .noimg { position: absolute; inset: 0; display: grid; place-items: center; color: var(--mute); font-size: .8rem; }
  .dl .lb { position: absolute; left: 0; right: 0; bottom: 0; padding: 1.1rem .5rem .3rem; background: linear-gradient(transparent, rgba(0,0,0,.88));
    font-size: 1.2rem; font-weight: 900; line-height: 1.1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .dl .lb small { font-size: .85rem; color: var(--amber); margin-left: .3rem; }
  .dl .lb.bgl { color: var(--dim); font-style: italic; }
  .dl time { position: absolute; top: .3rem; right: .35rem; font-size: .7rem; font-weight: 700; color: var(--dim); background: rgba(0,0,0,.65); padding: .05rem .35rem; border-radius: .3rem; }
  .dl .flag { position: absolute; top: .3rem; left: .35rem; font-size: .66rem; font-weight: 900; letter-spacing: .08em; text-transform: uppercase; padding: .1rem .4rem; border-radius: .3rem; display: none; }
  .dl .mt { padding: .3rem .5rem .35rem; font-size: .8rem; line-height: 1.35; }
  .dl .mt div { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--text); }
  .dl .mt b { color: var(--mute); font-size: .66rem; letter-spacing: .1em; margin-right: .3rem; }
  .dl .mt .dim { color: var(--mute); }
  .dl.dis { border-color: var(--bad); } .dl.dis .flag.fd { display: block; background: var(--bad); color: #2b0703; }
  .dl.fire { border-color: var(--amber); box-shadow: 0 0 1rem rgba(255, 211, 107, .45); } .dl.fire .flag.ff { display: block; background: var(--amber); color: #2a1802; }
  .dl.fire.dis .flag.fd { top: 1.5rem; }
  #dlogbtn.on { color: var(--text); border-color: var(--honey); }

  /* enrol drawer */
  #enrol { position: fixed; top: 0; right: 0; bottom: 0; width: 30rem; max-width: 92vw; z-index: 20; background: #1b140c; border-left: 1px solid var(--line);
    box-shadow: -2rem 0 4rem rgba(0,0,0,.6); padding: 1.6rem; display: flex; flex-direction: column; gap: 1rem; transform: translateX(105%); transition: transform .3s; overflow-y: auto; }
  #enrol.on { transform: none; }
  #enrol h2 { margin: 0; font-size: 1.6rem; }
  #enrol p { margin: 0; color: var(--dim); font-size: .95rem; }
  #enrol select, #enrol button.go { font-family: inherit; font-size: 1.1rem; font-weight: 700; padding: .7rem .9rem; border-radius: .7rem; border: 1px solid var(--line);
    background: var(--panel2); color: var(--text); }
  #enrol button.go { cursor: pointer; background: var(--honey); color: #2a1802; border: 0; font-weight: 900; }
  #enrol button.go:disabled { opacity: .55; cursor: wait; }
  #enrolmsg { font-size: 1.05rem; font-weight: 700; min-height: 1.4em; }
  #enrolmsg.err { color: var(--bad); } #enrolmsg.ok { color: var(--meadow); }
  #reftable { width: 100%; border-collapse: collapse; font-size: .98rem; }
  #reftable td { padding: .35rem .2rem; border-bottom: 1px solid rgba(255,255,255,.06); }
  #reftable td:last-child { text-align: right; font-weight: 800; }
  #reftable tr.zero td:last-child { color: var(--bad); }
  .row { display: flex; gap: .6rem; }
  .row select { flex: 1; }
  .kbd { color: var(--mute); font-size: .8rem; }
</style>
</head>
<body>
<header>
  <div class="brand"><div class="logo"><i></i></div><div><h1>BEE SCOUT <b>LIVE</b></h1><small>Live from Mentra Live glasses &middot; Beeswax in the wearer's ear</small></div></div>
  <button id="practice" title="POST /scout/practice"><i></i><span>Practice mode: every slide can trigger</span></button>
  <div id="phase"><b>-</b><span>waiting for the Hive</span></div>
  <div id="clock" class="num">--:--<small>elapsed</small></div>
</header>
<main>
  <section class="left">
    <div id="vidwrap">
      <video id="video" autoplay muted playsinline></video>
      <canvas id="boxes"></canvas>
      <div id="nostream"><div><div class="hexbig"><i></i></div><h2>Waiting for the glasses stream</h2><p id="nsmsg">Connecting...</p><p id="nshelp"></p></div></div>
      <div class="ov" id="livetag"><i></i><span id="livetxt">Glasses camera</span></div>
      <div class="ov" id="slidetag"></div>
      <button class="ov ghost" id="micbtn">Glasses mic: off</button>
      <div class="ov none" id="guess"><div class="ring"><b class="num" id="gpct">0%</b></div><div class="what"><small>The AI sees</small><span id="gname">-</span></div></div>
      <div class="ov" id="latro" title="Video: WebRTC network + jitter buffer + decode, measured by this browser (from MediaMTX on). AI: from the vision worker reading the frame to its result on this screen. For true glass-to-screen delay press T and point the glasses at the clock."><small>Delay on this laptop</small><div><span>Video playback</span><b id="lvid" class="none">-</b></div><div><span>Frame &rarr; AI result</span><b id="lai" class="none">-</b></div></div>
      <div id="found"><div class="dim"></div><div class="rays"></div><div class="box"><div class="kicker">Bee fact found!</div><div class="obj" id="fobj">-</div><div class="sub num" id="fsub"></div></div></div>
    </div>
    <div class="tilesrow"><div class="cardhead"><span class="label">Scout progress</span><span class="meta num" id="progtxt">0 of 6 facts found</span></div><div class="tiles" id="tiles"></div></div>
    <div class="lat" id="lat"></div>
    <div class="card" id="checks">
      <div class="cardhead"><span class="label">Slide check &middot; did each slide trigger?</span><span id="checksum">-</span><button class="ghost" id="checksclear">Clear</button></div>
      <div class="crow head"><span>time</span><span>screen showed</span><span>AI saw</span><span>result</span></div>
      <div id="checklist"><div class="none">Start the screen test (/scout/slides): each slide shows up here.</div></div>
    </div>
  </section>
  <section class="right">
    <div class="card" id="saycard">
      <div class="cardhead"><span class="label">Beeswax is saying &middot; in the wearer's ear</span></div>
      <div id="caption" class="empty">Nothing yet.</div>
      <div id="prev"></div>
      <div class="mirror">
        <button id="mirrorbtn" title="Play the same audio here (M)"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M3 9v6h4l5 5V4L7 9H3z"/><path d="M16.5 12a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z"/></svg><span id="mirrortxt">Hear it here</span></button>
        <canvas id="meter"></canvas>
        <span id="audnote"></span>
      </div>
    </div>
    <div class="card" id="heardcard">
      <div class="cardhead"><span class="label">Wearer said</span></div>
      <div class="heardrow"><div id="heard" class="empty">Waiting for an answer...</div><span id="answer">-</span></div>
    </div>
    <div class="card" id="scorecard">
      <div class="cardhead"><span class="label">What the AI is thinking</span><span class="meta num" id="fps">-</span><span id="stalenote">no frames from the vision worker</span></div>
      <div class="bars" id="bars"></div>
    </div>
    <div class="card" id="tlcard">
      <div class="cardhead"><span class="label">Timeline</span><span class="meta">newest first</span></div>
      <ul id="timeline"><li class="empty">Events show up here.</li></ul>
    </div>
  </section>
</main>
<footer>
  <span class="chip" id="hhive"><i></i>Hive events: <b>connecting</b></span>
  <span class="chip" id="hstream"><i></i>Stream: <b>-</b></span>
  <span class="chip" id="hvision"><i></i>Vision: <b>-</b></span>
  <span id="refs"></span>
  <button class="ghost" id="dlogbtn" title="Every detection from the vision worker (D)">Detections</button>
  <button class="ghost" id="checksbtn" title="Slide check panel (S)">Slide check</button>
  <button class="ghost" id="enrolbtn" title="Enrol references (E)">Calibrate</button>
</footer>
<section id="dlog">
  <div class="cardhead"><span class="label">Detection log &middot; last 12 from the vision worker</span><span id="dlstat">-</span>
    <span class="legend"><i class="lf">fired</i><i class="ld">OCR &ne; SIFT</i></span><button class="ghost" id="dlogclose">Close (D)</button></div>
  <div id="dlgrid"><div class="none">Waiting for the vision worker...</div></div>
</section>
<div id="bigclock"><b id="bigtime">00:00:00.000</b><span>Point the glasses at this clock: the video shows it a little late. The gap is the full glass-to-screen delay. (T hides it)</span></div>
<aside id="enrol">
  <div class="cardhead"><h2>Calibrate</h2><button class="ghost" id="enrolclose">Close (Esc)</button></div>
  <p>Point the glasses at one object, pick its label, press Enrol. The vision worker saves frames from the live stream as references. Use Background for the table, people and walls.</p>
  <div class="row"><select id="enrollabel"></select><button class="go" id="enrolgo">Enrol 5 s</button></div>
  <div id="enrolmsg"></div>
  <div class="label">References per label</div>
  <table id="reftable"></table>
  <p class="kbd">Keys: M mirror audio &middot; S slide check &middot; D detection log &middot; T latency clock &middot; E calibrate &middot; Esc close</p>
</aside>
<script>
(function () {
  "use strict";
  var CFG = __CONFIG__;
  var Q = new URLSearchParams(location.search);
  var token = Q.get("token") || "";
  var host = location.hostname || "localhost";
  var streamPath = Q.get("stream") || CFG.STREAM;
  var WEBRTC = (Q.get("webrtc") || ("http://" + host + ":" + CFG.PORTS.webrtc)).replace(/\\/+$/, "");
  var VISION = (Q.get("vision") || ("http://" + host + ":" + CFG.PORTS.vision)).replace(/\\/+$/, "");
  var CLASSES = CFG.LABELS.concat([CFG.BACKGROUND]);
  var TQ = "token=" + encodeURIComponent(token);
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function name(l) { return CFG.NAMES[l] || String(l || "").replace(/_/g, " "); }
  function pct(s) { return Math.round(Math.max(0, Math.min(1, s || 0)) * 100) + "%"; }
  function secs(ms) { return ms >= 10000 ? (ms / 1000).toFixed(0) + " s" : ms >= 1000 ? (ms / 1000).toFixed(2) + " s" : Math.round(ms) + " ms"; }
  function hhmmss(t) { var d = new Date(t || Date.now()); return [d.getHours(), d.getMinutes(), d.getSeconds()].map(function (n) { return (n < 10 ? "0" : "") + n; }).join(":"); }
  function restart(node, cls) { node.classList.remove(cls); void node.offsetWidth; node.classList.add(cls); }
  function chip(id, state, text) { var c = $(id); c.className = "chip " + state; c.querySelector("b").textContent = text; }

  // ------------------------------------------------------------------ state
  var S = { phase: null, found: [], skipped: [], offer: null, current: null, started_at: null };
  var lastFrameAt = 0, hiveOk = false, hiveAuth = true;

  // ------------------------------------------------------------------ progress tiles
  CFG.LABELS.forEach(function (l, i) {
    var t = el("div", "tile"); t.id = "tile_" + l;
    t.appendChild(el("span", "ix", String(i + 1)));
    t.appendChild(el("div", "nm", name(l)));
    t.appendChild(el("div", "st", "not yet"));
    $("tiles").appendChild(t);
  });
  var PHASES = { intro: "Beeswax introduces himself", looking: "looking for bee things", offering: "asked: want the rest?", telling: "telling the deeper fact", done: "six for six, hired" };
  function renderState() {
    var p = $("phase"); p.className = S.phase || "";
    p.querySelector("b").textContent = S.phase || "-";
    p.querySelector("span").textContent = S.phase ? (PHASES[S.phase] || "") : "waiting for the Hive";
    var found = S.found || [], skipped = S.skipped || [];
    $("practice").classList.toggle("on", !!S.practice);
    CFG.LABELS.forEach(function (l) {
      var t = $("tile_" + l), f = found.indexOf(l) >= 0, s = !f && skipped.indexOf(l) >= 0, o = S.offer === l;
      var cls = "tile" + (f ? " found" : "") + (s && !o ? " skipped" : "") + (o ? " offer" : "");
      if (f && !t.classList.contains("found")) cls += " pop";
      t.className = cls;
      t.querySelector(".st").textContent = o ? (S.phase === "telling" ? "telling now" : "asking now") : f ? "fact found" : s ? "skipped" : "not yet";
    });
    $("progtxt").textContent = found.length + " of " + CFG.LABELS.length + " facts found" + (skipped.length ? " \\u00b7 " + skipped.length + " skipped" : "");
  }
  function tickClock() {
    var c = $("clock").firstChild;
    if (!S.started_at) { c.textContent = "--:--"; return; }
    var s = Math.max(0, Math.floor((Date.now() - S.started_at) / 1000));
    c.textContent = Math.floor(s / 60) + ":" + (s % 60 < 10 ? "0" : "") + (s % 60);
  }
  setInterval(tickClock, 500);

  // ------------------------------------------------------------------ score bars
  var bars = {};
  CLASSES.forEach(function (c) {
    var b = el("div", "bar" + (c === CFG.BACKGROUND ? " bgc" : "")), t = el("div", "t"), f = el("div", "f");
    b.appendChild(el("span", "n", name(c))); t.appendChild(f); b.appendChild(t); b.appendChild(el("span", "p num", "-"));
    $("bars").appendChild(b); bars[c] = { row: b, fill: f, pctEl: b.querySelector(".p"), v: 0 };
  });
  (function () { // the match threshold (SCOUT_TUNING.minScore), drawn over the bar tracks
    var thr = el("div", "thr"); thr.appendChild(el("span", "", "MATCH"));
    $("bars").appendChild(thr);
    function place() {
      var t = bars[CFG.LABELS[0]].fill.parentNode, box = $("bars").getBoundingClientRect(), r = t.getBoundingClientRect();
      thr.style.left = (r.left - box.left + r.width * CFG.MIN_SCORE) + "px";
    }
    place(); addEventListener("resize", place);
  })();
  function onFrame(fr) {
    lastFrameAt = Date.now();
    var top = fr.top || [];
    var full = fr.scores && typeof fr.scores === "object"; // optional all-class scores (proposed), else the top 3
    var floor = top.length >= 3 ? top[top.length - 1].score : 0;
    CLASSES.forEach(function (c) {
      var b = bars[c], v = null;
      if (full && typeof fr.scores[c] === "number") v = fr.scores[c];
      else for (var i = 0; i < top.length; i++) if (top[i].label === c) v = top[i].score;
      if (v === null) { b.v = Math.min(b.v * 0.5, floor); b.row.classList.add("unk"); b.pctEl.textContent = top.length >= 3 ? "<" + pct(floor) : "-"; }
      else { b.v = v; b.row.classList.remove("unk"); b.pctEl.textContent = pct(v); }
      b.fill.style.width = (Math.max(0, Math.min(1, b.v)) * 100) + "%";
    });
    var lead = top[0];
    CLASSES.forEach(function (c) { bars[c].row.classList.toggle("lead", !!lead && lead.label === c); });
    checkFrame(fr, lead);
    if (Array.isArray(fr.boxes)) { bx.list = fr.boxes.filter(function (b) { return b && b.label !== CFG.BACKGROUND && b.w > 0 && b.h > 0; }); bx.at = Date.now(); }
    if (typeof fr.at === "number") { var lag = Date.now() - fr.at; if (lag >= 0 && lag < 60000) { aiLag.push(lag); if (aiLag.length > 15) aiLag.shift(); } }
    $("fps").textContent = (fr.fps || 0).toFixed(1) + " fps \\u00b7 " + Math.round(fr.frame_ms || 0) + " ms/frame";
    $("scorecard").classList.remove("stale");
    showGuess(lead || null);
  }
  function showGuess(g) {
    var box = $("guess");
    if (!g || boxesFresh()) { box.className = "ov none"; return; } // boxes on the video replace the guess chip
    var bg = g.label === CFG.BACKGROUND;
    box.className = "ov" + (bg ? " bg" : g.score >= CFG.MIN_SCORE ? " strong" : "");
    box.querySelector(".ring").style.setProperty("--p", String(Math.round(Math.max(0, Math.min(1, g.score)) * 100)));
    $("gpct").textContent = pct(g.score);
    $("gname").textContent = bg ? "Nothing bee-related" : name(g.label);
  }
  setInterval(function () {
    if (lastFrameAt && Date.now() - lastFrameAt > 3000) { $("scorecard").classList.add("stale"); showGuess(null); }
  }, 1000);

  // ------------------------------------------------------------------ detection boxes over the video
  var BOXCOL = { beehive: "#ffb020", native_flower: "#d68bff", honey_jar: "#ff8a3d", pebble_dish: "#5ec8ff", bug_spray: "#ff5a5a", bee_hotel: "#8fd16f" };
  var bx = { list: [], at: 0, flash: null }, disp = {}, aiLag = [];
  var BOX_HOLD_MS = 600, BOX_FADE_MS = 400;
  function boxesFresh() { return bx.list.length > 0 && Date.now() - bx.at < BOX_HOLD_MS; }
  var bc = $("boxes"), bg = bc.getContext("2d");
  function vrect(W, H) { // where the video actually is inside the element (object-fit: contain letterboxing)
    var vw = video.videoWidth || 16, vh = video.videoHeight || 9, k = Math.min(W / vw, H / vh);
    return { x: (W - vw * k) / 2, y: (H - vh * k) / 2, w: vw * k, h: vh * k };
  }
  function rrect(x, y, w, h, r) { bg.beginPath(); bg.moveTo(x + r, y); bg.arcTo(x + w, y, x + w, y + h, r); bg.arcTo(x + w, y + h, x, y + h, r); bg.arcTo(x, y + h, x, y, r); bg.arcTo(x, y, x + w, y, r); bg.closePath(); }
  function drawBoxes() {
    var d = devicePixelRatio || 1, W = bc.clientWidth * d, H = bc.clientHeight * d;
    if (bc.width !== Math.round(W) || bc.height !== Math.round(H)) { bc.width = Math.round(W); bc.height = Math.round(H); }
    bg.clearRect(0, 0, bc.width, bc.height);
    var now = Date.now(), age = now - bx.at, alpha = age < BOX_HOLD_MS ? 1 : Math.max(0, 1 - (age - BOX_HOLD_MS) / BOX_FADE_MS);
    if (alpha <= 0 || !bx.list.length || !playing) { disp = {}; requestAnimationFrame(drawBoxes); return; }
    var r = vrect(bc.width, bc.height), rem = parseFloat(getComputedStyle(document.documentElement).fontSize) * d, seen = {}, count = {};
    var flash = bx.flash && now < bx.flash.until ? bx.flash.label : null;
    bx.list.forEach(function (b) {
      count[b.label] = (count[b.label] || 0) + 1;
      var key = b.label + ":" + count[b.label];
      var t = { x: r.x + b.x * r.w, y: r.y + b.y * r.h, w: b.w * r.w, h: b.h * r.h }, p = disp[key];
      if (!p) p = disp[key] = t; else { p.x += (t.x - p.x) * 0.35; p.y += (t.y - p.y) * 0.35; p.w += (t.w - p.w) * 0.35; p.h += (t.h - p.h) * 0.35; }
      seen[key] = 1;
      var gold = flash === b.label, c = gold ? "#ffd36b" : BOXCOL[b.label] || "#e8dcc4", lw = (gold ? 5 : 3) * d;
      bg.save(); bg.globalAlpha = alpha;
      if (gold) { bg.shadowColor = "rgba(255, 211, 107, .9)"; bg.shadowBlur = 28 * d; }
      bg.strokeStyle = c; bg.lineWidth = lw; rrect(p.x, p.y, p.w, p.h, 6 * d); bg.stroke();
      bg.shadowBlur = 0;
      if (gold) { bg.fillStyle = "rgba(255, 211, 107, .12)"; rrect(p.x, p.y, p.w, p.h, 6 * d); bg.fill(); }
      var txt = (gold ? "BEE FACT FOUND \u00b7 " : "") + short(b.label) + " " + pct(b.score) + (b.via === "marker" ? " \u00b7 marker" : "");
      var fs = Math.round((gold ? 1.15 : 1) * rem);
      bg.font = "800 " + fs + "px -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";
      var tw = bg.measureText(txt).width + fs * 0.9, th = fs * 1.55, ty = p.y - th - lw / 2;
      if (ty < r.y) ty = p.y + lw / 2; // no room above: tag inside the box
      var tx = Math.min(Math.max(p.x - lw / 2, 0), bc.width - tw);
      bg.fillStyle = c; rrect(tx, ty, tw, th, 4 * d); bg.fill();
      bg.fillStyle = "#170f04"; bg.textBaseline = "middle"; bg.fillText(txt, tx + fs * 0.45, ty + th / 2 + d);
      bg.restore();
    });
    for (var k in disp) if (!seen[k]) delete disp[k];
    requestAnimationFrame(drawBoxes);
  }

  // ------------------------------------------------------------------ latency readout
  function median(a) { if (!a.length) return null; var s = a.slice().sort(function (x, y) { return x - y; }), m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; }
  function showLat(id, ms, slowAt) { var e = $(id); if (ms == null) { e.textContent = "-"; e.className = "none"; return; } e.textContent = Math.round(ms) + " ms"; e.className = ms > slowAt ? "slow" : ""; }
  var vstat = null;
  setInterval(function () {
    showLat("lai", lastFrameAt && Date.now() - lastFrameAt < 3000 ? median(aiLag) : null, 400);
    if (!pc || !playing) { showLat("lvid", null); vstat = null; return; }
    pc.getStats().then(function (st) {
      var v = null, rtt = null;
      st.forEach(function (r) {
        if (r.type === "inbound-rtp" && r.kind === "video") v = r;
        if (r.type === "candidate-pair" && r.nominated && r.state === "succeeded" && typeof r.currentRoundTripTime === "number") rtt = r.currentRoundTripTime;
      });
      if (!v) return;
      var cur = { jb: v.jitterBufferDelay || 0, jn: v.jitterBufferEmittedCount || 0, dt: v.totalDecodeTime || 0, dn: v.framesDecoded || 0 };
      if (vstat && cur.jn > vstat.jn && cur.dn > vstat.dn) { // averages over the last second
        var ms = ((cur.jb - vstat.jb) / (cur.jn - vstat.jn) + (cur.dt - vstat.dt) / (cur.dn - vstat.dn) + (rtt || 0) / 2) * 1000 + 16;
        showLat("lvid", ms, 400);
      }
      vstat = cur;
    }).catch(function () {});
  }, 1000);
  var bigOn = false;
  function tickBig() { if (!bigOn) return; var d = new Date(), ms = d.getMilliseconds(); $("bigtime").textContent = hhmmss(d.getTime()) + "." + (ms < 10 ? "00" : ms < 100 ? "0" : "") + ms; requestAnimationFrame(tickBig); }
  function toggleBig() { bigOn = !bigOn; $("bigclock").classList.toggle("on", bigOn); tickBig(); }

  // ------------------------------------------------------------------ found banner
  var foundTimer = null;
  function onFound(ev) {
    var boxed = boxesFresh() && bx.list.some(function (b) { return b.label === ev.label; });
    bx.flash = { label: ev.label, until: Date.now() + 3000 };
    $("found").classList.toggle("compact", boxed);
    $("found").querySelector(".kicker").textContent = boxed ? "Bee fact found: " + short(ev.label) : "Bee fact found!";
    $("fobj").textContent = name(ev.label);
    $("fsub").textContent = pct(ev.score) + " match \\u00b7 spotted in " + secs(ev.look_ms || 0);
    var f = $("found");
    f.querySelectorAll(".bit").forEach(function (b) { b.remove(); });
    var colors = ["#ffd36b", "#f6b72e", "#c9831a", "#8fd16f", "#fff3c4"];
    for (var i = 0; i < 26; i++) {
      var b = el("i", "bit"), a = Math.random() * Math.PI * 2, d = 14 + Math.random() * 22;
      b.style.setProperty("--dx", (Math.cos(a) * d) + "rem"); b.style.setProperty("--dy", (Math.sin(a) * d * 0.7) + "rem");
      b.style.setProperty("--r", (Math.random() * 540 - 270) + "deg");
      b.style.background = colors[i % colors.length]; b.style.animationDelay = (Math.random() * 0.15) + "s";
      f.appendChild(b);
    }
    f.classList.add("on");
    if (foundTimer) clearTimeout(foundTimer);
    foundTimer = setTimeout(function () { f.classList.remove("on"); }, 4500);
    var t = $("tile_" + ev.label); if (t) restart(t, "pop");
    var h = $("heard"); h.className = "empty"; h.textContent = "Waiting for yes or no..."; // a new offer: the last answer is stale
    var an = $("answer"); an.className = ""; an.textContent = "-";
  }

  // ------------------------------------------------------------------ timeline
  function addLog(kind, tag, text, at) {
    var ul = $("timeline"), first = ul.firstChild;
    if (first && first.classList && first.classList.contains("empty")) ul.removeChild(first);
    var li = el("li", kind);
    li.appendChild(el("time", "num", hhmmss(at)));
    li.appendChild(el("span", "tg", tag));
    li.appendChild(el("span", "tx", text));
    ul.insertBefore(li, ul.firstChild);
    while (ul.childNodes.length > 40) ul.removeChild(ul.lastChild);
  }

  // ------------------------------------------------------------------ latency
  var HOPS = [["frame_to_found", "First frame \\u2192 found"], ["slide_to_found", "Slide \\u2192 found"], ["found_to_say", "Found \\u2192 hook sent"], ["answer_to_fact", "Yes \\u2192 fact sent"]];
  var HOPNAME = {}, lat = {};
  HOPS.forEach(function (h) {
    HOPNAME[h[0]] = h[1]; lat[h[0]] = [];
    var c = el("div", "card"); c.id = "lat_" + h[0];
    c.appendChild(el("div", "hop", h[1])); c.appendChild(el("div", "v none num", "-")); c.appendChild(el("div", "md num", "no data yet"));
    $("lat").appendChild(c);
  });
  function onLatency(hop, ms) {
    if (!lat[hop]) return;
    var a = lat[hop]; a.push(ms); if (a.length > 25) a.shift();
    var s = a.slice().sort(function (x, y) { return x - y; }), med = s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2;
    var c = $("lat_" + hop), v = c.querySelector(".v");
    v.textContent = secs(ms); v.className = "v num"; restart(v, "fresh");
    c.querySelector(".md").textContent = "median " + secs(med) + " \\u00b7 n=" + a.length;
  }

  // ------------------------------------------------------------------ Beeswax says + mirror audio
  var KINDS = [["scout_intro", "intro"], ["scout_chime", "sting"], ["scout_hook", "hook"], ["scout_fact", "fact"], ["scout_no", "no"], ["scout_rude", "rude"],
    ["scout_silence", "silence"], ["scout_idle", "nudge"], ["scout_progress", "progress"], ["scout_done", "finale"]];
  function lineKind(id) { for (var i = 0; i < KINDS.length; i++) if (String(id).indexOf(KINDS[i][0]) === 0) return KINDS[i][1]; return ""; }
  var lastCaption = "";
  function onSay(ev) {
    var cap = ev.caption || (lineKind(ev.line_id) === "sting" ? "(the Bee fact found! sting)" : "(" + ev.line_id + ")");
    if (lastCaption) $("prev").textContent = "before: \\u201c" + lastCaption + "\\u201d";
    lastCaption = cap;
    var c = $("caption"); c.className = ""; c.textContent = "";
    var k = el("span", "", lineKind(ev.line_id)); k.id = "kind"; c.appendChild(k);
    c.appendChild(document.createTextNode("\\u201c" + cap + "\\u201d"));
    restart(c, "fresh");
    if (mirror) enqueue(ev);
  }

  var mirror = false, actx = null, analyser = null, freq = null, queue = [], cur = null;
  var audio = new Audio(); audio.preload = "auto";
  function ensureGraph() {
    if (actx) { if (actx.state === "suspended") actx.resume(); return; }
    try {
      var AC = window.AudioContext || window.webkitAudioContext;
      actx = new AC();
      var src = actx.createMediaElementSource(audio);
      analyser = actx.createAnalyser(); analyser.fftSize = 128; analyser.smoothingTimeConstant = 0.55;
      freq = new Uint8Array(analyser.frequencyBinCount);
      src.connect(analyser); analyser.connect(actx.destination);
    } catch (e) { actx = null; analyser = null; }
  }
  function note(t) { $("audnote").textContent = t || ""; }
  function enqueue(ev) {
    var local = "/scout/audio/" + encodeURIComponent(ev.line_id) + ".mp3?" + TQ;
    var urls = [local];
    if (ev.audio_url && ev.audio_url.indexOf("/scout/audio/" + ev.line_id) < 0) urls.push(ev.audio_url);
    queue.push({ id: ev.line_id, urls: urls, at: Date.now(), n: 0 });
    if (!cur) playNext();
  }
  function playNext() {
    while (queue.length && Date.now() - queue[0].at > 20000) queue.shift(); // never fall far behind the wearer
    cur = queue.shift() || null;
    if (!cur) { if (!/^no audio/.test($("audnote").textContent)) note(mirror ? "listening for Beeswax" : ""); return; }
    load();
  }
  function load() {
    audio.src = cur.urls[cur.n];
    note("playing: " + (lineKind(cur.id) || cur.id));
    var p = audio.play(); if (p && p.catch) p.catch(function () {});
  }
  audio.addEventListener("ended", playNext);
  audio.addEventListener("error", function () {
    if (!cur || !audio.getAttribute("src")) return;
    if (cur.n + 1 < cur.urls.length) { cur.n++; load(); return; }
    var id = cur.id; note("no audio file: " + id); playNext();
  });
  function setMirror(on) {
    mirror = on;
    $("mirrorbtn").className = on ? "on" : "";
    $("mirrortxt").textContent = on ? "Mirroring audio" : "Hear it here";
    if (on) { ensureGraph(); note("listening for Beeswax"); }
    else { queue = []; cur = null; audio.pause(); audio.removeAttribute("src"); note(""); }
  }
  $("mirrorbtn").addEventListener("click", function () { setMirror(!mirror); });

  // level meter: WebAudio analyser on the mirrored line
  var cv = $("meter"), g2 = cv.getContext("2d"), N = 28, lv = [];
  for (var li = 0; li < N; li++) lv.push(0);
  function sizeMeter() { var r = cv.getBoundingClientRect(), d = devicePixelRatio || 1; cv.width = Math.max(1, r.width * d); cv.height = Math.max(1, r.height * d); }
  sizeMeter(); addEventListener("resize", sizeMeter);
  function drawMeter() {
    var live = !!(analyser && cur && !audio.paused);
    if (live) analyser.getByteFrequencyData(freq);
    var w = cv.width, h = cv.height, gap = w / N * 0.3, bw = w / N - gap;
    g2.clearRect(0, 0, w, h);
    for (var i = 0; i < N; i++) {
      var target = 0;
      if (live) { var k = Math.floor(Math.pow(i / N, 1.4) * (freq.length * 0.7)); target = freq[k] / 255; }
      lv[i] += (target - lv[i]) * (target > lv[i] ? 0.6 : 0.18);
      var bh = Math.max(h * 0.08, lv[i] * h);
      g2.fillStyle = live ? (lv[i] > 0.75 ? "#ffd36b" : "#6cb8ff") : "rgba(178,161,132,.25)";
      g2.fillRect(i * (bw + gap), (h - bh) / 2, bw, bh);
    }
    requestAnimationFrame(drawMeter);
  }
  requestAnimationFrame(drawMeter);

  // ------------------------------------------------------------------ wearer said
  var ANS = { yes: "yes", no: "no", rude: "rude", other: "other" };
  function onHeard(ev) {
    var h = $("heard"); h.className = ""; h.textContent = "\\u201c" + (ev.text || "") + "\\u201d"; restart(h, "fresh");
    var a = $("answer"); a.className = ANS[ev.answer] || ""; a.textContent = ANS[ev.answer] || ev.answer || "-";
  }

  // ------------------------------------------------------------------ slide tag (screen test)
  var slideTimer = null;
  function onSlide(ev) {
    var t = $("slidetag");
    t.textContent = "Screen test: " + (ev.label == null ? "blank" : name(ev.label));
    t.className = "ov on";
    if (slideTimer) clearTimeout(slideTimer);
    slideTimer = setTimeout(function () { t.className = "ov"; }, 8000);
  }

  // ------------------------------------------------------------------ slide check: per screen-test slide, did it trigger and why not
  var BLOCKED = { already_found: "Already found (silent by design)", skipped: "Declined earlier (comes back later)", busy: "Beeswax was mid-line", cooldown: "4 s pause after a line" };
  var wins = [], CARRY_MS = 3000, FRAME_CARRY_MS = 1500, userHidChecks = 0;
  function isObj(l) { return l != null && l !== CFG.BACKGROUND; }
  function short(l) { return name(l).split(" /")[0]; }
  function setSlideMode(on) { document.body.classList.toggle("slidemode", on); $("checksbtn").classList.toggle("on", on); }
  function checkSlide(ev) {
    var now = Date.now(), last = wins[wins.length - 1];
    if (last) last.end = now;
    wins.push({ label: ev.label, at: ev.at, recv: now, end: 0, max: {}, led: {}, found: null, s2f: null, blocked: [] });
    if (wins.length > 60) wins.shift();
    if (now - userHidChecks > 60000) setSlideMode(true);
    renderChecks();
  }
  // Stream + vision lag: a find or block just after an object slide ended still belongs to that slide.
  function winFor(label) {
    var w = wins[wins.length - 1], prev = wins[wins.length - 2];
    if (!w) return null;
    if (!isObj(w.label) && prev && isObj(prev.label) && Date.now() - w.recv < CARRY_MS && (label == null || label === prev.label || !isObj(w.label))) return prev;
    return w;
  }
  function note1(w, fr, lead) {
    var sc = fr.scores && typeof fr.scores === "object" ? fr.scores : null;
    (fr.top || []).forEach(function (x) { if (!(x.score <= (w.max[x.label] || 0))) w.max[x.label] = x.score; });
    if (sc) for (var c in sc) if (typeof sc[c] === "number" && !(sc[c] <= (w.max[c] || 0))) w.max[c] = sc[c];
    if (lead && lead.score >= CFG.MIN_SCORE) w.led[lead.label] = Math.max(w.led[lead.label] || 0, lead.score);
  }
  var checkDirty = false;
  function checkFrame(fr, lead) {
    var w = wins[wins.length - 1], prev = wins[wins.length - 2];
    if (!w) return;
    note1(w, fr, lead);
    if (prev && isObj(prev.label) && Date.now() - w.recv < FRAME_CARRY_MS) note1(prev, fr, lead);
    checkDirty = true;
  }
  setInterval(function () { if (checkDirty) { checkDirty = false; renderChecks(); } }, 500);
  function checkFound(ev) { var w = winFor(ev.label); if (w && !w.found) { w.found = ev; renderChecks(); } }
  function checkS2F(ms) { for (var i = wins.length - 1; i >= Math.max(0, wins.length - 3); i--) if (wins[i].found && wins[i].s2f == null) { wins[i].s2f = ms; renderChecks(); return; } }
  function checkBlocked(ev) { var w = winFor(ev.label); if (w) { w.blocked.push(ev); renderChecks(); } }
  function bestSeen(w) { // best non-background label that led a frame, else background
    var best = null;
    CFG.LABELS.forEach(function (l) { if (w.led[l] && (!best || w.led[l] > best.score)) best = { label: l, score: w.led[l] }; });
    if (best) return { text: short(best.label) + " " + pct(best.score), hit: true };
    var raw = null;
    CFG.LABELS.forEach(function (l) { if (w.max[l] && (!raw || w.max[l] > raw.score)) raw = { label: l, score: w.max[l] }; });
    if (w.max[CFG.BACKGROUND] != null) return { text: "background " + pct(w.max[CFG.BACKGROUND]), hit: false, raw: raw };
    return { text: raw ? short(raw.label) + " " + pct(raw.score) : "no frames", hit: false, raw: raw };
  }
  function verdict(w, live) {
    var L = w.label;
    if (w.found) {
      if (w.found.label === L) {
        var s2f = w.s2f != null ? w.s2f : (w.found.at && w.at ? w.found.at - w.at : null);
        return { cls: "ok", tag: "\u2713 Bee fact found", sub: "look " + secs(w.found.look_ms || 0) + (s2f != null && s2f >= 0 ? " \u00b7 slide\u2192found " + secs(s2f) : "") };
      }
      return { cls: "wrong", tag: isObj(L) ? "Wrong object" : "Fired on blank", sub: "found " + name(w.found.label) + " " + pct(w.found.score) };
    }
    if (!isObj(L)) return null;
    var b = w.blocked.filter(function (x) { return x.label === L; })[0] || w.blocked[0];
    if (b) return { cls: "blocked", tag: BLOCKED[b.reason] || b.reason, sub: b.label !== L ? "saw " + name(b.label) + " " + pct(b.score) : pct(b.score) + " match, silent" };
    if (live) return { cls: "pend", tag: "watching\u2026", sub: "" };
    if (!w.led[L]) {
      var bs = bestSeen(w), r = bs.hit ? bs.text : bs.raw ? short(bs.raw.label) + " " + pct(bs.raw.score) : bs.text;
      return { cls: "miss", tag: "Not recognized", sub: "best: " + r };
    }
    return { cls: "miss", tag: "Seen, not steady", sub: name(L) + " " + pct(w.led[L]) + ", needs " + pct(CFG.MIN_SCORE) + " for 0.9 s" };
  }
  function renderChecks() {
    var box = $("checklist"); box.textContent = "";
    var objs = 0, hits = 0, wrong = 0, n = wins.length;
    if (!n) { box.appendChild(el("div", "none", "Start the screen test (/scout/slides): each slide shows up here.")); $("checksum").textContent = "-"; return; }
    for (var i = n - 1; i >= 0; i--) {
      var w = wins[i], live = i === n - 1 || (i === n - 2 && Date.now() - wins[n - 1].recv < CARRY_MS), obj = isObj(w.label);
      var v = verdict(w, live);
      if (obj) { objs++; if (w.found && w.found.label === w.label) hits++; else if (w.found) wrong++; }
      if (!obj && !v && i !== n - 1 && n - i > 12) continue; // keep old blank rows out of the way
      var row = el("div", "crow" + (obj ? "" : " blank") + (i === n - 1 ? " live" : ""));
      row.appendChild(el("time", "num", hhmmss(w.recv)));
      row.appendChild(el("span", "shw", obj ? short(w.label) : "blank"));
      var bs = bestSeen(w); row.appendChild(el("span", "saw num" + (bs.hit ? " hit" : ""), bs.text));
      var res = el("span", "res" + (v ? " " + v.cls : ""));
      if (v) { res.appendChild(el("b", "", v.tag)); if (v.sub) res.appendChild(el("span", "num", v.sub)); }
      else res.appendChild(el("span", "", "\u2013"));
      row.appendChild(res);
      box.appendChild(row);
    }
    var sum = $("checksum"); sum.textContent = hits + " of " + objs + " object slides triggered";
    if (wrong) sum.appendChild(el("small", "", wrong + " wrong"));
  }
  $("checksclear").addEventListener("click", function () { wins = []; renderChecks(); });
  $("checksbtn").addEventListener("click", function () {
    var on = !document.body.classList.contains("slidemode");
    setSlideMode(on); if (!on) userHidChecks = Date.now();
  });

  // ------------------------------------------------------------------ practice mode (POST /scout/practice)
  $("practice").addEventListener("click", function () {
    var btn = $("practice"), on = !btn.classList.contains("on");
    btn.classList.toggle("on", on); btn.classList.remove("err");
    fetch("/scout/practice?" + TQ, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ on: on }) })
      .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .then(function (st) { var s2 = st && st.state ? st.state : st; if (s2 && s2.phase) { S = s2; renderState(); } })
      .catch(function () { btn.classList.toggle("on", !on); btn.classList.add("err"); });
  });

  // ------------------------------------------------------------------ detection log: what the vision worker decided, frame by frame
  // GET <vision>/debug/recent -> newest first [{t, ocr: [..], ocr_label, sift: [{label, inliers}], final_label, score, img}]
  var founds = [], dlOn = false, dlTimer = null, dlSig = "", dlOkAt = 0;
  function msOf(t) { return typeof t === "number" ? (t < 1e12 ? t * 1000 : t) : Date.parse(t) || 0; }
  function lname(l) { return l ? short(l) : "-"; }
  function dlStat(t, err) { var e = $("dlstat"); e.textContent = t; e.className = err ? "err" : ""; }
  function pollDebug() {
    dlTimer = null;
    if (!dlOn) return;
    var ctl = new AbortController(), to = setTimeout(function () { ctl.abort(); }, 2500);
    fetch(VISION + "/debug/recent", { cache: "no-store", signal: ctl.signal }).then(function (r) {
      clearTimeout(to);
      if (r.status === 404) throw new Error("the vision worker has no /debug/recent yet (404)");
      if (!r.ok) throw new Error("/debug/recent answered HTTP " + r.status);
      return r.json();
    }).then(function (list) {
      list = (Array.isArray(list) ? list : (list && (list.recent || list.items)) || []).slice(0, 12);
      dlOkAt = Date.now();
      dlStat(list.length + " detections \u00b7 updated " + hhmmss(dlOkAt), false);
      renderDebug(list);
    }).catch(function (e) {
      clearTimeout(to);
      dlStat(e && e.name !== "AbortError" && e.message && e.message.indexOf("debug") >= 0 ? e.message : "vision worker not reachable at " + VISION, true);
    }).then(function () { if (dlOn && !dlTimer) dlTimer = setTimeout(pollDebug, 1000); });
  }
  function renderDebug(list) {
    var sig = list.map(function (d) { return (d.t || "") + (d.img || ""); }).join("|") + "#" + founds.length;
    if (sig === dlSig) return;
    dlSig = sig;
    var g = $("dlgrid"); g.textContent = "";
    if (!list.length) { g.appendChild(el("div", "none", "No detections yet. Point the glasses at something.")); return; }
    list.forEach(function (d) {
      var t = msOf(d.t), fl = d.final_label, sift = Array.isArray(d.sift) ? d.sift.slice().sort(function (a, b) { return (b.inliers || 0) - (a.inliers || 0); }) : [];
      var siftTop = sift[0] && sift[0].label, dis = !!(d.ocr_label && siftTop && d.ocr_label !== siftTop);
      var fire = !!fl && founds.some(function (f) { return f.label === fl && Math.abs(f.at - t) <= 1000; });
      var card = el("div", "dl" + (dis ? " dis" : "") + (fire ? " fire" : ""));
      var th = el("div", "th");
      if (d.img) {
        var img = el("img"); img.alt = ""; img.loading = "lazy";
        img.src = /^https?:/.test(d.img) ? d.img : VISION + (d.img.charAt(0) === "/" ? "" : "/") + d.img;
        img.onerror = function () { img.remove(); th.insertBefore(el("div", "noimg", "no image"), th.firstChild); };
        th.appendChild(img);
      } else th.appendChild(el("div", "noimg", "no image"));
      var bgl = !fl || fl === CFG.BACKGROUND, lb = el("div", "lb" + (bgl ? " bgl" : ""), bgl ? "background" : lname(fl));
      if (typeof d.score === "number") lb.appendChild(el("small", "num", pct(d.score)));
      th.appendChild(lb);
      if (t) th.appendChild(el("time", "num", hhmmss(t)));
      th.appendChild(el("span", "flag ff", "fired"));
      th.appendChild(el("span", "flag fd", "OCR \u2260 SIFT"));
      card.appendChild(th);
      var mt = el("div", "mt"), o = el("div"), sf = el("div");
      var words = Array.isArray(d.ocr) ? d.ocr.filter(Boolean) : [];
      o.appendChild(el("b", "", "OCR"));
      if (d.ocr_label) o.appendChild(document.createTextNode(lname(d.ocr_label) + " "));
      if (words.length) o.appendChild(el("span", "dim", "\u201c" + words.slice(0, 4).join(" ") + "\u201d"));
      else o.appendChild(el("span", "dim", "nothing"));
      sf.appendChild(el("b", "", "SIFT"));
      if (sift.length) sf.appendChild(document.createTextNode(sift.slice(0, 2).map(function (x) { return lname(x.label) + " " + (x.inliers || 0); }).join(" \u00b7 ")));
      else sf.appendChild(el("span", "dim", "no match"));
      mt.appendChild(o); mt.appendChild(sf); card.appendChild(mt);
      g.appendChild(card);
    });
  }
  function setDlog(on) {
    dlOn = on; $("dlog").classList.toggle("on", on); $("dlogbtn").classList.toggle("on", on);
    if (on) { dlSig = ""; if (!dlTimer) pollDebug(); }
    else if (dlTimer) { clearTimeout(dlTimer); dlTimer = null; }
  }
  $("dlogbtn").addEventListener("click", function () { setDlog(!dlOn); });
  $("dlogclose").addEventListener("click", function () { setDlog(false); });

  // ------------------------------------------------------------------ Hive events
  function handle(ev) {
    if (!ev || !ev.type) return;
    switch (ev.type) {
      case "frame": if (ev.frame) onFrame(ev.frame); break;
      case "state": if (ev.state) { S = ev.state; renderState(); tickClock(); } break;
      case "found": onFound(ev); addLog("found", "found", name(ev.label) + "  " + pct(ev.score) + " after " + secs(ev.look_ms || 0), ev.at); break;
      case "say": onSay(ev); addLog("say", lineKind(ev.line_id) || "say", ev.caption || ev.line_id, ev.at); break;
      case "heard": onHeard(ev); addLog("heard", "heard", "\\u201c" + ev.text + "\\u201d \\u2192 " + ev.answer, ev.at); break;
      case "latency": onLatency(ev.hop, ev.ms); addLog("latency", "latency", (HOPNAME[ev.hop] || ev.hop) + ": " + secs(ev.ms)); break;
      case "slide": onSlide(ev); checkSlide(ev); addLog("slide", "slide", ev.label == null ? "blank screen" : name(ev.label), ev.at); break;
      case "blocked": checkBlocked(ev); addLog("blocked", "silent", name(ev.label) + ": " + (BLOCKED[ev.reason] || ev.reason), ev.at); break;
    }
    if (ev.type === "found") { checkFound(ev); founds.push({ label: ev.label, at: ev.at || Date.now() }); if (founds.length > 40) founds.shift(); dlSig = ""; }
    if (ev.type === "latency" && ev.hop === "slide_to_found") checkS2F(ev.ms);
  }
  function fetchState() {
    fetch("/scout/state?" + TQ, { cache: "no-store" }).then(function (r) {
      hiveAuth = r.status !== 401;
      if (!r.ok) throw new Error(String(r.status));
      return r.json();
    }).then(function (st) { if (st && st.phase) { S = st; renderState(); tickClock(); } }).catch(function () { renderHive(); });
  }
  var es = null;
  function connect() {
    if (es) { try { es.close(); } catch (e) {} }
    es = new EventSource("/scout/events?" + TQ);
    es.onopen = function () { hiveOk = true; hiveAuth = true; renderHive(); fetchState(); };
    es.onerror = function () {
      hiveOk = false; renderHive();
      if (es.readyState === 2) { setTimeout(connect, 3000); fetchState(); } // closed for good (e.g. 401): retry ourselves
    };
    var parse = function (e) { try { handle(JSON.parse(e.data)); } catch (err) {} };
    es.onmessage = parse;
    ["frame", "found", "say", "heard", "state", "latency", "slide", "blocked"].forEach(function (t) { es.addEventListener(t, parse); });
  }
  function renderHive() {
    if (hiveOk) chip("hhive", "ok", "live");
    else chip("hhive", "", hiveAuth ? "reconnecting" : "bad token (add ?token=)");
  }
  connect(); fetchState();

  // ------------------------------------------------------------------ video: WHEP straight from MediaMTX
  var video = $("video"), pc = null, retryTimer = null, lastT = -1, lastMove = 0, playing = false, netFails = 0, iframeMode = Q.get("player") === "iframe";
  var playerUrl = WEBRTC + "/" + streamPath + "/";
  $("livetxt").textContent = "Glasses camera" + (streamPath !== CFG.STREAM ? " (" + streamPath + ")" : "");
  function setPlaying(on, why) {
    playing = on;
    $("vidwrap").classList.toggle("playing", on);
    $("nostream").classList.toggle("off", on);
    if (on) { chip("hstream", "ok", iframeMode ? "MediaMTX player" : "playing"); return; }
    chip("hstream", "", why === "mediamtx" ? "MediaMTX down" : "no stream");
    $("nsmsg").textContent = why === "mediamtx" ? "MediaMTX isn't answering at " + WEBRTC + "." : "Nothing is publishing to the \\u201c" + streamPath + "\\u201d path yet. Retrying...";
    var help = $("nshelp"); help.textContent = "";
    help.appendChild(document.createTextNode(why === "mediamtx" ? "Start it: " : "Glasses publish to "));
    help.appendChild(el("code", "", why === "mediamtx" ? "mediamtx ops/scout-mediamtx.yml" : "rtmp://<laptop-ip>:" + CFG.PORTS.rtmp + "/" + streamPath));
    help.appendChild(document.createTextNode(" \\u00b7 "));
    var a = el("a", "", "open the MediaMTX player"); a.href = playerUrl; a.target = "_blank"; help.appendChild(a);
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
    p.ontrack = function (e) {
      ms.addTrack(e.track); video.srcObject = ms;
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
    }).catch(function (err) {
      if (p !== pc) return;
      netFails++;
      setPlaying(false, err instanceof TypeError ? "mediamtx" : "");
      retry(Math.min(8000, 2000 * netFails));
    });
  }
  setInterval(function () { // frames moving = playing; stuck for 5 s = the publisher went away
    if (iframeMode) return;
    var t = video.currentTime, now = Date.now();
    if (t !== lastT && video.readyState >= 2) { lastT = t; lastMove = now; if (!playing) setPlaying(true); }
    else if (playing && now - lastMove > 5000) { setPlaying(false); retry(500); }
    if (pc && playing) pc.getStats().then(function (st) { // offer the glasses mic only if audio actually arrives
      var audioIn = false;
      st.forEach(function (r) { if (r.type === "inbound-rtp" && r.kind === "audio" && r.bytesReceived > 0) audioIn = true; });
      $("micbtn").className = "ov ghost" + (audioIn ? " on" : "");
    }).catch(function () {});
  }, 1000);
  $("micbtn").addEventListener("click", function () { video.muted = !video.muted; $("micbtn").textContent = "Glasses mic: " + (video.muted ? "off" : "on"); });
  if (iframeMode) {
    video.remove();
    var fr = el("iframe"); fr.src = playerUrl + "?controls=false&muted=true&autoplay=true"; fr.allow = "autoplay";
    $("vidwrap").insertBefore(fr, $("vidwrap").firstChild);
    setPlaying(true);
  } else {
    setPlaying(false); $("nsmsg").textContent = "Connecting to " + WEBRTC + "...";
    startWhep();
  }

  // ------------------------------------------------------------------ vision worker: health + enrol
  var refs = {};
  function getJson(url, opt, ms) {
    var ctl = new AbortController(), t = setTimeout(function () { ctl.abort(); }, ms || 2500);
    opt = opt || {}; opt.signal = ctl.signal; opt.cache = "no-store";
    return fetch(url, opt).then(function (r) { clearTimeout(t); if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); }, function (e) { clearTimeout(t); throw e; });
  }
  function pollVision() {
    getJson(VISION + "/health").then(function (h) {
      refs = h.refs || {};
      chip("hvision", h.ok ? "ok" : "warn", (h.ok ? "ok" : "not ok") + " \\u00b7 " + (h.fps || 0).toFixed(1) + " fps");
      $("hvision").title = (h.model || "") + " \\u00b7 " + (h.source || "");
      renderRefs();
    }).catch(function () { chip("hvision", "", "offline"); $("refs").textContent = "vision worker not reachable at " + VISION; refs = {}; renderRefTable(); });
  }
  function renderRefs() {
    var r = $("refs"); r.textContent = "refs ";
    CLASSES.forEach(function (c, i) {
      var n = refs[c] || 0;
      if (i) r.appendChild(document.createTextNode(" \\u00b7 "));
      var s = el("span", n ? "" : "zero"); s.appendChild(el("b", "", name(c).split(" /")[0] + " ")); s.appendChild(document.createTextNode(String(n)));
      r.appendChild(s);
    });
    renderRefTable();
  }
  function renderRefTable() {
    var t = $("reftable"); t.textContent = "";
    CLASSES.forEach(function (c) {
      var tr = el("tr", refs[c] ? "" : "zero"); tr.appendChild(el("td", "", name(c))); tr.appendChild(el("td", "num", String(refs[c] || 0))); t.appendChild(tr);
    });
  }
  pollVision(); setInterval(pollVision, 3000);
  CLASSES.forEach(function (c) { var o = el("option", "", name(c)); o.value = c; $("enrollabel").appendChild(o); });
  function openEnrol(on) { $("enrol").classList.toggle("on", on); }
  $("enrolbtn").addEventListener("click", function () { openEnrol(!$("enrol").classList.contains("on")); });
  $("enrolclose").addEventListener("click", function () { openEnrol(false); });
  $("enrolgo").addEventListener("click", function () {
    var label = $("enrollabel").value, btn = $("enrolgo"), m = $("enrolmsg"), left = 5;
    btn.disabled = true; m.className = ""; m.textContent = "Recording " + name(label) + "... " + left + " s";
    var tick = setInterval(function () { left--; m.textContent = left > 0 ? "Recording " + name(label) + "... " + left + " s" : "Saving..."; }, 1000);
    getJson(VISION + "/enrol", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ label: label, seconds: 5 }) }, 20000).then(function (r) {
      m.className = "ok"; m.textContent = "Saved " + r.saved + " frames. " + name(r.label) + " now has " + r.total + " references.";
      pollVision();
    }).catch(function (e) {
      m.className = "err"; m.textContent = "Enrol failed (" + ((e && e.message) || "vision worker unreachable") + ").";
    }).then(function () { clearInterval(tick); btn.disabled = false; });
  });

  // ------------------------------------------------------------------ keys
  addEventListener("keydown", function (e) {
    if (e.target && (e.target.tagName === "SELECT" || e.target.tagName === "INPUT")) return;
    if (e.key === "m" || e.key === "M") setMirror(!mirror);
    else if (e.key === "s" || e.key === "S") $("checksbtn").click();
    else if (e.key === "t" || e.key === "T") toggleBig();
    else if (e.key === "d" || e.key === "D") setDlog(!dlOn);
    else if (e.key === "e" || e.key === "E") openEnrol(!$("enrol").classList.contains("on"));
    else if (e.key === "Escape") openEnrol(false);
  });
  renderState();
  requestAnimationFrame(drawBoxes);
})();
</script>
</body>
</html>`;

const SLIDES = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bee Scout Slides</title>
<link rel="icon" href="data:,">
<style>
  * { box-sizing: border-box; }
  html, body { margin: 0; height: 100%; overflow: hidden; background: #000; color: #f4ead6;
    font: 18px/1.4 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
  body.idle { cursor: none; }
  #stage { position: fixed; inset: 0; }
  #stage img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; opacity: 0; transition: opacity .2s; }
  #stage img.on { opacity: 1; }
  #tag { position: fixed; left: 16px; bottom: 14px; padding: 6px 12px; border-radius: 8px; background: rgba(0,0,0,.65); color: #f6b72e;
    font-size: 15px; font-weight: 700; display: none; }
  #tag small { color: #8a7d66; font-weight: 500; margin-left: 8px; }
  body.labels #tag.has { display: block; }
  #mode { position: fixed; right: 14px; top: 12px; font-size: 13px; font-weight: 800; letter-spacing: .14em; color: #4a4336; text-transform: uppercase; }
  #post { position: fixed; right: 14px; bottom: 14px; width: 9px; height: 9px; border-radius: 50%; background: #2f3b2a; }
  #post.err { background: #e0574f; }
  #help { position: fixed; left: 50%; top: 50%; transform: translate(-50%, -50%); max-width: 680px; width: calc(100% - 48px); text-align: center;
    background: rgba(28, 22, 13, .94); border: 1px solid #3d2f1c; border-radius: 18px; padding: 28px 32px; transition: opacity .6s; }
  #help.off { opacity: 0; pointer-events: none; }
  #help h1 { margin: 0 0 10px; font-size: 28px; color: #f6b72e; }
  #help p { margin: 10px 0; color: #c9bb9f; }
  #help code { color: #ffd36b; font: 600 15px ui-monospace, Menlo, monospace; word-break: break-all; }
  #help kbd { display: inline-block; min-width: 26px; padding: 2px 7px; border-radius: 6px; background: #3a2d1a; color: #fbf3e2; font-family: inherit; font-weight: 700; font-size: 14px; }
  #help .keys { font-size: 15px; line-height: 2; }
  #help .keys span { white-space: nowrap; margin: 0 4px; }
  #help .more { font-size: 13px; color: #8a7d66; }
  #help .more kbd { font-size: 12px; min-width: 20px; }
</style>
</head>
<body class="idle">
<div id="stage"><img id="a" alt=""><img id="b" alt=""></div>
<div id="tag"></div>
<div id="mode">auto</div>
<div id="post" title="POST /scout/slide"></div>
<div id="help"><h1>Bee Scout screen test</h1><p id="hmsg">Loading slides...</p>
  <p class="keys"><span><kbd>Space</kbd> = next</span> &middot; <span><kbd>A</kbd> = auto</span> &middot; <span><kbd>F</kbd> = full screen</span> &middot; <span><kbd>L</kbd> = label</span></p>
  <p class="more">Also <kbd>B</kbd> blank now, <kbd>H</kbd> shows this again.</p></div>
<script>
(function () {
  "use strict";
  var CFG = __CONFIG__;
  var Q = new URLSearchParams(location.search);
  var token = Q.get("token") || "";
  var host = location.hostname || "localhost";
  var VISION = (Q.get("vision") || ("http://" + host + ":" + CFG.PORTS.vision)).replace(/\\/+$/, "");
  var OBJ_MS = 4000, BLANK_MS = 2000, HOLD_AFTER_MS = 1000, HOLD_CAP_MS = 25000;
  function $(id) { return document.getElementById(id); }
  var slides = [], deck = [], lastLabel = null, showing = null, auto = Q.get("auto") === "1", timer = null, front = $("a"), back = $("b"), loadedOnce = false;

  function shuffle(a) { for (var i = a.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)), t = a[i]; a[i] = a[j]; a[j] = t; } return a; }
  function nextSlide() {
    if (!deck.length) deck = shuffle(slides.slice());
    for (var i = deck.length - 1; i >= 0; i--) if (deck[i].label !== lastLabel) return deck.splice(i, 1)[0]; // a different object each time
    return deck.pop();
  }
  function post(label) {
    fetch("/scout/slide?token=" + encodeURIComponent(token), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ label: label, at: Date.now() }) })
      .then(function (r) { $("post").className = r.ok ? "" : "err"; }, function () { $("post").className = "err"; });
  }
  function setTag(s) {
    var t = $("tag");
    if (!s) { t.className = ""; t.textContent = ""; return; }
    t.className = "has"; t.textContent = CFG.NAMES[s.label] || s.label;
    if (s.credit) { var c = document.createElement("small"); c.textContent = s.credit; t.appendChild(c); }
  }
  function blank() { front.className = ""; showing = null; setTag(null); post(null); }
  function show(s) {
    var img = back;
    img.onload = function () {
      if (img !== back) return;
      back = front; front = img;
      front.className = "on"; back.className = "";
      showing = s; lastLabel = s.label; setTag(s); post(s.label); // POST when it is actually on screen
    };
    img.onerror = function () { slides = slides.filter(function (x) { return x !== s; }); deck = deck.filter(function (x) { return x !== s; }); if (slides.length) step(); else fail("Every slide image failed to load."); };
    img.src = s.url;
  }
  function step() {
    if (timer) clearTimeout(timer);
    timer = null; hold = null; showMode();
    if (!slides.length) return;
    if (showing) { blank(); schedule(BLANK_MS); }
    else { show(nextSlide()); schedule(OBJ_MS); }
  }
  function schedule(ms) { timer = auto ? setTimeout(step, ms) : null; }
  function setAuto(on) {
    auto = on; hold = null; showMode();
    if (timer) clearTimeout(timer);
    timer = null;
    if (on) schedule(showing ? OBJ_MS : BLANK_MS);
  }
  function showMode() {
    $("mode").textContent = hold ? "waiting for Beeswax" : (auto ? "auto" : "manual") + (practice ? " \u00b7 practice" : "");
  }

  // Practice mode pacing: after a "Bee fact found", hold the slide until the game is looking again (+1 s), so the
  // offer and fact finish before the next object. Capped at 25 s. Outside practice: plain 4 s / 2 s.
  var practice = false, phase = "looking", hold = null;
  function onHive(ev) {
    if (ev.type === "state" && ev.state) { practice = !!ev.state.practice; phase = ev.state.phase; showMode(); }
    else if (ev.type === "found" && practice && auto && slides.length) {
      if (timer) clearTimeout(timer);
      timer = null; phase = "offering"; hold = { cap: Date.now() + HOLD_CAP_MS, lookingAt: 0 }; showMode();
    }
  }
  setInterval(function () {
    if (!hold) return;
    var now = Date.now();
    if (phase === "looking") { if (!hold.lookingAt) hold.lookingAt = now; } else hold.lookingAt = 0;
    if ((hold.lookingAt && now - hold.lookingAt >= HOLD_AFTER_MS) || now >= hold.cap) { hold = null; showMode(); if (auto) step(); }
  }, 200);
  function hive() {
    var q = "?token=" + encodeURIComponent(token);
    fetch("/scout/state" + q, { cache: "no-store" }).then(function (r) { return r.ok ? r.json() : null; }).then(function (st) { if (st && st.phase) onHive({ type: "state", state: st }); }).catch(function () {});
    var es = new EventSource("/scout/events" + q);
    var parse = function (e) { try { onHive(JSON.parse(e.data)); } catch (err) {} };
    es.onmessage = parse;
    es.addEventListener("state", parse); es.addEventListener("found", parse);
    es.onerror = function () { if (es.readyState === 2) setTimeout(hive, 5000); };
  }
  function fail(msg) {
    var h = $("hmsg"); h.textContent = "";
    h.appendChild(document.createTextNode(msg + " Start the vision worker (hive/scout-vision) so it serves "));
    var c = document.createElement("code"); c.textContent = VISION + "/slides.json"; h.appendChild(c);
    h.appendChild(document.createTextNode(". Retrying every 5 s."));
    $("help").className = "";
  }
  function load() {
    fetch(VISION + "/slides.json", { cache: "no-store" }).then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); }).then(function (list) {
      slides = (Array.isArray(list) ? list : (list && list.slides) || []).filter(function (s) { return s && s.url && s.label; });
      if (!slides.length) { fail("The vision worker has no slides yet."); setTimeout(load, 5000); return; }
      var labels = {}; slides.forEach(function (s) { labels[s.label] = 1; });
      $("hmsg").textContent = slides.length + " slides of " + Object.keys(labels).length + " objects. " + (auto ? "Auto: " + (OBJ_MS / 1000) + " s object / " + (BLANK_MS / 1000) + " s blank." : "Manual: press Space for the first slide.");
      setTimeout(function () { $("help").className = "off"; }, 5000);
      if (!loadedOnce) { loadedOnce = true; blank(); setAuto(auto); }
    }).catch(function () { fail("Can't reach the vision worker at " + VISION + "."); setTimeout(load, 5000); });
  }
  addEventListener("keydown", function (e) {
    var k = e.key;
    if (k === " " || k === "ArrowRight" || k === "PageDown") { e.preventDefault(); step(); }
    else if (k === "a" || k === "A") setAuto(!auto);
    else if (k === "l" || k === "L") document.body.classList.toggle("labels");
    else if (k === "b" || k === "B") { if (timer) clearTimeout(timer); hold = null; showMode(); blank(); schedule(BLANK_MS); }
    else if (k === "h" || k === "H" || k === "?") $("help").classList.toggle("off");
    else if (k === "f" || k === "F") { if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen().catch(function () {}); }
  });
  var idleT = null;
  addEventListener("mousemove", function () { document.body.classList.remove("idle"); if (idleT) clearTimeout(idleT); idleT = setTimeout(function () { document.body.classList.add("idle"); }, 2000); });
  if (Q.get("labels") === "1") document.body.classList.add("labels");
  showMode();
  hive();
  load();
})();
</script>
</body>
</html>`;

export function scoutDashboardHtml(): string {
  return DASHBOARD.replace("__CONFIG__", () => CONFIG);
}

export function scoutSlidesHtml(): string {
  return SLIDES.replace("__CONFIG__", () => CONFIG);
}
