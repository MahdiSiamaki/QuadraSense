import { describe, expect, it, vi, beforeEach } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { ref } from 'vue'
import type { UnqueuedFile, WorkerHealth } from '@/api/imports'
import { ApiError } from '@/api/client'

/*
 * The two waits the Import Center has to explain, because nothing else does. A queue held for a
 * missing disk looks like a broken worker; a stored file with no job looks like nothing at all.
 */
const health = ref<WorkerHealth | null>(null)
const unqueued = ref<UnqueuedFile[]>([])
const queueFile = { mutate: vi.fn(), isPending: ref(false), variables: ref<UnqueuedFile | undefined>(undefined) }

const idle = (data: unknown) => ({
  data: ref(data),
  isPending: ref(false),
  isFetching: ref(false),
  isError: ref(false),
  error: ref(null),
  refetch: vi.fn(),
})

vi.mock('@/features/imports/useImportQueries', () => ({
  useBlockages: () => idle([]),
  useFreshness: () => idle([]),
  useImportActions: () => ({ queueFile }),
  useImportHistory: () => idle({ items: [], total: 0, page: 1, pageSize: 25 }),
  useStageDurations: () => idle([]),
  useUnqueuedFiles: () => ({ data: unqueued }),
  useWorkerHealth: () => ({ data: health }),
}))

const { default: ImportCenterPage } = await import('@/features/imports/ImportCenterPage.vue')

const workers = (extra: Partial<WorkerHealth> = {}): WorkerHealth => ({
  queued: 0,
  running: 0,
  retrying: 0,
  failedLast24Hours: 0,
  oldestQueuedAt: null,
  activeWorkers: 1,
  workerHostedInApi: true,
  stalled: false,
  staleLeases: 0,
  storeReachable: true,
  ...extra,
})

const stranded: UnqueuedFile = {
  fileId: 3980,
  sourceCode: 'SQM',
  originalFileName: 'daily_subs_device_sim_info_2026-10-26.csv',
  sizeBytes: 366_000_000,
  uploadedBy: 'admin',
  uploadedAt: '2026-11-03T08:30:54Z',
}

const page = async () => {
  const wrapper = mount(ImportCenterPage, {
    global: {
      stubs: { RouterLink: RouterLinkStub, UploadDropzone: true, TacVersionPanel: true, ImportHistoryTable: true },
    },
  })
  await flushPromises()
  return wrapper
}

const alerts = (w: Awaited<ReturnType<typeof page>>) => w.findAll('[role="alert"]').map((a) => a.text())

beforeEach(() => {
  health.value = workers()
  unqueued.value = []
  queueFile.mutate.mockReset()
  queueFile.isPending.value = false
  queueFile.variables.value = undefined
})

describe('Import Center', () => {
  it('says nothing when the store is there and every file has a job', async () => {
    expect(alerts(await page())).toEqual([])
  })

  it('says a held queue is paused, not broken, and that it resumes by itself', async () => {
    health.value = workers({ storeReachable: false, queued: 7 })

    const [alert] = alerts(await page())

    expect(alert).toContain('Imports are paused')
    expect(alert).toContain('7 queued jobs will start by themselves')
  })

  it('counts a single held job in the singular', async () => {
    health.value = workers({ storeReachable: false, queued: 1 })

    expect(alerts(await page())[0]).toContain('1 queued job will start')
  })

  it('still explains a hold with nothing queued', async () => {
    health.value = workers({ storeReachable: false })

    expect(alerts(await page())[0]).toContain('imports start again by themselves')
  })

  it('lists a stored file with no job, and queues it from the stored copy', async () => {
    unqueued.value = [stranded]

    const wrapper = await page()

    expect(alerts(wrapper)[0]).toContain('1 uploaded file has no import')
    expect(alerts(wrapper)[0]).toContain('daily_subs_device_sim_info_2026-10-26.csv')

    await wrapper.find('[role="alert"] button').trigger('click')

    expect(queueFile.mutate.mock.calls[0]?.[0]).toEqual(stranded)
  })

  it('shows why a file could not be queued', async () => {
    unqueued.value = [stranded]
    queueFile.mutate.mockImplementation((_, options: { onError: (error: unknown) => void }) =>
      options.onError(new ApiError(409, { title: 'No', detail: 'It is not where it was recorded.' }, null)),
    )

    const wrapper = await page()
    await wrapper.find('[role="alert"] button').trigger('click')

    expect(alerts(wrapper)[0]).toContain('It is not where it was recorded.')
  })
})
