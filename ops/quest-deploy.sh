#!/usr/bin/env bash
# quest-deploy.sh: install a Virtual Bee Swarm APK on the Quest, start it, and save its logcat (PLAN.md S1.14).
#
# Usage:
#   ops/quest-deploy.sh [-s SERIAL] [-p PACKAGE] [-o LOGFILE] [-t SECONDS] [--no-launch] APK
#
#   -s SERIAL    adb device serial or ip:port (default: $ANDROID_SERIAL, else the only device connected)
#   -p PACKAGE   package name (default: read from the APK with aapt2 if it's installed, else dev.drori.virtualbeeswarm)
#   -o LOGFILE   where the full logcat goes (default: $QUEST_LOG_DIR/<apk name>-<date-time>.logcat.txt, with
#                QUEST_LOG_DIR defaulting to ~/immerse-the-bay/quest-logs, outside the repo)
#   -t SECONDS   stop logging after this many seconds (default: log until Ctrl+C)
#   --no-launch  install only: no logcat -c, no launch, no logging
#
# What it does:
#   1. adb install -r -g APK (-g grants RECORD_AUDIO and the other runtime permissions, so no mic dialog).
#      On INSTALL_FAILED_UPDATE_INCOMPATIBLE (the installed copy was signed with a different key) or
#      INSTALL_FAILED_VERSION_DOWNGRADE, it uninstalls the app and installs again. The uninstall wipes the app's
#      data, including a tuning.json pushed into its persistentDataPath.
#   2. adb logcat -c, then starts the app through `cmd package resolve-activity` (no hard-coded activity name).
#   3. Saves the whole logcat to LOGFILE and prints only the interesting lines here: [VBS], [Beeswax], Unity
#      errors and exceptions, crashes. Ctrl+C stops the logging (the app keeps running).
#
# Example (laptop, Quest on USB, developer mode on, "Allow USB debugging" accepted in the headset):
#   scp pop-os:immerse-the-bay/unity-work/builds/VirtualBeeSwarm-D-4-20261114-0630.apk ~/Downloads/
#   ops/quest-deploy.sh ~/Downloads/VirtualBeeSwarm-D-4-20261114-0630.apk
#   ops/quest-deploy.sh -t 120 -o ~/boot.logcat.txt ~/Downloads/VirtualBeeSwarm-D-4-20261114-0630.apk
# Over Wi-Fi: once on USB run `adb tcpip 5555`, then `adb connect <quest-ip>:5555` and pass -s <quest-ip>:5555.
#
# The APKs carry the Hive token: never post them anywhere public.
# Env: ADB=/path/to/adb (default: adb on PATH, then ~/.local/bin/adb).

set -uo pipefail

die() { echo "quest-deploy: $*" >&2; exit 1; }

serial="${ANDROID_SERIAL:-}"
pkg=""
logfile=""
seconds=""
launch=1
apk=""
while [ $# -gt 0 ]; do
  case "$1" in
    -s) serial="${2:?-s needs a serial}"; shift 2 ;;
    -p) pkg="${2:?-p needs a package}"; shift 2 ;;
    -o) logfile="${2:?-o needs a file}"; shift 2 ;;
    -t) seconds="${2:?-t needs seconds}"; shift 2 ;;
    --no-launch) launch=0; shift ;;
    -h|--help) sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) die "unknown option $1 (see --help)" ;;
    *) [ -z "$apk" ] || die "only one APK, please"; apk="$1"; shift ;;
  esac
done
[ -n "$apk" ] || die "usage: $0 [-s SERIAL] [-p PACKAGE] [-o LOGFILE] [-t SECONDS] [--no-launch] APK"
[ -f "$apk" ] || die "no such APK: $apk"
[ -z "$seconds" ] || [[ "$seconds" =~ ^[0-9]+$ ]] || die "-t wants whole seconds, got '$seconds'"

# --- adb and the device
ADB="${ADB:-}"
if [ -z "$ADB" ]; then
  if command -v adb >/dev/null 2>&1; then ADB="$(command -v adb)"
  elif [ -x "$HOME/.local/bin/adb" ]; then ADB="$HOME/.local/bin/adb"
  else die "adb not found (install platform-tools or set ADB=/path/to/adb)"; fi
fi

