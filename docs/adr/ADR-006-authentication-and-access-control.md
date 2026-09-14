# ADR-006 — Authentication and access control: local accounts, server-side sessions, permission-based RBAC

- **Status:** Accepted
- **Date:** 2026-09-14
- **Supersedes:** the pending §4 sketch in `docs/architecture/04-security-model.md`

---

## Context

The system holds MSISDN, IMSI and IMEI for ~75 million individuals, displayed unmasked by the
product owner's explicit decision (Q4). That decision makes access control the load-bearing
control rather than one of several, and makes the audit log the primary record of who saw whom.

Four facts were established with the product owner before anything was chosen. They are the
reason the rest of this document reads the way it does:

| Question | Answer |
|---|---|
| Identity source, now and later | **Local accounts now**, with AD/LDAP/OIDC likely later — build the seam |
| Network exposure | **Strictly internal.** No internet path, no VPN-only path |
| Session mechanism | **Server-side sessions**, after the trade-off below was put to them |
| MFA at go-live | **Not now.** Make adding it cheap |

Three requirements come from the brief itself and constrain the design more than the answers do:

1. **Users may hold several roles, and individual permissions beyond them.**
2. **Authorisation must be enforced in the backend**, not merely hidden in the UI.
3. **Every login, role change, permission change and administrative operation must be auditable.**

## Decision 1 — Permissions are rows, roles are collections of them, and code names permissions

Endpoints require a **permission**, never a role. `RequirePermission(Permissions.TacActivate)`,
not `RequireRole("Administrator")`.

The difference matters when the organisation changes. A role check spreads a policy decision
across every endpoint that mentions the role: giving a new "Senior Analyst" the ability to
activate a TAC version means editing and redeploying the API. A permission check puts the policy
in one place an administrator can edit, and the endpoint never changes.

The catalogue lives in `auth.permission` — a table, seeded by migration:

| Category | Permissions |
|---|---|
| Analytics | `dashboard.view`, `lookup.subscriber`, `data.export` |
| Imports | `import.view`, `import.upload.sqm`, `import.upload.tac`, `import.reprocess`, `import.cancel`, `import.delete`, `tac.activate`, `tac.rollback` |
| Administration | `user.view`, `user.manage`, `role.view`, `role.manage`, `audit.view`, `system.admin` |

Seventeen permissions, each with a category, a description an administrator can act on, and an
`is_dangerous` flag that drives how the UI presents granting it.

**Adding one is an INSERT in a migration plus the code that enforces it.** No enum to widen, no
type to change, no frontend release for it to appear in the matrix.

A `Permissions` static class carries the codes as constants, because an endpoint has to name one
at compile time and a repeated string literal is a typo away from an unprotected route. The two
are kept honest by a test that asserts **both directions against the real database**: every
constant has a row, every row has a constant. A permission in the database with no code cannot be
enforced; a permission in code with no row cannot be granted. Both failures are silent.

### Why per-user overrides exist, with deny winning

Roles alone force a new role for every exception. "Analyst, but may not run subscriber lookups"
becomes a fifth role, then a sixth, and a year later nobody can say what separates `analyst_b`
from `analyst_c`. That is the failure mode RBAC actually has in the field, and it is worse than
the concept that avoids it.

So `auth.user_permission` layers direct grants and denies over role membership:

```
effective = (union of role permissions ∪ direct grants) − direct denies
```

Deny wins unconditionally. A deny is the control an auditor asks about — *"you removed her lookup
access on the 3rd; show me"* — so it must not be defeatable by adding a role.

The cost of override systems is that nobody can explain the result. That is paid for directly:
the user detail page renders, for **every** permission, whether it is held, which roles grant it,
whether a direct grant or deny applies, and the reason recorded with the override. The API
returns that provenance rather than a boolean, because a screen showing only the final answer
cannot answer either of the two questions anyone ever asks of an RBAC system.

### Options rejected

**Roles hard-coded as an enum.** Simplest, and it makes every policy change a deployment.
Rejected on the requirement that roles and permissions be definable by an administrator.

