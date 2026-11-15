// Talking to the Hive: HTTPS calls with host failover, and the wss bus with reconnect.
// Both always use the same "active" host so the glasses never split from the Quest's Hive.
// No URL/URLSearchParams/Headers here: the background runtime doesn't have them.
import {PROTOCOL_VERSION, type BusMessage} from "./protocol"
import {describe, withTimeout, type Log} from "./util"

export class HttpError extends Error {
  constructor(
    readonly status: number,
    readonly body: string,
  ) {
    super(`HTTP ${status}${body ? `: ${body.slice(0, 120)}` : ""}`)
    this.name = "HttpError"
  }
}

type Fetch = (url: string, init?: {method?: string; headers?: Record<string, string>; body?: string}) => Promise<{
  status: number
  text(): Promise<string>
}>

export class Hive {
  private active = 0
  /** Fires when the active host changes (the bus reconnects to it). */
  onHostChange: (host: string) => void = () => {}

  constructor(
    readonly hosts: string[],
    private token: string,
    private log: Log,
    private fetchFn: Fetch = (url, init) => fetch(url, init),
  ) {}

  get host(): string {
    return this.hosts[this.active]!
  }

  get activeIndex(): number {
    return this.active
  }

  /** `notify: false` when the bus itself is moving (it reconnects on its own schedule). */
  setActive(i: number, notify = true): void {
    if (i === this.active || !this.hosts[i]) return
    this.active = i
    this.log(`hive: active host -> ${this.host}`)
    if (notify) this.onHostChange(this.host)
  }

  url(host: string, path: string, query = ""): string {
    return `${host}${path}?token=${encodeURIComponent(this.token)}${query}`
  }

  busUrl(): string {
    return this.url(this.host.replace(/^http/, "ws"), "/bus", "&role=glasses")
  }

  /**
   * POST JSON to the active host; on a network error, timeout or 5xx, try the other hosts in
   * order and make the first one that answers active. 4xx is final (same token everywhere).
   */
  async post<T>(path: string, body: unknown, timeoutMs: number): Promise<T> {
    let lastErr: unknown
    for (let n = 0; n < this.hosts.length; n++) {
      const i = (this.active + n) % this.hosts.length
      const host = this.hosts[i]!
      try {
        const res = await withTimeout(
          this.fetchFn(this.url(host, path), {
            method: "POST",
            headers: {"content-type": "application/json"},
            body: JSON.stringify(body),
          }),
          timeoutMs,
          `POST ${path}`,
        )
        const text = await withTimeout(res.text(), timeoutMs, `POST ${path} body`)
        if (res.status >= 500) throw new HttpError(res.status, text)
        if (res.status >= 300) throw Object.assign(new HttpError(res.status, text), {final: true})
        this.setActive(i)
        return (text ? JSON.parse(text) : {}) as T
      } catch (err) {
        if ((err as {final?: boolean}).final) throw err
        lastErr = err
        this.log(`hive: POST ${path} via ${host} failed: ${describe(err)}`)
      }
    }
    throw lastErr
  }

  /** Fire-and-forget POST (latency, plea): one try on the active host, errors only logged. */
  send(path: string, body: unknown, timeoutMs = 4000): Promise<boolean> {
    return withTimeout(
      this.fetchFn(this.url(this.host, path), {
        method: "POST",
        headers: {"content-type": "application/json"},
        body: JSON.stringify(body),
      }),
      timeoutMs,
      `POST ${path}`,
    ).then(
      (res) => res.status < 300,
      (err) => {
        this.log(`hive: POST ${path} failed: ${describe(err)}`)
        return false
      },
    )
  }

  /** Is this host's Hive up? Its /health route; any non-5xx answer counts (Cloudflare says 502/530 when it's down). */
  async probe(i: number, timeoutMs = 3000): Promise<boolean> {
    const host = this.hosts[i]
    if (!host) return false
    try {
      const res = await withTimeout(this.fetchFn(this.url(host, "/health")), timeoutMs, "probe")
      return res.status < 500
    } catch {
      return false
    }
  }
}

export type BusState = "connecting" | "open" | "closed"

const KEEPALIVE_MS = 15_000 // hello -> welcome round trip; also keeps Bun's idle timeout away
const STALE_MS = 45_000 // nothing heard for this long = dead socket, reconnect
const PROBE_PRIMARY_MS = 30_000
const OPEN_TIMEOUT_MS = 10_000
const FAILURES_BEFORE_ROTATE = 2

/** The Hive bus as role=glasses. Reconnects forever until stop(). */
export class Bus {
  state: BusState = "closed"
  onMessage: (msg: BusMessage) => void = () => {}
  onState: (state: BusState) => void = () => {}

  private ws: WebSocket | null = null
  private stopped = true
  private failures = 0
  private lastRx = 0
  private timers: ReturnType<typeof setInterval>[] = []
  private retry: ReturnType<typeof setTimeout> | null = null

