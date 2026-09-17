import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/** One term of a candidate's quality score, and the measurement behind it. */
export interface ScoreTerm {
  term: string
  points: number
  detail: string
}

export interface DeviceImageCandidate {
  id: number
  modelKey: string
  brand: string
  marketingName: string
  status: 'needs_review' | 'approved' | 'rejected' | 'failed'
  contentType: string
  byteSize: number
  sourceType: string
  sourceDomain: string
  sourceUrl: string
  originalWidth: number
  originalHeight: number
  qualityScore: number
  scoreBreakdown: ScoreTerm[]
  rejectionReason: string | null
  createdAt: string
  /** What is being served for this model right now: missing, needs_review or verified. */
  currentStatus: 'missing' | 'needs_review' | 'verified'
}

export interface DeviceImageCandidatePage {
  total: number
  items: DeviceImageCandidate[]
}

export function useImageCandidates(
  status: MaybeRefOrGetter<string>,
  page: MaybeRefOrGetter<number>,
) {
  return useQuery({
    queryKey: [
      'image-candidates',
      computed(() => toValue(status)),
      computed(() => toValue(page)),
    ],
    queryFn: ({ signal }) =>
      api.get<DeviceImageCandidatePage>(
        '/api/v1/devices/image-candidates',
        { status: toValue(status), page: toValue(page), pageSize: 12 },
        signal,
      ),
    staleTime: 30_000,
  })
}

/**
 * Approve or reject.
 *
 * Both invalidate the queue *and* the device catalogue: approving promotes the candidate to the
 * live image, so a device list still holding `hasImage: false` would be stale the moment it
 * succeeds.
 */
export function useCandidateDecision() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ id, decision, reason }: { id: number; decision: 'approve' | 'reject'; reason?: string }) =>
      api.post<{ id: number; status: string }>(
        `/api/v1/devices/image-candidates/${id}/${decision}`,
        decision === 'reject' ? { reason: reason ?? null } : {},
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['image-candidates'] })
      void queryClient.invalidateQueries({ queryKey: ['devices'] })
    },
  })
}
