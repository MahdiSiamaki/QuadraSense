<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import Pagination from '@/design-system/Pagination.vue'
import { useDeviceIdentifiers } from '@/api/devices'
import { ApiError } from '@/api/client'
import { formatDate, formatFull } from '@/lib/format'

/**
 * Every IMEI, SIM and number bound to one device model.
 *
 * **This is the sensitive panel on this page, and it behaves like it.** Resolving one IMEI exposes
 * one person's handset; listing a model's identifiers exposes everybody who owns that model, and
 * the most populous model here covers 208,895 handsets. So it sits behind its own permission
 * (`device.identifiers`, not the per-handset `lookup.imei`), every request is audited server-side
 * with the count it returned, and **it does not load until asked**. A page that fetches two
 * hundred thousand people's identifiers because somebody opened a device page is generating audit
 * entries for a question nobody asked.
 *
 * Masking is applied on the server: without `identifier.reveal` the complete value is never in the
 * response at all, so there is nothing here for a developer-tools panel to recover.
 */
const props = defineProps<{ tac: string }>()

const activeOnly = ref(false)
const from = ref('')
const to = ref('')
const page = ref(1)
const pageSize = 50

const query = useDeviceIdentifiers()
const data = computed(() => query.data.value ?? null)

/**
 * Whether the panel has been asked for, separately from whether it holds data.
 *
 * The filters and the "Load" button used to key off the data. A failed request clears it, so
 * a filter that made the server fail took the filters away with it and put the button back: the
 * error was never shown, and every retry re-sent the same bad filter.
 */
const opened = ref(false)

function load(toPage = 1) {
  opened.value = true
  page.value = toPage
  query.mutate({
    tac: props.tac,
    activeOnly: activeOnly.value,
    from: from.value || null,
    to: to.value || null,
    page: toPage,
    pageSize,
  })
}

// Filters only narrow a list that has already been asked for, so they re-run it. They do not
// start one: this panel stays closed until somebody opens it.
watch([activeOnly, from, to], () => {
  if (opened.value) load(1)
})

// A different device is a different question. Anything on screen belongs to the previous one.
watch(
  () => props.tac,
  () => {
    query.reset()
    opened.value = false
  },
)

const masked = computed(() => data.value?.identifiers === 'Masked')

const problem = computed(() => {
  const error = query.error.value
  if (!error) return null
  if (error instanceof ApiError) {
    return error.problem?.detail ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return 'Could not reach the server.'
})
</script>

<template>
  <Card title="Identifiers" subtitle="Every IMEI, SIM and number bound to this model.">
    <template #actions>
      <div v-if="opened" class="flex flex-wrap items-end gap-2">
        <label
          class="flex items-center gap-1.5 text-xs text-[var(--c-text-secondary)]"
        >
          <input v-model="activeOnly" type="checkbox" />
          active only
        </label>
        <input
          v-model="from"
          type="date"
          aria-label="Changed from"
          class="tabular rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
        />
        <input
          v-model="to"
          type="date"
          aria-label="Changed to"
          class="tabular rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
        />
      </div>
    </template>

    <!-- Closed until asked. -->
    <div v-if="!opened" class="py-6 text-center">
      <Button variant="primary" size="lg" @click="load(1)">Load identifiers</Button>
      <p class="mx-auto mt-2 max-w-md text-2xs text-[var(--c-text-muted)]">
        This is bulk personal data and every request is recorded in the audit log with your name
        and the number of rows returned. It loads only when you ask for it.
      </p>
    </div>

    <p v-else-if="query.isPending.value" class="py-6 text-center text-sm text-[var(--c-text-muted)]">
      Loading…
    </p>

    <p
      v-else-if="problem"
      class="rounded-[var(--radius-md)] border border-[var(--c-danger)] px-3 py-2 text-sm"
    >
      {{ problem }}
    </p>

    <template v-else-if="data">
      <div
        v-if="masked"
        class="mb-3 rounded-[var(--radius-md)] border px-2.5 py-1.5 text-xs text-[var(--c-text-secondary)]"
      >
        Identifiers are redacted because you do not have the
        <code class="text-2xs">identifier.reveal</code> permission. The complete
        values were never sent to this page.
      </div>

      <div v-if="data.items.length === 0" class="py-8 text-center">
        <p class="text-sm text-[var(--c-text-secondary)]">
          No bindings match.
        </p>
        <p v-if="from || to" class="mt-1 text-2xs text-[var(--c-text-muted)]">
          A date range excludes every binding the daily feed has never mentioned, which is most of
          the tail.
        </p>
      </div>

      <div v-else class="overflow-x-auto">
        <table class="w-full border-collapse text-sm">
          <thead class="border-b text-xs text-[var(--c-text-secondary)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 text-left font-medium">IMEI</th>
              <th scope="col" class="px-2 py-1.5 text-left font-medium">IMSI</th>
              <th scope="col" class="px-2 py-1.5 text-left font-medium">Number</th>
              <th scope="col" class="px-2 py-1.5 text-left font-medium">State</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Last change</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="(row, i) in data.items"
              :key="`${row.imei}-${row.imsi}-${row.msisdn}-${i}`"
              class="border-b last:border-0"
            >
              <td class="tabular px-2 py-1.5">{{ row.imei }}</td>
              <td class="tabular px-2 py-1.5">{{ row.imsi }}</td>
              <td class="tabular px-2 py-1.5">{{ row.msisdn }}</td>
              <td class="px-2 py-1.5">
                <span
                  class="rounded-[var(--radius-sm)] px-1.5 py-0.5 text-2xs font-medium"
                  :style="{ color: row.isActive ? 'var(--viz-3)' : 'var(--c-text-muted)' }"
                >
                  {{ row.isActive ? 'active' : 'removed' }}
                </span>
              </td>
              <td
                class="tabular px-2 py-1.5 text-right text-xs text-[var(--c-text-muted)]"
                :title="row.lastChangeDate ? undefined : 'No daily file has ever named this binding.'"
              >
                {{ row.lastChangeDate ? formatDate(row.lastChangeDate) : 'dump only' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <Pagination
        v-if="data.total > pageSize"
        class="mt-3 border-t pt-2"
        :page="data.page"
        :page-size="data.pageSize"
        :total="data.total"
        :loading="query.isPending.value"
        @update:page="load($event)"
      />

      <p class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
        <span class="tabular">{{ formatFull(data.total) }}</span> binding{{ data.total === 1 ? '' : 's' }}
        · {{ data.timing.elapsedMs }} ms ·
        <span class="tabular">{{ formatFull(data.timing.rowsExamined) }}</span> rows examined.
        Served from an IMEI-ordered copy of current state, where this model is a contiguous range —
        the same filter on the primary table reads all 295 million rows.
      </p>
    </template>
  </Card>
</template>
