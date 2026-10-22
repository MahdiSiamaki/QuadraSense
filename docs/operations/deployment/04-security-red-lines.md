# MCI security red lines, against QuadraSense

**Status:** written 2026-10-22, from the MCI network-security form *Red lines and security
requirements* (FR-NSM-204/00, revision 00, dated 1404/01/08). Re-check every row when the form is
revised.

**What this is.** Each requirement of the form, in our own words, set against what QuadraSense does
today, and what has to happen for it. The form says its red lines must be studied and met **before a
project starts**, and its appendices while it is built; MCI's security office approves a system on a
test bed before it goes into production (§1-3). This table is the starting point for that review,
not a claim that the review would pass.

**Not reproduced here.** The form is MCI's document, distributed under its own terms, and this
repository is public. Requirements are paraphrased and cited by section; names, addresses and
contact details in the form are left out on purpose. The product owner holds the form.

Legend: **met** - true today and checked; **partly** - part of it is true; **gap** - not true
today; **decide** - needs a decision before anything is built; **n/a** - does not apply.

Stage 1 is the department VM (`02-department-vm.md`), stage 2 MCI's OpenShift (`03-mci-openshift.md`).

---

## Decisions this table needs first

Five rows below cannot be closed by engineering alone. They are repeated in `README.md`,
"Decisions needed", with a recommendation each.

1. **ClickHouse is not on the approved database list** (§1-1, table; §2-1). It is "Other", which
   needs the security office's approval, and the support burden is the owner's (§2-1). ADR-003 holds
   the evidence for it: 26-35x faster than the alternatives on the dashboard queries, 3.4x smaller.
2. **A captcha with Persian digits and letters** is required on the login page after three failed
   attempts (§2-3). The product is English-only by the owner's decision (CLAUDE.md). One of the two
   has to give, or the security office has to grant an exception.
3. **Clustering, and a second site running active-active** (§2-3). Today every store is one node.
   This is the largest architecture change in the list - see `03-mci-openshift.md`, "High
   availability".
4. **Real data on test servers is forbidden** (§1-3): the security test bed must hold made-up data.
   QuadraSense has no synthetic data set; one has to be built before the security review.
5. **Docker on a standalone server** (§1-1): a containerised system is to run on MCI's own container
   platform, not on Docker installed on a separate machine. Whether the department VM - stage 1 - is
   inside the scope of this form is a question for the owner and the department, not for code.

---

## 1. Red lines

### §1-1 Platform and applications

| # | Requirement | QuadraSense today | Action | Stage | |
|---|---|---|---|---|---|
| 1 | Install the OS, databases and services minimally; remove everything not used | Images hold the application and its runtime only; ClickHouse and PostgreSQL are their official images (Ubuntu / Debian userland). The VM's OS is the department's | Decide the base images (below, row 3). On the VM, minimal OS install is the department's | 1, 2 | partly |
| 2 | Application and database on separate servers; monitoring on its own server too | Stage 1 runs everything on one VM. On OpenShift each is its own pod | Stage 1: a known deviation for a test environment - confirm it is acceptable. Stage 2: ask the security office whether separate pods (with anti-affinity onto different nodes) satisfy "separate servers" | 1, 2 | decide |
| 3 | Red Hat Enterprise Linux 8.10 / 9.x, licensed by MCI; anything else only with the security office's approval | The VM is Ubuntu 26.04. Container base images: Microsoft's .NET image (Ubuntu), postgres (Debian), nginx (Alpine), ClickHouse (Ubuntu). On OpenShift the nodes are Red Hat's | Ask whether the rule extends to container base images. If it does, Red Hat's UBI images exist for .NET and nginx - `01-container-images.md`, "Base images" | 1, 2 | decide |
| 4 | Nothing out of vendor support | .NET 10 (LTS), PostgreSQL 17, nginx 1.28 (stable), Node 24 (LTS, build only), Ubuntu 26.04 (LTS) | **ClickHouse 25.8**: an LTS line, but ClickHouse supports only its most recent LTS lines - check its window before the review and plan the upgrade (a new image tag; the migrations are plain SQL) | 2 | partly |
| 5 | Latest security patches on everything | Every image tag is pinned exactly, which makes a build reproducible and also means nothing updates itself | A patch routine: rebuild on base-image releases, scan images in CI (`03-…`, "CI/CD") | 2 | gap |
| 6 | Software from legitimate providers with timely updates | Official images and packages only | - | - | met |
| 7 | A containerised system runs on MCI's integrated container platform; Docker, Kubernetes or Rancher on separate physical or virtual servers is not allowed | Stage 1 is Docker on the department VM | Decision 5 above. Stage 2 is the platform itself | 1 | decide |
| 8 | Ceph only via MCI's central Ceph | OpenShift's storage classes are the central Ceph | Request rbd / CephFS / S3 through the platform (`03-…`, "Storage") | 2 | met by design |
| 9 | No cracked software; licences that receive updates | Open source throughout | - | - | met |
| 10 | Use the organisation's shared services (Git, Grafana, Prometheus, Zabbix, ELK, SFTP, Ansible Tower, ...) rather than private copies | Code on GitHub; no monitoring or log stack of its own | Code into MCI's GitLab; metrics through the platform's monitoring and a Grafana; logs to the platform's ELK / the SOC; operator files through the central SFTP if that is the intake (below) | 2 | gap |
| 11 | Technologies from the approved table; "Other" needs approval | PostgreSQL, nginx, OpenShift: listed. **ClickHouse: not listed.** Kestrel serves the API behind nginx | Decision 1 above: request approval, with ADR-003 as the evidence | 2 | decide |

