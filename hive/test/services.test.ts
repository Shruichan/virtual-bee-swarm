import { beforeEach, describe, expect, test } from "bun:test";
import {
  checkElevenLabsCredits,
  checkVoiceConsistency,
  classify,
  creditsStatus,
  noteFailure,
  noteOk,
  resetServices,
  serviceAlerts,
} from "../src/services";

const httpError = (status: number, message: string) => Object.assign(new Error(message), { status });

describe("service alerts", () => {
  beforeEach(() => resetServices());

  test("classifies the failures the operator can fix", () => {
    expect(classify("claude", httpError(400, "Your credit balance is too low to access the Anthropic API.")).problem).toBe("Out of credits");
    expect(classify("claude", httpError(401, "invalid x-api-key")).problem).toBe("API key rejected");
    expect(classify("claude", httpError(429, "rate_limit_error")).problem).toBe("Rate limited");
    expect(classify("claude", httpError(529, "overloaded_error")).problem).toContain("overloaded");
    expect(classify("elevenlabs_tts", httpError(401, "invalid_api_key")).problem).toBe("API key rejected");
    expect(classify("elevenlabs_tts", httpError(401, "quota_exceeded: This request exceeds your quota")).problem).toBe("Out of credits");
    expect(classify("elevenlabs_tts", new Error("ElevenLabs speech budget reached")).problem).toContain("budget");
    expect(classify("elevenlabs_tts", new Error("not configured")).problem).toBe("Not configured");
    expect(classify("elevenlabs_stt", new Error("fetch failed: getaddrinfo ENOTFOUND api.elevenlabs.io")).network).toBe(true);
    expect(classify("whisper", new Error("connect ECONNREFUSED 127.0.0.1:8242")).problem).toContain("Whisper");
  });

  test("every fix names something to do", () => {
    for (const e of [httpError(402, "billing"), httpError(500, "boom"), new Error("fetch failed")]) {
      expect(classify("claude", e).fix.length).toBeGreaterThan(10);
      expect(classify("elevenlabs_tts", e).fix.length).toBeGreaterThan(10);
    }
  });

  test("a network failure also raises the internet alert, and any success clears both", () => {
    noteFailure("claude", new Error("fetch failed: getaddrinfo ENOTFOUND api.anthropic.com"));
    expect(serviceAlerts().map((a) => a.service).sort()).toEqual(["claude", "internet"]);
    noteOk("claude");
    expect(serviceAlerts()).toHaveLength(0);
  });

  test("repeats count up instead of piling up; credit problems sort first", () => {
    noteFailure("whisper", new Error("connect ECONNREFUSED"));
    noteFailure("elevenlabs_tts", httpError(401, "quota_exceeded"));
    noteFailure("elevenlabs_tts", httpError(401, "quota_exceeded"));
    const list = serviceAlerts();
    expect(list).toHaveLength(2);
    expect(list[0]!.service).toBe("elevenlabs_tts");
    expect(list[0]!.count).toBe(2);
  });

  test("voice consistency: local rehearsal voice and a different voice id are flagged", () => {
    expect(checkVoiceConsistency({ ELEVENLABS_VOICE_ID: "abc" }, "abc")).toBe("match");
    expect(serviceAlerts()).toHaveLength(0);
    expect(checkVoiceConsistency({ ELEVENLABS_VOICE_ID: "xyz" }, "abc")).toBe("mismatch");
    expect(serviceAlerts()[0]!.fix).toContain("ELEVENLABS_VOICE_ID=abc");
    resetServices();
    expect(checkVoiceConsistency({ VBS_LOCAL_TTS_URL: "http://127.0.0.1:8300", ELEVENLABS_VOICE_ID: "abc" }, "abc")).toBe("local");
    expect(serviceAlerts()[0]!.problem).toContain("local rehearsal voice");
  });

  test("ElevenLabs credits: reads what's left and warns when low; a key without user_read is skipped quietly", async () => {
    const reply = (status: number, body: unknown) =>
      (async () => new Response(typeof body === "string" ? body : JSON.stringify(body), { status })) as unknown as typeof fetch;
    const c = await checkElevenLabsCredits("k", { fetchImpl: reply(200, { character_count: 9_000, character_limit: 10_000 }), warnBelow: 3000 });
    expect(c?.left).toBe(1000);
    expect(creditsStatus()?.left).toBe(1000);
    expect(serviceAlerts()[0]!.problem).toContain("Low on credits");
    resetServices();
    expect(await checkElevenLabsCredits("k", { fetchImpl: reply(401, '{"detail":{"status":"missing_permissions","message":"user_read"}}') })).toBeNull();
    expect(serviceAlerts()).toHaveLength(0);
    expect(await checkElevenLabsCredits(undefined)).toBeNull();
  });
});
