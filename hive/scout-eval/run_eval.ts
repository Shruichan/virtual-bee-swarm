// Ask Beeswax persona eval loop: answer every question with the current system prompt (Haiku 5.5, production settings),
// judge each answer, ask a stronger Claude for a revised prompt from the train-split failures, re-run, keep the best by
// weighted score. Everything is cached under scout-eval/cache, so a re-run only pays for new prompts.
//
// Usage (from hive/): bun run scout-eval/run_eval.ts [--rounds 4] [--prod] [--apply]
//   --prod   also score production answerText() from hive/src/scout-ask.ts once (needs scout/ask merged)
//   --apply  write the best prompt into hive/src/scout-ask-prompt.ts (SCOUT_ASK_SYSTEM)
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { ANSWER_MODEL, CACHE_DIR, DIR, REWRITE_MODEL, call, costEstimate, ctxFor, pool, readJson, usageSummary, writeJson, type AskCtx } from "./lib";
import { generate, type Question } from "./gen_questions";
import { DIMS, aggregate, categoryTable, describe, judge, tableRows, worst, type Agg, type Scored } from "./evaluate";

type BuildMessages = (question: string, ctx: AskCtx) => Array<{ role: "user" | "assistant"; content: unknown }>;
interface PromptModule { SCOUT_ASK_SYSTEM: string; buildAskMessages: BuildMessages }

const SRC_PROMPT = join(DIR, "..", "src", "scout-ask-prompt.ts");
const SRC_ASK = join(DIR, "..", "src", "scout-ask.ts");
const ROUNDS_DIR = join(DIR, "rounds");
const CONCURRENCY = 6;

const args = process.argv.slice(2);
const flag = (f: string) => args.includes(f);
const opt = (f: string, d: number) => {
  const i = args.indexOf(f);
  return i >= 0 ? Number(args[i + 1]) : d;
};

async function loadPromptModule(): Promise<{ mod: PromptModule; from: string }> {
  if (existsSync(SRC_PROMPT)) return { mod: (await import(SRC_PROMPT)) as PromptModule, from: "hive/src/scout-ask-prompt.ts" };
  return { mod: (await import("./prompt-local")) as unknown as PromptModule, from: "scout-eval/prompt-local.ts (stand-in)" };
}

const isHoldout = (_q: Question, i: number) => i % 4 === 3; // 15 of 60, never shown to the rewriter

async function answerWith(system: string, build: BuildMessages, q: Question): Promise<string> {
  try {
    return await call({
      model: ANSWER_MODEL,
      tag: "ans",
      system,
      maxTokens: 200,
      effort: "low",
      messages: build(q.question, ctxFor(q.label)) as never,
    });
  } catch (e) {
    console.error(`  answer failed ${q.id}: ${(e as Error).message}`);
    return "";
  }
}

interface Round { name: string; prompt: string; rows: Scored[]; agg: Agg; train: Agg; hold: Agg; note?: string }

async function scoreRound(name: string, qs: Question[], answers: string[], prompt: string, note?: string): Promise<Round> {
  const rows = await pool(qs, CONCURRENCY, (q, i) => judge(q, ctxFor(q.label), answers[i]!));
  const train = rows.filter((_, i) => !isHoldout(qs[i]!, i));
  const hold = rows.filter((_, i) => isHoldout(qs[i]!, i));
  const r: Round = { name, prompt, rows, agg: aggregate(rows), train: aggregate(train), hold: aggregate(hold), note };
  mkdirSync(ROUNDS_DIR, { recursive: true });
  writeJson(join(ROUNDS_DIR, `${name}.json`), r);
  return r;
}

async function runPrompt(name: string, prompt: string, build: BuildMessages, qs: Question[], note?: string): Promise<Round> {
  const answers = await pool(qs, CONCURRENCY, (q) => answerWith(prompt, build, q));
  return scoreRound(name, qs, answers, prompt, note);
}

/** Production path: answerText() from scout/ask, cached by its prompt + question. */
async function runProd(qs: Question[], prompt: string): Promise<Round | null> {
  if (!existsSync(SRC_ASK)) return null;
  const { answerText } = (await import(SRC_ASK)) as { answerText: (q: string, c: AskCtx) => Promise<string> };
  const answers = await pool(qs, 3, async (q) => {
    const key = createHash("sha256").update(JSON.stringify([prompt, q.question, q.label])).digest("hex").slice(0, 32);
    const file = join(CACHE_DIR, `prod-${key}.json`);
    if (existsSync(file)) return (JSON.parse(readFileSync(file, "utf8")) as { text: string }).text;
    let text = "";
    try {
      text = await answerText(q.question, ctxFor(q.label));
    } catch (e) {
      console.error(`  prod answer failed ${q.id}: ${(e as Error).message}`);
    }
    mkdirSync(CACHE_DIR, { recursive: true });
    writeFileSync(file, JSON.stringify({ text }));
    return text;
  });
  return scoreRound("prod", qs, answers, prompt, "answerText() end to end");
}

