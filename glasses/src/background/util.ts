export type Log = (...args: unknown[]) => void

export const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms))

export class TimeoutError extends Error {
  constructor(what: string, ms: number) {
    super(`${what} timed out after ${ms} ms`)
    this.name = "TimeoutError"
  }
}

/** Rejects with TimeoutError after `ms`. The underlying work keeps running; callers ignore it. */
export function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new TimeoutError(what, ms)), ms)
  })
  return Promise.race([p, timeout]).finally(() => clearTimeout(timer))
}

/** SDK errors are plain `{code, message}` objects, not Error instances. */
export function describe(err: unknown): string {
  if (err instanceof Error) return err.message
  if (err && typeof err === "object") {
    const e = err as {code?: unknown; message?: unknown}
    if (e.code || e.message) return [e.code, e.message].filter(Boolean).join(": ")
  }
  return String(err)
}

export interface Deferred<T> {
  promise: Promise<T>
  resolve: (value: T) => void
}

/** A promise you resolve from outside; extra resolves are no-ops. */
export function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => (resolve = r))
  return {promise, resolve}
}
