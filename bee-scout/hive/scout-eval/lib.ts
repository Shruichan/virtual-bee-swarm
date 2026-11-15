// Shared helpers for the Ask Beeswax persona eval: Claude client (same credentials as hive/src/router.ts: a bare
// new Anthropic() reads the env key or the `ant auth login` profile), a disk cache so re-runs cost nothing, card context,
// and a small concurrency pool.
import Anthropic from "@anthropic-ai/sdk";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import cardsRaw from "./cards.json";

type CreateParams = Anthropic.Beta.Messages.MessageCreateParamsNonStreaming;

export const DIR = import.meta.dir;
export const CACHE_DIR = join(DIR, "cache");
export const ANSWER_MODEL = "claude-haiku-5-5"; // production Ask model
export const JUDGE_MODEL = process.env.EVAL_JUDGE_MODEL?.trim() || "claude-opus-5-5";
export const REWRITE_MODEL = process.env.EVAL_REWRITE_MODEL?.trim() || "claude-opus-5-5";

export interface AskCtx {
  label: string;
  facts: string[];
  sources: string[];
  vettedFacts: string[];
}
export interface Card { label: string; name: string; facts: string[]; sources: string[] }
export const CARDS: Card[] = (cardsRaw as { cards: Card[] }).cards;
export const VETTED_FACTS: string[] = (cardsRaw as { vettedFacts: string[] }).vettedFacts;
export const CARD_BY_LABEL = new Map(CARDS.map((c) => [c.label, c]));

export function ctxFor(label: string): AskCtx {
  const c = CARD_BY_LABEL.get(label);
  if (!c) throw new Error(`unknown card ${label}`);
  return { label: c.label, facts: c.facts, sources: c.sources, vettedFacts: VETTED_FACTS };
}

let client: Anthropic | null = null;
export const getClient = () => (client ??= new Anthropic({ maxRetries: 4 }));

// Per-model switches as in router.ts / ask.ts: Haiku 5.5 thinking off at effort low (production); Opus/Sonnet 5.5 get the
// server-side refusal fallback.
function modelParams(model: string): Partial<CreateParams> {
  if (model === "claude-haiku-5-5") return { thinking: { type: "disabled" } };
  if (model === "claude-opus-5-5" || model === "claude-sonnet-5-5") {
    return { betas: ["server-side-fallback-2026-07-01"], fallbacks: "default" } as Partial<CreateParams>;
  }
  return {};
}

export interface CallOpts {
  model: string;
  system: string;
  messages: Anthropic.Beta.Messages.BetaMessageParam[];
  maxTokens: number;
  effort?: "low" | "medium" | "high" | "xhigh" | "max";
  schema?: Record<string, unknown>;
  tag: string; // cache namespace
  salt?: string; // force a fresh sample
}

const usage = { calls: 0, cached: 0, inTok: 0, outTok: 0, byModel: {} as Record<string, { in: number; out: number }> };
export const usageSummary = () => usage;

/** One Claude call, cached on disk by its full request. Returns the response text. */
export async function call(o: CallOpts): Promise<string> {
  const body: CreateParams = {
    model: o.model,
    max_tokens: o.maxTokens,
    system: [{ type: "text", text: o.system, cache_control: { type: "ephemeral" } }],
    messages: o.messages,
    output_config: { effort: o.effort ?? "low", ...(o.schema ? { format: { type: "json_schema", schema: o.schema } } : {}) },
    ...modelParams(o.model),
  } as CreateParams;
  const key = createHash("sha256").update(JSON.stringify({ body, salt: o.salt ?? "" })).digest("hex").slice(0, 32);
  mkdirSync(CACHE_DIR, { recursive: true });
  const file = join(CACHE_DIR, `${o.tag}-${key}.json`);
  usage.calls++;
  if (existsSync(file)) {
    usage.cached++;
    return (JSON.parse(readFileSync(file, "utf8")) as { text: string }).text;
  }
  const msg = await getClient().beta.messages.create(body);
  if (msg.stop_reason === "refusal") throw new Error(`refusal (${o.tag})`);
  const text = msg.content.flatMap((b) => (b.type === "text" ? [b.text] : [])).join("").trim();
  const u = msg.usage;
  usage.inTok += (u.input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0) + (u.cache_creation_input_tokens ?? 0);
  usage.outTok += u.output_tokens ?? 0;
  const m = (usage.byModel[o.model] ??= { in: 0, out: 0 });
  m.in += (u.input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0) + (u.cache_creation_input_tokens ?? 0);
  m.out += u.output_tokens ?? 0;
  writeFileSync(file, JSON.stringify({ text, stop: msg.stop_reason, model: msg.model }));
  return text;
}

/** Rough $ estimate of uncached spend this process (list prices per 1M tokens). */
export function costEstimate(): number {
  const price: Record<string, [number, number]> = {
    "claude-haiku-5-5": [0.1, 0.5],
    "claude-sonnet-5-5": [2, 10],
    "claude-opus-5-5": [4, 20],
  };
  let usd = 0;
  for (const [m, t] of Object.entries(usage.byModel)) {
    const [pi, po] = price[m] ?? [4, 20];
    usd += (t.in * pi + t.out * po) / 1e6;
  }
  return usd;
}

/** Run fn over items with at most n in flight; keeps order. */
export async function pool<T, R>(items: T[], n: number, fn: (x: T, i: number) => Promise<R>): Promise<R[]> {
  const out = new Array<R>(items.length);
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(n, items.length) }, async () => {
      while (next < items.length) {
        const i = next++;
        out[i] = await fn(items[i]!, i);
      }
    }),
  );
  return out;
}

export function readJson<T>(path: string): T {
  return JSON.parse(readFileSync(path, "utf8")) as T;
}
export function writeJson(path: string, v: unknown): void {
  writeFileSync(path, JSON.stringify(v, null, 1) + "\n");
}

export const wordCount = (s: string) => s.split(/\s+/).filter((w) => /[a-z0-9]/i.test(w)).length;

export function pad(s: string | number, n: number): string {
  const t = String(s);
  return t.length >= n ? t.slice(0, n) : t + " ".repeat(n - t.length);
}
