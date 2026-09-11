# Security Model

**Status:** Draft — Phase 1. Authentication mechanism pending (LDAP/AD vs local) — see Q9.

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

| Role | Dashboards | Subscriber lookup | Export | Upload / import | TAC management | Admin |
|---|---|---|---|---|---|---|
| **Viewer** | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Analyst** | ✅ | ✅ | ✅ (audited) | ❌ | ❌ | ❌ |
| **Data Operator** | ✅ | ✅ | ✅ (audited) | ✅ | ✅ | ❌ |
| **Administrator** | ✅ | ✅ | ✅ (audited) | ✅ | ✅ | ✅ |

Permissions are checked **server-side on every request**. The UI hides what a user cannot do, but hiding is a
usability feature, never a security control.

Subscriber lookup is deliberately withheld from Viewer: aggregate analytics needs no ability to resolve an
individual, and most users only need aggregates.

## 4. Authentication

Pending the answer to Q9 (LDAP/AD vs local accounts). The design accommodates both behind one interface:

- **Local accounts:** Argon2id password hashing, per-user salt, configurable work factor.
- **LDAP / Active Directory:** bind-based authentication, directory groups mapped to application roles.

Session handling either way:

- Short-lived access token (15 min) plus a refresh token in an **`HttpOnly`, `Secure`, `SameSite=Strict`**
  cookie. Tokens are never placed in `localStorage`, which is readable by any injected script.
- Refresh tokens rotate on use, with reuse detection — a replayed refresh token invalidates the whole family.
- Because the refresh token is a cookie, **CSRF protection is required**: double-submit token on every
  state-changing request.
- Brute-force protection: per-account and per-IP exponential backoff, lockout with administrator alert.

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

The audit log is written to the operational store under a database role that can `INSERT` but not `UPDATE` or
`DELETE`. Administrators can read it through the UI; no application path can modify it. This is what makes it
evidence rather than decoration — and it matters more here precisely because raw identifiers are visible, so
the audit trail is the primary record of who looked at whom.

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