### §1-1 (continued) SFTP servers

Applies only if the operator's daily files reach QuadraSense by SFTP. Today they are uploaded through
the Import Center in a browser.

| # | Requirement | Action | |
|---|---|---|---|
| 1 | A dedicated, hardened SFTP server (Red Hat, OpenSSH) | Decide the intake path (README, "Decisions needed") | decide |
| 2 | One fixed non-standard port for SFTP/SCP only; servers reach a central SFTP server rather than each other | If SFTP: the worker would read from the central SFTP server - a new import source, not built | decide |
| 3 | The SFTP account cannot log in to a shell | - | - |
| 4 | Port 22 only for administration from the terminal server; never between servers | No SSH in any container | met |

### §1-2 Network access

| # | Requirement | QuadraSense today | Action | Stage | |
|---|---|---|---|---|---|
| 1 | Administration panel deployed apart from the user panel; reachable only from the internal network or through PAM | One application: user, role and audit administration are pages of it, behind permissions (ADR-006). The whole system is internal-only | Either a second entry point for the administration API paths, reachable only from PAM's address range (nginx can split by path and source address with no code change), or the security office accepts in-app RBAC on an internal-only system | 2 | decide |
| 2 | No management protocols (SSH, RDP) between servers; manage through PAM or Ansible Tower | No SSH in any container. VM administration is the department's | - | 1 | met |
| 3 | Files move between servers over web or web-service protocols, never over SSH/RDP | Uploads are HTTPS. **Moving images and data to the VM** is not yet decided | `02-…`, "Getting the software onto the VM" | 1 | decide |
| 4 | Every web system behind WAF and IPS | Nothing of ours | Request it with the network access (stage 2); ask the department (stage 1) | 1, 2 | gap |
| 5 | Service networks carry service traffic; people connect through PAM | Users reach the web UI as a service; nobody connects to a container | - | - | met |

### §1-3 Security evaluation and hardening

| # | Requirement | QuadraSense today | Action | Stage | |
|---|---|---|---|---|---|
| 1 | Evaluation on a test bed with the production architecture on minimal hardware; one test server per kind of server, two behind a load balancer if production has one | OpenShift's UAT namespace is that test bed | Deploy UAT from the same manifests as production (GitOps) | 2 | planned |
| 2 | **Test-bed data is made up and sample-sized, never real** | **No synthetic data exists.** Every test so far used the real dump | Build a generator for operator-format daily files and a TAC file, with the shapes the pipeline depends on (dual-SIM pairs, the 15 September shift, invalid identifiers) | 2 | gap |
| 3 | The test bed goes to the security office before production; production must match what was approved | - | Production from the same commit and manifests as the approved UAT | 2 | planned |
| 4 | No captcha, script, style or font loaded from outside MCI's data centres | Checked 2026-10-22: no external URL in `frontend/src` or `index.html`; the web image's Content-Security-Policy is `'self'` for everything | - | 1, 2 | met |