**Permissions as a bit flag on the user.** Compact and fast. Rejected: 64 permissions is a
ceiling reached sooner than anyone expects, a bitmask is unreadable in an audit entry, and
removing a permission from the catalogue silently shifts the meaning of every stored value.

**A policy engine (Casbin, OPA).** Genuinely more expressive — resource-scoped rules, conditions,
hierarchies. Rejected as overengineering for a single-tenant system with one dataset and no
per-row ownership: there is no rule to express here that a permission set cannot. The seam to
adopt one later is the same one that already exists — everything goes through one authorisation
handler.

## Decision 2 — Local accounts behind an authenticator interface

Argon2id-hashed local accounts, reached through `IPasswordAuthenticator`. Active Directory is a
second implementation of the same interface: username and password in, an outcome out.

OIDC deliberately does **not** implement that interface, and pretending it could would be the
expensive kind of abstraction. A redirect flow never sees a password; it adds a callback endpoint
that maps a subject claim onto `auth.user_account.external_id` and then creates a session through
the same `ISessionStore`. `provider` and `external_id` are in the schema from the first migration
because retrofitting them means rewriting uniqueness rules on a table that already holds accounts
— one column now against a migration under pressure later.

**Authorisation is never delegated, whatever the identity source.** Directory group membership
would map to roles held here, because the permission catalogue is this system's concept and no
directory knows what `tac.activate` means.

### Password hashing, measured

Argon2id over PBKDF2 and bcrypt: it is memory-hard, so a GPU or ASIC attack is expensive rather
than merely slow. OWASP lists it first.

Measured on the development machine (12 cores), 8 hashes per configuration after a warm-up:

| Configuration | ms / hash | hashes/s per core |
|---|---:|---:|
| **m=19 MiB, t=2, p=1 — OWASP #2, chosen** | **79.1** | **12.6** |
| m=45 MiB, t=1, p=1 — OWASP #1 | 75.5 | 13.2 |
| m=19 MiB, t=3, p=1 | 86.6 | 11.6 |
| m=32 MiB, t=2, p=1 | 93.4 | 10.7 |
| m=64 MiB, t=2, p=1 | 185.7 | 5.4 |
| m=128 MiB, t=2, p=1 | 399.9 | 2.5 |
| m=19 MiB, t=2, p=2 | 30.8 | 32.4 |

79 ms is in the right band for interactive sign-in: imperceptible to a person, and it caps an
offline attacker at ~13 guesses per second per core against a stolen hash.

The tempting move is 64 MiB for triple the attacker cost at 186 ms. It was not taken, and the
reason is not performance. **The login endpoint is unauthenticated**, so its memory cost is
something anyone on the network can invoke: at 64 MiB, twenty concurrent requests is 1.3 GiB on a
container that shares a machine with ClickHouse. 19 MiB bounds the same burst to 380 MiB.

The cost of a wrong guess is bounded by a **concurrency limiter on the login endpoint** rather
than by choosing weak parameters — which is the control that actually addresses the problem, and
leaves the work factor free to rise on hardware that can afford it.

Hashes are stored as PHC strings (`$argon2id$v=19$m=19456,t=2,p=1$…`). Every hash records the
parameters that produced it, so raising the work factor later does not invalidate existing
passwords: they are rehashed on the owner's next successful sign-in. A scheme that cannot do that
is a scheme whose work factor never rises.

### Password policy

NIST SP 800-63B: minimum 12 characters, no composition rules, **no scheduled expiry**. Forced
rotation produces `Summer2026!` followed by `Autumn2026!` and is a documented net negative. The
one extra check is rejecting a password containing the username or display name, which is the
first thing anyone guessing a password for a known account tries.

An administrator can still require a change for a specific account when there is a reason to.

## Decision 3 — Server-side sessions, not JWT

This one was put to the product owner explicitly, because `04-security-model.md` §4 had already
sketched short-lived JWTs with rotating refresh tokens, and that sketch conflicts with a stated
requirement.

