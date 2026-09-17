<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import VendorMovementChart from '@/design-system/VendorMovementChart.vue'
import { useVendorMovement, type VendorRanking } from '@/api/dashboard'
import { formatDate, formatFull } from '@/lib/format'

/**
 * Vendors, measured three ways, with the controls to choose which.
 *
 * <p>
 * This card replaces one called "Vendor growth" that answered only the first question and used
 * the wrong word for it. The three are genuinely different, and conflating them is how a vendor
 * chart misleads:
 * </p>
 *
 * - **Movement** counts add and remove *events*. A SIM moved between two handsets thirty times
 *   contributes thirty of each and changes the population by nothing. Over the full span HMD
 *   shows 41.7 million adds — far more devices than HMD has here — because its devices move.
 * - **Share** counts the population now. A vendor with ten million stable handsets produces no
 *   events at all and is invisible under movement while dominating this.
 * - **Growth** compares the population against the first delivery, shown *relative to the
 *   network*, which fell 9.29% over the same span. Every vendor's absolute change is negative;
 *   the ones that gained share are those that fell by less.
 *
 * The date range only affects movement — the two population figures are fixed points, the first
 * delivery and the latest — so the control is labelled for what it does rather than sitting
 * there looking global.
 */

/** Modes, with the one-line explanation each needs to be read correctly. */
const MODES: Array<{ id: VendorRanking; label: string; help: string }> = [
  {
    id: 'movement',
    label: 'Movement',
    help: 'Add and remove events in the window. Counts events, not devices — a SIM that moves between handsets repeatedly appears many times and changes the population by nothing.',
  },
  {
    id: 'share',
    label: 'Share',
    help: 'Active bindings now, as a share of the whole network. The figure to use for "how big is this vendor".',
  },
  {
    id: 'growth',
    label: 'Growth',
    help: 'Population now against the first delivery, relative to the network. Shown in points of share gained or lost, because every absolute change is negative.',
  },
]

const ranking = ref<VendorRanking>('movement')
const normalised = ref(false)
/**
 * How many vendors the widget opens with.
 *
 * Five rather than eight by product-owner decision. The selector still offers 5, 8, 12 and 20,
 * so this is the opening view rather than a ceiling.
 */
const limit = ref(5)
const from = ref('')
const to = ref('')

const filters = computed(() => ({
  rank: ranking.value,
  from: from.value || null,
  to: to.value || null,
  limit: limit.value,
}))

const query = useVendorMovement(filters)
const data = computed(() => query.data.value ?? null)
const mode = computed(() => MODES.find((m) => m.id === ranking.value) ?? MODES[0]!)

/**
 * The window in force, said in words.
 *
 * Absent dates mean "everything the feed covers", which is not obvious from two empty inputs.
 */
const windowLabel = computed(() => {
  const d = data.value
  if (!d) return ''
  const whole = !from.value && !to.value
  if (whole) {
    return d.earliestAvailable && d.latestAvailable
      ? `all ${formatDate(d.earliestAvailable)} – ${formatDate(d.latestAvailable)}`
      : 'all available days'
  }
  return `${d.from ? formatDate(d.from) : '…'} – ${d.to ? formatDate(d.to) : '…'}`
})

function clearRange() {
  from.value = ''
  to.value = ''
}

// Normalising share or growth is meaningless - both are already relative - so the toggle is
// hidden outside movement, and reset so it cannot come back on invisibly.
watch(ranking, (value) => {
  if (value !== 'movement') normalised.value = false
})
</script>

