import { computed, reactive } from 'vue'
import { uploadImportFile, type UploadResult } from '@/api/imports'
import { ApiError } from '@/api/client'
import { createRateMeter, timeLeftMs, type RateMeter } from '@/lib/transfer-rate'

/**
 * The files being uploaded, held outside any page.
 *
 * It lived in the drop zone until 2026-10-26, so it lived as long as the Import Center did:
 * open an import's details while a month of files was going up and the list vanished. The
 * request itself carried on - nothing aborted it - but nothing showed it any more, nothing
 * started the next file, and the only way to stop it was to close the tab. Here the queue
 * outlives the page, the header shows it from anywhere, and it can be cancelled.
 *
 * One queue per source, as before: a TAC file does not wait behind thirty daily ones.
 */
export type UploadStatus = 'waiting' | 'uploading' | 'queued' | 'duplicate' | 'failed' | 'cancelled'

export interface UploadItem {
  readonly id: number
  readonly sourceCode: string
  readonly file: File
  status: UploadStatus
  /** Bytes sent so far. */
  sent: number
  /** Bytes per second over the last few seconds; null until there is enough to say. */
  rate: number | null
  /**
   * The connection broke and the upload is trying again - which attempt - or 0 while it flows. It
   * continues from the last byte the server has (ADR-018), so nothing already sent is sent twice.
   */
  reconnecting: number
  message?: string
  jobId?: number
}

const items = reactive<UploadItem[]>([])
const controllers = new Map<number, AbortController>()
const meters = new Map<number, RateMeter>()
const draining = new Set<string>()
const finishedListeners = new Set<(sourceCode: string) => void>()
let nextId = 1
/** Bumped by a reset, so a drain loop from before it stops rather than sending new files. */
let generation = 0

const unfinished = (item: UploadItem) => item.status === 'waiting' || item.status === 'uploading'

export function enqueue(sourceCode: string, files: File[]): void {
  for (const file of files) {
    items.push({ id: nextId++, sourceCode, file, status: 'waiting', sent: 0, rate: null, reconnecting: 0 })
  }
  void drain(sourceCode)
}

/** Stops one file: aborts it mid-upload, or takes it out of the line before it starts. */
export function cancel(id: number): void {
  const item = items.find((i) => i.id === id)
  if (!item || !unfinished(item)) return

  if (item.status === 'waiting') {
    item.status = 'cancelled'
    item.message = 'Cancelled before it started'
    return
  }
  // The upload's promise rejects with an AbortError and drain() records the outcome; the server
  // deletes the part it had written when the request breaks off.
  controllers.get(id)?.abort()
}

/** Stops every file of a source still waiting or uploading. */
export function cancelAll(sourceCode: string): void {
  // Waiting ones first, so the abort of the current one does not let drain() start the next.
  for (const item of items) {
    if (item.sourceCode === sourceCode && item.status === 'waiting') cancel(item.id)
  }
  for (const item of items) {
    if (item.sourceCode === sourceCode && item.status === 'uploading') cancel(item.id)
  }
}

/** Removes the source's finished entries from the list. */
export function clearFinished(sourceCode: string): void {
  for (let i = items.length - 1; i >= 0; i--) {
    const item = items[i]!
    if (item.sourceCode === sourceCode && !unfinished(item)) items.splice(i, 1)
  }
}

/** Called whenever a file of a source reaches an outcome - to refresh what lists the imports. */
export function onUploadFinished(listener: (sourceCode: string) => void): () => void {
  finishedListeners.add(listener)
  return () => finishedListeners.delete(listener)
}

async function drain(sourceCode: string): Promise<void> {
  if (draining.has(sourceCode)) return
  draining.add(sourceCode)
  const started = generation

  try {
    let item: UploadItem | undefined
    while ((item = items.find((i) => i.sourceCode === sourceCode && i.status === 'waiting'))) {
      await send(item)
      if (generation !== started) return
      for (const listener of finishedListeners) listener(sourceCode)
    }
  } finally {
    if (generation === started) draining.delete(sourceCode)
  }
}

async function send(item: UploadItem): Promise<void> {
  const controller = new AbortController()
  const meter = createRateMeter()
  controllers.set(item.id, controller)
  meters.set(item.id, meter)
  item.status = 'uploading'
  // No sample of 0 at the start: a resumed upload's first report already counts what the server
  // had, and measured from 0 those bytes read as sent in the first seconds - 2.6 MB/s over a VPN
  // doing 350 KB/s, on the department VM, 2026-10-27.

  try {
    const result = await uploadImportFile(
      item.sourceCode,
      item.file,
      (fraction) => {
        item.sent = Math.round(item.file.size * fraction)
        item.reconnecting = 0
        meter.sample(item.sent)
        item.rate = meter.rate()
      },
      controller.signal,
      (attempt) => (item.reconnecting = attempt),
    )
    apply(item, result)
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      item.status = 'cancelled'
      item.message = 'Cancelled - nothing was kept'
    } else {
      item.status = 'failed'
      item.message =
        error instanceof ApiError
          ? (error.problem?.detail ?? error.message)
          : 'The upload did not complete.'
    }
  } finally {
    controllers.delete(item.id)
    meters.delete(item.id)
    item.rate = null
    item.reconnecting = 0
  }
}

function apply(item: UploadItem, result: UploadResult): void {
  item.sent = item.file.size
  if (result.kind === 'queued') {
    item.status = 'queued'
    item.jobId = result.jobId
    item.message = `Queued as import ${result.jobId}`
    return
  }

  item.status = 'duplicate'
  item.message = result.existingJobId
    ? `Already imported — see import ${result.existingJobId}`
    : 'This exact file has already been uploaded'
}

/** What is left to send, and roughly how long it takes at the speed of the file now uploading. */
export interface UploadTotals {
  files: number
  bytesLeft: number
  rate: number | null
  msLeft: number | null
  /** Of everything waiting or uploading: 0-100. */
  percent: number
}

function totalsOf(list: UploadItem[]): UploadTotals | null {
  const open = list.filter(unfinished)
  if (!open.length) return null

  const size = open.reduce((sum, i) => sum + i.file.size, 0)
  const sent = open.reduce((sum, i) => sum + i.sent, 0)
  const rate = open.find((i) => i.status === 'uploading')?.rate ?? null
  return {
    files: open.length,
    bytesLeft: size - sent,
    rate,
    msLeft: timeLeftMs(size - sent, rate),
    percent: size ? (sent / size) * 100 : 0,
  }
}

export function useUploadQueue(sourceCode?: string) {
  const list = computed(() =>
    sourceCode ? items.filter((i) => i.sourceCode === sourceCode) : [...items],
  )
  return {
    items: list,
    totals: computed(() => totalsOf(list.value)),
    active: computed(() => list.value.some(unfinished)),
  }
}

// Closing or reloading the tab ends every upload in it. The browser's own prompt is the only
// warning a page can give at that moment, and it is given only while something would be lost.
if (typeof window !== 'undefined') {
  window.addEventListener('beforeunload', (event) => {
    if (items.some(unfinished)) event.preventDefault()
  })
}

/** For tests: forget every file. */
export function resetUploadQueue(): void {
  generation++
  for (const controller of controllers.values()) controller.abort()
  items.splice(0)
  controllers.clear()
  meters.clear()
  draining.clear()
  finishedListeners.clear()
}
