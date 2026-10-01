/**
 * The initial dump's window: everything the operator observed between these days, not a snapshot
 * at one moment (docs/discovery/03-dated-daily-files.md). A binding from it has no start date, so
 * "first seen" for one is this window - the product owner's decision, 2026-10-01.
 *
 * Mirrors Sqm.Domain.Timeline.InitialDump. It is a fact about the data, fixed for good, so a copy
 * here cannot drift the way a rule could; the timeline API also returns it with every response.
 */
export const INITIAL_DUMP = { start: '2025-12-27', end: '2026-01-25' } as const
