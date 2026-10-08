<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Card from '@/design-system/Card.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import Button from '@/design-system/Button.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Pagination from '@/design-system/Pagination.vue'
import DeviceImage from './DeviceImage.vue'
import ImageCandidateCard from './ImageCandidateCard.vue'
import {
  CANDIDATE_PAGE_SIZE,
  useBulkApprove,
  useImageCandidates,
  useImageCandidateFacets,
  useCandidateDecision,
  useLiveImages,
  useLiveImageDecision,
  type BulkApproveResult,
  type CandidateFilters,
  type DeviceImageCandidate,
  type DeviceImageSummary,
  type FacetCount,
} from '@/api/deviceImageCandidates'
import { ApiError, apiUrl } from '@/api/client'
import { formatBytes, formatFull } from '@/lib/format'

/**
 * Review images before they go live.
 *
 * **The current image stays live until somebody approves the candidate.** That is the whole point
 * of this screen: automatic sourcing put shop-shelf photographs and, once, a picture of an office
 * building into the catalogue, and the fix is not a better search but a person looking before
 * anything changes (ADR-011).
 *
 * Two queues, and they answer different questions.
 *
 * **Proposed** is "should this replace what is there" - a proposal nobody has acted on. Since the
 * curated package arrived it holds ~2,600 of them, so it is a workbench: filters, the models
 * with the most bindings first, and approve-what-you-have-checked in bulk.
 * **Current images** is "should this still be there" - the 84 pictures a script put into the
 * catalogue before any review step existed. Flagging those was not the same as giving anybody a
 * way to deal with them, and for an image with no proposal the candidate queue is simply empty.
 */
const tab = ref<'candidates' | 'current'>('candidates')

// ------------------------------------------------------------------ current images

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

// ------------------------------------------------------------------ proposed: filters

const STATUSES = [
  { value: 'needs_review' as const, label: 'Awaiting review' },
  { value: 'approved' as const, label: 'Approved' },
  { value: 'rejected' as const, label: 'Rejected' },
  { value: 'all' as const, label: 'All' },
]

const filters = reactive<CandidateFilters>({
  status: 'needs_review',
  brand: '',
  sourceType: '',
  onNetwork: null,
  warnings: 'any',
  sort: 'bindings',
})
const page = ref(1)

/** A checkbox, so "only off the network" is not offered: the warnings and sort cover that need. */
const onNetworkOnly = computed({
  get: () => filters.onNetwork === true,
  set: (value: boolean) => {
    filters.onNetwork = value ? true : null
  },
})

const hasNarrowingFilters = computed(() =>
  filters.brand !== '' || filters.sourceType !== '' || filters.onNetwork !== null
  || filters.warnings !== 'any')

const queue = useImageCandidates(() => filters, page)
const facets = useImageCandidateFacets(() => filters.status)

const items = computed(() => queue.data.value?.items ?? [])
const total = computed(() => queue.data.value?.total ?? 0)

/** The cards on screen belong to the previous filters or page while the next ones load. */
const stale = computed(() => queue.isPlaceholderData.value)

const facetTotal = computed(() => facets.data.value?.total ?? null)

/**
 * A facet list that always contains the value in use. Switching status can leave the chosen
 * brand with no candidates; without this the select would show a blank rather than the filter
 * that is actually applied.
 */
function withCurrent(list: FacetCount[] | undefined, current: string): FacetCount[] {
  const values = list ?? []
  if (current === '' || values.some((f) => f.value === current)) return values
  return [{ value: current, count: 0 }, ...values]
}

const brandOptions = computed(() => withCurrent(facets.data.value?.brands, filters.brand))
const sourceOptions = computed(() => withCurrent(facets.data.value?.sourceTypes, filters.sourceType))

function counted(label: string, count: number | null | undefined): string {
  return count == null ? label : `${label} (${formatFull(count)})`
}

const warningOptions = computed(() => {
  const f = facets.data.value
  return [
    { value: 'any' as const, label: counted('With or without warnings', f?.total) },
    { value: 'with' as const, label: counted('With warnings', f?.withWarnings) },
    { value: 'high' as const, label: counted('High-risk warnings', f?.highWarnings) },
    {
      value: 'without' as const,
      label: counted('Without warnings', f ? f.total - f.withWarnings : null),
    },
  ]
})

const selectClass =
  'max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-xs ' +
  'text-[var(--c-text)]'

// ------------------------------------------------------------------ proposed: selection

/**
 * Ids ticked on the page in view.
 *
 * Per page, on purpose: it is cleared on every page or filter change, and only ids of cards
 * currently on screen and awaiting review are ever sent. A person approves what they have seen,
 * never a selection that scrolled away three pages ago.
 */
