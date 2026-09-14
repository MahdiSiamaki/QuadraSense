# Security Model

**Status:** Implemented. Authentication, RBAC and audit are built and verified. The decisions and
their evidence are in `docs/adr/ADR-006-authentication-and-access-control.md`; how to operate them
is in `docs/architecture/12-identity-and-access.md`. Q9 is answered: local accounts now with an
AD seam, a strictly internal deployment, and no MFA at go-live.

---

## 1. What we are protecting

This dataset is unusually sensitive, and the design should say so plainly even though masking has been
waived. It contains, for **~79 million individuals**:

- **MSISDN** — the person's phone number
- **IMSI** — their SIM's unique identity
- **IMEI** — their specific handset

Together these link a named individual to a physical device. A leak would be a serious incident, not an
inconvenience. The decision to display raw identifiers was made deliberately by the product owner
(see `docs/discovery/02-open-questions.md`, Q4), and this document takes that as given — while keeping the
controls that make the decision reversible and accountable.

## 2. Decisions taken

| Control | Decision | Rationale |
|---|---|---|
| Display masking | **Off** | Product owner's explicit decision |
| Masking capability | **Built, disabled by default** | Costs nothing now; a config flag rather than a rewrite if a regulator later requires it |
| Export audit | **On, mandatory** | Required by the original brief |
| RBAC | **On from day one** | Not optional at this data sensitivity |
| Transport | **TLS required** | Including on internal networks |
| Identifiers in logs | **Never** | Structured logs carry counts and IDs, never raw MSISDN/IMSI/IMEI |
| Identifiers in URLs | **Never** | Lookups use POST bodies, so numbers do not land in access logs, browser history or referrers |

The last two matter more than they look. A system that masks the UI but writes MSISDNs into an unrotated
nginx access log has achieved nothing. Since masking is off, keeping identifiers out of logs and URLs is the
control that is actually load-bearing.

## 3. Roles

| Role | Dashboards | Subscriber lookup | Export | Upload / import | TAC activation | Admin |
|---|---|---|---|---|---|---|
| **Viewer** | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Analyst** | ✅ | ✅ | ✅ (audited) | ❌ | ❌ | ❌ |
| **Data Operator** | ✅ | ✅ | ✅ (audited) | ✅ | ❌ | ❌ |
| **Administrator** | ✅ | ✅ | ✅ (audited) | ✅ | ✅ | ✅ |

**Corrected since this table was drafted.** Data Operator uploads a TAC snapshot but does not
activate it - that is decision D4 of the import platform, which arrived after this row was written.
Operator imports; administrator decides the product should believe it.

Roles are not the unit of enforcement. An endpoint requires a **permission**, and a role is a named
set of them that an administrator can edit without a deployment. A user may hold several roles and,
separately, individual grants and denies layered over them; a deny always wins. The full catalogue
is in `docs/architecture/12-identity-and-access.md` section 2.

Permissions are checked **server-side on every request**. The UI hides what a user cannot do, but
hiding is a usability feature, never a security control - and that is not merely asserted here:
`EndpointAuthorizationTests` enumerates the API's own route table and fails the build on any
endpoint that is neither authorised nor on a four-entry reviewed allow-list.

Subscriber lookup is deliberately withheld from Viewer: aggregate analytics needs no ability to resolve an
individual, and most users only need aggregates.

## 4. Authentication

**Local accounts**, per the product owner's answer to Q9, behind an `IPasswordAuthenticator` seam so
that Active Directory becomes a second implementation rather than a rewrite. Argon2id at OWASP's
m=19 MiB, t=2, p=1 - measured at 79 ms per verification - stored as a self-describing PHC string, so
the work factor can be raised later without invalidating a single existing password.

**Server-side sessions, not tokens.** This replaces the short-lived-JWT sketch that stood here, and
the reason is a requirement rather than a preference: deactivating a user has to take effect. A
signed token stays valid until it expires unless a revocation list is added - which is the same
per-request lookup the token was chosen to avoid, with none of its simplicity left. ADR-006 has the
full comparison.

- A 256-bit random value in an **`HttpOnly`, `Secure`, `SameSite=Strict`, `__Host-` prefixed**
  cookie. The database stores its SHA-256, never the value itself.
- Never `localStorage`, which is readable by any injected script.
- 8-hour idle timeout; 24-hour absolute ceiling that no amount of activity extends.
- Sessions are revoked, not merely expired, on deactivation, password change or reset, and explicit
  revocation - and deactivation does it in the same transaction as the account change.
- **CSRF**: double-submit token on every state-changing request, compared in constant time.
- Brute force: 5 failures, then a lockout doubling from 1 minute to a 15-minute ceiling, per
  account. Per-IP backoff is the control for an internet-facing deployment and is the first thing
  to add if the exposure ever changes.
