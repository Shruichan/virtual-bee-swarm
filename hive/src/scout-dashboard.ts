// Live Bee Scout pages: the /scout dashboard and the /scout/slides screen test.
// STUB from the orchestrator so the game lane can serve the routes; the dashboard lane (scout/dashboard)
// replaces the bodies. Keep the two export names and signatures.

export function scoutDashboardHtml(): string {
  return `<!doctype html><meta charset="utf-8"><title>Bee Scout</title><body style="font:16px system-ui;background:#111;color:#eee"><h1>Bee Scout dashboard (stub)</h1><p>Events: <code>/scout/events?token=…</code></p></body>`;
}

export function scoutSlidesHtml(): string {
  return `<!doctype html><meta charset="utf-8"><title>Bee Scout slides</title><body style="font:16px system-ui;background:#000;color:#eee"><h1>Bee Scout slides (stub)</h1></body>`;
}
