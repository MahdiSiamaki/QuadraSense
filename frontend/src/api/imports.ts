/**
 * Import Center API.
 *
 * Kept separate from the dashboard client because the two have different shapes: the dashboard
 * is all reads with cacheable responses, this one has uploads with progress and actions with
 * side effects.
 */

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
  rowsProcessed: number
  rowsExpected: number | null
  percent: number | null
  updatedAt: string
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
}

export interface WorkerHealth {
  queued: number
  running: number
  retrying: number
  failedLast24Hours: number
  oldestQueuedAt: string | null
  activeWorkers: number
  staleLeases: number
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
 * Uploads one file, reporting progress as it goes.
 *
 * Uses XMLHttpRequest rather than fetch, which is a deliberate step backwards. `fetch` cannot
 * report upload progress: a request body stream is consumed by the browser with no event to
 * observe, and these files reach a gigabyte. A progress bar that sits at nothing for four
 * minutes is worse than no progress bar, so the older API wins on the one thing that matters
 * here.
 */
export function uploadImportFile(
  sourceCode: string,
  file: File,
  onProgress: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<UploadResult> {
  return new Promise((resolve, reject) => {
    const form = new FormData()
    form.append('file', file)

    const request = new XMLHttpRequest()
    request.open('POST', `${BASE}/api/v1/imports/${sourceCode}/upload`)
    request.responseType = 'json'

    // The two things `request()` in client.ts adds to every other call, and that this one has to
    // assemble by hand because it does not go through it.
    //
    // WITHOUT THESE THE UPLOAD RETURNS 401, and it did: this function was written before the
    // system had authentication at all, and the auth work never revisited the one request that
    // bypasses the shared client. In development the SPA is on :5173 and the API on :5202, so
    // XMLHttpRequest sends no cookies unless told to - the server saw an anonymous request and
    // refused it in under two milliseconds, after the browser had pushed 319 MB at it.
    request.withCredentials = true

    // And then CSRF, which would have been the next failure: the middleware exempts nothing, so
    // a POST carrying a session cookie without the matching header is a 403.
    const csrf = csrfHeader()
    if (csrf) request.setRequestHeader(csrf.name, csrf.value)

    request.upload.addEventListener('progress', (event) => {
      if (event.lengthComputable) onProgress(event.loaded / event.total)
    })

    request.addEventListener('load', () => {
      const body = request.response as Record<string, unknown> | null
      const correlationId = request.getResponseHeader('X-Correlation-Id')

      if (request.status === 201) {
        resolve({
          kind: 'queued',
          jobId: body?.['jobId'] as number,
          fileName: body?.['fileName'] as string,
          sizeBytes: body?.['sizeBytes'] as number,
          sha256: body?.['sha256'] as string,
        })
        return
      }

      // A duplicate is a normal outcome, not a failure: the same file sent twice, or a retry
      // after a dropped connection. It resolves with an explanation rather than rejecting.
      if (request.status === 409 && body && 'sha256' in body) {
        resolve({
          kind: 'duplicate',
          fileId: body['fileId'] as number,
          existingJobId: (body['existingJobId'] as number | null) ?? null,
          originalFileName: body['originalFileName'] as string,
          sha256: body['sha256'] as string,
          message: body['message'] as string,
        })
        return
      }

      // A dead session must reach the auth layer, exactly as it does for every other request.
      // Reporting "Unauthorized" beside a progress bar tells the user nothing they can act on.
      if (request.status === 401) notifyUnauthenticated()

      reject(new ApiError(request.status, body as never, correlationId))
    })

    request.addEventListener('error', () =>
      reject(new ApiError(0, { title: 'The upload could not reach the server.' }, null)),
    )

    request.addEventListener('abort', () =>
      reject(new DOMException('Upload cancelled', 'AbortError')),
    )

    signal?.addEventListener('abort', () => request.abort())
    request.send(form)
  })
}
