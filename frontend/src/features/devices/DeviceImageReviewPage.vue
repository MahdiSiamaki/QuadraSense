<script setup lang="ts">
import { computed, ref } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Pagination from '@/design-system/Pagination.vue'
import DeviceImage from './DeviceImage.vue'
import {
  useImageCandidates,
  useCandidateDecision,
  useLiveImages,
  useLiveImageDecision,
  type DeviceImageCandidate,
  type DeviceImageSummary,
} from '@/api/deviceImageCandidates'
import { apiUrl } from '@/api/client'
import { formatBytes, formatDateTime } from '@/lib/format'

/**
 * Review images the sourcing pipeline has proposed.
 *
 * **The current image stays live until somebody approves the candidate.** That is the whole point
 * of this screen: automatic sourcing put shop-shelf photographs and, once, a picture of an office
 * building into the catalogue, and the fix is not a better search but a person looking before
 * anything changes.
 *
 * Side by side rather than a toggle. The question is never "is this a good picture" - it is "is
 * this better than what is there", and that cannot be answered by looking at one of them.
 */
/**
 * Two queues, and they answer different questions.
 *
 * **Candidates** is "should this replace what is there" - a proposal nobody has acted on.
 * **Current images** is "should this still be there" - the 84 pictures a script put into the
 * catalogue before any review step existed. Flagging those was not the same as giving anybody a
 * way to deal with them, and for an image with no proposal the candidate queue is simply empty.
 */
const tab = ref<'candidates' | 'current'>('candidates')

const status = ref<'needs_review' | 'approved' | 'rejected' | 'all'>('needs_review')
const page = ref(1)

const liveStatus = ref<'needs_review' | 'verified' | 'all'>('needs_review')
const livePage = ref(1)

const LIVE_STATUSES = [
  { value: 'needs_review' as const, label: 'Unverified' },
  { value: 'verified' as const, label: 'Verified' },
  { value: 'all' as const, label: 'All' },
]

const live = useLiveImages(liveStatus, livePage)
const liveDecision = useLiveImageDecision()

const liveItems = computed(() => live.data.value?.items ?? [])
const liveTotal = computed(() => live.data.value?.total ?? 0)

function setLiveStatus(next: typeof liveStatus.value) {
  liveStatus.value = next
  livePage.value = 1
}

function keep(image: DeviceImageSummary) {
  liveDecision.mutate({ modelKey: image.modelKey, decision: 'verify' })
}

function remove(image: DeviceImageSummary) {
  liveDecision.mutate({ modelKey: image.modelKey, decision: 'remove' })
}

function liveSrc(image: DeviceImageSummary): string {
  return apiUrl(
    `/api/v1/devices/image-candidates/current/${encodeURIComponent(image.modelKey)}`)
}

const STATUSES = [
  { value: 'needs_review' as const, label: 'Awaiting review' },
  { value: 'approved' as const, label: 'Approved' },
  { value: 'rejected' as const, label: 'Rejected' },
  { value: 'all' as const, label: 'All' },
]

const queue = useImageCandidates(status, page)
const decision = useCandidateDecision()

const rejecting = ref<number | null>(null)
const reason = ref('')

function setStatus(next: typeof status.value) {
  status.value = next
  page.value = 1
}

function approve(candidate: DeviceImageCandidate) {
  decision.mutate({ id: candidate.id, decision: 'approve' })
}

function confirmReject(candidate: DeviceImageCandidate) {
  decision.mutate({ id: candidate.id, decision: 'reject', reason: reason.value.trim() || undefined })
  rejecting.value = null
  reason.value = ''
}

function candidateSrc(candidate: DeviceImageCandidate): string {
  return apiUrl(`/api/v1/devices/image-candidates/${candidate.id}/image`)
}

/** Green above 70, amber 50-70, muted below: the score is advice, not a verdict. */
function scoreTone(score: number): string {
  if (score >= 70) return 'var(--c-success)'
  if (score >= 50) return 'var(--c-warning)'
  return 'var(--c-text-muted)'
}

const items = computed(() => queue.data.value?.items ?? [])
const total = computed(() => queue.data.value?.total ?? 0)
</script>

