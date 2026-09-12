<script setup lang="ts">
import { computed } from 'vue'
import { useKpiSummary, type DashboardFilters } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { formatFull, formatPercent } from '@/lib/format'

const props = defineProps<{ filters: DashboardFilters }>()
const kpi = useKpiSummary(() => props.filters)

/**
 * Computed from exact counts the API returns, never from the coverage percentage.
 * Deriving it from a figure rounded to three decimals put this ~30,000 rows off the
 * true 255,676 - a rounding artifact on the screen whose job is accuracy.
 */
const unregisteredTac = computed(() => {
  const k = kpi.data.value
  if (!k?.activeBindings) return null
  return k.activeBindings - k.tacMatchedBindings - k.unknownDeviceBindings - k.malformedImeiBindings
})
</script>

<template>
  <AsyncBoundary
    :is-loading="kpi.isPending.value"
    :is-error="kpi.isError.value"
    :error="kpi.error.value"
    min-height="8rem"
    @retry="kpi.refetch()"
  >
    <div class="space-y-4">
      <!-- The four categories sum exactly to the total. Showing them separately is the
           point: only one of them is a defect. -->
      <dl class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Enriched from GSMA</dt>
          <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular text-[var(--c-success)]">
            {{ formatFull(kpi.data.value?.tacMatchedBindings ?? 0) }}
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            {{ formatPercent(kpi.data.value?.tacCoveragePercent ?? 0, 2) }} of active bindings.
          </dd>
        </div>
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">
            Unknown device (<code class="font-[var(--font-mono)]">000000</code>)
          </dt>
          <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular text-[var(--c-warning)]">
            {{ formatFull(kpi.data.value?.unknownDeviceBindings ?? 0) }}
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            A sentinel the source uses for "device not known". Expected, not a defect.
          </dd>
        </div>
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Malformed IMEI</dt>
          <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular text-[var(--c-danger)]">
            {{ formatFull(kpi.data.value?.malformedImeiBindings ?? 0) }}
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            Numeric but not 14 digits. <strong>The only genuine defect here.</strong>
          </dd>
        </div>
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Unregistered TAC</dt>
          <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular">
            {{ formatFull(unregisteredTac ?? 0) }}
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            Well-formed IMEI whose TAC is absent from the GSMA database.
          </dd>
        </div>
      </dl>

      <p class="border-t pt-3 text-[var(--text-xs)] text-[var(--c-text-muted)]">
        The four figures sum to
        <span class="tabular font-medium text-[var(--c-text-secondary)]">
          {{ formatFull(kpi.data.value?.activeBindings ?? 0) }}
        </span>
        active bindings.
      </p>
    </div>
  </AsyncBoundary>
</template>
