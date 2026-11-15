// v2.1 Beat Board and monitor: run the inline page scripts against a tiny fake DOM + fake WebSocket (same approach as
// pages.test.ts; the harness is copied because importing a test file would re-run its tests), press the new buttons and
// check what they send, and feed v2.1 bus messages (score, custom what-ifs) and check what they show.
import { describe, expect, test } from "bun:test";
import { boardHtml } from "../src/board";
import { LINES, STATION_TABLE } from "../src/lines";
import { monitorHtml } from "../src/monitor";
import { CHOICES, CORRECT_CHOICE, FX, STATIONS } from "../src/protocol";

class El {
  id = "";
  className = "";
  innerHTML = "";
  value = "";
  disabled = false;
  dataset: Record<string, string> = {};
  style: Record<string, string> = {};
  children: El[] = [];
  onclick: (() => void) | null = null;
  listeners: Record<string, Array<(e: unknown) => void>> = {};
  private text = "";
  constructor(readonly tag: string) {}
  get textContent(): string {
    return this.text + this.children.map((c) => c.textContent).join("");
  }
  set textContent(v: string) {
    this.text = String(v);
    this.children = [];
  }
  get childNodes() {
    return this.children;
  }
  get firstChild() {
    return this.children[0] ?? null;
  }
  get lastChild() {
    return this.children[this.children.length - 1] ?? null;
  }
  get offsetWidth() {
    return 0;
  }
  appendChild(c: El) {
    this.children.push(c);
    return c;
  }
  insertBefore(c: El, ref: El | null) {
    const i = ref ? this.children.indexOf(ref) : -1;
    if (i < 0) this.children.push(c);
    else this.children.splice(i, 0, c);
    return c;
  }
  removeChild(c: El) {
    this.children = this.children.filter((x) => x !== c);
    return c;
  }
  readonly classList = {
    add: (...cs: string[]) => cs.forEach((c) => !this.has(c) && (this.className = `${this.className} ${c}`.trim())),
    remove: (...cs: string[]) => (this.className = this.className.split(/\s+/).filter((x) => x && !cs.includes(x)).join(" ")),
    toggle: (c: string, force?: boolean) => ((force ?? !this.has(c)) ? this.classList.add(c) : this.classList.remove(c)),
    contains: (c: string) => this.has(c),
  };
  private has(c: string) {
    return this.className.split(/\s+/).includes(c);
  }
  addEventListener(type: string, fn: (e: unknown) => void) {
    (this.listeners[type] ??= []).push(fn);
  }
  all(): El[] {
    return [this, ...this.children.flatMap((c) => c.all())];
  }
}

const VOID = new Set(["input", "meta", "br", "img", "link"]);

function parseBody(html: string): El {
  const body = html.slice(html.indexOf("<body>") + 6, html.indexOf("<script>"));
  const root = new El("body");
  const stack: El[] = [root];
  const rx = /<(\/?)([a-zA-Z0-9]+)([^>]*)>|([^<]+)/g;
  for (let m; (m = rx.exec(body)); ) {
    const top = stack[stack.length - 1]!;
    if (m[4] !== undefined) {
      if (m[4].trim()) {
        const t = new El("#text");
        t.textContent = m[4].replace(/&nbsp;/g, " ");
        top.appendChild(t);
      }
      continue;
    }
    if (m[1]) {
      stack.pop();
      continue;
    }
    const el = new El(m[2]!.toLowerCase());
    for (const a of m[3]!.matchAll(/([a-zA-Z-]+)="([^"]*)"/g)) {
      const [, k, v] = a as unknown as [string, string, string];
      if (k === "id") el.id = v;
      else if (k === "class") el.className = v;
      else if (k === "value") el.value = v;
      else if (k.startsWith("data-")) el.dataset[k.slice(5)] = v;
    }
    top.appendChild(el);
    if (!VOID.has(el.tag)) stack.push(el);
  }
  return root;
}

