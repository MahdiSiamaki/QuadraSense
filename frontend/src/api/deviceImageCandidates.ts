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

/** An image the product is currently serving. */
export interface DeviceImageSummary {
  modelKey: string
  brand: string
  marketingName: string
  status: 'verified' | 'needs_review'
  contentType: string
  byteSize: number
  sourceType: string
  sourceDomain: string
  sourceNote: string
  qualityScore: number | null
  uploadedBy: string | null
  verifiedBy: string | null
  updatedAt: string
}

export interface DeviceImagePage {
  total: number
  items: DeviceImageSummary[]
}

/**
 * The images already live, unverified first.
 *
 * The other half of the review problem. The candidate queue only ever shows *proposals*, so an
 * image that was sourced before any review step existed - there are 84 of them - had nothing to
 * appear in and no way to act on it short of visiting each device page.
 */
export function useLiveImages(
  status: MaybeRefOrGetter<string>,
  page: MaybeRefOrGetter<number>,
) {
  return useQuery({
    queryKey: ['live-images', computed(() => toValue(status)), computed(() => toValue(page))],
    queryFn: ({ signal }) =>
      api.get<DeviceImagePage>(
        '/api/v1/devices/images',
        { status: toValue(status), page: toValue(page), pageSize: 24 },
        signal,
      ),
    staleTime: 30_000,
  })
}

/**
 * Keep it, or remove it.
 *
 * Verifying changes no bytes - it records that a person looked. Removing takes the model back to
 * the drawn placeholder, which is the right answer for a photograph of a shop shelf.
 */
export function useLiveImageDecision() {
  const queryClient = useQueryClient()

  return useMutation({
    // Returns void either way: the caller acts on the invalidation, not on a payload, and the
    // two branches have different response shapes.
    mutationFn: async ({ modelKey, decision }: { modelKey: string; decision: 'verify' | 'remove' }) => {
      const path = `/api/v1/devices/images/${encodeURIComponent(modelKey)}`
      if (decision === 'verify') {
        await api.post<{ modelKey: string; status: string }>(`${path}/verify`, {})
      } else {
        await api.delete<void>(path)
      }
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['live-images'] })
      void queryClient.invalidateQueries({ queryKey: ['devices'] })
    },
  })
}
