<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { KIND_LABEL, useTimeline, type Timeline, type TimelineKind, type TimelinePeriod } from '@/api/timeline'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { formatDate, formatFull, formatImei, formatMsisdn } from '@/lib/format'
import TimelineChart, { type ChartRow } from './TimelineChart.vue'

/**
 * One number's, SIM's or handset's history: every binding over time, or the bindings combined by
 * what they were bound to - a number's SIMs (its SIM swaps), a SIM's handsets, a handset's SIMs.
 *
 * Read from the binding history, so it is the same cost for a handset with five bindings as for
 * one with eight thousand. What it can say is limited by the feed, and it says so where it
 * matters: "active" is "not yet removed by the feed", the dump is a month not a moment, and an
 * IMEI is one radio, not a phone.
 */
const props = defineProps<{ identifier: string }>()
const emit = defineEmits<{ drill: [identifier: string]; close: [] }>()

const timeline = useTimeline()
watch(
  () => props.identifier,
  (identifier) => timeline.mutate(identifier),
  { immediate: true },
)

const data = computed<Timeline | null>(() => timeline.data.value ?? null)

/** By default: what the question usually is - a number's SIMs, a SIM's handsets, a handset's SIMs. */
const DEFAULT_VIEW: Record<TimelineKind, TimelineKind> = { msisdn: 'imsi', imsi: 'imei', imei: 'imsi' }

type View = TimelineKind | 'bindings'
const view = ref<View>('bindings')
watch(data, (t) => {
  if (!t) return
  const preferred = DEFAULT_VIEW[t.kind]
  view.value = t.groups.some((g) => g.kind === preferred) ? preferred : (t.groups[0]?.kind ?? 'bindings')
})

const views = computed(() => {
  const t = data.value
  if (!t) return []
  const kinds = [...new Set(t.groups.map((g) => g.kind))]
  const BY: Record<TimelineKind, string> = { msisdn: 'By number', imsi: 'By SIM', imei: 'By handset' }
  return [
    ...kinds.map((k) => ({ id: k as View, label: BY[k] })),
    { id: 'bindings' as View, label: 'Every binding' },
  ]
})

const LIMIT = 200
const showAll = ref(false)
watch(view, () => (showAll.value = false))

function shown(kind: TimelineKind | null, value: string | null): string {
  if (value === null) return 'withheld'
  if (value.includes('*')) return value
  return kind === 'msisdn' ? formatMsisdn(value) : kind === 'imei' ? formatImei(value) : value
}

const allRows = computed<ChartRow[]>(() => {
  const t = data.value
  if (!t) return []

  if (view.value !== 'bindings') {
    return t.groups
      .filter((g) => g.kind === view.value)
      .map((g) => ({
        id: `${g.kind}:${g.key}`,
        label: shown(g.kind, g.key),
        sublabel:
          g.kind === 'imei'
            ? (g.model ?? (g.tac ? `TAC ${g.tac} not in GSMA` : 'no usable IMEI'))
            : `${g.bindings} binding${g.bindings === 1 ? '' : 's'}`,
        drill: g.drillable ? g.key : null,
        active: g.active,
        periods: g.periods,
      }))
  }

  // Each binding is labelled by what it adds to the centre: the two identifiers that are not it.
  return t.bindings.map((b, i) => {
    const parts = (['msisdn', 'imsi', 'imei'] as TimelineKind[])
      .filter((k) => k !== t.kind)
      .map((k) => shown(k, k === 'msisdn' ? b.msisdn : k === 'imsi' ? b.imsi : b.imei))
    return {
      id: `b${i}`,
      label: parts[0] ?? '',
      sublabel: [parts[1], b.model].filter(Boolean).join(' · '),
      drill: null,
      active: b.active,
      periods: b.periods,
      events: b.events,
    }
  })
})

const rows = computed(() => (showAll.value ? allRows.value : allRows.value.slice(0, LIMIT)))

const axisEnd = computed(() => {
  const t = data.value
  if (!t) return ''
  return t.dataThrough ?? t.summary.lastChange ?? t.dumpWindowEnd
})

function seen(date: string | null, isWindow: boolean, t: Timeline): string {
  if (isWindow) return `In the initial dump (${formatDate(t.dumpWindowStart)} – ${formatDate(t.dumpWindowEnd)})`
  return date ? formatDate(date) : '—'
}

function periodText(periods: TimelinePeriod[]): string {
  if (periods.length === 0) return 'never held'
  return periods
    .map((p) => `${p.startsInDump ? 'dump' : formatDate(p.start)} → ${p.end ? formatDate(p.end) : 'now'}`)
    .join('; ')
}

const problem = computed(() => {
  const error = timeline.error.value
  if (!(error instanceof ApiError)) return null
  return { title: error.problem?.title ?? 'Request failed', detail: error.problem?.detail ?? null, status: error.status }
})

const centreText = computed(() => {
  const t = data.value
  if (!t) return props.identifier
  return shown(t.kind, t.identifier)
})
</script>

