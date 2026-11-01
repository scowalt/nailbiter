#!/usr/bin/env bash
set -euo pipefail

sha=${1:?The tested commit SHA is required}
if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]]; then
  echo 'A full commit SHA is required' >&2
  exit 1
fi
test "$(git rev-parse HEAD)" = "$sha"
image=${2:?The tested immutable image reference is required}
if [[ ! "$image" =~ ^ghcr.io/scowalt/nailbiter-app/backend@sha256:[0-9a-f]{64}$ ]]; then
  echo 'An immutable backend image digest is required' >&2
  exit 1
fi
compose() {
  doppler run -- env "BACKEND_IMAGE=$image" docker compose \
    -f docker-compose.yml -f scripts/ci/compose.release.yaml -p excitement-score "$@"
}

echo '==> Ensuring swap reserve'
bash scripts/ensure-swap.sh

echo '==> Pulling the tested backend image'
compose pull backend

echo '==> Running database migrations'
for migration in apps/backend/Migrations/*.sql; do
  migration_name=$(basename "$migration")
  echo "Applying migration: $migration_name"
  case "$migration_name" in
    0[0-1][0-9]_*|020_*)
      docker exec -i excitement-score-db psql -U postgres -d excitement_score < "$migration" ||
        echo "Legacy migration $migration_name reported an error; continuing"
      ;;
    *)
      docker exec -i excitement-score-db psql -v ON_ERROR_STOP=1 -U postgres -d excitement_score < "$migration"
      ;;
  esac
done

echo '==> Deploying backend and caddy containers'
compose up -d --force-recreate --no-deps backend caddy

echo '==> Verifying running image and public health'
expected=$(docker image inspect --format '{{.Id}}' "$image")
running=$(docker inspect --format '{{.State.Running}} {{.Image}}' excitement-score-backend)
test "$running" = "true $expected"
curl --fail --silent --show-error --retry 5 --retry-connrefused --retry-delay 2 --max-time 10 \
  --proto '=https' https://api.nailbiter.app/health >/dev/null

echo '==> Cleaning up old images'
docker image prune -f

echo '==> Deploy complete'
