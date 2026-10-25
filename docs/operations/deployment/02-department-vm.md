# Stage 1 - QuadraSense on the department VM

**Status:** runbook, written 2026-10-22. **Running on the VM since 2026-10-24**, from
`/opt/quadrasense`, with the one-month dump loaded; no daily files and no GSMA TAC version yet
(section 9 records each session on the VM and what it changed in this runbook).

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

**By git - the routine** (the product owner's, 2026-10-24). Changes are pushed to GitHub; the server
pulls them and builds the images itself, from its own checkout. It needs the internet for that -
GitHub, Docker Hub, Microsoft's registry, NuGet and npm - which this VM has (section 9).

**Where everything lives on the server** - under `/opt/quadrasense`, never the operator's home:
`/home` is a 4.9 GB volume of its own, and `/` has the ~400 GB (section 9, third session).

| Path | What |
|---|---|
| `/opt/quadrasense/config/` | `.env` and `tls/` - the server's one configuration, read by `update.sh` and by the deploy runner (05 §7); never in git |
| `/opt/quadrasense/repo/` | the checkout `update.sh` deploys from; its `deploy/compose/.env` and `tls` are links into `config/` |
| `/opt/quadrasense/releases/<commit>/`, `current` | what the CD job deploys (05 §7) |
| `/opt/quadrasense/incoming/` | files carried to the server for the worker by hand - the dump |
| `/opt/quadrasense/logs/` | the output of runs by hand: `update.sh`, `--load-dump` |
| `/srv/docker` (Docker's data root) | the volumes: ClickHouse, PostgreSQL, the uploaded originals |

`/opt/quadrasense` is `root:deploy`, mode 2770 (`deploy/host/install-deploy-runner.sh` creates it):
nobody outside root and the `deploy` group can enter it, and what is created inside belongs to the
group. The operator joins the group once - `sudo usermod -aG deploy <operator>`, then a new login.

Once, as the operator, a checkout of the branch being deployed, with its configuration linked in:

```bash
git clone --branch deploy/containers-and-openshift \
  https://github.com/MahdiSiamaki/QuadraSense.git /opt/quadrasense/repo
cd /opt/quadrasense/repo/deploy/compose
ln -s /opt/quadrasense/config/.env .env && ln -s /opt/quadrasense/config/tls tls
```

Every deployment after that, including the first:

```bash
/opt/quadrasense/repo/deploy/compose/update.sh
```

`update.sh` refuses a checkout with local edits, pulls (fast-forward only) and then runs the
`update.sh` it pulled, from the top - so a change to the script applies on the run that brings it.
It builds the four application images tagged with the commit (`SQM_TAG`), writes that tag into
`.env` through the link, and `docker compose up -d`. The stores' images are pulled from Docker Hub.

**It breaks a department rule**, learned after it was built: Nexus is the only permitted source of
dependencies and no build may reach the internet directly - and `update.sh` builds from MCR, Docker
Hub, NuGet and npm. It is the interim, until the department pipeline of `05-department-ci-cd.md`
replaces it; then it stays only as the fallback.

- **When the branch is merged**, switch the checkout to `main` (`git switch main`) - and deploy from
  `main` only, as everything else does.
- **If the repository becomes private** (README, decision 10), the server needs a read-only deploy
  key for it; the operator's own credentials do not belong on a server.
- **Rollback**: earlier builds' images stay on the machine; `SQM_TAG=<older commit> docker compose up -d`
  starts them. Migrations are forward-only, so a rollback across a migration is a forward fix instead.

**Without internet** - the fallback. Build and save the images elsewhere and carry them in:

```bash
# On a machine with internet, from the repository root:
TAG=$(git rev-parse --short=12 HEAD)
for target in api worker migrator; do
  docker build -f deploy/images/backend/Dockerfile --target $target -t quadrasense/$target:$TAG .
done
docker build -f deploy/images/web/Dockerfile -t quadrasense/web:$TAG .
docker save quadrasense/api:$TAG quadrasense/worker:$TAG quadrasense/migrator:$TAG \
            quadrasense/web:$TAG clickhouse/clickhouse-server:25.8.33.6 postgres:17.11-bookworm \
  | gzip > quadrasense-images-$TAG.tar.gz
git archive --format=tar.gz -o quadrasense-$TAG.tar.gz HEAD
```

Both files go to the server by a route the department approves - the red lines forbid moving files
between servers over SSH (section 1-2 row 3). Then `docker load`, unpack, and `docker compose up -d`
with `SQM_TAG` set in `.env`.

**If the server can reach MCI's Nexus**: push the images there from the build machine and set
`SQM_REGISTRY` to the repository path; `docker compose pull` replaces the build.

## 4. First start

```bash
cd /opt/quadrasense/repo/deploy/compose       # the checkout and its links (section 3)
cp .env.example /opt/quadrasense/config/.env && chmod 640 /opt/quadrasense/config/.env
# fill in: five passwords (openssl rand -hex 24), SQM_PUBLIC_ORIGIN. SQM_TAG is set by update.sh.
mkdir /opt/quadrasense/config/tls
cp /path/to/tls.crt /path/to/tls.key /opt/quadrasense/config/tls/
chmod 644 /opt/quadrasense/config/tls/*       # the containers' UID reads them through the bind mount;
                                              # /opt/quadrasense (2770) keeps everyone else out

./update.sh
docker compose logs migrate grants           # both must end with exit code 0
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
| B. Replay the originals | Load the dump (`--load-dump`, below), then upload every daily file through the Import Center, oldest first | measured on the laptop: a delivery's mart refresh alone 63 min; ~250 deliveries | proving the archive restores the system - once, later |
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

### Loading the dump (B and C)

The Import Center takes daily files only: the dump's name is a range, not a day, and it is refused.
The worker loads it, once, into an empty store - `DumpLoad`, added 2026-10-24 when this VM was the
first machine that had to start without it:

**First, a GSMA TAC version** - uploaded through the Import Center and activated, before the dump.
The dump's device models are written as it loads and leave out shifted IMEIs, which only the TAC
table can recognise: loaded without one, every shifted IMEI is counted as a handset, and the models
have to be written again (section 9, third session).

```bash
# the dump in /opt/quadrasense/incoming, compressed or not (a .gz is read as it is)
docker compose run --rm -v /opt/quadrasense/incoming:/import:ro worker \
  --load-dump /import/dump_subs_device_sim_info_20251227_20260125.csv.gz
```

It refuses a store that already holds bindings; pauses merges; streams every row into
`binding_snapshot` exactly as the first machine loaded it (no row filtered, columns by header name);
seeds current state from it (active, change sequence 0, no change date), whose IMSI and IMEI copies
fill through their views; and checks that all four tables hold exactly the file's row count -
anything else, or any failure, empties them again. Then it writes the dump's bindings into the history
and its device models, the two steps the existing backfills already had for it. Daily files follow
through the Import Center, from `2026-01-26` on.

It also puts the dump in the Import Center, as the first machine has it (since 2026-10-25): the
original goes into the file store - a `.gz` as it is - and the history gets a completed import with no
business date, by `operator (--load-dump)`, with its row count and when it ran. A store loaded before
that (this VM, 2026-10-24) is recorded afterwards, from the same file:

```bash
docker compose run --rm -v /opt/quadrasense/incoming:/import:ro worker \
  --record-dump /import/dump_subs_device_sim_info_20251227_20260125.csv.gz
```

It counts the file's rows first and refuses any file that does not hold exactly what
`binding_snapshot` holds; recording the same file again changes nothing.

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

**2026-10-24, second session.**

- **Disk.** Grown to 500 GB - all of it into `/` (440 GB, 413 GB free); `/var` is still 42 GB and the
  volume group has nothing left. Docker kept its volumes on `/var`, so the space was out of its reach.
  `deploy/host/move-docker-data-root.sh /srv/docker` moves Docker's data root - the volumes, i.e. the
  databases - onto `/`, keeping everything; it needs root, so the owner runs it. Images stay on `/var`:
  Docker 29 keeps them in containerd's store (`/var/lib/containerd`), a few GB, pruned by hand.
- **Deployment by git** (section 3) replaced carried images: the first `update.sh` pulled from GitHub,
  built all four images on the server and restarted the system in 4 min 21 s.
- **Access** from outside the office is the department's WireGuard VPN. A tunnel Windows shows as "Up"
  passed no traffic for an hour; test with a TCP connect, not the interface state.
- **The laptop's upload** over that VPN moved the dump at ~200 KB/s - about three hours for its
  2.07 GB compressed. Bulk data is better carried at the office, or from a source the server can reach.
- **Storage moved** (the owner ran `deploy/host/move-docker-data-root.sh /srv/docker`): Docker's data
  root, and with it the ClickHouse and PostgreSQL volumes, is now on `/` with 412 GB free; every
  service came back healthy; `daemon.json` kept its log settings.
- **Builds through Nexus**: the server's `.env` sets `SQM_NUGET_SOURCE` and `SQM_NPM_REGISTRY` to the
  department's Nexus; the next `update.sh` restored every NuGet package and every npm package through it
  (05 section 1). Base images still come from the internet - 05 section 8, request 1.

**2026-10-24, third session.**

- **The dump loaded** (`--load-dump`, section 5) from the 2.07 GB `.gz`, in 20 min 46 s end to end:
  125,939,523 rows into `binding_snapshot` (56 s), current state seeded from it (275 s), and all four
  tables reconciled to the file's row count; then the history (5.2 min, the dump's bindings 313 s,
  compacted and reconciled) and the dump's device models.
- **Those models came out wrong** - more than the laptop's, from the same file:

  | Dump's models | Models | Handsets | SIMs |
  |---|---|---|---|
  | Laptop | 94,330 | 88,491,721 | 113,118,115 |
  | VM | 95,571 | 88,561,455 | 113,188,719 |
  | Difference | 1,241 | 69,734 | 70,604 |
  | What the shifted-IMEI test removes from the dump, measured on the laptop | 1,241 | 69,734 | 70,604 |

  The VM has no GSMA TAC version yet (`sqm.tac` is empty), and a shifted IMEI is recognised only through
  it - a TAC GSMA does not know, which one restored leading digit turns into one it does
  (`Countable()`, `ClickHouseIngestionStore.RiskSnapshot.cs`). With no TAC table nothing is shifted, so
  the shifted IMEIs were counted. The last row is that test run against the laptop's copy of the dump;
  it matches the difference exactly. Section 5 now loads a TAC version first. **To repair the VM:**
  upload the laptop's active version (`DeviceDatabase_TAC4Oct2026.csv`, v2026.10.04, 271,830 TACs)
  through the Import Center, activate it, then
  `docker compose run --rm worker --refresh-tac-days --force` writes the dump's models again.
- **Moved to `/opt/quadrasense`** (section 3). `/home` is a 4.9 GB volume of its own and the checkout,
  the dump and the logs had it at 47%; `/`, with `/opt` on it, has 394 GB free. Copied, checked (`cmp`
  on the dump, `diff` on `.env` and `tls/`), redeployed from the new place - every bind mount now comes
  from `/opt/quadrasense/repo`, HTTPS from the laptop answers 200 in 0.24 s, the row counts are
  unchanged - and only then the old copy deleted: `/home` is at 5%.
- **`update.sh` ran the script from before its own pull.** The redeploy pulled the fix that writes
  `SQM_TAG` through the `.env` link (`sed --follow-symlinks`) and still replaced the link with a copy:
  bash goes on reading the file it opened, and `git pull` had put a new one in its place. `update.sh`
  now pulls and then `exec`s the script it pulled. Proven in a scratch repository on the VM, with
  `docker` stubbed: upstream adds a line to `update.sh`, the clone runs its own copy - the new script
  prints that line, the one from before the fix does not. The link was put back by hand; nothing
  restarted.
