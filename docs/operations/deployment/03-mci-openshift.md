# Stage 2 - QuadraSense on MCI's OpenShift

**Status:** plan, written 2026-10-22. Nothing here has run on MCI's platform; every statement about
the platform comes from MCI's *Comprehensive guide to interacting with the DevOps infrastructure
(Kubernetes / OpenShift)*, version 1.5, which the product owner holds. Cite it by section; this
repository is public, and the guide is MCI's internal document, so its addresses, hostnames,
contacts and ticket paths are deliberately not copied here.

**Read with:** `04-security-red-lines.md` (the security office's requirements, which apply here in
full), `01-container-images.md` (the images this stage deploys), ADR-007.

---

## 1. How MCI's platform works, as far as it shapes QuadraSense

| Area | What the guide says | What it means for us |
|---|---|---|
| Access | Everything (console, API, Git, registry, GitOps) is reached only from the team's own terminal server, through PAM, with an account in MCI's service domain. OpenShift CLI tokens expire after 24 hours | Every person who deploys needs a domain account and PAM access - a ticket each. Nothing is deployed from a laptop |
| Environments | dev, uat and prd are separate clusters; production can run in two independent sites on request | Three namespaces, requested separately. The second site is ours to operate (below) |
| Resources | Requested by an SDM ticket; delivered as a namespace and a delivery e-mail that is *the* reference for every address and credential | Keep that e-mail; it is the source for the values `deploy/openshift/` will need |
| Responsibility | The platform runs the cluster. **High availability inside the namespace, every StatefulSet (replication, consistency), backups and the second site are the project's** | Section 5 below is our work, not a service we request |
| Security | Non-root is mandatory: a container that runs as root (UID 0) fails to deploy, on every cluster. UAT is where the security office approves the application and its images | Done - `01-container-images.md`, "Verified" |
| Source and CI | A group in MCI's GitLab per project; a dedicated, tagged GitLab Runner per group; images pushed to the project's hosted Nexus repository. Two routes: the whole pipeline in MCI's GitLab (recommended), or a pipeline that pulls finished images from an outside registry and pushes them inside | Section 3 |
| Registry | A Nexus per site. The hosted repository is for the project's own images only - never a cache of public ones. Clusters and runners pull public base images directly; a new external address is a firewall request | The Dockerfiles' base images (MCR, Docker Hub) may each need that request |
| Deployment | ArgoCD (GitOps) is available: it syncs a Git path of manifests to the namespace | Section 2's manifests live in Git and ArgoCD applies them |
| Storage | Central Ceph, three classes: **block** (RWO; the default; for databases), **file** (CephFS, RWX; for volumes several pods share), **object** (S3 buckets; for archives, files, backups). Very large or archival volumes can come from the storage team outside the cluster | Section 4 |
| Ingress | No NodePort. A Service of type LoadBalancer with an IP the platform assigns by ticket | The web front door is one such Service. No OpenShift Route is used for it |
| Egress | A label on the pods, an IP mapped to it by ticket, then firewall requests from that IP. One label and one IP per service | Only when something calls out |
| Monitoring | No Prometheus per project. The platform collects every namespace's metrics; a per-project token reads them through Thanos; Grafana is available as an operator for the project's own dashboards. Alerting is the project's | Section 6 |
| Logging | Forwarding to central Elasticsearch/ELK or Kafka by ticket, or an ECK stack in the namespace through its operator | Section 6 |
| Operators and Helm | **No cluster-scope permissions for projects.** Operators are installed by DevOps after review (Redis, Elastic, Kafka/Strimzi, Grafana are offered); **PostgreSQL and MongoDB operators are not installed - the project runs those databases itself.** A Helm chart that needs ClusterRoles must be stripped to namespace Roles and applied through ArgoCD | Plain manifests, namespace-scoped. ClickHouse and PostgreSQL as StatefulSets we maintain |

## 2. QuadraSense as OpenShift objects

The compose file (`deploy/compose/compose.yaml`) is the specification; each service maps to an
object, and everything it sets has a Kubernetes equivalent.

