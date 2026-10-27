/**
 * Import Center API.
 *
 * Kept separate from the dashboard client because the two have different shapes: the dashboard
 * is all reads with cacheable responses, this one has uploads with progress and actions with
 * side effects.
 */

import type { DetailedError } from 'tus-js-client'
import { api, ApiError, csrfHeader, notifyUnauthenticated } from './client'

const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

/**
 * Where a job is. The strings match the API, which matches the database enum, so the same
 * vocabulary appears in the UI, the logs and the schema.
 */
export type ImportStatus =
  | 'Uploaded'
  | 'Queued'
  | 'Validating'
  | 'Parsing'
  | 'Normalizing'
  | 'Deduplicating'
  | 'Enriching'
  | 'Importing'
  | 'Aggregating'
  | 'Finalizing'
  | 'Completed'
  | 'PartiallyCompleted'
  | 'Duplicate'
  | 'Failed'
  | 'Quarantined'
  | 'Cancelled'
  | 'Retrying'

/** Statuses where a worker is actively holding the job. */
const RUNNING: ReadonlySet<ImportStatus> = new Set<ImportStatus>([
  'Validating',
  'Parsing',
  'Normalizing',
  'Deduplicating',
  'Enriching',
  'Importing',
  'Aggregating',
  'Finalizing',
])

/** Statuses where nothing further will happen without someone asking. */
const TERMINAL: ReadonlySet<ImportStatus> = new Set<ImportStatus>([
  'Completed',
  'PartiallyCompleted',
  'Duplicate',
  'Failed',
  'Quarantined',
  'Cancelled',
])

export const isRunning = (status: ImportStatus): boolean => RUNNING.has(status)
export const isTerminal = (status: ImportStatus): boolean => TERMINAL.has(status)

/** How a status should read and colour. */
export type StatusTone = 'success' | 'warning' | 'danger' | 'running' | 'neutral'

export function statusTone(status: ImportStatus): StatusTone {
  if (status === 'Completed') return 'success'
  if (status === 'PartiallyCompleted' || status === 'Duplicate' || status === 'Retrying') return 'warning'
  if (status === 'Failed' || status === 'Quarantined') return 'danger'
  if (RUNNING.has(status)) return 'running'
  return 'neutral'
}

/** Human label. The API's PascalCase is a wire format, not a thing to show people. */
export function statusLabel(status: ImportStatus): string {
  if (status === 'PartiallyCompleted') return 'Partially completed'
  return status
}

export interface ImportJobSummary {
  jobId: number
  sourceCode: string
  originalFileName: string
  fileBytes: number
  status: ImportStatus
  businessDate: string | null
  revision: number
  isEffective: boolean
  attempt: number
  rowsInput: number
  rowsInserted: number
  rowsInvalid: number
  warningCount: number
  errorCount: number
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  durationMs: number | null
  createdBy: string
  errorSummary: string | null
  /**
   * Whether a cancel would be taken now. False once finished, and false once a running import is
   * past the point where stopping would leave work half-done - folding a daily file into the
   * history and the dashboards.
   */
  cancellable: boolean
  /** A cancel was asked for; a running import is stopping. */
  cancelRequested: boolean
  /** The stage a running import is in, in the database's spelling ("AGGREGATING"). */
  currentStage: string | null
  stageStartedAt: string | null
}

export interface ImportEvent {
  occurredAt: string
  severity: 'info' | 'warning' | 'error'
  stage: string | null
  message: string
  detailJson: string | null
}

export interface QuarantineGroup {
  summaryId: number
  ruleCode: string
  columnName: string | null
  severity: string
  occurrenceCount: number
  firstRowNumber: number | null
  message: string
}

export interface QuarantineSample {
  rowNumber: number
  rawLine: string
  offendingValue: string | null
}

export interface ImportProgress {
  stage: string
  bytesProcessed: number
  bytesExpected: number | null
  percent: number | null
  updatedAt: string
  /** For a stage that reads no file (AGGREGATING): the step now running, and where it is in the list. */
  step: string | null
  stepIndex: number | null
  stepCount: number | null
  /** Within the running step, for one long enough to count: "12 of 31 statements". */
  stepDone: number | null
  stepTotal: number | null
  stepUnit: string | null
}

/** How long a stage usually takes for a source: the median over its recent first attempts. */
export interface StageDuration {
  /** The database's spelling, as `currentStage` has it: "AGGREGATING". */
  stage: string
  typicalMs: number
  /** How many imports the median is taken over. */
  jobs: number
}

export interface ImportJobDetail {
  summary: ImportJobSummary
  fileId: number
  sha256: string
  storedPath: string
  isBlobPresent: boolean
  schemaVersion: string | null
  progress: ImportProgress | null
  events: ImportEvent[]
  quarantine: QuarantineGroup[]
  supersededByJobId: number | null
  supersedesJobId: number | null
  reprocessOfJobId: number | null
}

