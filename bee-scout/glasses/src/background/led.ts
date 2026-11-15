// The glasses LED as a scan indicator. Purely cosmetic: every failure is swallowed, and after
// a few failures in a row (no LIGHT on these glasses, old host) it stops trying.
import type {LedModule} from "@mentra/miniapp/background"
import type {Verdict} from "./protocol"
import {describe, type Log} from "./util"

export class Led {
  private failures = 0

  constructor(
    private led: Pick<LedModule, "turnOn" | "turnOff">,
    private log: Log,
  ) {}

  /** Amber has no LED name; orange is the closest. Blinks for up to ~20 s. */
  scanning(): void {
    this.run(() => this.led.turnOn({color: "orange", ontime: 250, offtime: 250, count: 40}))
  }

  verdict(v: Verdict): void {
    if (v === "good") this.run(() => this.led.turnOn({color: "green", ontime: 2500, offtime: 0, count: 1}))
    else if (v === "roast") this.run(() => this.led.turnOn({color: "blue", ontime: 300, offtime: 200, count: 4}))
    else this.run(() => this.led.turnOn({color: "red", ontime: 200, offtime: 200, count: 6}))
  }

  off(): void {
    this.run(() => this.led.turnOff())
  }

  private run(cmd: () => Promise<unknown>): void {
    if (this.failures >= 3) return
    let p: Promise<unknown>
    try {
      p = cmd()
    } catch (err) {
      p = Promise.reject(err)
    }
    p.then(
      () => (this.failures = 0),
      (err) => {
        this.failures++
        this.log(`led: ${describe(err)}${this.failures >= 3 ? " (giving up on the LED)" : ""}`)
      },
    )
  }
}
