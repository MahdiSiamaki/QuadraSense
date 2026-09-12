<script setup lang="ts">
import { computed } from 'vue'
import { formatFull, formatPercent } from '@/lib/format'
import type { CapabilitySupport } from '@/api/dashboard'

/**
 * Independent proportion bars, one per capability.
 *
 * Deliberately NOT a pie, donut or stacked bar. Those forms all say "these parts make
 * up one whole", and these capabilities overlap — the same handset is counted in LTE,
 * 5G and eSIM at once. Stacking them would produce a chart that sums past 100% and
 * invites exactly the wrong reading. Each bar is its own 0–100% scale.
 *
 * Where a capability can only be assessed for part of the population, the bar is drawn
 * muted and the coverage is stated. IMS emergency calling reads 76.5% supported, which
 * sounds strong until you see it is measured across 0.34% of the base.
 */
const props = defineProps<{ data: CapabilitySupport[] }>()

/** Below this, the headline percentage is not representative of the population. */
const LOW_COVERAGE_THRESHOLD = 50

const rows = computed(() =>
  props.data.map((c, i) => ({
    ...c,
    lowCoverage: c.coveragePercent < LOW_COVERAGE_THRESHOLD,
    color: c.coveragePercent < LOW_COVERAGE_THRESHOLD ? 'var(--viz-null)' : `var(--viz-${(i % 8) + 1})`,
  })),
)
</script>

<template>
  <div class="space-y-4">
    <div v-for="row in rows" :key="row.capability" class="space-y-1.5">
      <div class="flex items-baseline justify-between gap-3">
        <span class="text-[var(--text-sm)] font-medium">{{ row.capability }}</span>
        <span class="flex items-baseline gap-2">
          <span
            class="tabular text-[var(--text-base)] font-semibold"
            :class="row.lowCoverage ? 'text-[var(--c-text-muted)]' : ''"
          >
            {{ formatPercent(row.percentOfAssessable, 1) }}
          </span>
          <span class="tabular text-[var(--text-xs)] text-[var(--c-text-muted)]">
            {{ formatFull(row.supported) }} devices
          </span>
        </span>
      </div>

      <div
        class="h-2.5 w-full overflow-hidden rounded-full bg-[var(--c-surface-sunken)]"
        role="img"
        :aria-label="`${row.capability}: ${formatPercent(row.percentOfAssessable, 1)} of assessable devices`"
      >
        <div
          class="h-full rounded-full transition-[width] duration-200"
          :style="{ width: `${row.percentOfAssessable}%`, backgroundColor: row.color }"
        />
      </div>

      <p
        v-if="row.lowCoverage"
        class="text-[var(--text-2xs)] text-[var(--c-warning)]"
      >
        Only {{ formatPercent(row.coveragePercent, 2) }} of the base can be assessed for this —
        treat the percentage as indicative, not representative.
      </p>
    </div>

    <p class="border-t pt-3 text-[var(--text-xs)] text-[var(--c-text-muted)]">
      Percentages are of devices whose capability is known. These categories overlap: one handset can
      appear in several, so they do not sum to 100%.
    </p>
  </div>
</template>