export interface ImportPage {
  items: ImportJobSummary[]
  total: number
  page: number
  pageSize: number
}

export interface SourceFreshness {
  sourceCode: string
  latestBusinessDate: string | null
  latestImportedAt: string | null
  daysBehind: number | null
  missingBusinessDates: string[]
  failedLast7Days: number
  /**
   * Set while a run of queued files lands before the dashboard's figures are rebuilt once, at the
   * end of it. The data is current throughout; only the figures wait.
   */
  dashboardPendingSince: string | null
  /**
   * Each file is a whole dataset (the GSMA TAC database), not a day: `latestBusinessDate` is then
   * the newest imported dataset's own date, read from its file name, and no day can be missing.
   */
  isSnapshot: boolean
}

export interface WorkerHealth {
  queued: number
  running: number
  retrying: number
  failedLast24Hours: number
  oldestQueuedAt: string | null
  /** Workers that have polled recently, whether or not they are holding a job. */
  activeWorkers: number
  /** True when a live worker shares a process with the API, so restarting it stops the import. */
  workerHostedInApi: boolean
  /** Work is waiting and nothing is alive to take it. */
  stalled: boolean
  staleLeases: number
}

/** A day that did not land and holds the later days of its source back. */
export interface QueueBlockage {
  sourceCode: string
  businessDate: string
  /** The latest job for that day: the one to import again. */
  jobId: number
  status: 'FAILED' | 'CANCELLED'
  /** Later days of the same source queued behind it. */
  waiting: number
}

export interface ImportHistoryQuery {
  source?: string
  status?: string
  from?: string
  to?: string
  fileName?: string
  effectiveOnly?: boolean
  page?: number
  pageSize?: number
}

export const importsApi = {
  list: (query: ImportHistoryQuery, signal?: AbortSignal) =>
    api.get<ImportPage>('/api/v1/imports', { ...query }, signal),

  get: (jobId: number, signal?: AbortSignal) =>
    api.get<ImportJobDetail>(`/api/v1/imports/${jobId}`, undefined, signal),

  quarantineSamples: (jobId: number, summaryId: number, signal?: AbortSignal) =>
    api.get<QuarantineSample[]>(
      `/api/v1/imports/${jobId}/quarantine/${summaryId}`,
      undefined,
      signal,
    ),

  freshness: (signal?: AbortSignal) =>
    api.get<SourceFreshness[]>('/api/v1/imports/freshness', undefined, signal),

  workerHealth: (signal?: AbortSignal) =>
    api.get<WorkerHealth>('/api/v1/imports/worker-health', undefined, signal),

  blockages: (signal?: AbortSignal) =>
    api.get<QueueBlockage[]>('/api/v1/imports/blockages', undefined, signal),

  stageDurations: (source: string, signal?: AbortSignal) =>
    api.get<StageDuration[]>('/api/v1/imports/stage-durations', { source }, signal),

  cancel: (jobId: number) => api.post<void>(`/api/v1/imports/${jobId}/cancel`, {}),

  reprocess: (jobId: number) =>
    api.post<{ jobId: number; reprocessOfJobId: number }>(
      `/api/v1/imports/${jobId}/reprocess`,
      {},
    ),
}

/** What an upload reports back, either way. */
export type UploadResult =
  | { kind: 'queued'; jobId: number; fileName: string; sizeBytes: number; sha256: string }
  | {
      kind: 'duplicate'
      fileId: number
      existingJobId: number | null
      originalFileName: string
      sha256: string
      message: string
    }

/**
 * The server's answer to "would you take this file".
 *
 * A refusal arrives as a thrown `ApiError` rather than in this shape, so the caller's existing
 * error handling covers it unchanged - and now covers it before any bytes move.
 */
export interface UploadPreflight {
  source: string
  fileName: string
  sizeBytes: number
  /** What is still undecided: the duplicate check needs the content and happens on arrival. */
  message: string
}

/**
 * Waits before each new try after a connection breaks, in milliseconds: quickly at first, then a
 * minute apart for about forty minutes. Over the department VPN the tunnel drops every ~15 minutes
 * and can take a while to come back (docs/operations/deployment/02-department-vm.md).
 */
export const UPLOAD_RETRY_DELAYS = [0, 1_000, 3_000, 10_000, 30_000, ...Array<number>(40).fill(60_000)]

/** Whether a failed request is worth sending again: the network, the server, or a conflict tus resolves. */
function worthRetrying(error: DetailedError): boolean {
  const status = error.originalResponse?.getStatus() ?? 0
  return status === 0 || status >= 500 || status === 409 || status === 423
}

