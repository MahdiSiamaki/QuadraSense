# ADR-022: Getting back in, and asking for more than a password

- **Status:** Accepted
- **Date:** 2026-11-07
- **Decided by:** the product owner asked on 2026-11-06 for a redesigned sign-in with password recovery
  by e-mail and by a phone number proved beforehand, remember me, session management and "complementary
  features, two-step sign-in among them, chosen by what the system needs". The choices below were left to
  this work and are the owner's to overturn.
- **Replaces:** ADR-006 Decision 4 (multi-factor not built), and the "not built" entries of
  `docs/architecture/12-identity-and-access.md` section 8 for MFA and self-service reset.
- **Binding:** MCI's security red lines, section 2-3 rows 2, 7 and 8, and section 1-3 row 4.

## Context

Accounts are local (ADR-006). Until now a forgotten password meant finding an administrator, because
nothing could reach a person: `email` and `phone` were free text, "not used for anything else". Two things
make recovery dangerous if it is done casually. Whoever can edit a profile could point recovery at their own
phone. And an endpoint that answers differently for accounts that exist tells an attacker which ones to
spray, which is why the login endpoint already burns the same time for a missing user.

The deployment has no mail server and no SMS gateway connected yet, and may never have one on the
department's closed network. Whatever is built has to be complete with them and correct without them.

## Decision 1: a code, not a link

Six digits, random, ten minutes, void after five failures, one use, hashed at rest. Those are the terms of red
line 2-3 row 2 for SMS, and they apply to e-mail too so that there is one rule to test
(`Sqm.Infrastructure/Identity/OneTimeCodes.cs`). A link would put a credential in a URL, against row 7, and into
every proxy log and browser history on the way. A code also works the same on both channels, so there is one
page and not two.

The hash is Argon2id from the password hasher. Six digits are a small space, so this only slows a stolen table
down, but it is the same hasher that makes the path with no account cost the same as the path with one.

The attempt is counted in the statement that refuses a sixth, before the hash is compared. Counting afterwards
would let guesses sent at the same moment all see room for one more.

## Decision 2: only a proved contact

`auth.user_account` gains `email_verified_at` and `phone_verified_at`. A code goes only to a proved contact, and
only a proved contact identifies an account. Proving one takes the password and then a code sent to it, so a
session left open on a desk cannot aim recovery at another phone. The proof belongs to the value: whoever
changes the contact, its owner or an administrator, clears it, and a code sent to the old value proves nothing
about the new one. A proved contact belongs to one account, by a partial unique index.

Numbers arrive as `0912 345 6789`, `+98 912…`, `0098…` or in Persian digits from a phone's keyboard, so a proved
number is stored as `+989123456789` and found again from any of them.

## Decision 3: nothing observable depends on whether an account exists

`POST recovery/start` looks at no account. It places the request on a bounded queue in memory and answers `202`
with no body, and a background service does the lookup, the hash and the send where nobody can time them. A
queue in the database would survive a restart, but it would turn every anonymous request into a write, and a
person whose request was lost simply asks again.

`POST recovery/complete` does exactly one hash on every failing path: no account, no code, wrong code, used,
expired, void. `Every_failure_costs_exactly_one_hash` counts them. It gives one answer for all of them.

Three limits stand behind it: six requests a minute per address, one code a minute per account, five an hour
per account. The address limit stops one machine; the account limits stop many machines filling one person's
phone, and they are what bounds an SMS bill.

## Decision 4: a reset signs nobody in

A reset sets the password, lifts any lockout and forced change, ends every session and every half-finished
sign-in, and tells the owner on every proved contact. The person then signs in with the new password, and the
second step applies as it always does. Signing in at the end of recovery would make recovery a way around it.

## Decision 5: the second step is an authenticator app

RFC 6238: SHA-1, six digits, thirty seconds, one step of tolerance each way, because some apps silently ignore
other parameters and then show a code that never works. Ten recovery codes cover a lost phone.

**A code by SMS or e-mail is deliberately not offered as a second step.** The phone and the mailbox that would
receive it are the ones that can already reset the password, so whoever holds the phone would hold both factors
and there would be one factor wearing two hats. An authenticator also needs no gateway, on a network that has none.

Between the two steps there is no session, only a ticket (`auth.sign_in_challenge`): five minutes, five
attempts, stored as a hash. A good password records no sign-in and clears no failure count. Each wrong code
counts towards the same lockout as a wrong password, so a password alone can never clear what guessing the code
built up. A code cannot be used twice, even within its thirty seconds: `last_step` is advanced in one statement.

It is optional per person. A role can demand it (`auth.role.two_step_required`, the fourth step ADR-006 set out),
which holds a member back the way a forced password change does, through the same few endpoints. Off on every
role today.

Turning it off takes both the password and a code. An administrator with `user.manage` can reset it for someone
who lost their phone, but not for themselves, because that would be the way around the code.

## Decision 6: the secret is encrypted, under a key the database does not hold