class FakeWS {
  static last: FakeWS | null = null;
  readyState = 1;
  sent: any[] = [];
  binaryType = "";
  onopen: (() => void) | null = null;
  onmessage: ((e: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(readonly url: string) {
    FakeWS.last = this;
  }
  send(s: string) {
    this.sent.push(JSON.parse(s));
  }
  close() {}
  deliver(msg: object) {
    this.onmessage!({ data: JSON.stringify(msg) });
  }
}

interface FetchCall { url: string; init?: { method?: string; headers?: Record<string, string>; body?: string } }
interface Page { root: El; ws: FakeWS; byId(id: string): El; fetches: FetchCall[]; settle(): Promise<void> }

function runPage(html: string, fetchJson: (url: string, init?: FetchCall["init"]) => unknown): Page {
  const root = parseBody(html);
  const byId = (id: string) => {
    const el = root.all().find((e) => e.id === id);
    if (!el) throw new Error(`no #${id}`);
    return el;
  };
  const document = {
    getElementById: (id: string) => root.all().find((e) => e.id === id) ?? null,
    createElement: (tag: string) => new El(tag),
    createTextNode: (t: string) => {
      const n = new El("#text");
      n.textContent = t;
      return n;
    },
    querySelectorAll: (sel: string) => {
      const classes = sel.split(".").filter(Boolean);
      return root.all().filter((e) => classes.every((c) => e.classList.contains(c)));
    },
    addEventListener: () => {},
    hidden: false,
  };
  const fetches: FetchCall[] = [];
  const fetchStub = (url: string, init?: FetchCall["init"]) => {
    fetches.push({ url, init });
    return Promise.resolve({ json: () => Promise.resolve(fetchJson(url, init)) });
  };
  const script = html.slice(html.indexOf("<script>") + 8, html.lastIndexOf("</script>"));
  const run = new Function("document", "location", "WebSocket", "fetch", "setInterval", "setTimeout", "clearTimeout", "clearInterval", "requestAnimationFrame", "Image", script);
  run(document, { search: "?token=t", protocol: "http:", host: "hive.test" }, FakeWS, fetchStub, () => 0, () => 0, () => {}, () => {}, () => 0, class {});
  const ws = FakeWS.last!;
  ws.onopen!();
  return { root, ws, byId, fetches, settle: () => new Promise((r) => setTimeout(r, 5)) };
}

const HEALTH = {
  ok: true,
  clients: { quest: 1, board: 1, monitor: 0, glasses: 0 },
  beat: 8,
  orbis: { state: "ready", stage: 0, scene: "meadow", want: "live", relay: true },
  solved: [],
  score: null,
  grievances: 0,
  tts: true,
  stt: { elevenlabs: true, whisper: false, provider: "elevenlabs" },
};
const PROMPT = "A sunny meadow with a white wooden beehive on a low stand. The hive is a wheel of cheese. Wide shot, eye-level, static camera, deep depth of field.";

describe("Beat Board (v2.1)", () => {
  test("stations send choice.request (correct ones highlighted), effects fx.force, what-ifs whatif.force", async () => {
    const p = runPage(boardHtml(STATION_TABLE), (url) => (url.startsWith("/lines.json") ? LINES : HEALTH));
    await p.settle();
    for (const st of STATIONS) {
      for (const ch of CHOICES[st]) {
        const b = p.byId(`ch_${st}_${ch}`);
        expect({ id: b.id, good: b.classList.contains("good") }).toEqual({ id: b.id, good: CORRECT_CHOICE[st] === ch });
      }
    }
    expect(p.byId("fx").children.map((b) => b.id)).toEqual(FX.map((f) => `fx_${f}`));
    // the what-if grid: fire, put it out, then the other planned topics (custom is the text box, not a button)
    expect(p.byId("whatifs").children.map((b) => b.textContent)).toEqual(["FIRE", "PUT IT OUT", "RAIN", "FLOWERS", "SPRAY", "WINTER"]);

    p.ws.sent.length = 0;
    p.byId("ch_heat_pebble_dish").onclick!();
    p.byId("ch_smoker_swat").onclick!();
    p.byId("fx_embers").onclick!();
    expect(p.byId("fired").children[0]!.textContent).toBe("operatorfx embers");
    p.byId("fx_hive_glow_red").onclick!();
    for (const b of p.byId("whatifs").children) b.onclick!();
    p.byId("scenes").children.find((b) => b.dataset.scene === "whatif_custom")!.onclick!();
    expect(p.ws.sent).toEqual([
      { type: "choice.request", station: "heat", choice: "pebble_dish" },
      { type: "choice.request", station: "smoker", choice: "swat" },
      { type: "fx.force", name: "embers" },
      { type: "fx.force", name: "hive_glow_red" },
      { type: "whatif.force", topic: "fire" },
      { type: "whatif.force", undo: true },
      { type: "whatif.force", topic: "rain" },
      { type: "whatif.force", topic: "flowers" },
      { type: "whatif.force", topic: "spray" },
      { type: "whatif.force", topic: "winter" },
      { type: "orbis.request", scene: "whatif_custom" },
    ]);
    // what fired: the operator rows, newest first, the last 6 only
    expect(p.byId("fired").children.map((r) => r.textContent)).toEqual([
      "operatorwhat-if winter",
      "operatorwhat-if spray",
      "operatorwhat-if flowers",
      "operatorwhat-if rain",
      "operatorput it out / undo (window meadow)",
      "operatorwhat-if fire",
    ]);
  });

  test("the text box POSTs /board/custom; the custom prompt (or the refusal) shows in the voice log and what fired", async () => {
    let answer: unknown = { ok: true, intent: "what_if", topic: "custom", path: "claude", ms: 900, custom_prompt: PROMPT, blocked: null };
    const p = runPage(boardHtml(STATION_TABLE), (url) => (url.startsWith("/lines.json") ? LINES : url.startsWith("/board/custom") ? answer : HEALTH));
    await p.settle();
    p.byId("customText").value = "  what if the hive was made of cheese ";
    p.byId("customSend").onclick!();
    expect(p.byId("customSend").disabled).toBe(true); // one at a time
    p.byId("customSend").onclick!();
    const posted = p.fetches.filter((f) => f.url.startsWith("/board/custom"));
    expect(posted).toHaveLength(1);
    expect(posted[0]!.url).toBe("/board/custom?token=t");
    expect(posted[0]!.init).toMatchObject({ method: "POST", headers: { "content-type": "application/json" } });
    expect(JSON.parse(posted[0]!.init!.body!)).toEqual({ text: "what if the hive was made of cheese" });
    // the Hive's bus traffic for it
    p.ws.deliver({ type: "transcript", text: "what if the hive was made of cheese", final: true, source: "quest", ms: 0, stt: "board" });
    p.ws.deliver({ type: "intent", name: "what_if", topic: "custom", text: "what if the hive was made of cheese", path: "claude", ms: 900, custom_prompt: PROMPT });
    p.ws.deliver({ type: "say", channel: "room", line_id: "room_whatif_custom", tts_id: null, audio_url: null, caption: "Hold on." });
    p.ws.deliver({ type: "orbis", state: "ready", stage: 15, scene: "whatif_custom", fallback_clip: "meadow.mp4" });
    await p.settle();
    expect(p.byId("customSend").disabled).toBe(false);
    expect(p.byId("customText").value).toBe("");
    const heard = p.byId("heard").textContent;
    expect(heard).toContain("typed on the board");
    expect(heard).toContain("what_if/custom via claude 900 ms");
    expect(heard).toContain(`prompt: ${PROMPT}`);
    const fired = p.byId("fired");
    expect(fired.children).toHaveLength(1); // the typed text and its intent are one row
    expect(fired.textContent).toContain("operatortyped “what if the hive was made of cheese”");
    expect(fired.textContent).toContain("what_if/custom (prompt ready) via claude");
    expect(fired.textContent).toContain("says room_whatif_custom  →  window whatif custom");
    expect(p.byId("log").textContent).toContain("typed → what_if/custom via claude 900 ms prompt: A sunny meadow");

    // a refusal (Enter key sends too)
    answer = { ok: true, intent: "what_if", topic: "custom", path: "claude", ms: 700, custom_prompt: null, blocked: "blocked by the Hive: contains a URL" };
    p.byId("customText").value = "what if we add a link";
    for (const fn of p.byId("customText").listeners.keydown!) fn({ key: "Enter" });
    p.ws.deliver({ type: "intent", name: "what_if", topic: "custom", text: "what if we add a link", path: "claude", ms: 700, custom_prompt: null });
    await p.settle();
    expect(p.byId("heard").textContent).toContain("refused: Grievance filed. Not that one.");
    expect(p.byId("fired").textContent).toContain("what_if/custom (refused)");
    expect(p.byId("log").textContent).toContain("REFUSED (blocked by the Hive: contains a URL)");

    // a voice intent (not typed) starts its own row with its path
    p.ws.deliver({ type: "intent", name: "what_if", topic: "fire", text: "what if a fire starts", path: "keyword", ms: 1 });
    p.ws.deliver({ type: "fx", name: "embers", delay_ms: 2500 });
    expect(p.byId("fired").children[0]!.textContent).toBe("keyword“what if a fire starts” → what_if/fire→ fx embers +2500 ms");
  });

  test("score card with grievances; live grievance count; an operator choice and Unity's choice.made are one row", async () => {
    const p = runPage(boardHtml(STATION_TABLE), (url) => (url.startsWith("/lines.json") ? LINES : { ...HEALTH, grievances: 1 }));
    await p.settle();
    expect(p.byId("griev").textContent).toBe("1"); // from /health
    p.byId("ch_feed_store_honey").onclick!();
    p.ws.deliver({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    expect(p.byId("griev").textContent).toBe("2");
    expect(p.byId("fired").children).toHaveLength(1);
    expect(p.byId("fired").textContent).toBe("operatorchoice feed: store honey ✗→ applied ✗ grievance");
    p.ws.deliver({ type: "choice.made", station: "feed", choice: "sugar_syrup", correct: true }); // the player's own pick
    expect(p.byId("fired").children[0]!.textContent).toBe("questchoice feed: sugar syrup ✓");
    expect(p.byId("hex_feed").textContent).toBe("LIT");
    expect(p.byId("ch_feed_store_honey").className).toContain("made");

    p.ws.deliver({ type: "score", elapsed_s: 167, text: "Hive saved in 2:47", grievances: 2 });
    expect(p.byId("score").textContent).toBe("Hive saved in 2:47  ·  Grievances filed: 2");
    expect(p.byId("score").className).toBe("on");
    p.ws.deliver({ type: "log", text: "score: Hive saved in 2:47" }); // the older log line doesn't overwrite the card
    expect(p.byId("score").textContent).toContain("Grievances filed: 2");
    expect(p.byId("log").textContent).toContain("SCORE Hive saved in 2:47 (167 s), grievances filed: 2");

    p.ws.deliver({ type: "group.reset", group_id: "g2" });
    expect(p.byId("score").textContent).toBe("");
    expect(p.byId("griev").textContent).toBe("0");
    expect(p.byId("fired").textContent).toBe("nothing fired yet");
    expect(p.byId("ch_feed_store_honey").className).not.toContain("made");
  });

  test("/health score card (an object) shows on load", async () => {
    const p = runPage(boardHtml(STATION_TABLE), (url) =>
      url.startsWith("/lines.json") ? LINES : { ...HEALTH, score: { elapsed_s: 200, text: "Hive saved in 3:20", grievances: 0 }, grievances: 0 },
    );
    await p.settle();
    expect(p.byId("score").textContent).toBe("Hive saved in 3:20  ·  Grievances filed: 0");
  });
});

describe("monitor (v2.1)", () => {
  test("score card with grievances; an off-script what-if shows its prompt while it is on the window", async () => {
    const p = runPage(monitorHtml(), () => HEALTH);
    await p.settle();
    expect(p.byId("griev").textContent).toBe("");
    p.ws.deliver({ type: "transcript", text: "what if the hive was made of cheese", final: true, source: "quest", ms: 600, stt: "elevenlabs" });
    p.ws.deliver({ type: "intent", name: "what_if", topic: "custom", text: "what if the hive was made of cheese", path: "claude", ms: 900, custom_prompt: PROMPT });
    expect(p.byId("meant").textContent).toBe("what if: off the script");
    expect(p.byId("custom").className).toBe(""); // not on the window yet
    p.ws.deliver({ type: "orbis", state: "ready", stage: 15, scene: "whatif_custom", fallback_clip: "meadow.mp4" });
    expect(p.byId("scene").textContent).toBe("what if: off the script");
    expect(p.byId("custom").className).toBe("on");
    expect(p.byId("custom").textContent).toBe("A sunny meadow with a white wooden beehive on a low stand. The hive is a wheel of cheese."); // camera sentence dropped
    p.ws.deliver({ type: "orbis", state: "ready", stage: 0, scene: "meadow", fallback_clip: "meadow.mp4" });
    expect(p.byId("custom").className).toBe("");
    p.ws.deliver({ type: "intent", name: "what_if", topic: "custom", text: "what if something rude", path: "claude", ms: 900, custom_prompt: null });
    expect(p.byId("meant").textContent).toBe("what if: grievance filed");

    p.ws.deliver({ type: "score", elapsed_s: 167, text: "Hive saved in 2:47", grievances: 2 });
    expect(p.byId("score").textContent).toBe("Hive saved in 2:47");
    expect(p.byId("griev").textContent).toBe("Grievances filed: 2");
    p.ws.deliver({ type: "log", text: "score: Hive saved in 2:47" });
    expect(p.byId("griev").textContent).toBe("Grievances filed: 2");
    p.ws.deliver({ type: "group.reset", group_id: "g2" });
    expect(p.byId("score").textContent).toBe("");
    expect(p.byId("griev").textContent).toBe("");
  });

  test("syncs the score card and a showing custom prompt from /health on connect; zero grievances is a brag", async () => {
    const p = runPage(monitorHtml(), () => ({
      ...HEALTH,
      orbis: { state: "ready", stage: 15, scene: "whatif_custom", want: "live", relay: true, custom_prompt: PROMPT },
      score: { elapsed_s: 150, text: "Hive saved in 2:30", grievances: 0 },
    }));
    await p.settle();
    expect(p.byId("score").textContent).toBe("Hive saved in 2:30");
    expect(p.byId("griev").textContent).toBe("Grievances filed: 0");
    expect(p.byId("griev").className).toBe("zero");
    expect(p.byId("custom").className).toBe("on");
    expect(p.byId("custom").textContent).toContain("wheel of cheese");
  });
});
