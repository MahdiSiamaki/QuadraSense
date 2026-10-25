# CI/CD on the department's infrastructure

**Status:** written 2026-10-24 from the department's own guides - "Nexus Repository" and "GitLab
Runner", usage guides from its Confluence, provided by the product owner - and from measurements on
the QuadraSense server the same day. The git routine of section 6 runs today, and breaks one of the
department's rules (section 1). The pipeline of section 7 is a design: nothing has run on the
department's GitLab yet.

**Read with:** `02-department-vm.md` (the server), `03-mci-openshift.md` §3 (MCI's own pipeline, which
this one rehearses: GitLab, runners, a hosted registry), ADR-007.

**Placeholders.** The guides are the department's internal documents and this repository is public.
Hostnames, addresses, server, group and repository names are placeholders here; the real values are
in `.private/department-infrastructure.md` on the owner's machine (git-ignored) and with the
department. No password or token is written in either place (section 5).

| Placeholder | What it is |
|---|---|
| `<server>` | the department VM that runs QuadraSense (stage 1) |
| `<gitlab>` | the department's GitLab instance |
| `<nexus>` | the department's Nexus (OSS): console, Maven, npm, apt |
| `<nexus-pull>` | the Docker **group** endpoint (proxy + hosted), for pulling |
| `<nexus-push>` | the Docker **hosted** endpoint, for pushing |
| `<maven-group>`, `<npm-group>` | the Maven and npm group repositories |
| `<build-host>` | the machine that runs the build runners - and Nexus |
| `<build-group>` | the GitLab group whose build runner holds the Docker socket |
| `<infra-repo>` | that group's infrastructure repository, holding `releases/*.env` |
| `<project>` | a project's namespace for its images |
| `<pull-user>`, `<push-user>` | Nexus's two service accounts |

---

## 1. The rule: Nexus is the only source

**Nexus is the only permitted source of dependencies in this environment. No build may fetch
anything directly from Docker Hub, Maven Central, npmjs or Ubuntu's archives.**

Why:

- direct access to public repositories from this network is unreliable, and some of them geo-block;
- Nexus caches what it fetches, so builds are faster and do not depend on the internet being up;
- one place for every kind of artifact - Docker, Maven, npm, apt;
- it holds the projects' own images, so staging and production deploy from one source.

What that means for QuadraSense - **its builds break this rule today**:

