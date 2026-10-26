import type { ImportJobSummary, ImportProgress, StageDuration } from '@/api/imports'

/**
 * How long an import has left, from how long its stages usually take.
 *
 * Usually, because that is all there is to know: the median of each stage over the source's last
 * ten first attempts (GET /imports/stage-durations). On the department VM a daily file spends 2 s
 * validating, 6 s loading and about 28 minutes aggregating, so the estimate is mostly the
 * aggregating stage's median minus what has passed.
 *
 * Where the current stage counts bytes - validating, loading - its own rate is the better guide
 * and is used instead. A stage well past its usual time says so rather than promise a number it
 * has no basis for.
 */
export type StageState = 'done' | 'current' | 'upcoming'

export interface StagePlan {
  stage: string
  state: StageState
  /** How long it usually takes; null for a stage the history has not seen. */
  typicalMs: number | null
  /** For the current stage: how long it has run. */
  elapsedMs: number | null
}

export interface ImportEstimate {
  stages: StagePlan[]
  /** Until it finishes - or, for one still waiting, how long it takes once it starts. */
  msLeft: number | null
  /** Waiting in the queue: the time is from when it starts. */
  waiting: boolean
  /** The current stage has run well past its usual time; msLeft then covers only what follows. */
  overdue: boolean
  /** How many past imports the figures come from. */
  basedOn: number
}

/** Past this multiple of its usual time a stage is "taking longer than usual". */
const OVERDUE_FACTOR = 1.25

/** Below this a byte rate is noise: the first percent of a load includes opening the file. */
const MIN_PERCENT_FOR_RATE = 2

export function estimateImport(
  summary: Pick<ImportJobSummary, 'currentStage' | 'stageStartedAt'>,
  progress: ImportProgress | null | undefined,
  typicals: StageDuration[] | null | undefined,
  now: number,
): ImportEstimate | null {
  if (!typicals?.length) return null

  const basedOn = Math.max(...typicals.map((t) => t.jobs))
  const current = summary.currentStage

  if (!current) {
    return {
      stages: typicals.map((t) => ({ stage: t.stage, state: 'upcoming', typicalMs: t.typicalMs, elapsedMs: null })),
      msLeft: typicals.reduce((sum, t) => sum + t.typicalMs, 0),
      waiting: true,
      overdue: false,
      basedOn,
    }
  }

  const index = typicals.findIndex((t) => t.stage === current)
  const elapsedMs = summary.stageStartedAt
    ? Math.max(0, now - new Date(summary.stageStartedAt).getTime())
    : null

  if (index < 0) {
    // A stage the history has not seen: it can be shown, not timed.
    return {
      stages: [{ stage: current, state: 'current', typicalMs: null, elapsedMs }],
      msLeft: null,
      waiting: false,
      overdue: false,
      basedOn,
    }
  }

  const stages = typicals.map((t, i): StagePlan => ({
    stage: t.stage,
    state: i < index ? 'done' : i === index ? 'current' : 'upcoming',
    typicalMs: t.typicalMs,
    elapsedMs: i === index ? elapsedMs : null,
  }))

  const typical = typicals[index]!.typicalMs
  const after = typicals.slice(index + 1).reduce((sum, t) => sum + t.typicalMs, 0)

  // The stage's own rate, when it counts bytes and has counted enough of them.
  const percent = progress?.stage === current && progress.step === null ? progress.percent : null
  if (elapsedMs !== null && percent !== null && percent >= MIN_PERCENT_FOR_RATE && percent < 100) {
    const byRate = (elapsedMs * (100 - percent)) / percent
    return { stages, msLeft: byRate + after, waiting: false, overdue: false, basedOn }
  }

  const overdue = elapsedMs !== null && typical > 0 && elapsedMs > typical * OVERDUE_FACTOR
  const currentLeft = elapsedMs === null ? typical : Math.max(0, typical - elapsedMs)
  return {
    stages,
    msLeft: overdue ? after : currentLeft + after,
    waiting: false,
    overdue,
    basedOn,
  }
}

/** "AGGREGATING" -> "Aggregating", as the status badge says it. */
export function stageLabel(stage: string): string {
  return stage.charAt(0) + stage.slice(1).toLowerCase()
}
