<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useDeviceFacets } from '@/api/devices'
import { networkAgeText, useModelArrivals, useModelArrivalStatus, useNewModels } from '@/api/modelArrivals'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Card from '@/design-system/Card.vue'
import Pagination from '@/design-system/Pagination.vue'
import { control, miniLabel, segment } from '@/features/explorer/ui'
import { formatDate, formatFull } from '@/lib/format'
import ModelArrivalsChart from './ModelArrivalsChart.vue'

/**
 * New models: device models the feed named for the first time.
 *
 * First seen means the first daily file that named one of the model's handsets - not a launch date,
 * and not anything about the handsets themselves. Models the initial dump listed were seen before the
 * data starts and never appear here; the first weeks after 2026-01-26 also fill in models the dump's
 * month happened to miss, so an early first-seen date says less than a late one.
 */
const status = useModelArrivalStatus()
const ready = computed(() => status.data.value?.ready === true)
const facets = useDeviceFacets()

const grain = ref<'month' | 'week'>('month')
const arrivals = useModelArrivals(grain, ready)

type Preset = '7' | '30' | '90' | 'custom'
const preset = ref<Preset>('30')
const from = ref('')
const to = ref('')
const brand = ref('')
const deviceType = ref('')
const knownOnly = ref(true)
const page = ref(1)
const pageSize = 50

function daysBefore(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() - days)
  return d.toISOString().slice(0, 10)
}

const through = computed(() => status.data.value?.dataThrough ?? null)
const range = computed(() => {
  if (!through.value) return { from: undefined, to: undefined }
  if (preset.value === 'custom') return { from: from.value || undefined, to: to.value || undefined }
  return { from: daysBefore(through.value, Number(preset.value) - 1), to: through.value }
})

const query = computed(() => ({
  ...range.value,
  brand: brand.value.trim() || undefined,
  deviceType: deviceType.value || undefined,
  knownOnly: knownOnly.value,
  page: page.value,
  pageSize,
}))

const list = useNewModels(query, ready)
watch([preset, from, to, brand, deviceType, knownOnly], () => (page.value = 1))

