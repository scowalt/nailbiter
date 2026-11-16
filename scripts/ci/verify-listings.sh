#!/usr/bin/env bash
set -euo pipefail

origin=https://live.nailbiter.app
ready_ms=120000
timeout_ms=10000
revision=${GITHUB_SHA:?The released commit is required}
image=$(tr -d '\n' < .ci/backend-image.txt)
summary=${GITHUB_STEP_SUMMARY:-/dev/null}

finish() {
  local code=$1 verdict=$2
  {
    echo '## Deployed listing check'
    echo
    echo "- Revision: \`$revision\`"
    echo "- Image: \`$image\`"
    echo "- Origin: $origin"
    echo "- Report: artifact \`listing-check-$revision\`"
    echo
    echo "$verdict"
    if [[ -f .ci/listing-check.txt ]]; then
      echo
      echo '```text'
      cat .ci/listing-check.txt
      echo '```'
    fi
  } >> "$summary"
  if [[ "$code" != 0 ]]; then
    echo "::error title=Deployed listing check failed (exit $code)::$verdict"
  fi
  exit "$code"
}

if [[ "$(tr -d '\n' < .ci/listing-checker.commit 2>/dev/null)" != "$revision" ]] ||
  ! sha256sum --check --status .ci/listing-checker.sha256 2>/dev/null; then
  finish 2 "The listing-checker artifact does not match commit $revision or its recorded hash. No request was sent."
fi
tar -C .ci -xf .ci/listing-checker.tar

set +e
.ci/listing-checker/listing-check url --base-url "$origin" --allow-remote-host \
  --expected-revision "$revision" --ready-within-ms "$ready_ms" \
  --path / --path /nfl --path /nba --path /nhl --path /mlb --path /cfb \
  --samples 3 --warmup 1 --ttfb-budget-ms 1500 --completion-budget-ms 2500 --request-timeout-ms "$timeout_ms" \
  --report .ci/listing-check.json | tee .ci/listing-check.txt
code=${PIPESTATUS[0]}
set -e
detail=$(grep -m1 -E '^(readiness|revision) ' .ci/listing-check.txt || true)

case "$code" in
  0) finish 0 "Verified: $origin served revision $revision on every listing request, with listing markup and median timing within budget." ;;
  2) finish 2 'The checker rejected its options; fix the call in scripts/ci/verify-listings.sh. No request was sent.' ;;
  3) finish 3 'A listing answered with an error status, a redirect or a failed connection. The release is deployed; check the backend logs for that path.' ;;
  4) finish 4 "A listing did not send a complete response within $((timeout_ms / 1000)) s. The release is deployed; a slow listing query is the likely cause." ;;
  5) finish 5 'A listing answered 200 without its listing markup. The release is deployed; open the path and compare it with the page renderer.' ;;
  7) finish 7 'Every listing loaded, but a median was over budget. Do not raise the budget here; open an issue with the attached report.' ;;
  8) finish 8 "/health did not serve revision $revision within $((ready_ms / 1000)) s. No listing was requested. Checker: $detail. If the last poll names a different revision, that revision answered instead of this release (expected $revision): check which container serves on the host with docker ps and the backend logs. A status or request failure points at Cloudflare, Caddy or the network." ;;
  9) finish 9 "A listing response came from a different revision than this release (expected $revision). Checker: $detail. Check which containers serve behind Caddy on the host. No timing from this run is attributed to the release." ;;
  *) finish "$code" 'The checker stopped without a result. Read its stack trace in this step log.' ;;
esac
