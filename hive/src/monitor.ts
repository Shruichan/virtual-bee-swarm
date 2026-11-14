// Spectator monitor: the beat, the four station hexagons, the future window's scene, what the player said and
// what Beeswax took it to mean, Beeswax's captions, and the score. The polaroid shows up only for glasses scans.
// Served at GET /monitor?token=...; talks to the bus as role=monitor (receives binary SCAN_PHOTO).
import { BEATS, BINARY_KIND, PROTOCOL_VERSION, STATIONS } from "./protocol";

const CONFIG = JSON.stringify({ BEATS, STATIONS, PHOTO: BINARY_KIND.SCAN_PHOTO, VERSION: PROTOCOL_VERSION });

// NOTE: the inline script avoids backticks and "${" so it can live inside this template string.
const PAGE = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Beeswax Monitor</title>
<style>
  :root { --bg: #120f0a; --text: #f6ecd8; --dim: #a4957a; --honey: #f2b134; --good: #5cc96b; --bad: #e0574f; --fire: #ff7a1a; --blue: #59a7ff; --ask: #b48cff; }
  * { box-sizing: border-box; }
  html, body { margin: 0; min-height: 100%; background: radial-gradient(ellipse at 30% 20%, #2a2112, var(--bg) 70%) fixed; color: var(--text);
    font: 18px/1.35 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
  main { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 3vh 4vw; padding: 4vh 4vw; }
  @media (max-aspect-ratio: 1/1) { main { grid-template-columns: 1fr; } }
  .col { display: flex; flex-direction: column; gap: 3vh; min-width: 0; }
  .label { font-size: 1.8vh; letter-spacing: 3px; text-transform: uppercase; color: var(--honey); }
  #beat b { font-size: 5vh; display: block; letter-spacing: 1px; color: var(--text); text-transform: uppercase; }
  #score { font-size: 5.5vh; font-weight: 800; color: var(--good); min-height: 1em; }
  .hexes { display: flex; gap: 1.6vw; flex-wrap: wrap; }
  .hex { width: 11vh; height: 12.6vh; clip-path: polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%); background: #2c2519;
    display: grid; place-items: center; text-align: center; font-size: 1.7vh; font-weight: 700; color: var(--dim); text-transform: uppercase;
    transition: background .5s, color .5s; padding: 0 1vh; }
  .hex.lit { background: var(--honey); color: #1b1408; box-shadow: 0 0 40px var(--honey); }
  .hex .w { display: block; color: var(--bad); font-size: 1.5vh; }
  .card { background: rgba(0,0,0,.35); border-radius: 10px; padding: 1.6vh 1.6vw; }
  #scene { font-size: 4vh; font-weight: 800; text-transform: capitalize; }
  #scene.fire { color: var(--fire); }
  #stream { color: var(--dim); font-size: 2vh; margin-top: .6vh; }
  #said { font-size: 3.6vh; font-weight: 700; line-height: 1.2; min-height: 1.2em; }
  #said.empty { color: var(--dim); font-weight: 400; font-style: italic; }
  #meant { display: inline-block; margin-top: 1vh; font-size: 2vh; font-weight: 800; letter-spacing: 1px; text-transform: uppercase;
    padding: .5vh 1vw; border-radius: 999px; background: #2c2519; color: var(--dim); }
  #meant.what_if { background: var(--fire); color: #1b0d03; } #meant.undo { background: var(--blue); color: #071526; }
  #meant.ask { background: var(--ask); color: #160b29; }
  #captions { display: flex; flex-direction: column; gap: 1.2vh; }
  .cap { font-size: 2.8vh; background: rgba(0,0,0,.35); border-left: 4px solid var(--honey); padding: 1vh 1.4vw; border-radius: 6px; animation: in .4s ease-out; }
  .cap.ear { border-left-color: var(--blue); } .cap.plea { border-left-color: var(--good); font-style: italic; }
  .cap small { display: block; color: var(--dim); font-size: 1.6vh; text-transform: uppercase; letter-spacing: 1px; }
  @keyframes in { from { opacity: 0; transform: translateY(8px); } }
  #fx { position: fixed; left: 50%; top: 2vh; transform: translateX(-50%); font-size: 2.4vh; font-weight: 800; letter-spacing: 2px;
    padding: 1vh 2vw; border-radius: 999px; background: var(--fire); color: #1b0d03; opacity: 0; transition: opacity .4s; pointer-events: none; }
  #fx.on { opacity: 1; }
  .polaroid { display: none; background: #fbf8f1; padding: 3% 3% 0; border-radius: 4px; box-shadow: 0 30px 70px rgba(0,0,0,.6); transform: rotate(-2deg);
    max-width: 44vh; width: 100%; }
  .polaroid.on { display: block; }
  .polaroid.flash { animation: flash .5s ease-out; }
  @keyframes flash { 0% { box-shadow: 0 0 0 40px rgba(255,255,255,.9); } 100% { box-shadow: 0 30px 70px rgba(0,0,0,.6); } }
  .frame { aspect-ratio: 1 / 1; background: #1b1a17; overflow: hidden; display: grid; place-items: center; }
  .frame img { width: 100%; height: 100%; object-fit: cover; transition: filter 3.5s ease-out, opacity 1.2s; }
  .frame img.dev { filter: brightness(2.6) saturate(0) blur(8px) sepia(.4); opacity: .55; }
  .frame .blank { color: #6b675d; font-size: 2vh; letter-spacing: 2px; text-transform: uppercase; }
  .pcap { color: #2a2418; font: 600 2.4vh/1.2 "Marker Felt", "Comic Sans MS", "Bradley Hand", cursive; padding: 3.5% 2% 5%; min-height: 6vh; }
  #conn { position: fixed; right: 12px; bottom: 10px; font-size: 13px; color: var(--bad); }
  #conn.on { color: #4d6b4f; }
</style>
</head>
<body>
<main>
  <div class="col">
    <div id="beat" class="label">beat<b>-</b></div>
    <div id="score"></div>
    <div><div class="label">the hive</div><div class="hexes" id="hexes"></div></div>
    <div class="card"><div class="label">the future window</div><div id="scene">meadow</div><div id="stream">-</div></div>
    <div class="polaroid" id="polaroid">
      <div class="frame" id="frame"><div class="blank">developing...</div></div>
      <div class="pcap" id="pcap">&nbsp;</div>
    </div>
  </div>
  <div class="col">
    <div class="card"><div class="label">you said</div><div id="said" class="empty">Ask the window: "what if...?"</div><div id="meant">listening</div></div>
    <div><div class="label">Beeswax</div><div id="captions"></div></div>
  </div>
</main>
<div id="fx"></div>
<div id="conn">offline</div>
<script>
(function () {
  "use strict";
  var CFG = __CONFIG__;
  var token = new URLSearchParams(location.search).get("token") || "";
  var ws = null, backoff = 500, photoUrl = null, lastWelcome = 0, fxTimer = null;
  var wrong = {};
  var HELLO = JSON.stringify({ type: "hello", role: "monitor", client: "monitor-page", version: CFG.VERSION });
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function pretty(s) { return String(s || "").replace(/_/g, " "); }

  CFG.STATIONS.forEach(function (st) { var h = el("div", "hex", pretty(st)); h.id = "hex_" + st; $("hexes").appendChild(h); });
  function lightHex(st) { var h = $("hex_" + st); if (h) h.classList.add("lit"); }
  function markWrong(st) {
    wrong[st] = (wrong[st] || 0) + 1;
    var h = $("hex_" + st); if (!h) return;
    h.textContent = pretty(st);
    h.appendChild(el("span", "w", wrong[st] + " grievance" + (wrong[st] > 1 ? "s" : "")));
  }
  function clearHexes() {
    wrong = {};
    CFG.STATIONS.forEach(function (st) { var h = $("hex_" + st); h.className = "hex"; h.textContent = pretty(st); });
  }

  function caption(text, kind, label) {
    if (!text) return;
    var c = el("div", "cap " + (kind || ""));
    c.appendChild(el("small", "", label || ""));
    c.appendChild(document.createTextNode(text));
    var box = $("captions");
    box.insertBefore(c, box.firstChild);
    while (box.childNodes.length > 4) box.removeChild(box.lastChild);
  }
  function showBeat(n) { $("beat").lastChild.textContent = (CFG.BEATS[n] || "?"); }
  function showScore(t) { $("score").textContent = t || ""; }
  function showScene(scene, state) {
    if (scene) { $("scene").textContent = pretty(scene).replace(/^whatif /, "what if: "); $("scene").className = /fire/.test(scene) && scene !== "fire_out" ? "fire" : ""; }
    if (state) $("stream").textContent = state === "ready" ? "live Orbis stream" : state === "priming" ? "stream starting..." : "recorded clip";
  }
  var MEANT = { what_if: "what if", undo: "put it back", ask: "question", ack: "noted for the minutes", unknown: "noted for the minutes" };
  function showSaid(text) { $("said").textContent = text ? "\\u201c" + text + "\\u201d" : "(couldn't hear that)"; $("said").className = text ? "" : "empty"; $("meant").textContent = text ? "thinking..." : "try again"; $("meant").className = ""; }
  function showMeant(m) { $("meant").textContent = (MEANT[m.name] || m.name) + (m.topic ? ": " + m.topic : ""); $("meant").className = m.name; }
  function flashFx(text) {
    var f = $("fx"); f.textContent = text; f.className = "on";
    if (fxTimer) clearTimeout(fxTimer);
    fxTimer = setTimeout(function () { f.className = ""; }, 3500);
  }

  function blank(text) { var f = $("frame"); f.textContent = ""; f.appendChild(el("div", "blank", text)); }
  function showPhoto(bytes) {
    var url = URL.createObjectURL(new Blob([bytes], { type: "image/jpeg" }));
    var img = new Image();
    img.className = "dev";
    img.onload = function () {
      var f = $("frame"); f.textContent = ""; f.appendChild(img);
      requestAnimationFrame(function () { requestAnimationFrame(function () { img.className = ""; }); });
      if (photoUrl) URL.revokeObjectURL(photoUrl);
      photoUrl = url;
    };
    img.src = url;
  }

  function sync() {
    fetch("/health").then(function (r) { return r.json(); }).then(function (h) {
      showBeat(h.beat);
      if (h.orbis) showScene(h.orbis.scene, h.orbis.state);
      showScore(h.score || "");
      (h.solved || []).forEach(lightHex);
    }).catch(function () {});
  }

  function onMessage(m) {
    switch (m.type) {
      case "welcome": lastWelcome = Date.now(); showBeat(m.beat); break;
      case "beat.set": showBeat(m.n); break;
      case "group.reset":
        clearHexes(); showScore(""); showScene("meadow", null); $("captions").textContent = "";
        $("said").textContent = "Ask the window: \\u201cwhat if...?\\u201d"; $("said").className = "empty"; $("meant").textContent = "listening"; $("meant").className = "";
        $("polaroid").className = "polaroid"; break;
      case "choice.made": if (m.correct) lightHex(m.station); else markWrong(m.station); break;
      case "transcript": showSaid(m.text); break;
      case "intent": showMeant(m); break;
      case "orbis": showScene(m.scene, m.state); break;
      case "fx": if (m.name === "embers") flashFx("EMBERS INCOMING"); break;
      case "log": if (/^score: /.test(m.text)) showScore(m.text.slice(7)); break;
      case "say": caption(m.caption, m.channel, m.channel === "ear" ? "in the scout's ear" : "Beeswax"); break;
      case "plea": caption(m.text, "plea", "noted for the minutes"); break;
      case "scan.started":
        blank("developing..."); $("pcap").innerHTML = "&nbsp;";
        var p = $("polaroid"); p.className = "polaroid on"; void p.offsetWidth; p.className = "polaroid on flash"; break;
      case "scan.verdict": $("pcap").textContent = m.seen; caption(m.caption, "", "Beeswax"); break;
    }
  }
  function connect() {
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
    var proto = location.protocol === "https:" ? "wss://" : "ws://";
    ws = new WebSocket(proto + location.host + "/bus?role=monitor&token=" + encodeURIComponent(token));
    ws.binaryType = "arraybuffer";
    ws.onopen = function () {
      backoff = 500; lastWelcome = Date.now(); $("conn").textContent = "live"; $("conn").className = "on";
      ws.send(HELLO); sync();
    };
    ws.onmessage = function (e) {
      if (typeof e.data === "string") { try { onMessage(JSON.parse(e.data)); } catch (err) {} return; }
      var bytes = new Uint8Array(e.data);
      if (bytes.length > 1 && bytes[0] === CFG.PHOTO) showPhoto(bytes.subarray(1));
    };
    ws.onclose = function () {
      $("conn").textContent = "offline"; $("conn").className = "";
      setTimeout(connect, backoff); backoff = Math.min(backoff * 2, 5000);
    };
    ws.onerror = function () { try { ws.close(); } catch (e) {} };
  }
  function reconnectNow() {
    if (ws) { ws.onclose = ws.onmessage = ws.onerror = null; try { ws.close(); } catch (e) {} ws = null; }
    connect();
  }
  setInterval(function () { // hello/welcome doubles as a heartbeat
    if (!ws || ws.readyState !== 1) return;
    if (Date.now() - lastWelcome > 25000) { reconnectNow(); return; }
    ws.send(HELLO);
  }, 10000);
  document.addEventListener("visibilitychange", function () { if (!document.hidden) connect(); });
  connect();
})();
</script>
</body>
</html>`;

export function monitorHtml(): string {
  return PAGE.replace("__CONFIG__", () => CONFIG);
}
