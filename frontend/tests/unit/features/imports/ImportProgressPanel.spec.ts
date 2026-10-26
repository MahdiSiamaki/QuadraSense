import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ImportProgressPanel from '@/features/imports/ImportProgressPanel.vue'
import type { ImportJobSummary, ImportProgress, StageDuration } from '@/api/imports'

/* What a running import's page says: the stages in order, the step now running, and the time left. */
const typicals: StageDuration[] = [
  { stage: 'VALIDATING', typicalMs: 2_000, jobs: 10 },
  { stage: 'IMPORTING', typicalMs: 6_000, jobs: 10 },
  { stage: 'AGGREGATING', typicalMs: 28 * 60_000, jobs: 10 },
]

const summary = (minutesIn: number) =>
  ({
    sourceCode: 'SQM',
    currentStage: 'AGGREGATING',
    stageStartedAt: new Date(Date.now() - minutesIn * 60_000).toISOString(),
  }) as ImportJobSummary

const step: ImportProgress = {
  stage: 'AGGREGATING',
  bytesProcessed: 0,
  bytesExpected: null,
  percent: 28.57,
  updatedAt: '',
  step: 'Updating the binding history',
  stepIndex: 3,
  stepCount: 7,
}

describe('the progress of a running import', () => {
  it('names the step aggregating is on, and the time left from past imports', () => {
    const text = mount(ImportProgressPanel, {
      props: { summary: summary(10), progress: step, typicals },
    }).text()

    expect(text).toContain('Step 3 of 7: Updating the binding history')
    expect(text).toContain('About 18 min left, estimated from the last 10 imports')
    expect(text).toContain('Validating')
  })

  it('says a stage is taking longer than usual instead of a time it cannot know', () => {
    const text = mount(ImportProgressPanel, {
      props: { summary: summary(40), progress: step, typicals },
    }).text()

    expect(text).toContain('Taking longer than usual: this stage usually takes 28m 0s')
    expect(text).not.toContain('min left')
  })

  it('says why there is no estimate when the source has no history yet', () => {
    const text = mount(ImportProgressPanel, {
      props: { summary: summary(1), progress: null, typicals: [] },
    }).text()

    expect(text).toContain("No time estimate yet: it needs this source's earlier imports to go by.")
  })
})