An authenticator secret cannot be hashed: the server has to read it back. It is AES-256-GCM under
`Auth:TwoStep:SecretKey`, authenticated with the user id so that a row copied onto another account does not open.
A stolen backup does not carry the key.

The alternative was ASP.NET's Data Protection, which wants a key ring on a shared writable volume. The containers
have a read-only root and may run as several replicas (ADR-007), and one value in the environment needs neither.

No key means the feature is off: nothing is offered, and a role that demands it turns nobody away, because
nobody could comply. A key that is present and unusable stops the API at start, rather than failing on the first
person to try. Losing the key leaves every authenticator unreadable; recovery codes and the reset still work.

## Decision 7: remember me is a longer session, not a second mechanism

The box asks for `Auth:Session:RememberedLifetime`, seven days, and the session row says `remembered`. Listing,
revoking and every check are unchanged. The cookie then carries a `Max-Age` of exactly that, and so does the CSRF
cookie: a session cookie that outlives its CSRF cookie refuses the next sign-in, which has happened here
(`CookieNames.ClearSession`). One number, not an idle and an absolute limit, because a person who ticks the box is
told how long it lasts. It does not skip the second step on the next full sign-in.

## Decision 8: what leaves the building is configuration

`IContactDelivery` is the seam. E-mail is SMTP through MailKit, which Microsoft's documentation names in place of
`System.Net.Mail.SmtpClient`. SMS is one HTTP request described entirely by configuration, because gateways have no
common protocol: URL, method, headers and a body with `{to}`, `{toDigits}`, `{toNational}` and `{text}`, each
escaped for where it lands. A gateway speaking SMPP needs its own implementation of that interface, and nothing
above it changes.

A channel with nothing configured is off, `GET auth/options` says so, and the sign-in page keeps "ask your
administrator". A failure records what came back and never the text, which holds the code, or the address, which
may hold a key. Writing messages to the log exists for development and is refused outside it.

## Rejected

| Option | Why not |
|---|---|
| A reset link by e-mail | A credential in a URL (red line 2-3 row 7) and a second flow |
| A code to the phone as the second step | The same phone resets the password: one factor, counted twice |
| Masked hints ("a code was sent to j***@…") | Says the account exists |
| A queue in the database | A write per anonymous request, to protect something worth nothing after a restart |
| Signing in at the end of recovery | A way around the second step |
| ASP.NET Data Protection for the secret | Wants a shared writable key ring; the containers have neither |
| Trusting a device for thirty days after the second step | Another credential to store and revoke, for a step taken once a week |
| A captcha now | Red line 2-3 row 3 wants one in Persian, against English-only: decision 2 of `docs/operations/deployment/04-security-red-lines.md`, still open. The lockout and the limiters stand in the meantime |

## Consequences

- A deployment that sets nothing behaves as before, with a better sign-in page and a way to keep a session.
- What is needed to turn each part on is in `docs/operations/running-the-system.md` and `deploy/compose/.env.example`.
- The login endpoint can now answer `202`. A client that treats every 2xx as signed in is wrong, and the SPA does not.
- `AuthenticatedUser` gains a flag read on every request, by two indexed lookups in the statement that already
  resolves the session.

## How it was proved

See `backend/tests/Sqm.Integration.Tests/AccountRecoveryTests.cs`, `TwoStepTests.cs`, `AccountSecurityEndpointTests.cs`
and `AccountDeliveryTests.cs`, and `Sqm.Application.Tests/TwoStepAlgorithmTests.cs`, which holds the codes to the
vectors of RFC 6238 and RFC 4648. Those that need a database run against PostgreSQL, connected as `sqm_app`,
because the terms of a code are SQL. Each bug listed below was put back on purpose and the tests failed:

| Put back on purpose | Test that failed |
|---|---|
| An unproved contact receives a code | `A_contact_that_was_never_proved_receives_nothing` |
| The attempt limit left out of the statement that counts | `Guesses_sent_at_the_same_moment_cannot_exceed_five` |
| No hash for an unknown account | `Every_failure_costs_exactly_one_hash` |
| Sessions survive a recovery | `The_right_code_changes_the_password_and_ends_every_session` |
| Changing a contact keeps its proof | `Changing_a_contact_takes_its_proof_away`, and the administrator's |
| A good password alone records a sign-in | `A_good_password_alone_is_not_a_sign_in` |
| A code can be replayed | `A_code_works_once_even_inside_its_own_thirty_seconds` |
| A wrong code does not count towards the lockout | `Wrong_codes_count_towards_the_lockout_as_wrong_passwords_do` and five more |
| A ticket survives a new password | `A_ticket_means_nothing_after_the_password_changes` |
| The CSRF cookie is shorter than a remembered session | `With_remember_me_both_cookies_last_as_long_as_the_session` |
| The answer depends on the identifier | `Asking_for_a_code_answers_alike_whoever_is_named` |
| A role demands it with no key configured | `Without_a_key_a_role_that_demands_it_turns_nobody_away` |
