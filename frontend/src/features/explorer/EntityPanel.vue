<script setup lang="ts">
import { computed, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useEntitySummary, type ExplorerQueryRequest } from '@/api/explorer'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import RiskEntityCard from '@/features/risk/RiskEntityCard.vue'
import { networkAgeText } from '@/api/modelArrivals'
import { formatDate, formatFull, formatImei, formatMsisdn } from '@/lib/format'
import { INITIAL_DUMP } from '@/lib/initial-dump'
import { daysBefore } from './model'
import PlanSummary from './PlanSummary.vue'

/**
 * One number, SIM, handset or TAC, summarised beside the results.
 *
 * The drill-down lives here rather than on its own route: a route would put the identifier in
 * the address bar, and from there into history, bookmarks and every Referer the page sends.
 * The page keeps a back stack instead, so following a number to its SIM and that SIM to its
 * handsets can be retraced.
 *
 * Counts, not lists: what the summary shows is how many, and "List its bindings" turns it into a
 * query in the builder, where the rows can be seen, sorted and narrowed.
 */
const props = withDefaults(
  defineProps<{
    identifier: string
    /** How many entries the back stack holds, this one included. */
    depth: number
    dataThrough: string | null
    canOpenDevice: boolean
    /** False where no query builder is beside the panel to take "List its bindings". */
    canExplore?: boolean
  }>(),
  { canExplore: true },
)

const { can } = useAuth()

const emit = defineEmits<{
  back: []
  close: []
  explore: [request: ExplorerQueryRequest]
  timeline: []
}>()

const summary = useEntitySummary()

watch(
  () => props.identifier,
  (identifier) => summary.mutate(identifier),
  { immediate: true },
)

const data = computed(() => summary.data.value ?? null)

const KIND: Record<string, string> = { msisdn: 'Number', imsi: 'SIM', imei: 'Handset (IMEI)', tac: 'Model (TAC)' }

const title = computed(() => KIND[data.value?.kind ?? ''] ?? 'Identifier')

/**
 * The same thing in a sentence. Not title.toLowerCase(): that printed "this handset (imei)" and
 * "this sim" - the acronyms are names and keep their case.
 */
const NOUN: Record<string, string> = { msisdn: 'number', imsi: 'SIM', imei: 'handset (IMEI)', tac: 'model (TAC)' }
const noun = computed(() => NOUN[data.value?.kind ?? ''] ?? 'identifier')
const shown = computed(() => {
  const d = data.value
  if (!d) return props.identifier
  return d.kind === 'msisdn' ? formatMsisdn(d.identifier) : d.kind === 'imei' ? formatImei(d.identifier) : d.identifier
})

const rows = computed(() => {
  const d = data.value
  if (!d) return []
  return [
    { label: 'Bindings', total: d.bindings, active: d.activeBindings, show: true },
    { label: 'Numbers', total: d.numbers, active: d.activeNumbers, show: d.kind !== 'msisdn' },
    { label: 'SIMs', total: d.sims, active: d.activeSims, show: d.kind !== 'imsi' },
    { label: 'Handsets (IMEIs)', total: d.handsets, active: d.activeHandsets, show: d.kind !== 'imei' },
  ].filter((r) => r.show)
})

function listBindings() {
  const d = data.value
  if (!d) return
  emit(
    'explore',
    d.kind === 'tac'
      ? {
          dataset: 'Bindings',
          where: { logic: 'And', children: [{ field: 'tac', operator: 'Equals', values: [d.identifier] }] },
          groupBy: ['imei'],
          measures: [{ name: 'sims', aggregate: 'CountDistinct', field: 'imsi' }],
          sort: [{ field: 'sims', descending: true }],
        }
      : {
          dataset: 'Bindings',
          where: { logic: 'And', children: [{ field: d.kind, operator: 'Equals', values: [d.identifier] }] },
          sort: [{ field: 'lastChangeDate', descending: true }],
        },
  )
}

function history() {
  const d = data.value
  if (!d || !props.dataThrough || d.kind === 'tac') return
  emit('explore', {
    dataset: 'Events',
    where: {
      logic: 'And',
      children: [
        { field: d.kind, operator: 'Equals', values: [d.identifier] },
        { field: 'date', operator: 'Between', values: [daysBefore(props.dataThrough, 89), props.dataThrough] },
      ],
    },
    sort: [{ field: 'date', descending: true }],
    pageSize: 100,
  })
}
</script>

