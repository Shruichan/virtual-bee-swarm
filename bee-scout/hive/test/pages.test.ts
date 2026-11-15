// The Beat Board and monitor pages: run their inline scripts against a tiny fake DOM + fake WebSocket, feed them
// v3 bus messages, press buttons, and check what they show and send. Catches runtime errors a syntax check misses.
import { describe, expect, test } from "bun:test";
import { boardHtml } from "../src/board";
import { LINES, STATION_TABLE } from "../src/lines";
import { monitorHtml } from "../src/monitor";

// ---------------------------------------------------------------- a very small DOM

class El {
  id = "";
  className = "";
  innerHTML = "";
  value = "";
  dataset: Record<string, string> = {};
  style: Record<string, string> = {};
  children: El[] = [];
  parent: El | null = null;
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
    c.parent = this;
    this.children.push(c);
    return c;
  }
  insertBefore(c: El, ref: El | null) {
    const i = ref ? this.children.indexOf(ref) : -1;
    if (i < 0) this.children.push(c);
    else this.children.splice(i, 0, c);
    c.parent = this;
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

/** Parse the page body into El nodes (the pages use simple, well-formed HTML). */
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
      else if (k.startsWith("data-")) el.dataset[k.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = v;
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

interface Page { root: El; ws: FakeWS; byId(id: string): El; errors: unknown[]; settle(): Promise<void> }

function runPage(html: string, fetchJson: (url: string) => unknown): Page {
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
  const errors: unknown[] = [];
  const fetchStub = (url: string) =>
    Promise.resolve({ json: () => Promise.resolve(fetchJson(url)) });
  const script = html.slice(html.indexOf("<script>") + 8, html.lastIndexOf("</script>"));
  const run = new Function(
    "document",
    "location",
    "WebSocket",
    "fetch",
    "setInterval",
    "setTimeout",
    "clearTimeout",
    "clearInterval",
    "requestAnimationFrame",
    "Image",
    script,
  );
  run(
    document,
    { search: "?token=t", protocol: "http:", host: "hive.test" },
    FakeWS,
    fetchStub,
    () => 0,
    () => 0,
    () => {},
    () => {},
    () => 0,
    class {},
  );
  const ws = FakeWS.last!;
  ws.onopen!();
  return { root, ws, byId, errors, settle: () => new Promise((r) => setTimeout(r, 5)) };
}

const HEALTH = {
  ok: true,
  clients: { quest: 1, board: 1, monitor: 0, glasses: 0 },
  beat: 8,
  orbis: { state: "ready", stage: 0, scene: "meadow", want: "live", relay: true },
  solved: [],
  score: null,
  tts: true,
  stt: { elevenlabs: true, whisper: false, provider: "elevenlabs" },
};

describe("Beat Board page", () => {
  test("renders v3 controls, shows bus traffic, and its buttons send only contract ops", async () => {
    const p = runPage(boardHtml(STATION_TABLE), (url) => (url.startsWith("/lines.json") ? LINES : HEALTH));
    await p.settle();
    expect(p.ws.url).toBe("ws://hive.test/bus?role=board&token=t");
    expect(p.ws.sent[0]).toMatchObject({ type: "hello", role: "board", version: 2 });
    expect(p.byId("stt").textContent).toBe("elevenlabs");
    expect(p.byId("beats").children).toHaveLength(13);
    expect(p.byId("lines").children.length).toBe(Object.keys(LINES).length);

    p.ws.deliver({ type: "welcome", group_id: "g1", beat: 8, mode: "solo", server_time: 0, host: "h" });
    expect(p.byId("beatNow").textContent).toBe("8 whatif");
    p.ws.deliver({ type: "transcript", text: "what if a fire starts", final: true, source: "quest", ms: 640, stt: "elevenlabs" });
    p.ws.deliver({ type: "intent", name: "what_if", topic: "fire", text: "what if a fire starts", path: "keyword", ms: 1 });
    expect(p.byId("heard").textContent).toContain("what if a fire starts");
    expect(p.byId("heard").textContent).toContain("what_if/fire via keyword");
    p.ws.deliver({ type: "latency", hop: "voice.stt", ms: 640, source: "hive" });
    expect(p.byId("lStt").textContent).toBe("640 ms");
    p.ws.deliver({ type: "orbis", state: "ready", stage: 9, scene: "whatif_fire", fallback_clip: "whatif_fire.mp4" });
    expect(p.byId("winScene").textContent).toBe("whatif fire");
    p.ws.deliver({ type: "choice.made", station: "feed", choice: "sugar_syrup", correct: true });
    expect(p.byId("hex_feed").textContent).toBe("LIT");
    p.ws.deliver({ type: "log", text: "score: Hive saved in 2:47" });
    expect(p.byId("score").textContent).toBe("Hive saved in 2:47");
    for (const m of [
      { type: "fx", name: "embers", delay_ms: 2500 },
      { type: "say", channel: "room", line_id: "room_whatif_fire", tts_id: null, audio_url: null, caption: "Don't." },
      { type: "error", message: "x" },
      { type: "scan.verdict", item: "person", verdict: "roast", path: "claude", ms: 3, seen: "a person", caption: "" },
    ]) p.ws.deliver(m);

    // v2.1: what-ifs are whatif.force (the Hive runs the voice path), stations are choice.request
    p.ws.sent.length = 0;
    p.byId("wOut").onclick!(); // the window is on fire: put it out
    await p.settle();
    expect(p.ws.sent).toEqual([{ type: "whatif.force", undo: true }]);
    p.ws.sent.length = 0;
    p.byId("wFire").onclick!();
    const rain = p.byId("whatifs").children.find((b) => b.textContent === "RAIN")!;
    rain.onclick!();
    await p.settle();
    expect(p.ws.sent).toEqual([
      { type: "whatif.force", topic: "fire" },
      { type: "whatif.force", topic: "rain" },
    ]);
    p.ws.sent.length = 0;
    p.byId("ch_feed_store_honey").onclick!();
    await p.settle();
    expect(p.ws.sent).toEqual([{ type: "choice.request", station: "feed", choice: "store_honey" }]);
    // mode buttons are bare mode requests
    p.ws.sent.length = 0;
    p.byId("orbis").children.find((b) => b.dataset.want === "off")!.onclick!();
    expect(p.ws.sent).toEqual([{ type: "orbis.request", want: "off" }]);
    // beats, scenes, lines
    p.ws.sent.length = 0;
    p.byId("beats").children[9]!.onclick!();
    p.byId("next").onclick!();
    p.byId("scenes").children.find((b) => b.dataset.scene === "moon")!.onclick!();
    p.byId("lines").children[0]!.onclick!();
    await p.settle();
    expect(p.ws.sent.slice(0, 3)).toEqual([
      { type: "beat.request", n: 9 },
      { type: "beat.request", next: true },
      { type: "orbis.request", scene: "moon" }, // v2.1: a scene request carries no want
    ]);
    expect(p.ws.sent[3]).toMatchObject({ type: "say.force" });

    p.ws.deliver({ type: "group.reset", group_id: "g2" });
    expect(p.byId("score").textContent).toBe("");
    expect(p.byId("hex_feed").textContent).toBe("not yet");
    expect(p.byId("heard").textContent).toBe("nothing heard yet");
    expect(p.byId("log").children.length).toBeGreaterThan(5);
  });
});

describe("monitor page", () => {
  test("shows what was said, what it meant, the window, hexagons, captions and the score", async () => {
    const p = runPage(monitorHtml(), () => ({ ...HEALTH, solved: ["smoker"], score: null }));
    await p.settle();
    expect(p.ws.url).toBe("ws://hive.test/bus?role=monitor&token=t");
    expect(p.byId("hex_smoker").className).toContain("lit"); // synced from /health on connect
    p.ws.deliver({ type: "beat.set", n: 8, name: "whatif" });
    expect(p.byId("beat").textContent).toContain("whatif");
    p.ws.deliver({ type: "transcript", text: "what if a fire starts", final: true, source: "quest", ms: 600, stt: "elevenlabs" });
    expect(p.byId("said").textContent).toContain("what if a fire starts");
    p.ws.deliver({ type: "intent", name: "what_if", topic: "fire", text: "what if a fire starts", path: "keyword", ms: 1 });
    expect(p.byId("meant").textContent).toBe("what if: fire");
    p.ws.deliver({ type: "orbis", state: "ready", stage: 9, scene: "whatif_fire", fallback_clip: "whatif_fire.mp4" });
    expect(p.byId("scene").textContent).toBe("what if: fire");
    expect(p.byId("stream").textContent).toBe("live Orbis stream");
    p.ws.deliver({ type: "fx", name: "embers", delay_ms: 2500 });
    expect(p.byId("fx").className).toBe("on");
    p.ws.deliver({ type: "say", channel: "room", line_id: "room_whatif_fire", tts_id: null, audio_url: null, caption: "Don't. Say. That." });
    expect(p.byId("captions").textContent).toContain("Don't. Say. That.");
    p.ws.deliver({ type: "choice.made", station: "feed", choice: "store_honey", correct: false });
    expect(p.byId("hex_feed").textContent).toContain("1 grievance");
    p.ws.deliver({ type: "choice.made", station: "feed", choice: "sugar_syrup", correct: true });
    expect(p.byId("hex_feed").className).toContain("lit");
    p.ws.deliver({ type: "log", text: "score: Hive saved in 2:47" });
    expect(p.byId("score").textContent).toBe("Hive saved in 2:47");
    p.ws.deliver({ type: "transcript", text: "", final: true, source: "quest", ms: 600, stt: "none" });
    expect(p.byId("said").textContent).toBe("(couldn't hear that)");
    p.ws.deliver({ type: "scan.started", scan_id: "s1" });
    expect(p.byId("polaroid").className).toContain("on");
    p.ws.deliver({ type: "group.reset", group_id: "g2" });
    expect(p.byId("score").textContent).toBe("");
    expect(p.byId("hex_feed").className).toBe("hex");
    expect(p.byId("scene").textContent).toBe("meadow");
  });
});