**The conflict.** "Deactivate a user" and "change a user's roles" must take effect. A signed token
is valid until it expires: an account disabled at 10:00 keeps working until 10:15. The standard
fix is a revocation list — a per-request lookup against shared state, which is exactly the cost
the stateless token was chosen to avoid, with none of its simplicity left. Paying that cost while
still carrying the token's staleness is the worst of both.

So: a 256-bit random value in an `HttpOnly` cookie, with the session in PostgreSQL.

| | Server-side session | JWT + refresh |
|---|---|---|
| Revocation | Immediate | Delayed, or a revocation list |
| Role change takes effect | Next request | Next token refresh |
| Per-request cost | One indexed lookup | None, or one lookup with a list |
| Horizontal scale | Shared database | Shared secret |
| Failure mode | Database down → nobody signs in | Leaked signing key → silent, universal |

The per-request lookup is the whole cost, and it is one primary-key hit against a table that will
hold hundreds of rows. On a single-tenant internal system with tens of users, stateless
authentication solves a problem this deployment does not have.

There is deliberately **no cache of resolved sessions**. A cache reintroduces precisely the
staleness that choosing sessions removed, and does it invisibly: revocation would pass every test
and fail under load on whichever instance held the stale entry.

### Cookie and session mechanics

| Property | Value | Why |
|---|---|---|
| Name | `__Host-sqm_session` | The `__Host-` prefix is browser-enforced: the cookie is refused unless it is Secure, has no `Domain`, and has `Path=/`. A sibling subdomain cannot set or overwrite it |
| `HttpOnly` | yes | Not readable by script, so an XSS cannot exfiltrate the session |
| `Secure` | yes | Configurable off only for plain-HTTP local development, which also drops the `__Host-` prefix — a browser would otherwise reject the cookie silently |
| `SameSite` | `Strict` | The primary CSRF control |
| Idle timeout | 8 hours | A working day with a lunch break, on an internal network where the threat is an unattended desk. 30–60 minutes if this ever faces the internet |
| Absolute timeout | 24 hours | A ceiling no activity extends, so a stolen cookie has a bounded life — the idle window alone does not bound it, because the attacker keeps refreshing it |
| Token storage | SHA-256 in the database | A dump of `auth.session` does not hand the reader live sessions. Plain SHA-256 is correct here and would be wrong for a password: the input is 256 bits of CSPRNG output, so there is nothing to brute-force |

**Not `localStorage`.** Any injected script can read it. The cookie is the only option that an XSS
cannot exfiltrate, and the whole CSRF apparatus below is the price of that property — worth
paying, because XSS is the attack that actually happens.

**CSRF.** `SameSite=Strict` plus an origin-restricted CORS policy covers the realistic cases, and
a double-submit token is added anyway: a non-`HttpOnly` `__Host-sqm_csrf` cookie echoed in an
`X-CSRF-Token` header on every state-changing request, compared in constant time. Roughly thirty
lines, and it closes the residual paths `SameSite` does not — an older browser, or a same-site
subdomain the attacker controls.

**Sessions are revoked**, not merely expired, on: deactivation, password change or reset, an
administrator's explicit revocation, and "sign out everywhere". A password change that leaves the
attacker's session alive has not recovered the account, which is the reason a person changes a
password they believe is compromised.

### Lockout

Five consecutive failures locks the account, doubling from 1 minute to a 15-minute ceiling. The
cap is not a compromise: doubling without one produces locks measured in days, which is a denial
of service any colleague can perform on any other by typing a wrong password twenty times.
Fifteen minutes already makes online guessing hopeless.

Per-account only. Per-IP throttling is the control for an internet-facing deployment, where an
attacker sprays one password across many accounts and never trips a per-account counter; on an
internal network it mostly locks out an office behind one NAT address. **If the exposure answer
changes, this is the first thing to revisit.**

