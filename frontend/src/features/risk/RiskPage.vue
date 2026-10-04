<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { CHECK_LABELS, RULE_TITLES, useRiskDaily, useRiskOverview, useRiskStatus, type RiskListKind, type RiskRule } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Card from '@/design-system/Card.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import EntityPanel from '@/features/explorer/EntityPanel.vue'
import { segment } from '@/features/explorer/ui'
import TimelineView from '@/features/timeline/TimelineView.vue'
import { formatDate, formatDateTime, formatFull } from '@/lib/format'
import RiskLevelBadge from './RiskLevelBadge.vue'
import RiskListView from './RiskListView.vue'
import RiskDailyChart from './RiskDailyChart.vue'

/**
 * Risk signals: who is out of line, by which rule, on how much clean evidence.
 *
 * The page decides nothing. Levels and reasons come from the server, judged with the configured rule
 * set against measures the import worker computed; the page lists them, lets a person open one, and
 * says plainly what the data cannot show. Identifiers never enter the URL: lists are POSTed and
 * drill-down is a panel.
 */
const { can } = useAuth()
const status = useRiskStatus()
const data = computed(() => status.data.value ?? null)
const overview = useRiskOverview(() => data.value?.ready === true)
const daily = useRiskDaily(() => data.value?.ready === true)

type Tab = 'overview' | 'imei' | 'sim' | 'number' | 'rules'

const LISTS: Record<Exclude<Tab, 'overview' | 'rules'>, { label: string; lists: RiskListKind[]; permission: string; kind: string }> = {
  imei: { label: 'Shared IMEIs', lists: ['ImeiWindow', 'ImeiLifetime'], permission: Permission.LookupImei, kind: 'IMEIs' },
  sim: { label: 'SIMs on many IMEIs', lists: ['Sims'], permission: Permission.LookupImsi, kind: 'SIMs' },
  number: { label: 'SIM changes', lists: ['Numbers'], permission: Permission.LookupSubscriber, kind: 'numbers' },
}

const tab = ref<Tab>('overview')
const focus = ref<string | null>(null)

const tabs = computed(() => [
  { id: 'overview' as Tab, label: 'Overview', disabled: null as string | null },
  ...(Object.entries(LISTS) as [Exclude<Tab, 'overview' | 'rules'>, (typeof LISTS)['imei']][]).map(([id, l]) => ({
    id: id as Tab,
    label: l.label,
    disabled: !can(l.permission as never)
      ? `Lists ${l.kind} by identifier, which needs the "${l.permission}" permission.`
      : data.value && !data.value.ready
        ? data.value.notReady
        : null,
  })),
  { id: 'rules' as Tab, label: 'Rules', disabled: null as string | null },
])

function rulesOf(t: Tab): RiskRule[] {
  if (t === 'overview' || t === 'rules') return []
  return (data.value?.rules ?? []).filter((r) => LISTS[t].lists.includes(r.list))
}

function open(rule: string) {
  const r = data.value?.rules.find((x) => x.rule === rule)
  if (!r) return
  const t = (Object.keys(LISTS) as Exclude<Tab, 'overview' | 'rules'>[]).find((k) => LISTS[k].lists.includes(r.list))
  if (!t || tabs.value.find((x) => x.id === t)?.disabled) return
  focus.value = rule
  tab.value = t
}

const counts = computed(() => new Map((overview.data.value?.rules ?? []).map((c) => [c.rule, c])))

// ------------------------------------------------------------------ drill-down

const stack = ref<string[]>([])
const current = computed(() => stack.value[stack.value.length - 1] ?? null)
const timelineOpen = ref(false)
watch(current, (identifier) => {
  if (identifier === null) timelineOpen.value = false
})

