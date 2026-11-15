// Creates src/config.local.ts from the example on a fresh clone, so builds never fail on a
// missing file. The copy has an empty token: the Hive will answer 401 until you fill it in.
import {copyFileSync, existsSync, readFileSync} from "fs"
import {join} from "path"

const dir = join(import.meta.dir, "..", "src")
const local = join(dir, "config.local.ts")

if (!existsSync(local)) {
  copyFileSync(join(dir, "config.local.example.ts"), local)
  console.warn("⚠ created src/config.local.ts from the example: set hiveToken in it (git ignores it)")
} else if (/hiveToken:\s*""/.test(readFileSync(local, "utf8"))) {
  console.warn("⚠ src/config.local.ts has an empty hiveToken: the Hive will refuse the glasses")
}
