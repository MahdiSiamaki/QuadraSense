import { describe, expect, it, vi, beforeEach } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { ref } from 'vue'
import type { ImportJobDetail, ImportJobSummary, ImportStatus } from '@/api/imports'
import { ApiError } from '@/api/client'

/*
 * Cancel on an import's page, in each state the server can report: offered and asked about
 * first; refused past the point where stopping would leave work half-done, with the reason;
 * stopping once asked; and the server's refusal shown rather than swallowed.
 */
const detail = ref<ImportJobDetail | null>(null)
const cancel = { mutate: vi.fn(), isPending: ref(false), error: ref<unknown>(null) }
const reprocess = { mutate: vi.fn(), isPending: ref(false), error: ref<unknown>(null) }

vi.mock('vue-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => ({ params: { jobId: '42' } }),
  useRouter: () => ({ push: vi.fn() }),
}))

vi.mock('@/features/imports/useImportQueries', () => ({
  useImportDetail: () => ({
    data: detail,
    isPending: ref(false),
    isError: ref(false),
    error: ref(null),
    refetch: vi.fn(),
  }),
  useQuarantineSamples: () => ({ data: ref(null), isPending: ref(false), isError: ref(false) }),
  useImportActions: () => ({ cancel, reprocess }),
  useStageDurations: () => ({ data: ref([]) }),
}))

const { default: ImportDetailPage } = await import('@/features/imports/ImportDetailPage.vue')

function job(status: ImportStatus, extra: Partial<ImportJobSummary> = {}): ImportJobDetail {
  return {
    summary: {
      jobId: 42,
      sourceCode: 'SQM',
      originalFileName: 'daily_subs_device_sim_info_2026-02-10.csv',
      fileBytes: 380_000_000,
      status,
      businessDate: '2026-02-10',
      revision: 1,
      isEffective: false,
      attempt: 1,
      rowsInput: 0,
      rowsInserted: 0,
      rowsInvalid: 0,
      warningCount: 0,
      errorCount: 0,
      createdAt: '2026-10-26T10:00:00Z',
      startedAt: '2026-10-26T10:00:05Z',
      finishedAt: null,
      durationMs: null,
      createdBy: 'operator',
      errorSummary: null,
      cancellable: true,
      cancelRequested: false,
      currentStage: status.toUpperCase(),
      stageStartedAt: '2026-10-26T10:01:00Z',
      ...extra,
    },
    fileId: 1,
    sha256: 'sha',
    storedPath: 'SQM/x.csv',
    isBlobPresent: true,
    schemaVersion: 'v1',
    progress: null,
    events: [],
    quarantine: [],
    supersededByJobId: null,
    supersedesJobId: null,
    reprocessOfJobId: null,
  }
}

const page = async () => {
  const wrapper = mount(ImportDetailPage, {
    global: { stubs: { RouterLink: RouterLinkStub, FilePreview: true, EventTimeline: true } },
    attachTo: document.body,
  })
  await flushPromises()
  return wrapper
}
const buttons = (w: Awaited<ReturnType<typeof page>>) => w.findAll('button').map((b) => b.text())

beforeEach(() => {
  cancel.mutate.mockReset()
  cancel.error.value = null
  document.body.innerHTML = ''
})

describe("an import's Cancel", () => {
  it('asks first, says what stopping does, and cancels on confirmation', async () => {
    detail.value = job('Importing')
    const wrapper = await page()

    await wrapper.findAll('button').find((b) => b.text() === 'Cancel')!.trigger('click')
    expect(document.body.textContent).toContain('whatever it has loaded so far is removed')
    expect(cancel.mutate).not.toHaveBeenCalled()

    await wrapper.findAll('button').find((b) => b.text() === 'Cancel the import')!.trigger('click')
    expect(cancel.mutate).toHaveBeenCalledWith(42, expect.anything())
  })

  it('tells a waiting import it simply will not run', async () => {
    detail.value = job('Queued', { currentStage: null })
    const wrapper = await page()

    await wrapper.findAll('button').find((b) => b.text() === 'Cancel')!.trigger('click')
    expect(document.body.textContent).toContain('it simply will not run')
  })

  it('is not offered past the point of no return, and says why', async () => {
    detail.value = job('Aggregating', { cancellable: false })
    const wrapper = await page()

    expect(buttons(wrapper)).not.toContain('Cancel')
    expect(wrapper.text()).toContain('can no longer be cancelled')
  })

  it('shows a cancel already asked for as stopping', async () => {
    detail.value = job('Importing', { cancelRequested: true })
    const wrapper = await page()

    expect(buttons(wrapper)).not.toContain('Cancel')
    expect(buttons(wrapper).some((t) => t.includes('Stopping'))).toBe(true)
    expect(wrapper.text()).not.toContain('can no longer be cancelled')
  })

  it("shows the server's refusal instead of swallowing it", async () => {
    detail.value = job('Importing')
    cancel.error.value = new ApiError(409, { title: 'Too late to cancel', detail: 'Stopping now would leave them half-updated.' }, null)
    const wrapper = await page()

    expect(wrapper.text()).toContain('Stopping now would leave them half-updated.')
  })
})
