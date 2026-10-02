<script setup lang="ts">
import { computed, watch } from 'vue'
import { ApiError } from '@/api/client'
import { useRiskEntity } from '@/api/risk'
import { formatDate, formatFull } from '@/lib/format'
import RiskLevelBadge from './RiskLevelBadge.vue'

/**
 * One entity's risk verdicts, exactly as the server wrote them.
 *
 * Shown inside the entity panel to holders of risk.view. Before calibration, or without the lookup
 * permission of the entity's kind, it says so in one line rather than failing the panel around it.
 */
const props = defineProps<{ identifier: string }>()

const entity = useRiskEntity()
watch(
  () => props.identifier,
  (identifier) => entity.mutate(identifier),
  { immediate: true },
)

const data = computed(() => entity.data.value ?? null)
const unavailable = computed(() => {
  const e = entity.error.value
  if (!e) return null
  if (e instanceof ApiError && e.status === 400) return 'Risk measures exist for numbers, SIMs and IMEIs only.'
  return e instanceof ApiError ? (e.problem?.detail ?? e.problem?.title ?? e.message) : 'Risk measures could not be loaded.'
})

const KIND: Record<string, string> = { msisdn: 'number', imsi: 'SIM', imei: 'IMEI' }
const FAMILY: Record<string, string> = { Imei: 'IMEI(s)', Sim: 'SIM(s)', Number: 'number(s)' }

function value(name: string, type: string): string {
  const v = data.value?.values[name]
  if (v === null || v === undefined) return '—'
  if (Array.isArray(v)) return v.join(' ')
  if (type === 'Number' && typeof v === 'number') return formatFull(v)
  if (type === 'Date' && typeof v === 'string') return formatDate(v)
  return String(v)
}
</script>

<template>
  <section class="flex flex-col gap-2" aria-label="Risk signals">
    <div class="flex items-center justify-between gap-2">
      <h3 class="text-xs font-semibold text-[var(--c-text)]">Risk signals</h3>
      <RiskLevelBadge v-if="data?.stored" :level="data.level" compact />
    </div>

    <p v-if="entity.isPending.value" class="text-2xs text-[var(--c-text-muted)]">Loading…</p>
    <p v-else-if="unavailable" class="text-2xs text-[var(--c-text-muted)]">{{ unavailable }}</p>

    <template v-else-if="data">
      <p v-if="!data.stored" class="text-2xs text-[var(--c-text-secondary)]">
        Every measure is below its storage floor as of {{ formatDate(data.asOf) }}: nothing out of line.
      </p>
      <template v-else>
        <p v-if="!data.assessable" class="text-2xs text-[var(--c-text-secondary)]">
          Most of its evidence was set aside as feed defects, so it is not assessed as a risk.
        </p>
        <ul class="flex flex-col gap-1.5">
          <li v-for="r in data.reasons" :key="r.rule" class="flex items-start gap-2 text-2xs text-[var(--c-text-secondary)]">
            <RiskLevelBadge :level="r.level" compact class="mt-px" />
            <span>{{ r.text }}</span>
          </li>
        </ul>
        <dl class="grid grid-cols-[1fr_auto] gap-x-3 gap-y-0.5 text-2xs">
          <template v-for="c in data.columns" :key="c.name">
            <dt v-if="data.values[c.name] !== undefined" class="text-[var(--c-text-muted)]">{{ c.label }}</dt>
            <dd v-if="data.values[c.name] !== undefined" class="tabular text-right text-[var(--c-text)]">{{ value(c.name, c.type) }}</dd>
          </template>
        </dl>
      </template>
      <div v-if="data.pattern === 'SuspiciousPattern'" class="flex flex-col gap-1 rounded-[var(--radius-md)] bg-[var(--c-warning-subtle)] px-3 py-2">
        <RiskLevelBadge level="SuspiciousPattern" compact class="w-fit" />
        <p class="text-2xs text-[var(--c-text)]">
          Risk signals of two different kinds hold among this {{ KIND[data.kind] }} and the entities bound to it in the 30 days to
          {{ formatDate(data.asOf) }}. Suspected, not proven.
        </p>
      </div>
      <p v-if="data.linked.length" class="text-2xs text-[var(--c-text-secondary)]">
        Bound in the 30 days, with stored measures:
        <template v-for="(l, i) in data.linked" :key="l.family">
          {{ i > 0 ? '; ' : '' }}{{ formatFull(l.stored) }} {{ FAMILY[l.family] }}
          <template v-if="l.riskSignals || l.anomalies">
            ({{ [l.riskSignals ? `${formatFull(l.riskSignals)} at risk signal` : '', l.anomalies ? `${formatFull(l.anomalies)} at anomaly` : ''].filter(Boolean).join(', ') }})
          </template>
        </template>.
      </p>
      <p class="text-2xs text-[var(--c-text-muted)]">Rules {{ data.ruleSetVersion }}, as of {{ formatDate(data.asOf) }}.</p>
    </template>
  </section>
</template>
