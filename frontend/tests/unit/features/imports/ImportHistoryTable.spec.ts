import { describe, expect, it } from 'vitest'
import { mount, RouterLinkStub } from '@vue/test-utils'
import ImportHistoryTable from '@/features/imports/ImportHistoryTable.vue'
import type { ImportJobSummary, ImportStatus } from '@/api/imports'

function row(status: ImportStatus, finishedAt: string | null): ImportJobSummary {
  return {
    jobId: 2,
    sourceCode: 'SQM',
    originalFileName: 'dump_subs_device_sim_info_20251227_20260125.csv.gz',
    fileBytes: 2_073_261_571,
    status,
    businessDate: null,
    revision: 1,
    isEffective: true,
    attempt: 1,
    rowsInput: 125_939_523,
    rowsInserted: 125_939_523,
    rowsInvalid: 0,
    warningCount: 0,
    errorCount: 0,
    createdAt: '2026-10-25T07:59:59Z',
    startedAt: null,
    finishedAt,
    durationMs: null,
    createdBy: 'operator (--load-dump)',
    errorSummary: null,
    cancellable: false,
    cancelRequested: false,
    currentStage: null,
    stageStartedAt: null,
  }
}

const finishedCell = (status: ImportStatus, finishedAt: string | null) =>
  mount(ImportHistoryTable, {
    props: { rows: [row(status, finishedAt)] },
    global: { stubs: { RouterLink: RouterLinkStub } },
  })
    .findAll('tbody td')
    .at(-1)!

/* The Finished column: a time when one was kept, never "Not finished" for a job that has finished. */
describe('the import history', () => {
  it('shows a finished job with no recorded time as unknown, not as unfinished', () => {
    // The dump and the files loaded before the platform carry no times; beside a Completed badge
    // they read "Not finished" until 2026-10-25.
    const cell = finishedCell('Completed', null)
    expect(cell.text()).toBe('—')
    expect(cell.attributes('title')).toBe('Finished; when was not recorded')
  })

  it('still says a queued job has not finished', () => {
    expect(finishedCell('Queued', null).text()).toBe('Not finished')
  })

  it('shows when a job finished, when it is known', () => {
    expect(finishedCell('Completed', '2026-10-25T08:00:52Z').text()).not.toBe('—')
  })

  it("gives a running job roughly how long it has left, from its source's usual stage times", () => {
    const running = {
      ...row('Aggregating', null),
      currentStage: 'AGGREGATING',
      stageStartedAt: new Date(Date.now() - 10 * 60_000).toISOString(),
    }
    const cell = mount(ImportHistoryTable, {
      props: {
        rows: [running],
        typicals: {
          SQM: [
            { stage: 'AGGREGATING', typicalMs: 28 * 60_000, jobs: 10 },
            { stage: 'FINALIZING', typicalMs: 0, jobs: 10 },
          ],
        },
      },
      global: { stubs: { RouterLink: RouterLinkStub } },
    })
      .findAll('tbody td')
      .at(-1)!

    expect(cell.text()).toBe('about 18 min left')
  })
})