const selected = ref(new Set<number>())

/** Per card, so one card's spinner does not run on all 48 Approve buttons. */
const pendingDecisions = ref(new Map<number, 'approve' | 'reject'>())

// A card whose own approve or reject is in flight is not selectable: "Approve selected" could
// otherwise send a candidate the reviewer is rejecting at that moment.
const reviewable = computed(() =>
  items.value.filter((c) => c.status === 'needs_review' && !pendingDecisions.value.has(c.id)))
const selectedItems = computed(() => reviewable.value.filter((c) => selected.value.has(c.id)))
const allSelected = computed(() =>
  reviewable.value.length > 0 && selectedItems.value.length === reviewable.value.length)
const someSelected = computed(() => selectedItems.value.length > 0 && !allSelected.value)

/**
 * Models with two of their candidates ticked. The server refuses such a batch outright - one
 * image would silently replace the other - so the button is disabled here first, with the reason.
 */
const duplicateModels = computed(() => {
  const byModel = new Map<string, DeviceImageCandidate[]>()
  for (const c of selectedItems.value) {
    byModel.set(c.modelKey, [...(byModel.get(c.modelKey) ?? []), c])
  }
  return [...byModel.values()].filter((group) => group.length > 1).map((group) => title(group[0]!))
})

const replacing = computed(() => selectedItems.value.filter((c) => c.currentStatus !== 'missing'))
const replacingVerified = computed(() =>
  replacing.value.filter((c) => c.currentStatus === 'verified').length)

function setSelected(id: number, value: boolean) {
  if (value) selected.value.add(id)
  else selected.value.delete(id)
}

function toggleAll(event: Event) {
  const checked = (event.target as HTMLInputElement).checked
  selected.value.clear()
  if (checked) for (const c of reviewable.value) selected.value.add(c.id)
}

function clearSelection() {
  selected.value.clear()
}

// ------------------------------------------------------------------ proposed: decisions

const decision = useCandidateDecision()
const bulk = useBulkApprove()

type Notice = { tone: 'success' | 'danger'; text: string }
const notice = ref<Notice | null>(null)

function title(c: DeviceImageCandidate): string {
  return `${c.brand} ${c.marketingName}`
}

function plural(n: number, one: string, many: string): string {
  return `${formatFull(n)} ${n === 1 ? one : many}`
}

/**
 * The message worth showing. ProblemDetails for validation and the batch conflict; the single
 * routes answer a conflict with `{ id, message }`, which is read too rather than shown as a bare
 * "Request failed with status 409".
 */
function describe(error: unknown): string {
  if (error instanceof ApiError) {
    const fields = error.fieldErrors.map((e) => e.message)
    if (fields.length) return fields.join(' ')
    const body = error.problem as { detail?: string; message?: string; title?: string } | null
    return body?.detail ?? body?.message ?? body?.title ?? error.message
  }
  return error instanceof Error ? error.message : 'Something went wrong.'
}

/**
 * One card's decision, awaited on its own.
 *
 * Not mutate() with per-call callbacks: the 48 cards share one mutation observer, and a second
 * mutate() detaches it from the first - a reviewer clicking card B while A's request was in flight
 * never heard that A failed, and A's spinner never stopped. Each call's own promise settles.
 */
async function decide(candidate: DeviceImageCandidate, kind: 'approve' | 'reject', reason?: string) {
  notice.value = null
  pendingDecisions.value.set(candidate.id, kind)
  try {
    await decision.mutateAsync({ id: candidate.id, decision: kind, reason })
    selected.value.delete(candidate.id)
    notice.value = {
      tone: 'success',
      text: `${kind === 'approve' ? 'Approved' : 'Rejected'} ${title(candidate)}.`,
    }
  } catch (error) {
    notice.value = {
      tone: 'danger',
      text: `Could not ${kind} ${title(candidate)}: ${describe(error)}`,
    }
  } finally {
    pendingDecisions.value.delete(candidate.id)
  }
}

function approveSelected() {
  const ids = selectedItems.value.map((c) => c.id)
  if (ids.length === 0 || duplicateModels.value.length > 0) return

  notice.value = null
  bulk.mutate(ids, {
    onSuccess: (result: BulkApproveResult) => {
      selected.value.clear()
      let text = `Approved ${formatFull(result.approved.length)}.`
      const gone = result.notAwaitingReview.length
      if (gone > 0) text += ` ${plural(gone, 'was', 'were')} no longer awaiting review.`
      notice.value = { tone: 'success', text }
    },
    onError: (error) => {
      notice.value = { tone: 'danger', text: `Nothing was approved: ${describe(error)}` }
    },
  })
}

