#!/bin/sh
set -eu

[ "${GITHUB_ACTIONS:-}" = true ]
[ "${GITHUB_REPOSITORY:-}" = scowalt/nailbiter ]
[ "${PRODUCTION_RELEASES_ENABLED:-}" = true ]
[ "${GITHUB_REF:-}" = refs/heads/main ]
[ "${GITHUB_REF_PROTECTED:-}" = true ]
case "${GITHUB_EVENT_NAME:-}" in
  push|workflow_dispatch) ;;
  *) echo 'Only trusted main push or manual releases are allowed' >&2; exit 1 ;;
esac
printf '%s\n' "${GITHUB_SHA:-}" | grep -Eq '^[0-9a-f]{40}$'
[ "$(git rev-parse HEAD)" = "$GITHUB_SHA" ]
current=$(gh api "repos/$GITHUB_REPOSITORY/git/ref/heads/main" --jq .object.sha)
[ "$current" = "$GITHUB_SHA" ] || { echo 'Main moved; refusing a stale release' >&2; exit 1; }
