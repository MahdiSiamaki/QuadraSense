-- 023_account_recovery_and_two_step.sql
--
-- A person can get back into their own account, and an account can ask for more than a password.
-- ADR-022 holds the reasoning; this file holds the shapes.
--
-- Every table here is in the auth schema, so the default privileges of
-- db/operational/grants/003_least_privilege.sql already give sqm_app what it needs.

-- ===========================================================================
-- CONTACTS THAT HAVE BEEN PROVED
--
-- email and phone have been free text since migration 003, and "not used for anything else" was
-- true of them. A recovery code may only go to an address its owner has proved they control, or
-- whoever can edit a profile can take the account. So each contact gains the moment it was
-- proved, and that moment is cleared whenever the contact changes.
--
-- A proved contact identifies one account. The indexes are partial because two people may share
-- an unproved phone number on their profiles, as they could before this migration.
-- ===========================================================================
ALTER TABLE auth.user_account
    ADD COLUMN email_verified_at timestamptz,
    ADD COLUMN phone_verified_at timestamptz;

CREATE UNIQUE INDEX user_account_verified_email_uq
    ON auth.user_account (lower(email)) WHERE email_verified_at IS NOT NULL;

CREATE UNIQUE INDEX user_account_verified_phone_uq
    ON auth.user_account (phone) WHERE phone_verified_at IS NOT NULL;

COMMENT ON COLUMN auth.user_account.email_verified_at IS
    'When the owner proved they control this address. NULL means no recovery code may go to it.';
COMMENT ON COLUMN auth.user_account.phone_verified_at IS
    'When the owner proved they control this number, stored in international form from then on.';

-- ===========================================================================
-- ONE-TIME CODES
--
-- Six digits, ten minutes, void after five failures, whichever channel carries them: the terms
-- MCI's security red lines set for SMS (section 2-3, row 2), used for e-mail too so that there is
-- one rule to test. A code and not a link, so nothing sensitive travels in a URL (row 7).
--
-- code_hash is an Argon2id string from the same hasher as passwords. Six digits are a small space,
-- but this keeps a leaked table from being read straight off, and the same hasher is what makes
-- the unknown-account path take the same time as the real one.
--
-- destination is what the code was sent to. A code that proves a contact proves that value, so
-- changing the contact afterwards leaves the code proving nothing.
-- ===========================================================================
CREATE TABLE auth.one_time_code (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id      bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,
    purpose      text        NOT NULL CHECK (purpose IN ('password_reset', 'verify_contact')),
    channel      text        NOT NULL CHECK (channel IN ('email', 'sms')),
    destination  text        NOT NULL,
    code_hash    text        NOT NULL,
    attempts     smallint    NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    consumed_at  timestamptz
);

-- The one live code, found by who and what for.
CREATE INDEX one_time_code_live
    ON auth.one_time_code (user_id, purpose) WHERE consumed_at IS NULL;

-- How many were sent in the last hour, and when the last one went.
CREATE INDEX one_time_code_recent ON auth.one_time_code (user_id, created_at DESC);

COMMENT ON TABLE auth.one_time_code IS
    'Codes sent to a proved contact. Hashed, single use, void after five failures, ten minutes.';

-- ===========================================================================
-- TWO-STEP SIGN-IN
--
-- The four steps ADR-006 set out for adding it. An authenticator app, because it needs no gateway
-- in a closed network, and because a code sent to the phone or mailbox that can also reset the
-- password would not be a second factor at all (ADR-022).
--
-- secret cannot be hashed: the server has to read it back to compute the expected code. It is
-- AES-GCM ciphertext under a key held in configuration, never in this database, and bound to the
-- user id so that a row copied to another account does not decrypt.
--
-- confirmed_at is NULL while the person is setting it up. Until they have typed one good code the
-- row changes nothing about how they sign in, so an abandoned setup cannot lock anyone out.
--
-- last_step is the replay guard: the time step of the last code accepted. A code seen over a
-- shoulder works for no more than the moment it was typed in.
-- ===========================================================================
CREATE TABLE auth.user_two_step (
    user_id       bigint PRIMARY KEY REFERENCES auth.user_account (id) ON DELETE CASCADE,
    secret        bytea       NOT NULL,
    confirmed_at  timestamptz,
    last_step     bigint      NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE auth.user_two_step IS
    'One authenticator per account. Secret encrypted, key outside the database. See ADR-022.';

-- Single-use codes for the day the authenticator is lost. SHA-256 rather than Argon2id because
-- they carry 80 random bits, the same reasoning as a session token: nothing can search that space.
CREATE TABLE auth.user_recovery_code (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id      bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,
    code_sha256  bytea       NOT NULL,
    used_at      timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (user_id, code_sha256)
);

-- Between a good password and a good second step. No session exists yet; this says who proved the
-- first step, for how long, and how many guesses the second step has had. Stored as a hash for
-- the same reason a session token is.
CREATE TABLE auth.sign_in_challenge (
    token_sha256  bytea PRIMARY KEY,
    user_id       bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,
    remember      boolean     NOT NULL,
    attempts      smallint    NOT NULL DEFAULT 0,
    expires_at    timestamptz NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX sign_in_challenge_expires ON auth.sign_in_challenge (expires_at);

-- A role can demand it, so it can be asked of administrators only. Off everywhere: nobody is
-- turned away from their account by this migration.
ALTER TABLE auth.role
    ADD COLUMN two_step_required boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN auth.role.two_step_required IS
    'Members must set up two-step sign-in before they can use anything else.';

-- ===========================================================================
-- REMEMBERED SESSIONS
--
-- A session started with "remember me" gets a persistent cookie and a longer limit. It stays one
-- row in the same table, so listing it, revoking it and every check on it are unchanged.
-- ===========================================================================
ALTER TABLE auth.session
    ADD COLUMN remembered boolean NOT NULL DEFAULT false;
