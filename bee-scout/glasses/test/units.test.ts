import {expect, test} from "bun:test"
import {Ear} from "../src/background/ear"
import {Hive, HttpError} from "../src/background/hive"
import {Transcript} from "../src/background/transcript"
import {fakeSession, sleep, until} from "./fakes"

const quiet = () => {}
const LINES = {
  clip_a: {pcm: "AAAA", ms: 100, caption: "A"},
  filler: {pcm: "AAAAAAAA", ms: 100, caption: "F"},
}

function ear(opts: Parameters<typeof fakeSession>[0] = {}) {
  const fake = fakeSession({...opts, lines: LINES})
  return {fake, ear: new Ear((fake.session as any).speaker, quiet, LINES)}
}

test("ear: embedded clip beats audio_url and caption", async () => {
  const {fake, ear: e} = ear()
  const started = await e.play({line_id: "clip_a", audio_url: "https://x/a.mp3", caption: "A"})
  expect(typeof started).toBe("number")
  expect(fake.audio).toEqual([expect.objectContaining({kind: "clip", line: "clip_a"})])
})

test("ear: audio_url when not embedded, caption TTS when the url fails", async () => {
  const {fake, ear: e} = ear()
  await e.play({line_id: "live_1", audio_url: "https://x/live.mp3", caption: "Live"})
  expect(fake.audio.at(-1)).toMatchObject({kind: "url", url: "https://x/live.mp3"})
  ;(fake.session as any).speaker.play = async () => {
    throw {code: "INTERNAL", message: "404"}
  }
  await e.play({line_id: "live_2", audio_url: "https://x/missing.mp3", caption: "Fallback words"})
  await until(() => fake.audio.at(-1)?.kind === "speak", 1000, "tts fallback")
  expect(fake.audio.at(-1)).toMatchObject({kind: "speak", text: "Fallback words"})
})

test("ear: unknown line with no caption is skipped, known ids get a fallback caption", async () => {
  const {fake, ear: e} = ear()
  expect(await e.play({line_id: "nope"})).toBeNull()
  await e.play({line_id: "ear_scan_failed"})
  expect(fake.audio).toEqual([expect.objectContaining({kind: "speak", text: "Lost the signal. Press again."})])
})

test("ear: lines queue in order; 'fillers' interrupt cuts only a filler", async () => {
  const {fake, ear: e} = ear({clipPlayMs: 400})
  const t0 = Date.now()
  void e.play({line_id: "filler"}, {filler: true})
  await sleep(20)
  await e.play({line_id: "clip_a"}, {interrupt: "fillers"})
  expect(Date.now() - t0).toBeLessThan(300) // didn't wait out the 400 ms filler

  void e.play({line_id: "clip_a"})
  const queued = e.play({line_id: "clip_a", caption: "second"}, {interrupt: "fillers"}) // nothing to cut: waits
  const at = await queued
  expect(at! - t0).toBeGreaterThan(700)
  expect(fake.audio.map((a: any) => a.line)).toEqual(["filler", "clip_a", "clip_a", "clip_a"])
})

test("ear: replay repeats the last non-filler line", async () => {
  const {fake, ear: e} = ear()
  await e.play({line_id: "clip_a"})
  await e.play({line_id: "filler"}, {filler: true})
  await sleep(50)
  e.replay()
  await until(() => fake.audio.length === 3, 1000, "replay")
  expect((fake.audio[2] as any).line).toBe("clip_a")
})

function fakeFetch(plan: Record<string, () => Promise<{status: number; text(): Promise<string>}>>) {
  const calls: string[] = []
  const fn = async (url: string) => {
    calls.push(url)
    const host = Object.keys(plan).find((h) => url.startsWith(h))!
    return plan[host]!()
  }
  return {calls, fn}
}
const ok = (body: unknown) => async () => ({status: 200, text: async () => JSON.stringify(body)})

test("hive: token goes in the query, bus url is wss with role=glasses", () => {
  const h = new Hive(["https://a.dev", "https://b.dev"], "t o/k&", quiet)
  expect(h.url(h.host, "/glasses/press")).toBe("https://a.dev/glasses/press?token=t%20o%2Fk%26")
  expect(h.busUrl()).toBe("wss://a.dev/bus?token=t%20o%2Fk%26&role=glasses")
})

