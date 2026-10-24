#!/usr/bin/env bash
#
# Deploys the newest commit of this checkout's branch on this machine: pull, build, restart.
#
#   /opt/quadrasense/repo/deploy/compose/update.sh
#
# The deployment routine the product owner set (2026-10-24): changes are pushed to GitHub, and the
# server takes them from there. The images are built here, from the checkout, and tagged with its
# commit - so `docker compose ps` says exactly which code is running. .env and tls/ sit next to this
# file - on the server as links into /opt/quadrasense/config - and are ignored by git; apart from
# SQM_TAG, nothing here touches them.
#
# The migrate and grants jobs run on every `up` and are no-ops when nothing changed. A migration that
# fails stops the API and worker from being replaced: the old ones keep running.

set -euo pipefail
cd "$(dirname "$0")"

# Pull first, then run the script just pulled, from the top. Bash goes on reading the file it opened,
# and `git pull` puts a new file in its place: without the exec, a change to this script would take
# effect only one deployment later. That happened on 2026-10-24 - the run that pulled the
# --follow-symlinks fix below still ran the old sed, and replaced the .env link with a copy.
if [ -z "${SQM_UPDATE_FROM:-}" ]; then
  if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    echo "refusing: this checkout has local changes; deployments come from git, not from edits here" >&2
    git status --short --untracked-files=no >&2
    exit 1
  fi
  SQM_UPDATE_FROM="$(git rev-parse --short=12 HEAD)"
  git pull --ff-only
  SQM_UPDATE_FROM="$SQM_UPDATE_FROM" exec "./$(basename "$0")" "$@"
fi

before="$SQM_UPDATE_FROM"
export SQM_TAG="$(git rev-parse --short=12 HEAD)"

if [ "$before" = "$SQM_TAG" ]; then
  echo "already at $SQM_TAG; rebuilding and restarting anyway"
else
  echo "deploying $SQM_TAG (was $before)"
  git log --oneline "$before..$SQM_TAG" | sed 's/^/  /'
fi

docker compose build

# Recorded in .env once the build has succeeded, so every later command by hand (ps, logs,
# run --load-dump) addresses the build that is running, not whatever .env said before.
# --follow-symlinks: on a server .env is a link to the one configuration in /opt/quadrasense/config,
# which the deploy runner reads too; plain `sed -i` would replace the link with a copy, and the two
# would drift apart without a word.
sed -i --follow-symlinks "s/^SQM_TAG=.*/SQM_TAG=${SQM_TAG}/" .env

docker compose up -d
docker compose ps --format '  {{.Service}}\t{{.State}}\t{{.Health}}'

# Earlier builds' images stay until removed; they are what a rollback would start
# (SQM_TAG=<old commit> docker compose up -d). Remove old ones by hand when space is short:
#   docker image ls 'quadrasense/*'
