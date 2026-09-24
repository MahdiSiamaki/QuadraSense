# Working agreements

`README.md` explains what this system is and how to run it. This file is the part that is not
in the code: how work on it is expected to be done, and what is already known to be true so that
it is not re-derived or quietly contradicted.

## Rule Zero — ask

Where information is missing, ambiguous, or where an architectural decision would be better for
the product owner's answer, **ask**. A plausible assumption that turns out wrong costs more here
than a question does. This rule outranks the impulse to appear decisive.

## Prove it, never assert it

**"This is faster" is not a finding. A measurement is.** Every performance claim in this
repository is backed by a real run against the real dataset — `tools/benchmark/RESULTS.md`,
`docs/discovery/01-data-profiling-report.md`, the query plans quoted in the ADRs. The
mart layer exists because a dashboard query measured 3.5 s against a 500 ms budget, not because
marts are a good idea.

The same applies to bugs. Before claiming a fix works, reintroduce the bug and watch the test
fail; otherwise the test may be passing for an unrelated reason. That is not hypothetical — the
TAC scanner's 13 unit tests all passed against a version that silently dropped 62% of the file.

Choose technology from measured data, never from popularity. Record the decision as an ADR in
`docs/adr/` with the evidence in it.

## Simplest thing that meets the current need

Not the most extensible, not the most impressive. No infrastructure that today's requirement does
not force. When a simpler option was rejected, the ADR says why.

## Honesty about state

Report what happened, not what was hoped for. If tests fail, show the output. If a step was
skipped, say so. If an earlier statement in the session was wrong, correct it plainly and move on.
Several conclusions in `docs/` are marked SUPERSEDED for exactly this reason, and they are kept
rather than deleted.

## Product decisions already made

Do not re-open these without asking:

- **Masking is OFF.** `PRIVACY_MASK_IDENTIFIERS=false`, by the product owner's decision. MSISDN,
  IMSI and IMEI are returned in full. The flag exists so it can be flipped without a code change.
- **Retention is full history, no deletion.** Uploaded originals are kept indefinitely (~144 GB/
  year) because every table is rebuildable from them plus the pipeline.
- **English only, LTR, Gregorian dates.** No Persian in the product UI, no Jalali calendar.
- **Device images are proposed, never auto-applied.** Automatic sourcing once put a photograph of
  an office building into the catalogue. Nothing reaches the live image table without a person
  approving it — see `docs/adr/ADR-011-device-image-sourcing.md`.

## UI

Professional, modern, enterprise-grade, responsive and accessible. Design tokens live in
`frontend/src/design-system/`; use them rather than literal colours.

Two traps that have already cost time, both still live:

- **zrender cannot parse `oklch()`.** Passing a token straight to ECharts yields `undefined` and
  the series disappears on hover. Resolve through `frontend/src/lib/chart-colors.ts`.
- **Tailwind Preflight resets `margin: 0` on `<dialog>`**, which breaks the browser's own
  centring. `frontend/src/design-system/Modal.vue` restores it with `m-auto`.

## Counting rules

The grain is a **binding** — `(msisdn, imsi, imei)`. A number, a SIM and a handset.

Never write "users" or "devices" without saying which unit. Samsung is 52,704,438 bindings,
40,070,800 subscribers or 39,320,516 handsets, and all three are right answers to different
questions. Distinct counts in the marts use `uniq()` (HyperLogLog, ~0.5% error) and the UI labels
them *estimated* for that reason.

Dual-SIM handsets carry **two IMEIs**, one per radio — 76.9% of bindings. Two pairing schemes are
confirmed: Xiaomi/Redmi allocate the second IMEI as serial +1 within the same TAC, Samsung uses a
twin TAC (+100) with an identical serial. A rule that handles only the first will silently miss
every Samsung, which is how the second was found.

## Git

Small, logical commits with messages that explain *why*. No generated files, no dataset files, no
credentials. `.gitignore` excludes `*.csv` deliberately: the dumps are gigabytes of real PII.

## Environment

The full stack is Docker (`infra/docker-compose.yml`): ClickHouse on `localhost:18123`,
PostgreSQL on `localhost:15432`. Uploaded originals live outside the repository, at the path in
`ImportStorage:RootPath`.

**Away from that machine — in a cloud session, in CI, on a fresh clone — none of it is present.**
That is expected and the test suite is built for it: `Sqm.Integration.Tests` probes for a database
and skips with a reason when there is none. Measured with the whole stack down, 2026-09-24:

```
Sqm.Domain.Tests         134 passed
Sqm.Ingestion.Tests       47 passed
Sqm.Integration.Tests      8 passed, 34 skipped   <- no database
Sqm.Application.Tests      1 passed
                         190 passed, 34 skipped, 0 failed
```

So a green `dotnet test` in a cloud session means 190 of 224. What it cannot do is prove anything
about real data. Do not report a database-backed claim as verified when the integration tests
skipped — say which ones skipped and why.

What does work anywhere, given the toolchain below: `dotnet build backend/Sqm.slnx`,
`dotnet test backend/Sqm.slnx`, `cd frontend && npm ci && npm run build` (`vue-tsc -b && vite
build`), and `pip install -r tools/requirements.txt && python tools/test_device_image_pipeline.py`
(40 checks, no network, no database). `TreatWarningsAsErrors` is on for the backend: a warning is
a failure.

## Cloud sessions

The cloud VM is Ubuntu 24.04 with Node 22 and Python preinstalled, and **no .NET SDK**. The
environment's setup script, `tools/cloud/setup.sh`, installs it.

**First thing in a cloud session, before building anything:**

```bash
dotnet --version
```

It must print exactly the `sdk.version` in `global.json`. If `dotnet` is missing, or prints
something else, stop and say so. The setup script logs why it failed, and the usual cause is that
`builds.dotnet.microsoft.com` is not in the environment's allowed domains.

**Do not "fix" a missing SDK by editing `global.json`.** It will look like the obvious repair, and
it is the one change that silently breaks the guarantee this file exists to keep. The analyzers
ship inside the SDK and `AnalysisLevel` is `latest-recommended`, so a different SDK feature band
is a different set of warnings, and with warnings as errors, a different verdict. Ubuntu's own
`dotnet-sdk-10.0` package is the 1xx band; the laptop runs 10.0.400. A build that is green in the
cloud and red on the laptop is exactly the drift the two places must not have.

Work on a branch and open a pull request. `main` changes only through a merged PR, from either
place, so the laptop and the cloud can never both be ahead.

**Every change made in a cloud session ends committed and pushed.** The product owner tests cloud
work on the laptop, against the real stack and the real data the cloud does not have, and the
only way work gets there is through the remote. So after each task that changes files: commit
(same rules as above: small, logical, the *why* in the message), push the session's branch, and
end the reply with the branch name and the commands to bring it onto the laptop:

```bash
git fetch origin <branch>
git switch <branch>        # first time: git switch -c <branch> --track origin/<branch>
git pull
```

A task that changes nothing (a build, a test run, a question) has nothing to push; say so rather
than making an empty commit. Work that exists only in the cloud container is lost when the
container is reclaimed.