<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 class="text-lg font-semibold tracking-tight">Device images</h2>
        <p class="mt-0.5 text-xs text-[var(--c-text-muted)]">
          Images the sourcing pipeline has proposed. Nothing here is live until you approve it.
        </p>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <div class="flex rounded-[var(--radius-md)] border p-0.5" role="radiogroup" aria-label="Which queue">
          <button
            type="button"
            role="radio"
            :aria-checked="tab === 'candidates'"
            class="rounded-[var(--radius-sm)] px-2.5 py-1 text-xs font-medium transition-colors"
            :class="tab === 'candidates'
              ? 'bg-[var(--c-accent)] text-[var(--c-accent-text)]'
              : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'"
            @click="tab = 'candidates'"
          >
            Proposed
          </button>
          <button
            type="button"
            role="radio"
            :aria-checked="tab === 'current'"
            class="rounded-[var(--radius-sm)] px-2.5 py-1 text-xs font-medium transition-colors"
            :class="tab === 'current'
              ? 'bg-[var(--c-accent)] text-[var(--c-accent-text)]'
              : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'"
            @click="tab = 'current'"
          >
            Current images
          </button>
        </div>

        <div v-if="tab === 'candidates'" class="flex rounded-[var(--radius-md)] border p-0.5" role="radiogroup" aria-label="Queue">
          <button
            v-for="option in STATUSES"
            :key="option.value"
            type="button"
            role="radio"
            :aria-checked="status === option.value"
            class="rounded-[var(--radius-sm)] px-2.5 py-1 text-xs font-medium transition-colors"
            :class="
              status === option.value
                ? 'bg-[var(--c-accent)] text-[var(--c-accent-text)]'
                : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
            "
            @click="setStatus(option.value)"
          >
            {{ option.label }}
          </button>
        </div>

        <div v-else class="flex rounded-[var(--radius-md)] border p-0.5" role="radiogroup" aria-label="Which images">
          <button
            v-for="option in LIVE_STATUSES"
            :key="option.value"
            type="button"
            role="radio"
            :aria-checked="liveStatus === option.value"
            class="rounded-[var(--radius-sm)] px-2.5 py-1 text-xs font-medium transition-colors"
            :class="
              liveStatus === option.value
                ? 'bg-[var(--c-accent)] text-[var(--c-accent-text)]'
                : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
            "
            @click="setLiveStatus(option.value)"
          >
            {{ option.label }}
          </button>
        </div>
      </div>
    </header>

    <AsyncBoundary
      v-if="tab === 'candidates'"
      :is-loading="queue.isPending.value"
      :is-error="queue.isError.value"
      :error="queue.error.value"
      :is-empty="items.length === 0"
      empty-message="Nothing in this queue. Run the sourcing tool with --apply to stage candidates."
      min-height="14rem"
      @retry="queue.refetch()"
    >
      <div class="space-y-4">
        <Card v-for="candidate in items" :key="candidate.id">
          <div class="grid gap-5 lg:grid-cols-[auto_auto_1fr]">
            <!-- Current -->
            <div class="text-center">
              <p class="mb-1.5 text-2xs font-medium tracking-wide text-[var(--c-text-muted)]">
                CURRENT
              </p>
              <div class="size-40">
                <DeviceImage
                  v-if="candidate.currentStatus !== 'missing'"
                  :tac="candidate.modelKey"
                  :has-image="false"
                  :src="apiUrl(`/api/v1/devices/image-candidates/current/${encodeURIComponent(candidate.modelKey)}`)"
                  :name="candidate.brand"
                  size="lg"
                />
                <div
                  v-else
                  class="grid size-full place-items-center rounded-[var(--radius-md)] border border-dashed text-2xs text-[var(--c-text-muted)]"
                >
                  no image
                </div>
              </div>
              <p class="mt-1.5 text-2xs" :style="{
                color: candidate.currentStatus === 'verified' ? 'var(--c-success)' : 'var(--c-text-muted)',
              }">
                {{ candidate.currentStatus.replace('_', ' ') }}
              </p>
            </div>

            <!-- Candidate -->
            <div class="text-center">
              <p class="mb-1.5 text-2xs font-medium tracking-wide text-[var(--c-text-muted)]">
                CANDIDATE
              </p>
              <div class="size-40">
                <DeviceImage
                  :tac="candidate.modelKey"
                  :has-image="true"
                  :src="candidateSrc(candidate)"
                  :name="candidate.brand"
                  size="lg"
                />
              </div>
              <p class="mt-1.5 text-2xs font-semibold" :style="{ color: scoreTone(candidate.qualityScore) }">
                score {{ candidate.qualityScore }}
              </p>
            </div>

            <!-- Evidence and actions -->
            <div class="min-w-0">
              <h2 class="text-sm font-semibold">
                {{ candidate.brand }} {{ candidate.marketingName }}
              </h2>

              <dl class="mt-2 grid gap-x-6 gap-y-1 text-2xs sm:grid-cols-2">
                <div class="flex gap-1.5">
                  <dt class="text-[var(--c-text-muted)]">Source</dt>
                  <dd class="truncate">{{ candidate.sourceType }} · {{ candidate.sourceDomain }}</dd>
                </div>
                <div class="flex gap-1.5">
                  <dt class="text-[var(--c-text-muted)]">Original</dt>
                  <dd class="tabular">
                    {{ candidate.originalWidth }}×{{ candidate.originalHeight }} ·
                    {{ formatBytes(candidate.byteSize) }} stored
                  </dd>
                </div>
                <div class="flex gap-1.5">
                  <dt class="text-[var(--c-text-muted)]">Proposed</dt>
                  <dd>{{ formatDateTime(candidate.createdAt) }}</dd>
                </div>
                <div class="flex min-w-0 gap-1.5">
                  <dt class="shrink-0 text-[var(--c-text-muted)]">URL</dt>
                  <dd class="truncate" :title="candidate.sourceUrl">{{ candidate.sourceUrl }}</dd>
                </div>
              </dl>

              <!--
                The breakdown is shown, not summarised. A reviewer asked to replace what customers
                see deserves the reasoning, and a bare number invites either blind trust or blind
                distrust.
              -->
              <ul class="mt-3 space-y-0.5 border-t pt-2 text-2xs">
                <li v-for="term in candidate.scoreBreakdown" :key="term.term" class="flex gap-2">
                  <span class="tabular w-8 shrink-0 text-right font-medium"
                        :style="{ color: term.points > 0 ? 'var(--c-success)' : 'var(--c-text-muted)' }">
                    {{ term.points > 0 ? '+' : '' }}{{ term.points }}
                  </span>
                  <span class="w-32 shrink-0 text-[var(--c-text-secondary)]">{{ term.term }}</span>
                  <span class="min-w-0 truncate text-[var(--c-text-muted)]">{{ term.detail }}</span>
                </li>
              </ul>

              <p v-if="candidate.rejectionReason" class="mt-2 text-2xs text-[var(--c-danger)]">
                Rejected: {{ candidate.rejectionReason }}
              </p>

              <div v-if="candidate.status === 'needs_review'" class="mt-3 flex flex-wrap items-center gap-2">
                <Button
                  variant="primary"
                  :pending="decision.isPending.value"
                  @click="approve(candidate)"
                >
                  {{ candidate.currentStatus === 'missing' ? 'Approve' : 'Replace current' }}
                </Button>

                <Button variant="ghost" @click="rejecting = rejecting === candidate.id ? null : candidate.id">
                  Reject
                </Button>

                <span class="text-2xs text-[var(--c-text-muted)]">
                  Keeping the current image is simply leaving this alone.
                </span>
              </div>

              <div v-if="rejecting === candidate.id" class="mt-2 flex flex-wrap items-center gap-2">
                <input
                  v-model="reason"
                  placeholder="Why? e.g. shop shelf, wrong colourway"
                  class="min-w-[16rem] flex-1 rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-2.5 py-1.5 text-xs"
                />
                <Button variant="danger" @click="confirmReject(candidate)">Confirm reject</Button>
              </div>
            </div>
          </div>
        </Card>

        <Pagination
          v-if="total > 12"
          :page="page"
          :page-size="12"
          :total="total"
          @update:page="page = $event"
        />
      </div>
    </AsyncBoundary>

    <!--
      Current images. A grid rather than the side-by-side above, because there is nothing to
      compare against - the question is only whether this picture should stay.
    -->
    <AsyncBoundary
      v-else
      :is-loading="live.isPending.value"
      :is-error="live.isError.value"
      :error="live.error.value"
      :is-empty="liveItems.length === 0"
      empty-message="No images in this state."
      min-height="14rem"
      @retry="live.refetch()"
    >
      <div class="space-y-4">
        <p class="text-xs text-[var(--c-text-muted)]">
          {{ liveTotal }} image{{ liveTotal === 1 ? '' : 's' }}.
          <template v-if="liveStatus === 'needs_review'">
            These were sourced automatically before there was any review step. They are being
            shown to users right now. <strong>Keep</strong> records that you have checked one;
            <strong>Remove</strong> takes that model back to the drawn placeholder.
          </template>
        </p>

        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          <Card v-for="image in liveItems" :key="image.modelKey">
            <div class="mx-auto size-36">
              <DeviceImage
                :tac="image.modelKey"
                :has-image="true"
                :src="liveSrc(image)"
                :name="image.brand"
                size="lg"
              />
            </div>

            <p class="mt-2 truncate text-sm font-semibold" :title="image.marketingName">
              {{ image.brand }} {{ image.marketingName }}
            </p>

            <p class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
              {{ image.sourceType }}
              <template v-if="image.sourceDomain"> · {{ image.sourceDomain }}</template>
              · {{ formatBytes(image.byteSize) }}
            </p>

            <p
              v-if="image.status === 'verified'"
              class="mt-0.5 text-2xs text-[var(--c-success)]"
            >
              verified<template v-if="image.verifiedBy"> by {{ image.verifiedBy }}</template>
            </p>
            <p v-else class="mt-0.5 text-2xs text-[var(--c-warning)]">
              nobody has checked this
            </p>

            <div v-if="image.status !== 'verified'" class="mt-2.5 flex gap-2">
              <Button variant="primary" :pending="liveDecision.isPending.value" @click="keep(image)">
                Keep
              </Button>
              <Button variant="danger" :pending="liveDecision.isPending.value" @click="remove(image)">
                Remove
              </Button>
            </div>
            <div v-else class="mt-2.5">
              <Button variant="ghost" :pending="liveDecision.isPending.value" @click="remove(image)">
                Remove
              </Button>
            </div>
          </Card>
        </div>

        <Pagination
          v-if="liveTotal > 24"
          :page="livePage"
          :page-size="24"
          :total="liveTotal"
          @update:page="livePage = $event"
        />
      </div>
    </AsyncBoundary>
  </div>
</template>
