<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatDateTime, formatTime } from '@/lib/format'
import type { ImportEvent } from '@/api/imports'

/**
 * The timeline of one import.
 *
 * Every entry here was written by the worker while it worked. None of it is reconstructed from
 * status changes afterwards, which matters: a reconstruction can only show what the schema
 * happened to record, while this shows what the worker decided to say — including the parts
 * that changed no status, like "8,237,820 events already exist for this day; removing them".
 *
 * Entries are shown oldest first. A timeline read top-to-bottom is a narrative; reversed, it is
 * a log, and the question being asked of this screen is "what happened", not "what is latest".
 */
const props = defineProps<{ events: ImportEvent[] }>()

const expanded = ref<Set<number>>(new Set())

function toggle(index: number) {
  const next = new Set(expanded.value)
  if (!next.delete(index)) next.add(index)
  expanded.value = next
}

const rows = computed(() =>
  props.events.map((event, index) => ({
    ...event,
    index,
    detail: prettyDetail(event.detailJson),
  })),
)

function prettyDetail(json: string | null): string | null {
  if (!json) return null
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}

const tone: Record<string, { dot: string; text: string }> = {
  info: { dot: 'var(--c-text-muted)', text: 'var(--c-text)' },
  warning: { dot: 'var(--c-warning)', text: 'var(--c-text)' },
  error: { dot: 'var(--c-danger)', text: 'var(--c-danger)' },
}
</script>

<template>
  <ol v-if="rows.length" class="relative space-y-0">
    <li v-for="row in rows" :key="row.index" class="relative flex gap-3 pb-3 last:pb-0">
      <!-- The rail, drawn per item so the last one stops rather than trailing into nothing. -->
      <span
        v-if="row.index < rows.length - 1"
        class="absolute top-4 bottom-0 left-[3.5px] w-px bg-[var(--c-border)]"
        aria-hidden="true"
      />

      <span
        class="relative z-10 mt-1.5 size-2 shrink-0 rounded-full ring-2 ring-[var(--c-surface)]"
        :style="{ backgroundColor: tone[row.severity]?.dot }"
        aria-hidden="true"
      />

      <div class="min-w-0 flex-1">
        <div class="flex items-baseline gap-2">
          <time
            class="tabular shrink-0 text-2xs text-[var(--c-text-muted)]"
            :datetime="row.occurredAt"
            :title="formatDateTime(row.occurredAt)"
          >
            {{ formatTime(row.occurredAt) }}
          </time>
          <span
            v-if="row.stage"
            class="shrink-0 rounded-[var(--radius-sm)] bg-[var(--c-surface-sunken)] px-1.5 text-2xs font-medium tracking-wide text-[var(--c-text-secondary)] uppercase"
          >
            {{ row.stage.toLowerCase() }}
          </span>
        </div>

        <p
          class="mt-0.5 text-sm wrap-anywhere"
          :style="{ color: tone[row.severity]?.text }"
        >
          {{ row.message }}
        </p>

        <div v-if="row.detail" class="mt-1">
          <button
            type="button"
            class="text-2xs font-medium text-[var(--c-accent)] hover:underline"
            :aria-expanded="expanded.has(row.index)"
            @click="toggle(row.index)"
          >
            {{ expanded.has(row.index) ? 'Hide detail' : 'Show detail' }}
          </button>
          <pre
            v-if="expanded.has(row.index)"
            class="mt-1 overflow-x-auto rounded-[var(--radius-md)] bg-[var(--c-surface-sunken)] p-2 text-2xs leading-relaxed"
          >{{ row.detail }}</pre>
        </div>
      </div>
    </li>
  </ol>

  <p v-else class="text-sm text-[var(--c-text-muted)]">
    No events recorded yet.
  </p>
</template>
