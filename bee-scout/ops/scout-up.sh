#!/usr/bin/env bash
# Live Bee Scout: bring the whole laptop side up with one command, check it, open the pages.
# All paths are relative to the bee-scout/ folder (this script lives in bee-scout/ops/).
#
#   bee-scout/ops/scout-up.sh            start whatever is not running yet, health-check, open dashboard + judge view
#   bee-scout/ops/scout-up.sh --no-open  same, but don't open the browser
#   bee-scout/ops/scout-up.sh --qr       also open the glasses dev QR (scan it in the Mentra app)
#   bee-scout/ops/scout-down.sh          stop only what this script started
#
# Services (each skipped with "already running" if its port is listening; nothing is ever killed here):
#   mediamtx  RTMP in from the glasses, RTSP to the worker, WebRTC to the dashboard  (ops/logs/mediamtx.log)
#   hive      scout Hive (bun) on SCOUT_HIVE_PORT                                    (ops/logs/hive.log)
#   vision    vision worker (python) on SCOUT_VISION_PORT                            (ops/logs/vision.log)
#   glasses   mentra-miniapp dev server for the phone (QR)                           (ops/logs/glasses.log)
#
# Env overrides (defaults = SCOUT_PORTS in hive/src/scout-protocol.ts):
#   SCOUT_HIVE_PORT=8241 SCOUT_VISION_PORT=8242 SCOUT_RTMP_PORT=1935 SCOUT_RTSP_PORT=8554
#   SCOUT_WEBRTC_PORT=8889 SCOUT_SRT_PORT=8890 SCOUT_RTP_PORT=8000 (RTCP = +1) SCOUT_WEBRTC_UDP_PORT=8189
#   SCOUT_GLASSES_PORT=3000 (mentra-miniapp takes the next free port if busy)
#   SCOUT_NO_GLASSES=1 SCOUT_NO_VISION=1   skip those pieces
#   SCOUT_PRACTICE=1                       Hive starts in practice mode (default 1; 0 = off)
#   SCOUT_REFS_FROM=<dir>                  copy refs/ + data/test from another scout-vision checkout (optional)
#   SCOUT_FETCH_REFS=1                     download the old DINO reference photos (not needed: cards use --match)
#   SCOUT_LAN_IP=<ip>                      instead of ipconfig getifaddr en0/en1
#   SCOUT_HEALTH_TIMEOUT=120               seconds to wait for health checks
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OPS="$ROOT/ops"
LOGS="$OPS/logs"
RUN="$OPS/run"
VISION_DIR="$ROOT/hive/scout-vision"

OPEN=1
QR=0
for a in "$@"; do
  case "$a" in
    --open) OPEN=1 ;;
    --no-open) OPEN=0 ;;
    --qr) QR=1 ;;
    -h | --help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown option: $a (try --help)" >&2; exit 2 ;;
  esac
done

