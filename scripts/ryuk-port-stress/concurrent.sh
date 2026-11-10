#!/usr/bin/env bash
set -euo pipefail

iterations=${ITERATIONS:-4}
width=${PROCESSES:-8}
root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
logs=$(mktemp -d)
trap 'rm -rf "$logs"' EXIT

dotnet build "$root/apps/backend.tests/backend.tests.csproj" -c Release

echo "load before: $(cut -d' ' -f1-3 /proc/loadavg)"
for ((iteration = 0; iteration < iterations; iteration++)); do
  for ((process = 0; process < width; process++)); do
    dotnet test "$root/apps/backend.tests/backend.tests.csproj" -c Release --no-build \
      --filter "FullyQualifiedName~RyukHostPortTests" >"$logs/$iteration-$process.log" 2>&1 &
  done
  wait || true
done
echo "load after: $(cut -d' ' -f1-3 /proc/loadavg)"

passed=$(grep -l 'Passed!' "$logs"/*.log | wc -l || true)
conflicts=$(grep -lE 'address already in use|port is already allocated' "$logs"/*.log | wc -l || true)
echo "processes=$((iterations * width)) passed=$passed failed=$((iterations * width - passed)) port_conflicts=$conflicts"
grep -hoE 'endpoint (testcontainers-ryuk|[a-z]+_[a-z]+)[^ ]* |Bind for [^ ]+ failed: port is already allocated|listen tcp4 [^ ]+: bind: address already in use|Actual: [0-9]+' "$logs"/*.log | sort | uniq -c || true
((passed == iterations * width))
