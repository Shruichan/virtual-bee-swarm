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
  await e.play({line_id: "ear_not_now"})
  expect(fake.audio).toEqual([expect.objectContaining({kind: "speak", text: "Not now, I'm monologuing."})])
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
  t.push({text: "a purple", isFinal: true}, 5000)
  t.push({text: "salvia mayb", isFinal: false}, 5200)
  expect(t.since(4000)).toBe("a purple salvia mayb")
  expect(t.since(5100)).toBe("salvia mayb")
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
  const {parseClips, CAPTIONS} = await import("../src/background/lines")
  const pcm = "AAAA".repeat(8000) // 24000 bytes = 750 ms at 16 kHz
  const prerender = {SAMPLE_RATE: 16000, LINE_PCM16: {ear_not_now: pcm}, default: {ear_not_now: pcm}}
  expect(parseClips(prerender)).toEqual({ear_not_now: {pcm, ms: 750, caption: "Not now, I'm monologuing."}})
  const placeholder = {SAMPLE_RATE: 16000, LINES: {x: {pcm: "AAAA", ms: 1, caption: "X"}}}
  expect(parseClips(placeholder)).toEqual({x: {pcm: "AAAA", ms: 0, caption: "X"}})
  expect(parseClips({})).toEqual({})
  // every ear id the Hive can name without a caption has one here
  for (const id of ["ear_practice", "ear_not_now", "ear_swarm_arrived", "ear_no_signal", "ear_scan_failed"]) {
    expect(CAPTIONS[id]).toBeTruthy()
  }
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
