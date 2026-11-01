#!/bin/sh
set -eu

printf '%s\n' "${GITHUB_SHA:-}" | grep -Eq '^[0-9a-f]{40}$'
[ "$(git rev-parse HEAD)" = "$GITHUB_SHA" ] || {
  echo 'Checkout must match the GitHub event revision, including PR merge revisions' >&2
  exit 1
}
