// Stand-in for hive/src/scout-ask-prompt.ts + scout-ask.ts (branch scout/ask) with the same exports, so the eval harness
// runs before that branch lands. run_eval.ts prefers the real modules when they exist (merge origin/scout/ask).
import Anthropic from "@anthropic-ai/sdk";

export interface ScoutAskCtx {
  label: string;
  facts: string[];
  sources: string[];
  vettedFacts: string[];
}

export const SCOUT_ASK_SYSTEM = `You are Beeswax, a grumpy-but-lovable cartoon bee who lives in the wearer's smart glasses at the Live Bee Scout booth. Kids and judges look at a station card (sunflower, honey, bug spray, bee hotel, water dish, beehive) and you just told them a bee fact about it. Now they asked a follow-up question out loud, and your answer is spoken in your voice.

Answer as Beeswax: short, fuzzy, a little grumpy, kid-friendly, bee puns welcome. One or two short sentences, under 35 words, spoken words only (no lists, emoji or stage directions).
Use only the facts provided in the message. If you don't know, say so in character. Never say you are an AI. Stay kid-safe always.`;

export function buildAskMessages(question: string, ctx: ScoutAskCtx): Anthropic.MessageParam[] {
  const text = [
    `Card: ${ctx.label}`,
    `Facts you just said:\n${ctx.facts.map((f) => `- ${f}`).join("\n")}`,
    `Fact sheet (the only other facts you may use):\n${ctx.vettedFacts.map((f) => `- ${f}`).join("\n")}`,
    `Question (speech-to-text, may be garbled): ${question}`,
  ].join("\n\n");
  return [{ role: "user", content: text }];
}

let client: Anthropic | null = null;

export async function answerText(question: string, ctx: ScoutAskCtx): Promise<string> {
  client ??= new Anthropic();
  const msg = await client.messages.create({
    model: "claude-haiku-5-5",
    max_tokens: 200,
    system: SCOUT_ASK_SYSTEM,
    messages: buildAskMessages(question, ctx),
    thinking: { type: "disabled" },
    output_config: { effort: "low" },
  });
  return msg.content.flatMap((b) => (b.type === "text" ? [b.text] : [])).join(" ").trim();
}
