#!/usr/bin/env bash
#
# Installs the GitLab runner that deploys QuadraSense on this server - the CD half of the
# department pipeline (docs/operations/deployment/05-department-ci-cd.md):
#
#   sudo GITLAB_URL=https://<gitlab>/ bash deploy/host/install-deploy-runner.sh
#
# Before running it, create the runner in GitLab: the QuadraSense group -> Settings -> CI/CD ->
# Runners -> New group runner; tag `quadrasense-deploy`; untick "Run untagged jobs"; tick
# "Protected". GitLab then shows a `glrt-...` token. This script asks for it at a hidden prompt: it
# is never an argument, never in shell history, never in a chat - it is the runner's identity, and
# anyone holding it can run our deploy jobs on their own machine (the department's runner guide).
#
# What it sets up, the department's way:
#   - gitlab-runner (a pinned version, checksum-verified) running as the `gitlab-runner` user;
#   - that user in the `docker` group (it runs Compose) and the `deploy` group (it writes the
#     deployment directory) - the department's convention;
#   - /opt/quadrasense: config/ (.env, tls/ - copied from CONFIG_FROM if given) and releases/,
#     owned root:deploy, group-writable, closed to everyone else;
#   - the runner registered with a shell executor - deploy runners have no other.

set -euo pipefail

VERSION="${RUNNER_VERSION:-v19.5.1}"
GITLAB_URL="${GITLAB_URL:?set GITLAB_URL to the department GitLab, e.g. https://<gitlab>/}"
ROOT="${QUADRASENSE_ROOT:-/opt/quadrasense}"
DOWNLOADS="https://gitlab-runner-downloads.s3.amazonaws.com/$VERSION"

[ "$(id -u)" = 0 ] || { echo "run as root (sudo)" >&2; exit 1; }

echo "== gitlab-runner $VERSION"
if ! command -v gitlab-runner >/dev/null; then
  tmp=$(mktemp -d)
  # RUNNER_BINARY: a copy already downloaded (142 MB, ~17 minutes from the server, measured) - it is
  # checked against the release's checksum exactly as a fresh download would be.
  if [ -n "${RUNNER_BINARY:-}" ]; then
    cp "$RUNNER_BINARY" "$tmp/gitlab-runner-linux-amd64"
  else
    curl -fsSL -o "$tmp/gitlab-runner-linux-amd64" "$DOWNLOADS/binaries/gitlab-runner-linux-amd64"
  fi
  curl -fsSL -o "$tmp/release.sha256" "$DOWNLOADS/release.sha256"
  # release.sha256 is "<hash><TAB>binaries/<file>" (checked 2026-10-24). An empty expected hash is a
  # failure, never a match.
  want=$(awk '$2 == "binaries/gitlab-runner-linux-amd64" {print $1}' "$tmp/release.sha256")
  have=$(sha256sum "$tmp/gitlab-runner-linux-amd64" | cut -d' ' -f1)
  [ ${#want} -eq 64 ] && [ "$want" = "$have" ] \
    || { echo "checksum mismatch or missing: want '$want', have '$have'" >&2; exit 1; }
  echo "   checksum ok"
  install -m 0755 "$tmp/gitlab-runner-linux-amd64" /usr/local/bin/gitlab-runner
  rm -rf "$tmp"
fi
gitlab-runner --version | head -1

echo "== user and groups"
id gitlab-runner >/dev/null 2>&1 || useradd --system --create-home --shell /bin/bash gitlab-runner
# Ubuntu's skeleton .bash_logout runs clear_console, which fails without a terminal, and the shell
# executor then fails every job at "prepare environment: exit status 1" - the first deploy did,
# 2026-10-26 (pipeline 369). GitLab's documented fix is to remove the file.
rm -f ~gitlab-runner/.bash_logout
getent group deploy >/dev/null || groupadd --system deploy
usermod -aG docker,deploy gitlab-runner
id gitlab-runner

echo "== $ROOT"
install -d -o root -g deploy -m 2770 "$ROOT" "$ROOT/config" "$ROOT/releases"
if [ -n "${CONFIG_FROM:-}" ] && [ ! -e "$ROOT/config/.env" ]; then
  cp -p "$CONFIG_FROM/.env" "$ROOT/config/.env"
  cp -rp "$CONFIG_FROM/tls" "$ROOT/config/tls"
fi
if [ -e "$ROOT/config/.env" ]; then
  chown root:deploy "$ROOT/config/.env" && chmod 0640 "$ROOT/config/.env"
  # The containers read the certificate and key through a bind mount, as their own UID in group 0;
  # on the host nobody outside root and `deploy` can reach them ($ROOT is 2770).
  chown -R root:deploy "$ROOT/config/tls" && chmod 0755 "$ROOT/config/tls" && chmod 0644 "$ROOT"/config/tls/*
else
  echo "   no .env yet: put .env and tls/ in $ROOT/config before the first deploy (or set CONFIG_FROM)"
fi

echo "== service"
gitlab-runner status >/dev/null 2>&1 || gitlab-runner install --user gitlab-runner --working-directory /home/gitlab-runner
systemctl enable --now gitlab-runner >/dev/null

echo "== registration"
if gitlab-runner list 2>&1 | grep -q 'Executor=shell'; then
  echo "   a shell runner is already registered here; skipping (gitlab-runner unregister to redo)"
else
  read -rsp "runner token (glrt-..., not echoed): " CI_SERVER_TOKEN; echo
  [ -n "$CI_SERVER_TOKEN" ] || { echo "no token given" >&2; exit 1; }
  CI_SERVER_TOKEN="$CI_SERVER_TOKEN" gitlab-runner register --non-interactive \
    --url "$GITLAB_URL" --executor shell --name "quadrasense-deploy on $(hostname)"
  unset CI_SERVER_TOKEN
fi

# The runner process keeps the group memberships it started with: restart after usermod.
systemctl restart gitlab-runner
gitlab-runner verify

echo "== can gitlab-runner deploy?"
sudo -u gitlab-runner bash -c "touch '$ROOT/releases/.probe' && rm '$ROOT/releases/.probe' && docker ps >/dev/null && echo OK"
