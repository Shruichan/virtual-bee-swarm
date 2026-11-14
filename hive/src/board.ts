// The Beat Board: one self-contained page for the operator's phone (iPhone Safari first).
// Served at GET /board?token=...; talks to the bus as role=board.
import { BEATS, ITEMS, PROTOCOL_VERSION, SCAN_BEATS, SOCKET_OF } from "./protocol";

const CONFIG = JSON.stringify({ BEATS, ITEMS, SCAN_BEATS, SOCKET_OF, VERSION: PROTOCOL_VERSION });

// NOTE: the inline script avoids backticks and "${" so it can live inside this template string.
const PAGE = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="theme-color" content="#14110c">
<title>Beat Board</title>
<style>
  :root {
    --bg: #14110c; --panel: #1f1a12; --line: #3a3122; --text: #f4ead7; --dim: #a8997e;
    --honey: #f2b134; --good: #5cc96b; --bad: #e0574f; --danger: #ff7a1a; --roast: #b48cff; --blue: #59a7ff;
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  html, body { margin: 0; background: var(--bg); color: var(--text);
    font: 16px/1.3 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
  body { padding: 0 12px calc(24px + env(safe-area-inset-bottom)); }
  header { position: sticky; top: 0; z-index: 5; background: var(--bg); padding: calc(8px + env(safe-area-inset-top)) 0 8px;
    border-bottom: 1px solid var(--line); display: grid; grid-template-columns: 1fr auto; gap: 4px 8px; align-items: center; }
  #beatNow { font-size: 26px; font-weight: 800; letter-spacing: .5px; text-transform: uppercase; }
  #conn { font-size: 13px; padding: 4px 10px; border-radius: 999px; background: #4a1d1a; color: #ffb4ad; white-space: nowrap; }
  #conn.on { background: #173a1f; color: #9be8a7; }
  .meta { grid-column: 1 / -1; font-size: 13px; color: var(--dim); display: flex; flex-wrap: wrap; gap: 4px 12px; }
  .meta b { color: var(--text); font-weight: 600; }
  .meta .off { color: var(--bad); }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing: 1px; color: var(--dim); margin: 18px 0 8px; }
  button { font: inherit; color: var(--text); background: var(--panel); border: 1px solid var(--line); border-radius: 14px;
    min-height: 54px; padding: 8px 10px; touch-action: manipulation; user-select: none; -webkit-user-select: none; }
  button:active { transform: scale(.97); background: #2c2519; }
  .grid { display: grid; gap: 8px; }
  .g2 { grid-template-columns: 1fr 1fr; } .g3 { grid-template-columns: repeat(3, 1fr); }
  .beat { text-align: left; font-weight: 600; position: relative; }
  .beat small { display: block; color: var(--dim); font-weight: 400; font-size: 12px; }
  .beat.scan::after { content: "SCAN"; position: absolute; top: 6px; right: 8px; font-size: 10px; color: var(--honey); }
  .beat.now { background: var(--honey); color: #1b1408; border-color: var(--honey); }
  .beat.now small, .beat.now.scan::after { color: #4a3510; }
  #next { width: 100%; min-height: 72px; margin-top: 8px; font-size: 24px; font-weight: 800; background: var(--honey); color: #1b1408; border: 0; }
  .seg button.on { background: var(--blue); color: #071526; border-color: var(--blue); font-weight: 700; }
  .item { font-size: 14px; }
  .item.good { border-color: #2f6b38; } .item.bad { border-color: #7a2f2a; } .item.dangerous { border-color: #8a4310; } .item.roast { border-color: #4f3d78; }
  .item small { display: block; color: var(--dim); font-size: 11px; }
  #hum { width: 100%; height: 44px; accent-color: var(--danger); }
  .row { display: flex; gap: 8px; align-items: center; }
  .row > * { flex: 1; }
  input[type=text], input[type=search] { font: inherit; width: 100%; min-height: 48px; padding: 8px 12px; border-radius: 12px;
    border: 1px solid var(--line); background: #0f0d09; color: var(--text); }
  #lines { max-height: 340px; overflow-y: auto; -webkit-overflow-scrolling: touch; display: grid; gap: 6px; margin-top: 8px; }
  .line { text-align: left; min-height: 48px; font-size: 14px; }
  .line .ch { font-size: 10px; font-weight: 700; padding: 2px 6px; border-radius: 6px; margin-right: 6px; vertical-align: 1px; }
  .ch.ear { background: #173a5a; color: #9fd0ff; } .ch.room { background: #4a3510; color: #ffd98a; }
  .line small { display: block; color: var(--dim); font-size: 11px; margin-top: 2px; }
  #reset { width: 100%; background: #3a1512; border-color: #7a2f2a; font-weight: 700; }
  #reset.armed { background: var(--bad); color: #fff; }
  .card { background: var(--panel); border: 1px solid var(--line); border-radius: 14px; padding: 10px 12px; }
  #last .big { font-size: 20px; font-weight: 800; }
  #last .big.good { color: var(--good); } #last .big.bad { color: var(--bad); } #last .big.dangerous { color: var(--danger); } #last .big.roast { color: var(--roast); }
  #last .sub, #plea { color: var(--dim); font-size: 14px; margin-top: 4px; }
  #plea { color: var(--text); }
  #log { font: 12px/1.35 ui-monospace, Menlo, monospace; max-height: 320px; overflow-y: auto; -webkit-overflow-scrolling: touch; }
  #log div { padding: 3px 0; border-bottom: 1px solid #2a2418; word-break: break-word; }
  #log .t { color: var(--dim); margin-right: 6px; }
  #log .err { color: var(--bad); } #log .lat { color: var(--blue); } #log .verdict { color: var(--honey); }
</style>
</head>
<body>
<header>
  <div id="beatNow">-</div>
  <div id="conn">offline</div>
  <div class="meta">
    <span>group <b id="group">-</b></span>
    <span>mode <b id="modeNow">-</b></span>
    <span>quest <b id="cQuest">?</b></span>
    <span>glasses <b id="cGlasses">?</b></span>
    <span>voice <b id="tts">?</b></span>
  </div>
</header>

<h2>Beats</h2>
<div class="grid g2" id="beats"></div>
<button id="next">NEXT</button>

<h2>Last scan</h2>
<div class="card" id="last"><div class="big" id="lastBig">-</div><div class="sub" id="lastSub">no scans yet</div></div>
<div class="card" style="margin-top:8px"><div class="sub">last plea / transcript</div><div id="plea">-</div></div>

<h2>Force verdict</h2>
<div class="grid g2" id="items"></div>

<h2>Hum thermometer <span id="humVal">0.00</span></h2>
<input type="range" id="hum" min="0" max="1" step="0.01" value="0">

<h2>Mode</h2>
<div class="grid g2 seg" id="mode">
  <button data-mode="duo">DUO</button><button data-mode="solo">SOLO</button>
</div>

<h2>Window (Orbis) <span id="orbisNow"></span></h2>
<div class="grid g3 seg" id="orbis">
  <button data-want="live">LIVE</button><button data-want="clips">CLIPS</button><button data-want="off">OFF</button>
</div>

<h2>Say a line</h2>
<input type="search" id="filter" placeholder="filter lines (id or text)" autocomplete="off">
<div id="lines"><div class="sub">loading lines...</div></div>

<h2>Type a plea (if transcription fails)</h2>
<div class="row"><input type="text" id="pleaText" placeholder="why should the bees stay?"><button id="pleaSend" style="flex:0 0 90px">SEND</button></div>

<h2>Group</h2>
<button id="reset">RESET GROUP</button>

<h2>Log</h2>
<div class="card" id="log"></div>

<script>
(function () {
  "use strict";
  var CFG = __CONFIG__;
  var qs = new URLSearchParams(location.search);
  var token = qs.get("token") || "";
  var ws = null, backoff = 500, beat = 0, mode = "", humTimer = null, humPending = null, lastWelcome = 0;
  var HELLO = JSON.stringify({ type: "hello", role: "board", client: "beat-board", version: CFG.VERSION });
  var VERDICT_OF = { red_rose: "bad", purple_bee_flower: "good", clover_or_rosemary: "good", water_dish: "good",
    bee_hotel: "good", spray_bottle: "dangerous", plastic_flower: "bad", other_bee_plant: "good", other_plant: "bad",
    non_plant_object: "roast", person: "roast", unclear: "roast" };
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function send(msg) {
    if (ws && ws.readyState === 1) { ws.send(JSON.stringify(msg)); return true; }
    addLog("not connected: " + msg.type + " not sent", "err"); return false;
  }
  function addLog(text, cls) {
    var row = el("div", cls || "");
    row.appendChild(el("span", "t", new Date().toTimeString().slice(0, 8)));
    row.appendChild(document.createTextNode(text));
    var log = $("log");
    log.insertBefore(row, log.firstChild);
    while (log.childNodes.length > 150) log.removeChild(log.lastChild);
  }

  // ---- beats
  CFG.BEATS.forEach(function (name, n) {
    var b = el("button", "beat" + (CFG.SCAN_BEATS.indexOf(name) >= 0 ? " scan" : ""));
    b.appendChild(document.createTextNode(n + "  " + name));
    b.dataset.n = n;
    b.onclick = function () { send({ type: "beat.request", n: n }); };
    $("beats").appendChild(b);
  });
  $("next").onclick = function () { send({ type: "beat.request", next: true }); };
  function showBeat(n) {
    beat = n;
    $("beatNow").textContent = n + " " + (CFG.BEATS[n] || "?");
    Array.prototype.forEach.call($("beats").children, function (b) { b.classList.toggle("now", Number(b.dataset.n) === n); });
  }

  // ---- items
  CFG.ITEMS.forEach(function (item) {
    var v = VERDICT_OF[item] || "roast";
    var b = el("button", "item " + v);
    b.appendChild(document.createTextNode(item.replace(/_/g, " ")));
    var sock = CFG.SOCKET_OF[item];
    b.appendChild(el("small", "", v + (sock ? "  socket " + sock : "")));
    b.onclick = function () { send({ type: "verdict.force", item: item }); };
    $("items").appendChild(b);
  });

  // ---- hum (throttled ~10/s, last value always sent)
  $("hum").addEventListener("input", function (e) {
    humPending = Number(e.target.value);
    $("humVal").textContent = humPending.toFixed(2);
    if (humTimer) return;
    humTimer = setInterval(function () {
      if (humPending === null) { clearInterval(humTimer); humTimer = null; return; }
      send({ type: "hum.set", value: humPending }); humPending = null;
    }, 100);
  });

  // ---- mode / orbis
  Array.prototype.forEach.call($("mode").children, function (b) {
    b.onclick = function () { send({ type: "mode", mode: b.dataset.mode }); };
  });
  function showMode(m) {
    mode = m; $("modeNow").textContent = m || "-";
    Array.prototype.forEach.call($("mode").children, function (b) { b.classList.toggle("on", b.dataset.mode === m); });
  }
  Array.prototype.forEach.call($("orbis").children, function (b) {
    b.onclick = function () { send({ type: "orbis.request", want: b.dataset.want }); addLog("orbis.request " + b.dataset.want); };
  });

  // ---- lines
  var LINES = [];
  function renderLines() {
    var f = $("filter").value.trim().toLowerCase(), box = $("lines");
    box.textContent = "";
    var shown = LINES.filter(function (l) { return !f || (l.id + " " + l.text).toLowerCase().indexOf(f) >= 0; });
    if (!shown.length) box.appendChild(el("div", "sub", LINES.length ? "no match" : "no lines loaded"));
    shown.forEach(function (l) {
      var b = el("button", "line");
      b.appendChild(el("span", "ch " + l.channel, l.channel.toUpperCase()));
      b.appendChild(document.createTextNode(l.text || l.id));
      b.appendChild(el("small", "", l.id + (l.beat ? "  ·  " + l.beat : "")));
      b.onclick = function () { if (send({ type: "say.force", line_id: l.id, channel: l.channel })) addLog("say.force " + l.id); };
      box.appendChild(b);
    });
  }
  $("filter").addEventListener("input", renderLines);
  function loadLines() {
    fetch("/lines.json?token=" + encodeURIComponent(token)).then(function (r) { return r.json(); }).then(function (data) {
      LINES = Object.keys(data || {}).map(function (k) { var l = data[k]; return { id: l.id || k, channel: l.channel === "room" ? "room" : "ear", text: l.text || "", beat: l.beat || "" }; });
      LINES.sort(function (a, b) { return (a.beat || "~").localeCompare(b.beat || "~") || a.id.localeCompare(b.id); });
      renderLines();
    }).catch(function () { $("lines").textContent = "could not load /lines.json"; });
  }

  // ---- plea / reset
  $("pleaSend").onclick = function () {
    var t = $("pleaText").value.trim();
    if (t && send({ type: "plea", text: t })) $("pleaText").value = "";
  };
  var armed = null;
  $("reset").onclick = function () {
    var b = $("reset");
    if (!armed) {
      b.classList.add("armed"); b.textContent = "TAP AGAIN TO RESET";
      armed = setTimeout(function () { armed = null; b.classList.remove("armed"); b.textContent = "RESET GROUP"; }, 3000);
      return;
    }
    clearTimeout(armed); armed = null; b.classList.remove("armed"); b.textContent = "RESET GROUP";
    send({ type: "group.reset" });
  };

  // ---- health poll (client counts)
  function poll() {
    fetch("/health").then(function (r) { return r.json(); }).then(function (h) {
      $("cQuest").textContent = h.clients.quest; $("cQuest").className = h.clients.quest ? "" : "off";
      $("cGlasses").textContent = h.clients.glasses;
      $("tts").textContent = h.tts ? "on" : "off"; $("tts").className = h.tts ? "" : "off";
      if (h.orbis) $("orbisNow").textContent = h.orbis.state + " (stage " + h.orbis.stage + ")";
    }).catch(function () {});
  }
  setInterval(poll, 4000); poll();

  // ---- bus
  function onMessage(m) {
    switch (m.type) {
      case "welcome": lastWelcome = Date.now(); $("group").textContent = m.group_id; showBeat(m.beat); showMode(m.mode); break;
      case "beat.set": showBeat(m.n); addLog("beat " + m.n + " " + m.name); break;
      case "mode": showMode(m.mode); addLog("mode " + m.mode); break;
      case "group.reset": $("group").textContent = m.group_id; addLog("NEW GROUP " + m.group_id, "verdict");
        $("lastBig").textContent = "-"; $("lastSub").textContent = "no scans yet"; $("plea").textContent = "-"; break;
      case "scan.started": addLog("scan started " + m.scan_id); $("lastBig").textContent = "scanning..."; $("lastBig").className = "big"; break;
      case "scan.verdict":
        $("lastBig").textContent = m.item.replace(/_/g, " ") + " / " + m.verdict;
        $("lastBig").className = "big " + m.verdict;
        $("lastSub").textContent = m.path + "  " + m.ms + " ms  ·  saw: " + m.seen + (m.caption ? "  ·  \\u201c" + m.caption + "\\u201d" : "");
        addLog("VERDICT " + m.item + "/" + m.verdict + " via " + m.path + " " + m.ms + " ms" + (m.line_id ? " line " + m.line_id : "") + (m.tts_id ? " live tts" : ""), "verdict");
        break;
      case "plea": $("plea").textContent = m.text; addLog("plea: " + m.text); break;
      case "say": addLog("say " + m.channel + ": " + (m.line_id || m.tts_id || "") + (m.caption ? " \\u201c" + m.caption + "\\u201d" : "")); break;
      case "latency": addLog(m.hop + " " + m.ms + " ms (" + m.source + ")", "lat"); break;
      case "log": addLog((m.source ? m.source + ": " : "") + m.text); break;
      case "error": addLog("error: " + m.message, "err"); break;
      case "orbis": $("orbisNow").textContent = m.state + " (stage " + m.stage + ")"; addLog("orbis " + m.state + (m.fallback_clip ? " clip " + m.fallback_clip : "")); break;
      case "swarm.arrived": addLog("swarm arrived at socket " + m.socket); break;
      case "hornet.cooked": addLog("hornet cooked" + (m.overshoot ? " (overshoot!)" : "")); break;
    }
  }
  function connect() {
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
    var proto = location.protocol === "https:" ? "wss://" : "ws://";
    ws = new WebSocket(proto + location.host + "/bus?role=board&token=" + encodeURIComponent(token));
    ws.onopen = function () {
      backoff = 500; lastWelcome = Date.now(); $("conn").textContent = "live"; $("conn").className = "on";
      ws.send(HELLO);
      addLog("connected");
    };
    ws.onmessage = function (e) {
      if (typeof e.data !== "string") return;
      try { onMessage(JSON.parse(e.data)); } catch (err) { addLog("bad message: " + err, "err"); }
    };
    ws.onclose = function () {
      $("conn").textContent = "offline"; $("conn").className = "";
      setTimeout(connect, backoff); backoff = Math.min(backoff * 2, 5000);
    };
    ws.onerror = function () { try { ws.close(); } catch (e) {} };
  }
  // A phone that slept can hold a dead socket that still says OPEN: hello/welcome is our heartbeat.
  function reconnectNow() {
    if (ws) { ws.onclose = ws.onmessage = ws.onerror = null; try { ws.close(); } catch (e) {} ws = null; }
    $("conn").textContent = "offline"; $("conn").className = "";
    connect();
  }
  setInterval(function () {
    if (!ws || ws.readyState !== 1) return;
    if (Date.now() - lastWelcome > 25000) { addLog("connection stale, reconnecting", "err"); reconnectNow(); return; }
    ws.send(HELLO);
  }, 10000);
  document.addEventListener("visibilitychange", function () {
    if (document.hidden) return;
    poll();
    if (Date.now() - lastWelcome > 15000) reconnectNow(); else connect();
  });
  showBeat(0);
  loadLines();
  connect();
})();
</script>
</body>
</html>`;

export function boardHtml(): string {
  return PAGE.replace("__CONFIG__", () => CONFIG);
}
