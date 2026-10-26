#!/usr/bin/env bash
#
# Deploys one commit on this server, from images the CI pipeline already pushed - the CD half of
# the department pipeline. Run by the deploy job on the server's shell runner:
#
#   SQM_REGISTRY=<pull registry>/<project> deploy/compose/deploy-release.sh <commit short sha>
#
# Nothing is built here: the department's rule is that builds happen in CI and pull only through
# Nexus, so this pulls the four application images by tag and starts them.
#
# Each release gets its own directory - the compose files and grants of that commit - with the
# server's configuration linked in, and `current` points at the one running. The databases are
# named volumes of the `quadrasense` project, so they belong to no release directory. Commands by
# hand run from $root/current/deploy/compose, for the same reason Compose does (below). Rollback:
# run this again with an older commit; migrations are forward-only, so a rollback across one is a
# forward fix instead.

set -euo pipefail

sha="${1:?usage: deploy-release.sh <commit short sha>}"
root="${QUADRASENSE_ROOT:-/opt/quadrasense}"
src="$(cd "$(dirname "$0")/../.." && pwd)"
release="$root/releases/$sha"

[ -n "${SQM_REGISTRY:-}" ] || { echo "SQM_REGISTRY is not set: the registry path the images are pulled from" >&2; exit 1; }
[ -f "$root/config/.env" ] || { echo "no $root/config/.env" >&2; exit 1; }
[ -d "$root/config/tls" ] || { echo "no $root/config/tls" >&2; exit 1; }

echo "== release $sha"
rm -rf "$release.new"
mkdir -p "$release.new/deploy" "$release.new/db/operational"
cp -a "$src/deploy/compose" "$release.new/deploy/"
cp -a "$src/db/operational/grants" "$release.new/db/operational/"
ln -s "$root/config/.env" "$release.new/deploy/compose/.env"
ln -s "$root/config/tls" "$release.new/deploy/compose/tls"
rm -rf "$release"
mv "$release.new" "$release"

cd "$release/deploy/compose"
export SQM_TAG="$sha"

echo "== pulling $SQM_REGISTRY/{api,worker,migrator,web}:$sha"
docker compose pull --quiet migrate api worker web

# Starting the new worker stops the old one, and an import it is processing then starts again from
# the beginning once its lease expires - 27 to 32 minutes of processing for a day on this server
# (imports 28-36, 2026-10-26), and growing with the history. Nobody approves a deploy any more, so it
# waits for the import to finish: up to 90 minutes, then goes ahead, because a retried import loses
# time, not data. Queued jobs are not waited for; the new worker takes them.
in_progress() {
  docker compose exec -T postgres psql -U sqm -d sqm -tAc "SELECT count(*) FROM imports.import_job
    WHERE status IN ('VALIDATING','PARSING','NORMALIZING','DEDUPLICATING','ENRICHING','IMPORTING','AGGREGATING','FINALIZING')" \
    2>/dev/null || echo 0
}
for _ in $(seq 180); do
  busy="$(in_progress)"
  [ "${busy:-0}" = 0 ] && break
  echo "   an import is being processed ($busy); waiting for it to finish"
  sleep 30
done

# Compose runs from `current`, never from the release's own path. ClickHouse bind-mounts its config
# files by relative path and Compose hashes the resolved path, so from a new directory each release
# every deploy would recreate ClickHouse - a two-minute stop, and any import statement on it killed.
# Measured on the server 2026-10-26: the hash differed for every directory, and stayed the same
# through a symlink whatever it pointed at. Postgres mounts no files and was never affected.
previous="$(readlink "$root/current" || true)"
ln -sfn "$release" "$root/current"
cd "$root/current/deploy/compose"

echo "== starting (migrate and grants run first; a failed migration keeps the old API and worker)"
if ! docker compose up -d --no-build --remove-orphans; then
  [ -z "$previous" ] || ln -sfn "$previous" "$root/current"
  exit 1
fi

# `up -d` returns once the containers have started. Nobody watches this deploy, so the job goes red
# when the API or the web server does not become healthy, rather than reporting a release that is
# not serving.
echo "== waiting for api and web to report healthy"
for _ in $(seq 60); do
  healthy="$(docker compose ps --format '{{.Service}} {{.Health}}' api web | grep -c ' healthy$' || true)"
  [ "$healthy" = 2 ] && break
  sleep 5
done
if [ "$healthy" != 2 ]; then
  docker compose ps --format '  {{.Service}}\t{{.State}}\t{{.Health}}' >&2
  echo "api and web not both healthy after 5 minutes" >&2
  exit 1
fi

echo "$sha" > "$root/current.sha"
# Recorded in .env as update.sh does, so commands by hand (ps, logs, run --import-file) address the
# release that is running, not whatever was last built on this server. --follow-symlinks: .env is a
# link into config/, and plain `sed -i` would replace it with a copy.
sed -i --follow-symlinks -e "s|^SQM_TAG=.*|SQM_TAG=$sha|" -e "s|^SQM_REGISTRY=.*|SQM_REGISTRY=$SQM_REGISTRY|" .env

# Five releases are kept: the running one and four to roll back to.
ls -1dt "$root"/releases/*/ 2>/dev/null | tail -n +6 | xargs -r rm -rf

docker compose ps --format '  {{.Service}}\t{{.Image}}\t{{.State}}\t{{.Health}}'
