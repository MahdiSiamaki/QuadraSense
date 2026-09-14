<script setup lang="ts">
import { computed, ref } from 'vue'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useImsiHistory } from '@/api/imsi'
import { formatDate, formatFull } from '@/lib/format'

/**
 * One SIM's dated add and remove events.
 *
 * Read from the event log, which is partitioned by day, so the date range prunes partitions
 * before anything is decompressed: a two-week window opens 14 of 133 partitions rather than
 * scanning 1.05 billion rows. That is why the range control is at the top of this panel and not
 * buried in a filter drawer - it is the single biggest lever on what this query costs.
 *
 * Events are grouped by day and shown newest first, because the question is almost always "what
 * changed recently" rather than "what happened first".
 */
const props = defineProps<{ imsi: string }>()

const from = ref('')
const to = ref('')

const range = computed(() => ({ from: from.value || null, to: to.value || null }))
const history = useImsiHistory(() => props.imsi, range)

/** Events grouped by day, newest day first. */
const days = computed(() => {
  const events = history.data.value?.events ?? []
  const groups = new Map<string, typeof events>()

  for (const event of events) {
    const list = groups.get(event.date) ?? []
    list.push(event)
    groups.set(event.date, list)
  }

  return [...groups.entries()]
})

function clearRange() {
  from.value = ''
  to.value = ''
}
</script>

<template>
  <Card title="History" :subtitle="`Dated changes involving ${imsi}`">
    <template #actions>
      <div class="flex flex-wrap items-center gap-2">
        <label for="h-from" class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">From</label>
        <input
          id="h-from"
          v-model="from"
          type="date"
          class="tabular rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
        />
        <label for="h-to" class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">to</label>
        <input
          id="h-to"
          v-model="to"
          type="date"
          class="tabular rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
        />
        <button
          v-if="from || to"
          type="button"
          class="rounded-[var(--radius-md)] px-1.5 py-1 text-[var(--text-2xs)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
          @click="clearRange"
        >
          clear
        </button>
      </div>
    </template>

    <AsyncBoundary
      :is-loading="history.isPending.value"
      :is-error="history.isError.value"
      :error="history.error.value"
      :is-empty="(history.data.value?.events.length ?? 0) === 0"
      empty-message="No dated events for this SIM in the selected range."
      min-height="8rem"
      @retry="history.refetch()"
    >
      <p
        v-if="history.data.value?.truncated"
        class="mb-3 rounded-[var(--radius-md)] border px-2.5 py-1.5 text-[var(--text-xs)]"
        :style="{
          borderColor: 'var(--c-warning)',
          backgroundColor: 'var(--c-warning-subtle)',
          color: 'var(--c-warning)',
        }"
      >
        Showing the most recent 500 events. Narrow the date range to see the rest.
      </p>

      <ol class="flex flex-col gap-4">
        <li v-for="[date, events] in days" :key="date">
          <p class="tabular mb-1.5 text-[var(--text-xs)] font-semibold">
            {{ formatDate(date) }}
          </p>

          <ul class="flex flex-col gap-1.5 border-l pl-3">
            <li
              v-for="(event, index) in events"
              :key="`${event.sequence}-${event.imei}-${index}`"
              class="relative flex flex-wrap items-baseline gap-x-2 gap-y-0.5 text-[var(--text-xs)]"
            >
              <!--
                The dot sits on the rule, and its colour AND its word both carry the meaning -
                "added" and "removed" are spelled out, so the timeline survives being read by
                someone who cannot distinguish green from red.
              -->
              <span
                class="absolute -left-[1.03rem] top-1.5 size-1.5 rounded-full"
                :style="{
                  backgroundColor: event.added ? 'var(--c-success)' : 'var(--c-danger)',
                }"
                aria-hidden="true"
              />

              <span
                class="w-16 shrink-0 font-medium"
                :style="{ color: event.added ? 'var(--c-success)' : 'var(--c-danger)' }"
              >
                {{ event.added ? 'added' : 'removed' }}
              </span>

              <span class="tabular font-mono text-[var(--c-text-secondary)]">
                {{ event.imei }}
              </span>

              <span v-if="event.marketingName || event.manufacturer" class="text-[var(--c-text-muted)]">
                {{ event.marketingName ?? event.manufacturer }}
              </span>

              <span class="tabular text-[var(--c-text-muted)]">
                on {{ event.msisdn }}
              </span>
            </li>
          </ul>
        </li>
      </ol>

      <p
        v-if="history.data.value"
        class="tabular mt-4 border-t pt-2 text-[var(--text-2xs)] text-[var(--c-text-muted)]"
      >
        {{ history.data.value.timing.elapsedMs }} ms ·
        {{ formatFull(history.data.value.timing.rowsExamined) }} rows examined ·
        the event log holds 1,049,379,693 rows across 133 days
      </p>
    </AsyncBoundary>
  </Card>
</template>
