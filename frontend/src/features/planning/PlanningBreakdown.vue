<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { saveFile } from '@/api/client'
import { usePlanningBreakdown, usePlanningBreakdownExport, type PlanningBy, type PlanningFilter, type PlanningView } from '@/api/planning'
import Button from '@/design-system/Button.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { formatFull } from '@/lib/format'

/**
 * One class of a planning view, by vendor, model, device type or TAC - who holds what.
 *
 * A number holding two models is counted under each, so the rows do not add up to the class: the
 * column says "numbers holding it", and its figures are merged estimates. The rows are the largest
 * groups by active bindings, ordered by numbers: the server merges the estimates of those groups only.
 */
const props = defineProps<{
  view: PlanningView
  planningClass: string
  run: number | null
  filter: PlanningFilter
  canExport: boolean
  /** Whether a model links to its device page. */
  canOpenDevice: boolean
}>()

const by = ref<PlanningBy>('vendor')
const breakdown = usePlanningBreakdown(
  () => props.view,
  () => props.planningClass,
  by,
  () => props.run,
  () => props.filter,
  () => props.planningClass !== '',
)
const rows = computed(() => breakdown.data.value?.rows ?? [])

const exporter = usePlanningBreakdownExport()
async function exportCsv() {
  const file = await exporter.mutateAsync({ view: props.view, class: props.planningClass, by: by.value, runId: props.run, ...props.filter })
  saveFile(file, `planning-${props.view}-${props.planningClass}-by-${by.value}.csv`)
}

const BY: { value: PlanningBy; label: string }[] = [
  { value: 'vendor', label: 'Vendor' },
  { value: 'model', label: 'Model' },
  { value: 'deviceType', label: 'Device type' },
  { value: 'tac', label: 'TAC' },
]
</script>

<template>
  <div class="flex flex-col gap-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <SegmentedControl v-model="by" :options="BY" label="Break down by" />
      <Button v-if="canExport" size="sm" variant="secondary" :pending="exporter.isPending.value" @click="exportCsv">
        Export CSV
      </Button>
    </div>

    <p v-if="breakdown.isError.value" class="text-xs text-[var(--c-danger-text)]" role="alert">Not available.</p>
    <p v-else-if="!breakdown.data.value" class="py-6 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
    <p v-else-if="rows.length === 0" class="py-6 text-center text-2xs text-[var(--c-text-muted)]">Nothing in this class.</p>

    <div v-else class="overflow-x-auto">
      <table class="w-full text-xs">
        <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
          <tr>
            <th scope="col" class="px-3 py-2 font-medium">{{ BY.find((b) => b.value === by)?.label }}</th>
            <th scope="col" class="px-3 py-2 text-right font-medium" title="A number holding two of these is counted under each. Estimated.">
              Numbers holding it
            </th>
            <th scope="col" class="px-3 py-2 text-right font-medium">Active bindings</th>
            <th scope="col" class="px-3 py-2 text-right font-medium" title="Distinct IMEIs. Estimated.">Handsets</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in rows" :key="r.key" class="border-t">
            <td class="px-3 py-1.5">
              <RouterLink
                v-if="canOpenDevice && r.tac && (by === 'tac' || by === 'model')"
                :to="`/devices/${r.tac}`"
                class="text-[var(--c-accent)] hover:underline"
              >
                {{ r.label }}
              </RouterLink>
              <span v-else :class="r.key.startsWith('(unknown') ? 'text-[var(--c-text-muted)]' : ''">{{ r.label }}</span>
              <span v-if="by === 'tac'" class="tabular ml-2 text-2xs text-[var(--c-text-muted)]">{{ r.key || '—' }}</span>
            </td>
            <td class="tabular px-3 py-1.5 text-right">{{ formatFull(r.numbers) }}</td>
            <td class="tabular px-3 py-1.5 text-right">{{ formatFull(r.bindings) }}</td>
            <td class="tabular px-3 py-1.5 text-right">{{ formatFull(r.handsets) }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="(breakdown.data.value?.total ?? 0) > rows.length" class="border-t px-3 py-2 text-2xs text-[var(--c-text-muted)]">
        The largest {{ rows.length }} of {{ formatFull(breakdown.data.value!.total) }} by active bindings, ordered by numbers. The CSV holds
        up to 500.
      </p>
    </div>
  </div>
</template>
