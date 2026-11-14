// Spectator monitor: the latest polaroid, what Beeswax saw, the beat, captions.
// Served at GET /monitor?token=...; talks to the bus as role=monitor (receives binary SCAN_PHOTO).
import { BEATS, BINARY_KIND, PROTOCOL_VERSION } from "./protocol";

const CONFIG = JSON.stringify({ BEATS, PHOTO: BINARY_KIND.SCAN_PHOTO, VERSION: PROTOCOL_VERSION });

// NOTE: the inline script avoids backticks and "${" so it can live inside this template string.
const PAGE = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Beeswax Monitor</title>
<style>
  :root { --bg: #120f0a; --text: #f6ecd8; --dim: #a4957a; --honey: #f2b134; --good: #5cc96b; --bad: #e0574f; --danger: #ff7a1a; --roast: #b48cff; }
  * { box-sizing: border-box; }
  html, body { margin: 0; height: 100%; background: radial-gradient(ellipse at 30% 20%, #2a2112, var(--bg) 70%); color: var(--text);
    font: 18px/1.35 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; overflow: hidden; }
  main { height: 100%; display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(0, 1fr); gap: 4vw; align-items: center; padding: 4vh 4vw; }
  @media (max-aspect-ratio: 1/1) { main { grid-template-columns: 1fr; grid-template-rows: auto 1fr; gap: 3vh; } }
  .polaroid { background: #fbf8f1; padding: 3% 3% 0; border-radius: 4px; box-shadow: 0 30px 70px rgba(0,0,0,.6); transform: rotate(-2deg);
    max-width: 78vh; width: 100%; justify-self: center; transition: transform .4s; }
  .polaroid.flash { animation: flash .5s ease-out; }
  @keyframes flash { 0% { box-shadow: 0 0 0 40px rgba(255,255,255,.9); } 100% { box-shadow: 0 30px 70px rgba(0,0,0,.6); } }
  .frame { aspect-ratio: 1 / 1; background: #1b1a17; overflow: hidden; display: grid; place-items: center; }
  .frame img { width: 100%; height: 100%; object-fit: cover; transition: filter 3.5s ease-out, opacity 1.2s; }
  .frame img.dev { filter: brightness(2.6) saturate(0) blur(8px) sepia(.4); opacity: .55; }
  .frame .blank { color: #6b675d; font-size: 2.2vh; letter-spacing: 2px; text-transform: uppercase; }
  .caption { color: #2a2418; font: 600 3vh/1.2 "Marker Felt", "Comic Sans MS", "Bradley Hand", cursive; padding: 3.5% 2% 5%; min-height: 9vh; }
  .side { display: flex; flex-direction: column; gap: 3vh; min-width: 0; }
  #beat { font-size: 2.2vh; letter-spacing: 3px; text-transform: uppercase; color: var(--honey); }
  #beat b { font-size: 5vh; display: block; letter-spacing: 1px; color: var(--text); }
  #saw { font-size: 4vh; font-weight: 800; line-height: 1.15; }
  #saw.good { color: var(--good); } #saw.bad { color: var(--bad); } #saw.dangerous { color: var(--danger); } #saw.roast { color: var(--roast); }
  #how { color: var(--dim); font-size: 2.2vh; margin-top: .8vh; }
  #captions { display: flex; flex-direction: column; gap: 1.2vh; }
  .cap { font-size: 2.8vh; background: rgba(0,0,0,.35); border-left: 4px solid var(--honey); padding: 1vh 1.4vw; border-radius: 6px; animation: in .4s ease-out; }
  .cap.ear { border-left-color: #59a7ff; } .cap.plea { border-left-color: var(--good); font-style: italic; }
  .cap small { display: block; color: var(--dim); font-size: 1.6vh; text-transform: uppercase; letter-spacing: 1px; }
  @keyframes in { from { opacity: 0; transform: translateY(8px); } }
  #conn { position: fixed; right: 12px; bottom: 10px; font-size: 13px; color: var(--bad); }
  #conn.on { color: #4d6b4f; }
</style>
</head>
<body>
<main>
  <div class="polaroid" id="polaroid">
    <div class="frame" id="frame"><div class="blank">waiting for a scan</div></div>
    <div class="caption" id="pcap">&nbsp;</div>
  </div>
  <div class="side">
    <div id="beat">beat<b>-</b></div>
    <div><div id="saw">Point the glasses at a flower.</div><div id="how"></div></div>
    <div id="captions"></div>
  </div>
</main>
<div id="conn">offline</div>
<script>
(function () {
  "use strict";
  var CFG = __CONFIG__;
  var token = new URLSearchParams(location.search).get("token") || "";
  var ws = null, backoff = 500, photoUrl = null, lastWelcome = 0;
  var HELLO = JSON.stringify({ type: "hello", role: "monitor", client: "monitor-page", version: CFG.VERSION });
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

  function caption(text, kind, label) {
    if (!text) return;
    var c = el("div", "cap " + (kind || ""));
    c.appendChild(el("small", "", label || ""));
    c.appendChild(document.createTextNode(text));
    var box = $("captions");
    box.insertBefore(c, box.firstChild);
    while (box.childNodes.length > 4) box.removeChild(box.lastChild);
  }
  function showBeat(n) { $("beat").lastChild.textContent = n + "  " + (CFG.BEATS[n] || "?"); }
  function blank(text) {
    var f = $("frame"); f.textContent = ""; f.appendChild(el("div", "blank", text));
  }
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

  function onMessage(m) {
    switch (m.type) {
      case "welcome": lastWelcome = Date.now(); showBeat(m.beat); break;
      case "beat.set": showBeat(m.n); break;
      case "group.reset":
        blank("waiting for a scan"); $("pcap").innerHTML = "&nbsp;"; $("saw").textContent = "New group. Point the glasses at a flower.";
        $("saw").className = ""; $("how").textContent = ""; $("captions").textContent = ""; break;
      case "scan.started":
        blank("developing..."); $("pcap").innerHTML = "&nbsp;";
        var p = $("polaroid"); p.classList.remove("flash"); void p.offsetWidth; p.classList.add("flash"); break;
      case "scan.verdict":
        $("saw").textContent = "Beeswax saw: " + m.seen;
        $("saw").className = m.verdict;
        $("how").textContent = m.item.replace(/_/g, " ") + "  ·  " + m.verdict + "  ·  " + m.path + ", " + m.ms + " ms";
        $("pcap").textContent = m.seen;
        caption(m.caption, "", "Beeswax");
        break;
      case "say": caption(m.caption, m.channel, m.channel === "ear" ? "in the scout's ear" : "Beeswax"); break;
      case "plea": caption(m.text, "plea", "noted for the minutes"); break;
    }
  }
  function connect() {
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
    var proto = location.protocol === "https:" ? "wss://" : "ws://";
    ws = new WebSocket(proto + location.host + "/bus?role=monitor&token=" + encodeURIComponent(token));
    ws.binaryType = "arraybuffer";
    ws.onopen = function () {
      backoff = 500; lastWelcome = Date.now(); $("conn").textContent = "live"; $("conn").className = "on";
      ws.send(HELLO);
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