<template>
  <Card :title="data ? `${KIND_LABEL[data.kind].one} timeline` : 'Timeline'" :subtitle="centreText">
    <template #actions>
      <div class="flex flex-wrap items-center gap-2">
        <SegmentedControl
          v-if="views.length > 1"
          v-model="view"
          :options="views.map((v) => ({ value: v.id, label: v.label }))"
          label="Show"
        />
        <button
          type="button"
          class="grid size-7 place-items-center rounded-[var(--radius-md)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
          aria-label="Close timeline"
          @click="emit('close')"
        >
          <span aria-hidden="true" class="text-lg leading-none">&times;</span>
        </button>
      </div>
    </template>

    <!-- A history that is missing or still being built answers 503, and the reason is the message. -->
    <div
      v-if="problem && problem.status === 503"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] bg-[var(--c-warning-subtle)] px-3 py-2 text-xs"
      role="status"
    >
      <p class="font-semibold text-[var(--c-warning-text)]">{{ problem.title }}</p>
      <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ problem.detail }}</p>
    </div>

    <AsyncBoundary
      v-else
      :is-loading="timeline.isPending.value"
      :is-error="timeline.isError.value"
      :error="timeline.error.value"
      :is-empty="data !== null && data.bindings.length === 0"
      empty-message="No binding has ever held this identifier."
      min-height="12rem"
      @retry="timeline.mutate(identifier)"
    >
      <div v-if="data" class="flex flex-col gap-4">
        <dl class="tabular grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 lg:grid-cols-6">
          <div class="col-span-2">
            <dt class="text-2xs text-[var(--c-text-muted)]">First seen</dt>
            <dd class="text-sm font-medium">{{ seen(data.summary.firstSeen, data.summary.firstSeenIsDumpWindow, data) }}</dd>
          </div>
          <div>
            <dt class="text-2xs text-[var(--c-text-muted)]">Last change</dt>
            <dd class="text-sm font-medium">
              <template v-if="data.summary.lastChange">{{ formatDate(data.summary.lastChange) }}</template>
              <span v-else class="text-[var(--c-text-muted)]" title="No daily file has mentioned it: only the initial dump listed it.">dump only</span>
            </dd>
          </div>
          <div>
            <dt class="text-2xs text-[var(--c-text-muted)]">Bindings</dt>
            <dd class="text-sm font-medium">
              {{ formatFull(data.summary.bindings) }}
              <span class="text-xs font-normal text-[var(--c-text-secondary)]">{{ formatFull(data.summary.activeBindings) }} active</span>
            </dd>
          </div>
          <div v-if="data.kind !== 'msisdn' && !data.withheld.includes('msisdn')">
            <dt class="text-2xs text-[var(--c-text-muted)]">Numbers</dt>
            <dd class="text-sm font-medium">{{ formatFull(data.summary.numbers) }}</dd>
          </div>
          <div v-if="data.kind !== 'imsi' && !data.withheld.includes('imsi')">
            <dt class="text-2xs text-[var(--c-text-muted)]">SIMs</dt>
            <dd class="text-sm font-medium">{{ formatFull(data.summary.sims) }}</dd>
          </div>
          <div v-if="data.kind !== 'imei' && !data.withheld.includes('imei')">
            <dt class="text-2xs text-[var(--c-text-muted)]">Handsets (IMEIs)</dt>
            <dd class="text-sm font-medium">{{ formatFull(data.summary.handsets) }}</dd>
          </div>
          <div v-if="data.kind === 'imei'">
            <dt class="text-2xs text-[var(--c-text-muted)]">Model</dt>
            <dd class="text-sm font-medium">{{ data.model ?? (data.tac ? `TAC ${data.tac} not in GSMA` : '—') }}</dd>
          </div>
        </dl>

        <!-- What the reader may not see, and what the feed did that does not fit. -->
        <ul class="flex flex-col gap-1 text-2xs">
          <li v-if="data.withheld.length" class="text-[var(--c-warning-text)]">
            {{ data.withheld.map((k) => KIND_LABEL[k].many).join(' and ') }} withheld: your account may not look them up.
          </li>
          <li v-if="data.masked" class="text-[var(--c-warning-text)]">
            Identifiers are masked: your account does not hold <code class="font-mono">identifier.reveal</code>. A masked
            identifier cannot be opened.
          </li>
          <li v-if="data.truncated" class="text-[var(--c-warning-text)]">
            Showing the {{ formatFull(data.maxBindings) }} most recently changed bindings of more.
          </li>
          <li v-if="data.summary.redundantAdds || data.summary.orphanRemoves" class="text-[var(--c-text-muted)]">
            Feed notes: {{ formatFull(data.summary.redundantAdds) }} add(s) of a binding already held - mostly the initial dump
            being a month rather than a moment - and {{ formatFull(data.summary.orphanRemoves) }} remove(s) of one not held. Marked
            amber on the bindings view; neither changes a binding's state.
          </li>
          <li v-if="data.summary.stateDisagreements" class="text-[var(--c-warning-text)]">
            {{ data.summary.stateDisagreements }} binding(s) end differently by their events than in current state - usually a day
            part way through being imported. "Active" shows current state.
          </li>
        </ul>

        <div class="overflow-x-auto">
          <div class="min-w-[44rem]">
            <TimelineChart
              :rows="rows"
              :from="data.dumpWindowStart"
              :dump-end="data.dumpWindowEnd"
              :to="axisEnd"
              @drill="(id) => emit('drill', id)"
            />
          </div>
        </div>

        <Button v-if="!showAll && allRows.length > LIMIT" size="sm" variant="ghost" class="w-fit" @click="showAll = true">
          Show all {{ formatFull(allRows.length) }}
        </Button>

        <!-- The legend states the limits of the data in the place it is read. -->
        <div class="flex flex-wrap items-center gap-x-5 gap-y-1 border-t pt-3 text-2xs text-[var(--c-text-muted)]">
          <span class="inline-flex items-center gap-1.5">
            <span class="legend-hatch inline-block h-3 w-5 rounded-[2px] border" aria-hidden="true" />
            Initial dump: seen at some point in the window, not necessarily throughout
          </span>
          <span class="inline-flex items-center gap-1.5">
            <span class="inline-block h-2 w-5 rounded-[2px] bg-[var(--c-accent)]" aria-hidden="true" />
            Held by the feed - "active" means not yet removed, not that the SIM is in the handset today
          </span>
          <span v-if="view === 'bindings'" class="inline-flex items-center gap-1.5">
            <span class="inline-block h-3 w-0.5 bg-[var(--c-text)]" aria-hidden="true" /> add or remove
            <span class="ml-2 inline-block h-3 w-0.5 bg-[var(--c-warning)]" aria-hidden="true" /> one that changed nothing
          </span>
          <span v-if="data.kind === 'imei' || view === 'imei'">An IMEI is one radio, not one phone: a dual-SIM handset has two.</span>
        </div>

        <!-- The same facts as text: for reading closely, copying, and screen readers. -->
        <details class="text-xs">
          <summary class="cursor-pointer text-[var(--c-text-secondary)]">As a table</summary>
          <div class="mt-2 overflow-x-auto">
            <table class="w-full text-left">
              <thead class="border-b text-2xs tracking-wide text-[var(--c-text-muted)] uppercase">
                <tr>
                  <th scope="col" class="px-2 py-1.5 font-medium">{{ view === 'bindings' ? 'Binding' : KIND_LABEL[view].one }}</th>
                  <th scope="col" class="px-2 py-1.5 font-medium">First seen</th>
                  <th scope="col" class="px-2 py-1.5 font-medium">Last change</th>
                  <th scope="col" class="px-2 py-1.5 font-medium">State</th>
                  <th scope="col" class="px-2 py-1.5 font-medium">Held</th>
                </tr>
              </thead>
              <tbody class="divide-y">
                <template v-if="view === 'bindings'">
                  <tr v-for="(b, i) in showAll ? data.bindings : data.bindings.slice(0, LIMIT)" :key="i">
                    <td class="tabular px-2 py-1 font-mono">{{ allRows[i]?.label }} <span class="font-sans text-[var(--c-text-muted)]">{{ allRows[i]?.sublabel }}</span></td>
                    <td class="tabular px-2 py-1">{{ seen(b.firstSeen, b.firstSeenIsDumpWindow, data) }}</td>
                    <td class="tabular px-2 py-1">{{ b.lastChange ? formatDate(b.lastChange) : 'dump only' }}</td>
                    <td class="px-2 py-1">{{ b.active ? 'Active' : 'Ended' }}</td>
                    <td class="tabular px-2 py-1">{{ periodText(b.periods) }}</td>
                  </tr>
                </template>
                <template v-else>
                  <tr v-for="g in data.groups.filter((x) => x.kind === view).slice(0, showAll ? undefined : LIMIT)" :key="g.key">
                    <td class="tabular px-2 py-1 font-mono">{{ shown(g.kind, g.key) }}</td>
                    <td class="tabular px-2 py-1">{{ seen(g.firstSeen, g.firstSeenIsDumpWindow, data) }}</td>
                    <td class="tabular px-2 py-1">{{ g.lastChange ? formatDate(g.lastChange) : 'dump only' }}</td>
                    <td class="px-2 py-1">{{ g.active ? 'Active' : 'Ended' }}</td>
                    <td class="tabular px-2 py-1">{{ periodText(g.periods) }}</td>
                  </tr>
                </template>
              </tbody>
            </table>
          </div>
        </details>

        <p class="tabular text-2xs text-[var(--c-text-muted)]">
          Data through {{ data.dataThrough ? formatDate(data.dataThrough) : '—' }} · {{ formatFull(data.elapsedMs) }} ms ·
          {{ formatFull(data.rowsRead) }} rows read
        </p>
      </div>
    </AsyncBoundary>
  </Card>
</template>

<style scoped>
.legend-hatch {
  background-image: repeating-linear-gradient(135deg, var(--c-border-strong) 0, var(--c-border-strong) 1px, transparent 1px, transparent 4px);
}
</style>
