// Service health for the operator: when Claude, ElevenLabs, Whisper or the internet fails, the ride keeps going on its
// fallbacks (recorded lines, canned answers, captions), and this module says WHICH service failed, WHY in plain words,
// and the one thing to do about it. /health returns the list, the Board shows it as a red banner, the console logs it
// once per change. Nothing here throws, and nothing in the ride waits on it.

export type ServiceName = "claude" | "elevenlabs_tts" | "elevenlabs_stt" | "whisper" | "internet";

export interface ServiceAlert {
  service: ServiceName;
  label: string;
  problem: string;
  fix: string;
  since: number; // ms epoch, first failure in this streak
  last: number; // ms epoch, latest failure
  count: number;
}

export interface Credits {
  service: "elevenlabs";
  left: number;
  limit: number;
  checked: number;
  resets_at: number | null; // ms epoch, when the plan's character count resets
}

const LABELS: Record<ServiceName, string> = {
  claude: "Claude (Anthropic)",
  elevenlabs_tts: "ElevenLabs voice",
  elevenlabs_stt: "ElevenLabs transcription",
  whisper: "Whisper (local transcription)",
  internet: "Internet",
};

const alerts = new Map<ServiceName, ServiceAlert>();
let credits: Credits | null = null;
let creditsNote: { problem: string; fix: string } | null = null;

function message(e: unknown): string {
  if (e instanceof Error) {
    const body = (e as { body?: unknown }).body;
    const extra = body && typeof body === "object" ? ` ${JSON.stringify(body).slice(0, 300)}` : "";
    return `${e.message}${extra}`;
  }
  return typeof e === "string" ? e : JSON.stringify(e ?? "").slice(0, 300);
}

function statusOf(e: unknown): number | null {
  const o = e as { status?: unknown; statusCode?: unknown } | null;
  const s = o && (typeof o.status === "number" ? o.status : typeof o.statusCode === "number" ? o.statusCode : null);
  return typeof s === "number" ? s : null;
}

const NETWORK_RX = /enotfound|econnrefused|econnreset|etimedout|eai_again|getaddrinfo|fetch failed|network|socket hang up|unable to connect|connection (?:error|refused|reset)|dns/i;
const TIMEOUT_RX = /timed? ?out|timeout|exceeded \d+ ?ms|aborted/i;

/** Plain-words reason and the fix for one failure. Exported for tests. */
export function classify(service: ServiceName, e: unknown): { problem: string; fix: string; network: boolean } {
  const status = statusOf(e);
  const msg = message(e);
  const low = msg.toLowerCase();
  const offline = { problem: "Can't reach the service (no internet, or DNS / Wi-Fi is down)", fix: "Check the Hive laptop's Wi-Fi or hotspot. The ride keeps going offline with recorded lines.", network: true };
  if (service === "whisper") {
    if (NETWORK_RX.test(low) || status === null && /refused|404/.test(low))
      return { problem: "The Whisper sidecar isn't answering", fix: "Start hive/stt-whisper (port 8242), or ignore this if ElevenLabs transcription works.", network: false };
    return { problem: `Whisper failed: ${msg.slice(0, 120)}`, fix: "Restart hive/stt-whisper; see the Hive console.", network: false };
  }
  if (service === "claude") {
    if (status === 401 || /authentication|invalid x-api-key|invalid api key/.test(low))
      return { problem: "API key rejected", fix: "Put a valid ANTHROPIC_API_KEY in hive/.env and restart the Hive. Canned answers cover it meanwhile.", network: false };
    if (status === 402 || /credit balance|billing|insufficient|purchase credits|quota/.test(low))
      return { problem: "Out of credits", fix: "Top up at console.anthropic.com (Plans & Billing) or swap ANTHROPIC_API_KEY in hive/.env. Canned answers cover it meanwhile.", network: false };
    if (status === 429 || /rate.?limit/.test(low))
      return { problem: "Rate limited", fix: "Wait a minute; fewer back-to-back questions. Canned answers cover it.", network: false };
    if (status === 529 || /overloaded/.test(low))
      return { problem: "Anthropic is overloaded right now", fix: "Temporary on their side; nothing to change. Canned answers cover it.", network: false };
    if (status === 404 || /model.*not.*found|not_found_error/.test(low))
      return { problem: "Model not found", fix: "Check ROUTER_MODEL / ASK_MODEL / DIRECTOR_MODEL in hive/.env (default claude-haiku-5-5).", network: false };
    if (NETWORK_RX.test(low) || (status === null && /connection/.test(low))) return offline;
    if (TIMEOUT_RX.test(low)) return { problem: "Answers are too slow (timing out)", fix: "Check the Wi-Fi speed; the ride falls back to canned answers.", network: false };
    return { problem: `Request failed${status ? ` (HTTP ${status})` : ""}: ${msg.slice(0, 120)}`, fix: "See the Hive console for the full error.", network: false };
  }
  // ElevenLabs (voice out, voice in)
  const env = service === "elevenlabs_tts" ? "ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID" : "ELEVENLABS_API_KEY";
  if (/speech budget reached/.test(low))
    return { problem: "The Hive's own speech budget is used up", fix: "Raise ELEVENLABS_TTS_CHAR_BUDGET in hive/.env (or start a new ELEVENLABS_TTS_BUDGET_PERIOD) and restart. Captions show meanwhile.", network: false };
  if (/not configured|unset/.test(low))
    return { problem: "Not configured", fix: `Set ${env} in hive/.env and restart the Hive.`, network: false };
  if (status === 401 || /invalid_api_key|unauthorized|invalid api key/.test(low))
    return { problem: "API key rejected", fix: "Put a valid ELEVENLABS_API_KEY in hive/.env and restart the Hive.", network: false };
  if (status === 402 || /quota_exceeded|quota exceeded|insufficient|credits|character limit|payment/.test(low))
    return { problem: "Out of credits", fix: "Top up at elevenlabs.io (Subscription), or set ELEVENLABS_TTS_CHAR_BUDGET=0 to run on recorded lines + captions only.", network: false };
  if (status === 429 || /too_many_concurrent|rate.?limit|too many requests/.test(low))
    return { problem: "Too many requests at once", fix: "Temporary; wait a moment. Recorded lines keep playing.", network: false };
  if (status === 404 || /voice.*not.*found|voice_not_found/.test(low))
    return { problem: "Voice not found", fix: "Check ELEVENLABS_VOICE_ID in hive/.env.", network: false };
  if (NETWORK_RX.test(low)) return offline;
  if (TIMEOUT_RX.test(low)) return { problem: "Responses are too slow (timing out)", fix: "Check the Wi-Fi speed; Beeswax falls back to captions.", network: false };
  return { problem: `Request failed${status ? ` (HTTP ${status})` : ""}: ${msg.slice(0, 120)}`, fix: "See the Hive console for the full error.", network: false };
}

