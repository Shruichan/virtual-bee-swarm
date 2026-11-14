// scripts/fake-quest.ts WAV handling: any common WAV -> 16 kHz mono PCM16 for MIC_PCM16 frames.
import { describe, expect, test } from "bun:test";
import { parseSteps, parseWav, toPcm16 } from "../scripts/fake-quest";

/** A WAV file: `frames` x `channels` samples from fn(i, c) in -1..1. */
function makeWav(opts: { rate: number; channels: number; bits: 8 | 16 | 24 | 32; float?: boolean; frames: number; fn: (i: number, c: number) => number; extensible?: boolean }): Uint8Array {
  const { rate, channels, bits, frames } = opts;
  const bytes = bits / 8;
  const dataLen = frames * channels * bytes;
  const fmtLen = opts.extensible ? 40 : 16;
  const buf = new Uint8Array(12 + 8 + fmtLen + 8 + 4 + 8 + dataLen); // includes a junk LIST chunk before data
  const dv = new DataView(buf.buffer);
  const put = (at: number, s: string) => [...s].forEach((ch, i) => (buf[at + i] = ch.charCodeAt(0)));
  put(0, "RIFF");
  dv.setUint32(4, buf.length - 8, true);
  put(8, "WAVE");
  put(12, "fmt ");
  dv.setUint32(16, fmtLen, true);
  const format = opts.float ? 3 : 1;
  dv.setUint16(20, opts.extensible ? 0xfffe : format, true);
  dv.setUint16(22, channels, true);
  dv.setUint32(24, rate, true);
  dv.setUint32(28, rate * channels * bytes, true);
  dv.setUint16(32, channels * bytes, true);
  dv.setUint16(34, bits, true);
  if (opts.extensible) {
    dv.setUint16(36, 22, true);
    dv.setUint16(44, format, true); // SubFormat GUID starts with the format code
  }
  let at = 20 + fmtLen;
  put(at, "LIST");
  dv.setUint32(at + 4, 4, true);
  put(at + 8, "INFO");
  at += 12;
  put(at, "data");
  dv.setUint32(at + 4, dataLen, true);
  at += 8;
  for (let i = 0; i < frames; i++) {
    for (let c = 0; c < channels; c++) {
      const v = opts.fn(i, c);
      if (opts.float) dv.setFloat32(at, v, true);
      else if (bits === 8) dv.setUint8(at, Math.round(v * 127) + 128);
      else if (bits === 16) dv.setInt16(at, Math.round(v * 32767), true);
      else if (bits === 24) {
        const x = Math.round(v * 8388607);
        dv.setUint8(at, x & 0xff);
        dv.setUint8(at + 1, (x >> 8) & 0xff);
        dv.setInt8(at + 2, x >> 16);
      } else dv.setInt32(at, Math.round(v * 2147483647), true);
      at += bytes;
    }
  }
  return buf;
}

const pcmValues = (pcm: Uint8Array) => {
  const dv = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
  return Array.from({ length: pcm.length / 2 }, (_, i) => dv.getInt16(i * 2, true));
};

describe("fake-quest WAV -> 16 kHz mono PCM16", () => {
  test("48 kHz stereo 16-bit: mixed to mono and resampled to a third of the samples", () => {
    const wav = parseWav(makeWav({ rate: 48000, channels: 2, bits: 16, frames: 48000, fn: (_i, c) => (c === 0 ? 0.5 : 0.1) }));
    expect(wav).toMatchObject({ sampleRate: 48000, channels: 2 });
    expect(wav.samples.length).toBe(48000);
    const pcm = toPcm16(wav);
    expect(pcm.length).toBe(32000); // 1 s at 16 kHz, 2 bytes per sample
    for (const v of pcmValues(pcm).slice(0, 50)) expect(Math.abs(v - 0.3 * 32767)).toBeLessThan(3);
  });

  test("float32, 24-bit, 8-bit and WAVE_FORMAT_EXTENSIBLE all decode", () => {
    const sine = (i: number) => 0.8 * Math.sin((2 * Math.PI * 440 * i) / 22050);
    for (const spec of [
      { bits: 32 as const, float: true },
      { bits: 24 as const },
      { bits: 8 as const },
      { bits: 16 as const, extensible: true },
      { bits: 32 as const },
    ]) {
      const wav = parseWav(makeWav({ rate: 22050, channels: 1, frames: 2205, fn: sine, ...spec }));
      const pcm = toPcm16(wav);
      expect(pcm.length).toBe(Math.round((2205 * 16000) / 22050) * 2);
      const peak = Math.max(...pcmValues(pcm).map(Math.abs));
      expect({ spec, ok: peak > 0.7 * 32767 && peak <= 32767 }).toEqual({ spec, ok: true });
    }
  });

  test("garbage is rejected with a clear error", () => {
    expect(() => parseWav(new TextEncoder().encode("not a wav at all"))).toThrow("RIFF");
    const noData = makeWav({ rate: 16000, channels: 1, bits: 16, frames: 0, fn: () => 0 }).subarray(0, 36);
    expect(() => parseWav(noData)).toThrow();
  });
});

describe("fake-quest --steps", () => {
  test("beats, choices, utterances and waits keep their order", () => {
    expect(parseSteps("hook, 2,smoker,smoker:swat,say:/tmp/a b.wav,wait:1.5,heat:pebble_dish")).toEqual([
      { kind: "beat", n: 1 },
      { kind: "beat", n: 2 },
      { kind: "beat", n: 3 }, // a bare station name is its beat
      { kind: "choice", station: "smoker", choice: "swat" },
      { kind: "say", file: "/tmp/a b.wav" },
      { kind: "wait", seconds: 1.5 },
      { kind: "choice", station: "heat", choice: "pebble_dish" },
    ]);
  });

  test("bad entries fail with the step named", () => {
    expect(() => parseSteps("hook,smoker:pebble_dish")).toThrow('bad step "smoker:pebble_dish"');
    expect(() => parseSteps("lunch")).toThrow('bad step "lunch"');
    expect(() => parseSteps("99")).toThrow('bad step "99"');
    expect(() => parseSteps("say:")).toThrow("say:<wav file>");
    expect(() => parseSteps("wait:soon")).toThrow("wait:<seconds>");
    expect(parseSteps(" , ")).toEqual([]);
  });
});
