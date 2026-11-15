// Copy to src/config.local.ts (git ignores it) and fill in. `bun run build` / `bun dev`
// create it from this file if it's missing. Never commit a real token: the repo is public.
//
// Note: whatever you put here is bundled into the miniapp. That's fine for a LAN
// `mentra-miniapp release`, but never `pack` and host this build on a public URL.
import type {LocalConfig} from "./background/config"

export const LOCAL: LocalConfig = {
  // HIVE_TOKEN from hive/.env on the host (ask Rome / Discord).
  hiveToken: "",
  // Standalone audio field guide; button scans work without the Quest's game beat.
  fieldGuideMode: true,

  // Everything else is optional; see DEFAULTS in src/background/config.ts. Examples:
  // hosts: ["https://beeswax.drori.dev"],   // force pop-os only (no venue laptop tonight)
  // speechFirst: true,                       // the 1:30 AM scan-gate fallback
  // photo: {size: "medium"},

  // Live Bee Scout (scout/live): stream the camera to the laptop's MediaMTX the whole time.
  // scoutMode: true,
  // streamUrl: "rtmp://192.168.x.y:1935/scout",   // the laptop's LAN ip (ipconfig getifaddr en0)
  // hosts: ["http://192.168.x.y:8241"],            // the scout/live Hive on the same laptop
}