function upsert(service: ServiceName, problem: string, fix: string, now: number): void {
  const old = alerts.get(service);
  if (old && old.problem === problem) {
    old.last = now;
    old.count++;
    return;
  }
  alerts.set(service, { service, label: LABELS[service], problem, fix, since: old?.since ?? now, last: now, count: (old?.count ?? 0) + 1 });
  console.warn(`[alert] ${LABELS[service]}: ${problem}. Fix: ${fix}`);
}

/** A call to `service` failed. Never throws. */
export function noteFailure(service: ServiceName, e: unknown, now = Date.now()): void {
  try {
    const c = classify(service, e);
    if (c.network) {
      upsert("internet", "The Hive laptop can't reach the internet", "Check its Wi-Fi or hotspot. Recorded lines, captions and the Board keep working offline.", now);
    }
    upsert(service, c.problem, c.fix, now);
  } catch {
    // never let reporting break the ride
  }
}

/** A call to `service` worked: clear its alert (and the internet alert, since something remote answered). */
export function noteOk(service: ServiceName): void {
  if (alerts.delete(service)) console.log(`[alert] ${LABELS[service]}: working again`);
  if (service !== "whisper" && alerts.delete("internet")) console.log("[alert] Internet: working again");
}

/** Everything failing right now, worst first (credits/keys before transient ones), plus a low-credit warning. */
export function serviceAlerts(): ServiceAlert[] {
  const list = [...alerts.values()];
  if (creditsNote && credits) {
    list.push({ service: "elevenlabs_tts", label: "ElevenLabs credits", problem: creditsNote.problem, fix: creditsNote.fix, since: credits.checked, last: credits.checked, count: 1 });
  }
  const weight = (a: ServiceAlert) => (/credits|key rejected|not configured|budget/i.test(a.problem) ? 0 : a.service === "internet" ? 1 : 2);
  return list.sort((a, b) => weight(a) - weight(b) || b.last - a.last);
}

export function creditsStatus(): Credits | null {
  return credits;
}

/** Test helper. */
export function resetServices(): void {
  alerts.clear();
  credits = null;
  creditsNote = null;
}

/**
 * ElevenLabs credits left (GET /v1/user/subscription: free, spends nothing). Warns below `warnBelow` characters.
 * A key without the user_read permission can't read it: that's noted in the log only, not raised as an alert.
 */
export async function checkElevenLabsCredits(
  apiKey: string | undefined,
  opts: { warnBelow?: number; fetchImpl?: typeof fetch; timeoutMs?: number } = {},
): Promise<Credits | null> {
  if (!apiKey) return null;
  const f = opts.fetchImpl ?? fetch;
  const warnBelow = opts.warnBelow ?? (Number(process.env.ELEVENLABS_CREDIT_WARN) || 3000);
  try {
    const res = await f("https://api.elevenlabs.io/v1/user/subscription", {
      headers: { "xi-api-key": apiKey },
      signal: AbortSignal.timeout(opts.timeoutMs ?? 5000),
    });
    if (res.status === 401 || res.status === 403) {
      const body = await res.text().catch(() => "");
      if (/permission|user_read|missing_permissions/i.test(body)) {
        console.log("[alert] ElevenLabs credits: this key can't read the subscription (no user_read permission); skipping the credit check");
        return null;
      }
      noteFailure("elevenlabs_tts", Object.assign(new Error(body.slice(0, 200) || "unauthorized"), { status: res.status }));
      return null;
    }
    if (!res.ok) return null;
    const j = (await res.json()) as { character_count?: number; character_limit?: number; next_character_count_reset_unix?: number };
    const limit = Number(j.character_limit ?? 0);
    const used = Number(j.character_count ?? 0);
    const left = Math.max(0, limit - used);
    credits = { service: "elevenlabs", left, limit, checked: Date.now(), resets_at: j.next_character_count_reset_unix ? j.next_character_count_reset_unix * 1000 : null };
    const was = creditsNote?.problem;
    creditsNote = left < warnBelow
      ? {
          problem: left <= 0 ? "Out of credits" : `Low on credits: ${left.toLocaleString("en-US")} characters left`,
          fix: "Top up at elevenlabs.io (Subscription), or set ELEVENLABS_TTS_CHAR_BUDGET below what's left. Recorded lines don't use credits.",
        }
      : null;
    if (creditsNote && creditsNote.problem !== was) console.warn(`[alert] ElevenLabs credits: ${creditsNote.problem}`);
    return credits;
  } catch (e) {
    noteFailure("elevenlabs_tts", e);
    return null;
  }
}
