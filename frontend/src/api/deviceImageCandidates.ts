import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/** One term of a candidate's quality score, and the measurement behind it. */
export interface ScoreTerm {
  term: string
  points: number
  detail: string
}

export type CandidateStatus = 'needs_review' | 'approved' | 'rejected' | 'failed'

/**
 * Something a reviewer should look at before approving.
 *
 * Advice, never a verdict: the owner decided the pixel rules warn and do not reject, so a
 * candidate with five warnings is still approvable. `high` is the subset worth stopping for -
 * replacing a verified image, a package image that covers almost none of the model's bindings,
 * a product name that names a different variant.
 */
export interface CandidateWarning {
  /**
   * low_resolution, touches_edges, small_subject, background, aspect, partial_coverage,
   * different_variant, several_images, replaces_verified, replaces_unreviewed, checksum_mismatch.
   * Typed as a string so a code added later renders by its name rather than breaking the page.
   */
  code: string
  severity: 'high' | 'info'
  detail: string
}

/**
 * Where a candidate came from and what it was matched on. Every key is optional and may be null:
 * a web-sourced candidate has none of the package fields, and the API sends `null` for an empty
 * object.
 */
export interface CandidateEvidence {
  package?: string | null
  productName?: string | null
  productId?: string | null
  matchMethods?: string[] | null
  /** TACs of the model the package maps this image to, out of `modelTacs`. */
  mappedTacs?: number | null
  modelTacs?: number | null
  /** Active bindings on those mapped TACs, out of `modelBindings`. */
  mappedBindings?: number | null
  modelBindings?: number | null
  sourceKind?: string | null
  sourcePage?: string | null
  imageUrl?: string | null
  identitySource?: string | null
  matchScope?: string | null
  qaStatus?: string | null
  packageFile?: string | null
  /** The scale factor normalisation applied; above 1 the image was enlarged. */
  upscale?: number | null
  originalSha256Ok?: boolean | null
}

export interface DeviceImageCandidate {
  id: number
  modelKey: string
  brand: string
  marketingName: string
  status: CandidateStatus
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
  /** The model's active bindings when the candidate was staged. 0 = not on the network. */
  bindings: number
  warnings: CandidateWarning[]
  evidence: CandidateEvidence | null
  /** The package folder a candidate was imported from; null for web-sourced ones. */
  package: string | null
}

export interface DeviceImageCandidatePage {
  total: number
  items: DeviceImageCandidate[]
}

/** The review queue's filters. Empty strings and nulls mean "no filter" and are not sent. */
export interface CandidateFilters {
  status: CandidateStatus | 'all'
  /** Exact brand, matched case-insensitively by the server. */
  brand: string
  /** Exact source type. */
  sourceType: string
  /** true: bindings > 0; false: bindings = 0; null: both. */
  onNetwork: boolean | null
  warnings: 'any' | 'with' | 'without' | 'high'
  sort: 'bindings' | 'score'
}

/**
 * The queue holds ~2,600 package candidates, so a page is larger than the 12 it used to be: 48
 * fills whole rows of the 1-, 2-, 3- and 4-column grid. The API clamps at 96.
 */
export const CANDIDATE_PAGE_SIZE = 48

export function useImageCandidates(
  filters: MaybeRefOrGetter<CandidateFilters>,
  page: MaybeRefOrGetter<number>,
) {
  return useQuery({
    queryKey: [
      'image-candidates',
      computed(() => ({ ...toValue(filters) })),
      computed(() => toValue(page)),
    ],
    queryFn: ({ signal }) => {
      const f = toValue(filters)
      return api.get<DeviceImageCandidatePage>(
        '/api/v1/devices/image-candidates',
        {
          status: f.status,
          brand: f.brand,
          sourceType: f.sourceType,
          onNetwork: f.onNetwork,
          warnings: f.warnings,
          sort: f.sort,
          page: toValue(page),
          pageSize: CANDIDATE_PAGE_SIZE,
        },
        signal,
      )
    },
    // The previous page stays on screen, dimmed, while the next one loads: 48 cards collapsing
    // to a skeleton and back on every filter change reads as the page breaking.
    placeholderData: keepPreviousData,
    staleTime: 30_000,
  })
}

export interface FacetCount {
  value: string
  count: number
}

/** Counts for the filter row: one status, no other filter applied. Every count is candidates. */
export interface DeviceImageCandidateFacets {
  total: number
  /** Candidates whose model has active bindings. */
  onNetwork: number
  withWarnings: number
  highWarnings: number
  /** Count descending, then value ascending. */
  brands: FacetCount[]
  sourceTypes: FacetCount[]
}

export function useImageCandidateFacets(status: MaybeRefOrGetter<CandidateFilters['status']>) {
  return useQuery({
    queryKey: ['image-candidate-facets', computed(() => toValue(status))],
    queryFn: ({ signal }) =>
      api.get<DeviceImageCandidateFacets>(
        '/api/v1/devices/image-candidates/facets',
        { status: toValue(status) },
        signal,
      ),
    placeholderData: keepPreviousData,
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
    onSuccess: () => invalidateAfterDecision(queryClient),
  })
}

export interface BulkApproveResult {
  approved: number[]
  /** Ids already approved or rejected by the time the request arrived - another tab, say. */
  notAwaitingReview: number[]
}

/**
 * Approve several candidates in one request.
 *
 * The server approves each id with the same one-candidate transaction as the single route, in
 * order, and refuses the whole batch (409) when two ids are candidates for the same model: one of
 * them would silently replace the other. At most 100 ids, no duplicates.
 */
export function useBulkApprove() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (ids: number[]) =>
      api.post<BulkApproveResult>('/api/v1/devices/image-candidates/approve', { ids }),
    onSuccess: () => invalidateAfterDecision(queryClient),
  })
}

/**
 * What a decision makes stale: the queue and its counts, the device catalogue (`hasImage`), and
 * the list of live images, which an approval has just changed.
 */
function invalidateAfterDecision(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ['image-candidates'] })
  void queryClient.invalidateQueries({ queryKey: ['image-candidate-facets'] })
  void queryClient.invalidateQueries({ queryKey: ['devices'] })
  void queryClient.invalidateQueries({ queryKey: ['live-images'] })
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