if [ -z "$serial" ]; then
  mapfile -t devices < <("$ADB" devices | awk 'NR > 1 && $2 == "device" {print $1}')
  case ${#devices[@]} in
    0) "$ADB" devices >&2
       die "no Quest connected (unauthorized? put the headset on and accept \"Allow USB debugging\")" ;;
    1) serial="${devices[0]}" ;;
    *) die "several devices connected (${devices[*]}): pick one with -s" ;;
  esac
fi
q() { "$ADB" -s "$serial" "$@"; }   # adb on the chosen device
[ "$(q get-state 2>/dev/null)" = "device" ] || die "device $serial isn't ready (adb get-state: $(q get-state 2>&1))"
model="$(q shell getprop ro.product.model 2>/dev/null | tr -d '\r')"

# --- package name
if [ -z "$pkg" ]; then
  for tool in aapt2 aapt; do
    if command -v "$tool" >/dev/null 2>&1; then
      pkg="$("$tool" dump badging "$apk" 2>/dev/null | sed -n "s/^package: name='\([^']*\)'.*/\1/p" | head -n 1)"
      [ -n "$pkg" ] && break
    fi
  done
  [ -n "$pkg" ] || pkg="dev.drori.virtualbeeswarm"
fi

echo "quest-deploy: $(basename "$apk") ($(du -h "$apk" | cut -f1)) -> $serial ${model:+($model)}, package $pkg"

# --- 1. install
install_apk() { q install -r -g "$apk" 2>&1 | tr -d '\r'; }
t0=$(date +%s)
out="$(install_apk)"
echo "$out" | tail -n 3
if echo "$out" | grep -qE "INSTALL_FAILED_UPDATE_INCOMPATIBLE|INSTALL_FAILED_VERSION_DOWNGRADE"; then
  echo "quest-deploy: the installed copy doesn't match (other signing key or newer version code): uninstalling $pkg (its data goes too) and trying again"
  q uninstall "$pkg" 2>&1 | tr -d '\r'
  out="$(install_apk)"
  echo "$out" | tail -n 3
fi
echo "$out" | grep -q "^Success" || die "install failed (see above)"
echo "quest-deploy: installed in $(( $(date +%s) - t0 )) s"
# -g already grants it; this is only a fallback for installers that ignore -g.
q shell pm grant "$pkg" android.permission.RECORD_AUDIO >/dev/null 2>&1 || true

[ "$launch" = 1 ] || { echo "quest-deploy: --no-launch: done"; exit 0; }

# --- 2. clear logcat, launch
q logcat -c || die "adb logcat -c failed"
activity="$(q shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER "$pkg" 2>/dev/null | tr -d '\r' | tail -n 1)"
[[ "$activity" == */* ]] || die "can't resolve the launch activity of $pkg (got: '$activity')"
q shell am force-stop "$pkg" >/dev/null 2>&1 || true
echo "quest-deploy: starting $activity"
q shell am start -W -n "$activity" 2>&1 | tr -d '\r' | grep -E "Status|LaunchState|TotalTime|Error" || true

# --- 3. logcat to a file
if [ -z "$logfile" ]; then
  logfile="${QUEST_LOG_DIR:-$HOME/immerse-the-bay/quest-logs}/$(basename "$apk" .apk)-$(date +%Y%m%d-%H%M%S).logcat.txt"
fi
mkdir -p "$(dirname "$logfile")"
if [ -n "$seconds" ]; then how="for $seconds s"; else how="Ctrl+C stops it; the app keeps running"; fi
echo "quest-deploy: full logcat -> $logfile ($how)"
pid="$(q shell pidof "$pkg" 2>/dev/null | tr -d '\r')"
echo "quest-deploy: app pid ${pid:-not running yet}"

trap 'echo; echo "quest-deploy: stopped logging. $(wc -l < "$logfile") lines in $logfile"; exit 0' INT TERM
filter='\[VBS|\[Beeswax|AndroidRuntime|FATAL|SIGSEGV|SIGABRT|Exception| E Unity |CRASH|Tombstone|DEBUG +: '
if [ -n "$seconds" ]; then
  timeout "$seconds" "$ADB" -s "$serial" logcat -v threadtime | tee "$logfile" | grep --line-buffered -E "$filter"
else
  "$ADB" -s "$serial" logcat -v threadtime | tee "$logfile" | grep --line-buffered -E "$filter"
fi
echo "quest-deploy: done. $(wc -l < "$logfile") lines in $logfile"