- A username that does not exist costs the same time as a wrong password, because the login path
  runs a real Argon2 derivation either way. Measured: 176 / 162 / 179 / 136 ms across existing and
  non-existent accounts. Without it the endpoint is a username oracle, and knowing which accounts
  exist is what makes a password spray cheap.
- The endpoint is rate-limited to 6 attempts per minute per address. That is not about guessing -
  the lockout handles that - it bounds the memory an unauthenticated caller can make the server
  allocate, since each attempt costs 19 MiB.

**MFA is not built**, per the product owner's answer for a strictly internal deployment. No unused
columns were added for it; the four steps to add it are recorded in ADR-006.

## 5. Upload security

File upload is the largest attack surface, because it accepts bulk data from outside.

| Threat | Control |
|---|---|
| Path traversal | Generated storage names only; the client-supplied filename is stored as metadata and never used as a path |
| Zip bomb / oversized file | Hard byte cap, enforced streaming (never buffered whole in memory) |
| Content-type spoofing | Extension and declared MIME are advisory; the file is validated by **parsing** it against the expected schema |
| Malicious CSV content | Values are never evaluated; CSV-injection prefixes (`=`, `+`, `-`, `@`) are neutralised on export |
| Resource exhaustion | Uploads are queued as background jobs with concurrency limits, never processed in the request thread |
| Duplicate / replayed file | SHA-256 content hash checked before any processing |
| Storage exhaustion | Quota per batch and overall, with alerting |

Uploaded files are stored **outside the web root** and are never directly servable.

## 6. Query and injection safety

- All SQL is parameterised. No string concatenation of user input, without exception.
- Filter values are validated against allow-lists derived from the schema (column names, sort directions,
  operators) — a user cannot name an arbitrary column or inject an expression.
- Result-set size is capped server-side regardless of what the client asks for; anything larger becomes a
  background export job.
- Query timeouts are enforced at the database, not only in the application, so a runaway aggregation cannot
  pin a core indefinitely.

## 7. Web hardening

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'self'`; no `unsafe-inline`, no `unsafe-eval` |
| `Strict-Transport-Security` | `max-age=63072000; includeSubDomains` |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Referrer-Policy` | `no-referrer` |
| `Permissions-Policy` | deny camera, microphone, geolocation |

CORS is restricted to the known frontend origin; wildcards are not used. XSS is mitigated primarily by Vue's
default escaping, and `v-html` is banned by lint rule rather than by convention.

## 8. Audit log

Append-only. Recorded for: login success and failure, logout, subscriber lookup, **export (with the filter
and row count)**, upload, import start/complete/fail, reprocess, TAC upload and rollback, vendor-map edit,
user and role changes, and configuration changes.

Each entry carries: timestamp, user, source IP, action, target, outcome, and correlation ID.

The audit log is written under a database role that holds `INSERT` and `SELECT` and nothing else
(`db/operational/grants/003_least_privilege.sql`). Administrators read it through the UI; no
application path can modify it. This is what makes it evidence rather than decoration - and it
matters more here precisely because raw identifiers are visible, so the audit trail is the primary
record of who looked at whom.

That was an aspiration when it was first written here, and the application connected as the schema
owner. It is now true, and verified rather than asserted: `UPDATE` and `DELETE` on both audit tables
are refused by PostgreSQL, and an integration test connects as that role and proves it.

Two additions worth naming. **Every 403 is recorded**, with the permission that was required - a
refusal that leaves no trace is how someone probing for what they can reach stays invisible. And
**role and permission changes record before and after**, because "roles are now Viewer" does not
tell a reviewer what was taken away, which is the half that matters.

## 9. Secrets

- No secret is ever committed. `.env.example` documents every variable by name with a placeholder value.
- Secrets are supplied by environment variables at deploy time; the repository contains a `.gitignore` that
  excludes `.env` and a CI secret-scanning step.
- Database credentials are per-service and least-privilege: the API cannot `DROP`, the ingestion worker
  cannot read the audit table, the audit writer cannot `DELETE`.

## 10. Dependency and supply chain

- Dependencies pinned to exact versions with a committed lock file.
- CI runs vulnerability scanning on every build and fails on high/critical findings.
- Dependency count kept deliberately small (nine runtime frontend packages) — the most reliable defence
  against supply-chain risk is not depending on much.

## 11. Residual risks

| Risk | Status |
|---|---|
| Raw PII visible to all authenticated users | **Accepted by product owner.** Mitigated by RBAC, mandatory export audit, no identifiers in logs or URLs, and a masking switch ready to enable |
| Insider misuse of subscriber lookup | Mitigated by audit; detection depends on someone reviewing it — recommend periodic review, and alerting on abnormal lookup volume per user |
| No DLP on exports | Exports are audited but not blocked. If this becomes a concern, add per-user export row quotas |