test("hive: network error or 5xx fails over and makes the live host active", async () => {
  const f = fakeFetch({
    "https://a.dev": async () => {
      throw new Error("offline")
    },
    "https://b.dev": ok({allowed: true}),
  })
  const h = new Hive(["https://a.dev", "https://b.dev"], "t", quiet, f.fn)
  let changed = ""
  h.onHostChange = (host) => (changed = host)
  expect(await h.post("/glasses/press", {}, 500)).toEqual({allowed: true})
  expect(h.host).toBe("https://b.dev")
  expect(changed).toBe("https://b.dev")

  const g = fakeFetch({"https://a.dev": async () => ({status: 502, text: async () => "bad gateway"}), "https://b.dev": ok({})})
  const h2 = new Hive(["https://a.dev", "https://b.dev"], "t", quiet, g.fn)
  await h2.post("/x", {}, 500)
  expect(g.calls).toHaveLength(2)
})

test("hive: 4xx is final (no failover), timeouts fail over", async () => {
  const f = fakeFetch({"https://a.dev": async () => ({status: 401, text: async () => "bad token"}), "https://b.dev": ok({})})
  const h = new Hive(["https://a.dev", "https://b.dev"], "t", quiet, f.fn)
  await expect(h.post("/x", {}, 500)).rejects.toBeInstanceOf(HttpError)
  expect(f.calls).toHaveLength(1)

  const g = fakeFetch({"https://a.dev": () => new Promise(() => {}), "https://b.dev": ok({late: false})})
  const h2 = new Hive(["https://a.dev", "https://b.dev"], "t", quiet, g.fn)
  expect(await h2.post("/x", {}, 100)).toEqual({late: false})
})

test("transcript: since() includes finals and the live interim", () => {
  const t = new Transcript()
  t.push({text: "old", isFinal: true}, 1000)
  t.push({text: "the sugar", isFinal: true}, 5000)
  t.push({text: "syrup bott", isFinal: false}, 5200)
  expect(t.since(4000)).toBe("the sugar syrup bott")
  expect(t.since(5100)).toBe("syrup bott")
})

test("transcript: listen() returns at once when quiet, waits out a sentence, honours needText", async () => {
  const t = new Transcript()
  let now = Date.now()
  expect(await t.listen(now, now + 2000, false)).toBe("") // quiet: immediately

  now = Date.now()
  t.push({text: "it's ros", isFinal: false})
  const mid = t.listen(now - 10, now + 2000, false)
  setTimeout(() => t.push({text: "it's rosemary", isFinal: true}), 50)
  let t0 = Date.now()
  expect(await mid).toBe("it's rosemary")
  expect(Date.now() - t0).toBeLessThan(500)

  await sleep(5)
  now = Date.now()
  const wait = t.listen(now, now + 2000, true) // needs words: waits for them
  setTimeout(() => t.push({text: "clover", isFinal: true}), 100)
  t0 = Date.now()
  expect(await wait).toBe("clover")
  expect(Date.now() - t0).toBeGreaterThanOrEqual(90)

  await sleep(5)
  now = Date.now()
  t0 = Date.now()
  expect(await t.listen(now, now + 150, true)).toBe("") // nothing said: the deadline ends it
  expect(Date.now() - t0).toBeGreaterThanOrEqual(140)
})

test("lines: the prerender's LINE_PCM16 shape and the placeholder's LINES shape both load", async () => {
  const {parseClips} = await import("../src/background/lines")
  const pcm = "AAAA".repeat(8000) // 24000 bytes = 750 ms at 16 kHz
  const prerender = {SAMPLE_RATE: 16000, LINE_PCM16: {ear_no_signal: pcm}, default: {ear_no_signal: pcm}}
  expect(parseClips(prerender)).toEqual({ear_no_signal: {pcm, ms: 750, caption: "I can't reach the hive. Check the hotspot."}})
  const placeholder = {SAMPLE_RATE: 16000, LINES: {x: {pcm: "AAAA", ms: 1, caption: "X"}}}
  expect(parseClips(placeholder)).toEqual({x: {pcm: "AAAA", ms: 0, caption: "X"}})
  expect(parseClips({})).toEqual({})
})