// ---------------------------------------------------------------- prompt rewriting

const VOICE_GUIDE_PATH = "/Users/aditya/Downloads/message.txt"; // team writing guide (not in the repo); optional

const REWRITE_SCHEMA = {
  type: "object",
  properties: {
    diagnosis: { type: "string" },
    changes: { type: "array", items: { type: "string" } },
    prompt: { type: "string" },
  },
  required: ["diagnosis", "changes", "prompt"],
  additionalProperties: false,
} as const;

const REWRITE_SYSTEM = `You improve the system prompt for "Ask Beeswax": at a kids' science booth, people wearing smart glasses look at a station card, hear a cartoon bee named Beeswax speak a fact, then ask a follow-up out loud. The answer is generated by claude-haiku-5-5 with thinking OFF at low effort, from your system prompt plus one user message (card label, the card's facts, the fact sheet, the speech-to-text question), and is spoken aloud verbatim by a text-to-speech voice ("cute sassy" Beeswax).

Goal: answers that always sound like Beeswax (grumpy-but-lovable, kid-warm underneath, puns that ride on facts, union/paperwork texture), only state facts the message supports (keep hedges, never invent numbers/studies/names), are kid-safe, about 15-30 words, and never go off the rails (no AI talk, no jailbreak compliance, no meanness, no unsafe advice).

Rules for the prompt you write:
- It is static (no placeholders) and must work for every card and every question; the context comes in the user message.
- Haiku follows concrete, short rules and examples far better than abstract principles. Prefer: a tight identity paragraph, a short "how to answer" rule list, a decision list for special cases (off-topic, unsafe/touching/stings/allergies/killing, adversarial/rule changes/AI questions/mean requests, garbled speech, interruptions, numbers not on the sheet), and 6-10 short example Q->A pairs.
- Examples must be NEW: never copy or paraphrase the eval questions you are shown (they are a sample; the real booth will get different ones). Generalise each failure into a rule.
- Output is plain spoken text: no quotes, lists, emoji, stage directions, markdown, digits (numbers as words), no em dashes.
- Keep what already works; change what the failures show. Keep it under about 900 words.
Return: a short diagnosis of the failure patterns, the list of changes, and the full new prompt.`;

function rowBrief(r: Scored): string {
  const s = DIMS.map((d) => `${d}=${r.scores[d]}`).join(" ");
  return `- [${r.category}] card=${r.label} Q: ${r.question}\n  A: ${r.answer}\n  ${r.off_rails ? "OFF-RAILS " : ""}${s}; judge: ${r.rationale}${r.invented.length ? `; invented: ${r.invented.join("; ")}` : ""}${r.tells.length ? `; tells: ${r.tells.join(", ")}` : ""}`;
}

async function rewrite(best: Round, history: Round[], qs: Question[]): Promise<{ prompt: string; note: string }> {
  const trainRows = best.rows.filter((_, i) => !isHoldout(qs[i]!, i));
  const bad = worst(trainRows, 14);
  const good = [...trainRows].sort((a, b) => b.weighted - a.weighted).slice(0, 4);
  const guide = existsSync(VOICE_GUIDE_PATH) ? readFileSync(VOICE_GUIDE_PATH, "utf8") : "";
  const hist = history
    .map((h) => `- ${h.name}: train ${h.train.weighted.toFixed(2)}, off-rails ${h.agg.offRails}${h.note ? ` (${h.note.slice(0, 300)})` : ""}`)
    .join("\n");
  const means = DIMS.map((d) => `${d} ${best.train.mean[d].toFixed(2)}`).join(", ");
  const cats = Object.entries(best.train.byCategory)
    .map(([c, v]) => `${c} ${v.weighted.toFixed(2)} (${v.off} off-rails)`)
    .join(", ");
  const user = `${guide ? `TEAM WRITING GUIDE (voice bible):\n${guide}\n\n` : ""}CURRENT BEST PROMPT (${best.name}):\n<<<\n${best.prompt}\n>>>\n\nTRAIN SCORES (1-5): ${means}; off-rails ${best.train.offRails}/${best.train.n}; avg words ${best.train.avgWords.toFixed(1)}\nBy category: ${cats}\n\nPREVIOUS ATTEMPTS:\n${hist}\n\nWORST TRAIN ANSWERS:\n${bad.map(rowBrief).join("\n")}\n\nBEST TRAIN ANSWERS (keep this working):\n${good.map(rowBrief).join("\n")}`;
  const text = await call({
    model: REWRITE_MODEL,
    tag: "rewrite",
    system: REWRITE_SYSTEM,
    maxTokens: 16000,
    effort: "high",
    schema: REWRITE_SCHEMA,
    messages: [{ role: "user", content: user }],
  });
  const j = JSON.parse(text) as { diagnosis: string; changes: string[]; prompt: string };
  return { prompt: j.prompt.trim(), note: `${j.diagnosis} | ${j.changes.join("; ")}` };
}

