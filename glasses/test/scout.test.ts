// Live Bee Scout on the glasses: the real app with a fake session, against the REAL hive bus and stub
// /scout/* + /glasses/* HTTP endpoints. No internet, no speaker.
import {afterAll, afterEach, beforeAll, describe, expect, test} from "bun:test"
import {Bus as HiveBus, setQuiet} from "../../hive/src/bus"
import {createState} from "../../hive/src/state"
import {start} from "../src/background/app"
import {makeConfig, type LocalConfig} from "../src/background/config"
import {hasWake, isEchoOf, Scout, scoutLineId} from "../src/background/scout"
import {Ear} from "../src/background/ear"
import {fakeSession, sleep, until, type FakeOptions} from "./fakes"

const TOKEN = "test-token"
setQuiet(true)

const state = createState()
const hiveBus = new HiveBus(state, {lineText: () => "", onVerdictForce() {}, onPlea() {}}, {token: TOKEN, host: "test", heartbeatMs: 60_000})

type Hit = {path: string; body: any}
let hits: Hit[] = []
let server: ReturnType<typeof Bun.serve>
let base = ""

beforeAll(() => {
  server = Bun.serve({
    port: 0,
    hostname: "127.0.0.1",
    websocket: hiveBus.websocket as any,
    async fetch(req, srv) {
      const url = new URL(req.url)
      if (url.pathname === "/bus") return hiveBus.upgrade(req, srv as any) ?? undefined
      if (url.searchParams.get("token") !== TOKEN) return new Response("bad token", {status: 401})
      const body = req.method === "POST" ? await req.json().catch(() => null) : null
      hits.push({path: url.pathname, body})
      if (url.pathname === "/glasses/press") return Response.json({allowed: false, scan_id: null, line_id: null})
      if (url.pathname === "/glasses/utterance") return Response.json({intent: "unknown", topic: null, line_id: null, audio_url: null, caption: ""})
      return Response.json({ok: true})
    },
  })
  hiveBus.start()
  base = `http://127.0.0.1:${server.port}`
})

afterAll(() => {
  hiveBus.stop()
  server.stop(true)
})

let app: {stop(): void} | null = null
afterEach(() => {
  app?.stop()
  app = null
  hits = []
})

const paths = () => hits.map((h) => h.path)

async function boot(cfg: Partial<LocalConfig> = {}, opts: FakeOptions = {}) {
  const fake = fakeSession(opts)
  app = start(fake.session, makeConfig({hiveToken: TOKEN, hosts: [base], fieldGuideMode: false, ...cfg}))
  await until(() => fake.status.last?.bus === "open", 5000, "bus open")
  return fake
}

const sayOnBus = (tts_id: string | null, caption: string, audio_url: string | null = null) =>
  hiveBus.broadcast({type: "say", channel: "ear", line_id: null, tts_id, audio_url, caption}, ["glasses"])

