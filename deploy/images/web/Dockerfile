# syntax=docker/dockerfile:1
#
# The frontend, and the one door into QuadraSense:
#
#   docker build -f deploy/images/web/Dockerfile -t quadrasense/web:<tag> .
#
# nginx serves the built single-page app and forwards /api/ to the API, so the browser sees one
# origin - which is what keeps the session cookie first-party and CORS out of the picture, the same
# arrangement as the Vite proxy in development (frontend/vite.config.ts).
#
# Listens on 8080 (HTTP) and, when a certificate is mounted, 8443 (HTTPS): never a port below 1024,
# which a non-root process cannot bind. The image's user is non-root and any UID works - see
# docs/operations/deployment/01-container-images.md.

# Empty means the default after ':-' below (see deploy/images/backend/Dockerfile).
ARG NODE_IMAGE
ARG NGINX_IMAGE

# ------------------------------------------------------------------------------------- build
FROM ${NODE_IMAGE:-node:24.19.0-alpine} AS build

# In MCI's network npm comes through the Nexus proxy, not registry.npmjs.org (FR-NSM-204/00 2-1).
ARG NPM_REGISTRY=

WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN set -eu; \
    if [ -n "$NPM_REGISTRY" ]; then npm config set registry "$NPM_REGISTRY"; fi; \
    npm ci --no-audit --no-fund

COPY frontend/ ./
# vue-tsc -b && vite build: a type error fails the image, as it fails CI.
RUN npm run build

# ------------------------------------------------------------------------------------ runtime
FROM ${NGINX_IMAGE:-nginxinc/nginx-unprivileged:1.28-alpine} AS web

# Owned by root and only readable: the process, whatever its UID, cannot change its own config.
# The directory first: COPY --chmod would create it with the files' 0644, and a directory without
# its execute bit cannot be entered - nginx then cannot read its own configuration.
USER 0
RUN install -d -o 0 -g 0 -m 0755 /etc/nginx/sqm
USER 101
COPY --chown=0:0 --chmod=0644 deploy/images/web/nginx.conf       /etc/nginx/nginx.conf
COPY --chown=0:0 --chmod=0644 deploy/images/web/app.inc          /etc/nginx/sqm/app.inc
COPY --chown=0:0 --chmod=0644 deploy/images/web/security.inc     /etc/nginx/sqm/security.inc
COPY --chown=0:0 --chmod=0644 deploy/images/web/server-http.conf /etc/nginx/sqm/server.conf
COPY --chown=0:0 --from=build /src/frontend/dist/  /usr/share/nginx/html/

# The base image's own entrypoint scripts rewrite files under /etc/nginx at startup (listen on
# IPv6, envsubst templates). This image's configuration is complete as built, and a read-only root
# filesystem would refuse those writes - so nginx is started directly.
ENTRYPOINT []
CMD ["nginx", "-g", "daemon off;"]

EXPOSE 8080 8443