/**
 * Uploads one file, reporting progress as it goes, and survives a broken connection.
 *
 * Resumable, with the tus protocol (ADR-018): the file is created as an upload on the server, its
 * bytes sent, and when the connection breaks the browser asks how many arrived and sends the rest -
 * by itself, for about forty minutes (`UPLOAD_RETRY_DELAYS`). It was one request until 2026-10-27,
 * and over the department VPN two of two daily files broke partway and had to start from nothing.
 * The same file dropped again after the page was reloaded continues where it stopped: tus keeps
 * where each upload lives, in this browser, keyed by the file's name, size and date.
 *
 * Once every byte is in, the server is asked to complete it, which is where it becomes an import -
 * or turns out to be one already - exactly as a single upload did.
 *
 * Cancelling terminates the upload on the server, so nothing of it is kept.
 */
export async function uploadImportFile(
  sourceCode: string,
  file: File,
  onProgress: (fraction: number) => void,
  signal?: AbortSignal,
  onRetry?: (attempt: number) => void,
): Promise<UploadResult> {
  // Ask before sending. This is a ~200-byte JSON POST that gets the same answer the upload would
  // give for everything decidable without the body - is the session alive, is the source real,
  // does this caller hold the upload permission for it.
  //
  // It exists because of what a refusal used to cost. A 319.6 MB daily file was pushed across
  // the wire and refused in 0.4 ms: the server was right to refuse before reading a byte, but the
  // browser had no way to know that and sent the whole thing anyway.
  //
  // Not a security boundary. The upload re-checks everything; skipping this would waste an
  // upload, not gain one.
  await api.post<UploadPreflight>(
    `/api/v1/imports/${sourceCode}/upload/preflight`,
    { fileName: file.name, sizeBytes: file.size },
    signal,
  )

  // Loaded here, not with the page: it is 15.5 KB gzipped, and every page carried it once the
  // upload queue moved into the header (measured 2026-10-27: 30.3 -> 45.8 KB for the entry chunk).
  const tus = await import('tus-js-client')

  const uploadUrl = await new Promise<string>((resolve, reject) => {
    // A cancel that came while the preflight was answering has already fired its event, so the
    // listener added below would never hear it and the whole file would go anyway.
    if (signal?.aborted) {
      reject(new DOMException('Upload cancelled', 'AbortError'))
      return
    }

    const upload = new tus.Upload(file, {
      endpoint: `${BASE}/api/v1/imports/uploads`,
      metadata: { source: sourceCode, filename: file.name, filetype: file.type },
      retryDelays: UPLOAD_RETRY_DELAYS,
      removeFingerprintOnSuccess: true,
      onBeforeRequest: (request) => {
        // What `request()` in client.ts adds to every other call: the session cookie across
        // origins in development, and the CSRF header on everything that changes state.
        const xhr = request.getUnderlyingObject() as XMLHttpRequest
        xhr.withCredentials = true
        const csrf = csrfHeader()
        if (csrf) request.setHeader(csrf.name, csrf.value)
      },
      onShouldRetry: (error, attempt) => {
        const retry = worthRetrying(error)
        if (retry) onRetry?.(attempt + 1)
        return retry
      },
      onProgress: (sent, total) => onProgress(total ? sent / total : 0),
      onSuccess: () => resolve(upload.url ?? ''),
      onError: (error) => {
        const status =
          error instanceof tus.DetailedError ? (error.originalResponse?.getStatus() ?? 0) : 0
        // A dead session must reach the auth layer, as it does for every other request.
        if (status === 401) notifyUnauthenticated()
        const title =
          status === 0 ? 'The upload could not reach the server.' : 'The server refused the upload.'
        reject(new ApiError(status, { title }, null))
      },
    })

    signal?.addEventListener('abort', () => {
      void upload.abort(true).finally(() => reject(new DOMException('Upload cancelled', 'AbortError')))
    })

    void upload.findPreviousUploads().then((previous) => {
      const earlier = previous[0]
      if (earlier) upload.resumeFromPreviousUpload(earlier)
      upload.start()
    })
  })

  // Every byte is in: now it becomes an import, or turns out to be one already.
  const id = uploadUrl.slice(uploadUrl.lastIndexOf('/') + 1)
  try {
    const queued = await api.post<{ jobId: number; fileName: string; sizeBytes: number; sha256: string }>(
      `/api/v1/imports/uploads/${id}/complete`,
      {},
      signal,
    )
    return { kind: 'queued', ...queued }
  } catch (error) {
    // A duplicate is a normal outcome, not a failure: the same file sent twice, or a retry after
    // a dropped connection. It resolves with an explanation rather than rejecting.
    const body = error instanceof ApiError ? (error.problem as Record<string, unknown> | null) : null
    if (error instanceof ApiError && error.status === 409 && body && 'sha256' in body) {
      return {
        kind: 'duplicate',
        fileId: body['fileId'] as number,
        existingJobId: (body['existingJobId'] as number | null) ?? null,
        originalFileName: body['originalFileName'] as string,
        sha256: body['sha256'] as string,
        message: body['message'] as string,
      }
    }
    throw error
  }
}
