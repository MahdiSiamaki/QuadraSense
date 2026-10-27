<script setup lang="ts">
import { ref, watch } from 'vue'
import { usePlanningNumbers, type PlanningFilter, type PlanningNumberPage } from '@/api/planning'
import Pagination from '@/design-system/Pagination.vue'
import { formatFull } from '@/lib/format'

/**
 * A page of the numbers in one class. POSTed and audited - with its class and filters, never the
 * numbers - and masked unless the caller may reveal identifiers.
 *
 * A page, not a list to take away: the first 10,000 rows can be paged through here. A whole class as
 * a campaign list leaves the system only through the controlled export (CampaignRequestDialog), with
 * its own permission, a recorded reason and a second person's approval (the product owner's decision
 * of 2026-10-21).
 */
const props = defineProps<{
  view: 'sunset' | 'esim'
  planningClass: string
  reason?: string | null
  run: number | null
  filter: PlanningFilter
}>()

const PAGE_SIZE = 50
const page = ref(1)
const result = ref<PlanningNumberPage | null>(null)
const load = usePlanningNumbers()

async function fetchPage(p: number) {
  page.value = p
  result.value = await load.mutateAsync({
    view: props.view,
    class: props.planningClass,
    reason: props.reason ?? null,
    runId: props.run,
    page: p,
    pageSize: PAGE_SIZE,
    ...props.filter,
  })
}

watch(() => [props.view, props.planningClass, props.reason, props.run, props.filter], () => void fetchPage(1), { immediate: true, deep: true })
</script>

<template>
  <div class="flex flex-col gap-2">
    <p v-if="load.isError.value" class="text-xs text-[var(--c-danger-text)]" role="alert">
      {{ (load.error.value as Error | null)?.message ?? 'Not available.' }}
    </p>

    <template v-else-if="result">
      <p class="text-2xs text-[var(--c-text-muted)]">
        {{ formatFull(result.total) }} number{{ result.total === 1 ? '' : 's' }} in this class<template v-if="result.masked">,
        masked</template>. Pages reach the first 10,000; the whole class, as a campaign list, is a request with a
        reason and a second person's approval.
      </p>
      <div class="overflow-x-auto">
        <table class="w-full text-xs">
          <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-3 py-2 font-medium">Number</th>
              <th v-if="planningClass === 'not-assessable'" scope="col" class="px-3 py-2 font-medium">Why not assessable</th>
              <th scope="col" class="px-3 py-2 text-right font-medium">Handsets</th>
              <th scope="col" class="px-3 py-2 text-right font-medium">Active bindings</th>
              <th scope="col" class="px-3 py-2 font-medium">Models held</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in result.rows" :key="r.msisdn" class="border-t align-top">
              <td class="tabular px-3 py-1.5 whitespace-nowrap">{{ r.msisdn }}</td>
              <td v-if="planningClass === 'not-assessable'" class="px-3 py-1.5 text-[var(--c-text-secondary)]">{{ r.reason ?? '—' }}</td>
              <td class="tabular px-3 py-1.5 text-right">{{ r.handsets }}</td>
              <td class="tabular px-3 py-1.5 text-right">{{ r.bindings }}</td>
              <td class="px-3 py-1.5 text-[var(--c-text-secondary)]">{{ r.models.length ? r.models.join(', ') : '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <Pagination
        :page="page"
        :page-size="PAGE_SIZE"
        :total="Math.min(result.total, 10_000)"
        :loading="load.isPending.value"
        @update:page="fetchPage"
      />
    </template>

    <p v-else class="py-6 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
  </div>
</template>