test("lines: every ear line in the Hive's catalog has its caption here, plus the glasses-only fallbacks", async () => {
  const {CAPTIONS, FILLERS} = await import("../src/background/lines")
  const catalog = (await import("../../hive/lines/lines.json")).default as unknown
  const list = (Array.isArray(catalog) ? catalog : ((catalog as {lines?: unknown[]}).lines ?? [])) as Record<string, any>[]
  const ear = list.filter((e) => e?.channel === "ear" && typeof e.id === "string" && typeof e.text === "string" && e.text.trim())
  const first = new Map<string, string>()
  for (const e of ear) if (!first.has(e.id)) first.set(e.id, e.text)
  for (const [id, text] of first) expect(CAPTIONS[id]).toBe(text)
  expect(CAPTIONS.ear_no_signal).toBeTruthy()
  expect(CAPTIONS.ear_scan_failed).toBeTruthy()
  for (const id of FILLERS) expect(ear.find((e) => e.id === id)?.tags).toContain("filler")
  // room lines never leak into the ear
  for (const e of list.filter((e) => e?.channel === "room")) if (!first.has(e.id)) expect(CAPTIONS[e.id]).toBeUndefined()
})

test("lines: earEntries reads an array or {lines: [...]}, skipping bad and room entries", async () => {
  const {earEntries} = await import("../src/background/lines")
  const good = {id: "ear_a", channel: "ear", text: "A", tags: ["filler", 3]}
  const bad = [null, 7, {id: "ear_b", channel: "ear", text: "  "}, {id: "room_c", channel: "room", text: "C"}, {channel: "ear", text: "no id"}]
  expect(earEntries([good, ...bad])).toEqual([{id: "ear_a", text: "A", tags: ["filler"]}])
  expect(earEntries({lines: [good]})).toEqual([{id: "ear_a", text: "A", tags: ["filler"]}])
  expect(earEntries("nope")).toEqual([])
})

test("voice: noise filter keeps short real requests, drops fillers and junk", async () => {
  const {isNoise, words} = await import("../src/background/voice")
  expect(words("What if a FIRE starts?! It's bad.")).toEqual(["what", "if", "a", "fire", "starts", "it's", "bad"])
  for (const t of ["", "  ", "?!", "uh", "Um.", "okay", "hmm, yeah", "hi", "no", "oh wow", "the"]) expect(isNoise(t)).toBe(true)
  for (const t of ["fire", "put it out", "What if a fire starts?", "syrup", "Beeswax!", "what if it rains"]) expect(isNoise(t)).toBe(false)
})

