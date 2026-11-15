#!/usr/bin/env bun
// Benchmark the scan director on real prop photos against Haiku 5.5 and Sonnet 5.5 (paid API calls).
//   bun scripts/bench-director.ts --dir bench/photos [--runs 3] [--heard "it's a rose"]
//        [--models claude-haiku-5-5,claude-sonnet-5-5] [--timeout 15000] [--beat beeline] [--json out/bench.json]
// Photos are named <expected_item>__anything.jpg (expected_item is one of protocol ITEMS).
// Runs are sequential so latency isn't distorted; each model is pre-warmed first.
import { readdirSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { basename, dirname, extname, join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { judge, prewarm } from "../src/director";
import { BEATS, ITEMS, type BeatName, type Item } from "../src/protocol";

const { values: args } = parseArgs({
  options: {
    dir: { type: "string", default: "bench/photos" },
    runs: { type: "string", default: "3" },
    heard: { type: "string" },
    models: { type: "string", default: "claude-haiku-5-5,claude-sonnet-5-5" },
    timeout: { type: "string", default: "15000" },
    beat: { type: "string", default: "beeline" },
    json: { type: "string" },
  },
});

const MIME: Record<string, string> = { ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp" };

interface Row {
  model: string;
  served: string;
  file: string;
  expected: Item;
  got: Item;
  verdict: string;
  ms: number;
  fallback: boolean;
  error?: string;
  cache_read?: number;
  line: string | null;
}

function pct(sorted: number[], p: number): number {
  if (!sorted.length) return NaN;
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1))]!;
}

function table(header: string[], rows: (string | number)[][]): string {
  const cells = [header, ...rows].map((r) => r.map(String));
  const widths = header.map((_, i) => Math.max(...cells.map((r) => r[i]!.length)));
  const fmt = (r: string[]) => `| ${r.map((c, i) => c.padEnd(widths[i]!)).join(" | ")} |`;
  return [fmt(cells[0]!), `|${widths.map((w) => "-".repeat(w + 2)).join("|")}|`, ...cells.slice(1).map(fmt)].join("\n");
}

async function main(): Promise<void> {
  const dir = resolve(args.dir!);
  const runs = Math.max(1, Number(args.runs) || 1);
  const timeoutMs = Math.max(1000, Number(args.timeout) || 15000);
  const models = args.models!.split(",").map((m) => m.trim()).filter(Boolean);
  if (!(BEATS as readonly string[]).includes(args.beat!)) throw new Error(`--beat must be one of ${BEATS.join(", ")}`);
  const beat = args.beat as BeatName;
  if (!process.env.ANTHROPIC_API_KEY && !process.env.ANTHROPIC_AUTH_TOKEN) {
    console.warn("ANTHROPIC_API_KEY is not set; relying on other credential sources (ant auth profile).");
  }

  const photos = readdirSync(dir)
    .filter((f) => MIME[extname(f).toLowerCase()])
    .map((f) => ({ file: f, expected: f.split("__")[0] as Item }))
    .filter((p) => {
      const ok = (ITEMS as readonly string[]).includes(p.expected);
      if (!ok) console.warn(`skipping ${p.file}: "${p.expected}" is not an item (name files <item>__x.jpg)`);
      return ok;
    });
  if (!photos.length) throw new Error(`no <item>__*.jpg photos in ${dir}`);
  console.log(`${photos.length} photos x ${runs} runs x ${models.length} models, timeout ${timeoutMs} ms, beat ${beat}\n`);

  const rows: Row[] = [];
  for (const model of models) {
    await prewarm(model);
    for (let run = 1; run <= runs; run++) {
      for (const p of photos) {
        const r = await judge(
          {
            photo: new Uint8Array(readFileSync(join(dir, p.file))),
            mime: MIME[extname(p.file).toLowerCase()],
            heard: args.heard,
            beat,
            memory: { scanned: [] },
          },
          { model, timeoutMs },
        );
        rows.push({
          model, served: r.model, file: p.file, expected: p.expected, got: r.item, verdict: r.verdict, ms: r.ms,
          fallback: r.fallback, error: r.error, cache_read: r.usage?.cache_read, line: r.line,
        });
        const mark = r.fallback ? `FALLBACK(${r.error})` : r.item === p.expected ? "ok" : `MISS -> ${r.item}`;
        console.log(`${model} run${run} ${p.file}: ${mark} ${r.ms}ms${r.line ? `  "${r.line}"` : ""}`);
      }
    }
  }

  console.log("\n## Summary\n");
  const summary = models.map((model) => {
    const mine = rows.filter((r) => r.model === model);
    const ms = mine.filter((r) => !r.fallback).map((r) => r.ms).sort((a, b) => a - b);
    const reads = mine.map((r) => r.cache_read ?? 0);
    const errors = [...new Set(mine.filter((r) => r.fallback).map((r) => r.error))].join(" ");
    return [
      model,
      mine.length,
      `${((100 * mine.filter((r) => r.got === r.expected).length) / mine.length).toFixed(0)}%`,
      `${mine.filter((r) => r.fallback).length}${errors ? ` (${errors})` : ""}`,
      pct(ms, 0.5),
      pct(ms, 0.9),
      ms.length ? ms[ms.length - 1]! : NaN,
      Math.round(reads.reduce((a, b) => a + b, 0) / Math.max(1, reads.length)),
    ];
  });
  console.log(table(["model", "n", "accuracy", "fallbacks", "p50 ms", "p90 ms", "max ms", "avg cache_read"], summary));

  console.log("\n## Accuracy by expected item\n");
  const items = [...new Set(photos.map((p) => p.expected))];
  console.log(
    table(
      ["item", ...models],
      items.map((item) => [
        item,
        ...models.map((model) => {
          const mine = rows.filter((r) => r.model === model && r.expected === item);
          return `${mine.filter((r) => r.got === item).length}/${mine.length}`;
        }),
      ]),
    ),
  );

  if (args.json) {
    mkdirSync(dirname(resolve(args.json)), { recursive: true });
    writeFileSync(resolve(args.json), JSON.stringify({ at: new Date().toISOString(), dir: basename(dir), runs, rows }, null, 2));
    console.log(`\nRaw rows: ${resolve(args.json)}`);
  }
}

if (import.meta.main) {
  main().catch((e) => {
    console.error(`bench failed: ${e instanceof Error ? e.message : String(e)}`);
    process.exit(1);
  });
}
