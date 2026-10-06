<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
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

/** Month boundaries inside the axis, for the scale. Every one gets a tick; labels are chosen below. */
const months = computed(() => {
  const out: Array<{ left: number; label: string; january: boolean }> = []
  const d = new Date(`${props.from}T00:00:00Z`)
  d.setUTCDate(1)
  d.setUTCMonth(d.getUTCMonth() + 1)
  while (d.toISOString().slice(0, 10) <= props.to) {
    const iso = d.toISOString().slice(0, 10)
    const january = d.getUTCMonth() === 0
    out.push({
      left: x(iso),
      label: d.toLocaleString('en-US', { month: 'short', timeZone: 'UTC' }) + (january ? ` ${d.getUTCFullYear()}` : ''),
      january,
    })
    d.setUTCMonth(d.getUTCMonth() + 1)
  }
  return out
})

/**
 * The axis's width in pixels, so labels are placed by what fits rather than by percentages.
 *
 * Percentages alone put "Dec 27, 2025" (the dump's first day) on top of "Jan 2026", five days
 * later, at every width - and as the history grows by a day a day, every month label would
 * eventually collide with its neighbours and the last one would hang past the axis's end.
 */
const scale = ref<HTMLElement | null>(null)
const axisWidth = ref(0)
let observer: ResizeObserver | null = null
let context: CanvasRenderingContext2D | null = null

onMounted(() => {
  if (!scale.value) return
  axisWidth.value = scale.value.clientWidth
  observer = new ResizeObserver(([entry]) => {
    if (entry) axisWidth.value = entry.contentRect.width
  })
  observer.observe(scale.value)
})
onBeforeUnmount(() => observer?.disconnect())

/** A label's width in the scale's own font, plus its left padding. */
function labelWidth(text: string): number {
  context ??= document.createElement('canvas').getContext('2d')
  if (context && scale.value) {
    context.font = getComputedStyle(scale.value).font
    return context.measureText(text).width + 4
  }
  return text.length * 7 + 4
}

/** Labels drawn after the axis's end date, which always shows: Januaries first, then in order. */
const LABEL_GAP = 8
const endLabel = computed(() => formatDate(props.to))
const labels = computed(() => {
  const width = axisWidth.value
  if (width <= 0) return []
  const taken: Array<{ left: number; right: number }> = [{ left: width - labelWidth(endLabel.value), right: width }]
  const ordered = [...months.value].sort((a, b) => Number(b.january) - Number(a.january) || a.left - b.left)
  const kept: typeof ordered = []
  for (const m of ordered) {
    const left = (m.left / 100) * width
    const right = left + labelWidth(m.label)
    if (right > width) continue
    if (taken.some((t) => left < t.right + LABEL_GAP && right + LABEL_GAP > t.left)) continue
    taken.push({ left, right })
    kept.push(m)
  }
  return kept
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
    <!--
      The scale. The axis's first day sits in the label column, against the axis's origin; its
      last day at the right end; month labels between, on one baseline, only where they fit.
    -->
    <div class="flex">
      <div class="w-52 shrink-0 self-end pr-2 text-right text-2xs leading-5 whitespace-nowrap text-[var(--c-text-muted)]">
        {{ formatDate(from) }}
      </div>
      <div ref="scale" class="relative h-5 flex-1 border-b text-2xs text-[var(--c-text-muted)]" aria-hidden="true">
        <span
          v-for="m in months"
          :key="`tick-${m.left}`"
          class="absolute bottom-0 h-1.5 border-l border-[var(--c-border-strong)]"
          :style="{ left: `${m.left}%` }"
        />
        <span
          v-for="m in labels"
          :key="`label-${m.left}`"
          class="absolute top-0 pl-1 leading-4 whitespace-nowrap"
          :style="{ left: `${m.left}%` }"
        >{{ m.label }}</span>
        <span class="absolute top-0 right-0 leading-4 whitespace-nowrap">{{ endLabel }}</span>
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
