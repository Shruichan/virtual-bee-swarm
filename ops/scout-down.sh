#!/usr/bin/env bash
# Live Bee Scout: stop what ops/scout-up.sh started (ops/run/*.pid), nothing else.
# A pid is only signalled if it still runs the command scout-up.sh recorded for it.
set -uo pipefail

RUN="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/run"
shopt -s nullglob
pidfiles=("$RUN"/*.pid)
if ((${#pidfiles[@]} == 0)); then
  echo "nothing to stop (no pid files in ops/run/)"
  exit 0
fi

for f in "${pidfiles[@]}"; do
  name="$(basename "$f" .pid)"
  IFS=$'\t' read -r pid sig <"$f"
  cmd="$(ps -o command= -p "$pid" 2>/dev/null || true)"
  if [[ -z "$pid" || -z "$cmd" ]]; then
    echo "$name: not running (pid ${pid:-?})"
  elif [[ "$cmd" != *"$sig"* ]]; then
    echo "$name: pid $pid now runs something else, left alone"
  else
    # scout-up starts each service as its own process-group leader: stop the group (children too).
    target="$pid"
    [[ "$(ps -o pgid= -p "$pid" | tr -d ' ')" == "$pid" ]] && target="-$pid"
    kill -TERM -- "$target" 2>/dev/null
    for _ in $(seq 1 50); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    if kill -0 "$pid" 2>/dev/null; then
      kill -KILL -- "$target" 2>/dev/null
      echo "$name: killed (pid $pid did not exit on TERM)"
    else
      echo "$name: stopped (pid $pid)"
    fi
  fi
  rm -f "$f"
done