describe("scout mode", () => {
  const SCOUT = {scoutMode: true, streamUrl: "rtmp://127.0.0.1:1935/scout"}

  test("bus open -> POST /scout/start once; the camera stream starts", async () => {
    const fake = await boot(SCOUT)
    await until(() => paths().includes("/scout/start"), 2000, "/scout/start")
    expect(typeof hits.find((h) => h.path === "/scout/start")!.body.at).toBe("number")
    await until(() => fake.streams.some((s) => s.op === "start"), 2000, "stream start")
    expect((fake.streams[0]!.opts as any).direct).toBe(SCOUT.streamUrl)
    await sleep(100)
    expect(paths().filter((p) => p === "/scout/start").length).toBe(1)
  })

  test("short press -> /scout/button (not the scanner); long press only replays", async () => {
    const fake = await boot(SCOUT)
    await until(() => paths().includes("/scout/start"), 2000, "/scout/start")
    fake.press("short")
    await until(() => paths().includes("/scout/button"), 2000, "/scout/button")
    fake.press("long")
    await sleep(150)
    expect(paths().filter((p) => p === "/scout/button").length).toBe(1)
    expect(paths()).not.toContain("/glasses/press")
    expect(paths()).not.toContain("/glasses/scan")
    expect(fake.photos).toEqual([])
  })

  test("every final transcript -> /scout/utterance; interims and /glasses/utterance never", async () => {
    const fake = await boot(SCOUT)
    await until(() => paths().includes("/scout/start"), 2000, "/scout/start")
    fake.say("yes ple", false)
    fake.say("yes please")
    fake.say("no") // a short filler word Voice would drop: the game still needs it
    await until(() => paths().filter((p) => p === "/scout/utterance").length === 2, 2000, "two utterances")
    expect(hits.filter((h) => h.path === "/scout/utterance").map((h) => h.body.text)).toEqual(["yes please", "no"])
    await sleep(100)
    expect(paths()).not.toContain("/glasses/utterance")
  })

  test("the ear plays scout says; LED orange for the chime, green for a fact, nothing for the rest", async () => {
    const fake = await boot(SCOUT)
    sayOnBus("scout_chime", "Bee fact found!")
    sayOnBus("scout_hook_beehive", "A hive. Union headquarters. Want the gossip?")
    await until(() => fake.audio.length >= 2, 3000, "chime + hook audio")
    expect(fake.audio.map((a) => (a.kind === "speak" ? a.text : a.kind))).toEqual(["Bee fact found!", "A hive. Union headquarters. Want the gossip?"])
    expect(fake.leds).toEqual([{color: "orange", ontime: 300, offtime: 200, count: 4}])

    const url = `${base}/scout/audio/scout_fact_beehive.mp3`
    sayOnBus(null, "One queen lays the colony's eggs.", url) // id from the URL alone
    sayOnBus("scout_progress_1", "One down. Five to go.")
    await until(() => fake.audio.length >= 4, 3000, "fact + progress audio")
    expect(fake.audio[2]).toMatchObject({kind: "url", url})
    expect(fake.leds).toEqual([
      {color: "orange", ontime: 300, offtime: 200, count: 4},
      {color: "green", ontime: 3000, offtime: 0, count: 1},
    ])
  })
})

describe("scout stop", () => {
  test("a scout_stop say cuts the current line and drops the queue; it plays nothing itself", async () => {
    const fake = await boot({scoutMode: true, streamUrl: "rtmp://127.0.0.1:1935/scout"}, {speakMs: 400})
    sayOnBus("scout_chime", "Bee fact found!")
    sayOnBus("scout_fact_beehive", "Queued fact that never plays.")
    await until(() => fake.audio.length >= 1, 2000, "chime starts")
    sayOnBus("scout_stop", "")
    await sleep(600)
    expect(fake.audio.map((a) => (a.kind === "speak" ? a.text : a.kind))).toEqual(["Bee fact found!"])
    expect(fake.status.last?.log.some((l) => l.includes("scout: stop"))).toBe(true)
    sayOnBus("scout_teacher", "Ask your teacher for another assignment!")
    await until(() => fake.audio.length >= 2, 2000, "next line plays")
    expect(fake.audio[1]).toMatchObject({kind: "speak", text: "Ask your teacher for another assignment!"})
  })
})