const result = computed(() => list.data.value ?? null)
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <RouterLink to="/devices" class="text-xs text-[var(--c-accent)] hover:underline">← Devices</RouterLink>
        <h1 class="mt-1 text-xl font-semibold tracking-tight">New models</h1>
        <p class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          Device models the daily files named for the first time. First seen is when the feed first named one of the model's
          handsets - not a launch date. Network age is the days since then, in this data; it is not the age of any handset.
        </p>
      </div>
      <p
        v-if="through"
        class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs text-[var(--c-text-secondary)]"
      >
        Data through {{ formatDate(through) }}
      </p>
    </header>

    <AsyncBoundary
      :is-loading="status.isPending.value"
      :is-error="status.isError.value"
      :error="status.error.value"
      min-height="10rem"
      @retry="status.refetch()"
    >
      <p
        v-if="status.data.value && !status.data.value.ready"
        class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-sm text-[var(--c-text-secondary)]"
        role="status"
      >
        {{ status.data.value.notReady }}
      </p>

      <template v-else>
        <Card title="Models first seen" :subtitle="`Since the first daily file, ${formatDate(status.data.value?.dataStart ?? null)}`">
          <template #actions>
            <div class="inline-flex overflow-hidden rounded-[var(--radius-md)] border" role="radiogroup" aria-label="Period">
              <button type="button" role="radio" :aria-checked="grain === 'month'" :class="segment(grain === 'month')" @click="grain = 'month'">
                Month
              </button>
              <button type="button" role="radio" :aria-checked="grain === 'week'" :class="segment(grain === 'week')" @click="grain = 'week'">
                Week
              </button>
            </div>
          </template>
          <ModelArrivalsChart v-if="arrivals.data.value" :data="arrivals.data.value" />
          <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
          <p class="mt-2 text-2xs text-[var(--c-text-muted)]">
            The first weeks after {{ formatDate(status.data.value?.dataStart ?? null) }} are mostly models the initial dump's month
            happened to miss, not new arrivals. From September, TACs not in GSMA include no shifted IMEIs: only countable IMEIs are
            counted.
          </p>
        </Card>

        <div class="flex flex-wrap items-end gap-x-6 gap-y-3">
          <div>
            <span :class="miniLabel">First seen</span>
            <div class="mt-1 inline-flex overflow-hidden rounded-[var(--radius-md)] border" role="radiogroup" aria-label="First seen">
              <button
                v-for="p in [{ id: '7', label: 'Last 7 days' }, { id: '30', label: 'Last 30 days' }, { id: '90', label: 'Last 90 days' }, { id: 'custom', label: 'Custom' }]"
                :key="p.id"
                type="button"
                role="radio"
                :aria-checked="preset === p.id"
                :class="segment(preset === p.id)"
                @click="preset = p.id as Preset"
              >
                {{ p.label }}
              </button>
            </div>
          </div>
          <template v-if="preset === 'custom'">
            <label>
              <span :class="miniLabel">From</span>
              <input v-model="from" type="date" :class="[control, 'mt-1']" :max="through ?? undefined" />
            </label>
            <label>
              <span :class="miniLabel">To</span>
              <input v-model="to" type="date" :class="[control, 'mt-1']" :max="through ?? undefined" />
            </label>
          </template>
          <label>
            <span :class="miniLabel">Brand</span>
            <input v-model="brand" type="search" placeholder="Any brand" :class="[control, 'mt-1 w-40']" maxlength="100" />
          </label>
          <!-- Capped at the row: the longest device type would otherwise widen a phone's page. -->
          <label class="max-w-full min-w-0">
            <span :class="miniLabel">Device type</span>
            <select v-model="deviceType" :class="[control, 'mt-1 w-full']">
              <option value="">All device types</option>
              <option v-for="t in facets.data.value?.deviceTypes ?? []" :key="t.value" :value="t.value">{{ t.value }}</option>
            </select>
          </label>
          <label class="inline-flex items-center gap-2 text-xs text-[var(--c-text-secondary)]">
            <input v-model="knownOnly" type="checkbox" />
            Known to GSMA only
          </label>
        </div>

        <Card flush>
          <div class="overflow-x-auto">
            <table class="w-full text-xs" :aria-busy="list.isFetching.value">
              <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
                <tr>
                  <th scope="col" class="px-3 py-2 font-medium">Model</th>
                  <th scope="col" class="px-3 py-2 font-medium">Device type</th>
                  <th scope="col" class="px-3 py-2 font-medium">TAC</th>
                  <th scope="col" class="px-3 py-2 font-medium">First seen</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Network age</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Days seen</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Handsets (IMEIs) on day one</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Handsets now, est.</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">SIMs now, est.</th>
                </tr>
              </thead>
              <tbody>
                <tr v-if="result && result.rows.length === 0">
                  <td colspan="9" class="px-3 py-8 text-center text-[var(--c-text-muted)]">No model was first seen in this period.</td>
                </tr>
                <tr v-for="m in result?.rows ?? []" :key="m.tac" class="border-t">
                  <td class="px-3 py-2">
                    <RouterLink :to="`/devices/${m.tac}`" class="font-medium text-[var(--c-accent)] hover:underline">
                      {{ m.model ?? 'Model not in GSMA' }}
                    </RouterLink>
                    <span v-if="m.brand" class="text-[var(--c-text-secondary)]"> · {{ m.brand }}</span>
                  </td>
                  <td class="px-3 py-2 text-[var(--c-text-secondary)]">{{ m.deviceType ?? '—' }}</td>
                  <td class="tabular px-3 py-2 font-mono">{{ m.tac }}</td>
                  <td class="tabular px-3 py-2 whitespace-nowrap">{{ formatDate(m.firstSeen) }}</td>
                  <td class="tabular px-3 py-2 text-right whitespace-nowrap">{{ networkAgeText(m.networkAgeDays, false) }}</td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(m.daysSeen) }}</td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(m.firstDayImeis) }}</td>
                  <td class="tabular px-3 py-2 text-right">{{ m.handsets === null ? '—' : formatFull(m.handsets) }}</td>
                  <td class="tabular px-3 py-2 text-right">{{ m.sims === null ? '—' : formatFull(m.sims) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <Pagination
            v-if="result"
            :page="result.page"
            :page-size="result.pageSize"
            :total="result.total"
            :loading="list.isFetching.value"
            @update:page="(p: number) => (page = p)"
          />
          <p v-if="result" class="border-t px-4 py-2 text-2xs text-[var(--c-text-muted)]">
            {{ formatFull(result.total) }} models first seen between {{ formatDate(result.from) }} and {{ formatDate(result.to) }}.
            Handsets and SIMs now come from the latest delivery's device figures and are estimates; a model with none yet has
            not reached a delivery's figures.
          </p>
        </Card>
      </template>
    </AsyncBoundary>
  </div>
</template>