  constructor(
    private hive: Hive,
    private log: Log,
    private client = "beeswax-miniapp",
  ) {
    hive.onHostChange = () => this.reconnect("host changed")
  }

  start(): void {
    if (!this.stopped) return
    this.stopped = false
    this.connect()
    this.timers.push(
      setInterval(() => this.keepalive(), KEEPALIVE_MS),
      setInterval(() => void this.probePrimary(), PROBE_PRIMARY_MS),
    )
  }

  stop(): void {
    this.stopped = true
    for (const t of this.timers) clearInterval(t)
    this.timers = []
    if (this.retry) clearTimeout(this.retry)
    this.retry = null
    this.drop()
    this.setState("closed")
  }

  send(msg: BusMessage): boolean {
    if (this.state !== "open" || !this.ws) return false
    try {
      this.ws.send(JSON.stringify(msg))
      return true
    } catch (err) {
      this.log(`bus: send failed: ${describe(err)}`)
      return false
    }
  }

  private connect(): void {
    if (this.stopped) return
    this.drop()
    this.setState("connecting")
    let ws: WebSocket
    try {
      ws = new WebSocket(this.hive.busUrl())
    } catch (err) {
      this.log(`bus: can't open socket: ${describe(err)}`)
      return this.scheduleRetry()
    }
    this.ws = ws
    ws.onopen = () => {
      if (ws !== this.ws) return
      this.failures = 0
      this.lastRx = Date.now()
      this.setState("open")
      this.log(`bus: connected to ${this.hive.host}`)
      this.send({type: "hello", role: "glasses", client: this.client, version: PROTOCOL_VERSION})
    }
    ws.onmessage = (ev: {data: unknown}) => {
      if (ws !== this.ws) return
      this.lastRx = Date.now()
      if (typeof ev.data !== "string") return // binary frames are for the Quest
      let msg: unknown
      try {
        msg = JSON.parse(ev.data)
      } catch {
        return this.log("bus: ignoring bad JSON")
      }
      if (msg && typeof msg === "object" && typeof (msg as {type?: unknown}).type === "string") {
        try {
          this.onMessage(msg as BusMessage)
        } catch (err) {
          this.log(`bus: handler error: ${describe(err)}`)
        }
      }
    }
    // Some native bridges fire error without a close (or neither): treat both as fatal, and
    // give up on a socket that hasn't opened in OPEN_TIMEOUT_MS.
    ws.onerror = () => {
      if (ws !== this.ws) return
      this.log("bus: socket error")
      this.drop()
      this.scheduleRetry()
    }
    ws.onclose = (ev: {code?: number; reason?: string}) => {
      if (ws !== this.ws) return
      this.ws = null
      this.log(`bus: closed (${ev?.code ?? "?"}${ev?.reason ? ` ${ev.reason}` : ""})`)
      this.scheduleRetry()
    }
    setTimeout(() => {
      if (ws !== this.ws || this.state === "open") return
      this.log(`bus: no answer from ${this.hive.host}`)
      this.drop()
      this.scheduleRetry()
    }, OPEN_TIMEOUT_MS)
  }

  private scheduleRetry(): void {
    this.setState("closed")
    if (this.stopped || this.retry) return
    this.failures++
    if (this.failures % FAILURES_BEFORE_ROTATE === 0) {
      this.hive.setActive((this.hive.activeIndex + 1) % this.hive.hosts.length, false)
    }
    const delay = Math.min(5000, 500 * 2 ** Math.min(this.failures - 1, 4))
    this.retry = setTimeout(() => {
      this.retry = null
      this.connect()
    }, delay)
  }

  private reconnect(why: string): void {
    if (this.stopped) return
    this.log(`bus: reconnecting (${why})`)
    if (this.retry) clearTimeout(this.retry)
    this.retry = null
    this.connect()
  }

  private drop(): void {
    const ws = this.ws
    this.ws = null
    if (!ws) return
    ws.onopen = ws.onmessage = ws.onerror = ws.onclose = null
    try {
      ws.close()
    } catch {}
  }

  private keepalive(): void {
    if (this.state !== "open") return
    if (Date.now() - this.lastRx > STALE_MS) return this.reconnect("no traffic")
    this.send({type: "hello", role: "glasses", client: this.client, version: PROTOCOL_VERSION})
  }

  /** On a fallback host, move back to the primary as soon as its tunnel answers. */
  private async probePrimary(): Promise<void> {
    if (this.stopped || this.hive.activeIndex === 0) return
    if (await this.hive.probe(0)) {
      this.log("bus: primary host is back")
      this.hive.setActive(0)
    }
  }

  private setState(state: BusState): void {
    if (state === this.state) return
    this.state = state
    this.onState(state)
  }
}