| Compose | OpenShift | Replicas | Notes |
|---|---|---|---|
| `web` | Deployment + Service `web` (type LoadBalancer, the assigned ingress IP, ports 443→8443 and 80→8080) | 2 | TLS terminates here (`server-https.conf` from a ConfigMap, the certificate from a Secret). PodDisruptionBudget minAvailable 1 |
| `api` | Deployment + Service `api` (ClusterIP 8080) | 2 | Stateless: sessions are in PostgreSQL. The login rate limiter is per process, so two replicas allow twice the attempts per address - still far below what an attack needs (ADR-006). Readiness `/health/ready`, liveness `/health/live` |
| `worker` | Deployment, `strategy: Recreate` | 1 | Jobs are claimed under leases (`ImportWorker:LeaseDuration`), but `MaxConcurrentJobs` 1 is a measurement - imports share ClickHouse's disk (ADR-007). **No probe exists for it yet**: a hung worker is not restarted |
| `migrate` | Job, an ArgoCD PreSync hook | - | Runs before every sync; a no-op when up to date. Forward-only, checksummed |
| `grants` | Job, PreSync, after `migrate` (sync wave) | - | The grants files from a ConfigMap; the `postgres` image provides `psql` |
| `clickhouse` | StatefulSet + headless Service, PVC on **block** storage | 1 (section 5) | The four XML files from a ConfigMap; passwords from a Secret as environment. The memory limit is the one number to choose: `memory.xml` takes 80% of it |
| `postgres` | StatefulSet + headless Service, PVC on block | 1 (section 5) | `PGDATA` a subdirectory of the volume, as in compose |
| volume `uploads` | PVC on **file** storage (RWX), mounted by API and worker | - | Or object storage - section 4 |
| networks `edge` / `data` | NetworkPolicies: deny all ingress by default, then one rule per arrow of the traffic flow below | - | §1-4 of the red lines |
| `mem_limit`, `cpus` | `resources.limits` and `requests`; plus `ephemeral-storage` | - | A ResourceQuota on the namespace |
| `read_only`, `cap_drop`, `user` | Nothing to write: restricted-v2 assigns the UID, and `securityContext` sets `readOnlyRootFilesystem: true`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `runAsNonRoot: true`, `seccompProfile: RuntimeDefault` | - | `/tmp` becomes an `emptyDir` (medium Memory, sized) |

**Not written yet:** the manifests themselves (`deploy/openshift/`, Kustomize: a base and an overlay
per environment). They need values only the delivery e-mail will give - registry path, storage class
names, the ingress IP - and an untested manifest is a guess. Write them when the dev namespace
exists, and test them there.

### Traffic flow

The table the security office asks for (§2-3 of the red lines), and the source of the
NetworkPolicies. Nothing else is allowed.

| From | To | Port | Why |
|---|---|---|---|
| Users (internal network) | `web` | 443 (80 only redirects) | The application |
| `web` | `api` | 8080 | `/api/` |
| `api` | `clickhouse` | 8123 (HTTP) | Analytics reads; mart refresh |
| `api` | `postgres` | 5432 | Sessions, users, audit, jobs, dashboards |
| `worker` | `clickhouse` | 8123 | Imports, marts, risk snapshot |
| `worker` | `postgres` | 5432 | Jobs, TAC versions |
| `migrate` | `clickhouse`, `postgres` | 8123, 5432 | Schema |
| `grants` | `postgres` | 5432 | Roles and grants |
| Platform monitoring | every pod | metrics port (once one exists) | Section 6 |
| *(future)* `api` | internal mail server | SMTP | Alerts (research item X1) - needs an egress IP |

ClickHouse's native (9000) and inter-server (9009) ports are used by nothing in QuadraSense; the
NetworkPolicies open only 8123. (Inside compose's `data` network they are reachable - compose
isolates by network, not by port.)

## 3. CI/CD

| Step | Where | What |
|---|---|---|
| Source | MCI GitLab, the project's group | **Today the source is on GitHub, and public.** Decide whether MCI GitLab becomes the source of truth or a mirror (README, "Decisions needed") |
| Build | The project's runner | The two Dockerfiles, with `NUGET_SOURCE`, `NPM_REGISTRY` and the base-image arguments pointing at MCI's Nexus proxies |
| Tests | The runner | `dotnet test backend/Sqm.slnx` (190 pass, 34 skip without a database - CLAUDE.md), `npm test`, `npm run build`. A failed test fails the pipeline |
| Scan | The runner | An image scanner (Trivy or what the security office uses) - red lines §1-1 row 5 |
| Push | The hosted Nexus repository | `<registry>:<port>/<project>/<image>:<git sha>` - never `latest` |
| Deploy | ArgoCD | The overlay's image tags are bumped by commit; ArgoCD syncs dev automatically, uat and prd by hand |

## 4. Storage

Measured on the laptop, 2026-10-22: ClickHouse **182.5 GiB** (event log 53.6, three history tables
71.7, current-state tables 51.3), growing ~20 GiB a month at the September/October feed; uploaded
originals 40.6 GiB, ~1.1 GB per daily file; PostgreSQL 332 MB.

| Data | Class | Size to request (first year) | Why |
|---|---|---|---|
| ClickHouse | block (RWO) | **600 GiB**, expandable | 183 now + ~240 a year + headroom for merges (a merge writes the new part before dropping the old) |
| PostgreSQL | block | 20 GiB | Small; WAL and growth |
| Uploaded originals | file (RWX) - or object | **500 GiB**, growing ~400 GiB a year | Kept for ever (CLAUDE.md, retention): they are the backup every table can be rebuilt from |
| Backups | object (S3) | sized to the backup design | Section 5 |

