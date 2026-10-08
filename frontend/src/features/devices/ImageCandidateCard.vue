<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import DeviceImage from './DeviceImage.vue'
import { apiUrl } from '@/api/client'
import type { CandidateWarning, DeviceImageCandidate } from '@/api/deviceImageCandidates'
import { formatBytes, formatDateTime, formatFull } from '@/lib/format'

/**
 * One proposed image, with everything needed to decide it without leaving the card.
 *
 * The card is a column whose decision bar is the Card footer, so in a grid of stretched cards the
 * buttons line up along the bottom of every row however much evidence each card carries - no
 * dead space under a short card, no hunting for the button on a long one.
 *
 * Warnings are shown as text, not hover-only tooltips: the owner decided pixel rules warn and
 * never reject, which makes the warning the reviewer's main input, and a tooltip is invisible on
 * a touch screen and to anybody scanning the page.
 */
const props = withDefaults(
  defineProps<{
    candidate: DeviceImageCandidate
    selected?: boolean
    /** Decisions and selection are off, e.g. while the page shows the previous result set. */
    locked?: boolean
    /** This card's own decision is in flight. */
    pending?: 'approve' | 'reject' | null
  }>(),
  { selected: false, locked: false, pending: null },
)

const emit = defineEmits<{
  'update:selected': [value: boolean]
  approve: []
  reject: [reason: string | undefined]
}>()

const id = useId()

/**
 * The proposed image failed to load. Its placeholder looks like content, so the card says so and
 * neither the checkbox nor Approve accepts it: a person approves bytes they have seen.
 */
const imageFailed = ref(false)

const title = computed(() => `${props.candidate.brand} ${props.candidate.marketingName}`)
const reviewable = computed(() => props.candidate.status === 'needs_review')

const bindingsLabel = computed(() => {
  const n = props.candidate.bindings
  if (n <= 0) return 'Not on the network'
  return `${formatFull(n)} binding${n === 1 ? '' : 's'}`
})

const candidateSrc = computed(() =>
  apiUrl(`/api/v1/devices/image-candidates/${props.candidate.id}/image`))
const currentSrc = computed(() =>
  apiUrl(`/api/v1/devices/image-candidates/current/${encodeURIComponent(props.candidate.modelKey)}`))

function same(a: string, b: string): boolean {
  const clean = (s: string) => s.trim().replace(/\s+/g, ' ').toLowerCase()
  return clean(a) === clean(b)
}

/**
 * The package's own product name, when it says something the model name does not.
 *
 * Packages tend to prefix the brand ("Alcatel One Touch 311" for the model "One Touch 311"), so
 * that spelling is treated as the same name rather than as a discrepancy to look at.
 */
const packageName = computed(() => {
  const name = props.candidate.evidence?.productName?.trim()
  if (!name) return null
  const { brand, marketingName } = props.candidate
  return same(name, marketingName) || same(name, `${brand} ${marketingName}`) ? null : name
})

/** Only when partial: a line saying "all of them" on every card is noise. */
const coverage = computed(() => {
  const e = props.candidate.evidence
  if (e?.mappedTacs == null || e.modelTacs == null || e.mappedTacs >= e.modelTacs) return null

  let text = `${formatFull(e.mappedTacs)} of ${formatFull(e.modelTacs)} TACs`
  if (e.mappedBindings != null && e.modelBindings != null && e.modelBindings > 0) {
    text += ` · ${formatFull(e.mappedBindings)} of ${formatFull(e.modelBindings)} bindings`
  }
  return text
})

/** The page the image was found on. Only http(s): this value came from a file, not a person. */
const sourceLink = computed(() => {
  const url = props.candidate.evidence?.sourcePage || props.candidate.sourceUrl
  return url && /^https?:\/\//i.test(url) ? url : null
})

const upscale = computed(() => {
  const factor = props.candidate.evidence?.upscale
  return factor != null && factor > 1.05 ? factor.toFixed(1) : null
})

const WARNING_LABELS: Record<string, string> = {
  low_resolution: 'Low resolution',
  touches_edges: 'Touches the edges',
  small_subject: 'Small subject',
  background: 'Busy background',
  aspect: 'Unusual shape',
  partial_coverage: 'Partial coverage',
  different_variant: 'Different variant',
  several_images: 'Several images',
  replaces_verified: 'Replaces a verified image',
  replaces_unreviewed: 'Replaces an unverified image',
  checksum_mismatch: 'Checksum mismatch',
}

