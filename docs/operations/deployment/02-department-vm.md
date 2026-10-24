# Stage 1 - QuadraSense on the department VM

**Status:** runbook, written 2026-10-22. **Running on the VM since 2026-10-24, empty** - no real
data until the data disk and the data decision (section 9 records that first deployment and what
it changed in this runbook).

**Purpose of this stage.** Run the real system away from the laptop, on infrastructure the department
operates, and find what breaks: the images, the configuration, the operating routine (upgrades,
backups, imports), and the performance on hardware that is not a laptop. It is also the rehearsal for
OpenShift - the containers run here exactly as OpenShift will run them.

---

## 1. The VM, and what QuadraSense needs from it

| | The VM | QuadraSense needs | |
|---|---|---|---|
| CPU | 8 vCPU, Xeon Gold 6148 | 6 for ClickHouse at peak, 2 each for API and worker; limits, not reservations | enough |
| Memory | 15 GiB usable, 2 GiB swap | 12.75 GiB of container limits (below), ~2 GiB for the host | enough, barely |
| Disk | 200 GB in total: `/` 110 GB, `/var` 41.9 GB, the rest small | **~225 GiB today** (ClickHouse 182.5 + originals 40.6), **+~30 GiB a month** | **not enough** |
| OS | Ubuntu 26.04.1 LTS | Docker Engine with the compose plugin; nothing else | fine (but see red lines §1-1, row 3) |
| Containers | Docker CLI and socket present, access not verified | Someone who can run `docker` | unknown |

### Disk - the blocker

Measured on the laptop on 2026-10-22, with data through 10 October:

| | Size | Growth |
|---|---|---|
| ClickHouse | 182.5 GiB | event log ~290 MiB a day (10 M events), history tables ~3.5 GiB a month each, ×3: **~20 GiB a month** |
| Uploaded originals | 40.6 GiB (118 files) | ~350 MB per daily file on average, the largest ~1.1 GB: **~10.5 GB a month** (corrected 2026-10-24 from the files themselves; this page first said 33) |
| PostgreSQL | 332 MB | negligible |
| Images | ~2 GB | per release |

Docker keeps its volumes under `/var/lib/docker` by default - the 41.9 GB `/var`. Even moved to `/`
(110 GB) the data does not fit, and a year from now it is ~600 GiB.

**What to ask the department for:** a separate data disk of **1 TB** (about two years at the current feed),
as its own logical volume mounted at `/srv/quadrasense`, `noexec,nodev,nosuid`, with Docker's
`data-root` moved onto it. Without it, stage 1 can run only on a sample of the data - which tests the
deployment but not the system, and makes every timing meaningless.

### Memory

| Container | Limit | Why |
|---|---|---|
| ClickHouse | 8 GiB | its ceiling is 80% of this (`deploy/compose/clickhouse/memory.xml`); the laptop gives it 6 |
| worker | 2 GiB | streams the file; parsing buffers |
| API | 1.5 GiB | Argon2id is 19 MiB per login; exports |
| PostgreSQL | 1 GiB | a 332 MB database |
| web | 256 MiB | nginx |
| **Total** | **12.75 GiB** | of 15; the host and Docker keep ~2 |

**The VM is not the dedicated hardware of `docs/operations/dedicated-hardware.md`.** It gives
ClickHouse a third more memory than the laptop, not several times more. So the laptop's compromises
stay: the 2 GiB merge cap, single-threaded folds, `binding_current` at ~7 versions per binding and
`FINAL` at a minute or more. What changes is disk speed (measure it - section 7), and that nothing
competes with a desktop.

## 2. Before anything is installed - for the department

