// Which glasses build is live: logged on start (and mirrored to the Hive log in scout mode).
// Bump BUILD_TAG with any change you need to confirm on the phone.
import manifest from "../../miniapp.json"

export const APP_VERSION: string = (manifest as {version?: string}).version ?? "?"
export const BUILD_TAG = "scout-ask-4 (wake word at start, queued lines play in order, no double play)"
