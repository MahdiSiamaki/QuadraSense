<script setup lang="ts">
import { computed } from 'vue'
import { formatDate, formatFull } from '@/lib/format'
import type { DeviceTimelinePoint } from '@/api/devices'

/**
 * One device model's daily adds and removes, as a diverging column chart.
 *
 * Diverging because these two series are opposites, not two magnitudes: a day with 900 adds and
 * 900 removes is a busy day with no net effect, and stacking them would draw that as a tall bar
 * meaning "growth". Above and below a zero line, the shape of a quiet month and the shape of a
 * churning one are different at a glance.
 *
 * Built from divs rather than ECharts. It is a row of paired bars on a baseline, which is less
 * code in CSS than configuration in a chart library — and it keeps the device page from pulling
 * in the charting bundle that only the dashboard needs.
 */
const props = defineProps<{ points: DeviceTimelinePoint[] }>()

/** One scale for both directions, so a bar twice as tall really is twice the count. */
const scale = computed(() =>
  Math.max(1, ...props.points.flatMap((p) => [p.added, p.removed])),
)

const bars = computed(() =>
  props.points.map((p) => ({
    date: p.date,
    added: p.added,
    removed: p.removed,
    net: p.added - p.removed,
    up: (p.added / scale.value) * 100,
    down: (p.removed / scale.value) * 100,
  })),
)

const totals = computed(() => ({
  added: props.points.reduce((sum, p) => sum + p.added, 0),
  removed: props.points.reduce((sum, p) => sum + p.removed, 0),
}))
</script>

<template>
  <div v-if="points.length === 0" class="grid h-40 place-items-center">
    <p class="text-sm text-[var(--c-text-muted)]">
      No daily file changed a binding of this model in this range.
    </p>
  </div>

  <div v-else class="flex flex-col gap-2">
    <div class="flex items-center gap-4 text-2xs">
      <span class="flex items-center gap-1.5">
        <span class="size-2 rounded-[1px]" :style="{ backgroundColor: 'var(--viz-3)' }" />
        <span class="tabular">{{ formatFull(totals.added) }} added</span>
      </span>
      <span class="flex items-center gap-1.5">
        <span class="size-2 rounded-[1px]" :style="{ backgroundColor: 'var(--viz-6)' }" />
        <span class="tabular">{{ formatFull(totals.removed) }} removed</span>
      </span>
      <span class="tabular ml-auto text-[var(--c-text-muted)]">
        net {{ totals.added - totals.removed >= 0 ? '+' : ''
        }}{{ formatFull(totals.added - totals.removed) }}
      </span>
    </div>

    <!--
      Days with no movement are absent from the response rather than zero-filled, so the columns
      are evenly spaced by INDEX and not by date. For a feed that delivers daily that is the same
      picture; for one with gaps it compresses them, which the caption below says out loud rather
      than letting the reader assume otherwise.
    -->
    <div class="flex h-40 items-stretch gap-px" role="img" aria-label="Daily adds and removes">
      <div
        v-for="bar in bars"
        :key="bar.date"
        class="group relative flex min-w-0 flex-1 flex-col"
        :title="`${formatDate(bar.date)}\n+${formatFull(bar.added)} added\n−${formatFull(bar.removed)} removed\nnet ${bar.net >= 0 ? '+' : ''}${formatFull(bar.net)}`"
      >
        <div class="flex flex-1 items-end">
          <div
            class="w-full rounded-t-[1px] group-hover:opacity-70"
            :style="{ height: `${bar.up}%`, backgroundColor: 'var(--viz-3)' }"
          />
        </div>
        <div class="h-px shrink-0 bg-[var(--c-border-strong)]" />
        <div class="flex flex-1 items-start">
          <div
            class="w-full rounded-b-[1px] group-hover:opacity-70"
            :style="{ height: `${bar.down}%`, backgroundColor: 'var(--viz-6)' }"
          />
        </div>
      </div>
    </div>

    <div class="tabular flex justify-between text-2xs text-[var(--c-text-muted)]">
      <span>{{ formatDate(bars[0]!.date) }}</span>
      <span>{{ bars.length }} day{{ bars.length === 1 ? '' : 's' }} with movement</span>
      <span>{{ formatDate(bars[bars.length - 1]!.date) }}</span>
    </div>
  </div>
</template>
