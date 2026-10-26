import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'

/*
 * The upload queue, against a stand-in for the upload itself: each call is held open until the
 * test settles it, the way a real upload is open for minutes, so cancelling can happen mid-flight.
 */
interface Pending {
  sourceCode: string
  file: File
  onProgress: (fraction: number) => void
  signal: AbortSignal
  resolve: (jobId: number) => void
}

const pending: Pending[] = []

vi.mock('@/api/imports', () => ({
  uploadImportFile: (
    sourceCode: string,
    file: File,
    onProgress: (fraction: number) => void,
    signal: AbortSignal,
  ) =>
    new Promise((resolve, reject) => {
      signal.addEventListener('abort', () =>
        reject(new DOMException('Upload cancelled', 'AbortError')),
      )
      pending.push({
        sourceCode,
        file,
        onProgress,
        signal,
        resolve: (jobId) =>
          resolve({ kind: 'queued', jobId, fileName: file.name, sizeBytes: file.size, sha256: '' }),
      })
    }),
}))

const { enqueue, cancel, cancelAll, useUploadQueue, resetUploadQueue } = await import(
  '@/features/imports/uploadQueue'
)

const file = (name: string, bytes = 1_000) => new File([new Uint8Array(bytes)], name)

beforeEach(() => {
  pending.length = 0
  resetUploadQueue()
})
afterEach(() => resetUploadQueue())

describe('the upload queue', () => {
  it('sends one file at a time, in order', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv'), file('b.csv')])
    await flushPromises()

    expect(pending.map((p) => p.file.name)).toEqual(['a.csv'])
    expect(items.value.map((i) => i.status)).toEqual(['uploading', 'waiting'])

    pending[0]!.resolve(11)
    await flushPromises()
    expect(pending.map((p) => p.file.name)).toEqual(['a.csv', 'b.csv'])
    expect(items.value.map((i) => i.status)).toEqual(['queued', 'uploading'])
  })

  it('makes a file dropped during an upload wait its turn', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv')])
    await flushPromises()
    enqueue('SQM', [file('b.csv')])
    await flushPromises()

    expect(pending.map((p) => p.file.name)).toEqual(['a.csv'])
    expect(items.value.map((i) => i.status)).toEqual(['uploading', 'waiting'])
  })

  it('stops a file mid-upload, says nothing was kept, and goes on with the next', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv'), file('b.csv')])
    await flushPromises()

    pending[0]!.onProgress(0.4)
    cancel(items.value[0]!.id)
    await flushPromises()

    expect(pending[0]!.signal.aborted).toBe(true)
    expect(items.value[0]!.status).toBe('cancelled')
    expect(items.value[0]!.message).toBe('Cancelled - nothing was kept')
    expect(items.value[1]!.status).toBe('uploading')
  })

  it('takes a waiting file out of the line without sending any of it', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv'), file('b.csv')])
    await flushPromises()

    cancel(items.value[1]!.id)
    pending[0]!.resolve(11)
    await flushPromises()

    expect(pending.map((p) => p.file.name)).toEqual(['a.csv'])
    expect(items.value[1]!.status).toBe('cancelled')
  })

  it('cancels everything of one source and nothing of the other', async () => {
    const sqm = useUploadQueue('SQM')
    const tac = useUploadQueue('TAC')
    enqueue('SQM', [file('a.csv'), file('b.csv'), file('c.csv')])
    enqueue('TAC', [file('tac.csv')])
    await flushPromises()

    cancelAll('SQM')
    await flushPromises()

    expect(sqm.items.value.map((i) => i.status)).toEqual(['cancelled', 'cancelled', 'cancelled'])
    expect(pending.filter((p) => p.sourceCode === 'SQM')).toHaveLength(1)
    expect(tac.items.value[0]!.status).toBe('uploading')
  })

  it('totals what is left of every unfinished file', async () => {
    const { totals } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv', 1_000), file('b.csv', 3_000)])
    await flushPromises()

    pending[0]!.onProgress(0.5)
    expect(totals.value).toMatchObject({ files: 2, bytesLeft: 3_500, percent: 12.5 })

    cancelAll('SQM')
    await flushPromises()
    expect(totals.value).toBeNull()
  })

  it('outlives the page that started it', async () => {
    enqueue('SQM', [file('a.csv')])
    await flushPromises()

    // A component that mounts later - the Import Center opened again - sees the same upload.
    const later = useUploadQueue('SQM')
    expect(later.items.value.map((i) => [i.file.name, i.status])).toEqual([['a.csv', 'uploading']])
  })
})
