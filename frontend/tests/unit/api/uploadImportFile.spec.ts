import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'

/*
 * The resumable upload (ADR-018), against stand-ins for tus-js-client and the API client: what it
 * asks tus to do, what it does with what tus reports, and how a finished upload becomes an import.
 */
const state = vi.hoisted(() => ({
  uploads: [] as Array<{
    file: File
    options: Record<string, any>
    url: string | null
    started: boolean
    resumedFrom: unknown
    aborted: boolean | null
  }>,
  previous: [] as unknown[],
  post: null as unknown as ReturnType<typeof vi.fn>,
}))

vi.mock('tus-js-client', () => {
  class DetailedError extends Error {
    originalResponse: { getStatus: () => number } | null = null
  }
  class Upload {
    url: string | null = null
    started = false
    resumedFrom: unknown = null
    aborted: boolean | null = null
    constructor(
      public file: File,
      public options: Record<string, any>,
    ) {
      state.uploads.push(this)
    }
    findPreviousUploads() {
      return Promise.resolve(state.previous)
    }
    resumeFromPreviousUpload(previous: unknown) {
      this.resumedFrom = previous
    }
    start() {
      this.started = true
    }
    abort(terminate: boolean) {
      this.aborted = terminate
      return Promise.resolve()
    }
  }
  return { Upload, DetailedError }
})

vi.mock('@/api/client', async (importOriginal) => {
  state.post = vi.fn()
  return { ...(await importOriginal<typeof import('@/api/client')>()), api: { post: state.post } }
})

const { uploadImportFile } = await import('@/api/imports')
const { ApiError } = await import('@/api/client')
const { DetailedError } = await import('tus-js-client')

const file = () => new File(['abc'], 'daily_subs_device_sim_info_2026-02-10.csv')
const preflight = { source: 'SQM', fileName: 'x', sizeBytes: 3, message: '' }

/** The upload tus was asked to make, once it has started. */
async function started() {
  await flushPromises()
  const upload = state.uploads.at(-1)!
  expect(upload.started).toBe(true)
  return upload
}

beforeEach(() => {
  state.uploads.length = 0
  state.previous = []
  state.post.mockReset()
})

describe('a resumable upload', () => {
  it('sends nothing when it is cancelled while the preflight is answering', async () => {
    const controller = new AbortController()
    state.post.mockImplementationOnce(async () => {
      controller.abort()
      return preflight
    })

    await expect(uploadImportFile('SQM', file(), () => {}, controller.signal)).rejects.toMatchObject({
      name: 'AbortError',
    })
    expect(state.uploads).toHaveLength(0)
  })

  it('completes a finished upload, and the import is queued', async () => {
    state.post.mockResolvedValueOnce(preflight).mockResolvedValueOnce({
      jobId: 77,
      fileName: 'daily_subs_device_sim_info_2026-02-10.csv',
      sizeBytes: 3,
      sha256: 'abc',
    })

    const outcome = uploadImportFile('SQM', file(), () => {})
    const upload = await started()
    expect(upload.options['metadata']).toMatchObject({ source: 'SQM', filename: 'daily_subs_device_sim_info_2026-02-10.csv' })

    upload.url = 'https://vm/api/v1/imports/uploads/0123456789abcdef0123456789abcdef'
    upload.options['onSuccess']()

    await expect(outcome).resolves.toMatchObject({ kind: 'queued', jobId: 77 })
    expect(state.post).toHaveBeenLastCalledWith(
      '/api/v1/imports/uploads/0123456789abcdef0123456789abcdef/complete',
      {},
      undefined,
    )
  })

  it('reports a file already imported as a duplicate, not a failure', async () => {
    state.post
      .mockResolvedValueOnce(preflight)
      .mockRejectedValueOnce(
        new ApiError(409, { fileId: 41, existingJobId: 12, originalFileName: 'a.csv', sha256: 'abc', message: 'known' } as never, null),
      )

    const outcome = uploadImportFile('SQM', file(), () => {})
    const upload = await started()
    upload.url = '/api/v1/imports/uploads/0123456789abcdef0123456789abcdef'
    upload.options['onSuccess']()

    await expect(outcome).resolves.toMatchObject({ kind: 'duplicate', existingJobId: 12 })
  })

  it('terminates the upload on the server when it is cancelled mid-way', async () => {
    state.post.mockResolvedValueOnce(preflight)
    const controller = new AbortController()

    const outcome = uploadImportFile('SQM', file(), () => {}, controller.signal)
    const upload = await started()
    controller.abort()

    await expect(outcome).rejects.toMatchObject({ name: 'AbortError' })
    expect(upload.aborted).toBe(true)
  })

  it('tries again after a broken connection, and says so; not after a refusal', async () => {
    state.post.mockResolvedValueOnce(preflight)
    const retries: number[] = []

    void uploadImportFile('SQM', file(), () => {}, undefined, (attempt) => retries.push(attempt))
    const upload = await started()

    const dropped = new DetailedError('network')
    const refused = Object.assign(new DetailedError('forbidden'), { originalResponse: { getStatus: () => 403 } })

    expect(upload.options['onShouldRetry'](dropped, 0)).toBe(true)
    expect(upload.options['onShouldRetry'](refused, 1)).toBe(false)
    expect(retries).toEqual([1])
  })

  it('continues an upload of the same file that a reload interrupted', async () => {
    state.post.mockResolvedValueOnce(preflight)
    state.previous = [{ uploadUrl: '/api/v1/imports/uploads/0123456789abcdef0123456789abcdef' }]

    void uploadImportFile('SQM', file(), () => {})
    const upload = await started()

    expect(upload.resumedFrom).toEqual(state.previous[0])
  })
})
