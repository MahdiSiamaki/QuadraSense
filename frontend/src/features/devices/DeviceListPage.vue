<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter, type LocationQuery } from 'vue-router'
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
import { recall, remember } from '@/lib/session-memory'

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

/*
  Where the reader is lives in the URL - filters, sort and page - so opening a device and coming
  back returns to the same place, and a view can be shared. The search text does not: it can be a
  phone number, an IMSI or an IMEI, and a query string carries those into history, bookmarks and
  every copied link. It is kept per tab in session storage instead (the product owner's choice),
  and forgotten on sign-out.
*/
const route = useRoute()
const router = useRouter()

const SORTS: readonly DeviceSort[] = [
  'bindings', 'handsets', 'sims', 'subscribers', 'model', 'manufacturer', 'tac', 'lastSeen',
]
const SEARCH_KEY = 'devices.search'

// The route object is shared, so these watchers also see the navigation that leaves this page -
// to a device, with an empty query - before it unmounts. Acting on it would reset the state the
// reader is coming back to, and write the list's query onto the device page.
const listPath = route.path
const onList = () => route.path === listPath

function text(q: LocationQuery, key: string): string {
  const v = q[key]
  return typeof v === 'string' ? v : ''
}

function readUrl(q: LocationQuery) {
  const sortParam = text(q, 'sort') as DeviceSort
  const sort = SORTS.includes(sortParam) ? sortParam : 'bindings'
  const dir = text(q, 'dir')
  const pageParam = Number.parseInt(text(q, 'page'), 10)
  return {
    manufacturer: text(q, 'manufacturer'),
    deviceType: text(q, 'type'),
    operatingSystem: text(q, 'os'),
    sort,
    descending: dir === 'asc' ? false : dir === 'desc' ? true : defaultDescending(sort),
    page: Number.isFinite(pageParam) && pageParam > 1 ? pageParam : 1,
  }
}

const initial = readUrl(route.query)
const query = ref(recall(SEARCH_KEY))
const submitted = ref(query.value.trim())
const manufacturer = ref(initial.manufacturer)
const deviceType = ref(initial.deviceType)
const operatingSystem = ref(initial.operatingSystem)
const sort = ref<DeviceSort>(initial.sort)
const descending = ref(initial.descending)
const page = ref(initial.page)
const pageSize = 40

/** The URL for the current state; defaults are left out so the plain page stays `/devices`. */
function toUrl(): Record<string, string> {
  const q: Record<string, string> = {}
  if (manufacturer.value) q.manufacturer = manufacturer.value
  if (deviceType.value) q.type = deviceType.value
  if (operatingSystem.value) q.os = operatingSystem.value
  if (sort.value !== 'bindings') q.sort = sort.value
  // The direction is written whenever it is not the one sortBy() would pick, so it survives.
  if (descending.value !== defaultDescending(sort.value)) q.dir = descending.value ? 'desc' : 'asc'
  if (page.value > 1) q.page = String(page.value)
  return q
}

// Set while the state is being taken from the URL, so the page-one rule below does not undo a
// page that came with it. That rule runs synchronously for this reason: a deferred watcher would
// run after the flag is cleared.
let fromUrl = false

watch(
  () => route.query,
  (q) => {
    if (!onList()) return
    const next = readUrl(q)
    if (JSON.stringify(next) === JSON.stringify(readUrl(toUrl()))) return
    fromUrl = true
    manufacturer.value = next.manufacturer
    deviceType.value = next.deviceType
    operatingSystem.value = next.operatingSystem
    sort.value = next.sort
    descending.value = next.descending
    page.value = next.page
    fromUrl = false
  },
)

watch([manufacturer, deviceType, operatingSystem, sort, descending, page], () => {
  if (!onList()) return
  const q = toUrl()
  if (JSON.stringify(readUrl(q)) === JSON.stringify(readUrl(route.query))) return
  void router.replace({ query: q })
})

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
watch(
  [submitted, manufacturer, deviceType, operatingSystem, sort, descending],
  () => {
    if (!fromUrl) page.value = 1
  },
  { flush: 'sync' },
)

function submit() {
  submitted.value = query.value.trim()
  remember(SEARCH_KEY, submitted.value)
}

function clearAll() {
  query.value = ''
  submitted.value = ''
  remember(SEARCH_KEY, '')
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
    descending.value = defaultDescending(column)
  }
}

