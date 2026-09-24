import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import {
  importsApi,
  isTerminal,
  type ImportHistoryQuery,
  type ImportJobDetail,
  type ImportPage,
} from '@/api/imports'

/**
 * Polling intervals.
 *
 * A job in flight changes second by second, so its detail page polls fast enough for the
 * progress bar to move. A job that has finished changes never, so polling stops entirely —
 * `refetchInterval` returns false rather than a number, which is what makes an open detail
 * page stop costing anything the moment its import completes.
 *
 * The history list polls slowly. It is a list of mostly-finished work, and the one row that is
 * moving is better watched on its own page.
 */
const RUNNING_POLL_MS = 1500
const LIST_POLL_MS = 10_000
const FRESHNESS_STALE_MS = 60_000

export function useImportHistory(query: MaybeRefOrGetter<ImportHistoryQuery>) {
  return useQuery({
    queryKey: ['imports', 'list', computed(() => toValue(query))],
    queryFn: ({ signal }) => importsApi.list(toValue(query), signal),

    // Keeps the previous page on screen while the next one loads, so paging and filtering do
    // not blank the table and shift the layout under the cursor.
    placeholderData: keepPreviousData,

    refetchInterval: (q) => {
      const data = q.state.data as ImportPage | undefined
      const anyRunning = data?.items.some((item) => !isTerminal(item.status)) ?? false
      return anyRunning ? LIST_POLL_MS : false
    },
  })
}

export function useImportDetail(jobId: MaybeRefOrGetter<number>) {
  return useQuery({
    queryKey: ['imports', 'detail', computed(() => toValue(jobId))],
    queryFn: ({ signal }) => importsApi.get(toValue(jobId), signal),
    refetchInterval: (q) => {
      // An error is an answer, not a job still loading: a 404 for a job that does not exist
      // used to be asked again every two seconds for as long as the page stayed open.
      if (q.state.status === 'error') return false
      const data = q.state.data as ImportJobDetail | undefined
      if (!data) return RUNNING_POLL_MS
      return isTerminal(data.summary.status) ? false : RUNNING_POLL_MS
    },
  })
}

export function useQuarantineSamples(
  jobId: MaybeRefOrGetter<number>,
  summaryId: MaybeRefOrGetter<number | null>,
) {
  return useQuery({
    queryKey: ['imports', 'quarantine', computed(() => toValue(jobId)), computed(() => toValue(summaryId))],
    queryFn: ({ signal }) => importsApi.quarantineSamples(toValue(jobId), toValue(summaryId)!, signal),
    enabled: computed(() => toValue(summaryId) !== null),
    staleTime: Number.POSITIVE_INFINITY, // a finished job's quarantine rows never change
  })
}

/**
 * How current each source is.
 *
 * `enabled` exists because the header shows this to everyone, and a Viewer without `import.view`
 * would otherwise poll an endpoint that answers 403 every thirty seconds - filling the audit log
 * with denials that mean nothing and are indistinguishable from someone probing.
 */
export function useFreshness(options: { enabled?: MaybeRefOrGetter<boolean> } = {}) {
  return useQuery({
    queryKey: ['imports', 'freshness'],
    queryFn: ({ signal }) => importsApi.freshness(signal),
    staleTime: FRESHNESS_STALE_MS,
    refetchInterval: FRESHNESS_STALE_MS,
    enabled: computed(() => toValue(options.enabled ?? true)),
  })
}

/** `enabled`, as for {@link useFreshness}: without import.view this is a 403 every five seconds. */
export function useWorkerHealth(options: { enabled?: MaybeRefOrGetter<boolean> } = {}) {
  return useQuery({
    queryKey: ['imports', 'worker-health'],
    queryFn: ({ signal }) => importsApi.workerHealth(signal),
    refetchInterval: 5000,
    enabled: computed(() => toValue(options.enabled ?? true)),
  })
}

/**
 * Cancel and reprocess.
 *
 * Both invalidate the import queries rather than patching the cache by hand. The server
 * decides what a cancellation actually did — a queued job stops immediately, a running one only
 * gets a flag — and guessing that in the client would show the operator an outcome the backend
 * had not agreed to.
 */
export function useImportActions() {
  const queryClient = useQueryClient()
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['imports'] })

  const cancel = useMutation({
    mutationFn: (jobId: number) => importsApi.cancel(jobId),
    onSuccess: invalidate,
  })

  const reprocess = useMutation({
    mutationFn: (jobId: number) => importsApi.reprocess(jobId),
    onSuccess: invalidate,
  })

  return { cancel, reprocess, invalidate }
}
