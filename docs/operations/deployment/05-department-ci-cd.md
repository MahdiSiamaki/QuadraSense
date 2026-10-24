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
~/quadrasense/repo/deploy/compose/update.sh
```

It pulls the branch, builds the four application images on the server tagged with the commit,
records the tag in `.env` and starts them (`02-…` §3). Against section 1 it fails twice: its builds
reach the internet directly, and nothing tests a commit before it runs. It stays as the fallback.

## 7. QuadraSense on the department's pipeline

```
push to <gitlab> ─> build + push  (a Group build runner with the socket)  ─> <nexus-push>
               └──> test          (docker-global: no socket needed)
                                                                              │
                     deploy (shell runner on <server>, Protected) <───────────┘
                       pulls <nexus-pull>/<project>/<image>:<sha>; docker compose up -d
```

| Piece | QuadraSense |
|---|---|
| Repository | on `<gitlab>`. Developers push there; GitHub is mirrored or retired (decision 2) |
| **Build runner** | **must be a Group build runner with the Docker socket** - `docker-global` has none. Either QuadraSense's project lives in `<build-group>` and uses its `docker` runner, or the department gives QuadraSense's own group a build runner (decision 3) |
| Test runner | `docker-global` can run `dotnet test` and the frontend's tests: they need an image, not a daemon |
| Base images | the Dockerfiles' `SDK_IMAGE`, `RUNTIME_IMAGE`, `NODE_IMAGE`, `NGINX_IMAGE` arguments (`01-container-images.md`) pointed at `<nexus-pull>`; the .NET two need an MCR proxy (request 1) |
| Packages | `NPM_REGISTRY` → `<npm-group>`, its credential as a BuildKit secret (the web Dockerfile needs that mount - backlog); `NUGET_SOURCE` → a NuGet proxy (request 1) |
| Tests | `dotnet test backend/Sqm.slnx`, the frontend's `npm test` and `npm run build`. Without a database the integration tests skip - the job says how many, and a skip is not a pass (CLAUDE.md) |
| Push | `<nexus-push>/<project>/{api,worker,migrator,web}:<short-sha>`, by the build job only |
| Release file | the tag each environment runs, in `releases/<env>.env` of an infrastructure repository - **or Nexus's cleanup deletes the image after 30 days** |
| Deploy runner | a **shell** runner **on `<server>`**, Protected, tagged for QuadraSense; `gitlab-runner` in `docker` and `deploy`; the deployment directory `/opt/quadrasense/compose` (`.env`, `tls/`) instead of an operator's home |
| Deploy job | reads the release file, sets `SQM_REGISTRY=<nexus-pull>/<project>` and `SQM_TAG`, `docker compose pull && docker compose up -d`. Pull credential only - never the push one |
| Stores | `CLICKHOUSE_IMAGE`, `POSTGRES_IMAGE` in `.env` through `<nexus-pull>` |

The pipeline this implies - **a design, not run**. It is written as a file once the project, its
group, its runners and variables exist on `<gitlab>` (backlog):

```yaml
stages: [build, test, deploy]

variables:
  REGISTRY_PULL: <nexus-pull>
  REGISTRY_PUSH: <nexus-push>
  IMAGE_PREFIX: <project>