<template>
  <Card title="Vendors" :subtitle="mode.help">
    <template #actions>
      <div class="flex flex-wrap items-center justify-end gap-2">
        <!-- Mode. A segmented control rather than a dropdown: three options, all worth seeing. -->
        <div
          class="inline-flex overflow-hidden rounded-[var(--radius-md)] border"
          role="group"
          aria-label="What to measure"
        >
          <button
            v-for="m in MODES"
            :key="m.id"
            type="button"
            class="px-2.5 py-1 text-[var(--text-xs)] font-medium transition-colors"
            :class="
              ranking === m.id
                ? 'bg-[var(--c-accent)] text-[var(--c-accent-text)]'
                : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
            "
            :aria-pressed="ranking === m.id"
            @click="ranking = m.id"
          >
            {{ m.label }}
          </button>
        </div>

        <label
          v-if="ranking === 'movement'"
          class="flex items-center gap-1.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]"
          title="Net movement as a percentage of the vendor's own population, which is what makes a vendor with fifty million bindings comparable to one with fifty thousand."
        >
          <input v-model="normalised" type="checkbox" />
          as % of population
        </label>

        <select
          v-model.number="limit"
          class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-1.5 py-1 text-[var(--text-xs)]"
          aria-label="How many vendors"
        >
          <option :value="5">5</option>
          <option :value="8">8</option>
          <option :value="12">12</option>
          <option :value="20">20</option>
        </select>
      </div>
    </template>

    <AsyncBoundary
      :is-loading="query.isPending.value"
      :is-error="query.isError.value"
      :error="query.error.value"
      min-height="14rem"
      @retry="query.refetch()"
    >
      <template v-if="data">
        <!-- The date range, only where it does something. -->
        <div
          v-if="ranking === 'movement'"
          class="mb-3 flex flex-wrap items-end gap-2 border-b pb-3"
        >
          <div>
            <label for="vm-from" class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              Events from
            </label>
            <input
              id="vm-from"
              v-model="from"
              type="date"
              :min="data.earliestAvailable ?? undefined"
              :max="data.latestAvailable ?? undefined"
              class="tabular mt-0.5 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
            />
          </div>
          <div>
            <label for="vm-to" class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              to
            </label>
            <input
              id="vm-to"
              v-model="to"
              type="date"
              :min="data.earliestAvailable ?? undefined"
              :max="data.latestAvailable ?? undefined"
              class="tabular mt-0.5 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
            />
          </div>
          <button
            v-if="from || to"
            type="button"
            class="rounded-[var(--radius-md)] px-1.5 py-1 text-[var(--text-2xs)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
            @click="clearRange"
          >
            clear
          </button>
          <p class="ml-auto text-[var(--text-2xs)] text-[var(--c-text-muted)]">
            {{ windowLabel }}
          </p>
        </div>

        <VendorMovementChart
          :rows="data.rows"
          :ranking="ranking"
          :normalised="normalised"
          :network-change-percent="data.networkChangePercent"
        />

        <!--
          The caveat that makes growth readable. Stated under the chart rather than in a tooltip,
          because a reader who does not know it will draw the wrong conclusion without ever
          thinking to hover.
        -->
        <div class="mt-3 border-t pt-2 text-[var(--text-2xs)] text-[var(--c-text-muted)]">
          <p v-if="ranking === 'growth'">
            Network:
            <span class="tabular">{{ formatFull(data.networkPopulationAtStart) }}</span>
            →
            <span class="tabular">{{ formatFull(data.networkPopulation) }}</span>
            (<span class="tabular">{{ data.networkChangePercent.toFixed(2) }}%</span>). Bars show
            each vendor's change <em>against</em> that, so positive means share gained even where
            the count fell.
            <span v-if="data.startIsInitialDump" class="text-[var(--c-warning)]">
              The starting point is the initial dump, which covers a 30-day window rather than an
              instant and averages 1.57 handsets per SIM — so part of the network's decline is
              that over-count being resolved, not devices leaving.
            </span>
          </p>

          <p v-else-if="ranking === 'share'">
            <span class="tabular">{{ formatFull(data.networkPopulation) }}</span> active bindings
            across the network. A binding is one number + SIM + handset, so a subscriber with two
            handsets counts twice.
          </p>

          <p v-else>
            Net events over {{ windowLabel }}.
            <template v-if="normalised">
              Shown as a percentage of each vendor's own population, so a small vendor with a big
              swing is comparable to a large one.
            </template>
            <template v-else>
              These are <em>events</em>, not devices — one SIM moving between handsets thirty times
              contributes thirty adds and thirty removes and changes the population by nothing.
              Switch to Growth for the population figure.
            </template>
          </p>
        </div>
      </template>
    </AsyncBoundary>
  </Card>
</template>
