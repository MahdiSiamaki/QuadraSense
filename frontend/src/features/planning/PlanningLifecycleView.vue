<script setup lang="ts">
import { computed, ref } from 'vue'
import { useLifecycle, type PlanningFilter } from '@/api/planning'
import Card from '@/design-system/Card.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { formatFull, formatPercent } from '@/lib/format'

/**
 * N4 - handset lifecycle (docs/architecture/19-network-planning.md section 6).
 *
 * A change of handset is a successor within the windows the product owner set on 2026-10-27: the old
 * handset gone at most 7 days after the new one arrived, or at most 30 days before. Two handsets used
 * side by side are not a change, a dual-SIM handset's second IMEI is not a handset, and a spell still
 * running is followed only as far as the data goes - so the cohort shares are Kaplan-Meier estimates,
 * and a horizon nobody was followed to is shown as not yet observable, never as zero.
 */
const props = defineProps<{
  run: number
  filter: PlanningFilter
}>()

const lifecycle = useLifecycle(() => props.run, () => props.filter, true)
const data = computed(() => lifecycle.data.value ?? null)

const by = ref<'vendor' | 'model'>('vendor')
const BY = [
  { value: 'vendor' as const, label: 'Vendor' },
  { value: 'model' as const, label: 'Model' },
]
const moves = computed(() => (by.value === 'vendor' ? data.value?.vendorMoves : data.value?.modelMoves) ?? [])

function pct(value: number | null) {
  return value === null ? '—' : formatPercent(value)
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <p v-if="lifecycle.isError.value" class="text-sm text-[var(--c-danger-text)]" role="alert">Not available.</p>
    <template v-else-if="data">
      <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <div v-for="k in data.kinds" :key="k.kind" class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4">
          <p class="text-xs text-[var(--c-text-secondary)]">{{ k.label }}</p>
          <p class="tabular mt-1 text-2xl font-semibold">{{ formatFull(k.changes) }}</p>
          <p class="tabular text-2xs text-[var(--c-text-muted)]">{{ formatPercent(k.sharePercent) }} of changes</p>
        </div>
      </div>
      <p class="text-2xs text-[var(--c-text-muted)]">
        {{ formatFull(data.changes) }} changes of handset<template v-if="data.firstMonth"> from {{ data.firstMonth }} to {{ data.lastMonth }}</template>,
        <template v-if="data.filtered">from or to the models filtered, </template>counted once each. A newer model is one with a later GSMA
        allocation date - the TAC's, not the model's release. Two handsets used side by side for more than 7 days are not a change;
        <template v-if="data.notAssessedNumbers">{{ formatFull(data.notAssessedNumbers) }} numbers with more than 20 handsets in their history are left out.</template>
      </p>

      <div v-for="n in data.noData" :key="n.topic" class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-xs">
        <p class="font-medium">{{ n.topic }}: no data</p>
        <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ n.reason }}</p>
      </div>

      <Card title="Where numbers moved, from and to" subtitle="The largest moves; a number that changed twice is counted twice">
        <div class="mb-2"><SegmentedControl v-model="by" :options="BY" label="Moves between" /></div>
        <div class="overflow-x-auto">
          <table class="w-full text-xs">
            <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="px-3 py-2 font-medium">From</th>
                <th scope="col" class="px-3 py-2 font-medium">To</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">Changes</th>
                <th scope="col" class="px-3 py-2 text-right font-medium" title="To a model with a later GSMA allocation date">Of which upgrades</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="m in moves" :key="`${m.from}→${m.to}`" class="border-t">
                <td class="px-3 py-1.5">{{ m.from }}</td>
                <td class="px-3 py-1.5">{{ m.to }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ formatFull(m.changes) }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ formatFull(m.upgrades) }}</td>
              </tr>
              <tr v-if="!moves.length"><td colspan="4" class="px-3 py-3 text-[var(--c-text-muted)]">No change in the window.</td></tr>
            </tbody>
          </table>
        </div>
      </Card>

      <Card
        title="Time to a change, by the month a handset arrived on a number"
        subtitle="Kaplan-Meier: a handset still held, or gone with no successor, counts only as long as it was followed"
      >
        <p v-if="data.filtered" class="py-4 text-xs text-[var(--c-text-secondary)]">
          The cohorts are the whole network's only: a spell is not stored with its model. Clear the filter to see them.
        </p>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-xs">
            <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="px-3 py-2 font-medium">Arrived</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">Handsets on numbers</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">Changed within 30 days</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">90 days</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">180 days</th>
                <th scope="col" class="px-3 py-2 text-right font-medium" title="Other changes censored">Upgraded within 90 days</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">Median days to a change</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="c in data.cohorts" :key="c.cohort" class="border-t">
                <td class="tabular px-3 py-1.5">{{ c.cohort }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ formatFull(c.spells) }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ pct(c.changedBy30) }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ pct(c.changedBy90) }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ pct(c.changedBy180) }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ pct(c.upgradedBy90) }}</td>
                <td class="tabular px-3 py-1.5 text-right">{{ c.medianDays ?? '—' }}</td>
              </tr>
              <tr v-if="!data.cohorts.length"><td colspan="7" class="px-3 py-3 text-[var(--c-text-muted)]">No cohort yet.</td></tr>
            </tbody>
          </table>
          <p class="mt-2 text-2xs text-[var(--c-text-muted)]">
            — means not yet observable: nobody in that cohort has been followed so long. The feed is read as delivered: since
            15 September 2026 its files carry shifted IMEIs (set aside here, see Data quality), so a handset hidden behind one
            ends its spell early and those months' changes are understated until corrected files are imported.
            <template v-if="data.dump">{{ formatFull(data.dump.spells) }} handsets were already on their numbers in the initial dump - when they
              arrived is unknown, so they are not a cohort; {{ formatFull(data.dump.changed) }} of them have changed since.</template>
          </p>
        </div>
      </Card>

      <div class="grid gap-3 lg:grid-cols-2">
        <Card
          v-for="block in [
            { title: 'Handsets held now, by when the network first saw them', rows: data.deviceAge, note: 'Observed on the network - not a purchase or manufacture date.' },
            { title: 'Handsets held now, by their model\'s GSMA allocation year', rows: data.modelAge, note: 'The TAC allocation precedes a model\'s release.' },
          ]"
          :key="block.title"
          :title="block.title"
          :subtitle="block.note"
          flush
        >
          <table class="w-full text-xs">
            <tbody>
              <tr v-for="r in block.rows" :key="r.key" class="border-t first:border-t-0">
                <td class="px-4 py-1.5">{{ r.label }}</td>
                <td class="tabular px-4 py-1.5 text-right">{{ formatFull(r.handsets) }}</td>
                <td class="tabular px-4 py-1.5 text-right text-[var(--c-text-muted)]">{{ formatPercent(r.sharePercent) }}</td>
              </tr>
              <tr v-if="!block.rows.length"><td class="px-4 py-3 text-[var(--c-text-muted)]">Not available.</td></tr>
            </tbody>
          </table>
        </Card>
      </div>
    </template>
    <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
  </div>
</template>
