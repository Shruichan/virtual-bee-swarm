// The Beat Board: one self-contained page for the operator's phone (iPhone Safari first).
// Served at GET /board?token=...; talks to the bus as role=board.
//
// v2.1 operator ops: station buttons send choice.request (Unity applies it and reports choice.made; with no Quest the
// Hive plays the outcome itself), effect buttons fx.force, what-if buttons whatif.force (the same path as the player's
// voice), and the text box POSTs /board/custom {text}, routed exactly as if the player had said it (off-list what-ifs
// become a Claude prompt for the live window). "What fired" shows what each action set off, and by which path.
import { BEATS, CHOICES, CORRECT_CHOICE, FX, ITEMS, PROTOCOL_VERSION, SCAN_BEATS, SCENES, STATIONS, VOICE_BEATS, WHATIF_TOPICS } from "./protocol";

export interface BoardStation { intro_line: string | null; choices: Partial<Record<string, { correct: boolean; line_id: string; scene: string }>> }

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
    --honey: #f2b134; --good: #5cc96b; --bad: #e0574f; --fire: #ff7a1a; --roast: #b48cff; --blue: #59a7ff;
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  html, body { margin: 0; background: var(--bg); color: var(--text);
    font: 16px/1.3 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
  body { padding: 0 12px calc(24px + env(safe-area-inset-bottom)); max-width: 820px; margin: 0 auto; }
  header { position: sticky; top: 0; z-index: 5; background: var(--bg); padding: calc(8px + env(safe-area-inset-top)) 0 8px;
    border-bottom: 1px solid var(--line); display: grid; grid-template-columns: 1fr auto; gap: 4px 8px; align-items: center; }
  #beatNow { font-size: 26px; font-weight: 800; letter-spacing: .5px; text-transform: uppercase; }
  #conn { font-size: 13px; padding: 4px 10px; border-radius: 999px; background: #4a1d1a; color: #ffb4ad; white-space: nowrap; }
  #conn.on { background: #173a1f; color: #9be8a7; }
  .meta { grid-column: 1 / -1; font-size: 13px; color: var(--dim); display: flex; flex-wrap: wrap; gap: 4px 12px; }
  .meta b { color: var(--text); font-weight: 600; }
  .meta .off { color: var(--bad); }
  #score { grid-column: 1 / -1; display: none; font-size: 20px; font-weight: 800; color: var(--good); }
  #score.on { display: block; }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing: 1px; color: var(--dim); margin: 18px 0 8px; }
  h3 { font-size: 15px; margin: 12px 0 6px; display: flex; gap: 8px; align-items: center; }
  h3 .hex { font-size: 12px; padding: 2px 8px; border-radius: 999px; background: #2c2519; color: var(--dim); font-weight: 600; }
  h3 .hex.lit { background: var(--honey); color: #1b1408; }
  button { font: inherit; color: var(--text); background: var(--panel); border: 1px solid var(--line); border-radius: 14px;
    min-height: 54px; padding: 8px 10px; touch-action: manipulation; user-select: none; -webkit-user-select: none; }
  button:active { transform: scale(.97); background: #2c2519; }
  button small { display: block; color: var(--dim); font-weight: 400; font-size: 11px; margin-top: 2px; }
  .grid { display: grid; gap: 8px; }
  .g2 { grid-template-columns: 1fr 1fr; } .g3 { grid-template-columns: repeat(3, 1fr); } .g4 { grid-template-columns: repeat(4, 1fr); }
  @media (max-width: 380px) { .g4 { grid-template-columns: 1fr 1fr; } .g3 { grid-template-columns: 1fr 1fr; } }
  .beat { text-align: left; font-weight: 600; position: relative; }
  .beat.voice::after { content: "MIC"; position: absolute; top: 6px; right: 8px; font-size: 10px; color: var(--blue); }
  .beat.now { background: var(--honey); color: #1b1408; border-color: var(--honey); }
  .beat.now small, .beat.now.voice::after { color: #4a3510; }
  #next { width: 100%; min-height: 72px; margin-top: 8px; font-size: 24px; font-weight: 800; background: var(--honey); color: #1b1408; border: 0; }
  .seg button.on { background: var(--blue); color: #071526; border-color: var(--blue); font-weight: 700; }
  .choice { font-weight: 700; }
  .choice.good { background: #173a1f; border-color: var(--good); color: #c9f5cf; } .choice.bad { border-color: #7a2f2a; }
  .choice.good small { color: #8fd49a; }
  .choice.made { box-shadow: inset 0 0 0 3px var(--honey); }
  .fxb { font-size: 14px; min-height: 48px; }
  #custom { margin-top: 8px; }
  #customSend { flex: 0 0 84px; background: var(--roast); color: #160b29; border: 0; font-weight: 800; }
  #fired { display: grid; gap: 6px; }
  #fired .f { border-left: 3px solid var(--line); padding: 4px 8px; }
  #fired .h { font-size: 15px; font-weight: 600; }
  #fired .s { font-size: 13px; color: var(--dim); margin-top: 2px; word-break: break-word; }
  .path { display: inline-block; font-size: 10px; font-weight: 800; letter-spacing: .5px; text-transform: uppercase; padding: 2px 6px;
    border-radius: 6px; margin-right: 6px; vertical-align: 2px; background: #2c2519; color: var(--dim); }
  .path.keyword { background: #173a5a; color: #9fd0ff; } .path.claude { background: #2f1f4a; color: #d6c2ff; }
  .path.operator { background: #4a3510; color: #ffd98a; } .path.quest { background: #173a1f; color: #9be8a7; }
  #heard .u .p { font-size: 13px; color: #d6c2ff; margin-top: 2px; font-style: italic; }
  #heard .u .p.refused { color: var(--bad); font-style: normal; font-weight: 700; }
  .intro { font-size: 13px; min-height: 44px; color: var(--dim); }
  .whatif { font-weight: 800; font-size: 18px; min-height: 64px; }
  #wFire { background: #4a2008; border-color: var(--fire); color: #ffd2b0; grid-column: span 2; font-size: 22px; }
  #wOut { background: #10304a; border-color: var(--blue); color: #cfe6ff; grid-column: span 2; font-size: 22px; }
  .scene { font-size: 12px; min-height: 44px; padding: 6px; word-break: break-word; }
  .scene.now { background: var(--blue); color: #071526; border-color: var(--blue); font-weight: 700; }
  .card { background: var(--panel); border: 1px solid var(--line); border-radius: 14px; padding: 10px 12px; }
  #window .big { font-size: 20px; font-weight: 800; }
  .sub { color: var(--dim); font-size: 13px; margin-top: 4px; }
  #heard { display: grid; gap: 6px; }
  #heard .u { border-left: 3px solid var(--line); padding: 4px 8px; }
  #heard .u .t { font-size: 17px; font-weight: 600; }
  #heard .u .i { font-size: 13px; color: var(--dim); margin-top: 2px; }
  #heard .u.what_if { border-left-color: var(--fire); } #heard .u.undo { border-left-color: var(--blue); }
  #heard .u.ask { border-left-color: var(--roast); } #heard .u.ack, #heard .u.unknown { border-left-color: var(--dim); }
  #heard .u.empty .t { color: var(--dim); font-style: italic; font-weight: 400; }
  .lat { display: flex; flex-wrap: wrap; gap: 4px 14px; font-size: 13px; color: var(--dim); margin-top: 8px; }
  .lat b { color: var(--blue); }
  #hum { width: 100%; height: 44px; accent-color: var(--fire); }
  .row { display: flex; gap: 8px; align-items: center; }
  .row > * { flex: 1; }
  input[type=text], input[type=search] { font: inherit; width: 100%; min-height: 48px; padding: 8px 12px; border-radius: 12px;
    border: 1px solid var(--line); background: #0f0d09; color: var(--text); }
  #lines { max-height: 340px; overflow-y: auto; -webkit-overflow-scrolling: touch; display: grid; gap: 6px; margin-top: 8px; }
  .line { text-align: left; min-height: 48px; font-size: 14px; }
  .line .ch { font-size: 10px; font-weight: 700; padding: 2px 6px; border-radius: 6px; margin-right: 6px; vertical-align: 1px; }
  .ch.ear { background: #173a5a; color: #9fd0ff; } .ch.room { background: #4a3510; color: #ffd98a; }
  details { margin-top: 18px; } summary { color: var(--dim); font-size: 13px; text-transform: uppercase; letter-spacing: 1px; cursor: pointer; padding: 8px 0; }
  .item { font-size: 14px; }
  #reset { width: 100%; background: #3a1512; border-color: #7a2f2a; font-weight: 700; }
  #reset.armed { background: var(--bad); color: #fff; }
  #log { font: 12px/1.35 ui-monospace, Menlo, monospace; max-height: 320px; overflow-y: auto; -webkit-overflow-scrolling: touch; }
  #log div { padding: 3px 0; border-bottom: 1px solid #2a2418; word-break: break-word; }
  #log .t { color: var(--dim); margin-right: 6px; }
  #log .err { color: var(--bad); } #log .lat { color: var(--blue); } #log .hi { color: var(--honey); }
</style>
</head>
<body>
<header>
  <div id="beatNow">-</div>
  <div id="conn">offline</div>
  <div class="meta">
    <span>quest <b id="cQuest">?</b></span>
    <span>glasses <b id="cGlasses">?</b></span>
    <span>voice out <b id="tts">?</b></span>
    <span>voice in <b id="stt">?</b></span>
    <span>window <b id="winMeta">-</b></span>
    <span>grievances <b id="griev">0</b></span>
    <span>group <b id="group">-</b></span>
  </div>
  <div id="score"></div>
</header>

<h2>Beats</h2>
<div class="grid g2" id="beats"></div>
<button id="next">NEXT</button>

<h2>What if (operator twin of the voice)</h2>
<div class="grid g4" id="whatifs">
  <button class="whatif" id="wFire">FIRE</button>
  <button class="whatif" id="wOut">PUT IT OUT</button>
</div>
<div class="row" id="custom">
  <input type="text" id="customText" placeholder="type what they said: what if..." autocomplete="off" maxlength="300">
  <button id="customSend">ASK</button>
</div>
<div class="sub">Typed words go through the router like speech. An off-list what-if gets a Claude prompt (live window only).</div>

<h2>What fired</h2>
<div class="card"><div id="fired"><div class="sub">nothing fired yet</div></div></div>

<h2>Voice</h2>
<div class="card">
  <div id="heard"><div class="sub">nothing heard yet</div></div>
  <div class="lat"><span>stt <b id="lStt">-</b></span><span>route <b id="lRoute">-</b></span><span>total <b id="lTotal">-</b></span></div>
</div>

<h2>Stations <span class="sub">(sent to the Quest; with no Quest the Hive plays it)</span></h2>
<div id="stations"></div>

<h2>Effects</h2>
<div class="grid g3" id="fx"></div>

<h2>Window (Orbis)</h2>
<div class="card" id="window"><div class="big" id="winScene">-</div><div class="sub" id="winState">-</div></div>
<div class="grid g3 seg" id="orbis" style="margin-top:8px">
  <button data-want="live">LIVE</button><button data-want="clips">CLIPS</button><button data-want="off">OFF</button>
</div>
<div class="grid g3" id="scenes" style="margin-top:8px"></div>

<h2>Hornet hum <span id="humVal">0.00</span></h2>
<input type="range" id="hum" min="0" max="1" step="0.01" value="0">

<h2>Say a line</h2>
<input type="search" id="filter" placeholder="filter lines (id or text)" autocomplete="off">
<div id="lines"><div class="sub">loading lines...</div></div>

<details>
  <summary>Glasses (real-prop mode, later)</summary>
  <div class="sub">mode</div>
  <div class="grid g2 seg" id="mode"><button data-mode="duo">DUO</button><button data-mode="solo">SOLO</button></div>
  <div class="sub" style="margin-top:10px">last scan: <b id="lastScan">-</b></div>
  <div class="sub">force a scan verdict</div>
  <div class="grid g2" id="items"></div>
  <div class="sub" style="margin-top:10px">type a plea</div>
  <div class="row"><input type="text" id="pleaText" placeholder="why should the bees stay?"><button id="pleaSend" style="flex:0 0 90px">SEND</button></div>
</details>

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
  var ws = null, backoff = 500, beat = 0, humTimer = null, humPending = null, lastWelcome = 0;
  var want = null, scene = "meadow";
  var scoreCard = null, griev = 0, typedPending = null, pendingChoice = {};
  var HELLO = JSON.stringify({ type: "hello", role: "board", client: "beat-board", version: CFG.VERSION });
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function pretty(s) { return String(s || "").replace(/_/g, " "); }
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

  // ---- what fired: each action (voice, typed text, operator button, Quest choice) and what it set off, by path
  var fired = [], cur = null;
  function fire(label, path) {
    cur = { at: Date.now(), label: label, path: path, steps: [] };
    fired.unshift(cur);
    if (fired.length > 6) fired.length = 6;
    renderFired();
  }
  function step(text) {
    if (!cur || Date.now() - cur.at > 8000 || cur.steps[cur.steps.length - 1] === text) return;
    cur.steps.push(text);
    renderFired();
  }
  function renderFired() {
    var box = $("fired"); box.textContent = "";
    if (!fired.length) { box.appendChild(el("div", "sub", "nothing fired yet")); return; }
    fired.forEach(function (f) {
      var row = el("div", "f"), head = el("div", "h");
      head.appendChild(el("span", "path " + f.path, f.path));
      head.appendChild(document.createTextNode(f.label));
      row.appendChild(head);
      if (f.steps.length) row.appendChild(el("div", "s", "→ " + f.steps.join("  →  ")));
      box.appendChild(row);
    });
  }

  // ---- window: a scene request only switches the scene (v2.1); LIVE/CLIPS/OFF alone change the mode
  function sendScene(s) { send({ type: "orbis.request", scene: s }); }
  function sayRoom(id) { if (id) send({ type: "say.force", line_id: id, channel: "room" }); }

  // ---- beats
  CFG.BEATS.forEach(function (name, n) {
    var b = el("button", "beat" + (CFG.VOICE_BEATS.indexOf(name) >= 0 ? " voice" : ""));
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

  // ---- what-ifs: whatif.force runs the voice path in the Hive (line, window, embers)
  var TOPIC_LABEL = { fire: "FIRE", rain: "RAIN", flowers: "FLOWERS", spray: "SPRAY", winter: "WINTER" };
  CFG.WHATIF_TOPICS.forEach(function (t) {
    if (t === "fire" || t === "custom") return;
    var b = el("button", "whatif", TOPIC_LABEL[t] || t.toUpperCase());
    b.onclick = function () { whatIf(t); };
    $("whatifs").appendChild(b);
  });
  function whatIf(t) {
    if (send({ type: "whatif.force", topic: t })) fire("what-if " + t, "operator");
  }
  // undo: the Hive picks the line from the window (room_whatif_out on fire, room_whatif_back, room_whatif_nothing)
  function undo() {
    if (send({ type: "whatif.force", undo: true })) fire("put it out / undo (window " + pretty(scene) + ")", "operator");
  }
  $("wFire").onclick = function () { whatIf("fire"); };
  $("wOut").onclick = undo;

  // ---- typed what-if: POST /board/custom, routed as if the player said it
  function sendCustom() {
    var t = $("customText").value.trim(), b = $("customSend");
    if (!t || b.disabled) return;
    b.disabled = true; b.textContent = "...";
    typedPending = t;
    fire("typed “" + t + "”", "operator");
    fetch("/board/custom?token=" + encodeURIComponent(token), {
      method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ text: t })
    }).then(function (r) { return r.json(); }).then(function (j) {
      if (!j || !j.ok) { addLog("typed what-if failed: " + ((j && j.error) || "no answer"), "err"); return; }
      $("customText").value = "";
      var info = "typed → " + j.intent + (j.topic ? "/" + j.topic : "") + " via " + j.path + " " + j.ms + " ms";
      if (j.topic === "custom") info += j.custom_prompt ? " prompt: " + j.custom_prompt : " REFUSED" + (j.blocked ? " (" + j.blocked + ")" : "");
      addLog(info, "hi");
    }).catch(function (e) { addLog("typed what-if failed: " + e, "err"); }).then(function () {
      b.disabled = false; b.textContent = "ASK";
    });
  }
  $("customSend").onclick = sendCustom;
  $("customText").addEventListener("keydown", function (e) { if (e.key === "Enter") sendCustom(); });

  // ---- stations: choice.request (Unity applies it; no Quest = the Hive plays the outcome)
  CFG.STATIONS.forEach(function (st) {
    var cfg = CFG.TABLE[st] || { intro_line: null, choices: {} };
    var h = el("h3", "", pretty(st));
    var hex = el("span", "hex", "not yet"); hex.id = "hex_" + st; h.appendChild(hex);
    $("stations").appendChild(h);
    var grid = el("div", "grid g3");
    if (cfg.intro_line) {
      var intro = el("button", "intro", "intro line");
      intro.onclick = function () { sayRoom(cfg.intro_line); };
      grid.appendChild(intro);
    }
    CFG.CHOICES[st].forEach(function (ch) {
      var o = cfg.choices[ch];
      var good = CFG.CORRECT[st] === ch;
      var b = el("button", "choice " + (good ? "good" : "bad"));
      b.id = "ch_" + st + "_" + ch;
      b.appendChild(document.createTextNode((good ? "✓ " : "") + pretty(ch)));
      b.appendChild(el("small", "", o ? pretty(o.scene) : "no outcome"));
      b.onclick = function () {
        if (!send({ type: "choice.request", station: st, choice: ch })) return;
        pendingChoice[st + "/" + ch] = Date.now();
        fire("choice " + pretty(st) + ": " + pretty(ch) + (good ? " ✓" : " ✗"), "operator");
      };
      grid.appendChild(b);
    });
    $("stations").appendChild(grid);
  });
  function showChoice(m) {
    var b = $("ch_" + m.station + "_" + m.choice); if (b) b.classList.add("made");
    if (m.correct) { var hx = $("hex_" + m.station); hx.textContent = "LIT"; hx.className = "hex lit"; }
    else showGriev(griev + 1);
    var key = m.station + "/" + m.choice, asked = pendingChoice[key];
    delete pendingChoice[key];
    var label = "choice " + pretty(m.station) + ": " + pretty(m.choice) + (m.correct ? " ✓" : " ✗ grievance");
    if (asked && Date.now() - asked < 10000) step("applied" + (m.correct ? " ✓" : " ✗ grievance"));
    else fire(label, "quest");
  }
  function clearChoices() {
    pendingChoice = {};
    Array.prototype.forEach.call(document.querySelectorAll(".choice.made"), function (b) { b.classList.remove("made"); });
    CFG.STATIONS.forEach(function (st) { var hx = $("hex_" + st); hx.textContent = "not yet"; hx.className = "hex"; });
  }

  // ---- effects: fx.force
  CFG.FX.forEach(function (name) {
    var b = el("button", "fxb", pretty(name));
    b.id = "fx_" + name;
    b.onclick = function () { if (send({ type: "fx.force", name: name })) fire("fx " + pretty(name), "operator"); };
    $("fx").appendChild(b);
  });

  // ---- window
  CFG.SCENES.forEach(function (s) {
    var b = el("button", "scene", pretty(s));
    b.dataset.scene = s;
    b.onclick = function () { sendScene(s); };
    $("scenes").appendChild(b);
  });
  Array.prototype.forEach.call($("orbis").children, function (b) {
    b.onclick = function () { want = b.dataset.want; send({ type: "orbis.request", want: b.dataset.want }); showWant(); addLog("orbis.request " + b.dataset.want); };
  });
  function showWant() {
    Array.prototype.forEach.call($("orbis").children, function (b) { b.classList.toggle("on", b.dataset.want === want); });
  }
  function showWindow(st, sc) {
    if (sc) {
      scene = sc;
      $("winScene").textContent = pretty(sc);
      Array.prototype.forEach.call($("scenes").children, function (b) { b.classList.toggle("now", b.dataset.scene === sc); });
    }
    if (st) $("winState").textContent = "stream " + st + (want ? "  ·  mode " + want : "");
    $("winMeta").textContent = pretty(scene) + (st ? " (" + st + ")" : "");
  }

  // ---- voice (and typed text): transcript, intent, and a custom what-if's prompt
  var heard = [];
  function renderHeard() {
    var box = $("heard"); box.textContent = "";
    if (!heard.length) { box.appendChild(el("div", "sub", "nothing heard yet")); return; }
    heard.forEach(function (u) {
      var row = el("div", "u " + (u.intent ? u.intent.name : "") + (u.text ? "" : " empty"));
      row.appendChild(el("div", "t", u.text ? "“" + u.text + "”" : "(heard nothing)"));
      var info = u.stt === "board" ? "typed on the board" : u.source + "  ·  " + u.stt + " " + u.ms + " ms";
      if (u.intent) info += "  →  " + u.intent.name + (u.intent.topic ? "/" + u.intent.topic : "") + " via " + u.intent.path + " " + u.intent.ms + " ms";
      row.appendChild(el("div", "i", info));
      if (u.intent && u.intent.topic === "custom") {
        row.appendChild(u.intent.custom_prompt ? el("div", "p", "prompt: " + u.intent.custom_prompt) : el("div", "p refused", "refused: Grievance filed. Not that one."));
      }
      box.appendChild(row);
    });
  }
  function onTranscript(m) {
    heard.unshift({ text: m.text, source: m.source, stt: m.stt, ms: m.ms, intent: null });
    if (heard.length > 8) heard.length = 8;
    renderHeard();
  }
  function intentLabel(m) {
    var s = m.name + (m.topic ? "/" + m.topic : "");
    if (m.topic === "custom") s += m.custom_prompt ? " (prompt ready)" : " (refused)";
    return s;
  }
  function onIntent(m) {
    if (typedPending !== null && m.text === typedPending && cur && cur.path === "operator") {
      typedPending = null;
      cur.label += "  →  " + intentLabel(m) + " via " + m.path; renderFired();
    } else {
      fire("“" + m.text + "” → " + intentLabel(m), m.path);
    }
    for (var i = 0; i < heard.length; i++) if (!heard[i].intent && heard[i].text === m.text) { heard[i].intent = m; renderHeard(); return; }
    heard.unshift({ text: m.text, source: "?", stt: "-", ms: 0, intent: m }); renderHeard();
  }

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

  // ---- glasses section
  Array.prototype.forEach.call($("mode").children, function (b) {
    b.onclick = function () { send({ type: "mode", mode: b.dataset.mode }); };
  });
  function showMode(m) { Array.prototype.forEach.call($("mode").children, function (b) { b.classList.toggle("on", b.dataset.mode === m); }); }
  CFG.ITEMS.forEach(function (item) {
    var b = el("button", "item", pretty(item));
    b.onclick = function () { send({ type: "verdict.force", item: item }); };
    $("items").appendChild(b);
  });
  $("pleaSend").onclick = function () {
    var t = $("pleaText").value.trim();
    if (t && send({ type: "plea", text: t })) $("pleaText").value = "";
  };

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
      var order = {}; CFG.BEATS.forEach(function (b, i) { order[b] = i; });
      LINES = Object.keys(data || {}).map(function (k) { var l = data[k]; return { id: l.id || k, channel: l.channel === "room" ? "room" : "ear", text: l.text || "", beat: l.beat || "" }; });
      LINES.sort(function (a, b) { return ((a.beat in order ? order[a.beat] : 99) - (b.beat in order ? order[b.beat] : 99)) || a.id.localeCompare(b.id); });
      renderLines();
    }).catch(function () { $("lines").textContent = "could not load /lines.json"; });
  }

  // ---- reset (two taps)
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

  // ---- score card: {elapsed_s, text, grievances} from the score message or /health (a bare string from older logs)
  function showScore(card) {
    var s = $("score");
    if (!card) { s.textContent = ""; s.className = ""; return; }
    var text = typeof card === "string" ? card : card.text + (typeof card.grievances === "number" ? "  ·  Grievances filed: " + card.grievances : "");
    s.textContent = text; s.className = "on";
  }
  function showGriev(n) { griev = n; $("griev").textContent = String(n); }

  // ---- health poll
  function poll() {
    fetch("/health").then(function (r) { return r.json(); }).then(function (h) {
      $("cQuest").textContent = h.clients.quest; $("cQuest").className = h.clients.quest ? "" : "off";
      $("cGlasses").textContent = h.clients.glasses;
      $("tts").textContent = h.tts ? "on" : "off"; $("tts").className = h.tts ? "" : "off";
      var stt = h.stt ? (h.stt.provider || "?") + (h.stt.elevenlabs || h.stt.whisper ? "" : " (down)") : "off";
      $("stt").textContent = stt; $("stt").className = h.stt && (h.stt.elevenlabs || h.stt.whisper) ? "" : "off";
      if (h.orbis) { want = h.orbis.want || want; showWant(); showWindow(h.orbis.state, h.orbis.scene); }
      if (h.score && typeof h.score === "object") { scoreCard = h.score; showScore(h.score); }
      else if (h.score && !scoreCard) showScore(h.score);
      if (typeof h.grievances === "number") showGriev(h.grievances);
      (h.solved || []).forEach(function (st) { var hx = $("hex_" + st); if (hx) { hx.textContent = "LIT"; hx.className = "hex lit"; } });
    }).catch(function () {});
  }
  setInterval(poll, 4000); poll();

  // ---- bus
  function onMessage(m) {
    switch (m.type) {
      case "welcome": lastWelcome = Date.now(); $("group").textContent = m.group_id; showBeat(m.beat); showMode(m.mode); break;
      case "beat.set": showBeat(m.n); addLog("beat " + m.n + " " + m.name); break;
      case "mode": showMode(m.mode); addLog("mode " + m.mode); break;
      case "group.reset": $("group").textContent = m.group_id; addLog("NEW GROUP " + m.group_id, "hi");
        heard = []; renderHeard(); clearChoices(); scoreCard = null; showScore(null); showGriev(0); $("lastScan").textContent = "-";
        fired = []; cur = null; renderFired(); break;
      case "choice.made": showChoice(m); addLog("CHOICE " + m.station + ": " + m.choice + (m.correct ? " (correct)" : " (wrong)"), "hi"); break;
      case "transcript": onTranscript(m); break;
      case "intent":
        onIntent(m);
        addLog("intent " + intentLabel(m) + " “" + m.text + "”" + (m.custom_prompt ? " prompt: " + m.custom_prompt : ""), "hi"); break;
      case "fx": step("fx " + m.name + (m.delay_ms ? " +" + m.delay_ms + " ms" : "")); addLog("fx " + m.name + (m.delay_ms ? " in " + m.delay_ms + " ms" : "")); break;
      case "orbis":
        if (m.scene && m.scene !== scene) step("window " + pretty(m.scene));
        showWindow(m.state, m.scene); addLog("window " + (m.scene || "-") + " (" + m.state + ")" + (m.fallback_clip ? " clip " + m.fallback_clip : "")); break;
      case "score":
        scoreCard = m; showScore(m); showGriev(m.grievances); step(m.text);
        addLog("SCORE " + m.text + " (" + m.elapsed_s + " s), grievances filed: " + m.grievances, "hi"); break;
      case "scan.started": addLog("scan started " + m.scan_id); $("lastScan").textContent = "scanning..."; break;
      case "scan.verdict":
        $("lastScan").textContent = pretty(m.item) + " / " + m.verdict + " (" + m.path + ", " + m.ms + " ms)";
        addLog("VERDICT " + m.item + "/" + m.verdict + " via " + m.path + " " + m.ms + " ms", "hi");
        break;
      case "plea": addLog("plea: " + m.text); break;
      case "say":
        step("says " + (m.line_id || (m.tts_id ? "live voice" : "caption")));
        addLog("say " + m.channel + ": " + (m.line_id || m.tts_id || "") + (m.caption ? " “" + m.caption + "”" : "")); break;
      case "latency":
        if (m.hop === "voice.stt") $("lStt").textContent = m.ms + " ms";
        else if (m.hop === "voice.route" || m.hop === "board.route") $("lRoute").textContent = m.ms + " ms";
        else if (m.hop === "voice.total" || m.hop === "utterance.total" || m.hop === "board.total") $("lTotal").textContent = m.ms + " ms";
        addLog(m.hop + " " + m.ms + " ms (" + m.source + ")", "lat"); break;
      case "log":
        if (/^score: /.test(m.text) && !scoreCard) showScore(m.text.slice(7));
        addLog((m.source ? m.source + ": " : "") + m.text, /^score: |^operator /.test(m.text) ? "hi" : ""); break;
      case "error": addLog("error: " + m.message, "err"); break;
      case "swarm.arrived": addLog("swarm arrived at socket " + m.socket); break;
      case "hornet.cooked": addLog("hornet cooked" + (m.overshoot ? " (overshoot!)" : ""), "hi"); break;
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
  showWindow(null, "meadow");
  renderFired();
  loadLines();
  connect();
})();
</script>
</body>
</html>`;

let cached: { key: unknown; html: string } | null = null;

/** The page, with the protocol enums and the station table baked in. */
export function boardHtml(stations: Partial<Record<string, BoardStation>> = {}): string {
  if (cached?.key === stations) return cached.html;
  const config = JSON.stringify({
    BEATS,
    VOICE_BEATS,
    SCAN_BEATS,
    STATIONS,
    CHOICES,
    CORRECT: CORRECT_CHOICE,
    SCENES,
    WHATIF_TOPICS,
    FX,
    ITEMS,
    TABLE: stations,
    VERSION: PROTOCOL_VERSION,
  }).replace(/</g, "\\u003c");
  const html = PAGE.replace("__CONFIG__", () => config);
  cached = { key: stations, html };
  return html;
}
