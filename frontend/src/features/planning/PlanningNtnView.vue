<script setup lang="ts">
import { computed, ref } from 'vue'
import { useNtn, type PlanningFilter, type PlanningModelTrendPoint } from '@/api/planning'
import Card from '@/design-system/Card.vue'
import { formatFull, formatPercent, formatSequence } from '@/lib/format'
import PlanningBreakdown from './PlanningBreakdown.vue'
import PlanningTrendChart from './PlanningTrendChart.vue'

/**
 * N6 - satellite capability, from the GSMA record's ntnConnectivity and its band list
 * (docs/architecture/19-network-planning.md).
 *
 * 3GPP NTN and a proprietary satellite radio are different technologies and kept apart; a record
 * that says nothing is "not stated", never "no". Capability is not service: the operator's files hold
 * no satellite subscription, so nothing here says who uses one.
 */
const props = defineProps<{
  run: number
  filter: PlanningFilter
  canExport: boolean
  canOpenDevice: boolean
}>()

const ntn = useNtn(() => props.run, () => props.filter, true)
const data = computed(() => ntn.data.value ?? null)
const chosen = ref('ntn')

const COLORS: Record<string, string> = {
  ntn: '--viz-1',
  satellite: '--viz-4',
  'not-capable': '--viz-5',
  'not-stated': '--viz-null',
  'not-classified': '--viz-8',
}

const series = computed(() => [
  ...(data.value?.classes ?? []).map((c) => ({ key: c.class, label: c.label, color: COLORS[c.class] ?? '--viz-null' })),
  { key: 'not-classified', label: 'Not classifiable', color: COLORS['not-classified']! },
])

function modelPoints(points: PlanningModelTrendPoint[]) {
  const bySeq = new Map<number, { label: string; values: Record<string, number> }>()
  for (const p of points) {
    const entry = bySeq.get(p.sequence) ?? { label: formatSequence(p.sequence, p.date), values: {} }
    entry.values[p.class] = (entry.values[p.class] ?? 0) + p.bindings
    bySeq.set(p.sequence, entry)
  }
  return [...bySeq.entries()].sort((a, b) => a[0] - b[0]).map(([, v]) => v)
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <p v-if="ntn.isError.value" class="text-sm text-[var(--c-danger-text)]" role="alert">Not available.</p>
    <template v-else-if="data">
      <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <button
          v-for="c in data.classes"
          :key="c.class"
          type="button"
          class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4 text-left transition-colors hover:border-[var(--c-border-strong)]"
          :class="chosen === c.class ? 'border-[var(--c-accent)] ring-1 ring-[var(--c-accent)]' : ''"
          :aria-pressed="chosen === c.class"
          @click="chosen = c.class"
        >
          <p class="flex items-center gap-2 text-xs text-[var(--c-text-secondary)]">
            <span class="size-2 rounded-full" :style="{ background: `var(${COLORS[c.class]})` }" aria-hidden="true" />
            {{ c.label }}
          </p>
          <p class="tabular mt-1 text-2xl font-semibold">~{{ formatFull(c.numbers) }}</p>
          <p class="tabular text-2xs text-[var(--c-text-muted)]">
            {{ formatPercent(c.sharePercent) }} of numbers · {{ formatFull(c.bindings) }} bindings · ~{{ formatFull(c.handsets) }} handsets
          </p>
        </button>
      </div>
      <p class="text-2xs text-[var(--c-text-muted)]">
        Numbers holding such a model, estimated; a number holding two models is counted under each. Shares are of the
        {{ formatFull(data.totalNumbers) }} numbers with an active binding. Not classifiable - unknown devices and TACs the catalogue
        lacks: {{ formatFull(data.notClassified.bindings) }} bindings.
      </p>

      <div v-for="n in data.noData" :key="n.topic" class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-xs">
        <p class="font-medium">{{ n.topic }}: no data</p>
        <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ n.reason }}</p>
      </div>

      <div class="grid gap-3 lg:grid-cols-3">
        <Card
          v-for="block in [
            { title: 'NTN models: with the terrestrial network?', rows: data.byMode },
            { title: 'NTN models: orbits', rows: data.byOrbit },
            { title: 'NTN models: NTN bands', rows: data.byBand },
          ]"
          :key="block.title"
          :title="block.title"
          flush
        >
          <table class="w-full text-xs">
            <tbody>
              <tr v-for="r in block.rows" :key="r.key" class="border-t first:border-t-0">
                <td class="px-4 py-1.5">{{ r.label }}</td>
                <td class="tabular px-4 py-1.5 text-right" title="Numbers holding such a model. Estimated.">{{ formatFull(r.numbers) }}</td>
              </tr>
              <tr v-if="!block.rows.length"><td class="px-4 py-3 text-[var(--c-text-muted)]">No NTN model on the network.</td></tr>
            </tbody>
          </table>
        </Card>
      </div>

      <Card title="Bindings on each class, per delivery" subtitle="Per model, from the dashboard's own mart, under today's catalogue - bindings, not numbers.">
        <PlanningTrendChart :points="modelPoints(data.modelTrend)" :series="series" unit="bindings" />
      </Card>

      <Card :title="`Who holds what: ${data.classes.find((c) => c.class === chosen)?.label}`">
        <PlanningBreakdown
          view="ntn"
          :planning-class="chosen"
          :run="run"
          :filter="filter"
          :can-export="canExport"
          :can-open-device="canOpenDevice"
        />
      </Card>
    </template>
    <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
  </div>
</template>
