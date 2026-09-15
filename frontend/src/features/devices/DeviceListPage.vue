<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Card from '@/design-system/Card.vue'
import Pagination from '@/design-system/Pagination.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import DeviceImage from './DeviceImage.vue'
import {
  classifyTerm,
  useDeviceFacets,
  useDeviceSearch,
  type DeviceSort,
  type DeviceSummary,
} from '@/api/devices'
import { formatCompact, formatDate, formatFull } from '@/lib/format'

/**
 * The device catalogue: ~98,000 models, searchable by name or by any identifier.
 *
 * **One search box, five kinds of input.** A reader arrives here with a model name, a TAC, an
 * IMEI, an IMSI or a phone number, and making them choose which box to type it into is asking
 * them to classify their own question before they are allowed to ask it. The server classifies
 * instead — by length, which is unambiguous here because TAC, MSISDN, IMEI and IMSI have four
 * distinct lengths in this feed — and the hint under the box says what it decided *before*
 * anything is sent.
 *
 * **Permissions are enforced on the server, and explained here.** Resolving an IMSI needs
 * `lookup.imsi`, a number needs `lookup.subscriber`, an IMEI needs `lookup.imei`. A reader
 * without one gets a plain sentence saying which permission is missing — not an empty list, which
 * would read as "no such SIM" and teach them the data is incomplete when it is not.
 *
 * **No virtualisation, deliberately.** The requirement mentioned it, and it is not needed: the
 * server pages at 40 rows and caps at 100, so the DOM never holds more than a hundred rows of a
 * plain table. A virtual scroller here would add a scroll-position bug surface to solve a problem
 * that pagination has already solved. What made the list fast was the mart — 98,000 rows instead
 * of a 2.35 s aggregate over 295 million — not the rendering.
 */

const query = ref('')
const submitted = ref('')
const manufacturer = ref('')
const deviceType = ref('')
const operatingSystem = ref('')
const sort = ref<DeviceSort>('bindings')
const descending = ref(true)
const page = ref(1)
const pageSize = 40

const facets = useDeviceFacets()

const filters = computed(() => ({
  query: submitted.value || null,
  manufacturer: manufacturer.value || null,
  deviceType: deviceType.value || null,
  operatingSystem: operatingSystem.value || null,
  sort: sort.value,
  descending: descending.value,
  page: page.value,
  pageSize,
}))

const search = useDeviceSearch(filters)
const data = computed(() => search.data.value ?? null)

/** What the term will be taken to be, said before the request goes out. */
const term = computed(() => classifyTerm(query.value))

/** Any filter or sort change starts from page one; page 7 of a different result is meaningless. */
watch([submitted, manufacturer, deviceType, operatingSystem, sort, descending], () => {
  page.value = 1
})

function submit() {
  submitted.value = query.value.trim()
}

function clearAll() {
  query.value = ''
  submitted.value = ''
  manufacturer.value = ''
  deviceType.value = ''
  operatingSystem.value = ''
}

const hasFilters = computed(
  () => !!(submitted.value || manufacturer.value || deviceType.value || operatingSystem.value),
)

/** Clicking a column sorts by it; clicking the active column reverses it. */
function sortBy(column: DeviceSort) {
  if (sort.value === column) {
    descending.value = !descending.value
  } else {
    sort.value = column
    // Counts are interesting from the top, names from A. Defaulting every column to descending
    // would make an alphabetical sort start at Z.
    descending.value = !['model', 'manufacturer', 'tac'].includes(column)
  }
}

function sortMark(column: DeviceSort): string {
  if (sort.value !== column) return ''
  return descending.value ? ' ↓' : ' ↑'
}

/**
 * The name to show. Marketing name first, because that is what a device is called.
 *
 * The empty TAC is not a nameless device, it is the *absence* of one: 5.5 million bindings whose
 * IMEI is the source's `000000` sentinel or otherwise malformed. Labelling it the way the
 * dashboard already does keeps one vocabulary across the product, and stops the largest row in
 * the catalogue rendering as a line of blanks.
 */
function displayName(d: DeviceSummary): string {
  if (!d.tac) return '(unknown device)'
  return d.marketingName || d.model || d.brand || '(no model name)'
}