### §1-4 The central container service

| # | Requirement | QuadraSense today | Action | Stage | |
|---|---|---|---|---|---|
| 1 | Pod Security Admission enforced | Every container runs non-root, read-only, no capabilities, no privilege escalation - the `restricted` profile's terms (`01-…`, "Verified") | Confirm on the cluster | 2 | met by design |
| 2 | One namespace per project, no access between namespaces | The platform's | - | 2 | met by design |
| 3 | dev, uat and prd fully isolated | Three namespaces; nothing shared, not even a database | Request all three | 2 | planned |
| 4 | Each service gets its own ingress IP and egress IP | One ingress IP for the web front door; no egress today (closed network) | Egress only when something calls out: the internal mail server for alerts (research item X1), LDAP if adopted | 2 | planned |
| 5 | Pods of one service cannot reach another's; each connection (X to database Y on port P) is requested | Compose: two networks, `edge` (web, API) and `data` (internal: stores, API, worker, jobs) | OpenShift: NetworkPolicies, deny by default, one rule per arrow of the traffic-flow table (`03-…`) | 1, 2 | partly |
| 6 | Multi-stage builds; no `latest` tag | Both Dockerfiles are multi-stage; every tag is exact | - | 1, 2 | met |
| 7 | No privileged containers | None | - | 1, 2 | met |
| 8 | Immutability: read-only root filesystem, only the capabilities needed | Read-only root, all capabilities dropped, verified for every container | - | 1, 2 | met |
| 9 | Never run as root | Every container runs as an arbitrary UID in group 0, verified | - | 1, 2 | met |
| 10 | CPU, memory and disk limits on every pod | CPU and memory limits on every container | Ephemeral-storage limits and a ResourceQuota on OpenShift | 1, 2 | partly |
| 11 | Least privilege for people and services | PostgreSQL: the application role cannot alter the schema or rewrite the audit log (`db/operational/grants/003_…`). **ClickHouse: the application users have every privilege** - two secrets, one privilege set | GRANTs per user (`sqm_app`: SELECT plus the marts it writes), with a test that exercises every endpoint as that user | 2 | gap |

---

## 2. Appendices

### §2-1 Platform basics

| # | Requirement | QuadraSense today | Action | |
|---|---|---|---|---|
| 1 | Packages and images from MCI's central repositories: a proxy of the public ones, and a hosted one for the project's own | The Dockerfiles take the NuGet feed, the npm registry and every base image as build arguments | Point them at the Nexus proxies in CI | met by design |
| 2 | Central DNS | Infrastructure | - | n/a |
| 3 | Services containerised and ready for the integrated cloud | This change | - | met |
| 4 | Firewalls in the OS and in every service; defence in depth | Compose: internal `data` network; nothing but the web container publishes a port | VM firewall (department); NetworkPolicies (stage 2) | partly |
| 5 | Oracle 19c is the recommended database; with any other, support is the owner's | PostgreSQL and ClickHouse | Name who supports them - see decision 1 | decide |
| 6 | A platform outside the approved list needs a meeting with the security office and a migration plan | ClickHouse | Ask for the meeting early: everything downstream depends on it | decide |
| 7 | Servers are not handed over with root | Running Docker on the VM needs root or the `docker` group, which is equivalent | `02-…`, "Access" | decide |
| 8 | **HTTPS for every application, with a certificate from MCI's internal CA** | The web image serves TLS 1.2/1.3 when given a certificate; plain HTTP only redirects | Request the certificate. **Between services** (API to ClickHouse and PostgreSQL) traffic is plain inside the private network today - ask whether the rule extends to it; both stores support TLS | partly |
| 9 | Systems talk through APIs, never to each other's databases | Nothing outside reads our stores; another system wanting the data gets an API | - | met |
| 10 | No monitoring over SSH or WMI; use metrics, syslog or agents | Logs are JSON on stdout. No metrics endpoint | A Prometheus endpoint in the API and worker; ClickHouse has its own (`03-…`, "Monitoring") | partly |

