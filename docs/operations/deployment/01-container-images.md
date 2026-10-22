# Container images

**Status:** written 2026-10-22, with the stack built and run on the laptop. ADR-007 is the decision;
this page is how it is done and what was checked.

## The images

| Image | Built from | Base | Listens | Writes to | Health |
|---|---|---|---|---|---|
| `quadrasense/api` | `deploy/images/backend/Dockerfile`, target `api` | `mcr.microsoft.com/dotnet/aspnet:10.0.11` | 8080 | the uploads volume; `/tmp` | `/health/live`, `/health/ready` (checks ClickHouse) |
| `quadrasense/worker` | same, target `worker` | same | - | reads the uploads volume; `/tmp` | none yet (backlog) |
| `quadrasense/migrator` | same, target `migrator` | same | - | nothing; runs and exits | exit code |
| `quadrasense/web` | `deploy/images/web/Dockerfile` | `nginxinc/nginx-unprivileged:1.28-alpine`; built with `node:24.19.0-alpine` | 8080, 8443 | `/tmp` only | `/healthz` |
| ClickHouse | not built - `clickhouse/clickhouse-server:25.8.33.6` | - | 8123 (9000 unused) | its volume; `/tmp` | `SELECT 1` |
| PostgreSQL | not built - `postgres:17.11-bookworm` | - | 5432 | its volume; `/tmp`, `/var/run/postgresql` | `pg_isready` |

The .NET build is pinned to SDK **10.0.400**, global.json's version, because a different feature band
ships different analyzers and, with warnings as errors, a different verdict (CLAUDE.md, "Cloud
sessions"). The build compiles with `TreatWarningsAsErrors` like every other build of this code.

## The contract - why the same image runs on the VM and on OpenShift

OpenShift's default security context (restricted-v2) starts a container as **an arbitrary UID from
the namespace's range, in group 0**, and refuses root outright (MCI's DevOps guide: a root container
fails to deploy). The red lines add a read-only root filesystem, no capabilities and no privileged
mode (FR-NSM-204/00 §1-4). Every image here follows six rules, and the compose file enforces all of
them on every container, so a breach shows up on the VM long before OpenShift:

1. **No dependency on a user.** The process may be any UID; nothing looks itself up in
   `/etc/passwd`. Compose runs everything as `1000700000:0`, a UID that exists in no image.
2. **Files belong to root and are only read.** Application files are copied `--chown=0:0`; the
   process cannot change its own code or configuration.
3. **What is written, is written to a volume or `/tmp`** - and a volume directory that must be
   written is owned by group 0 and group-writable (`chmod 2770`), because group 0 is the one thing
   OpenShift guarantees. A new named volume copies the image directory's permissions, which is why
   the uploads directory exists in the image at all.
4. **Nothing binds a port below 1024** - 8080 and 8443 inside; the host maps 80 and 443.
5. **Logs go to stdout.** The API, worker and nginx as JSON. ClickHouse writes log files by default
   and refuses to start on a read-only filesystem until told not to (`clickhouse/logging.xml`).
6. **Configuration and secrets come from the environment** (or mounted files), never from the image.

## The images, one by one

**api, worker, migrator.** One multi-stage build: restore (its own layer), publish the three
projects, copy the migrations into the migrator. The runtime stage creates
`/var/lib/quadrasense/uploads` (group 0, 2770) and sets `ImportStorage__RootPath` to it. No curl
or wget is added: the compose health check speaks HTTP through bash's `/dev/tcp`, and OpenShift's
probes need nothing inside the container. The PostgreSQL connection strings say
`GSS Encryption Mode=Disable`: Npgsql tries Kerberos encryption first by default, the image has no
Kerberos library, and every connection wrote a "Cannot load library libgssapi_krb5.so.2" line to
stderr before falling back to the password it authenticates with anyway.

**web.** Built with `vue-tsc -b && vite build` (a type error fails the image), served by nginx:

- `/api/` is forwarded to `api:8080`, with `X-Forwarded-For` **overwritten** with the client address
  and `X-Forwarded-Proto` set, read timeouts above the API's own 180 s;
- only the upload endpoint accepts a body over 1 MiB (up to 11 GiB, streamed, not buffered); every
  other request body is capped at the proxy;
