# Identity and access

How authentication, authorisation and audit are built, and how to operate them. The *why* is in
`docs/adr/ADR-006-authentication-and-access-control.md`; this is the working document.

---

## 1. The shape of it

```
browser ──cookie──▶ SessionAuthenticationHandler ──▶ auth.session ⋈ auth.user_account
                              │                            │
                              │                            └─▶ effective permissions (one query)
                              ▼
                    PermissionAuthorizationHandler ──▶ 200 / 401 / 403
                              │
                              └─ on 403 ──▶ audit.event (outcome = 'denied')
```

Three tables carry the state, one view reads the trail:

| | |
|---|---|
| `auth.permission` | The catalogue. 17 rows, extended by migration |
| `auth.role`, `auth.role_permission` | Named sets of permissions |
| `auth.user_account`, `auth.user_role`, `auth.user_permission` | People, their roles, and per-user overrides |
| `auth.session` | Live sessions, addressed by the SHA-256 of a cookie value |
| `audit.event` + view `audit.event_log` | What happened, merged with the import platform's own trail |

## 2. The authorisation rule

```
effective = (union of role permissions ∪ direct grants) − direct denies
```

Deny wins, always. It is written once as SQL — `IdentitySql.GrantedPermissionCodes` — and used by
the request pipeline, the user detail page and the last-administrator guard, so the permissions
rendered on screen cannot disagree with the ones enforced. An integration test asserts the C# and
SQL forms produce the same set for a user holding a role, a direct grant and a direct deny at once.

### The permissions

| Category | Code | What it allows |
|---|---|---|
| Analytics | `dashboard.view` | Aggregate figures and breakdowns |
| | `lookup.subscriber` | Resolve one MSISDN to its SIMs and handsets |
| | `data.export` | Download results as a file |
| | `lookup.imsi` | Resolve one SIM, by full IMSI or a prefix of ten digits or more |
| | `identifier.reveal` | See MSISDN, IMSI and IMEI in full rather than masked |
| Imports | `import.view` | The Import Center |
| | `import.upload.sqm` / `import.upload.tac` | Submit a file |
| | `import.reprocess`, `import.cancel`, `import.delete` | Act on a job |
| | `tac.activate`, `tac.rollback` | Change what the product believes about devices |
| Administration | `user.view` / `user.manage` | See / change accounts |
| | `role.view` / `role.manage` | See / change roles |
| | `audit.view` | Read the trail |
| | `system.admin` | Everything not covered by a narrower one |

Seeded roles:

| Role | Permissions |
|---|---|
| **Viewer** | `dashboard.view`, `import.view` |
| **Analyst** | + `lookup.subscriber`, `lookup.imsi`, `identifier.reveal`, `data.export` |
| **Data Operator** | + both uploads, `import.reprocess`, `import.cancel` |
| **Administrator** | all 19 |

Data Operator does **not** hold `tac.activate`. That is decision D4 of the import platform —
operator imports, administrator activates — and it corrects the table in
`04-security-model.md` §3, which predates it.

`lookup.imsi` is separate from `lookup.subscriber` because the two answer different questions for
different people: *which handset is this SIM in* is SIM operations, *which SIM does this person
have* is subscriber support. Neither implies the other, and an organisation can now grant one
without the other.

`identifier.reveal` is the masking switch. Without it the API returns redacted identifiers and the
raw values are never in the response — see `04-security-model.md` §2. It is granted to every role
that could already see them, so nothing a user could do yesterday stopped working; what is new is
that it can be taken away from one person, and that a new role starts without it.

These two were added by `db/operational/migrations/004_imsi_search_permissions.sql` and are the
demonstration that the catalogue really is extensible by INSERT: no enum widened, no type altered,
and no frontend deployed for them to appear in the permission matrix and be grantable.

### Adding a permission

1. `INSERT` into `auth.permission` in a new migration, and grant it to whichever roles should have it.
2. Add the constant to `Sqm.Application.Identity.Permissions`.
3. Require it on the endpoint: `.RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.NewThing)`.
4. Add it to `Permission` in `frontend/src/features/auth/useAuth.ts` if the UI needs to hide anything.