build:
  stage: build
  tags: [docker]                      # the Group build runner - not docker-global
  image: <nexus-pull>/library/docker:29-cli
  rules: [{ if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH' }]
  before_script:
    - echo "$NEXUS_PULL_PASS" | docker login "$REGISTRY_PULL" -u "$NEXUS_PULL_USER" --password-stdin
    - echo "$NEXUS_PUSH_PASS" | docker login "$REGISTRY_PUSH" -u "$NEXUS_PUSH_USER" --password-stdin
  script:
    - |
      for t in api worker migrator; do
        docker build -f deploy/images/backend/Dockerfile --target $t \
          --build-arg SDK_IMAGE=<via nexus> --build-arg RUNTIME_IMAGE=<via nexus> \
          --build-arg NUGET_SOURCE=<nuget proxy> \
          -t "$REGISTRY_PUSH/$IMAGE_PREFIX/$t:$CI_COMMIT_SHORT_SHA" .
      done
      docker build -f deploy/images/web/Dockerfile \
        --build-arg NODE_IMAGE=<via nexus> --build-arg NGINX_IMAGE=<via nexus> \
        --secret id=npmrc,src="$NPMRC_FILE" \
        -t "$REGISTRY_PUSH/$IMAGE_PREFIX/web:$CI_COMMIT_SHORT_SHA" .
      for t in api worker migrator web; do
        docker push "$REGISTRY_PUSH/$IMAGE_PREFIX/$t:$CI_COMMIT_SHORT_SHA"
      done

test:
  stage: test
  tags: [docker-global]
  image: <nexus-pull>/<mcr proxy path>/dotnet/sdk:10.0.400     # global.json's SDK, exactly
  script:
    - dotnet test backend/Sqm.slnx

deploy:
  stage: deploy
  tags: [<quadrasense deploy tag>]    # shell runner on <server>, Protected
  rules: [{ if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH' }]
  before_script:
    - echo "$NEXUS_PULL_PASS" | docker login "$REGISTRY_PULL" -u "$NEXUS_PULL_USER" --password-stdin
  script:
    - cd /opt/quadrasense/compose
    - export SQM_REGISTRY="$REGISTRY_PULL/$IMAGE_PREFIX" SQM_TAG="$CI_COMMIT_SHORT_SHA"
    - docker compose pull && docker compose up -d
```

How the deployed tag gets into `releases/<env>.env` (a commit to the infrastructure repository, by the
pipeline or by a person) follows whatever the department's other projects do - question 4 below.

## 8. Requests and decisions

| # | Item | Recommendation |
|---|---|---|
| 1 | **Request to the department: a proxy of Microsoft's registry (MCR)** in Nexus, in the docker group. NuGet is there already (found by reading Nexus's repository list); MCR is not, and every .NET build needs the SDK and runtime images | Ask first: no .NET build can comply without it. Stopgap: push the two images once into the hosted repository from a workstation, by whoever holds the push account |
| 2 | **Where the code lives**: `<gitlab>` as primary, GitHub mirrored or retired | `<gitlab>`: the department runs it, it is private by default - which also ends the public-repository problem - and it rehearses MCI's GitLab (stage 2) |
| 3 | **Which build runner**: QuadraSense's project inside `<build-group>` (uses its `docker` runner), or a group of its own with its own Group build runner | The department's call: the first is immediate; the second keeps QuadraSense's jobs and variables apart from the other group's |
| 4 | **Release files**: QuadraSense's `releases/<env>.env` in the group's `<infra-repo>`, or an infrastructure repository of its own; who commits the tag | follow the department's practice; it decides what Nexus keeps |
| 5 | **A deploy runner on `<server>`**: shell, Protected, registered with a `glrt-…` token by someone with access - never sent in a chat | yes; it needs `sudo` on `<server>` (install, groups `docker` and `deploy`, `/opt/quadrasense`) - the owner or the department |
| 6 | **Which branch deploys** | the default branch, protected |

## 9. Order of work

| # | Step | Kind | Waits on |
|---|---|---|---|
| 1 | Revoke the GitLab token and change both Nexus passwords (they went through a chat) | R | the owner |
| 2 | Request 1 to the department (MCR and NuGet proxies); decisions 2-4 | R | the owner, the department |
| 3 | On `<server>`: `registry-mirrors` and `docker login <nexus-pull>` (owner); `.env`'s store images through Nexus | T | 1 |
| 4 | The repository and its group on `<gitlab>`; protect the default branch; CI/CD variables (section 2) | T | 2 |
| 5 | Deploy runner on `<server>`; `/opt/quadrasense/compose` with `.env` and `tls/` moved there | T | 4; `sudo` |
| 6 | The web Dockerfile takes the npm credential as a BuildKit secret; `.gitlab-ci.yml` from section 7 | P | 2, 4 |
| 7 | First pipeline end to end; the tag in a release file; `update.sh` becomes the fallback | T | 5, 6 |