test("voice: Beeswax's room lines heard across the table are dropped; the scripted wearer lines are not", async () => {
  const {Voice} = await import("../src/background/voice")
  const {ROOM_TEXTS, catalogEntries} = await import("../src/background/lines")
  const {makeConfig} = await import("../src/background/config")
  const sent: string[] = []
  const fetchFn = async (_url: string, init?: {body?: string}) => {
    sent.push(JSON.parse(init!.body!).text)
    return {status: 200, text: async () => JSON.stringify({intent: "unknown", topic: null, line_id: null, audio_url: null, caption: ""})}
  }
  const room = ["Clover? Now you're talking. More flowers, more food. I'll allow it.", "Motion carried."]
  const v = new Voice({
    hive: new Hive(["https://a.dev"], "t", quiet, fetchFn),
    ear: ear().ear,
    config: makeConfig({hiveToken: "t"}),
    beat: () => "whatif",
    scanning: () => false,
    latency: quiet,
    log: quiet,
    roomTexts: room,
  })
  const say = async (text: string) => {
    v.heard({text, isFinal: true})
    await sleep(5)
  }
  // Whole lines, Mentra's sentence-sized pieces of them, and long runs of one: all Beeswax.
  for (const t of ["Clover. Now you're talking.", "Now you're talking!", "more flowers more food I'll allow it", "Motion carried!"]) await say(t)
  expect(sent).toEqual([])
  for (const t of ["what if we plant clover", "what if a fire starts", "put it out", "carried away", "on the hive"]) await say(t)
  expect(sent).toEqual(["what if we plant clover", "what if a fire starts", "put it out", "carried away", "on the hive"])

  // The shipped catalog: every room line is caught, no scripted wearer line is.
  expect(ROOM_TEXTS.length).toBe(catalogEntries((await import("../../hive/lines/lines.json")).default, "room").length)
  const real = new Voice({hive: new Hive(["https://a.dev"], "t", quiet, fetchFn), ear: ear().ear, config: makeConfig({hiveToken: "t"}), beat: () => "whatif", scanning: () => false, latency: quiet, log: quiet}) as any
  for (const t of ROOM_TEXTS) if (t.split(/\s+/).length >= 2) expect(real.isRoomLine(t)).toBe(true)
  expect(real.isRoomLine("Undo what?")).toBe(ROOM_TEXTS.some((t) => t.startsWith("Undo what?"))) // else "undo" -> "Undo what?" -> undo...
  // If a catalog edit trips this, a room line now contains one of these phrases: the wearer could no longer say it.
  const {WEARER_PHRASES, roomGuardProblems} = await import("../src/background/voice")
  expect(WEARER_PHRASES.length).toBeGreaterThan(30)
  for (const t of WEARER_PHRASES) expect([t, real.isRoomLine(t)]).toEqual([t, false])
  expect(roomGuardProblems()).toEqual([]) // what build.ts warns about
})

test("voice: the room-line guard follows catalog text edits (the pitch's planned lines, typography)", async () => {
  const {RoomGuard, WEARER_PHRASES, roomGuardProblems, words} = await import("../src/background/voice")
  const {ROOM_TEXTS} = await import("../src/background/lines")
  // the pitch review's line edits, on top of tonight's lines (a superset only catches more).
  const planned = [
    "See that window? That's the meadow. Every choice you make shows up in there.",
    'Your turn. Ask me "what if," and the window shows it.',
    "My cousins in Japan do this. Never tried it. Make a fist.",
    "Same time tomorrow? Other hives have it worse.",
    "Where'd the honey go? ...Rocket fuel. Next question.",
    "Light, right? That's bad.",
    "Grievance filed. Not that one.",
    "Water? Now they're wet AND angry. Grievance filed. Try again.",
  ]
  expect(roomGuardProblems([...ROOM_TEXTS, ...planned])).toEqual([])
  const guard = new RoomGuard(planned)
  for (const heardBack of ["Ask me what if", "and the window shows it", "rocket fuel next question", "Not that one.", "my cousins in japan do this", "other hives have it worse"])
    expect([heardBack, guard.match(heardBack)]).not.toEqual([heardBack, null])
  for (const t of WEARER_PHRASES) expect([t, guard.match(t)]).toEqual([t, null])

  // A catalog written with curly quotes, an ellipsis and dashes still catches the plain transcript.
  expect(words("Don\u2019t. Say. That.")).toEqual(["don't", "say", "that"])
  const typographic = new RoomGuard(["Don\u2019t. Say. That. \u2026You said it \u2014 look at the window.", "Rocket fuel \u2013 next question."])
  for (const t of ["don't say that", "You said it.", "look at the window", "next question"]) expect([t, typographic.match(t)]).not.toEqual([t, null])
  expect(typographic.match("what if the window catches fire")).toBeNull()

  // Speech-to-text spellings: "Fire's out" sounds like "Fires out" (which the Hive's whatif-beat keyword path reads
  // as a NEW fire what-if), and contractions may come back spelled out.
  const spoken = new RoomGuard([
    "Fire's out. I'm not crying, that's smoke. Motion carried.",
    "Undo what? Nothing's on fire. Yet.",
    "A deep bowl? That's a pool. Bees drown in deep water, and the lifeguard's on strike. Grievance filed.",
  ])
  for (const t of ["Fires out.", "fire is out", "Fire's out!", "Nothings on fire.", "nothing is on fire yet", "Undo what? Nothings on fire. Yet.", "I am not crying, that is smoke", "Thats a pool."])
    expect([t, spoken.match(t)]).not.toEqual([t, null])
  // ...while the wearer's own follow-up about that line, and the scripted lines, still get through.
  for (const t of ["what if the bees drown", "why do bees drown in deep water", "what if a fire starts", "put it out", "put the fire out", "is the fire out", "undo"])
    expect([t, spoken.match(t)]).toEqual([t, null])
  expect(roomGuardProblems(["Fire's out. Motion carried."], ["fires out"])).toEqual([
    `the wearer can no longer say "fires out": room line "Fire's out. Motion carried." swallows it`,
  ])

  // A planted collision is reported, with the line that swallows it.
  const problems = roomGuardProblems(["Put it out, or I'm filing.", "Okay"], ["put it out", "what if it rains"])
  expect(problems).toEqual([`the wearer can no longer say "put it out": room line "Put it out, or I'm filing." swallows it`])
})