### §2-2 Network basics

| # | Requirement | QuadraSense today | |
|---|---|---|---|
| 1 | People reach services and servers through PAM, except where approved | Infrastructure | n/a |
| 2 | Git over HTTPS, not SSH; developers push from VDI, servers pull | The repository is used over HTTPS | met |
| 3 | No production access before the security office approves the system | Process | planned |
| 4 | Only the logical ports a connection needs; no SSH between application and database | Ports per arrow in `03-…`, "Traffic flow" | met by design |

### §2-3 Evaluation and hardening basics

| # | Requirement | QuadraSense today | Action | |
|---|---|---|---|---|
| 1 | **A system identification document, its architecture and its traffic-flow map, approved by the security office** | `docs/architecture/` describes the system; the traffic-flow table is in `03-…` | Assemble them in the office's format | gap |
| 2 | SMS one-time passwords: six digits, random, void after five failures, expire in ten minutes | No OTP. MFA is recorded as not built (`docs/backlog.md`) | Follow these terms if MFA is ever added | n/a |
| 3 | **Captcha**: hidden at first, shown after three failed logins (five elsewhere); Persian digits and letters in several Persian fonts; at least ten patterns; resistant to OCR; with an on-screen keyboard | Login is rate-limited (six attempts a minute per address) and every attempt costs an Argon2id verification (ADR-006). **No captcha** | Decision 2 above; then a self-hosted captcha (row 4 of §1-3: nothing external) | gap |
| 4 | Prefer CNCF-landscape software; horizontal scaling in code, service and database layers | API stateless (sessions in PostgreSQL), can run several replicas. One worker. **Each store is one node** | `03-…`, "High availability" | partly |
| 5 | TLS 1.2 or later | The web image allows 1.2 and 1.3 only | - | met |
| 6 | Uploads: format and size limits, renamed, stored where nothing executes and nobody else reads | Size enforced while writing (`ImportStorage:MaxFileBytes`); stored under a generated name, never the client's (`DirectoryImportFileStore`); in a volume only the API and worker mount, mode 2770. The format is judged by the parser, which quarantines what it cannot read | Mount the VM's data disk `noexec` | met |
| 7 | No sensitive data in GET requests | Checked 2026-10-22: lookups are POST bodies; no GET route or query string carries an MSISDN, IMSI or IMEI | - | met |
| 8 | Secrets that need not be read back are stored hashed | Passwords Argon2id; session tokens SHA-256 (`PostgresSessionStore`) | - | met |
| 9 | **Clustered servers; every service active-active across two sites** | Single nodes | Decision 3 above | gap |
| 10 | OS and services hardened to CIS / DISA benchmarks | Containers follow the CIS Docker items that apply to an image (non-root, read-only, no capabilities, limits, health checks) | OS hardening is the department's (stage 1) and the platform's (stage 2) | partly |
| 11 | Platform tested per NIST SP 800-115 / ISSAF; application per NIST SP 800-70 and the OWASP Testing Guide v4 | Never tested | Expect a penetration test; self-check against OWASP ASVS first | gap |

### §2-4 Backup and incidents

| # | Requirement | QuadraSense today | Action | |
|---|---|---|---|---|
| 1 | Logs of every OS, application, database and service reach the SOC's collectors | Every container logs to stdout (the API and worker as JSON; nginx as JSON; ClickHouse once `logging.xml` stopped it writing files). The application's own audit trail is a PostgreSQL table | Platform log forwarding (stage 2, a ticket); an export of the audit table to the SOC - which events, in which format, is for the SOC to say | gap |
| 2 | Backups of sensitive data and configuration, coordinated with the infrastructure team | None, beyond the originals kept forever (CLAUDE.md, retention) | `02-…`, "Backups"; `03-…`, "Backup" | gap |

### §2-5 APN, §3 Android, §4 iOS

Not applicable: QuadraSense is not reached over an APN and has no mobile application.