<template>
  <Card :title="title" :subtitle="shown">
    <template #actions>
      <div class="flex items-center gap-1">
        <Button v-if="depth > 1" size="sm" variant="ghost" @click="emit('back')">← Back</Button>
        <button
          type="button"
          class="grid size-7 place-items-center rounded-[var(--radius-md)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
          aria-label="Close summary"
          @click="emit('close')"
        >
          <span aria-hidden="true" class="text-lg leading-none">&times;</span>
        </button>
      </div>
    </template>

    <AsyncBoundary
      :is-loading="summary.isPending.value"
      :is-error="summary.isError.value"
      :error="summary.error.value"
      :is-empty="data !== null && !data.found"
      :empty-message="`No binding holds this ${noun}.`"
      min-height="10rem"
      @retry="summary.mutate(identifier)"
    >
      <div v-if="data" class="flex flex-col gap-4">
        <dl class="grid grid-cols-2 gap-x-4 gap-y-3">
          <div v-for="row in rows" :key="row.label">
            <dt class="text-2xs text-[var(--c-text-muted)]">{{ row.label }}</dt>
            <dd class="tabular text-lg font-semibold">
              {{ formatFull(row.total) }}
              <span class="text-xs font-normal text-[var(--c-text-secondary)]">
                {{ formatFull(row.active) }} active
              </span>
            </dd>
          </div>
          <div v-if="data.firstSeen">
            <dt class="text-2xs text-[var(--c-text-muted)]">First seen</dt>
            <dd class="tabular text-sm font-medium">
              <template v-if="data.firstSeenIsDumpWindow">
                <span :title="`Seen at some point between ${formatDate(INITIAL_DUMP.start)} and ${formatDate(INITIAL_DUMP.end)}`">
                  Initial dump
                </span>
              </template>
              <template v-else>{{ formatDate(data.firstSeen) }}</template>
              <span
                v-if="data.networkAgeDays !== null"
                class="block text-2xs font-normal text-[var(--c-text-muted)]"
                title="Days since first seen in this data. Not the age of the handset, SIM or number."
              >
                network age {{ networkAgeText(data.networkAgeDays, data.firstSeenIsDumpWindow) }}
              </span>
            </dd>
          </div>
          <div>
            <dt class="text-2xs text-[var(--c-text-muted)]">Last change</dt>
            <dd class="tabular text-sm font-medium">
              <template v-if="data.lastChange">{{ formatDate(data.lastChange) }}</template>
              <span
                v-else
                class="text-[var(--c-text-muted)]"
                title="No daily file has mentioned it: only the initial dump listed it."
              >
                dump only
              </span>
            </dd>
          </div>
        </dl>

        <div v-if="data.tac" class="rounded-[var(--radius-md)] bg-[var(--c-surface-sunken)] px-3 py-2">
          <p class="text-sm font-medium">
            {{ data.model ?? 'Model not in the GSMA database' }}
            <span v-if="data.brand" class="font-normal text-[var(--c-text-secondary)]">· {{ data.brand }}</span>
          </p>
          <p class="text-2xs text-[var(--c-text-muted)]">
            TAC <span class="tabular font-mono">{{ data.tac }}</span>
            <template v-if="canOpenDevice">
              ·
              <RouterLink :to="`/devices/${data.tac}`" class="text-[var(--c-accent)] hover:underline">
                device page
              </RouterLink>
            </template>
          </p>
        </div>

        <div class="flex flex-wrap gap-2">
          <Button v-if="canExplore" size="sm" variant="primary" @click="listBindings">
            {{ data.kind === 'tac' ? 'Its handsets, by SIM count' : 'List its bindings' }}
          </Button>
          <Button v-if="data.kind !== 'tac'" size="sm" @click="emit('timeline')">Timeline</Button>
          <Button v-if="canExplore && data.kind !== 'tac' && dataThrough" size="sm" variant="ghost" @click="history">Events, last 90 days</Button>
        </div>

        <!-- Risk verdicts, for those who may see them. The server judges; this only shows. -->
        <div v-if="data.kind !== 'tac' && can(Permission.RiskView)" class="border-t pt-3">
          <RiskEntityCard :identifier="identifier" />
        </div>

        <!-- What the numbers above are not; said where they are read. -->
        <ul class="flex list-disc flex-col gap-0.5 pl-4 text-2xs text-[var(--c-text-muted)]">
          <li>Active means the feed has not yet removed the binding, not that the SIM is in the handset today.</li>
          <li>Handsets are counted by IMEI, and a dual-SIM phone has two - so this is not a count of phones.</li>
        </ul>

        <details class="text-2xs text-[var(--c-text-muted)]">
          <summary class="cursor-pointer">What this cost</summary>
          <div class="mt-2"><PlanSummary :plan="data.plan" compact /></div>
        </details>
      </div>
    </AsyncBoundary>
  </Card>
</template>
