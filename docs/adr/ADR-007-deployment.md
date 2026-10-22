# ADR-007: Deployment - containers that run as OpenShift runs them, the worker on its own

- **Status:** Proposed - stage 1 (department VM) is built and was run end to end on the laptop;
  stage 2 (MCI OpenShift) is planned, not built.
- **Date:** 2026-10-22
- **Asked for by:** the product owner: the whole system as docker compose, suitable for OpenShift,
  non-root; first on the department's VM, then on MCI's OpenShift.

## Context

Until now QuadraSense ran only on the owner's laptop: ClickHouse and PostgreSQL in Docker
(`infra/docker-compose.yml`), the API with `dotnet run` (hosting the import worker in-process,
`Import:RunWorkerInProcess`), the frontend with `npm run dev`. Nothing was packaged.

It moves twice: to a department VM (8 vCPU, 15 GiB, Ubuntu, Docker), then to MCI's OpenShift. Two
MCI documents set the terms (`docs/operations/deployment/03-…`, `04-…`):

- the DevOps guide: **a container that runs as root fails to deploy**; no cluster-scope permissions;
  no PostgreSQL operator; high availability, backups and the second site are the project's;
- the security red lines (FR-NSM-204/00): non-root, read-only root filesystem, only the capabilities
  needed, no privileged containers, multi-stage builds, no `latest`, resource limits, HTTPS with an
  internal-CA certificate, services isolated from each other except on the ports they need.

`Program.cs` named this ADR as the place to decide where the import worker runs in production.

## Decision

1. **Four images built from this repository**, from two Dockerfiles in `deploy/images/`: `api`,
   `worker` and `migrator` (one multi-stage .NET build, three targets), and `web` (the built SPA in
   unprivileged nginx). **ClickHouse and PostgreSQL are their official images, configured rather than
   rebuilt**: measured on 2026-10-22, both run under the restricted contract below once given the
   right configuration, so there is no image of ours to patch for them.

2. **Every container runs the way OpenShift's restricted-v2 SCC runs it - in compose too**: an
   arbitrary UID in group 0 (`1000700000:0` by default), read-only root filesystem, all capabilities
   dropped, no privilege escalation, `/tmp` the only scratch space, no port below 1024 inside a
   container, a memory and CPU limit on each. The images make it work by owning their files as root
   and only reading them; the one directory the application writes (uploads) is group-0 writable.
   If the stack runs on the VM, the same images run on OpenShift.

3. **The import worker is its own container, one replica.** This settles what `Program.cs` left to
   this record. In-process hosting stays the Development default (one `dotnet run` is the whole
   system), and its costs, stated in `Program.cs`, are the reasons against it in production: an
   import competes with the API for the process that serves the progress page, and the two cannot
   be restarted or sized apart. One replica, because `ImportWorker:MaxConcurrentJobs` is 1 on
   measurement - imports share ClickHouse's disk.

4. **One front door.** nginx serves the SPA and forwards `/api/` to the API, as Vite's proxy does in
   development, so the session cookie stays first-party and CORS is unused. TLS terminates there (TLS
   1.2 and 1.3 only). Only the upload endpoint accepts a large body, streamed, not buffered. The API
   trusts `X-Forwarded-For`/`-Proto` (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`) because nothing else can
   reach it - it publishes no port, and nginx overwrites the header rather than appending to it.
   Without that, every request would come from nginx's address: one login rate limit for everybody,
   and nginx's address in every audit row.

5. **Schema change runs as one-shot jobs before the application starts**: `migrate` (both stores,
   forward-only, checksummed - unchanged) then `grants` (`db/operational/grants`, through `psql`).
   API and worker start only after both succeed. On OpenShift they become ArgoCD PreSync Jobs.

6. **Configuration from the environment; secrets never in an image or in Git.** On the VM an
   untracked `.env` (mode 600); on OpenShift, Secrets. ClickHouse reads its users' passwords from the
   environment (`from_env` in `users.xml`), so the same configuration file serves both.

7. **Docker Compose for stage 1** (`deploy/compose/compose.yaml`); **plain manifests, Kustomize, for
   stage 2**, written when the dev namespace exists. The compose file is the specification they are
   derived from (`03-…`, section 2).

## Alternatives rejected

| Alternative | Why not |
|---|---|
| Our own ClickHouse and PostgreSQL images | Nothing to add: configuration and a volume layout are enough (measured). An image of ours is one more thing to patch every release |
| The API serves the SPA itself (static files in Kestrel) | One container fewer, but then Kestrel faces users directly: TLS, the upload streaming limit, caching and any path-based restriction (the red lines' separate administration panel) would all be code. nginx is on the security office's approved list; Kestrel is not |
| A Helm chart | The platform grants no cluster-scope permissions and the guide recommends plain manifests through ArgoCD; a chart here would be stripped back to that |
| Kubernetes (k3s) or Podman on the VM, to match OpenShift more closely | More to operate for a test stage, and the red lines forbid running an orchestrator on a standalone server anyway (§1-1). Compose is the minimum that runs the images under the same constraints |
| Secrets as files (`KeyPerFile` configuration) | More private than environment variables (not visible in `docker inspect`), at the cost of a package and a code change. Environment variables are the standard on both platforms; recorded in `docs/backlog.md` |
| Red Hat UBI base images | May be what the security office wants (red lines §1-1, row 3). Not chosen yet: Microsoft's .NET image is what the SDK is pinned against, and UBI's .NET 10 image was not reachable from the laptop (2026-10-22). The Dockerfiles take the base image as a build argument, so it is a rebuild, not a rewrite |

## Consequences

- The laptop's development loop is unchanged: `infra/docker-compose.yml`, `dotnet run`,
  `npm run dev`. Nothing in `deploy/` touches its volumes.
- **The API could not be published at all** until this change: its reference to `Sqm.Ingestion`
  (for in-process hosting) carried the worker's `appsettings*.json` into its output under the API's
  own names, and `dotnet publish` refused the duplicates (NETSDK1152). The API had only ever been
  `dotnet run`. `Sqm.Api.csproj` now drops the worker's settings files from what the API publishes;
  without that target the publish fails again.
- **The worker on its own could not read from ClickHouse** - the first time it ran as its own
  process. The read paths it shares with the API ask for the API's HTTP client, which only the API
  registers; alone, the worker got a default client that does not decompress ClickHouse's responses,
  and a TAC upload failed on every attempt. The worker's own host now registers that client
  (`AddStandaloneQueryClient`), with a test that fails against the old code. This is the cost of
  in-process hosting in development, stated in `Program.cs`, showing up exactly where it said it
  would: the worker had never been run the way production runs it.
- **The VM's disk is too small for the data** - 182.5 GiB of ClickHouse and 40.6 GiB of originals
  against 200 GB with 41.9 GB on `/var` (`02-…`, section 1). Stage 1 waits on a data disk.
- A deployment that is not HTTPS does not work: the session cookie is Secure. That is deliberate.
- The worker has no health probe; a hung worker is not restarted. Recorded in `docs/backlog.md`.
- ClickHouse's application users still hold every privilege (as in development). Two secrets, one
  privilege set, until the GRANTs are written and tested (`04-…`, §1-4 row 11).

## Evidence

`docs/operations/deployment/01-container-images.md`, "Verified" - 26 checks on the stack built from
these files and run on the laptop: every container's UID and read-only root, network isolation, the
migrations on empty stores, sign-in over HTTPS, the TLS floor, a 206 MB upload through nginx
imported by the worker, the client address in the audit log (and nginx's address when forwarding is
switched off), a full stop and start.
