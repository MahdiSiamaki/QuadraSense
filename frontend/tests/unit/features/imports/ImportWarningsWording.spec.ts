import { describe, expect, it } from 'vitest'
import { mount, RouterLinkStub } from '@vue/test-utils'
import EventTimeline from '@/design-system/EventTimeline.vue'
import ImportHistoryTable from '@/features/imports/ImportHistoryTable.vue'
import type { ImportEvent, ImportJobSummary } from '@/api/imports'

/*
  "With warnings" is off the Import pages until data quality is settled (the product owner,
  2026-11-05): every row of such a day landed, and the words read as if the import had a problem.
  The attempt stays. warningCount and the job's quarantine are untouched.
*/
const pages = import.meta.glob('/src/features/imports/*.vue', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

function job(overrides: Partial<ImportJobSummary>): ImportJobSummary {
  return {
    jobId: 2,
    sourceCode: 'SQM',
    originalFileName: 'daily_subs_device_sim_info_2026-10-30.csv.gz',
    fileBytes: 200_000_000,
    status: 'Completed',
    businessDate: '2026-10-30',
    revision: 1,
    isEffective: true,
    attempt: 1,
    rowsInput: 7_369_456,
    rowsInserted: 7_369_456,
    rowsInvalid: 0,
    warningCount: 3_489,
    errorCount: 0,
    createdAt: '2026-11-03T07:59:59Z',
    startedAt: '2026-11-03T08:00:00Z',
    finishedAt: '2026-11-03T08:30:00Z',
    durationMs: 1_800_000,
    createdBy: 'operator',
    errorSummary: null,
    cancellable: false,
    cancelRequested: false,
    currentStage: null,
    stageStartedAt: null,
    ...overrides,
  }
}

const statusCell = (row: ImportJobSummary) =>
  mount(ImportHistoryTable, {
    props: { rows: [row] },
    global: { stubs: { RouterLink: RouterLinkStub } },
  })
    .findAll('tbody td')
    .at(2)!
    .text()

const event = (message: string): ImportEvent =>
  ({ occurredAt: '2026-11-03T08:30:00Z', severity: 'info', stage: 'COMPLETED', message, detailJson: null }) as ImportEvent

const timeline = (message: string) => mount(EventTimeline, { props: { events: [event(message)] } }).text()

describe('the Import pages without "with warnings"', () => {
  it('shows a completed day with warnings as plainly completed', () => {
    expect(statusCell(job({}))).toBe('Completed')
  })

  it('keeps the attempt', () => {
    expect(statusCell(job({ attempt: 2 }))).toContain('attempt 2')
  })

  it('says it nowhere in the pages themselves', () => {
    expect(Object.keys(pages).length).toBeGreaterThan(5)
    expect(Object.entries(pages).filter(([, source]) => source.includes('with warnings')).map(([file]) => file)).toEqual([])
  })

  it('leaves the count out of the worker\'s closing line, old lines included', () => {
    const text = timeline('Imported 7,369,456 rows for 2026-10-30 as day 267; 3,489 imported with warnings.')
    expect(text).toContain('Imported 7,369,456 rows for 2026-10-30 as day 267.')
    expect(text).not.toContain('warnings')
  })

  it('keeps the rejected rows, which are not in the data', () => {
    const text = timeline('Imported 9 rows for 2026-10-30 as day 267; 12 rejected and not imported, 3,489 imported with warnings.')
    expect(text).toContain('Imported 9 rows for 2026-10-30 as day 267; 12 rejected and not imported.')
    expect(text).not.toContain('warnings')
  })

  it('leaves any other line as the worker wrote it', () => {
    const line = '8,237,820 events already exist for this day; removing them'
    expect(timeline(line)).toContain(line)
  })
})