**Originals in object storage** is the better fit for data that is written once and kept for
ever, and ClickHouse can read S3 directly. It is a code change: `IImportFileStore` has one
implementation, a directory (`DirectoryImportFileStore`). RWX file storage needs no code. Start with
file storage; move when the volume grows past what the storage team wants on CephFS.

## 5. High availability, backup, second site

The guide makes all three the project's; the red lines (§2-3) ask for clustered servers and every
service active-active across two sites. Today each store is a single node.

| Component | Today | To be highly available | Size of the change |
|---|---|---|---|
| web, api | stateless | 2+ replicas, anti-affinity, PDB | Manifests only |
| worker | one process | One active is correct (imports share the disk); a standby is a second replica that waits on the lease | Small: a liveness signal first |
| PostgreSQL | one node | Streaming replication with automatic failover. No operator is offered, so either Patroni-style images run as our StatefulSet, or an operator the DevOps team agrees to install (CloudNativePG is the CNCF one) | **Medium**, operational more than code |
| ClickHouse | one node, MergeTree tables | `Replicated*MergeTree` with ClickHouse Keeper (three small pods); two replicas | **Large**: every table changes engine - a migration that recreates and refills ~180 GiB, plus a Keeper ensemble. Plan, measure, then decide |
| Second site | none | ClickHouse replicas across sites; PostgreSQL replica across sites; the web front door switched by DNS | **Large**; and "active-active" for a pipeline with one writer is a question to put to the security office before designing it |

**Backup** (none exists today):

| Data | How | Restore test |
|---|---|---|
| ClickHouse | `BACKUP DATABASE sqm TO S3(...)` - native, incremental after the first full one | Quarterly, into a scratch namespace, row counts compared |
| PostgreSQL | `pg_dump` nightly to S3 at first (332 MB); WAL archiving when replication arrives | Same |
| Originals | Object storage with versioning; or the file volume backed up by the storage team | Re-import one day and compare its counts |
| Configuration | Git (manifests), the platform's Secrets | Re-create the namespace from Git |

RPO and RTO are the owner's to set (README, "Decisions needed").

## 6. Monitoring and logs

| What | How | Built? |
|---|---|---|
| CPU, memory, restarts | The platform collects them for every pod | Nothing to build |
| Application metrics | A Prometheus endpoint in the API and the worker (request rates and durations, import progress, store latencies) | No - backlog |
| ClickHouse metrics | Its built-in Prometheus endpoint (`<prometheus>` in the config, port 9363) | Not enabled |
| Dashboards | A Grafana instance through the Grafana operator, reading the platform's Thanos with the project token | Request |
| Alerts | Ours: Grafana alerting to the internal mail server | Not built |
| Logs | Every container writes stdout; the API, the worker and nginx as JSON. Forwarded to central ELK / the SOC by ticket | Forwarding: request |
| Audit trail | A PostgreSQL table today; the SOC needs it too (red lines §2-4) | Not built: an export, in a format the SOC names |

## 7. Order of work

Each step names what it is - **T** technical/infrastructure, **P** programming, **A** architecture,
**R** request (ticket or decision) - and what it waits on.

| # | Step | Kind | Waits on |
|---|---|---|---|
| 1 | Settle the five decisions in `04-…` and the README's list (ClickHouse approval, captcha, HA, synthetic data, intake path) | R | The owner; the security office |
| 2 | Domain accounts, PAM and terminal-server access for whoever deploys; `oc`/`kubectl` on the terminal server | R | 1 |
| 3 | Request namespaces dev, uat, prd with the storage of section 4; a GitLab group and runner; Nexus hosted repository | R | 1 |
| 4 | Source into MCI GitLab; `.gitlab-ci.yml` (section 3) | T | 3 |
| 5 | Kustomize manifests (section 2), NetworkPolicies from the traffic-flow table; ArgoCD application for dev | T | 3 |
| 6 | Synthetic data generator, and a data set from it | P | - |
| 7 | Deploy dev; run the acceptance checks of `02-…` against it | T | 4, 5 |
| 8 | ClickHouse least-privilege GRANTs, tested endpoint by endpoint | P | 7 |
| 9 | Captcha (if decided), admin-path separation (if decided) | P | 1 |
| 10 | Metrics endpoint; Grafana; alerts; log forwarding; audit export | P, R | 7 |
| 11 | Backups (section 5) and a restore test | T | 7 |
| 12 | Ingress IP and TLS certificate from the internal CA | R | 7 |
| 13 | Deploy uat with synthetic data; security evaluation; fix findings | T, R | 6, 8-12 |
| 14 | High availability and the second site (section 5), as decided | A, P | 1, 13 |
| 15 | Deploy prd from the approved commit; load the real data | T | 13, 14 |
