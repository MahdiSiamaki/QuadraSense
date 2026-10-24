#!/usr/bin/env bash
#
# Moves Docker's data - images, containers, and the volumes the databases live in - to another
# directory, keeping all of it. For a host whose space was added somewhere Docker does not look.
#
#   sudo bash deploy/host/move-docker-data-root.sh /srv/docker
#
# Written for the department VM (2026-10-24): its disk grew from 200 to 500 GB, all of it into `/`,
# while Docker kept everything on the 42 GB `/var` (docs/operations/deployment/02-department-vm.md).
#
# What it does: stops Docker (the containers stop with their own grace periods - ClickHouse two
# minutes), copies the data root with every owner, mode, link and ACL, points daemon.json's
# data-root at the copy (other settings kept), and starts Docker. Containers with a restart policy
# come back by themselves. The old directory is left in place, to be deleted by hand once the
# system is checked - a move that deletes its source first has nothing to fall back on.
#
# What it does not move: on a host using containerd's image store (Docker 29's default, `docker
# info` says io.containerd.snapshotter.v1), images live in /var/lib/containerd. That is the small
# part - a few GB, and old builds can be pruned. The volumes, which hold the databases and are what
# grows, are under the data root and move.

set -euo pipefail

target="${1:?usage: move-docker-data-root.sh <new data root, e.g. /srv/docker>}"
[ "$(id -u)" = 0 ] || { echo "run as root (sudo)" >&2; exit 1; }

current="$(docker info --format '{{.DockerRootDir}}')"
if [ "$current" = "$target" ]; then
  echo "Docker's data root is already $target"
  exit 0
fi

mkdir -p "$target"
need=$(du -sb "$current" | cut -f1)
free=$(df -B1 --output=avail "$target" | tail -1)
if [ "$free" -lt $((need * 2)) ]; then
  echo "refusing: $target has $free bytes free for $need bytes of data (want twice that)" >&2
  exit 1
fi
if [ -n "$(ls -A "$target")" ]; then
  echo "refusing: $target is not empty" >&2
  exit 1
fi

echo "stopping Docker; running containers stop within their grace periods"
systemctl stop docker.socket docker.service

echo "copying $current -> $target ($(du -sh "$current" | cut -f1))"
rsync -aHAX --numeric-ids "$current/" "$target/"

config=/etc/docker/daemon.json
[ -s "$config" ] || echo '{}' > "$config"
cp -p "$config" "$config.before-data-root-move"
jq --arg root "$target" '. + {"data-root": $root}' "$config.before-data-root-move" > "$config"
echo "daemon.json: $(jq -c . "$config")"

systemctl start docker.service
echo "Docker's data root is now: $(docker info --format '{{.DockerRootDir}}')"
docker ps --format '  {{.Names}}\t{{.Status}}'
echo
echo "The old data is still in $current. Check the system first, then remove it: rm -rf $current"