A missing username costs the same time as a wrong password, by running a real Argon2 derivation
against a throwaway salt. Without it the login endpoint is a username oracle — and knowing which
accounts exist is what makes a password spray cheap.

## Decision 4 — Multi-factor is not built, and the path to it is written down

Not required for a strictly internal deployment, per the product owner. **No dead columns were
added for it**, because a schema full of unused MFA fields is indistinguishable from a schema
whose MFA is broken.

What makes it cheap instead: `LoginOutcome` is a status record rather than a user-or-null, so a
`MfaRequired` status is an addition rather than a signature change. Adding TOTP is then:

1. a migration creating `auth.user_mfa` (secret, enrolled_at, recovery code hashes),
2. a second step in the login endpoint between password check and session creation,
3. an enrolment page,
4. an `mfa_required` flag on `auth.role`, so it can be demanded of administrators only.

## Decision 5 — Enforcement is server-side and its coverage is tested

The requirement was explicit: do not merely hide permissions in the frontend.

- Every endpoint carries an authorisation policy. The default is deny.
- **A test enumerates the application's own `EndpointDataSource` and asserts that every endpoint
  either requires authorisation or appears in a short, named allow-list** (health checks, the
  login endpoint, the OpenAPI document). Forgetting to protect a new endpoint fails the build
  rather than shipping.
- A 403 is written to the audit log with outcome `denied`. Refusals that leave no trace are how a
  probing insider stays invisible.
- The frontend hides what a user cannot do, as a usability feature. It is never the control.

## Decision 6 — The audit log is append-only by grant

`docs/architecture/04-security-model.md` §8 already claimed this. It was not true: the application
connected as the schema owner and could have rewritten any audit row.

`db/operational/grants/003_least_privilege.sql` creates an `sqm_app` role with INSERT and SELECT
on the audit tables and nothing else. Verified against the running database rather than asserted:

```
INSERT INTO audit.event ...      -> 1 row
UPDATE audit.event SET ...       -> ERROR: permission denied for table event
DELETE FROM audit.event          -> ERROR: permission denied for table event
UPDATE imports.import_audit ...  -> ERROR: permission denied for table import_audit
CREATE TABLE auth.evil (x int)   -> ERROR: permission denied for schema auth
```

This survives a bug in the application, a compromised API process, and anyone holding the
application's password. It does not survive the owner's or a superuser's credentials — nothing at
this layer does, and saying otherwise would repeat the original mistake.

Identity and administrative events go to `audit.event`; the import platform keeps writing to
`imports.import_audit`, which has columns this table does not and tested code behind it. A view,
`audit.event_log`, presents both as one stream for the Audit Log page. **Identifiers are never
written into an audit detail** — a subscriber lookup is recorded as who, when and how many
results, not which number — because masking is off, which makes an audit log full of MSISDNs a
second copy of the data it exists to protect.

## Consequences

**Gained.** Policy is editable by an administrator rather than by a deployment. Revocation is
immediate. Every permission a user holds can be explained, in the UI, with its source. The audit
trail is evidence rather than a log.

**Given up.** Stateless authentication, and with it the option of validating a session without
touching PostgreSQL. If the API is ever split into services that must authorise independently,
each one needs a database connection or a gateway must resolve the session and pass the result
inward. On a single-tenant internal system this is not a constraint that binds.

**Watch for.** The database becomes the availability dependency for sign-in — it already was for
imports, so no new class of outage. Session rows accumulate; a sweeper deletes those ended more
than 30 days ago, and the audit log keeps the permanent record either way.

**The stated trigger for revisiting this:** if the deployment ever becomes internet-facing, the
per-IP lockout, the 8-hour idle timeout, and the MFA decision are all wrong, in that order.

## Related

- `docs/architecture/04-security-model.md` — the controls this implements
- `docs/architecture/12-identity-and-access.md` — how it is built, and how to operate it
- `db/operational/migrations/003_identity_and_access.sql` — the schema, with its reasoning
- `db/operational/grants/003_least_privilege.sql` — the grants that make the audit log evidence
