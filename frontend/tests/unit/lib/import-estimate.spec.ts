import { describe, expect, it } from 'vitest'
import { estimateImport } from '@/lib/import-estimate'
import type { ImportProgress, StageDuration } from '@/api/imports'

// The department VM's medians, 2026-10-26.
const typicals: StageDuration[] = [
  { stage: 'VALIDATING', typicalMs: 1_922, jobs: 10 },
  { stage: 'DEDUPLICATING', typicalMs: 299, jobs: 10 },
  { stage: 'IMPORTING', typicalMs: 6_107, jobs: 10 },
  { stage: 'AGGREGATING', typicalMs: 1_659_094, jobs: 10 },
  { stage: 'FINALIZING', typicalMs: 1, jobs: 10 },
]

const start = Date.parse('2026-10-26T10:00:00Z')
const at = (minutes: number) => start + minutes * 60_000
const running = (stage: string) => ({ currentStage: stage, stageStartedAt: new Date(start).toISOString() })

function bytes(stage: string, percent: number): ImportProgress {
  return {
    stage,
    bytesProcessed: 0,
    bytesExpected: 100,
    percent,
    updatedAt: '',
    step: null,
    stepIndex: null,
    stepCount: null,
    stepDone: null,
    stepTotal: null,
    stepUnit: null,
  }
}

describe('the time an import has left', () => {
  it('is the usual time of the stage minus what has passed, plus the stages after it', () => {
    const estimate = estimateImport(running('AGGREGATING'), null, typicals, at(10))!

    expect(estimate.msLeft).toBe(1_659_094 - 600_000 + 1)
    expect(estimate.stages.map((s) => s.state)).toEqual(['done', 'done', 'done', 'current', 'upcoming'])
    expect(estimate.overdue).toBe(false)
    expect(estimate.basedOn).toBe(10)
  })

  it("follows the stage's own rate where it counts bytes", () => {
    // A minute in and a quarter of the file read: three more minutes, whatever the median says.
    const estimate = estimateImport(running('IMPORTING'), bytes('IMPORTING', 25), typicals, at(1))!

    expect(estimate.msLeft).toBe(180_000 + 1_659_094 + 1)
  })

  it('ignores a rate measured on too little, and one left over from another stage', () => {
    const early = estimateImport(running('IMPORTING'), bytes('IMPORTING', 1), typicals, at(0.05))!
    expect(early.msLeft).toBe(6_107 - 3_000 + 1_659_094 + 1)

    const stale = estimateImport(running('AGGREGATING'), bytes('IMPORTING', 50), typicals, at(1))!
    expect(stale.msLeft).toBe(1_659_094 - 60_000 + 1)
  })

  it('says a stage is taking longer than usual rather than promising a number', () => {
    const estimate = estimateImport(running('AGGREGATING'), null, typicals, at(40))!

    expect(estimate.overdue).toBe(true)
    expect(estimate.msLeft).toBe(1)
  })

  it('gives a waiting import the whole usual time, from when it starts', () => {
    const estimate = estimateImport({ currentStage: null, stageStartedAt: null }, null, typicals, at(0))!

    expect(estimate.waiting).toBe(true)
    expect(estimate.msLeft).toBe(1_922 + 299 + 6_107 + 1_659_094 + 1)
    expect(estimate.stages.every((s) => s.state === 'upcoming')).toBe(true)
  })

  it('has nothing to say without a history, and shows a stage it has not seen without timing it', () => {
    expect(estimateImport(running('AGGREGATING'), null, [], at(1))).toBeNull()

    const unseen = estimateImport(running('ENRICHING'), null, typicals, at(1))!
    expect(unseen.msLeft).toBeNull()
    expect(unseen.stages).toEqual([{ stage: 'ENRICHING', state: 'current', typicalMs: null, elapsedMs: 60_000 }])
  })
})