Steps 1 and 2 are checked against each other by a test that reads the real database. Step 3 is
checked by a test that enumerates the router. Step 4 is not checked, and does not need to be:
getting it wrong hides a button.

## 3. Sessions

| | Value | Where |
|---|---|---|
| Cookie | `__Host-sqm_session`, HttpOnly, Secure, SameSite=Strict | `Auth:Session:CookieName` |
| Idle timeout | 8 hours | `Auth:Session:IdleTimeout` |
| Absolute timeout | 24 hours | `Auth:Session:AbsoluteTimeout` |
| Touch interval | 1 minute | `Auth:Session:TouchInterval` |
| Ended sessions retained | 30 days | `Auth:Session:RetainEndedSessionsFor` |

The cookie carries 256 bits of CSPRNG output and nothing else; the database stores its SHA-256, so
a dump of `auth.session` does not hand the reader live sessions.

**Sessions are revoked** — not merely left to expire — on deactivation, password change, password
reset, an administrator's explicit revocation, and "sign out everywhere". Deactivation does it in
the same transaction as the account change: an account disabled in one transaction and signed out
in another has a window, short but real, in which it is disabled and still working.

`SessionSweeper` deletes sessions that ended more than 30 days ago, every six hours. Nothing
depends on it running — expired sessions are already unusable — it only keeps the table bounded.

### CSRF

A non-`HttpOnly` `__Host-sqm_csrf` cookie, echoed in `X-CSRF-Token` on every POST/PUT/PATCH/DELETE
that carries a session cookie, compared in constant time. `SameSite=Strict` covers the realistic
cases; this closes the edges it does not (an older browser, a same-site subdomain).

The SPA does this automatically in `api/client.ts`. **Anything calling the API by hand has to send
it too** — see §7.

## 4. Passwords

Argon2id, m=19 MiB, t=2, p=1, measured at **79 ms** per verification (ADR-006 has the table).
Stored as a PHC string, so raising the work factor later rehashes on next sign-in rather than
locking anyone out.

Policy, following NIST SP 800-63B: minimum 12 characters, no composition rules, **no scheduled
expiry**, and a rejection if the password contains the user's name or username.

Lockout: 5 consecutive failures, then 1 minute doubling to a 15-minute ceiling. Per-account only —
per-IP is the control for an internet-facing deployment, and on an internal network it mostly
locks out an office behind one NAT address.

A missing username costs the same time as a wrong password, because the login path runs a real
Argon2 derivation against a throwaway salt either way. Measured over the running API: **176 / 162 /
179 / 136 ms**, existing account and non-existent account interleaved.

The login endpoint is additionally rate-limited to **6 attempts per minute per address**. That is
not about guessing — the lockout handles that — it bounds the memory an unauthenticated caller can
make the server allocate, since each attempt costs 19 MiB.

## 5. Audit

Everything is in `audit.event_log`, which unions `audit.event` with `imports.import_audit`.

Recorded: sign-in (success, failure with reason, lockout, deactivated), sign-out, session
revocation, password change and reset, user create/update/activate/deactivate, role assignment,
permission override, role create/update/delete, role permission changes, subscriber lookup, and
**every 403**.

Role and permission changes record **before and after**, plus the added and removed sets. "Roles
are now Viewer" does not tell a reviewer that Analyst — and with it subscriber lookup — was taken
away, which is the half that matters.

**Identifiers are never written into an audit detail.** A subscriber lookup is recorded as who,
when, and how many results. Masking is off by product-owner decision, which makes this log the
primary record of who saw whom; a log full of MSISDNs would be a second copy of the data it exists
to protect.

### Append-only, verified

`db/operational/grants/003_least_privilege.sql` gives the application role INSERT and SELECT on the
audit tables and nothing else:

```
INSERT INTO audit.event ...      -> 1 row
UPDATE audit.event SET ...       -> ERROR: permission denied for table event
DELETE FROM audit.event          -> ERROR: permission denied for table event
UPDATE imports.import_audit ...  -> ERROR: permission denied for table import_audit
CREATE TABLE auth.evil (x int)   -> ERROR: permission denied for schema auth
```

