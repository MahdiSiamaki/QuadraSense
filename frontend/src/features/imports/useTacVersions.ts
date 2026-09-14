import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from '@/api/client'

/** Lifecycle of a TAC dataset version. */
export type TacStatus = 'Draft' | 'Processing' | 'Ready' | 'Active' | 'Superseded' | 'Failed'

export interface TacVersion {
  id: number
  jobId: number
  versionLabel: string
  datasetDate: string | null
  status: TacStatus
  rowCount: number | null
  diffAgainstId: number | null
  tacsAdded: number | null
  tacsUpdated: number | null
  tacsRemoved: number | null
  tacsUnchanged: number | null
  createdAt: string
  activatedAt: string | null
  activatedBy: string | null
  supersededAt: string | null
}

export function useTacVersions() {
  return useQuery({
    queryKey: ['tac-versions'],
    queryFn: ({ signal }) => api.get<TacVersion[]>('/api/v1/tac-versions', undefined, signal),
    staleTime: 60_000,
  })
}

/**
 * Activation.
 *
 * Invalidates the dashboard queries as well as the version list, because activating a version
 * changes the manufacturer and model behind every figure on the dashboard. Refreshing only the
 * list would leave the rest of the product showing the previous mapping with nothing to say so.
 */
export function useActivateTacVersion() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (id: number) =>
      api.post<{ tacVersionId: number; versionLabel: string; message: string }>(
        `/api/v1/tac-versions/${id}/activate`,
        {},
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['tac-versions'] })
      void queryClient.invalidateQueries({ queryKey: ['kpi'] })
      void queryClient.invalidateQueries({ queryKey: ['top'] })
      void queryClient.invalidateQueries({ queryKey: ['distribution'] })
    },
  })
}
