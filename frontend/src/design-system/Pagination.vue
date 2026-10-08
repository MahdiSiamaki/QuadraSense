<script setup lang="ts">
import { computed } from 'vue'
import { formatFull } from '@/lib/format'

/**
 * Paging for a table.
 *
 * States the range and the total in words - "1–25 of 312" - rather than only offering arrows.
 * On an access-review screen the total is itself information: "312 users" is the answer to a
 * question somebody is about to ask, and a bare pair of arrows hides it.
 *
 * Page numbers are not rendered as a strip of buttons. With a few hundred rows that strip is
 * either truncated with ellipses nobody reads or long enough to wrap, and neither beats
 * previous/next plus an honest range.
 */
const props = defineProps<{
  page: number
  pageSize: number
  total: number
  /** Dims the controls during a refetch without collapsing the layout. */
  loading?: boolean
}>()

const emit = defineEmits<{ 'update:page': [page: number] }>()

const lastPage = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))
const first = computed(() => (props.total === 0 ? 0 : (props.page - 1) * props.pageSize + 1))
const last = computed(() => Math.min(props.page * props.pageSize, props.total))

const buttonClass =
  'rounded-[var(--radius-md)] border px-2 py-1 text-xs font-medium ' +
  'enabled:hover:bg-[var(--c-surface-hover)] disabled:cursor-not-allowed disabled:opacity-40'
</script>

<template>
  <div
    class="flex flex-wrap items-center justify-between gap-3 border-t px-4 py-2.5"
    :class="loading ? 'opacity-60' : ''"
  >
    <p class="tabular text-xs text-[var(--c-text-muted)]">
      <template v-if="total === 0">No results</template>
      <template v-else>
        <!-- formatFull, not toLocaleString(): en-US grouping on every browser, not the reader's locale. -->
        <span class="font-medium text-[var(--c-text-secondary)]">{{ formatFull(first) }}–{{ formatFull(last) }}</span>
        of {{ formatFull(total) }}
      </template>
    </p>

    <nav class="flex items-center gap-1.5" aria-label="Pagination">
      <button
        type="button"
        :class="buttonClass"
        :disabled="page <= 1"
        @click="emit('update:page', page - 1)"
      >
        Previous
      </button>
      <span class="tabular px-1 text-xs text-[var(--c-text-muted)]">
        {{ formatFull(page) }} / {{ formatFull(lastPage) }}
      </span>
      <button
        type="button"
        :class="buttonClass"
        :disabled="page >= lastPage"
        @click="emit('update:page', page + 1)"
      >
        Next
      </button>
    </nav>
  </div>
</template>
