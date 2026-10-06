<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import { CHECK_LABELS, type QualitySignal, useFeedQuality, useQualitySignals } from '@/api/quality'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import Card from '@/design-system/Card.vue'
import KpiCard from '@/design-system/KpiCard.vue'
import { formatDate, formatFull, formatPercent, formatRelative } from '@/lib/format'

/**
 * Data quality: facts about the feed and its identifiers, over all of the data.
 *
 * Two sources. The categories are counted with each measures run (analytics migration 026): current
 * state for identifier faults, the binding history for sequences and how long the feed held a binding.
 * The day-by-day judgement is each file against the ordinary days (/quality/days). Neither is risk:
 * nothing on this page says anything about a subscriber.
 */
const signals = useQualitySignals()
const days = useFeedQuality()

const data = computed(() => signals.data.value ?? null)
const base = (code: string) => data.value?.bases.find((b) => b.code === code) ?? null
const group = (g: QualitySignal['group']) => data.value?.categories.filter((c) => c.group === g) ?? []

const periodCodes = [
  'period_same_day',
  'period_1_day',
  'period_2_7_days',
  'period_8_30_days',
  'period_31_90_days',
  'period_over_90_days',
]
const periodLabels: Record<string, string> = {
  period_same_day: 'Same day',
  period_1_day: '1 day',
  period_2_7_days: '2–7 days',
  period_8_30_days: '8–30 days',
  period_31_90_days: '31–90 days',
  period_over_90_days: 'Over 90 days',
}
const periods = computed(() => {
  const rows = periodCodes.map((code) => data.value?.categories.find((c) => c.code === code)).filter((c) => !!c)
  const total = rows.reduce((sum, c) => sum + c.periods, 0)
  return {
    total,
    chart: rows.map((c) => ({
      key: periodLabels[c.code] ?? c.label,
      count: c.periods,
      percent: total ? (c.periods / total) * 100 : 0,
    })),
    shortShare: total
      ? (rows.filter((c) => c.code === 'period_same_day' || c.code === 'period_1_day').reduce((s, c) => s + c.periods, 0) / total) * 100
      : null,
  }
})
const lifetimeOthers = computed(() => group('lifetimes').filter((c) => !periodCodes.includes(c.code)))

const flagged = computed(() => [...(days.data.value?.days ?? [])].filter((d) => d.flagged).reverse())
const measured = computed(() => days.data.value?.days.length ?? 0)