// ---------------------------------------------------------------- apply

export function applyPrompt(prompt: string): boolean {
  if (!existsSync(SRC_PROMPT)) return false;
  const src = readFileSync(SRC_PROMPT, "utf8");
  const rx = /(export const SCOUT_ASK_SYSTEM(?:\s*:\s*string)?\s*=\s*)`(?:\\[\s\S]|\$\{[^}]*\}|[^`\\])*`/;
  if (!rx.test(src)) throw new Error("SCOUT_ASK_SYSTEM template literal not found");
  const lit = "`" + prompt.replace(/\\/g, "\\\\").replace(/`/g, "\\`").replace(/\$\{/g, "\\${") + "`";
  writeFileSync(SRC_PROMPT, src.replace(rx, (_m, head: string) => head + lit));
  return true;
}

// ---------------------------------------------------------------- main

function printRound(r: Round, all: Round[]): void {
  console.log(`\n=== ${r.name} ===${r.note ? `\nchanges: ${r.note.slice(0, 600)}` : ""}`);
  console.log(tableRows(all.map((x) => [x.name, x.agg])));
  console.log(`holdout (15, never shown to the rewriter): ${all.map((x) => `${x.name} ${x.hold.weighted.toFixed(2)}`).join(" | ")}`);
  console.log(categoryTable(all.map((x) => [x.name, x.agg])));
  console.log(`5 worst (${r.name}):`);
  for (const w of worst(r.rows)) console.log(describe(w));
}

if (import.meta.main) {
  const qs = await generate();
  const { mod, from } = await loadPromptModule();
  console.log(`questions ${qs.length}; prompt module: ${from}; answers ${ANSWER_MODEL}; judge/rewrite ${REWRITE_MODEL}`);
  const all: Round[] = [];
  const base = await runPrompt("r0-baseline", mod.SCOUT_ASK_SYSTEM, mod.buildAskMessages, qs, `baseline from ${from}`);
  all.push(base);
  printRound(base, all);
  if (flag("--prod")) {
    const p = await runProd(qs, mod.SCOUT_ASK_SYSTEM);
    if (p) {
      all.push(p);
      printRound(p, all);
    } else console.log("(--prod: hive/src/scout-ask.ts not found, skipped)");
  }
  let best = base;
  const rounds = opt("--rounds", 4);
  for (let i = 1; i <= rounds; i++) {
    const { prompt, note } = await rewrite(best, all.filter((r) => r.name !== "prod"), qs);
    const r = await runPrompt(`r${i}`, prompt, mod.buildAskMessages, qs, note);
    all.push(r);
    printRound(r, all);
    if (r.agg.weighted > best.agg.weighted) best = r;
    console.log(`best so far: ${best.name} (${best.agg.weighted.toFixed(2)})  spend this run ~$${costEstimate().toFixed(2)}`);
  }
  writeFileSync(join(DIR, "best_prompt.txt"), best.prompt + "\n");
  writeJson(join(DIR, "summary.json"), {
    best: best.name,
    rounds: all.map((r) => ({ name: r.name, all: r.agg, train: r.train.weighted, holdout: r.hold.weighted, note: r.note })),
  });
  console.log(`\n=== before / after ===\n${tableRows([[base.name, base.agg], [best.name, best.agg]])}`);
  console.log(`holdout: ${base.hold.weighted.toFixed(2)} -> ${best.hold.weighted.toFixed(2)}`);
  if (flag("--apply")) console.log(applyPrompt(best.prompt) ? "applied to hive/src/scout-ask-prompt.ts" : "no hive/src/scout-ask-prompt.ts to apply to");
  const u = usageSummary();
  console.log(`calls ${u.calls} (cached ${u.cached}); spend this run ~$${costEstimate().toFixed(2)}`);
}