/** Whether this row has a detail page. The unknown bucket has no TAC, so it has no page. */
function isRealDevice(d: DeviceSummary): boolean {
  return d.tac.length > 0
}

const columnClass =
  'cursor-pointer select-none px-3 py-2 text-right font-medium hover:text-[var(--c-text)]'
</script>

<template>
  <div class="flex flex-col gap-5">
    <header>
      <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Devices</h1>
      <p class="mt-0.5 text-[var(--text-sm)] text-[var(--c-text-secondary)]">
        Every device model on the network, by name or by any identifier.
      </p>
    </header>

    <Card>
      <form class="flex flex-col gap-3" @submit.prevent="submit">
        <div class="flex flex-wrap items-start gap-3">
          <div class="min-w-0 flex-1">
            <label for="device-q" class="sr-only">Search devices</label>
            <input
              id="device-q"
              v-model="query"
              type="search"
              autocomplete="off"
              placeholder="Model name, brand, TAC, IMEI, IMSI or number"
              class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-3 py-2 text-[var(--text-sm)]"
            />
            <p
              v-if="term.hint"
              class="mt-1 text-[var(--text-2xs)] text-[var(--c-text-muted)]"
            >
              {{ term.hint }}
            </p>
          </div>

          <button
            type="submit"
            class="rounded-[var(--radius-md)] bg-[var(--c-accent)] px-4 py-2 text-[var(--text-sm)] font-medium text-[var(--c-accent-text)]"
          >
            Search
          </button>
        </div>

        <div class="flex flex-wrap items-center gap-2 border-t pt-3">
          <select
            v-model="manufacturer"
            class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            aria-label="Manufacturer"
          >
            <option value="">All manufacturers</option>
            <option v-for="m in facets.data.value?.manufacturers ?? []" :key="m.value" :value="m.value">
              {{ m.value }} ({{ formatCompact(m.models) }})
            </option>
          </select>

          <select
            v-model="deviceType"
            class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            aria-label="Device type"
          >
            <option value="">All device types</option>
            <option v-for="t in facets.data.value?.deviceTypes ?? []" :key="t.value" :value="t.value">
              {{ t.value }} ({{ formatCompact(t.models) }})
            </option>
          </select>

          <select
            v-model="operatingSystem"
            class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            aria-label="Operating system"
          >
            <option value="">All operating systems</option>
            <option v-for="o in facets.data.value?.operatingSystems ?? []" :key="o.value" :value="o.value">
              {{ o.value }} ({{ formatCompact(o.models) }})
            </option>
          </select>

          <button
            v-if="hasFilters"
            type="button"
            class="rounded-[var(--radius-md)] px-2 py-1.5 text-[var(--text-xs)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
            @click="clearAll"
          >
            Clear
          </button>

          <p
            v-if="data"
            class="tabular ml-auto text-[var(--text-2xs)] text-[var(--c-text-muted)]"
          >
            {{ formatFull(data.total) }} model{{ data.total === 1 ? '' : 's' }}
            · {{ data.timing.elapsedMs }} ms
          </p>
        </div>
      </form>
    </Card>

    <!--
      What the identifier turned out to be. Shown as its own panel rather than a toast, because it
      is the answer to what the reader asked, not a notification about it.
    -->
    <div
      v-if="data?.resolution?.note"
      class="rounded-[var(--radius-md)] border px-3 py-2 text-[var(--text-sm)]"
      :class="
        data.resolution.permitted
          ? 'border-[var(--c-border)] text-[var(--c-text-secondary)]'
          : 'border-[var(--c-warning)] text-[var(--c-text)]'
      "
    >
      {{ data.resolution.note }}
    </div>

    <Card flush>
      <AsyncBoundary
        :is-loading="search.isPending.value"
        :is-error="search.isError.value"
        :error="search.error.value"
        min-height="20rem"
        @retry="search.refetch()"
      >
        <template v-if="data">
          <div v-if="data.items.length === 0" class="px-4 py-14 text-center">
            <p class="text-[var(--text-sm)] text-[var(--c-text-secondary)]">
              No device models match.
            </p>
            <button
              v-if="hasFilters"
              type="button"
              class="mt-2 text-[var(--text-xs)] text-[var(--c-accent)] hover:underline"
              @click="clearAll"
            >
              Clear the filters
            </button>
          </div>

          <div v-else class="overflow-x-auto">
            <table class="w-full border-collapse text-[var(--text-sm)]">
              <thead
                class="border-b text-[var(--text-xs)] text-[var(--c-text-secondary)]"
              >
                <tr>
                  <th scope="col" class="px-3 py-2 text-left font-medium">Device</th>
                  <th scope="col" :class="[columnClass, 'text-left']" @click="sortBy('tac')">
                    TAC{{ sortMark('tac') }}
                  </th>
                  <th scope="col" class="px-3 py-2 text-left font-medium">Type</th>
                  <th scope="col" :class="columnClass" @click="sortBy('bindings')">
                    Bindings{{ sortMark('bindings') }}
                  </th>
                  <th scope="col" :class="columnClass" @click="sortBy('handsets')">
                    Handsets{{ sortMark('handsets') }}
                  </th>
                  <th scope="col" :class="columnClass" @click="sortBy('sims')">
                    SIMs{{ sortMark('sims') }}
                  </th>
                  <th scope="col" :class="columnClass" @click="sortBy('subscribers')">
                    Numbers{{ sortMark('subscribers') }}
                  </th>
                  <th scope="col" :class="columnClass" @click="sortBy('lastSeen')">
                    Last seen{{ sortMark('lastSeen') }}
                  </th>
                </tr>
              </thead>

              <tbody>
                <tr
                  v-for="d in data.items"
                  :key="d.tac"
                  class="border-b last:border-0 hover:bg-[var(--c-surface-hover)]"
                >
                  <td class="px-3 py-2">
                    <component
                      :is="isRealDevice(d) ? RouterLink : 'div'"
                      :to="isRealDevice(d) ? `/devices/${d.tac}` : undefined"
                      class="flex items-center gap-3"
                      :class="isRealDevice(d) ? 'hover:text-[var(--c-accent)]' : ''"
                    >
                      <DeviceImage
                        :tac="d.tac"
                        :has-image="d.hasImage"
                        :name="d.vendor ?? d.brand"
                        :device-type="d.deviceType"
                        size="sm"
                      />
                      <span class="min-w-0">
                        <span class="block truncate font-medium">{{ displayName(d) }}</span>
                        <span
                          class="block truncate text-[var(--text-2xs)] text-[var(--c-text-muted)]"
                        >
                          {{
                            isRealDevice(d)
                              ? (d.vendor ?? d.manufacturer ?? 'Unknown manufacturer')
                              : 'IMEI identifies no model'
                          }}
                        </span>
                      </span>
                    </component>
                  </td>
                  <td class="tabular px-3 py-2 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                    {{ d.tac || '—' }}
                  </td>
                  <td class="px-3 py-2 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                    {{ d.deviceType ?? '—' }}
                  </td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(d.bindings) }}</td>
                  <td class="tabular px-3 py-2 text-right text-[var(--c-text-secondary)]">
                    {{ formatFull(d.handsets) }}
                  </td>
                  <td class="tabular px-3 py-2 text-right text-[var(--c-text-secondary)]">
                    {{ formatFull(d.sims) }}
                  </td>
                  <td class="tabular px-3 py-2 text-right text-[var(--c-text-secondary)]">
                    {{ formatFull(d.subscribers) }}
                  </td>
                  <td
                    class="tabular whitespace-nowrap px-3 py-2 text-right text-[var(--text-xs)] text-[var(--c-text-muted)]"
                    :title="
                      d.lastSeen
                        ? undefined
                        : 'No daily file has ever named a binding of this model. It is here because the initial dump listed it.'
                    "
                  >
                    {{ d.lastSeen ? formatDate(d.lastSeen) : 'dump only' }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <Pagination
            v-if="data.total > pageSize"
            class="border-t px-3 py-2"
            :page="data.page"
            :page-size="data.pageSize"
            :total="data.total"
            :loading="search.isFetching.value"
            @update:page="page = $event"
          />
        </template>
      </AsyncBoundary>
    </Card>

    <p class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
      A device is a <strong>model</strong>, identified by its 8-digit TAC. Handset, SIM and number
      counts are HyperLogLog estimates at roughly 0.5% error — the same estimator the dashboard
      uses, so the two cannot disagree about one population. Handsets sit below bindings whenever a
      handset carries more than one SIM.
    </p>
  </div>
</template>
