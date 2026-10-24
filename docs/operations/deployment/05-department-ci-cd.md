# CI/CD on the department's infrastructure

**Status:** written 2026-10-24. The routine in section 2 runs today; the pipeline of section 4 is a
design waiting on the decisions in section 5.

**Read with:** `02-department-vm.md` (the server), `03-mci-openshift.md` §3 (MCI's own pipeline,
which this one rehearses), ADR-007.

**Addresses and accounts are placeholders here.** This repository is public; the department's
hostnames, users and passwords are not written into it. The owner holds them; on the server they
live in Docker's login store and in `deploy/compose/.env`, never in git.

| Placeholder | What it is |
|---|---|
| `<server>` | the department VM that runs QuadraSense (stage 1) |
| `<nexus>` | the department's Nexus - its UI and non-Docker repositories |
| `<nexus-pull>` | Nexus's Docker endpoint for **pulling**: a proxy/group of public registries |
| `<nexus-push>` | Nexus's Docker endpoint for **pushing** our own images: a hosted repository |
| `<pull-user>`, `<push-user>` | one account each: the server only ever needs the first |

---

## 1. The department's infrastructure, as measured

| | Found (2026-10-24) |
|---|---|
| Server | Ubuntu 26.04, 8 vCPU, 15 GiB; disk grown to 500 GB, all into `/` (440 GB); `/var` 42 GB. Docker 29 (Ubuntu's package), images in containerd's store under `/var/lib/containerd`, volumes under Docker's data root |
| Access | SSH with a key, from the department network or its WireGuard VPN. The operator account is in the `docker` group; `sudo` needs its password |
| Internet | from the server, yes: GitHub, Docker Hub, Microsoft's registry |
| Nexus | three names, one internal address, reachable from the server. The UI answers 200; both Docker endpoints answer 401 to an anonymous request - they want a login, as they should |
| Not yet known | whether `<nexus-pull>` carries Microsoft's registry as well as Docker Hub; whether `<nexus>` proxies NuGet and npm |

## 2. Today: git, built on the server

The product owner's routine: changes are pushed to GitHub; the server pulls and builds.

```bash
~/quadrasense/repo/deploy/compose/update.sh
```

It pulls the branch, builds the four application images on the server tagged with the commit,
records the tag in `.env` and starts them (`02-…` §3). It needs nothing from Nexus - which is also
its weakness: base images, NuGet and npm come straight from the internet (red lines §2-1 ask for
the central repositories), nothing tests the commit before it runs, and each server builds its own
images, so two servers can run different bytes for the same commit.

## 3. Credentials - where each one lives

| Credential | Lives in | Never in |
|---|---|---|
| `<pull-user>` | the server's Docker login store: `docker login <nexus-pull>` run **by the owner** on the server (stored in `~/.docker/config.json`, mode 600 - base64, not encryption: the operator account is the boundary) | git, chat, a script |
| `<push-user>` | the CI system's secret variables only. The server never pushes | the server, git, chat |
| SSH key for deployment (section 4) | the CI system's secret variables; its public half in the server's `authorized_keys`, restricted to the deploy command | anywhere else |

**Both Nexus passwords were sent in a chat on 2026-10-24. Rotate them**, and put the new ones only
where this table says.

## 4. The pipeline to build

```
push to GitHub ──> CI runner in the department network
                     1. build the four images   (base images via <nexus-pull>; NuGet, npm via <nexus> if proxied)
                     2. test                     (dotnet test; npm test; npm run build)
                     3. push, tagged with commit (<nexus-push>/quadrasense/<image>:<sha>)
                     4. deploy                   (on <server>: pull those tags, docker compose up -d)
```

| Step | How | Why this way |
|---|---|---|
| Runner | **in the department network** - GitHub's hosted runners cannot reach an internal Nexus or server | the only place that can see both |
| Build | the two Dockerfiles, with `SDK_IMAGE`, `RUNTIME_IMAGE`, `NODE_IMAGE`, `NGINX_IMAGE` pointed at `<nexus-pull>` and `NUGET_SOURCE`, `NPM_REGISTRY` at `<nexus>` if it proxies them (build arguments that exist already - `01-container-images.md`) | the red lines' central repositories; builds that do not depend on the public internet |
| Test | the unit tests everywhere; the integration tests skip without a database - stated in the run, never read as a pass (CLAUDE.md) | a commit that fails its tests never reaches the server |
| Push | `<nexus-push>/quadrasense/{api,worker,migrator,web}:<commit>` - never `latest` | one set of bytes per commit, built once |
| Deploy | the server pulls: `.env` sets `SQM_REGISTRY=<nexus-pull>/quadrasense` (if the pull endpoint groups the hosted repository) and the runner runs `SQM_TAG=<commit> docker compose pull && docker compose up -d` over SSH | what ran in CI is what runs; no compiler on the server |
| Stores | `CLICKHOUSE_IMAGE` and `POSTGRES_IMAGE` in `.env` pointed at `<nexus-pull>` | same reason as the base images |

`update.sh` stays as the fallback for when the pipeline is down.

## 5. Decisions this needs

| # | Decision | Recommendation |
|---|---|---|
| 1 | **Which CI system** runs in the department: GitHub Actions with a self-hosted runner, the department's GitLab (if there is one), Jenkins | Whatever the department already operates. If nothing: GitHub Actions with a self-hosted runner - the code is on GitHub, and the runner only connects out |
| 2 | **Make the repository private first** | **Required before any self-hosted runner.** On a public repository, a pull request from anyone's fork can run its own code on the runner - inside the department network. GitHub's own guidance says never to pair the two |
| 3 | **Where the runner runs**: the application server, or a separate build machine | A separate machine: builds take CPU, memory and disk from the databases (`02-…` §1 - the server has 15 GiB) |
| 4 | **Does `<nexus-pull>` include the hosted repository** (a group), and does it proxy Microsoft's registry, NuGet, npm | To check with one `docker login` and pulls; decides whether one or two Docker endpoints go into `.env` |
| 5 | **Which branch deploys** | `main`, once the deployment branch is merged; until then that branch |

## 6. Order of work

| # | Step | Kind | Waits on |
|---|---|---|---|
| 1 | Rotate the two Nexus passwords; the owner runs `docker login <nexus-pull>` on the server | R | the owner |
| 2 | Check what `<nexus-pull>` carries: Docker Hub, MCR, our hosted repository; whether `<nexus>` proxies NuGet and npm | T | 1 |
| 3 | Point `.env`'s store images and `update.sh`'s builds at Nexus | T | 2 |
| 4 | Decide 1-3 of section 5; make the repository private | R | the owner |
| 5 | Install the runner; the pipeline file (build, test, push, deploy) | T | 4 |
| 6 | Run it once end to end; the server deploys from Nexus | T | 5 |