| # | What | Who |
|---|---|---|
| 1 | The data disk (section 1) | department infrastructure |
| 2 | Docker Engine and the compose plugin from Docker's own repository (not Ubuntu's `docker.io`, which lags); `docker compose version` ≥ 2.24 | department |
| 3 | Docker's `data-root` on the data disk: `/etc/docker/daemon.json` → `{"data-root": "/srv/quadrasense/docker", "log-driver": "json-file", "log-opts": {"max-size": "50m", "max-file": "5"}}`, then restart Docker | department |
| 4 | Who runs `docker`: membership of the `docker` group **is root on that machine** (red lines §2-1 row 7). Options: a named operator with `sudo` for `docker` only; or rootless Docker for that user | department, with the owner |
| 5 | A DNS name for the VM, and a TLS certificate for it from MCI's internal CA (red lines §2-1 row 8) - `tls.crt` with its chain, `tls.key` | department / MCI's Microsoft team |
| 6 | Firewall: 443 (and 80, which only redirects) from the users' networks; SSH only from PAM. Nothing else inbound | department |
| 7 | Time synchronisation (NTP / chrony) - TLS, sessions and ClickHouse's dates depend on it | department |
| 8 | Transparent huge pages `madvise` (ClickHouse's recommendation); swap left as it is, `vm.swappiness=1` | department |
| 9 | Whether real subscriber data may live on this VM, and who may reach it (red lines §1-3 row 2 forbids real data on a *security test bed*; this is not one, but the department must agree) | the owner |

## 3. Getting the software onto the VM

The VM may not reach Docker Hub or GitHub. So the images are built elsewhere and carried in.

```bash
# On the laptop (or a build machine), from the repository root:
TAG=$(git rev-parse --short=12 HEAD)
for target in api worker migrator; do
  docker build -f deploy/images/backend/Dockerfile --target $target -t quadrasense/$target:$TAG .
done
docker build -f deploy/images/web/Dockerfile -t quadrasense/web:$TAG .

docker save quadrasense/api:$TAG quadrasense/worker:$TAG quadrasense/migrator:$TAG \
            quadrasense/web:$TAG \
            clickhouse/clickhouse-server:25.8.33.6 postgres:17.11-bookworm \
  | gzip > quadrasense-images-$TAG.tar.gz
git archive --format=tar.gz -o quadrasense-$TAG.tar.gz HEAD
```

Both files go to the VM by a route the department approves - the red lines forbid moving files
between servers over SSH (§1-2 row 3), so not `scp` from another server. Then:

```bash
docker load < quadrasense-images-$TAG.tar.gz
mkdir -p /srv/quadrasense/app && tar -xzf quadrasense-$TAG.tar.gz -C /srv/quadrasense/app
```

**If the VM can reach MCI's Nexus**, the department's alternative: push the images to it from the
build machine and `docker compose pull` on the VM. Set `SQM_REGISTRY` to the repository path.

## 4. First start

```bash
cd /srv/quadrasense/app/deploy/compose
cp .env.example .env && chmod 600 .env
# fill in: SQM_TAG, five passwords (openssl rand -hex 24), SQM_PUBLIC_ORIGIN
mkdir tls && cp /path/to/tls.crt /path/to/tls.key tls/
chown root:root tls/* && chmod 640 tls/*      # the containers run in group 0

docker compose up -d
docker compose logs -f migrate grants        # both must end with exit code 0
docker compose ps                            # clickhouse, postgres, api, web healthy; worker up
```

On a new machine the migrate job creates both schemas from nothing (16 PostgreSQL migrations and
27 ClickHouse ones, as of 2026-10-22) and the grants job creates `sqm_app`. Every later `up` runs them again; each is a
no-op when up to date.

**The first administrator** - once; the password is typed, never an argument:

```bash
docker compose run --rm -it --entrypoint dotnet migrate \
  Sqm.Migrator.dll --create-admin --username admin --display-name "System Administrator"
```

Then `https://<the VM's name>/` and sign in.

## 5. Loading the data

| Way | How | Time | Use it for |
|---|---|---|---|
| **A. Copy the stores** | ClickHouse: stop it on the laptop, archive the `bench_chdata` volume, restore it as `quadrasense_clickhouse-data` (same 25.8 version, so the files are compatible); or `BACKUP`/`RESTORE` to a disk. PostgreSQL: `pg_dump -Fc` / `pg_restore` | the time to move ~185 GiB | **the first load** - exact, fastest |
| B. Replay the originals | Load the dump, then upload every daily file through the Import Center, oldest first. **The dump has no supported path in** (found 2026-10-24): the Import Center rejects a file whose name is not one day, and on the laptop it was loaded by hand with SQL. See the backlog | measured on the laptop: a delivery's mart refresh alone 63 min; ~250 deliveries | proving the archive restores the system - once, later |
| C. A sample | The dump and the first days or weeks after it (a sample without the dump is wrong from the first remove) | minutes to hours | testing the deployment and the pipeline; every count is of the sample only |

Recommended: **A**, then daily files through the Import Center as they arrive.

Two things about copying ClickHouse's volume:

- **Ownership.** On the laptop the files belong to ClickHouse's own user (UID 101); here the server
  runs as `SQM_RUN_UID` in group 0 and cannot read them. After restoring, as root on the VM:
  `chown -R <SQM_RUN_UID>:0 /srv/quadrasense/docker/volumes/quadrasense_clickhouse-data/_data`.
  (OpenShift does the equivalent itself, through the pod's fsGroup.)
- **Users.** The volume carries no users - they are defined in `users.xml` - so the deployment's
  users and passwords apply as soon as it starts.

Caveat for A: the copy moves real subscriber data off the laptop - point 9 of section 2 first.

## 6. Operating it

| Task | How |
|---|---|
| **Daily files** | Import Center upload, as on the laptop. Whether the operator's files can reach the VM by SFTP instead is a decision (red lines, SFTP section) |
| **Upgrade** | Load the new images; set `SQM_TAG`; `docker compose up -d`. migrate runs first; the API and worker start only after it succeeds. A failed migration leaves the old containers running |
| **Logs** | `docker compose logs <service>`; JSON from the API, worker and nginx. Rotated by the json-file limits (50 MB × 5 per container) |
| **Health** | `docker compose ps`; `/health/ready` (inside: `docker compose exec api bash -c ...`, or through the web container) |
| **ClickHouse by hand** | `docker compose exec clickhouse clickhouse-client` - the `default` user works from inside the container only |
| **Stop** | `docker compose stop` - ClickHouse gets two minutes to stop cleanly; an unclean stop costs a merge backlog at the next start |

### Backups

There are none until this is done, and the data disk is one disk.

| What | How | Where to |
|---|---|---|
| PostgreSQL | nightly `docker compose exec -T postgres pg_dump -U sqm -Fc sqm > sqm-$(date +%F).dump` | off the VM |
| ClickHouse | `BACKUP DATABASE sqm TO Disk('backups', ...)` after adding a backup disk to the config; or an LVM snapshot of the data volume with ClickHouse stopped | off the VM |
| Originals | they are the backup of everything else - so they need one of their own | off the VM |
| `.env`, `tls/` | with the department's secret handling | not in Git |

"Off the VM" needs a place: a NAS, a backup server, the department's backup system. That is a
question for the department (README, "Decisions needed").

## 7. Acceptance - what to check on the VM

Each is a measurement to record in `docs/operations/dedicated-hardware.md` beside the laptop's.

| # | Check | Expect |
|---|---|---|
| 1 | `docker compose ps` | all healthy; migrate and grants exited 0 |
| 2 | `docker compose exec api id` (and each service) | `uid=1000700000 gid=0`, never 0 |
| 3 | Sign in over HTTPS; the audit page shows your workstation's address, not the web container's | forwarded headers work |
| 4 | Upload a daily file; follow it to "Completed" | the import path works across containers |
| 5 | The dashboard against the laptop's for the same delivery | identical figures |
| 6 | `dotnet test backend/Sqm.slnx` from a machine that can reach the stores, or the integration tests pointed at them | the real-data tests' timing bounds hold |
| 7 | `SELECT count() FROM sqm.binding_current FINAL` and the filtered KPI | the laptop: 74 s and 100 s (ADR-017); disk speed decides the VM's |
| 8 | A full mart refresh (`--refresh-dashboard`, in the worker image) | the laptop: 63 min for delivery 246 |
| 9 | `docker compose restart clickhouse` while idle; then under an import | comes back healthy; the import resumes from its lease |
| 10 | Restore last night's PostgreSQL dump into a scratch container | it restores |

## 8. Order of work for stage 1

**T** technical/infrastructure, **P** programming, **A** architecture, **R** request or decision.

| # | Step | Kind | Waits on |
|---|---|---|---|
| 1 | Data disk; Docker installed with data-root on it; operator access | R, T | the department |
| 2 | DNS name and certificate from the internal CA | R | the department |
| 3 | Decide: real data on the VM; the route for moving files to it | R | the owner, the department |
| 4 | Build and carry the images and the bundle (section 3) | T | 1 |
| 5 | First start; administrator; sign in (section 4) | T | 2, 4 |
| 6 | Load the data (section 5, A) | T | 3, 5 |
| 7 | Acceptance checks (section 7), recorded in `dedicated-hardware.md` | T | 6 |
| 8 | Backups off the VM, and one restore test | T, R | 7, a destination |
| 9 | Run it daily for a few weeks: imports, upgrades, a restart; write down what breaks | T | 7 |
| 10 | What stage 1 taught goes into `03-mci-openshift.md` before any ticket is filed | A | 9 |

## 9. First deployment - 2026-10-24

The empty system, commit `d16ce7c9a1cc`, brought up on the VM over SSH from the laptop.

**What the VM turned out to be**, against what sections 1-3 assumed:

| | Assumed | Found |
|---|---|---|
| Docker | Docker's own packages, to be installed | Ubuntu's `docker.io` 29.1.3 and Compose 2.40.3, already running; data-root `/var/lib/docker`; cgroup v2 |
| Operator access | to be arranged | the operator account was added to the `docker` group by the department (root-equivalent - section 2 row 4) |
| Internet | possibly none | **available**: the VM pulled ClickHouse and PostgreSQL from Docker Hub in 34 s (~14 MB/s) |
| Laptop to VM | - | **~340 KB/s**: the full image bundle (512 MB) did not finish in 15 minutes |
| `/tmp` | a disk | a 7.6 GB **tmpfs** - memory, not a place for a bundle |
| Home directory | - | 4.9 GB, mode 750 |

**What that changed in the procedure:**

- **Only the four application images travel** - `docker save` of `quadrasense/*` is 127 MB and copied
  in 5.5 minutes; the VM pulls the two store images itself (`docker compose pull clickhouse postgres`).
- **The TLS key without root.** Section 4's `chown root:root` needs root. Instead the key is mode
  644 inside `tls/` under the operator's home: the containers' UID can read it through the bind
  mount, and nobody else on the host can reach it, because the home directory is 750.
- **A temporary self-signed certificate** (90 days, for the VM's address and hostname) until the
  internal CA's arrives; browsers warn until then.
- **Passwords generated on the VM** with `openssl rand -hex 24` straight into `.env` (mode 600);
  they never existed anywhere else.

**What was checked** (section 7):

| # | Check | Result |
|---|---|---|
| 1 | `docker compose up -d` | all healthy in 33 s; migrate applied 16 + 27 migrations, grants all 4 files, both exit 0 |
| 2 | `id`, `touch /etc/x` in every service | `uid=1000700000 gid=0`, read-only root, in all five |
| 3 | From the laptop, HTTPS on the VM's address | 200 in 0.35 s - the firewall admits 443; HTTP answers 308 to HTTPS; `/api/v1/auth/me` without a session 401 |
| - | Idle memory | ClickHouse 215 MiB of 8 GiB; API, worker, PostgreSQL under 50 MiB; web 8 MiB |
| - | Disk | `/var` 2.5 GB used of 42 |

Not yet done: the first administrator (the owner's, with the owner's password), checks 3-10 of
section 7 that need data, backups, and everything that waits on the data disk.
