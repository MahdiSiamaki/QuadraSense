<script setup lang="ts">
import { computed, ref } from 'vue'
import { useBands, type PlanningBandRow, type PlanningClass, type PlanningFilter } from '@/api/planning'
import Card from '@/design-system/Card.vue'
import { formatFull, formatPercent } from '@/lib/format'
import PlanningBreakdown from './PlanningBreakdown.vue'

/**
 * N2 - the LTE and NR bands the models on the network list, matched against the operator's own
 * bands (docs/architecture/19-network-planning.md).
 *
 * Per model, from the delivery's per-model mart: a number holding two models is counted under each,
 * so the rows of a block need not add up to anything, and the figures are estimated. The operator's
 * list is configuration (OperatorBands); until it is set nothing is matched and the page says so.
 */
const props = defineProps<{
  run: number
  filter: PlanningFilter
  canExport: boolean
  canOpenDevice: boolean
}>()

const bands = useBands(() => props.run, () => props.filter, true)
const data = computed(() => bands.data.value ?? null)

/** The class the breakdown below shows: a match class, an architecture, or one band ("lte-3"). */
const chosen = ref('lte-3')
const chosenLabel = computed(() => {
  const d = data.value
  if (!d) return ''
  const named = [...d.lteMatch, ...d.nrMatch, ...d.architecture, d.ambiguous].find((c) => c.class === chosen.value)
  if (named) return named.label
  const [tech, band] = chosen.value.split('-')
  return tech === 'nr' ? `models listing n${band}` : `models listing LTE band ${band}`
})

function bandName(b: PlanningBandRow) {
  return b.technology === 'nr' ? `n${b.band}` : `Band ${b.band}`
}

const blocks = computed(() => {
  const d = data.value
  if (!d) return []
  return [
    { title: 'LTE models against the operator’s LTE bands', rows: d.lteMatch, empty: 'The operator’s LTE bands are not configured.' },
    { title: '5G models against the operator’s NR bands', rows: d.nrMatch, empty: 'The operator’s NR bands are not configured.' },
    { title: '5G models: SA and NSA, as their records state', rows: d.architecture, empty: 'No 5G model.' },
  ] satisfies { title: string; rows: PlanningClass[]; empty: string }[]
})
</script>

<template>
  <div class="flex flex-col gap-4">
    <p v-if="bands.isError.value" class="text-sm text-[var(--c-danger-text)]" role="alert">Not available.</p>
    <template v-else-if="data">
      <div v-for="n in data.noData" :key="n.topic" class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-xs">
        <p class="font-medium">{{ n.topic }}: no data</p>
        <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ n.reason }}</p>
      </div>

      <p class="text-2xs text-[var(--c-text-muted)]">
        Per model: a number holding two models is counted under each, and numbers and handsets are estimated. Shares are of the
        {{ formatFull(data.totalNumbers) }} numbers with an active binding.
        <template v-if="data.operatorLte.length || data.operatorNr.length">
          The operator’s bands as configured: LTE {{ data.operatorLte.join(', ') || '—' }}; NR {{ data.operatorNr.map((b) => `n${b}`).join(', ') || '—' }}.
        </template>
      </p>

      <div class="grid gap-3 lg:grid-cols-3">
        <Card v-for="block in blocks" :key="block.title" :title="block.title" flush>
          <table class="w-full text-xs">
            <tbody>
              <tr v-for="c in block.rows" :key="c.class" class="border-t first:border-t-0">
                <td class="px-4 py-1.5">
                  <button
                    type="button"
                    class="text-left hover:underline"
                    :class="chosen === c.class ? 'font-medium text-[var(--c-accent)]' : ''"
                    @click="chosen = c.class"
                  >
                    {{ c.label }}
                  </button>
                </td>
                <td class="tabular px-4 py-1.5 text-right">{{ formatFull(c.numbers) }}</td>
                <td class="tabular px-4 py-1.5 text-right text-[var(--c-text-muted)]">{{ formatPercent(c.sharePercent) }}</td>
              </tr>
              <tr v-if="!block.rows.length"><td class="px-4 py-3 text-[var(--c-text-muted)]">{{ block.empty }}</td></tr>
            </tbody>
          </table>
        </Card>
      </div>
      <p class="text-2xs text-[var(--c-text-muted)]">
        A model is 5G only when its record lists an NR band. SA is read from “5G-2Series”, NSA from options 3, 4 and 7 or an EN-DC
        combination - nothing is inferred from the bands alone.
        <button type="button" class="hover:underline" :class="chosen === 'ambiguous' ? 'font-medium text-[var(--c-accent)]' : ''" @click="chosen = 'ambiguous'">
          {{ formatFull(data.ambiguous.numbers) }} numbers hold a model with a band entry the parser could not read
        </button>; it is kept as written, not guessed.
      </p>

      <div class="grid gap-3 lg:grid-cols-2">
        <Card v-for="list in [{ title: 'LTE bands', rows: data.lteBands }, { title: 'NR (5G) bands', rows: data.nrBands }]" :key="list.title" :title="list.title" flush>
          <div class="max-h-96 overflow-y-auto">
            <table class="w-full text-xs">
              <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
                <tr>
                  <th scope="col" class="px-4 py-2 font-medium">Band</th>
                  <th scope="col" class="px-4 py-2 text-right font-medium" title="Numbers holding a model that lists the band. Estimated.">Numbers</th>
                  <th scope="col" class="px-4 py-2 text-right font-medium">Share</th>
                  <th scope="col" class="px-4 py-2 text-right font-medium" title="Distinct IMEIs. Estimated.">Handsets</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="b in list.rows" :key="b.band" class="border-t">
                  <td class="px-4 py-1.5">
                    <button
                      type="button"
                      class="text-left hover:underline"
                      :class="chosen === `${b.technology}-${b.band}` ? 'font-medium text-[var(--c-accent)]' : ''"
                      @click="chosen = `${b.technology}-${b.band}`"
                    >
                      {{ bandName(b) }}
                    </button>
                    <span v-if="b.operator" class="ml-2 rounded-full border px-1.5 text-2xs text-[var(--c-text-secondary)]">operator</span>
                  </td>
                  <td class="tabular px-4 py-1.5 text-right">{{ formatFull(b.numbers) }}</td>
                  <td class="tabular px-4 py-1.5 text-right text-[var(--c-text-muted)]">{{ formatPercent(b.sharePercent) }}</td>
                  <td class="tabular px-4 py-1.5 text-right">{{ formatFull(b.handsets) }}</td>
                </tr>
                <tr v-if="!list.rows.length"><td colspan="4" class="px-4 py-3 text-[var(--c-text-muted)]">No model lists one.</td></tr>
              </tbody>
            </table>
          </div>
        </Card>
      </div>

      <Card :title="`Who holds what: ${chosenLabel}`">
        <PlanningBreakdown
          view="bands"
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