- hashed assets are cached for a year, `index.html` never;
- security headers and a Content-Security-Policy of `'self'` on everything nginx serves (styles also
  allow inline, for ECharts' tooltips); the API sets its own on `/api/`;
- `server.conf` serves HTTP on 8080. A deployment mounts `deploy/compose/web/server-https.conf` over
  it: TLS 1.2 and 1.3 on 8443, plain HTTP redirecting.

The base image's own entrypoint scripts rewrite `/etc/nginx` at start-up; on a read-only filesystem
they cannot, so the image starts nginx directly.

**ClickHouse.** Its entrypoint supports a non-root `--user` (it skips `chown`) and the image's
directories are world-writable, so it needs only configuration, mounted into `config.d` / `users.d`:

| File | Why |
|---|---|
| `logging.xml` | stdout only - otherwise it will not start read-only |
| `users.xml` | passwords from the environment (`from_env`); `default` locked to loopback, because the entrypoint that normally does that would have to write into `/etc` (`CLICKHOUSE_SKIP_USER_SETUP=1`) |
| `memory.xml` | the server ceiling as **80% of the container's limit**, read from the cgroup |
| `system-logs.xml` | the laptop's five disabled system logs, plus a 30-day TTL on the two kept |

**PostgreSQL.** The Debian variant, not Alpine (the development stack's): its entrypoint handles a UID
missing from `/etc/passwd`, and `PGDATA` is a subdirectory of the volume so that `initdb` creates it
as whichever UID runs.

## Building

From the repository root:

```bash
TAG=$(git rev-parse --short=12 HEAD)
for target in api worker migrator; do
  docker build -f deploy/images/backend/Dockerfile --target $target -t quadrasense/$target:$TAG .
done
docker build -f deploy/images/web/Dockerfile -t quadrasense/web:$TAG .
```

The tag is the commit, never `latest` (red lines §1-4). Build arguments, for building inside MCI's
network, where public registries are reached through Nexus proxies (red lines §2-1):

| Argument | Default | Inside MCI |
|---|---|---|
| `SDK_IMAGE`, `RUNTIME_IMAGE` | Microsoft's MCR images | the Nexus Docker proxy's path to the same tags |
| `NUGET_SOURCE` | nuget.org | the Nexus NuGet proxy |
| `NODE_IMAGE`, `NGINX_IMAGE` | Docker Hub | the Nexus Docker proxy |
| `NPM_REGISTRY` | registry.npmjs.org | the Nexus npm proxy |
| `APP_UID` | 10001 | any; OpenShift replaces it |

`.dockerignore` keeps out of every build context: `*.csv` and other data, `.env` files, keys and
certificates, `.claude/` (worktrees with whole copies of the repository), tests, docs, `bin/`, `obj/`,
`node_modules/`.

## Base images - an open question

| | Microsoft / Debian / Alpine (chosen for now) | Red Hat UBI |
|---|---|---|
| .NET | `mcr.microsoft.com/dotnet/aspnet` - Ubuntu; patched first, by the vendor of the SDK the build is pinned to | `ubi9`/`ubi10` `dotnet-100-runtime` - not reachable from the laptop on 2026-10-22, so untested |
| nginx | `nginxinc/nginx-unprivileged` (Alpine) - non-root by design | `ubi9/nginx-124` - runs non-root on OpenShift by design |
| Stores | official images (Ubuntu, Debian) | none for ClickHouse; `rhel9/postgresql-16` (16, not 17) |

The red lines make Red Hat the operating system (§1-1 row 3). Whether that reaches container base
images is for the security office (README, decision 9). Switching is a build argument and a re-run of
the checks below, not a rewrite.

## Verified

On the laptop, 2026-10-22: Docker Desktop 29.7.2, Compose 5.5.1. The development stack was stopped,
and the whole system ran as a separate compose project (`sqmtest`) on fresh volumes, from images
built by these Dockerfiles, with a self-signed certificate and generated passwords.

**Images and configuration**

| # | Check | Result |
|---|---|---|
| 1 | Build all four images | api, worker, migrator 350 MB each; web 84 MB. The .NET build: 0 warnings with warnings as errors; the web build runs `vue-tsc -b` |
| 2 | `docker compose config` | valid; every service `user: 1000700000:0`, `read_only: true`, a memory limit |
| 3 | The API image's settings | its own `appsettings.json` (not the worker's - see "Found" below); `/var/lib/quadrasense/uploads` `drwxrws--- 0:0` |
| 4 | The migrator image | carries all 16 PostgreSQL and 27 ClickHouse migrations |

**The contract, inside every running container** (`docker compose exec <service> ...`)

| # | Check | Result |
|---|---|---|
| 5 | `id` in clickhouse, postgres, api, worker, web | `uid=1000700000 gid=0` in all five |
| 6 | `touch /etc/x` | `Read-only file system` in all five; `/tmp` writable in all five |
| 7 | `docker inspect` | `CapDrop [ALL]`, `Privileged false`, `no-new-privileges:true`, memory limit set |
| 8 | Uploads volume | writable by the API and by the worker |
| 9 | Network | web → api: reachable; web → clickhouse, web → postgres: `bad address` (not even resolvable); worker → the internet: `Network is unreachable`; only web publishes ports |

**The stores**

| # | Check | Result |
|---|---|---|
| 10 | ClickHouse memory | a 3 GiB container → "Available RAM: 3.00 GiB", `max_server_memory_usage` **2.40 GiB** (80%), and "used cores: 4" from the CPU limit |
| 11 | ClickHouse users, from another container on the `data` network | `sqm_app`, `sqm_ingest` with their passwords: accepted; a wrong password: refused (516); `default`: refused (516) |
| 12 | ClickHouse logging and system logs | no file in `/var/log/clickhouse-server`; `query_log` created with `TTL event_date + 30 days`; none of the five disabled logs exists |
| 13 | PostgreSQL 17.11 | `initdb` as UID 1000700000; `pgdata` `drwx------ 1000700000 0` |

**The system**

| # | Check | Result |
|---|---|---|
| 14 | `docker compose up -d` on empty volumes | all healthy in 15 s; migrate applied 16 + 27 migrations; grants applied all four files |
| 15 | `up -d` again | both migrators "up to date"; grants re-run cleanly |
| 16 | `--create-admin` via `docker compose run`, password from the environment | "created administrator 'admin' (id 1)" |
| 17 | `https://localhost:18443/` and a deep link (`/explorer`) | 200 `text/html` |
| 18 | Headers on the app shell | CSP as above, `X-Frame-Options: DENY`, HSTS, `Cache-Control: no-cache`; hashed assets `max-age=31536000, immutable` |
| 19 | TLS | 1.1 refused (handshake fails); 1.2 accepted. Plain HTTP → `308` to `https://` |
| 20 | Sign-in | 200; cookies `__Host-sqm_session` (`secure; httponly; samesite=strict`) and `__Host-sqm_csrf` (`secure; samesite=strict`); `/me` 200 with them, 401 without |
| 21 | 2 MiB posted to the login endpoint | **413** from nginx - never reaches the API |
| 22 | Upload the 206 MB TAC file through nginx | 201 in 2.3 s; stored under a generated name; size and SHA-256 recorded |
| 23 | The worker imports it | 270,166 rows as TAC version 2 in 1.1 s, 0 invalid, awaiting activation - *after* the fix below; before it, every attempt failed |
| 24 | Client address in the audit log | `172.21.0.1`, the address nginx saw; with `ASPNETCORE_FORWARDEDHEADERS_ENABLED=false` the same sign-in records `172.21.0.3` - nginx's own |
| 25 | The browser (Chromium, through an HTTP `web` container on the same network) | sign-in, dashboard, Import Center, Explorer, Devices, Data quality render; no CSP violation in the console; the one error is the expected 401 before sign-in |
| 26 | `docker compose stop` then `up -d --wait` | stopped in 2 s, all healthy again in 25 s; sign-in works; the 270,166 TAC rows still there |

**Found by doing this - both latent since the code was written, both invisible in development:**

1. **The API could not be published.** `dotnet publish` failed with NETSDK1152: the API's reference
   to `Sqm.Ingestion` (for in-process hosting) brought the worker's `appsettings*.json` in under the
   same names. Fixed in `Sqm.Api.csproj` by dropping the worker's settings files from the API's
   publish list - not by turning the check off, which would let either file win. Without the target
   the publish fails again.
2. **The standalone worker could not read from ClickHouse.** The read paths it shares with the API
   ask for the API's HTTP client name, which only the API registers; on its own the worker got the
   factory's default, which does not decompress what ClickHouse compresses. The first TAC upload
   failed on all three attempts. Fixed by `AddStandaloneQueryClient`, called only by the worker's own
   host. `StandaloneQueryClientTests` fail against the old code (decompression `None`, timeout 100 s)
   and pass against the new.

**Also seen, and left as they are:** ClickHouse's entrypoint prints `Not found: logger.log` twice
at start (it looks up the file-log keys `logging.xml` removes) and warns that it cannot listen on
IPv6 where the host has none. Both are harmless.

**Not verified:** anything on the department VM or OpenShift; Red Hat UBI bases; a full SQM daily
file through the containers (the TAC file exercised the same upload, storage and worker path);
the CSP against ECharts tooltips, which need data to appear.
