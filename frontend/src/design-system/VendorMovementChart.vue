<script setup lang="ts">
import { computed } from 'vue'
import { formatCompact, formatFull, formatSigned } from '@/lib/format'
import type { VendorMovementRow, VendorRanking } from '@/api/dashboard'

/**
 * Vendors as a diverging bar chart, measuring whichever of three things was asked for.
 *
 * A diverging layout is right here because every one of these values has a meaningful sign and a
 * meaningful zero: gaining and losing are opposites, not just magnitudes. The exception is share,
 * which has no negative side, so it renders as a plain ranked bar instead — forcing it into the
 * diverging form would put a centre line in a chart where nothing crosses it.
 *
 * Built from divs rather than ECharts: it is a sorted list of labelled bars around a line, and
 * that is easier to control in CSS than to configure in a chart library.
 */
const props = defineProps<{
  rows: VendorMovementRow[]
  ranking: VendorRanking
  /** Movement as a share of the vendor's own population, rather than as a raw count. */
  normalised: boolean
  networkChangePercent: number
}>()

/** What each mode puts on the bar, and how to say it in words. */
const measure = computed(() => {
  if (props.ranking === 'share') {
    return {
      value: (r: VendorMovementRow) => r.population,
      label: (r: VendorMovementRow) => `${r.sharePercent.toFixed(2)}%`,
      detail: (r: VendorMovementRow) =>
        `${formatFull(r.population)} active bindings, ${r.sharePercent.toFixed(2)}% of the network`,
      diverging: false,
    }
  }

  if (props.ranking === 'growth') {
    return {
      value: (r: VendorMovementRow) => r.vsNetworkPoints,
      label: (r: VendorMovementRow) =>
        `${r.vsNetworkPoints >= 0 ? '+' : ''}${r.vsNetworkPoints.toFixed(1)} pts`,
      detail: (r: VendorMovementRow) =>
        `${formatFull(r.populationAtStart)} → ${formatFull(r.population)} ` +
        `(${r.populationChangePercent.toFixed(2)}%), against the network's ` +
        `${props.networkChangePercent.toFixed(2)}%`,
      diverging: true,
    }
  }

  return props.normalised
    ? {
        value: (r: VendorMovementRow) => r.netPercentOfPopulation,
        label: (r: VendorMovementRow) =>
          `${r.netPercentOfPopulation >= 0 ? '+' : ''}${r.netPercentOfPopulation.toFixed(2)}%`,
        detail: (r: VendorMovementRow) =>
          `net ${formatSigned(r.net)} events against ${formatFull(r.population)} bindings ` +
          `(added ${formatCompact(r.added)}, removed ${formatCompact(r.removed)})`,
        diverging: true,
      }
    : {
        value: (r: VendorMovementRow) => r.net,
        label: (r: VendorMovementRow) => formatSigned(r.net),
        detail: (r: VendorMovementRow) =>
          `added ${formatFull(r.added)}, removed ${formatFull(r.removed)}`,
        diverging: true,
      }
})

const bars = computed(() => {
  const m = measure.value
  // One scale across both directions, so a bar twice as long really is twice the value.
  const widest = Math.max(...props.rows.map((r) => Math.abs(m.value(r))), 1)

  return props.rows.map((r) => {
    const v = m.value(r)
    return {
      row: r,
      positive: v >= 0,
      // Half the track each side when diverging; the whole track when not.
      width: (Math.abs(v) / widest) * (m.diverging ? 50 : 100),
      label: m.label(r),
      detail: m.detail(r),
    }
  })
})

const diverging = computed(() => measure.value.diverging)
</script>

<template>
  <div class="space-y-1">
    <div
      v-for="bar in bars"
      :key="bar.row.vendor"
      class="flex items-center gap-2 py-0.5"
      :title="bar.detail"
    >
      <span
        class="w-28 shrink-0 truncate text-right text-[var(--text-xs)]"
        :title="bar.row.vendor"
      >
        {{ bar.row.vendor }}
      </span>

      <div class="relative h-5 flex-1">
        <span
          v-if="diverging"
          aria-hidden="true"
          class="absolute inset-y-0 left-1/2 w-px bg-[var(--c-border-strong)]"
        />
        <span
          class="absolute inset-y-0.5 rounded-[2px]"
          :style="{
            width: `${bar.width}%`,
            left: diverging ? (bar.positive ? '50%' : `${50 - bar.width}%`) : '0%',
            backgroundColor: diverging
              ? bar.positive
                ? 'var(--viz-3)'
                : 'var(--viz-6)'
              : 'var(--viz-1)',
          }"
        />
      </div>

      <span
        class="tabular w-24 shrink-0 text-right text-[var(--text-xs)] font-medium"
        :style="{
          color: diverging
            ? bar.positive
              ? 'var(--viz-3)'
              : 'var(--viz-6)'
            : 'var(--c-text-secondary)',
        }"
      >
        {{ bar.label }}
      </span>
    </div>
  </div>
</template>