function warningLabel(warning: CandidateWarning): string {
  return WARNING_LABELS[warning.code] ?? warning.code.replace(/_/g, ' ')
}

/** High first, so the three shown before "+n more" are the three that matter most. */
const warnings = computed(() => {
  const all = props.candidate.warnings ?? []
  return [...all.filter((w) => w.severity === 'high'), ...all.filter((w) => w.severity !== 'high')]
})

const WARNINGS_SHOWN = 3
const showAllWarnings = ref(false)
const visibleWarnings = computed(() =>
  showAllWarnings.value ? warnings.value : warnings.value.slice(0, WARNINGS_SHOWN))
const hiddenWarnings = computed(() => warnings.value.length - WARNINGS_SHOWN)

/** Green above 70, amber 50-70, muted below: the score is advice, not a verdict. */
const scoreTone = computed(() => {
  const score = props.candidate.qualityScore
  if (score >= 70) return 'var(--c-success-text)'
  if (score >= 50) return 'var(--c-warning-text)'
  return 'var(--c-text-muted)'
})

const STATUS_LABELS: Record<DeviceImageCandidate['status'], string> = {
  needs_review: 'Awaiting review',
  approved: 'Approved',
  rejected: 'Rejected',
  failed: 'Failed',
}

const statusClass = computed(() => ({
  needs_review: 'bg-[var(--c-surface-sunken)] text-[var(--c-text-secondary)]',
  approved: 'bg-[var(--c-success-subtle)] text-[var(--c-text)]',
  rejected: 'bg-[var(--c-danger-subtle)] text-[var(--c-text)]',
  failed: 'bg-[var(--c-surface-sunken)] text-[var(--c-text-secondary)]',
}[props.candidate.status]))

const rejectOpen = ref(false)
const reason = ref('')

function submitReject() {
  emit('reject', reason.value.trim() || undefined)
}

function onToggle(event: Event) {
  emit('update:selected', (event.target as HTMLInputElement).checked)
}
</script>