describe("Ask Beeswax barge-in", () => {
  const SCOUT = {scoutMode: true, streamUrl: "rtmp://127.0.0.1:1935/scout"}

  test("'Hey Beeswax, ...' cuts Beeswax at once (before the Hive answers) and still goes to the Hive", async () => {
    const fake = await boot(SCOUT, {speakMs: 500})
    sayOnBus("scout_fact_pebble_dish", "Bees can't swim, so pebbles are their landing pads.")
    sayOnBus("scout_fact_pebble_dish_2", "Queued second fact.")
    await until(() => fake.audio.length >= 1, 2000, "fact starts")
    fake.say("Hey Beeswax, why do bees need pebbles?")
    await until(() => paths().includes("/scout/utterance"), 2000, "utterance")
    await sleep(700)
    expect(fake.audio.map((a) => (a.kind === "speak" ? a.text : a.kind))).toEqual(["Bees can't swim, so pebbles are their landing pads."])
    expect(fake.status.last?.log.some((l) => l.includes("ear stopped"))).toBe(true)
  })

  test("questions without the wake word, Beeswax's own voice, and yes/no never cut him", async () => {
    const fake = await boot(SCOUT, {speakMs: 300})
    sayOnBus("scout_intro_1", "Hey class! I'm Beeswax, your unbeelievably charming and handsome bee assistant.")
    sayOnBus("scout_chime_ask", "Bee fact found! Would you like to learn more?")
    sayOnBus("scout_fact_honey_jar", "This is honey.")
    await until(() => fake.audio.length >= 1, 2000, "intro starts")
    fake.say("Hey class, I'm Beeswax, your unbelievably charming") // his own intro heard back, name included
    fake.say("so by the way, where's the judge view?") // people talking, not to Beeswax
    fake.say("would you like to learn more")
    fake.say("yes")
    await until(() => fake.audio.length >= 3, 3000, "all three lines play")
    expect(fake.status.last?.log.some((l) => l.includes("ear stopped"))).toBe(false)
    expect(hits.filter((h) => h.path === "/scout/utterance").length).toBe(4) // all still reach the Hive (judge screen)
  })

  test("an Ask Beeswax answer (tts_id scout_ask_*) cuts whatever is playing or queued", async () => {
    const fake = await boot(SCOUT, {speakMs: 400})
    sayOnBus("scout_fact_bug_spray", "This is bug spray.")
    sayOnBus("scout_fact_bug_spray_2", "Queued, never plays.")
    await until(() => fake.audio.length >= 1, 2000, "fact starts")
    const url = `${base}/scout/ask/abc1.mp3`
    sayOnBus("scout_ask_abc1", "Great question, class!", url)
    await until(() => fake.audio.length >= 2, 2000, "answer plays")
    await sleep(500)
    expect(fake.audio.map((a) => (a.kind === "url" ? a.url : a.kind === "speak" ? a.text : a.kind))).toEqual(["This is bug spray.", url])
  })

  test("partials stop Beeswax only when scoutStopOnPartial is on", async () => {
    const fake = await boot({...SCOUT, scoutStopOnPartial: true}, {speakMs: 500})
    sayOnBus("scout_fact_beehive", "This is a beehive.")
    sayOnBus("scout_fact_beehive_2", "Queued.")
    await until(() => fake.audio.length >= 1, 2000, "fact starts")
    fake.say("how do bees", false) // no wake word: nothing
    await sleep(100)
    fake.say("hey beeswax how do", false)
    await sleep(700)
    expect(fake.audio.length).toBe(1)
    expect(paths()).not.toContain("/scout/utterance") // a partial is never sent
  })
})

describe("non-scout mode is unchanged", () => {
  test("press goes to the scanner, no /scout calls, no stream", async () => {
    const fake = await boot({scoutMode: false})
    await sleep(100)
    fake.press("short")
    await until(() => paths().includes("/glasses/press"), 2000, "/glasses/press")
    fake.say("yes please")
    sayOnBus("scout_chime", "Bee fact found!")
    await until(() => fake.audio.length >= 1, 3000, "audio")
    await sleep(100)
    expect(paths().some((p) => p.startsWith("/scout/"))).toBe(false)
    expect(fake.streams).toEqual([])
    expect(fake.leds.some((l) => (l as any)?.color === "orange" && (l as any).count === 4)).toBe(false)
  })
})

