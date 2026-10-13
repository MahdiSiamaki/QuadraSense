<script setup lang="ts">
import { computed } from 'vue'
import { CHECK_LABELS, RULE_TITLES } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import RiskLevelBadge from '@/features/risk/RiskLevelBadge.vue'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatDateTime, formatFull } from '@/lib/format'
import type { RiskStatusConfig } from './config'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'

/**
 * Whether the risk figures can be read, and as of when: data through, the published run and its
 * staleness, the rule set's version, and optionally the rules themselves - windows, thresholds and
 * what holds a rule at anomaly. Names nobody.
 */
const props = defineProps<WidgetViewProps<RiskStatusConfig>>()

const { status } = useRiskSource(props)
const data = computed(() => status.data.value)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3">
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value"
      :is-error="status.isError.value"
      :error="status.error.value"
      min-height="5rem"
      gap="none"
      @retry="status.refetch()"
    >
      <template v-if="data">
        <div class="flex flex-wrap gap-2 text-xs text-[var(--c-text-secondary)]">
          <span v-if="data.dataThrough" class="tabular inline-flex items-center rounded-full border px-2.5 py-1">
            Data through {{ formatDate(data.dataThrough) }}
          </span>
          <span
            v-if="data.run"
            class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1"
            :title="`Published ${formatDateTime(data.run.publishedAt)}`"
          >
            <span class="size-1.5 shrink-0 rounded-full" :class="data.run.stale ? 'bg-[var(--c-warning)]' : 'bg-[var(--c-success)]'" aria-hidden="true" />
            Risk as of {{ formatDate(data.run.asOf) }}<template v-if="data.run.stale"> (stale)</template>
          </span>
          <span class="tabular inline-flex items-center rounded-full border px-2.5 py-1" title="The rule set's version, quoted in every reason">
            Rules {{ data.ruleSetVersion }}
          </span>
          <span v-if="!data.calibrated" class="inline-flex items-center rounded-full border px-2.5 py-1">
            Not calibrated: nothing is judged
          </span>
        </div>

        <RiskNotice :status="data" />

        <div v-if="instance.config.showRules && data.ready" class="min-h-0 flex-1 overflow-auto overscroll-contain">
          <table class="w-full text-xs">
            <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="px-2 py-1.5 font-medium">Rule</th>
                <th scope="col" class="px-2 py-1.5 font-medium">Window</th>
                <th scope="col" class="px-2 py-1.5 text-right font-medium">More than</th>
                <th scope="col" class="px-2 py-1.5 font-medium">At most</th>
                <th scope="col" class="px-2 py-1.5 font-medium">Held at anomaly by</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="r in data.rules" :key="r.rule" class="border-t align-top">
                <td class="px-2 py-1.5">
                  <span class="font-medium text-[var(--c-text)]">{{ RULE_TITLES[r.rule] ?? r.rule }}</span>
                  <span class="block text-2xs text-[var(--c-text-muted)]">{{ r.unit }}</span>
                </td>
                <td class="tabular px-2 py-1.5 whitespace-nowrap">
                  <template v-if="r.windowFrom">
                    {{ r.windowDays }} days to {{ formatDate(r.windowTo) }}
                    <span class="block text-2xs text-[var(--c-text-muted)]">{{ r.daysWithData }} of {{ r.windowDays }} with data</span>
                  </template>
                  <template v-else>All time</template>
                </td>
                <td class="tabular px-2 py-1.5 text-right">
                  <template v-if="r.threshold !== null">
                    {{ formatFull(r.threshold) }}<template v-if="r.tacsThreshold !== null"> / {{ formatFull(r.tacsThreshold) }} TACs</template>
                  </template>
                  <span v-else class="text-[var(--c-text-muted)]">Not calibrated</span>
                </td>
                <td class="px-2 py-1.5"><RiskLevelBadge :level="r.ceiling" compact /></td>
                <td class="px-2 py-1.5 text-2xs text-[var(--c-text-secondary)]">
                  <template v-if="r.cappingChecks.length">
                    {{ r.cappingChecks.map((c) => CHECK_LABELS[c] ?? c).join(', ') }}<template v-if="r.flaggedDays.length">
                      — {{ r.flaggedDays.length }} flagged {{ r.flaggedDays.length === 1 ? 'day' : 'days' }} in the window</template>
                  </template>
                  <template v-else>—</template>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </AsyncBoundary>
    <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      Observed, not proven: a level states how strong the evidence is, never that anyone did anything.
    </p>
  </div>
</template>
