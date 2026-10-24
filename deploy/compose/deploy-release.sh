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
# named volumes of the `quadrasense` project, so they belong to no release directory. Rollback:
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

echo "== starting (migrate and grants run first; a failed migration keeps the old API and worker)"
docker compose up -d --no-build --remove-orphans

ln -sfn "$release" "$root/current"
echo "$sha" > "$root/current.sha"

# Five releases are kept: the running one and four to roll back to.
ls -1dt "$root"/releases/*/ 2>/dev/null | tail -n +6 | xargs -r rm -rf

docker compose ps --format '  {{.Service}}\t{{.Image}}\t{{.State}}\t{{.Health}}'