`AccessControlTests.The_application_role_cannot_rewrite_or_delete_audit_history` asserts this
against the running database, connected as that role.

## 6. Operating it

### First run on a fresh database

```bash
dotnet run --project backend/src/Sqm.Migrator -- --target postgres --dir db/operational/migrations
```

```bash
psql -U sqm -d sqm -v app_password="a-strong-password" -f db/operational/grants/003_least_privilege.sql
```

```bash
SQM_BOOTSTRAP_PASSWORD='...' dotnet run --project backend/src/Sqm.Migrator -- --create-admin --username admin --display-name "System Administrator"
```

The bootstrap refuses to run if any active user can already manage users and roles. It reads the
password from stdin or `SQM_BOOTSTRAP_PASSWORD`, never from an argument — arguments appear in shell
history and in the process list.

Then sign in and create the rest from the Users page.

### Locked out of administration

The guard makes this very hard: every change to users, roles or grants checks, inside the same
transaction, that at least one active user still holds `user.manage` and `role.manage`, and rolls
back if not. If it happens anyway — by direct database access — recovery is:

```sql
INSERT INTO auth.user_role (user_id, role_id)
SELECT u.id, r.id FROM auth.user_account u, auth.role r
WHERE u.username = 'admin' AND r.code = 'administrator'
ON CONFLICT DO NOTHING;
```

run as the owner, not as `sqm_app`.

### A user cannot sign in

| Symptom | Cause | Fix |
|---|---|---|
| "Incorrect username or password" | Wrong password, no such account, or **deactivated** — the message is identical on purpose | Check the Users page |
| "Too many failed attempts" | Locked. Clears itself within 15 minutes | Unlock on their detail page |
| Signs in, then everything is refused | A password change is pending | They must complete it; the app sends them there |
| Signed out unexpectedly | Password changed elsewhere, deactivated, or an administrator revoked sessions | The audit log says which |

### Turning on TLS

`Auth:Session:RequireSecureCookies` is `true` by default and `false` only in
`appsettings.Development.json`. When false the `__Host-` prefix is dropped too — a browser rejects
a `__Host-` cookie that is not Secure, silently, with no error a developer can act on.

### If this ever faces the internet

In order of importance: add per-IP lockout, drop the idle timeout to 30–60 minutes, and revisit the
MFA decision. ADR-006 records why each is the way it is.

## 7. Calling the API without a browser

```bash
# Sign in; keep the cookie jar.
curl -s -c jar.txt -X POST -H 'Content-Type: application/json' \
     -d '{"username":"admin","password":"..."}' \
     http://localhost:5202/api/v1/auth/login

# Reads need only the session cookie.
curl -s -b jar.txt http://localhost:5202/api/v1/dashboard/kpi

# Writes must also echo the CSRF cookie in the header.
CSRF=$(grep -v '^#' jar.txt | grep sqm_csrf | awk '{print $7}')
curl -s -b jar.txt -H "X-CSRF-Token: $CSRF" -X POST ... 
```

A write without the header returns 403 with a body that says exactly this.

## 8. What is not built

- **MFA.** Not required for a strictly internal deployment. No dead columns were added for it; the
  four steps to add it are in ADR-006 and in the migration's comments.
- **LDAP / Active Directory.** The seam is `IPasswordAuthenticator`, and `auth.user_account` already
  carries `provider` and `external_id`. Adding it is one implementation plus a group-to-role mapping.
- **OIDC.** Deliberately not behind that interface — a redirect flow never sees a password. It adds
  a callback endpoint that maps a subject claim onto `external_id` and then creates a session
  through the same `ISessionStore`.
- **Self-service password reset.** Local accounts have no email delivery behind them, so a reset
  link would go nowhere. The login page says an administrator can reset it, rather than offering a
  link that silently does nothing.

## Related

- `docs/adr/ADR-006-authentication-and-access-control.md` — the decisions and their evidence
- `docs/architecture/04-security-model.md` — the wider control set
- `db/operational/migrations/003_identity_and_access.sql` — the schema, with its reasoning
- `db/operational/grants/003_least_privilege.sql` — the grants that make the audit log evidence
