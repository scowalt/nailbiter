#!/usr/bin/env bash
set -euo pipefail

rounds=${ROUNDS:-100}
listeners=${LISTENERS:-8000}
churn=${CHURN_PER_SECOND:-200}
docker_range_loops=${DOCKER_RANGE_LOOPS:-0}
root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
label="nailbiter.ryuk-stress=$$"
range_log=$(mktemp)
test_log=$(mktemp)
background=()
groups=()

run_in_group() {
  setsid "$@" &
  groups=("$!")
  local status=0
  wait "$!" || status=$?
  groups=()
  return "$status"
}

stop_background() {
  if ((${#groups[@]})); then kill -- "-${groups[0]}" 2>/dev/null || true; fi
  if ((${#background[@]})); then kill "${background[@]}" 2>/dev/null || true; fi
  wait 2>/dev/null || true
  docker ps -aq --filter "label=$label" | xargs -r docker rm -f >/dev/null
  rm -f "$range_log" "$test_log"
}
trap stop_background EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

run_in_group dotnet build "$root/apps/backend.tests/backend.tests.csproj" -c Release

python3 -I "$root/scripts/ryuk-port-stress/hold_ephemeral_listeners.py" "$listeners" "$churn" &
background+=($!)

for ((loop = 0; loop < docker_range_loops; loop++)); do
  (
    while :; do
      if failure=$(docker run --rm --label "$label" --entrypoint sleep -p 61000-65535:5432 postgres:16-alpine 1 2>&1 >/dev/null); then
        echo started >>"$range_log"
      else
        echo "failed ${failure//$'\n'/ }" >>"$range_log"
      fi
    done
  ) &
  background+=($!)
done

sleep 5
echo "load before: $(cut -d' ' -f1-3 /proc/loadavg)"
status=0
NAILBITER_RYUK_STRESS_ROUNDS=$rounds run_in_group dotnet test "$root/apps/backend.tests/backend.tests.csproj" -c Release --no-build \
  --filter "FullyQualifiedName~RyukHostPortStressProbe" --logger "console;verbosity=detailed" >"$test_log" 2>&1 || status=$?
grep -E '^ RYUK-STRESS|^Total tests' "$test_log" || tail -n 40 "$test_log"
echo "load after: $(cut -d' ' -f1-3 /proc/loadavg)"
if ((docker_range_loops)); then
  echo "range containers started=$(grep -c '^started' "$range_log" || true) failed=$(grep -c '^failed' "$range_log" || true)"
  grep '^failed' "$range_log" | sort | uniq -c || true
fi
exit "$status"