test("ear: onSpeak fires for every line that starts, fillers too; cancelFillers cuts only fillers", async () => {
  const {fake, ear: e} = ear({clipPlayMs: 300})
  const said: string[] = []
  e.onSpeak = (c) => said.push(c)
  const filler = e.play({line_id: "filler"}, {filler: true})
  await sleep(20)
  e.cancelFillers()
  expect(await filler).not.toBeNull() // it had started
  await e.play({line_id: "clip_a"})
  expect(said).toEqual(["F", "A"])
  expect(fake.audio.map((a: any) => a.line)).toEqual(["filler", "clip_a"])
})

test("ear: a long clip goes out in two writes, cut on a sample boundary, and starts after the first", async () => {
  const writes: string[] = []
  let secondDoneAt = 0
  const pcm = "AAAA".repeat(10_000) // 30000 bytes
  const speaker = {
    async createStream() {
      return {
        async writeBase64(b64: string) {
          writes.push(b64)
          if (writes.length === 2) {
            await sleep(200) // the host holding the reply while >2 s is buffered
            secondDoneAt = Date.now()
          }
          return {bufferedMs: 0}
        },
        async close() {
          return {}
        },
        async abort() {},
      }
    },
  }
  const e = new Ear(speaker as any, quiet, {long: {pcm, ms: 938, caption: "L"}})
  const started = await e.play({line_id: "long"})
  expect(typeof started).toBe("number")
  await until(() => secondDoneAt > 0, 1000, "second write")
  expect(started!).toBeLessThan(secondDoneAt - 100)
  expect(writes.join("")).toBe(pcm)
  expect(writes).toHaveLength(2)
  expect(writes[0]!.length % 8).toBe(0) // whole base64 groups AND whole 16-bit samples
})

test("voice: a reply to words heard before a group reset is not played; the next one is", async () => {
  const {Voice} = await import("../src/background/voice")
  const {makeConfig} = await import("../src/background/config")
  const {fake, ear: e} = ear()
  const sent: string[] = []
  const slowFetch = async (_url: string, init?: {body?: string}) => {
    sent.push(JSON.parse(init!.body!).text)
    await sleep(100)
    return {status: 200, text: async () => JSON.stringify({intent: "ask", topic: null, line_id: null, audio_url: null, caption: "Old news"})}
  }
  const v = new Voice({
    hive: new Hive(["https://a.dev"], "t", quiet, slowFetch),
    ear: e,
    config: makeConfig({hiveToken: "t"}),
    beat: () => "whatif",
    scanning: () => false,
    latency: quiet,
    log: quiet,
  })
  v.heard({text: "what if it rains", isFinal: true})
  await sleep(20)
  v.reset()
  await sleep(200)
  expect(sent).toEqual(["what if it rains"])
  expect(fake.audio).toEqual([])
  v.heard({text: "what if it snows", isFinal: true})
  await until(() => fake.audio.length === 1, 1000, "reply after reset")
  v.heard({text: "what if it snows", isFinal: true}) // repeat within 3 s: dropped
  v.heard(null as any) // junk from the host: ignored, never throws
  await sleep(150)
  expect(sent).toEqual(["what if it rains", "what if it snows"])
})