describe("Scout unit", () => {
  test("scoutLineId", () => {
    expect(scoutLineId({tts_id: "scout_chime"})).toBe("scout_chime")
    expect(scoutLineId({tts_id: null, audio_url: "https://h/scout/audio/scout_fact_bee_hotel.mp3"})).toBe("scout_fact_bee_hotel")
    expect(scoutLineId({tts_id: "abc", audio_url: "https://h/audio/123.mp3"})).toBe(null)
    expect(scoutLineId({})).toBe(null)
  })

  test("busOpen starts only once (a reconnect keeps progress)", () => {
    const sent: string[] = []
    const s = new Scout({hive: {send: async (p: string) => (sent.push(p), true)}, led: {turnOn: async () => ({}) as any}, log: () => {}})
    s.busOpen()
    s.busOpen()
    s.heard({text: "  ", isFinal: true})
    s.heard({text: "sure", isFinal: false})
    expect(sent).toEqual(["/scout/start"])
  })

  test("wake word mirrors the Hive's (greeting + name at the start); his own lines from any speaker are echoes", () => {
    for (const w of ["Hey Beeswax, why", "hey bees wax", "Hey, bee's wax.", "hay beeswax", "A beeswax tell me", "hey beezwax", "Hey bees, why", "okay beeswax", "hello beeswax", "um so hey beeswax what", "Hey Bs wax"])
      expect([w, hasWake(w)]).toEqual([w, true])
    for (const n of ["why do bees need pebbles", "the bees are cute", "what's that, hey beeswax?", "showing the transcript of what Beeswax was saying", "Hey B, hello?", "Hey, can you tell me a little bit more?", "Beeswax, why?", "Beeswax found! This is a bee water dish.", "Just say Hey Beeswax and I'll BEE right there", ""])
      expect([n, hasWake(n)]).toEqual([n, false])
    expect(isEchoOf("would you like to learn more", ["Bee fact found! Would you like to learn more?"])).toBe(true)
    const caps = ["Bee fact found!", "This is a bee water dish. Bees can't swim, so pebbles are their landing pads.", 'Got a question? Just say "Hey Beeswax" and I\'ll BEE right there!']
    for (const e of ["Beeswax found! This is a bee water dish. Bees can't swim, so pebbles are their landing pads.", "Be fact found.", "Bee facts found!", "Hey Beeswax and I'll be right there"])
      expect([e, isEchoOf(e, caps)]).toEqual([e, true])
    expect(isEchoOf("Hey Beeswax, what is a bee water dish?", caps)).toBe(false) // the wearer's own call

    const s = new Scout({hive: {send: async () => true}, led: {turnOn: async () => ({}) as any}, log: () => {}})
    expect(s.callsBeeswax("why the honey")).toBe(false)
    expect(s.callsBeeswax("Hey Beeswax, why the honey")).toBe(true)
    s.noteSaid("Hey class! I'm Beeswax, your unbeelievably charming and handsome bee assistant.")
    expect(s.callsBeeswax("I'm Beeswax")).toBe(false)
    expect(s.callsBeeswax("I'm Beeswax your unbelievably charming")).toBe(false)
    expect(s.callsBeeswax("Hey Beeswax, what is honey")).toBe(true)
    s.noteSaid('Questions? Just say "Hey Beeswax"!')
    expect(s.callsBeeswax("Hey Beeswax")).toBe(false) // the tip line heard back while it plays
    s.noteDone('Questions? Just say "Hey Beeswax"!')
    expect(s.callsBeeswax("Hey Beeswax")).toBe(false) // ...and just after
    // a line the Hive sent that played on the Mac (not yet, or never, in this ear) is still an echo
    s.said({tts_id: "scout_fact_pebble_dish", caption: "This is a bee water dish. Bees can't swim, so pebbles are their landing pads."})
    expect(s.callsBeeswax("Hey Beeswax and pebbles are their landing pads")).toBe(true) // a call it doesn't contain
    expect(s.callsBeeswax("Beeswax found! This is a bee water dish.")).toBe(false)
  })

  test("LED failures are swallowed and it gives up after three", async () => {
    let calls = 0
    const s = new Scout({hive: {send: async () => true}, led: {turnOn: () => (calls++, Promise.reject(new Error("no led")))}, log: () => {}})
    for (let i = 0; i < 5; i++) {
      s.said({tts_id: "scout_chime"})
      await sleep(5)
    }
    expect(calls).toBe(3)
  })
})

