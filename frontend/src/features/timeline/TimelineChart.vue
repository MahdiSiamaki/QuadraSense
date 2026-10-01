<script setup lang="ts">
import { computed } from 'vue'
import type { TimelineEvent, TimelinePeriod } from '@/api/timeline'
import { formatDate } from '@/lib/format'

/**
 * Bindings or groups as bars on one shared time axis.
 *
 * Plain positioned elements on percentages, not a charting library: a row is a handful of bars,
 * every colour is a design token used directly (ECharts' renderer cannot read oklch - see
 * lib/chart-colors.ts), and each bar keeps a title a pointer or a screen reader can read. The table
 * beside this component carries the same facts in text.
 *
 * The axis starts at the initial dump's window, drawn hatched: a binding from the dump was seen
 * at some point in that month, not necessarily all of it, so its bar is pale there.
 */
export interface ChartRow {
  id: string
  label: string
  sublabel?: string | null
  /** A complete identifier that can be opened; null when it cannot. */
  drill?: string | null
  active: boolean
  periods: TimelinePeriod[]
  events?: TimelineEvent[]
}

const props = defineProps<{
  rows: ChartRow[]
  /** yyyy-MM-dd: the dump window's first day, where the axis starts. */
  from: string
  /** yyyy-MM-dd: the dump window's last day. */
  dumpEnd: string
  /** yyyy-MM-dd: the latest day in the data, where the axis ends. */
  to: string
}>()

const emit = defineEmits<{ drill: [identifier: string] }>()

const day = (iso: string) => Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10)) / 86_400_000
const start = computed(() => day(props.from))
const span = computed(() => Math.max(1, day(props.to) + 1 - start.value))

/** Percent along the axis of the start of a day. */
const x = (iso: string) => Math.min(100, Math.max(0, ((day(iso) - start.value) / span.value) * 100))
/** Percent along the axis of the end of a day. */
const xEnd = (iso: string) => Math.min(100, Math.max(0, ((day(iso) + 1 - start.value) / span.value) * 100))

const dumpWidth = computed(() => xEnd(props.dumpEnd))

/** Month boundaries inside the axis, for the scale. */
const months = computed(() => {
  const out: Array<{ left: number; label: string }> = []
  const d = new Date(`${props.from}T00:00:00Z`)
  d.setUTCDate(1)
  d.setUTCMonth(d.getUTCMonth() + 1)
  while (d.toISOString().slice(0, 10) <= props.to) {
    const iso = d.toISOString().slice(0, 10)
    out.push({
      left: x(iso),
      label: d.toLocaleString('en-US', { month: 'short', timeZone: 'UTC' }) + (d.getUTCMonth() === 0 ? ` ${d.getUTCFullYear()}` : ''),
    })
    d.setUTCMonth(d.getUTCMonth() + 1)
  }
  return out
})

interface Segment {
  left: number
  width: number
  pale: boolean
  open: boolean
  title: string
}

function segments(row: ChartRow): Segment[] {
  return row.periods.flatMap((p) => {
    const endPct = p.end ? xEnd(p.end) : 100
    const title = `${p.startsInDump ? 'In the initial dump' : formatDate(p.start)} → ${p.end ? formatDate(p.end) : 'not yet removed'}`
    const left = x(p.start)

    // The part inside the dump window is pale: seen at some point in it, not throughout.
    if (p.startsInDump) {
      const dump: Segment = { left, width: Math.max(0.3, Math.min(endPct, dumpWidth.value) - left), pale: true, open: false, title }
      if (endPct <= dumpWidth.value) return [dump]
      return [dump, { left: dumpWidth.value, width: Math.max(0.3, endPct - dumpWidth.value), pale: false, open: !p.end, title }]
    }

    return [{ left, width: Math.max(0.3, endPct - left), pale: false, open: !p.end, title }]
  })
}

function marker(e: TimelineEvent) {
  return {
    left: x(e.date) + (xEnd(e.date) - x(e.date)) / 2,
    odd: e.effect !== 'applied',
    title: `${formatDate(e.date)}: ${e.change}${e.effect === 'redundant' ? ' (already held)' : e.effect === 'orphan' ? ' (not held)' : ''}`,
  }
}

function describe(row: ChartRow): string {
  if (row.periods.length === 0) return 'Never held by the feed.'
  return row.periods
    .map((p) => `${p.startsInDump ? 'in the initial dump' : formatDate(p.start)} to ${p.end ? formatDate(p.end) : 'not yet removed'}`)
    .join('; ')
}
</script>

<template>
  <div class="flex flex-col text-xs">
    <!-- The scale. -->
    <div class="flex">
      <div class="w-52 shrink-0" />
      <div class="relative h-5 flex-1 border-b text-2xs text-[var(--c-text-muted)]" aria-hidden="true">
        <span class="absolute bottom-0.5 left-0 pl-1">{{ formatDate(from) }}</span>
        <span
          v-for="m in months"
          :key="m.left"
          class="absolute bottom-0 h-2 border-l border-[var(--c-border-strong)] pl-1 leading-none whitespace-nowrap"
          :style="{ left: `${m.left}%` }"
        >
          <span class="relative -top-3">{{ m.label }}</span>
        </span>
      </div>
    </div>

    <ul class="flex flex-col">
      <li v-for="row in rows" :key="row.id" class="flex items-center border-b border-[var(--c-border)]/60 py-1">
        <div class="w-52 shrink-0 truncate pr-2">
          <button
            v-if="row.drill"
            type="button"
            class="tabular font-mono hover:text-[var(--c-accent)] hover:underline focus-visible:outline-2 focus-visible:outline-[var(--c-accent)]"
            :aria-label="`Open ${row.label}`"
            @click="emit('drill', row.drill)"
          >
            {{ row.label }}
          </button>
          <span v-else class="tabular font-mono">{{ row.label }}</span>
          <span v-if="row.sublabel" class="block truncate text-2xs text-[var(--c-text-muted)]">{{ row.sublabel }}</span>
        </div>

        <div class="relative h-5 flex-1">
          <span class="sr-only">{{ describe(row) }}</span>

          <!-- The dump window, behind everything. -->
          <div class="hatch absolute inset-y-0 left-0" :style="{ width: `${dumpWidth}%` }" aria-hidden="true" />

          <div
            v-for="(s, i) in segments(row)"
            :key="i"
            class="absolute top-1 bottom-1 rounded-[2px]"
            :class="s.open ? 'rounded-r-none' : ''"
            :style="{
              left: `${s.left}%`,
              width: `${s.width}%`,
              backgroundColor: row.active ? 'var(--c-accent)' : 'var(--c-text-muted)',
              opacity: s.pale ? 0.35 : 0.85,
            }"
            :title="s.title"
            aria-hidden="true"
          />

          <!-- Still held: the bar runs to the end of the data, and says so. -->
          <span
            v-if="row.periods.some((p) => !p.end)"
            class="absolute top-0 right-0 text-[0.625rem] leading-5 text-[var(--c-accent)]"
            aria-hidden="true"
          >▶</span>

          <span
            v-for="(e, i) in (row.events ?? []).map(marker)"
            :key="`e${i}`"
            class="absolute -top-0.5 h-6 w-0.5 -translate-x-1/2 rounded-full"
            :style="{ left: `${e.left}%`, backgroundColor: e.odd ? 'var(--c-warning)' : 'var(--c-text)' }"
            :title="e.title"
            aria-hidden="true"
          />
        </div>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.hatch {
  background-image: repeating-linear-gradient(
    135deg,
    var(--c-border) 0,
    var(--c-border) 1px,
    transparent 1px,
    transparent 6px
  );
}
</style>