HIVE_PORT="${SCOUT_HIVE_PORT:-8241}"
VISION_PORT="${SCOUT_VISION_PORT:-8242}"
RTMP_PORT="${SCOUT_RTMP_PORT:-1935}"
RTSP_PORT="${SCOUT_RTSP_PORT:-8554}"
WEBRTC_PORT="${SCOUT_WEBRTC_PORT:-8889}"
SRT_PORT="${SCOUT_SRT_PORT:-8890}"
RTP_PORT="${SCOUT_RTP_PORT:-8000}"
WEBRTC_UDP_PORT="${SCOUT_WEBRTC_UDP_PORT:-8189}"
GLASSES_PORT="${SCOUT_GLASSES_PORT:-3000}"
NO_GLASSES="${SCOUT_NO_GLASSES:-0}"
NO_VISION="${SCOUT_NO_VISION:-0}"
PRACTICE="${SCOUT_PRACTICE:-1}"
HEALTH_TIMEOUT="${SCOUT_HEALTH_TIMEOUT:-120}"
PKG="$(sed -n 's/.*"packageName": *"\([^"]*\)".*/\1/p' "$ROOT/glasses/miniapp.json" 2>/dev/null)"

say() { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

# pids listening on a TCP port (empty if none)
listeners() { lsof -nP -iTCP:"$1" -sTCP:LISTEN -t 2>/dev/null | sort -u; }
cwd_of() { lsof -a -p "$1" -d cwd -Fn 2>/dev/null | sed -n 's/^n//p'; }
mask() { sed -E 's/token=[^&[:space:]]*/token=***/g'; }

# ------------------------------------------------------------------ 1. tools
step "tools"
missing=()
for t in bun uv mediamtx ffmpeg curl lsof openssl; do
  command -v "$t" >/dev/null 2>&1 || missing+=("$t")
done
if ((${#missing[@]})); then
  say "missing: ${missing[*]}"
  for t in "${missing[@]}"; do
    case "$t" in
      bun) say "  brew install oven-sh/bun/bun" ;;
      *) say "  brew install $t" ;;
    esac
  done
  exit 1
fi
say "ok: bun uv mediamtx ffmpeg"

# ------------------------------------------------------------------ 2. LAN ip
IP="${SCOUT_LAN_IP:-$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)}"
if [[ -z "$IP" ]]; then
  IP=127.0.0.1
  say "warning: no LAN ip on en0/en1, using 127.0.0.1 (the glasses won't reach it; set SCOUT_LAN_IP)"
fi
say "LAN ip: $IP"

mkdir -p "$LOGS" "$RUN"

# ------------------------------------------------------------------ 3. hive/.env (token never printed)
step "hive/.env"
ENV_FILE="$ROOT/hive/.env"
env_get() { [[ -f "$1" ]] && sed -n "s/^[[:space:]]*$2[[:space:]]*=[[:space:]]*//p" "$1" | tail -1 | tr -d "\"'" ; }
TOKEN="$(env_get "$ENV_FILE" HIVE_TOKEN)"
if [[ -z "$TOKEN" ]]; then
  TOKEN="$(openssl rand -hex 16)"
  touch "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  {
    [[ -s "$ENV_FILE" && -n "$(tail -c1 "$ENV_FILE")" ]] && echo
    echo "HIVE_TOKEN=$TOKEN"
    grep -q '^PORT=' "$ENV_FILE" || echo "PORT=$HIVE_PORT"
  } >>"$ENV_FILE"
  say "created a new HIVE_TOKEN in hive/.env"
else
  say "kept the existing HIVE_TOKEN"
fi
PUBLIC_BASE_URL="http://$IP:$HIVE_PORT"
if grep -q '^PUBLIC_BASE_URL=' "$ENV_FILE"; then
  sed -i '' "s|^PUBLIC_BASE_URL=.*|PUBLIC_BASE_URL=$PUBLIC_BASE_URL|" "$ENV_FILE"
else
  echo "PUBLIC_BASE_URL=$PUBLIC_BASE_URL" >>"$ENV_FILE"
fi
say "PUBLIC_BASE_URL=$PUBLIC_BASE_URL"

# If a Hive is already serving the port from another checkout, its token is the one that works.
ACTIVE_TOKEN="$TOKEN"
HIVE_PIDS="$(listeners "$HIVE_PORT")"
if [[ -n "$HIVE_PIDS" ]]; then
  hive_cwd="$(cwd_of "$(head -1 <<<"$HIVE_PIDS")")"
  if [[ -n "$hive_cwd" && "$hive_cwd" != "$ROOT/hive" ]]; then
    other="$(env_get "$hive_cwd/.env" HIVE_TOKEN)"
    if [[ -n "$other" && "$other" != "$TOKEN" ]]; then
      ACTIVE_TOKEN="$other"
      say "note: the Hive on :$HIVE_PORT runs from $hive_cwd; using its token for the glasses config, checks and URLs"
    fi
  fi
fi

# ------------------------------------------------------------------ 4. glasses/src/config.local.ts
if [[ "$NO_GLASSES" != 1 ]]; then
  step "glasses/src/config.local.ts"
  CFG="$ROOT/glasses/src/config.local.ts"
  MARK="Generated by ops/scout-up.sh"
  if [[ -f "$CFG" ]] && ! grep -q "$MARK" "$CFG"; then
    cp "$CFG" "$CFG.bak"
    say "backed up your hand-written config to config.local.ts.bak"
  fi
  umask 077
  cat >"$CFG" <<EOF
// $MARK (git-ignored; rewritten on every run).
import type {LocalConfig} from "./background/config"

export const LOCAL: LocalConfig = {
  hiveToken: "$ACTIVE_TOKEN",
  hosts: ["http://$IP:$HIVE_PORT"],
  fieldGuideMode: true,
  scoutMode: true,
  streamUrl: "rtmp://$IP:$RTMP_PORT/scout",
  voice: false,
}
EOF
  umask 022
  say "hosts http://$IP:$HIVE_PORT, streamUrl rtmp://$IP:$RTMP_PORT/scout, scoutMode on, voice off"
fi

# ------------------------------------------------------------------ 5. dependencies + vision data
# (only for the pieces that will actually start here)
step "dependencies"
NEED_HIVE=1 NEED_VISION=1 NEED_GLASSES=1
[[ -n "$(listeners "$HIVE_PORT")" ]] && NEED_HIVE=0
[[ "$NO_VISION" == 1 || -n "$(listeners "$VISION_PORT")" ]] && NEED_VISION=0
[[ "$NO_GLASSES" == 1 ]] && NEED_GLASSES=0
for p in $(pgrep -f "mentra-miniapp dev" 2>/dev/null); do
  [[ "$(cwd_of "$p")" == "$ROOT/glasses" ]] && NEED_GLASSES=0
done
for d in hive glasses; do
  [[ "$d" == hive && "$NEED_HIVE" == 0 ]] && continue
  [[ "$d" == glasses && "$NEED_GLASSES" == 0 ]] && continue
  if [[ ! -d "$ROOT/$d/node_modules" ]]; then
    say "bun install in $d/"
    (cd "$ROOT/$d" && bun install --frozen-lockfile >>"$LOGS/install.log" 2>&1) || die "bun install failed in $d/ (see ops/logs/install.log)"
  fi
done
if [[ "$NEED_VISION" == 1 ]]; then
  if [[ ! -x "$VISION_DIR/.venv/bin/python" ]]; then
    say "uv sync in hive/scout-vision (first time: downloads torch, a few minutes)"
  fi
  (cd "$VISION_DIR" && uv sync --frozen >>"$LOGS/install.log" 2>&1) || die "uv sync failed (see ops/logs/install.log)"

  if [[ ! -d "$VISION_DIR/refs" ]] || [[ -z "$(ls -A "$VISION_DIR/refs" 2>/dev/null)" ]]; then
    if [[ -n "${SCOUT_REFS_FROM:-}" ]]; then
      src="${SCOUT_REFS_FROM%/}"
      [[ -d "$src/refs" ]] || die "SCOUT_REFS_FROM=$src has no refs/"
      say "copying refs/ and data/test from $src"
      mkdir -p "$VISION_DIR/refs" "$VISION_DIR/data/test" "$VISION_DIR/data/web"
      cp -R "$src/refs/." "$VISION_DIR/refs/"
      [[ -d "$src/data/test" ]] && cp -R "$src/data/test/." "$VISION_DIR/data/test/"
      [[ -f "$src/data/web/credits.csv" ]] && cp "$src/data/web/credits.csv" "$VISION_DIR/data/web/"
    elif [[ "${SCOUT_FETCH_REFS:-0}" == 1 ]]; then
      say "no refs/: downloading reference photos from Wikimedia Commons (a few minutes; log ops/logs/fetch_refs.log)"
      (cd "$VISION_DIR" && .venv/bin/python fetch_web_refs.py && .venv/bin/python fetch_web_refs.py --split) \
        >>"$LOGS/fetch_refs.log" 2>&1 || die "fetch_web_refs.py failed (see ops/logs/fetch_refs.log)"
    else
      say "no refs/: fine, the cards are recognized by the card matcher + OCR (detect/card_images)"
    fi
  fi
  say "vision refs: $(find "$VISION_DIR/refs" -name '*.jpg' 2>/dev/null | wc -l | tr -d ' ') photos, slides: $(find "$VISION_DIR/data/test" -type f 2>/dev/null | wc -l | tr -d ' ') photos"
fi

# ------------------------------------------------------------------ 6. start services
step "services"
declare -a ROWS=()
row() { ROWS+=("$(printf '%-9s %-6s %s' "$1" "$2" "$3")"); }

# start <name> <dir> <signature> [VAR=value ...] -- <cmd...>: background in its own process group,
# log + pid file. Variables are exported (not passed on argv), so the token never shows up in ps.
start() {
  local name="$1" dir="$2" sig="$3"
  shift 3
  printf '\n---- %s started by scout-up.sh\n' "$(date '+%F %T')" >>"$LOGS/$name.log"
  (
    cd "$dir" || exit 1
    while [[ $# -gt 0 && "$1" != "--" ]]; do export "${1?}"; shift; done
    shift
    set -m
    nohup "$@" </dev/null >>"$LOGS/$name.log" 2>&1 &
    printf '%s\t%s\n' "$!" "$sig" >"$RUN/$name.pid"
  )
}

# already <name> <port...>: true (and records the row) if any port is listening
already() {
  local name="$1" p pids
  shift
  for p in "$@"; do
    pids="$(listeners "$p")"
    if [[ -n "$pids" ]]; then
      row "$name" "$p" "already running (pid $(head -1 <<<"$pids"), $(cwd_of "$(head -1 <<<"$pids")"))"
      return 0
    fi
  done
  return 1
}

# MediaMTX: the checked-in config, with ports rewritten when any differ from the defaults
MTX_CFG="$OPS/scout-mediamtx.yml"
if [[ "$RTMP_PORT/$RTSP_PORT/$WEBRTC_PORT/$SRT_PORT/$RTP_PORT/$WEBRTC_UDP_PORT" != "1935/8554/8889/8890/8000/8189" ]]; then
  MTX_CFG="$RUN/scout-mediamtx.yml"
  sed -e "s/^rtmpAddress: .*/rtmpAddress: :$RTMP_PORT/" \
    -e "s/^rtspAddress: .*/rtspAddress: :$RTSP_PORT/" \
    -e "s/^webrtcAddress: .*/webrtcAddress: :$WEBRTC_PORT/" \
    -e "s/^srtAddress: .*/srtAddress: :$SRT_PORT/" \
    -e "s|rtsp://127.0.0.1:8554/|rtsp://127.0.0.1:$RTSP_PORT/|" \
    "$OPS/scout-mediamtx.yml" >"$MTX_CFG"
  cat >>"$MTX_CFG" <<EOF
# ports rewritten by ops/scout-up.sh
rtpAddress: :$RTP_PORT
rtcpAddress: :$((RTP_PORT + 1))
webrtcLocalUDPAddress: :$WEBRTC_UDP_PORT
EOF
fi
if ! already mediamtx "$RTMP_PORT" "$RTSP_PORT" "$WEBRTC_PORT"; then
  start mediamtx "$ROOT" mediamtx -- mediamtx "$MTX_CFG"
  row mediamtx "$RTMP_PORT" "started (rtmp :$RTMP_PORT rtsp :$RTSP_PORT webrtc :$WEBRTC_PORT)"
fi

if ! already hive "$HIVE_PORT"; then
  start hive "$ROOT/hive" src/server.ts PORT="$HIVE_PORT" HIVE_TOKEN="$TOKEN" PUBLIC_BASE_URL="$PUBLIC_BASE_URL" \
    SCOUT_PRACTICE="$PRACTICE" -- bun run src/server.ts
  row hive "$HIVE_PORT" "started (practice=$PRACTICE)"
fi

if [[ "$NO_VISION" == 1 ]]; then
  row vision "$VISION_PORT" "skipped (SCOUT_NO_VISION=1)"
elif ! already vision "$VISION_PORT"; then
  # Card matcher (SIFT on the six real-photo cards) + OCR of the trail-sign labels; no AprilTag markers.
  # The token goes in via the exported HIVE_TOKEN (worker.py's --token default), so it never shows up in ps.
  start vision "$VISION_DIR" worker.py HIVE_TOKEN="$ACTIVE_TOKEN" SCOUT_VISION_PORT="$VISION_PORT" \
    SCOUT_SOURCE="rtsp://127.0.0.1:$RTSP_PORT/scout" SCOUT_CAPTURE=ffmpeg \
    -- uv run --frozen python worker.py --match "$VISION_DIR/detect/card_images" --ocr --no-markers \
    --hive "http://127.0.0.1:$HIVE_PORT"
  row vision "$VISION_PORT" "started (card matcher + OCR)"
fi

# Glasses dev server: it picks the next free port, so "running" means a mentra-miniapp dev from THIS checkout.
GLASSES_PID=""
GLASSES_LISTEN=""
for p in $(pgrep -f "mentra-miniapp dev" 2>/dev/null); do
  if [[ "$(cwd_of "$p")" == "$ROOT/glasses" ]]; then GLASSES_PID="$p"; break; fi
done
if [[ "$NO_GLASSES" == 1 ]]; then
  row glasses "-" "skipped (SCOUT_NO_GLASSES=1)"
elif [[ -n "$GLASSES_PID" ]]; then
  GLASSES_LISTEN="$(lsof -nP -a -p "$GLASSES_PID" -iTCP -sTCP:LISTEN -Fn 2>/dev/null | sed -n 's/^n.*://p' | sort -n | head -1)"
  row glasses "${GLASSES_LISTEN:--}" "already running (pid $GLASSES_PID, $ROOT/glasses)"
else
  others="$(for p in $(pgrep -f "mentra-miniapp dev" 2>/dev/null); do cwd_of "$p"; done | sort -u | tr '\n' ' ')"
  [[ -n "$others" ]] && say "note: other glasses dev servers are running from: $others"
  start glasses "$ROOT/glasses" "mentra-miniapp dev" MENTRA_MINIAPP_DEV_PORT="$GLASSES_PORT" \
    -- node_modules/.bin/mentra-miniapp dev
  row glasses "$GLASSES_PORT+" "started (log ops/logs/glasses.log)"
fi

for r in "${ROWS[@]}"; do say "$r"; done

# ------------------------------------------------------------------ 7. health
step "health (up to ${HEALTH_TIMEOUT}s)"
# curl with the token in a header read from stdin, so it never shows up in ps
hive_get() { printf 'header = "x-hive-token: %s"\n' "$ACTIVE_TOKEN" | curl -fsS -m 3 -K - "http://127.0.0.1:$HIVE_PORT$1" 2>/dev/null; }
check() {
  case "$1" in
    hive) curl -fsS -m 3 "http://127.0.0.1:$HIVE_PORT/health" >/dev/null 2>&1 ;;
    state) hive_get /scout/state | grep -q '"phase"' ;;
    vision) curl -fsS -m 3 "http://127.0.0.1:$VISION_PORT/health" 2>/dev/null | grep -q '"ok": *true' ;;
    webrtc) [[ -n "$(listeners "$WEBRTC_PORT")" ]] ;;
    glasses)
      for p in $(pgrep -f "mentra-miniapp dev" 2>/dev/null); do
        if [[ "$(cwd_of "$p")" == "$ROOT/glasses" ]]; then
          GLASSES_PID="$p"
          GLASSES_LISTEN="$(lsof -nP -a -p "$p" -iTCP -sTCP:LISTEN -Fn 2>/dev/null | sed -n 's/^n.*://p' | sort -n | head -1)"
          [[ -n "$GLASSES_LISTEN" ]] && return 0
        fi
      done
      return 1 ;;
  esac
}
CHECKS=(webrtc hive state)
[[ "$NO_VISION" != 1 ]] && CHECKS+=(vision)
[[ "$NO_GLASSES" != 1 ]] && CHECKS+=(glasses)
OKS=" "
is_ok() { [[ "$OKS" == *" $1 "* ]]; }
deadline=$((SECONDS + HEALTH_TIMEOUT))
while :; do
  pending=0
  for c in "${CHECKS[@]}"; do
    is_ok "$c" && continue
    if check "$c"; then OKS+="$c "; else pending=1; fi
  done
  ((pending == 0 || SECONDS >= deadline)) && break
  sleep 1
