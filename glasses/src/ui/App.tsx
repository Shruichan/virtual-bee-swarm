import {useEffect, useState} from "react"
import {MiniappHeader, useSafeArea} from "@mentra/miniapp/ui"
import type {GlassesStatus} from "../shared/channels"

const HOP_ORDER = ["press_ack", "photo", "scan_rtt", "press_to_verdict", "press_to_audio"]

export function App() {
  const {insets} = useSafeArea()
  const [status, setStatus] = useState<GlassesStatus | null>(null)

  useEffect(() => mentra.on("status", setStatus), [])

  return (
    <div
      className="app"
      style={{
        paddingTop: insets.top,
        paddingBottom: insets.bottom,
        paddingLeft: insets.left,
        paddingRight: insets.right,
      }}>
      <MiniappHeader title="Beeswax" />
      {status ? <Status s={status} /> : <p className="muted pad">Waiting for the background…</p>}
    </div>
  )
}

function Status({s}: {s: GlassesStatus}) {
  const hops = Object.keys(s.latency).sort((a, b) => rank(a) - rank(b))
  return (
    <main className="content">
      <section className="card">
        <Row label="Hive">
          <span className={`dot ${s.bus}`} /> {s.bus === "open" ? s.host.replace(/^https?:\/\//, "") : s.bus}
        </Row>
        <Row label="Beat">{s.beat ?? "unknown"}</Row>
        <Row label="Glasses">{s.glasses == null ? "unknown" : s.glasses ? "connected" : "not connected"}</Row>
        <Row label="Cloud">{s.cloud}</Row>
        {!s.tokenSet && <p className="warn">No hiveToken in src/config.local.ts</p>}
      </section>

      <div className="buttons">
        <button type="button" disabled={s.scanning} onClick={() => mentra.send("press", {at: Date.now()})}>
          {s.scanning ? "Scanning…" : "Test scan"}
        </button>
        <button type="button" className="secondary" onClick={() => mentra.send("replay", {at: Date.now()})}>
          Replay
        </button>
      </div>

      <section className="card">
        <h2>Last line</h2>
        {s.lastLine ? (
          <>
            <p className="caption">“{s.lastLine.caption || "…"}”</p>
            <p className="muted small">
              {s.lastLine.id ?? "live"} · {new Date(s.lastLine.at).toLocaleTimeString()}
            </p>
          </>
        ) : (
          <p className="muted">Nothing yet</p>
        )}
      </section>

      <section className="card">
        <h2>Latency</h2>
        {hops.length ? (
          hops.map((hop) => (
            <Row key={hop} label={hop}>
              <span className={s.latency[hop]! > 6000 ? "slow" : ""}>{(s.latency[hop]! / 1000).toFixed(2)} s</span>
            </Row>
          ))
        ) : (
          <p className="muted">No scans yet</p>
        )}
      </section>

      <section className="card">
        <h2>Log</h2>
        <pre className="log">{[...s.log].reverse().join("\n")}</pre>
      </section>
    </main>
  )
}

function Row({label, children}: {label: string; children: React.ReactNode}) {
  return (
    <div className="row">
      <span className="muted">{label}</span>
      <span>{children}</span>
    </div>
  )
}

function rank(hop: string): number {
  const i = HOP_ORDER.indexOf(hop)
  return i === -1 ? HOP_ORDER.length : i
}