/** Nothing can be decided on cards that are about to be replaced, or while a batch is running. */
const locked = computed(() => stale.value || bulk.isPending.value)

// ------------------------------------------------------------------ proposed: paging

const queueTop = ref<HTMLElement | null>(null)

function goToPage(next: number) {
  page.value = next
  // Next is at the bottom of 48 cards; the next page starts at the top - for the eye and for the
  // keyboard, whose focus otherwise stayed on Next below 48 cards it would have to tab back through.
  queueTop.value?.scrollIntoView({ block: 'start' })
  queueTop.value?.focus({ preventScroll: true })
}

watch(filters, () => {
  page.value = 1
  selected.value.clear()
  notice.value = null
})

watch(page, () => {
  selected.value.clear()
})

/**
 * Approving the whole of the last page empties it. Step back to the page that now is the last
 * rather than showing "nothing here" with candidates still in the queue.
 */
watch(
  () => queue.data.value,
  (data) => {
    if (!data || queue.isPlaceholderData.value) return
    const last = Math.max(1, Math.ceil(data.total / CANDIDATE_PAGE_SIZE))
    if (page.value > last) page.value = last
  },
)

const emptyMessage = computed(() => {
  if (hasNarrowingFilters.value) return 'No candidates match these filters.'
  if (filters.status === 'needs_review') {
    return 'Nothing awaiting review. Stage candidates with the sourcing tool or the package importer (--apply).'
  }
  return 'Nothing in this queue.'
})
</script>