// Counts are interesting from the top, names from A. Defaulting every column to descending would
// make an alphabetical sort start at Z.
function defaultDescending(column: DeviceSort): boolean {
  return !['model', 'manufacturer', 'tac'].includes(column)
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

// Alignment is left out on purpose and set per column. With `text-right` in here, the TAC header
// carried both `text-right` and `text-left`, and whichever Tailwind emits later wins - not the
// one written last in the template - so the header sat right while its values sat left.
const sortableClass = 'px-3 py-2 font-medium'
const columnClass = `${sortableClass} text-right`

// The sort is a button inside the header, not a click on the header cell: a cell cannot take
// focus, so sorting was mouse-only and screen readers were never told the column was actionable.
const sortButtonClass = 'cursor-pointer select-none font-medium hover:text-[var(--c-text)]'

function ariaSort(column: DeviceSort): 'ascending' | 'descending' | 'none' {
  if (sort.value !== column) return 'none'
  return descending.value ? 'descending' : 'ascending'
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <header>
      <h1 class="text-xl font-semibold tracking-tight">Devices</h1>
      <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
        Every device model on the network, by name or by any identifier.
        <RouterLink to="/devices/new-models" class="ml-1 text-[var(--c-accent)] hover:underline">New models →</RouterLink>
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
              class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-3 py-2 text-sm"
            />
            <p
              v-if="term.hint"
              class="mt-1 text-2xs text-[var(--c-text-muted)]"
            >
              {{ term.hint }}
            </p>
          </div>

          <button
            type="submit"
            class="rounded-[var(--radius-md)] bg-[var(--c-accent)] px-4 py-2 text-sm font-medium text-[var(--c-accent-text)]"
          >
            Search
          </button>
        </div>

        <div class="flex flex-wrap items-center gap-2 border-t pt-3">
          <select
            v-model="manufacturer"
            class="max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-xs"
            aria-label="Manufacturer"
          >
            <option value="">All manufacturers</option>
            <option v-for="m in facets.data.value?.manufacturers ?? []" :key="m.value" :value="m.value">
              {{ m.value }} ({{ formatCompact(m.models) }})
            </option>
          </select>

          <select
            v-model="deviceType"
            class="max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-xs"
            aria-label="Device type"
          >
            <option value="">All device types</option>
            <option v-for="t in facets.data.value?.deviceTypes ?? []" :key="t.value" :value="t.value">
              {{ t.value }} ({{ formatCompact(t.models) }})
            </option>
          </select>

          <select
            v-model="operatingSystem"
            class="max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-xs"
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
            class="rounded-[var(--radius-md)] px-2 py-1.5 text-xs text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
            @click="clearAll"
          >
            Clear
          </button>

          <p
            v-if="data"
            class="tabular ml-auto text-2xs text-[var(--c-text-muted)]"
          >
            {{ formatFull(data.total) }} model{{ data.total === 1 ? '' : 's' }}
            · {{ formatFull(data.timing.elapsedMs) }} ms
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
      class="rounded-[var(--radius-md)] border px-3 py-2 text-sm"
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
        gap="none"
        @retry="search.refetch()"
      >
        <template v-if="data">
          <div v-if="data.items.length === 0" class="px-4 py-14 text-center">
            <p class="text-sm text-[var(--c-text-secondary)]">
              No device models match.
            </p>
            <button
              v-if="hasFilters"
              type="button"
              class="mt-2 text-xs text-[var(--c-accent)] hover:underline"
              @click="clearAll"
            >
              Clear the filters
            </button>
          </div>

          <div v-else class="overflow-x-auto">
            <table class="w-full border-collapse text-sm">
              <thead
                class="border-b text-xs text-[var(--c-text-secondary)]"
              >
                <tr>
                  <th scope="col" class="px-3 py-2 text-left font-medium">Device</th>
                  <th scope="col" :class="[sortableClass, 'text-left']" :aria-sort="ariaSort('tac')">
                    <button type="button" :class="sortButtonClass" @click="sortBy('tac')">
                      TAC{{ sortMark('tac') }}
                    </button>
                  </th>
                  <th scope="col" class="px-3 py-2 text-left font-medium">Type</th>
                  <th scope="col" :class="columnClass" :aria-sort="ariaSort('bindings')">
                    <button type="button" :class="sortButtonClass" @click="sortBy('bindings')">
                      Bindings{{ sortMark('bindings') }}
                    </button>
                  </th>
                  <th scope="col" :class="columnClass" :aria-sort="ariaSort('handsets')">
                    <button type="button" :class="sortButtonClass" @click="sortBy('handsets')">
                      Handsets{{ sortMark('handsets') }}
                    </button>
                  </th>
                  <th scope="col" :class="columnClass" :aria-sort="ariaSort('sims')">
                    <button type="button" :class="sortButtonClass" @click="sortBy('sims')">
                      SIMs{{ sortMark('sims') }}
                    </button>
                  </th>
                  <th scope="col" :class="columnClass" :aria-sort="ariaSort('subscribers')">
                    <button type="button" :class="sortButtonClass" @click="sortBy('subscribers')">
                      Numbers{{ sortMark('subscribers') }}
                    </button>
                  </th>
                  <th scope="col" :class="columnClass" :aria-sort="ariaSort('lastSeen')">
                    <button type="button" :class="sortButtonClass" @click="sortBy('lastSeen')">
                      Last seen{{ sortMark('lastSeen') }}
                    </button>
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
                          class="block truncate text-2xs text-[var(--c-text-muted)]"
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
                  <td class="tabular px-3 py-2 text-xs text-[var(--c-text-secondary)]">
                    {{ d.tac || '—' }}
                  </td>
                  <td class="px-3 py-2 text-xs text-[var(--c-text-secondary)]">
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
                    class="tabular whitespace-nowrap px-3 py-2 text-right text-xs text-[var(--c-text-muted)]"
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

    <p class="text-2xs text-[var(--c-text-muted)]">
      A device is a <strong>model</strong>, identified by its 8-digit TAC. Handset, SIM and number
      counts are HyperLogLog estimates at roughly 0.5% error — the same estimator the dashboard
      uses, so the two cannot disagree about one population. Handsets sit below bindings whenever a
      handset carries more than one SIM.
    </p>
  </div>
</template>