describe("Ear: queued scout lines play fully and in order", () => {
  /** A speaker whose url play() can hang; it reports playing, then stopped after `ms` (unless silent). */
  function speaker(o: {ms: number; hang?: boolean; silentStates?: boolean}) {
    const played: string[] = []
    const spoken: string[] = []
    const listeners = new Set<(e: {state: string}) => void>()
    const emit = (state: string) => listeners.forEach((l) => l({state}))
    return {
      played,
      spoken,
      sp: {
        async createStream() {
          throw new Error("no clips")
        },
        async play(p: {audioUrl: string}) {
          played.push(p.audioUrl)
          if (!o.silentStates) emit("playing")
          await sleep(o.ms)
          if (!o.silentStates) emit("stopped")
          if (o.hang) await new Promise(() => {}) // the host never sends the result
        },
        async speak(t: string) {
          spoken.push(t)
          return {completed: true}
        },
        stop() {},
        onStateChange(h: (e: {state: string}) => void) {
          listeners.add(h)
          return () => listeners.delete(h)
        },
      } as any,
    }
  }
  const intro = ["intro_1", "intro_2", "intro_3", "chime", "fact", "fact_2", "any_q"]

  test("a long queued sequence is not dropped as stale in scout mode (it was at the 10 s default)", async () => {
    const short = speaker({ms: 40})
    const dropping = new Ear(short.sp, () => {}, {}, {maxQueueWaitMs: 100})
    await Promise.all(intro.map((id) => dropping.play({line_id: null, audio_url: `u/${id}`, caption: id})))
    expect(short.played.length).toBeLessThan(intro.length) // the old behaviour: the tail is dropped

    const s = speaker({ms: 40})
    const ear = new Ear(s.sp, () => {}, {}, {maxQueueWaitMs: 120_000})
    await Promise.all(intro.map((id) => ear.play({line_id: null, audio_url: `u/${id}`, caption: id})))
    expect(s.played).toEqual(intro.map((id) => `u/${id}`))
  })

  test("a play() the host never resolves still ends on 'stopped', and the queue goes on", async () => {
    const s = speaker({ms: 30, hang: true})
    const ear = new Ear(s.sp, () => {}, {}, {maxQueueWaitMs: 120_000})
    const t0 = Date.now()
    await Promise.all(["a", "b", "c"].map((id) => ear.play({line_id: null, audio_url: `u/${id}`, caption: id})))
    await until(() => s.played.length === 3, 2000, "all three")
    expect(s.played).toEqual(["u/a", "u/b", "u/c"])
    expect(Date.now() - t0).toBeLessThan(3000) // each next play waits up to 0.8 s for the unsettled request

  })

  test("no result and no state events: the caption-based limit ends it instead of 45 s", async () => {
    const s = speaker({ms: 10, hang: true, silentStates: true})
    const logs: string[] = []
    const ear = new Ear(s.sp, (l: string) => logs.push(l), {}, {maxQueueWaitMs: 120_000})
    void ear.play({line_id: null, audio_url: "u/a", caption: "a"}, {maxMs: 150})
    void ear.play({line_id: null, audio_url: "u/b", caption: "b"}, {maxMs: 150})
    await until(() => s.played.length === 2, 2000, "second line")
    expect(s.played).toEqual(["u/a", "u/b"])
    await sleep(300)
    expect(s.spoken).toEqual([]) // never played a second time in the phone's voice
    expect(logs.some((l) => String(l).includes("took too long"))).toBe(true)
  })

  test("a play started right after the previous one would stall: the gap keeps the second line (intro_2)", async () => {
    // a host that silently drops a play() issued within 100 ms of the previous one ending
    const played: string[] = []
    let lastEnd = 0
    const listeners = new Set<(e: {state: string}) => void>()
    const sp = {
      async createStream() {
        throw new Error("no clips")
      },
      async play(p: {audioUrl: string}) {
        if (Date.now() - lastEnd < 100) return new Promise(() => {}) // stalls: no sound, no states, no result
        played.push(p.audioUrl)
        listeners.forEach((l) => l({state: "playing"}))
        await sleep(30)
        lastEnd = Date.now()
        listeners.forEach((l) => l({state: "stopped"}))
      },
      async speak() {
        return {completed: true}
      },
      stop() {},
      onStateChange(h: (e: {state: string}) => void) {
        listeners.add(h)
        return () => listeners.delete(h)
      },
    } as any
    const logs: string[] = []
    const ear = new Ear(sp, (l: string) => logs.push(String(l)), {}, {maxQueueWaitMs: 120_000})
    await Promise.all(["intro_1", "intro_2", "intro_3"].map((id) => ear.play({line_id: null, audio_url: `u/${id}`, caption: id}, {maxMs: 2000})))
    expect(played).toEqual(["u/intro_1", "u/intro_2", "u/intro_3"])
    expect(logs.filter((l) => l.startsWith("ear: play ")).length).toBe(3)
    await until(() => logs.filter((l) => l.startsWith("ear: end ")).length === 3, 1000, "three ends logged")
  })

  test("a play that never starts is stopped and retried once (host that reports states)", async () => {
    const played: string[] = []
    let calls = 0
    const listeners = new Set<(e: {state: string}) => void>()
    const sp = {
      async createStream() {
        throw new Error("no clips")
      },
      async play(p: {audioUrl: string}) {
        calls++
        if (calls === 2) return new Promise(() => {}) // the second request stalls once
        played.push(p.audioUrl)
        listeners.forEach((l) => l({state: "playing"}))
        await sleep(20)
        listeners.forEach((l) => l({state: "stopped"}))
      },
      async speak() {
        return {completed: true}
      },
      stop() {},
      onStateChange(h: (e: {state: string}) => void) {
        listeners.add(h)
        return () => listeners.delete(h)
      },
    } as any
    const logs: string[] = []
    const ear = new Ear(sp, (l: string) => logs.push(String(l)), {}, {maxQueueWaitMs: 120_000})
    await Promise.all(["a", "b"].map((id) => ear.play({line_id: null, audio_url: `u/${id}`, caption: id}, {maxMs: 10_000})))
    expect(played).toEqual(["u/a", "u/b"])
    expect(calls).toBe(3)
    expect(logs.some((l) => l.includes("never started playing, retrying"))).toBe(true)
  }, 15_000)

  test("a line whose player starts but never reports the end is over at its known length (?ms= in the URL)", async () => {
    const played: {url: string; stopOther: boolean}[] = []
    const listeners = new Set<(e: {state: string}) => void>()
    const sp = {
      async createStream() {
        throw new Error("no clips")
      },
      async play(p: {audioUrl: string; stopOtherAudio?: boolean}) {
        played.push({url: p.audioUrl, stopOther: !!p.stopOtherAudio})
        listeners.forEach((l) => l({state: "playing"}))
        if (p.audioUrl.includes("hang")) return new Promise(() => {}) // plays, then silence from the host
        await sleep(50)
        listeners.forEach((l) => l({state: "stopped"}))
      },
      async speak() {
        return {completed: true}
      },
      stop() {},
      onStateChange(h: (e: {state: string}) => void) {
        listeners.add(h)
        return () => listeners.delete(h)
      },
    } as any
    const logs: string[] = []
    const ear = new Ear(sp, (l: string) => logs.push(String(l)), {}, {maxQueueWaitMs: 120_000})
    const t0 = Date.now()
    void ear.play({line_id: null, audio_url: "u/hang.mp3?ms=300", caption: "a"}, {maxMs: 20_000})
    void ear.play({line_id: null, audio_url: "u/next.mp3?ms=50", caption: "b"}, {maxMs: 20_000})
    await until(() => played.length === 2, 3000, "next line")
    expect(Date.now() - t0).toBeLessThan(2000) // ~300 ms + 700 ms slack + gap, not the 20 s limit
    expect(played[1]!.stopOther).toBe(true) // takes the player over from the hung one
    expect(logs.some((l) => l.includes("(duration)"))).toBe(true)
  })
})
