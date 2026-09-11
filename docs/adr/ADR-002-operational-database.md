# ADR-002 — Operational (OLTP) store: PostgreSQL 17

- **Status:** Proposed — confirm licensing question below before accepting
- **Date:** 2026-09-12

---

## Context

The system needs two stores with genuinely different jobs. This ADR covers the **operational** one; the
analytics store is ADR-003.

Operational data is small, relational, transactional, and correctness-critical:

| Data | Est. rows | Characteristics |
|---|---:|---|
| Users, roles, permissions | < 1,000 | needs FK integrity |
| Import batches and files | ~400/year | frequent small updates (job status, progress) |
| Quarantined rows | ~10K/year | referenced by batch |
| Audit log | ~100K/year | **append-only, must be tamper-evident** |
| Dashboard and widget definitions | < 10,000 | JSON payloads, relational ownership |
| TAC versions and vendor mapping | ~50K | versioned, edited through the UI |

Total footprint is well under 10 GB even after years of operation. Nothing here is a scale problem. What
matters is transactional integrity, foreign keys, row-level updates, and the ability to give the audit writer
an `INSERT`-only role.

This is emphatically **not** a job for the analytics engine. Column stores handle small frequent updates
poorly and generally lack enforced foreign keys — exactly the guarantees this data needs most.

## Options considered

### A. PostgreSQL 17 — **proposed**

- **Pros:** no licence cost; enforced FKs and real constraints; excellent JSONB for dashboard definitions;
  fine-grained role and column privileges (which is how the append-only audit log is enforced); trivial to
  run in Docker for local development, so `docker compose up` needs no licence handling; the most widely
  documented OLTP database in existence.
- **Cons:** not on the team's stated skill list. Mitigated by the fact that for a schema this small and
  ordinary, day-to-day work is standard SQL that transfers directly from SQL Server experience — this is not
  a deep-expertise dependency the way the analytics engine is.

### B. Microsoft SQL Server

- **Pros:** **the team's stated strength.** First-class .NET integration and tooling. If the organisation
  already owns licences, marginal cost is zero. Would keep the count of unfamiliar technologies down.
- **Cons:** licensing. Express edition is capped at **10 GB per database** — adequate for the operational
  store alone today, but an uncomfortable ceiling with no headroom, and it cannot be used for the analytics
  store at all. Standard/Enterprise is a real cost and complicates local development and CI, where every
  developer machine and build agent would need a licensed instance or a container with licence acceptance.
- **Verdict:** genuinely viable *if* licences already exist. This is the open question below.

### C. MongoDB

- **Pros:** on the team's list; flexible schema suits dashboard definitions.
- **Cons:** wrong tool. This data is relational and integrity-critical — users → roles → permissions, batches
  → files → quarantined rows. Giving up foreign keys and multi-table constraints to gain schema flexibility we
  do not need is a bad trade. Enforcing an append-only audit log is also markedly weaker.
- **Rejected.**

### D. Reuse the analytics engine for everything

- **Pros:** one system.
- **Cons:** column stores are poor at the small, frequent, transactional updates that job tracking requires,
  and generally offer no enforced referential integrity. Job status updates would be pathological.
- **Rejected.**

## Decision

**PostgreSQL 17** for the operational store, pending the licensing question below.

Schema notes:

- Audit log written by a dedicated role holding `INSERT` only — no `UPDATE`, no `DELETE`. This is what makes
  it evidence rather than a convention.
- Least-privilege roles per service: the API cannot `DROP`; the ingestion worker cannot read audit rows.
- Migrations version-controlled and applied automatically on deploy; no manual production schema change.

## Rationale

1. **Zero licence friction is worth a lot operationally.** Every developer laptop, every CI build agent, and
   every ephemeral test container needs a database. With PostgreSQL that is a `docker compose up` line. With
   SQL Server it is a licensing conversation repeated in several places.
2. **The skill gap is small here.** The operational schema is ordinary relational work. The team's SQL Server
   experience transfers almost completely. The genuine learning investment in this project belongs in the
   analytics engine (ADR-003), and concentrating it there is deliberate.
3. **It is the best fit for the tamper-evident audit requirement**, via straightforward role privileges.

## Open question — this decision is reversible and cheap to reverse

**Does the organisation already own Microsoft SQL Server licences for this deployment?**

If yes, SQL Server becomes a strong contender for the operational store: the team knows it, .NET integration
is excellent, and the marginal cost is nil. The operational schema is small and uses no PostgreSQL-specific
features beyond JSONB (which maps to SQL Server's native JSON support), so switching costs little — provided
the decision is made before Phase 2 completes.

**It would not change ADR-003.** SQL Server's 10 GB Express cap and per-core licensing make it unsuitable for
a 3B-row/year analytics store regardless of what is chosen here.

## Consequences

- **Positive:** no licence cost or friction; strong integrity guarantees; genuinely enforceable append-only
  audit; effortless local and CI setup.
- **Negative:** one more technology that is not on the team's current list. Judged low-risk given how
  conventional the schema is.
- **Neutral:** if SQL Server licences exist and the team prefers it, switching before Phase 2 completes is
  inexpensive. After that it becomes progressively more costly, so the answer is wanted early.
