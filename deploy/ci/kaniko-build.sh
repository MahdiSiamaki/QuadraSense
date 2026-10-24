#!/bin/sh
#
# Builds one QuadraSense image with kaniko and pushes it to Nexus - the CI build, on the
# department's instance runner. That runner has no Docker socket, deliberately (the department's
# runner guide: the socket is root on the machine Nexus runs on, and an instance runner is open to
# every project), so `docker build` cannot run there; kaniko builds the Dockerfile in user space
# inside the job's own container and pushes the result itself.
#
#   deploy/ci/kaniko-build.sh <api|worker|migrator|web>
#
# POSIX sh: the kaniko debug image has busybox and nothing else. Every address and credential comes
# from GitLab CI/CD variables - none is written here, because this file is also mirrored to a public
# repository.
#
#   REGISTRY_PULL, REGISTRY_PUSH, IMAGE_PREFIX               where images come from and go to
#   NEXUS_PULL_USER/_PASS, NEXUS_PUSH_USER/_PASS              Protected, Masked
#   NUGET_SOURCE, NPM_REGISTRY                                Nexus's NuGet and npm groups
#   SDK_IMAGE, RUNTIME_IMAGE, NODE_IMAGE, NGINX_IMAGE         base images; empty = the Dockerfile's default

set -eu

component="${1:?usage: kaniko-build.sh <api|worker|migrator|web>}"
: "${REGISTRY_PULL:?}" "${REGISTRY_PUSH:?}" "${IMAGE_PREFIX:?}" "${CI_COMMIT_SHORT_SHA:?}"

# Credentials for both registries, written where kaniko reads them - from Masked variables, never
# echoed. base64 of "user:password", as Docker's config.json has it.
auth() { printf '%s:%s' "$1" "$2" | base64 | tr -d '\n'; }
mkdir -p /kaniko/.docker
cat > /kaniko/.docker/config.json <<EOF
{ "auths": {
    "$REGISTRY_PULL": { "auth": "$(auth "$NEXUS_PULL_USER" "$NEXUS_PULL_PASS")" },
    "$REGISTRY_PUSH": { "auth": "$(auth "$NEXUS_PUSH_USER" "$NEXUS_PUSH_PASS")" }
} }
EOF

case "$component" in
  api|worker|migrator)
    dockerfile=deploy/images/backend/Dockerfile
    set -- --target "$component"
    [ -n "${NUGET_SOURCE:-}" ] && set -- "$@" --build-arg "NUGET_SOURCE=$NUGET_SOURCE"
    [ -n "${SDK_IMAGE:-}" ] && set -- "$@" --build-arg "SDK_IMAGE=$SDK_IMAGE"
    [ -n "${RUNTIME_IMAGE:-}" ] && set -- "$@" --build-arg "RUNTIME_IMAGE=$RUNTIME_IMAGE"
    ;;
  web)
    dockerfile=deploy/images/web/Dockerfile
    set --
    [ -n "${NPM_REGISTRY:-}" ] && set -- "$@" --build-arg "NPM_REGISTRY=$NPM_REGISTRY"
    [ -n "${NODE_IMAGE:-}" ] && set -- "$@" --build-arg "NODE_IMAGE=$NODE_IMAGE"
    [ -n "${NGINX_IMAGE:-}" ] && set -- "$@" --build-arg "NGINX_IMAGE=$NGINX_IMAGE"
    ;;
  *) echo "unknown component: $component" >&2; exit 2 ;;
esac

destination="$REGISTRY_PUSH/$IMAGE_PREFIX/$component:$CI_COMMIT_SHORT_SHA"
echo "building $component -> $destination"

# --registry-mirror: Docker Hub images (FROM node, nginx...) through Nexus's pull group, the
# department's only permitted source.
/kaniko/executor \
  --context "$CI_PROJECT_DIR" \
  --dockerfile "$dockerfile" \
  --registry-mirror "$REGISTRY_PULL" \
  --destination "$destination" \
  --snapshot-mode=redo \
  "$@"