<template>
  <Card
    class="transition-shadow"
    :class="selected ? 'border-[var(--c-accent)] ring-1 ring-[color:var(--c-accent)]' : ''"
  >
    <!-- Who, and how much of the network it is. -->
    <div class="flex items-start gap-2.5">
      <label v-if="reviewable" class="-m-1 shrink-0 cursor-pointer p-1">
        <input
          type="checkbox"
          class="size-4 cursor-pointer align-middle accent-[var(--c-accent)] disabled:cursor-not-allowed"
          :checked="selected"
          :disabled="locked || pending !== null || imageFailed"
          :aria-label="`Select ${title}`"
          @change="onToggle"
        />
      </label>

      <div class="min-w-0 flex-1">
        <h3 :id="`${id}-title`" class="text-sm font-semibold break-words">{{ title }}</h3>
        <p
          class="tabular mt-0.5 text-xs"
          :class="candidate.bindings > 0 ? 'text-[var(--c-text-secondary)]' : 'text-[var(--c-text-muted)]'"
        >
          {{ bindingsLabel }}
        </p>
      </div>

      <span
        v-if="!reviewable"
        class="shrink-0 rounded-full px-2 py-0.5 text-2xs font-medium"
        :class="statusClass"
      >
        {{ STATUS_LABELS[candidate.status] }}
      </span>
    </div>

    <!--
      Proposed and current side by side, the current one smaller. The question is never "is this
      a good picture" but "is this better than what is there", which needs both in view.
    -->
    <div class="mt-3 flex flex-wrap items-end gap-3">
      <figure>
        <div class="size-40">
          <DeviceImage
            :tac="candidate.modelKey"
            :has-image="true"
            :src="candidateSrc"
            :name="candidate.brand"
            size="lg"
            @failed="imageFailed = true"
          />
        </div>
        <figcaption v-if="imageFailed" class="mt-1 text-2xs font-medium text-[var(--c-danger-text)]" role="alert">
          The proposed image did not load - reload before deciding
        </figcaption>
        <figcaption v-else class="mt-1 text-2xs text-[var(--c-text-muted)]">Proposed</figcaption>
      </figure>

      <figure v-if="candidate.currentStatus !== 'missing'">
        <div class="size-20">
          <DeviceImage
            :tac="candidate.modelKey"
            :has-image="true"
            :src="currentSrc"
            :name="candidate.brand"
            size="lg"
          />
        </div>
        <figcaption class="mt-1 text-2xs text-[var(--c-text-muted)]">
          Current ·
          <span
            :class="candidate.currentStatus === 'verified'
              ? 'text-[var(--c-success-text)]'
              : 'text-[var(--c-text-secondary)]'"
          >
            {{ candidate.currentStatus === 'verified' ? 'verified' : 'unverified' }}
          </span>
        </figcaption>
      </figure>
    </div>

    <dl class="mt-3 space-y-1 text-2xs">
      <div v-if="packageName" class="flex gap-1.5">
        <dt class="shrink-0 text-[var(--c-text-muted)]">Package name:</dt>
        <dd class="min-w-0 break-words">{{ packageName }}</dd>
      </div>
      <div v-if="coverage" class="flex gap-1.5">
        <dt class="shrink-0 text-[var(--c-text-muted)]">Package covers:</dt>
        <dd class="tabular min-w-0">{{ coverage }}</dd>
      </div>
      <div class="flex gap-1.5">
        <dt class="shrink-0 text-[var(--c-text-muted)]">Source:</dt>
        <dd class="min-w-0 break-words">
          {{ candidate.sourceType }} ·
          <a
            v-if="sourceLink"
            :href="sourceLink"
            target="_blank"
            rel="noopener noreferrer"
            class="text-[var(--c-accent)] underline-offset-2 hover:underline"
          >
            {{ candidate.sourceDomain || 'source page' }}<span class="sr-only"> (source page, opens in a new tab)</span>
          </a>
          <template v-else>{{ candidate.sourceDomain || 'unknown' }}</template>
        </dd>
      </div>
      <div class="flex gap-1.5">
        <dt class="shrink-0 text-[var(--c-text-muted)]">Original:</dt>
        <dd class="tabular min-w-0">
          {{ candidate.originalWidth }}×{{ candidate.originalHeight }} ·
          {{ formatBytes(candidate.byteSize) }} stored<template v-if="upscale"> · enlarged {{ upscale }}×</template>
        </dd>
      </div>
    </dl>

    <template v-if="warnings.length">
      <ul :id="`${id}-warnings`" class="mt-3 space-y-1.5" :aria-label="`Warnings for ${title}`">
        <li
          v-for="(warning, index) in visibleWarnings"
          :key="`${warning.code}-${index}`"
          class="text-2xs leading-snug"
        >
          <span
            class="mr-1.5 inline-flex items-center gap-1 rounded-full border px-1.5 py-px font-medium text-[var(--c-text)]"
            :class="warning.severity === 'high'
              ? 'border-[var(--c-danger)] bg-[var(--c-danger-subtle)]'
              : 'border-[var(--c-warning)] bg-[var(--c-warning-subtle)]'"
          >
            <!-- Shape as well as colour: a triangle for high, a dot for a note. -->
            <svg
              v-if="warning.severity === 'high'"
              class="size-2.5 shrink-0 text-[var(--c-danger-text)]"
              viewBox="0 0 10 10"
              fill="currentColor"
              aria-hidden="true"
            >
              <path d="M5 0.5 9.7 9.2H0.3Z" />
            </svg>
            <span
              v-else
              class="size-1.5 shrink-0 rounded-full bg-[var(--c-warning)]"
              aria-hidden="true"
            />
            <span class="sr-only">{{ warning.severity === 'high' ? 'High risk:' : 'Note:' }}</span>
            {{ warningLabel(warning) }}
          </span>
          <span class="text-[var(--c-text-secondary)]">{{ warning.detail }}</span>
        </li>
      </ul>
      <button
        v-if="hiddenWarnings > 0"
        type="button"
        class="mt-1 rounded-[var(--radius-sm)] text-2xs font-medium text-[var(--c-accent)] hover:underline"
        :aria-expanded="showAllWarnings"
        :aria-controls="`${id}-warnings`"
        @click="showAllWarnings = !showAllWarnings"
      >
        {{ showAllWarnings ? 'Show fewer' : `+${hiddenWarnings} more` }}
      </button>
    </template>
    <p v-else class="mt-3 text-2xs text-[var(--c-text-muted)]">No warnings.</p>

    <!--
      The breakdown is one click away, not summarised away. A reviewer asked to replace what
      customers see deserves the reasoning; a bare number invites blind trust or blind distrust.
    -->
    <details class="mt-3 text-2xs">
      <summary class="cursor-pointer rounded-[var(--radius-sm)] select-none">
        <span class="font-semibold" :style="{ color: scoreTone }">Score {{ candidate.qualityScore }}</span>
        <span class="text-[var(--c-text-muted)]"> · how it was scored</span>
      </summary>

      <ul class="mt-1.5 space-y-0.5">
        <li v-for="term in candidate.scoreBreakdown" :key="term.term" class="flex gap-2">
          <span
            class="tabular w-7 shrink-0 text-right font-medium"
            :class="term.points > 0 ? 'text-[var(--c-success-text)]' : 'text-[var(--c-text-muted)]'"
          >
            {{ term.points > 0 ? '+' : '' }}{{ term.points }}
          </span>
          <span class="min-w-0 break-words">
            <span class="text-[var(--c-text-secondary)]">{{ term.term }}</span>
            <span v-if="term.detail" class="text-[var(--c-text-muted)]"> - {{ term.detail }}</span>
          </span>
        </li>
      </ul>

      <dl class="mt-2 space-y-0.5 border-t pt-1.5 text-[var(--c-text-muted)]">
        <div v-if="candidate.evidence?.matchMethods?.length" class="flex gap-1.5">
          <dt class="shrink-0">Matched on:</dt>
          <dd class="min-w-0 break-words text-[var(--c-text-secondary)]">
            {{ candidate.evidence.matchMethods.join(', ') }}
          </dd>
        </div>
        <div v-if="candidate.package" class="flex gap-1.5">
          <dt class="shrink-0">Package:</dt>
          <dd class="min-w-0 break-all text-[var(--c-text-secondary)]">{{ candidate.package }}</dd>
        </div>
        <div v-if="candidate.evidence?.qaStatus" class="flex gap-1.5">
          <dt class="shrink-0">Package QA:</dt>
          <dd class="min-w-0 break-words text-[var(--c-text-secondary)]">{{ candidate.evidence.qaStatus }}</dd>
        </div>
        <div class="flex gap-1.5">
          <dt class="shrink-0">Proposed:</dt>
          <dd class="text-[var(--c-text-secondary)]">{{ formatDateTime(candidate.createdAt) }}</dd>
        </div>
      </dl>
    </details>

    <p v-if="candidate.rejectionReason" class="mt-2 text-2xs wrap-anywhere text-[var(--c-danger-text)]">
      Rejected: {{ candidate.rejectionReason }}
    </p>

    <template v-if="reviewable" #footer>
      <div class="flex flex-wrap items-center gap-2 py-0.5">
        <Button
          variant="primary"
          size="sm"
          :pending="pending === 'approve'"
          :disabled="locked || pending === 'reject' || imageFailed"
          @click="emit('approve')"
        >
          {{ candidate.currentStatus === 'missing' ? 'Approve' : 'Replace current' }}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          :disabled="locked || pending === 'approve'"
          :aria-expanded="rejectOpen"
          :aria-controls="`${id}-reject`"
          @click="rejectOpen = !rejectOpen"
        >
          Reject
        </Button>
      </div>

      <form
        v-if="rejectOpen"
        :id="`${id}-reject`"
        class="mt-2 flex flex-wrap items-center gap-2 pb-0.5"
        @submit.prevent="submitReject"
      >
        <label class="sr-only" :for="`${id}-reason`">Why reject {{ title }}? (optional)</label>
        <input
          :id="`${id}-reason`"
          v-model="reason"
          placeholder="Why? e.g. shop shelf, wrong colourway"
          class="min-w-0 flex-1 basis-40 rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-2 py-1 text-xs"
        />
        <Button type="submit" variant="danger" size="sm" :pending="pending === 'reject'" :disabled="locked">
          Confirm reject
        </Button>
      </form>
    </template>
  </Card>
</template>