done
label() {
  case "$1" in
    webrtc) echo "mediamtx  webrtc :$WEBRTC_PORT listening" ;;
    hive) echo "hive      GET :$HIVE_PORT/health" ;;
    state) echo "hive      GET :$HIVE_PORT/scout/state (token)" ;;
    vision) echo "vision    GET :$VISION_PORT/health" ;;
    glasses) echo "glasses   mentra-miniapp dev listening" ;;
  esac
}
fail=0
for c in "${CHECKS[@]}"; do
  if is_ok "$c"; then printf '  ok    %s\n' "$(label "$c")"; else printf '  FAIL  %s\n' "$(label "$c")"; fail=1; fi
done
if is_ok vision; then
  say "  vision: $(curl -fsS -m 3 "http://127.0.0.1:$VISION_PORT/health" | sed -E 's/.*"model": *"([^"]*)".*"connected": *([a-z]+).*/model \1, stream connected=\2/')"
fi
if is_ok hive && ! is_ok state; then
  say "  /scout/state failed: token mismatch (hive/.env vs the running Hive) or the scout routes are missing"
fi
((fail)) && say "  logs: $LOGS/<name>.log"

# ------------------------------------------------------------------ 8. URLs, QR
step "open these"
q=""
[[ "$WEBRTC_PORT" != 8889 ]] && q+="&webrtc=http://$IP:$WEBRTC_PORT"
[[ "$VISION_PORT" != 8242 ]] && q+="&vision=http://$IP:$VISION_PORT"
DASH="http://$IP:$HIVE_PORT/scout?token=$ACTIVE_TOKEN$q"
SLIDES="http://$IP:$HIVE_PORT/scout/slides?token=$ACTIVE_TOKEN$q"
SHOW="http://$IP:$HIVE_PORT/scout/show?token=$ACTIVE_TOKEN$q"
say "judge view: $(mask <<<"$SHOW")"
say "dashboard:  $(mask <<<"$DASH")"
say "slides:     $(mask <<<"$SLIDES")"
say "glasses stream: rtmp://$IP:$RTMP_PORT/scout"

if [[ "$NO_GLASSES" != 1 ]]; then
  QR_PNG=""
  if [[ -n "$GLASSES_LISTEN" ]]; then
    QR_PNG="${TMPDIR:-/tmp}"
    QR_PNG="${QR_PNG%/}/mentra-dev-qr-$PKG-$GLASSES_LISTEN.png"
  fi
  if [[ -n "$QR_PNG" && -f "$QR_PNG" ]]; then
    say "glasses QR: $QR_PNG  (Mentra app > Settings > Miniapp Developer Settings > Scan; or rerun with --qr)"
    ((QR)) && open "$QR_PNG"
  else
    say "glasses QR: not found yet (dev server port ${GLASSES_LISTEN:-?}); it is also printed in ops/logs/glasses.log"
  fi
fi

if ((OPEN)); then
  open "$DASH"
  open "$SHOW"
fi
say ""
say "stop with: $OPS/scout-down.sh"
exit "$fail"
