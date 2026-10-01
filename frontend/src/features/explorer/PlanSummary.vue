<script setup lang="ts">
import { computed } from 'vue'
import type { ExplorerPlan } from '@/api/explorer'
import { formatCompact, formatFull } from '@/lib/format'

/**
 * What a query costs, from the server's own plan - before it runs, or beside what it returned.
 *
 * Rows to be read against the budget, as a bar, because that ratio is the whole decision: a
 * query at 2% of the budget and one at 90% are both "allowed", and only one of them should be
 * run casually while an import is loading. Refused plans carry the server's notes on how to
 * narrow them, which is the useful half of a refusal.
 */
const props = defineProps<{ plan: ExplorerPlan; compact?: boolean }>()

const ACCESS: Record<string, string> = {
  KeyRead: 'Key read - straight to the rows',
  RangeRead: 'Range read - a slice of the ordered table',
  IndexRead: 'Index read - skips what the index rules out',
  Scan: 'Scan - reads every row in range',
}

const TONES: Record<string, { fg: string; bg: string }> = {
  Light: { fg: 'var(--c-success)', bg: 'var(--c-success-subtle)' },
  Moderate: { fg: 'var(--c-accent)', bg: 'var(--c-accent-subtle)' },
  Heavy: { fg: 'var(--c-warning)', bg: 'var(--c-warning-subtle)' },
  Refused: { fg: 'var(--c-danger)', bg: 'var(--c-danger-subtle)' },
}

const tone = computed(() => TONES[props.plan.verdict] ?? TONES['Moderate']!)
const share = computed(() => Math.min(100, (props.plan.estimatedRows / Math.max(1, props.plan.budgetRows)) * 100))
</script>

<template>
  <div class="flex flex-col gap-2" role="status">
    <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
      <span
        class="inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-semibold"
        :style="{ color: tone.fg, backgroundColor: tone.bg }"
      >
        <span class="size-1.5 rounded-full" :style="{ backgroundColor: tone.fg }" aria-hidden="true" />
        {{ plan.verdict }}
      </span>

      <span class="tabular text-xs text-[var(--c-text-secondary)]" :title="`${formatFull(plan.estimatedRows)} rows`">
        ~{{ formatCompact(plan.estimatedRows) }} rows to read of a {{ formatCompact(plan.budgetRows) }} budget
      </span>

      <span class="text-2xs text-[var(--c-text-muted)]">
        {{ plan.source }} · {{ ACCESS[plan.access] ?? plan.access }}
      </span>
    </div>

    <div
      class="h-1.5 w-full max-w-md overflow-hidden rounded-full bg-[var(--c-surface-sunken)]"
      role="meter"
      aria-label="Share of the query budget"
      :aria-valuenow="Math.round(share)"
      aria-valuemin="0"
      aria-valuemax="100"
    >
      <div class="h-full rounded-full" :style="{ width: `${Math.max(share, 1)}%`, backgroundColor: tone.fg }" />
    </div>

    <ul v-if="!compact && plan.notes.length" class="flex list-disc flex-col gap-0.5 pl-4 text-2xs text-[var(--c-text-secondary)]">
      <li v-for="(note, i) in plan.notes" :key="i">{{ note }}</li>
    </ul>
  </div>
</template>