<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 class="text-lg font-semibold tracking-tight">Device images</h2>
        <p class="mt-0.5 text-xs text-[var(--c-text-muted)]">
          Images proposed by the sourcing tool and the curated package. Nothing here is live until
          you approve it.
        </p>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <SegmentedControl
          v-model="tab"
          :options="[
            { value: 'candidates', label: 'Proposed' },
            { value: 'current', label: 'Current images' },
          ]"
          label="Which queue"
        />

        <SegmentedControl
          v-if="tab === 'current'"
          :model-value="liveStatus"
          :options="LIVE_STATUSES"
          label="Which images"
          @update:model-value="setLiveStatus"
        />
      </div>
    </header>

    <!-- ============================================================== Proposed -->
    <section v-if="tab === 'candidates'" class="space-y-3" aria-label="Proposed images">
      <!-- One filter row. Every change goes back to page 1 and clears the selection. -->
      <div ref="queueTop" tabindex="-1" class="flex scroll-mt-20 flex-wrap items-center gap-2 focus:outline-none">
        <SegmentedControl v-model="filters.status" :options="STATUSES" label="Review status" />

        <label class="sr-only" for="candidate-brand">Brand</label>
        <select id="candidate-brand" v-model="filters.brand" :class="selectClass">
          <option value="">{{ counted('All brands', facetTotal) }}</option>
          <option v-for="option in brandOptions" :key="option.value" :value="option.value">
            {{ option.value }} ({{ formatFull(option.count) }})
          </option>
        </select>

        <label class="sr-only" for="candidate-source">Source</label>
        <select id="candidate-source" v-model="filters.sourceType" :class="selectClass">
          <option value="">{{ counted('All sources', facetTotal) }}</option>
          <option v-for="option in sourceOptions" :key="option.value" :value="option.value">
            {{ option.value }} ({{ formatFull(option.count) }})
          </option>
        </select>

        <label class="sr-only" for="candidate-warnings">Warnings</label>
        <select id="candidate-warnings" v-model="filters.warnings" :class="selectClass">
          <option v-for="option in warningOptions" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>

        <label class="sr-only" for="candidate-sort">Sort</label>
        <select id="candidate-sort" v-model="filters.sort" :class="selectClass">
          <option value="bindings">Most bindings first</option>
          <option value="score">Highest score first</option>
        </select>

        <label class="inline-flex items-center gap-2 px-1 text-xs text-[var(--c-text-secondary)]">
          <input v-model="onNetworkOnly" type="checkbox" class="size-4 accent-[var(--c-accent)]" />
          On the network only
          <span v-if="facets.data.value" class="tabular text-[var(--c-text-muted)]">
            ({{ formatFull(facets.data.value.onNetwork) }})
          </span>
        </label>

        <p v-if="queue.data.value" class="tabular ml-auto text-2xs text-[var(--c-text-muted)]" aria-live="polite">
          {{ plural(total, 'candidate', 'candidates') }}
        </p>
      </div>

      <!--
        Selection bar. Sticky under the app header: the button that acts on the selection has to
        be reachable from the bottom of 48 cards, not only from the top.
      -->
      <div
        v-if="reviewable.length > 0"
        class="sticky top-16 z-5 flex flex-wrap items-center gap-x-4 gap-y-2 rounded-[var(--radius-lg)] border bg-[var(--c-surface)] px-3 py-2 shadow-[var(--shadow-sm)]"
      >
        <label class="inline-flex items-center gap-2 text-xs font-medium">
          <input
            type="checkbox"
            class="size-4 accent-[var(--c-accent)]"
            :checked="allSelected"
            :indeterminate="someSelected"
            :disabled="locked"
            @change="toggleAll"
          />
          Select all on this page
        </label>

        <p class="tabular text-xs text-[var(--c-text-secondary)]" aria-live="polite">
          {{ formatFull(selectedItems.length) }} selected
        </p>

        <p v-if="duplicateModels.length" class="text-xs text-[var(--c-danger-text)]" role="alert">
          Two images of {{ duplicateModels.join(', ') }} are selected - keep one.
        </p>
        <p v-else-if="replacing.length" class="text-2xs text-[var(--c-text-muted)]">
          {{ plural(replacing.length, 'replaces', 'replace') }} a current image<template
            v-if="replacingVerified"
          >, {{ formatFull(replacingVerified) }} of them verified</template>.
        </p>

        <div class="ml-auto flex items-center gap-2">
          <Button variant="ghost" size="sm" :disabled="selectedItems.length === 0" @click="clearSelection">
            Clear
          </Button>
          <Button
            variant="primary"
            size="sm"
            :pending="bulk.isPending.value"
            :disabled="selectedItems.length === 0 || duplicateModels.length > 0 || stale"
            @click="approveSelected"
          >
            Approve selected ({{ formatFull(selectedItems.length) }})
          </Button>
        </div>
      </div>

      <!--
        Announced from one live region that is always there: a role="status" element inserted
        together with its text is often not read out at all. The visible notice is separate.
      -->
      <p class="sr-only" role="status" aria-live="polite">{{ notice?.tone === 'success' ? notice.text : '' }}</p>
      <p class="sr-only" role="alert">{{ notice?.tone === 'danger' ? notice.text : '' }}</p>
      <p
        v-if="notice"
        class="rounded-[var(--radius-md)] border px-3 py-2 text-xs"
        :class="notice.tone === 'danger'
          ? 'border-[var(--c-danger)] bg-[var(--c-danger-subtle)] text-[var(--c-text)]'
          : 'border-[var(--c-success)] bg-[var(--c-success-subtle)] text-[var(--c-text)]'"
        aria-hidden="true"
      >
        {{ notice.text }}
      </p>

      <AsyncBoundary
        :is-loading="queue.isPending.value"
        :is-error="queue.isError.value"
        :error="queue.error.value"
        :is-empty="items.length === 0"
        :empty-message="emptyMessage"
        min-height="14rem"
        @retry="queue.refetch()"
      >
        <!--
          Equal-height cards: grid items stretch, and each card is a column whose decision bar is
          its footer, so a row of cards ends on one line however much evidence each one carries.
        -->
        <div
          class="grid grid-cols-1 gap-4 transition-opacity sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4"
          :class="stale ? 'opacity-60' : ''"
          :aria-busy="stale"
        >
          <ImageCandidateCard
            v-for="candidate in items"
            :key="candidate.id"
            :candidate="candidate"
            :selected="selected.has(candidate.id)"
            :locked="locked"
            :pending="pendingDecisions.get(candidate.id) ?? null"
            @update:selected="setSelected(candidate.id, $event)"
            @approve="decide(candidate, 'approve')"
            @reject="decide(candidate, 'reject', $event)"
          />
        </div>

        <Pagination
          v-if="total > CANDIDATE_PAGE_SIZE"
          :page="page"
          :page-size="CANDIDATE_PAGE_SIZE"
          :total="total"
          :loading="stale"
          @update:page="goToPage"
        />
      </AsyncBoundary>
    </section>

    <!--
      ============================================================== Current images
      A grid rather than a comparison, because there is nothing to compare against - the
      question is only whether this picture should stay.
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
              class="mt-0.5 text-2xs text-[var(--c-success-text)]"
            >
              verified<template v-if="image.verifiedBy"> by {{ image.verifiedBy }}</template>
            </p>
            <p v-else class="mt-0.5 text-2xs text-[var(--c-warning-text)]">
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