function drill(identifier: string) {
  if (current.value !== identifier) stack.value.push(identifier)
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Risk signals</h1>
        <p class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          Numbers, SIMs and IMEIs whose observed changes are out of line, judged against thresholds cut from the measured
          data. Observed, not proven: a level states how strong the evidence is, never that anyone did anything.
        </p>
      </div>

      <div v-if="data" class="flex flex-wrap gap-2 text-xs text-[var(--c-text-secondary)]">
        <span v-if="data.dataThrough" class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1">
          Data through {{ formatDate(data.dataThrough) }}
        </span>
        <span
          v-if="data.run"
          class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1"
          :title="`Published ${formatDateTime(data.run.publishedAt)}`"
        >
          <span
            class="size-1.5 rounded-full"
            :class="data.run.stale ? 'bg-[var(--c-warning)]' : 'bg-[var(--c-success)]'"
            aria-hidden="true"
          />
          Risk as of {{ formatDate(data.run.asOf) }}
        </span>
        <span class="tabular inline-flex items-center rounded-full border px-2.5 py-1" title="The rule set's version, quoted in every reason">
          Rules {{ data.ruleSetVersion }}
        </span>
      </div>
    </header>

    <AsyncBoundary
      :is-loading="status.isPending.value"
      :is-error="status.isError.value"
      :error="status.error.value"
      min-height="12rem"
      @retry="status.refetch()"
    >
      <template v-if="data">
        <p
          v-if="data.notReady"
          class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-sm text-[var(--c-text-secondary)]"
          role="status"
        >
          {{ data.notReady }}
        </p>
        <p
          v-if="data.run?.stale"
          class="rounded-[var(--radius-md)] border border-[var(--c-warning)] bg-[var(--c-warning-subtle)] px-4 py-3 text-sm text-[var(--c-text)]"
          role="status"
        >
          {{ data.run.stale }} Until then, everything here is as of {{ formatDate(data.run.asOf) }}.
        </p>

        <div class="inline-flex w-fit flex-wrap overflow-hidden rounded-[var(--radius-md)] border" role="tablist" aria-label="Risk signals">
          <button
            v-for="t in tabs"
            :key="t.id"
            type="button"
            role="tab"
            :aria-selected="tab === t.id"
            :aria-disabled="t.disabled !== null"
            :title="t.disabled ?? ''"
            :class="[segment(tab === t.id), t.disabled ? 'cursor-not-allowed opacity-50' : '']"
            @click="t.disabled ? null : ((focus = null), (tab = t.id))"
          >
            {{ t.label }}
          </button>
        </div>

        <div class="grid items-start gap-5" :class="current ? 'xl:grid-cols-[minmax(0,1fr)_24rem]' : ''">
          <div class="min-w-0">
            <!-- Overview: counts only, naming nobody. -->
            <div v-if="tab === 'overview'" class="flex flex-col gap-3">
            <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <Card v-for="r in data.rules" :key="r.rule" :title="RULE_TITLES[r.rule] ?? r.rule">
                <div class="flex flex-col gap-2 text-xs">
                  <template v-if="counts.get(r.rule)">
                    <p class="tabular text-2xl font-semibold text-[var(--c-text)]">{{ formatFull(counts.get(r.rule)!.listed) }}</p>
                    <div class="flex flex-wrap items-center gap-2">
                      <RiskLevelBadge :level="counts.get(r.rule)!.level" :capped="counts.get(r.rule)!.capped" compact />
                      <span v-if="counts.get(r.rule)!.dataQuality > 0" class="tabular text-2xs text-[var(--c-text-muted)]">
                        + {{ formatFull(counts.get(r.rule)!.dataQuality) }} data quality (not risk)
                      </span>
                    </div>
                    <button type="button" class="w-fit text-xs text-[var(--c-accent)] hover:underline" @click="open(r.rule)">
                      Open list
                    </button>
                  </template>
                  <p v-else class="text-[var(--c-text-muted)]">
                    {{
                      r.threshold === null
                        ? 'Not calibrated: nothing is judged.'
                        : overview.isPending.value || overview.isFetching.value
                          ? 'Loading…'
                          : 'Not available.'
                    }}
                  </p>
                </div>
              </Card>
            </div>
            <Card
              title="SIM changes per day"
              subtitle="Numbers whose SIM changed: counted, and set aside as feed defects. Shaded days were flagged by the feed-quality monitor."
            >
              <RiskDailyChart v-if="daily.data.value" :days="daily.data.value.days" />
              <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">
                {{ daily.isError.value ? 'Not available.' : 'Loading…' }}
              </p>
            </Card>
            </div>

            <RiskListView
              v-else-if="tab !== 'rules'"
              :key="`${tab}:${focus ?? ''}`"
              :rules="rulesOf(tab)"
              :initial="focus"
              :device-types="data.deviceTypes"
              :can-export="can(Permission.DataExport)"
              @drill="drill"
            />

            <!-- Rules: read-only. -->
            <Card v-else flush>
              <div class="overflow-x-auto">
                <table class="w-full text-xs">
                  <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
                    <tr>
                      <th scope="col" class="px-3 py-2 font-medium">Rule</th>
                      <th scope="col" class="px-3 py-2 font-medium">Window</th>
                      <th scope="col" class="px-3 py-2 text-right font-medium">Threshold (more than)</th>
                      <th scope="col" class="px-3 py-2 text-right font-medium">Stored from</th>
                      <th scope="col" class="px-3 py-2 font-medium">At most</th>
                      <th scope="col" class="px-3 py-2 font-medium">Held at anomaly by</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="r in data.rules" :key="r.rule" class="border-t align-top">
                      <td class="px-3 py-2">
                        <p class="font-medium text-[var(--c-text)]">{{ RULE_TITLES[r.rule] ?? r.rule }}</p>
                        <p class="text-2xs text-[var(--c-text-muted)]">{{ r.unit }} {{ r.what }}</p>
                      </td>
                      <td class="tabular px-3 py-2 whitespace-nowrap">
                        <template v-if="r.windowFrom">
                          {{ r.windowDays }} days, {{ formatDate(r.windowFrom) }} – {{ formatDate(r.windowTo) }}<br />
                          <span class="text-2xs text-[var(--c-text-muted)]">{{ r.daysWithData }} of {{ r.windowDays }} days have data</span>
                        </template>
                        <template v-else>All time</template>
                      </td>
                      <td class="tabular px-3 py-2 text-right">
                        <template v-if="r.threshold !== null">
                          {{ formatFull(r.threshold) }}<template v-if="r.tacsThreshold !== null"> / {{ formatFull(r.tacsThreshold) }} TACs</template>
                        </template>
                        <span v-else class="text-[var(--c-text-muted)]">Not calibrated</span>
                      </td>
                      <td class="tabular px-3 py-2 text-right">{{ r.floor }}</td>
                      <td class="px-3 py-2"><RiskLevelBadge :level="r.ceiling" compact /></td>
                      <td class="px-3 py-2 text-2xs text-[var(--c-text-secondary)]">
                        <template v-if="r.cappingChecks.length">
                          {{ r.cappingChecks.map((c) => CHECK_LABELS[c] ?? c).join(', ') }}
                          <template v-if="r.flaggedDays.length">
                            — {{ r.flaggedDays.length }} flagged day(s) in this window
                          </template>
                        </template>
                        <template v-else>—</template>
                      </td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <p class="border-t px-4 py-2 text-2xs text-[var(--c-text-muted)]">
                Thresholds are set in the server's Risk configuration and recorded with their measurements in ADR-014.
                A value is out of line when it is more than its threshold. Above
                {{ data.maxDefectShare === null ? 'every' : `${Math.round(data.maxDefectShare * 100)}% of` }} an entity's adds set
                aside as feed defects, it is listed under data quality only.
              </p>
            </Card>
          </div>

          <div v-if="current" class="flex flex-col gap-4 xl:sticky xl:top-20">
            <EntityPanel
              :key="current"
              :identifier="current"
              :depth="stack.length"
              :data-through="data.dataThrough"
              :can-open-device="can(Permission.DeviceView)"
              :can-explore="false"
              @back="stack.pop()"
              @close="stack = []"
              @timeline="timelineOpen = true"
            />
          </div>
        </div>

        <p class="text-2xs text-[var(--c-text-muted)]">
          Observed, not proven. This data has no calls, traffic, location or time of day. “Not removed” means not yet removed
          by the feed, not that a SIM is in a handset today.
        </p>
      </template>
    </AsyncBoundary>

    <TimelineView v-if="timelineOpen && current" :identifier="current" @drill="drill" @close="timelineOpen = false" />
  </div>
</template>