| QuadraSense needs | Through Nexus? |
|---|---|
| Docker Hub images: `node`, `nginx-unprivileged`, `clickhouse-server`, `postgres` | yes - `<nexus-pull>` proxies Docker Hub |
| Microsoft's .NET images (`mcr.microsoft.com/dotnet/sdk`, `…/aspnet`) | **no** - Nexus's only Docker proxy is Docker Hub's: request 1 (section 8) |
| npm packages | yes - `<npm-group>`, readable without a login |
| NuGet packages | **yes**, though the guide does not list it: a `nuget-group` (nuget.org proxy + hosted), readable without a login |
| apt (the server's own updates) | yes - proxies of Ubuntu's archive and of its security archive |

`update.sh` (section 6) builds on the server. Since 2026-10-24 its NuGet and npm come from Nexus
(`SQM_NUGET_SOURCE`, `SQM_NPM_REGISTRY` in the server's `.env`) - checked: Nexus's NuGet proxy went
from 0 packages to 55 in that build, and our npm packages appeared in its npm proxy. Base images still
come from Docker Hub and MCR directly: Docker Hub's through Nexus needs a `docker login` on the server
(the owner's), MCR's needs request 1. Until both, the rule is still broken there.

## 2. Nexus - the endpoints

| Use | Endpoint | Kind |
|---|---|---|
| Pull Docker images | `<nexus-pull>` | group: proxy + hosted |
| Push Docker images | `<nexus-push>` | hosted |
| Maven | `<maven-group>` | group |
| npm | `<npm-group>` | group |
| apt | from the infrastructure team | proxy |
| Web console | on `<build-host>` | - |

**Why two Docker addresses:** Nexus OSS cannot write to a docker group - a product limit, not a
choice. So pushes go to `<nexus-push>`, pulls to `<nexus-pull>`, which serves the pushed images too.
**Never pull from the push address.**

### On a machine

```bash
# /etc/docker/daemon.json - every Docker Hub pull goes through Nexus
{ "registry-mirrors": ["https://<nexus-pull>"] }

sudo systemctl restart docker
docker login <nexus-pull>                      # <pull-user>
```

Pushing needs its own login:

```bash
docker login <nexus-push>                      # <push-user> - build jobs only (section 5)
docker tag  myimage:1.0 <nexus-push>/<project>/myimage:1.0
docker push <nexus-push>/<project>/myimage:1.0
```

### Maven and npm

```xml
<!-- ~/.m2/settings.xml -->
<settings>
  <mirrors><mirror><id>nexus</id><url><maven-group></url><mirrorOf>*</mirrorOf></mirror></mirrors>
  <servers><server><id>nexus</id><username>…</username><password>…</password></server></servers>
</settings>
```

`<mirrorOf>*</mirrorOf>` routes every repository a `pom.xml` names through Nexus as well.

```ini
# the project's .npmrc or ~/.npmrc
registry=<npm-group>
always-auth=true
```

### In a pipeline

Credentials are CI/CD variables, at group or project level - **never in a project's files**:

| Variable | For |
|---|---|
| `NEXUS_PULL_USER` / `NEXUS_PULL_PASS` | pulling images and dependencies |
| `NEXUS_PUSH_USER` / `NEXUS_PUSH_PASS` | pushing built images |

```yaml
before_script:
  - echo "$NEXUS_PULL_PASS" | docker login "$REGISTRY_PULL" -u "$NEXUS_PULL_USER" --password-stdin
  - echo "$NEXUS_PUSH_PASS" | docker login "$REGISTRY_PUSH" -u "$NEXUS_PUSH_USER" --password-stdin
```

**Always `--password-stdin`.** `docker login -p "$PASS"` prints the password in the job log, and Masked
variables are not always masked.

Maven's and npm's credentials inside a Dockerfile are given as a **BuildKit secret, never `COPY`**:

```bash
docker build --secret id=m2settings,src=/tmp/settings.xml .
```

A `COPY`ed credential stays in the image's layers, extractable by anyone who can pull the image.

### Image names, and what survives cleanup

```
<nexus-pull>/<project>/<component>:<git-short-sha>
```

The tag is **always the commit's short SHA** - never `latest`, never a version number - so every
image traces to exactly one commit. Version numbers belong in a release file:
`releases/production.env` in the group's `<infra-repo>`.

**Cleanup policy: images that no `releases/*.env` names, and that are more than 30 days old, are
deleted.** For QuadraSense: every tag a server runs must be in a release file, or a month later the
server cannot pull it again - after a host rebuild, say.

### When Nexus goes wrong

| Symptom | Cause | What to do |
|---|---|---|
| `username is empty` at login | the variable did not reach the job: it is **Protected** and the branch is not in Protected Branches - GitLab then sends an empty value, without an error | add `echo "len=${#NEXUS_PUSH_USER}"` to the job for a moment (prints a length, not the value) |
| A pull is very slow or times out | Nexus fetches an artifact from the internet the first time; if its own outbound access is blocked the request hangs until the timeout | the repository's status in the console: `Remote Unavailable` means **Nexus's** outbound access, not the client |
| Login succeeds, pull fails | usually the nginx routing in front of Nexus | nginx's logs - traffic may land in the general `access.log`, not the domain's. A missing port-80 block once made login work and pulls fail silently |
| A job gets an old image | the Docker runners' pull policy is `if-not-present` | cannot happen with SHA tags - only with `latest` |

**The department's Nexus rules:** no build connects to the internet directly; `latest` is used in no
environment; the push credential belongs to build jobs only, never to deploy jobs; images in no
`releases/*.env` and older than 30 days are removed.

## 3. GitLab runners

Every pipeline job runs on the department's own runners. Two kinds: **build** runners, which make
images, and **deploy** runners, which start services on the staging and production servers. A job
chooses its runner by `tags`.

| Runner | Executor | Scope | Tags | For |
|---|---|---|---|---|
| on `<build-host>` | Docker | Group (`<build-group>`) | `docker`, the group's name | **building images and pushing them to Nexus** |
| on `<build-host>` (instance runner) | Docker | **Instance** | `docker-global` | general jobs of the **other** projects on `<gitlab>` |
| on the staging host | shell | Group | `deploy-stage`, the host's name | deploying to staging |
| on the production host | shell | Group | `deploy-prod`, the host's name | deploying to production |

**Scope decides who can use a runner.** A Group runner serves only its group's projects. The Instance
runner serves every project on `<gitlab>` - including teams with nothing to do with this one.

### Group and Instance: the security difference

Anyone who can create a project can run jobs on `docker-global`. It runs on `<build-host>` - **the
machine Nexus runs on.** Root on that machine is access to the whole chain of images that go to
production.

### The two executors - the difference that matters most

**Docker executor.** Every job in a fresh container; nothing survives from the job before; the job's
`image:` decides what it runs in. **The build runner mounts the host's Docker socket**, so
`docker build` in a job runs on the machine's own daemon - and the BuildKit cache persists between
jobs, which makes builds fast.

> **The socket is root.** Access to the Docker socket is root on that machine. So the socket is given
> **only to the Group build runner, and must never be added to the Instance runner (`docker-global`).**
> The two runners share a machine and deliberately do not share a level of access.

So **`docker-global` cannot build images**: it has no daemon to build with.

**Shell executor.** The job runs on the server itself as the `gitlab-runner` user, with no isolation;
`image:` is ignored. Hence **deploys only**: the job needs the project's files (the guide's example:
`/opt/<project>`) and the server's Docker daemon, so `gitlab-runner` is a member of the `docker` and
`deploy` groups. Nothing is isolated: whatever a job writes to disk stays - clean up your own
temporary files.

### Choosing a runner

```yaml
build-image:
  tags: [docker]          # the build runner on <build-host>
deploy:staging:
  tags: [deploy-stage]    # the staging host
deploy:production:
  tags: [deploy-prod]     # the production host
```

> **A typo in a tag:** a job whose tags match no runner stays `pending` for ever, **with no error** -
> GitLab never says "no runner found". Compare the spelling against the table before committing.

### Protected runners and variables

The production runner is **Protected**: only jobs on protected branches or tags can use it. Without
that, anyone who could create a branch could deploy to production. Variables follow the same logic,
and that is a common error:

| Variable flag | On a protected branch | On any other branch |
|---|---|---|
| Protected | injected | **empty - and no error** |
| Masked | injected | injected |

A job failing with `username is empty`, or with an authentication error: first check whether the
variable is Protected and the branch is not.

### Settings shared by a machine

**`concurrent`** (in `/etc/gitlab-runner/config.toml`) is **global, not per runner.** Two runners on one
machine with `concurrent = 1` run one job at a time between them; the rest queue. When raising it,
count the machine's RAM: each Docker job can take up to its `memory` limit.

**Resource limits:**

```toml
[runners.docker]
  memory = "3g"
  memory_swap = "6g"
  cpus = "2"
```

A backend build that needs more memory is killed (OOM) - and the log then looks like a **compile error,
not a memory error**.

### Adding a runner to an existing machine

A machine can have several runners: each `register` adds a `[runners]` block to the same
`config.toml`, and one process serves them all.

1. In GitLab, create the token: **Settings → CI/CD → Runners → New project/group runner**. Tags,
   *Run untagged jobs* and *Protected* are set here.
2. On the machine:

```bash
sudo gitlab-runner register --non-interactive \
  --url https://<gitlab>/ \
  --token glrt-XXXXXXXX \
  --name "<descriptive name>" \
  --executor docker \
  --docker-image <nexus-pull>/library/docker:29-cli
```

3. Raise `concurrent` if needed.
4. Check:

```bash
sudo gitlab-runner list
sudo gitlab-runner verify
```

No restart is needed; the config file is re-read by itself.

> **The runner token is the runner's identity.** Anyone holding a `glrt-…` token can register a fake
> runner with the same tags and have our jobs - production jobs included - run on their own machine.
> It is never written in a chat, a ticket, an e-mail or Confluence. If it leaks anywhere: reset it in
> GitLab and register the runner again.

### When runners go wrong

| Symptom | Check, in this order |
|---|---|
| Job `pending` for ever | the tag's spelling; the runner online on the Runners page; a job on an unprotected branch that landed on a Protected runner |
| Runner shown offline | `sudo systemctl status gitlab-runner`, `sudo gitlab-runner verify`, `sudo journalctl -u gitlab-runner -n 50` |
| Deploy job fails on file permissions | `gitlab-runner` must be in `docker` and `deploy`: `id gitlab-runner`; then `sudo -u gitlab-runner bash -c 'touch /opt/<project>/compose/.probe && rm /opt/<project>/compose/.probe && docker ps >/dev/null && echo OK'` |
| A group change has no effect | the runner process keeps its group memberships from start-up: after `usermod`, `sudo systemctl restart gitlab-runner` |

**The department's runner rules:** deploy runners have only the shell executor, and each sits only on
its own target server; the production runner is always Protected; the Docker socket is given only to
the Group build runner, never to any Instance runner; a runner token is never written anywhere but
`config.toml`.

## 4. What was measured (2026-10-24)

| | Found |
|---|---|
| `<gitlab>`, `<nexus>`, `<nexus-pull>`, `<nexus-push>` | all reachable from `<server>` and (over the VPN) the laptop; all four resolve to one front address - the nginx the guide describes |
| Docker endpoints | `401` to an anonymous `/v2/` - they want a login, as they should |
| `<server>` | no `gitlab-runner` installed |

## 5. Credentials - where each one lives

| Credential | Lives in | Never in |
|---|---|---|
| `<pull-user>` | on a server: Docker's login store, by `docker login <nexus-pull>` **run by the owner** (`~/.docker/config.json`, mode 600 - base64, not encryption). In CI: a Protected, Masked variable | git, chat, scripts |
| `<push-user>` | CI variables only, Protected and Masked, used by **build jobs only** | servers, deploy jobs, git, chat |
| Runner token `glrt-…` | the runner's `config.toml`, written by `gitlab-runner register` | anywhere else - chat, tickets, e-mail, Confluence |
| Access to the repository on `<gitlab>` | a person: their own SSH key, or a personal token in their own git credential store. A server: a read-only deploy key, or the deploy runner's job token | another person's token; a server holding a person's token |

**Went through a chat on 2026-10-24, so to be replaced:** both Nexus passwords, and a GitLab personal
access token. Revoke the token; create a new one with only `read_repository` and `write_repository`;
change both Nexus passwords.

## 6. Today: git, built on the server

The product owner's routine: changes are pushed to GitHub; the server pulls and builds.

```bash
/opt/quadrasense/repo/deploy/compose/update.sh
```

It pulls the branch, builds the four application images on the server tagged with the commit,
records the tag in `.env` and starts them (`02-…` §3). Against section 1 it fails twice: its builds
reach the internet directly, and nothing tests a commit before it runs. It stays as the fallback.

## 7. QuadraSense on the department's pipeline

**Decided by the product owner (2026-10-24):** CI on the department's **instance runner**
(`docker-global`); CD on **a new Group runner of QuadraSense's own, shell executor, with its own tag**,
for deploying only. The repository is `quadrasense/quadrasense` on `<gitlab>`.

```
push to the default branch of <gitlab>
   test     dotnet test, npm test + build          docker-global
   build    4 images with kaniko -> <nexus-push>   docker-global
   deploy   pull those tags, docker compose up     group runner `quadrasense-deploy`, shell, on <server>
```

| Piece | How | Files |
|---|---|---|
| **Build without a socket** | `docker-global` has no Docker socket (section 3), so `docker build` cannot run there. kaniko builds the same Dockerfiles in user space inside the job's container and pushes the image itself. Base images from Docker Hub come through `<nexus-pull>` (`--registry-mirror`) | `deploy/ci/kaniko-build.sh`; image `martizih/kaniko:v1.28.6-debug` (Docker Hub, so through Nexus) |
| Tests | `dotnet restore` from Nexus's NuGet group, `dotnet test`; the frontend's `npm ci` from Nexus's npm group, `npm test`, `npm run build` | `.gitlab-ci.yml` |
| Images | `<nexus-push>/quadrasense/{api,worker,migrator,web}:<short sha>` - default branch only | |
| **Deploy runner** | shell executor on `<server>`, Group runner of the QuadraSense group, tag `quadrasense-deploy`, Protected; `gitlab-runner` in `docker` and `deploy` | `deploy/host/install-deploy-runner.sh` |
| Deploy | each commit gets `/opt/quadrasense/releases/<sha>` (its compose files and grants), `.env` and `tls/` linked from `/opt/quadrasense/config`; pull the four images by tag, `docker compose up -d --no-build`; `current` points at the running one; five releases kept | `deploy/compose/deploy-release.sh` |
| Rollback | re-run the deploy job of an older pipeline (the images are in Nexus) | |
| Probe | a manual job that reports what `docker-global` really allows: user, capabilities, socket, which of Nexus / MCR / Docker Hub it reaches | `probe:runner` in `.gitlab-ci.yml` |

**Checked before committing** (2026-10-24): the pipeline file parses, and each job lands on the runner
intended; `deploy-release.sh` run on `<server>` against a scratch directory with a stand-in `docker`
asked only for `compose pull` of the four application images and `compose up -d --no-build`, kept the
five newest of six releases, linked `.env` and `tls/` and copied the grants; the runner install's
checksum step, run against the real release files, refused a truncated download. **Not yet run: any
of it on `<gitlab>`** - that waits on the steps below.

### CI/CD variables - on the QuadraSense group (Settings → CI/CD → Variables)

| Variable | Value | Flags |
|---|---|---|
| `REGISTRY_PULL` | `<nexus-pull>` | - |
| `REGISTRY_PUSH` | `<nexus-push>` | - |
| `NUGET_SOURCE` | `<nexus>/repository/nuget-group/index.json` | - |
| `NPM_REGISTRY` | `<npm-group>` | - |
| `NODE_IMAGE` | `<nexus-pull>/library/node:24.19.0-alpine` | - |
| `NGINX_IMAGE` | `<nexus-pull>/nginxinc/nginx-unprivileged:1.28-alpine` | - |
| `NEXUS_PULL_USER`, `NEXUS_PULL_PASS` | `<pull-user>` and its password | Protected, Masked |
| `NEXUS_PUSH_USER`, `NEXUS_PUSH_PASS` | `<push-user>` and its password | Protected, Masked |
| `DOCKER_AUTH_CONFIG` | only if `docker-global` cannot pull job images from `<nexus-pull>` by itself - Docker's `config.json` with the pull account | Protected |
| `DOTNET_SDK_IMAGE`, `SDK_IMAGE`, `RUNTIME_IMAGE` | through Nexus once it proxies Microsoft's registry (request 1); until then unset - the defaults in the files | - |

Protected variables reach only protected branches: the default branch is protected by GitLab already;
a test pipeline on another branch gets empty credentials - `username is empty` (section 2).

### What the owner does (credentials, root)

1. **Push the code to `<gitlab>`** - the first push, with the owner's own credentials (a *new* token:
   the one sent in a chat is to be revoked). Git's credential manager on the laptop stores it; after
   that, pushes need no one. From the laptop over the VPN only HTTPS reaches `<gitlab>`.
2. **The variables above**, in the group's settings.
3. **The deploy runner**: create it in the group (Settings → CI/CD → Runners → New group runner: tag
   `quadrasense-deploy`, not untagged, Protected), then on `<server>`:
   `sudo GITLAB_URL=https://<gitlab>/ bash deploy/host/install-deploy-runner.sh` (the configuration is
   in `/opt/quadrasense/config` already, so no `CONFIG_FROM`)
   - it asks for the token at a hidden prompt. It downloads the runner (142 MB; ~17 minutes at the
   server's 135 KB/s to that host, measured).
4. **Run `probe:runner` once** from the first pipeline, and send its output: it decides whether
   `docker-global` can pull job images, reach Nexus, and whether the .NET images can come from MCR
   directly until the proxy exists.

## 8. Requests and decisions

| # | Item | State |
|---|---|---|
| 1 | **Request to the department: a proxy of Microsoft's registry (MCR)** in Nexus, in the docker group. NuGet is there already; MCR is not, and every .NET build and test needs the SDK and runtime images | **open** - until then the .NET images come from MCR directly, if `docker-global` reaches it (the probe says), which the "Nexus only" rule does not allow |
| 2 | Where the code lives | **decided**: `quadrasense/quadrasense` on `<gitlab>`. GitHub (public) remains the mirror for now; making it private, or retiring it, is still recommended |
| 3 | Which runner builds | **decided**: the instance runner, `docker-global`, with kaniko (it has no socket) |
| 4 | **Release files** - where the deployed tag of each environment is recorded, and so what Nexus's cleanup keeps (section 2: images in no `releases/*.env`, older than 30 days, are deleted) | **open**: ask the department how its cleanup reads them; until then the server records the running tag in `/opt/quadrasense/current.sha` |
| 5 | The deploy runner | **decided**: a Group runner of QuadraSense's group, shell, tag `quadrasense-deploy`, Protected, on `<server>` |
| 6 | Which branch deploys | the default branch, protected |
| 7 | **How GitHub's `main` reaches `<gitlab>`** after the owner merges a pull request. The owner's routine (2026-10-25): the laptop is current, Claude pushes and opens the pull request, the owner merges, `<gitlab>` follows, the runners build and deploy | **open**. The owner chose a timer on `<server>` pushing to `<gitlab>` over SSH with a write deploy key - and it is not built: the red lines say git over HTTPS, not SSH, and *servers pull* (`04-…`, §2-2 row 2), and section 5 gives a server read-only access only. Compliant ways: **(a)** `<gitlab>`'s own pull mirror from the public GitHub URL (Settings → Repository → Mirroring repositories; a paid GitLab feature, and `<gitlab>` must reach github.com); **(b)** a scheduled pipeline that fetches GitHub and pushes with the job token (the instance runner must reach github.com; the probe says); **(c)** the developer pushes from the laptop, over HTTPS with their own credential - what happens until (a) or (b) exists |
| 8 | **`<gitlab>`'s `main` held only GitLab's template README** (`Initial commit`, by the project's creator, 2026-10-24), with no history in common with the code | The owner approved replacing it once with GitHub's `main` (2026-10-25). Refused: `main` is protected against force pushes. The owner allows force pushes for Maintainers on `main` once, Claude pushes, the owner switches it back. Nothing else on `<gitlab>` is lost by it - checked 2026-10-25: of its commits, only that README and six on two `wip/` branches are missing from GitHub, and the six are already on GitHub's `main` as the same changes (`git cherry`) and on the laptop's branches |

## 9. Order of work

| # | Step | Kind | Waits on |
|---|---|---|---|
| 1 | Revoke the GitLab token and change both Nexus passwords (they went through a chat) | R | the owner |
| 2 | First push to `<gitlab>` (section 7, step 1) - replaces the template README on `main` (section 8, item 8) | R | 1; force pushes allowed on `main` once |
| 3 | Group CI/CD variables (section 7) | R | 1 |
| 4 | Deploy runner on `<server>` (section 7, step 3) | R, T | `sudo`, a runner token |
| 5 | First pipeline; run `probe:runner`; fix what it shows | T | 2-4 |
| 6 | Request 1 (MCR) and decision 4 (release files) with the department | R | - |
| 7 | When the pipeline deploys, `update.sh` becomes the fallback only | T | 5 |
