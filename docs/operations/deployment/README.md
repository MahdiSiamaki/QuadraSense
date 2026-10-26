# Deploying QuadraSense - from the laptop to MCI's OpenShift

**Status:** started 2026-10-22, at the product owner's request: package the whole system as Docker
Compose that is also fit for OpenShift (non-root), deploy it first on the department's VM, then on
MCI's OpenShift, and write down every step and what each stage needs.

| | Where | Runs as | State |
|---|---|---|---|
| Today | the owner's laptop | `infra/docker-compose.yml` for the stores; `dotnet run`, `npm run dev` | in use |
| **Stage 1** | the department VM (8 vCPU, 15 GiB, 200 GB, Ubuntu 26.04) | `deploy/compose/compose.yaml` | **running since 2026-10-24** (`02-…` §9): the dump, GSMA v2026.10.04 and the daily files to 2026-02-28; the full history waits on a data disk. Deployed by the department pipeline once its two Nexus passwords are set (`05-…` §7), `update.sh` until then |
| **Stage 2** | MCI's OpenShift - dev, uat, prd namespaces | the same images, as Kubernetes objects through ArgoCD | planned |

The decision behind the shape of it is **ADR-007**.

## The documents

| | |
|---|---|
| `01-container-images.md` | The images: what each is, the rules that make them run on OpenShift unchanged, how to build them, what was verified |
| `02-department-vm.md` | Stage 1: what the VM lacks, what to ask the department for, the runbook, the acceptance checks |
| `03-mci-openshift.md` | Stage 2: how MCI's platform works, QuadraSense as OpenShift objects, traffic flow, storage, HA, backup, monitoring, and the ordered work |
| `04-security-red-lines.md` | MCI's security red lines (FR-NSM-204/00), row by row, against QuadraSense today |
| `05-department-ci-cd.md` | The department's infrastructure (Nexus, the server) and the CI/CD pipeline to build on it: today's git routine, credentials, the target pipeline, the decisions it needs |

## The files

| Path | What |
|---|---|
| `deploy/images/backend/Dockerfile` | `api`, `worker`, `migrator` - one build, three targets |
| `deploy/images/web/` | `web`: the SPA in unprivileged nginx, and its configuration |
| `deploy/compose/compose.yaml` | the whole system; `.env.example` lists every variable |
| `deploy/compose/update.sh` | every deployment on a server: pull from GitHub, build there, restart (`02-…` §3) |
| `deploy/compose/clickhouse/` | ClickHouse's users (passwords from the environment), memory, system logs, logging |
| `deploy/compose/web/server-https.conf` | the HTTPS server block mounted into `web` |
| `.dockerignore` | what never enters an image - data, credentials, `.claude/`, tests |

## About the MCI documents

Two documents from MCI set the terms of stage 2: the DevOps team's guide to the OpenShift platform
(version 1.5) and the network-security office's red lines (FR-NSM-204/00). **The guide is marked as
MCI's internal confidential document, and this repository is public.** So these pages say what the
documents *require*, in our own words, with section references - and leave out every address,
hostname, URL, contact and ticket path. Those are in the documents, which the owner holds.

## Decisions needed

Each blocks something; each has a recommendation. Numbers are referred to from the other pages.

| # | Decision | Blocks | Recommendation |
|---|---|---|---|
| 1 | **The VM's disk.** 200 GB cannot hold 182.5 GiB of ClickHouse plus 40.6 GiB of originals, growing ~30 GiB a month (`02-…` §1) | stage 1 with real data | Ask the department for a **1 TB** data disk (2 TB for two years) as its own volume |
| 2 | **Real subscriber data on the VM** - who may reach it, and how the data gets there (the red lines forbid copying between servers over SSH) | loading data | Agree it with the department in writing before copying |
| 3 | **Does the red line against Docker on standalone servers (§1-1) cover the department VM?** | stage 1 at all | Ask the department; if it does, stage 1 runs only as an exception |
| 4 | **Who runs `docker` on the VM** - the `docker` group is root on that machine | stage 1 | One named operator with `sudo` for `docker` |
| 5 | **ClickHouse needs the security office's approval** - it is not on the approved list | stage 2 | Ask for the meeting now, with ADR-003's measurements; everything else depends on it |
| 6 | **Captcha in Persian after three failed logins (red lines §2-3) against English-only (CLAUDE.md)** | the security review | Ask the security office whether rate limiting plus Argon2id (ADR-006) is accepted on an internal system; if not, a Persian captcha on the login page only, as the one exception to English-only |
| 7 | **Active-active across two sites (red lines §2-3)** | production | Ask what is required for an internal analytics system; propose replicated stores in one site first, a second site as a warm standby (`03-…` §5) |
| 8 | **A synthetic data set** - the security test bed may not hold real data (§1-3) | the security review | Build the generator (a few days of work); it also gives the project test data it can share |
| 9 | **Base images**: Microsoft's/Debian/Alpine, or Red Hat UBI where one exists (§1-1, row 3) | stage 2 | Ask the security office; the Dockerfiles take the base as a build argument either way |
| 10 | **Where the source lives.** It is on GitHub, **public**, and stage 2 builds from MCI's GitLab | stage 2 | Make the GitHub repository private now - an operator's system and its operational documents should not be public - then mirror to MCI's GitLab and build there |
| 11 | **How the operator's daily files arrive**: browser upload (today) or the central SFTP server | daily operation on the VM | Keep the browser upload for stage 1; decide SFTP with the operator for stage 2 |
| 12 | **A separate administration entry point** (§1-2) | the security review | Ask; if required, nginx splits the user and role administration paths onto a second address reachable only from PAM - no code change |
| 13 | **TLS between services** (API to ClickHouse and PostgreSQL) - the red lines say HTTPS for every application | stage 2 | Ask whether it covers traffic inside the namespace; both stores support it |
| 14 | **RPO and RTO**, and where backups go (off the VM; S3 on OpenShift) | backups | The owner sets them; the department names the destination |

## Access and resources to arrange

| For | What | From |
|---|---|---|
| Stage 1 | A 1 TB data disk on the VM; Docker Engine + compose plugin; data-root on the disk | the department |
| Stage 1 | An operator account that can run `docker`; SSH to the VM from PAM | the department |
| Stage 1 | A DNS name and a certificate from MCI's internal CA | the department; MCI's Microsoft team |
| Stage 1 | A route to move the images (~2 GB) and the data (~185 GiB) onto the VM | the department |
| Stage 1 | A backup destination off the VM | the department |
| Stage 2 | Domain accounts, PAM and terminal-server access for each person who deploys | MCI, by SDM ticket |
| Stage 2 | dev / uat / prd namespaces with the storage of `03-…` §4; a GitLab group and runner; a hosted Nexus repository | MCI, by SDM ticket |
| Stage 2 | Firewall openings for the base-image registries the runner must reach (Microsoft's MCR, Docker Hub) unless the Nexus proxies serve them | MCI, by ticket |
| Stage 2 | An ingress IP for the front door; an egress IP if alerts are sent by mail | MCI, by SDM ticket |
| Stage 2 | Log forwarding to central ELK / the SOC; a Grafana instance | MCI, by ticket |
| Stage 2 | The meeting with the security office (decisions 5-9, 12, 13) | the owner |