function share(c: QualitySignal): string {
  return c.share === null ? '—' : formatPercent(c.share * 100, c.share < 0.001 ? 3 : 2)
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Data quality</h1>
        <p class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          Facts about the feed and its identifiers, counted over all of the data: what is missing or malformed, what the
          feed's sequences contradict, and how long it holds a binding. Nothing here is evidence about a subscriber.
        </p>
      </div>
      <p
        v-if="data?.available"
        class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs text-[var(--c-text-secondary)]"
        :title="`Measures run ${data.runId}, published ${formatRelative(data.publishedAt)}`"
      >
        Data through {{ formatDate(data.asOf) }} · counted {{ formatRelative(data.publishedAt) }}
      </p>
    </header>

    <AsyncBoundary
      :is-loading="signals.isPending.value"
      :is-error="signals.isError.value"
      :error="signals.error.value"
      min-height="10rem"
      @retry="signals.refetch()"
    >
      <p
        v-if="data && !data.available"
        class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-sm text-[var(--c-text-secondary)]"
        role="status"
      >
        {{ data.reason }}
      </p>

      <template v-else-if="data">
        <div class="grid gap-3 sm:grid-cols-3">
          <KpiCard
            label="Bindings in current state"
            :value="base('all')?.bindings"
            identifier="MSISDN · IMSI · IMEI"
            definition="Every binding current state knows, held or removed. Identifier faults are shares of this."
          />
          <KpiCard
            label="Bindings held now"
            :value="base('active')?.bindings"
            identifier="MSISDN · IMSI · IMEI"
            definition="Bindings whose last event is an add, or that came from the initial dump and were never removed."
          />
          <KpiCard
            label="Bindings in the history"
            :value="base('history_all')?.bindings"
            identifier="MSISDN · IMSI · IMEI"
            definition="Bindings a daily file has mentioned since 2026-01-26. Sequences and period lengths are shares of this."
          />
        </div>

        <Card title="Identifiers" subtitle="Current state: bindings whose identifiers are missing, malformed or unknown">
          <div class="overflow-x-auto">
            <table class="w-full text-xs">
              <thead class="text-left text-2xs text-[var(--c-text-muted)]">
                <tr>
                  <th scope="col" class="py-2 pr-3 font-medium">Category</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Bindings</th>
                  <th scope="col" class="px-3 py-2 font-medium">Share of all bindings</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Numbers</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">SIMs, est.</th>
                  <th scope="col" class="py-2 pl-3 text-right font-medium">IMEIs, est.</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="c in group('identifiers')" :key="c.code" class="border-t align-top">
                  <th scope="row" class="py-2 pr-3 text-left font-normal">
                    <span class="font-medium text-[var(--c-text-primary)]">{{ c.label }}</span>
                    <span class="block max-w-md text-2xs text-[var(--c-text-muted)]">{{ c.meaning }}</span>
                  </th>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(c.bindings) }}</td>
                  <td class="px-3 py-2">
                    <span class="flex items-center gap-2">
                      <span class="h-1.5 w-24 overflow-hidden rounded-full bg-[var(--c-surface-sunken)]" aria-hidden="true">
                        <span class="block h-full rounded-full bg-[var(--c-accent)]" :style="{ width: `${Math.min(100, (c.share ?? 0) * 100)}%` }" />
                      </span>
                      <span class="tabular">{{ share(c) }}</span>
                    </span>
                  </td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(c.numbers) }}</td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(c.sims) }}</td>
                  <td class="tabular py-2 pl-3 text-right">{{ formatFull(c.imeis) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="mt-2 text-2xs text-[var(--c-text-muted)]">
            A binding can be in more than one category: an IMSI and an IMEI can both be wrong. SIMs and IMEIs are estimated
            (about 0.5%); bindings and numbers are exact.
          </p>
        </Card>

        <div class="grid gap-5 lg:grid-cols-2">
          <Card title="Sequences" subtitle="Events the feed's own history contradicts">
            <table class="w-full text-xs">
              <thead class="text-left text-2xs text-[var(--c-text-muted)]">
                <tr>
                  <th scope="col" class="py-2 pr-3 font-medium">Category</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Bindings</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Times</th>
                  <th scope="col" class="py-2 pl-3 text-right font-medium">Share of history</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="c in group('sequences')" :key="c.code" class="border-t align-top">
                  <th scope="row" class="py-2 pr-3 text-left font-normal">
                    <span class="font-medium text-[var(--c-text-primary)]">{{ c.label }}</span>
                    <span class="block text-2xs text-[var(--c-text-muted)]">{{ c.meaning }}</span>
                  </th>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(c.bindings) }}</td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(c.periods) }}</td>
                  <td class="tabular py-2 pl-3 text-right">{{ share(c) }}</td>
                </tr>
              </tbody>
            </table>
            <p class="mt-2 text-2xs text-[var(--c-text-muted)]">
              The fold applies these as the feed sent them - an add of a held binding changes nothing, a remove of one not
              held removes nothing - so they are counted, never treated as errors.
            </p>
          </Card>

          <Card title="How long the feed held a binding" subtitle="Add-to-remove periods, by length">
            <BarChart :data="periods.chart" :horizontal="false" height="14rem" unit="periods" />
            <p v-if="periods.shortShare !== null" class="mt-2 text-2xs text-[var(--c-text-muted)]">
              {{ formatPercent(periods.shortShare, 0) }} of {{ formatFull(periods.total) }} periods ended the day they began or
              the day after. That is how this feed behaves, so a short period is shown here and never flagged.
            </p>
            <dl class="mt-3 grid gap-2 border-t pt-3 text-xs sm:grid-cols-2">
              <div v-for="c in lifetimeOthers" :key="c.code">
                <dt class="text-[var(--c-text-secondary)]" :title="c.meaning">{{ c.label }}</dt>
                <dd class="tabular font-medium">
                  {{ formatFull(c.bindings) }}
                  <span class="font-normal text-[var(--c-text-muted)]">· {{ share(c) }} of {{ c.base === 'active' ? 'held' : 'history' }}</span>
                </dd>
              </div>
            </dl>
          </Card>
        </div>
      </template>
    </AsyncBoundary>

    <Card title="Day by day" subtitle="Each daily file judged against the ordinary days">
      <AsyncBoundary
        :is-loading="days.isPending.value"
        :is-error="days.isError.value"
        :error="days.error.value"
        min-height="6rem"
        gap="md"
        @retry="days.refetch()"
      >
        <p class="text-xs text-[var(--c-text-secondary)]">
          {{ formatFull(flagged.length) }} of {{ formatFull(measured) }} measured days were out of line. A day is flagged when a
          rate is above {{ days.data.value?.reference.multiplier }}× the median of the reference days
          ({{ formatDate(days.data.value?.reference.from ?? null) }} – {{ formatDate(days.data.value?.reference.to ?? null) }}).
        </p>
        <div v-if="flagged.length" class="max-h-96 overflow-auto">
          <table class="w-full text-xs">
            <thead class="sticky top-0 bg-[var(--c-surface)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="py-2 pr-3 font-medium">Day</th>
                <th scope="col" class="px-3 py-2 text-right font-medium">Rows</th>
                <th scope="col" class="py-2 pl-3 font-medium">Out of line</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="d in flagged" :key="d.date" class="border-t align-top">
                <td class="tabular py-2 pr-3 whitespace-nowrap">{{ formatDate(d.date) }}</td>
                <td class="tabular px-3 py-2 text-right">{{ formatFull(d.rows) }}</td>
                <td class="py-2 pl-3">
                  <span v-for="f in d.findings.filter((x) => x.flagged)" :key="f.check" class="block">
                    {{ CHECK_LABELS[f.check] }}:
                    <span class="tabular font-medium">{{ formatPercent(f.rate * 100, 2) }}</span>
                    <span class="text-[var(--c-text-muted)]">
                      (ordinary {{ f.referenceMedian === null ? '—' : formatPercent(f.referenceMedian * 100, 2) }})</span
                    >
                  </span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p class="text-2xs text-[var(--c-text-muted)]">
          The files themselves are in the <RouterLink to="/imports" class="text-[var(--c-accent)] hover:underline">Import Center</RouterLink>.
        </p>
      </AsyncBoundary>
    </Card>
  </div>
</template>
