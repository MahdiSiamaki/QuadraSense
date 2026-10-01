<script setup lang="ts">
import type { SavedQuery } from '@/api/explorer'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import { formatDateTime } from '@/lib/format'

/**
 * My Queries: the signed-in person's saved queries, and nobody else's.
 *
 * Each is a definition. Running one runs it now, against today's data and today's permissions -
 * a query saved while its owner could see SIMs is refused, with the reason, once they cannot.
 */
defineProps<{
  saved: SavedQuery[] | undefined
  isLoading: boolean
  isError: boolean
  error: unknown
  /** The one loaded in the builder, if any. */
  currentId: number | null
}>()

const emit = defineEmits<{
  run: [query: SavedQuery]
  edit: [query: SavedQuery]
  duplicate: [query: SavedQuery]
  rename: [query: SavedQuery]
  remove: [query: SavedQuery]
  retry: []
}>()
</script>

<template>
  <AsyncBoundary
    :is-loading="isLoading"
    :is-error="isError"
    :error="error"
    :is-empty="(saved?.length ?? 0) === 0"
    empty-message="No saved queries yet. Build one, then choose Save."
    @retry="emit('retry')"
  >
    <div class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead class="border-b text-2xs tracking-wide text-[var(--c-text-muted)] uppercase">
          <tr>
            <th scope="col" class="px-4 py-2 font-medium">Name</th>
            <th scope="col" class="px-4 py-2 font-medium">Reads</th>
            <th scope="col" class="px-4 py-2 font-medium">Changed</th>
            <th scope="col" class="px-4 py-2 font-medium"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody class="divide-y">
          <tr
            v-for="q in saved"
            :key="q.id"
            class="hover:bg-[var(--c-surface-hover)]"
            :class="currentId === q.id ? 'bg-[var(--c-surface-sunken)]' : ''"
          >
            <td class="px-4 py-2">
              <span class="block font-medium">{{ q.name }}</span>
              <span v-if="q.description" class="block text-xs text-[var(--c-text-muted)]">{{ q.description }}</span>
            </td>
            <td class="px-4 py-2 text-xs text-[var(--c-text-secondary)]">
              {{ q.query.dataset === 'Events' ? 'Events' : 'Bindings' }}
              <template v-if="(q.query.groupBy?.length ?? 0) > 0 || (q.query.measures?.length ?? 0) > 0"> · grouped</template>
            </td>
            <td class="tabular px-4 py-2 text-xs whitespace-nowrap text-[var(--c-text-secondary)]">
              {{ formatDateTime(q.updatedAt) }}
            </td>
            <td class="px-4 py-2">
              <div class="flex flex-wrap justify-end gap-1">
                <Button size="sm" variant="primary" @click="emit('run', q)">Run</Button>
                <Button size="sm" @click="emit('edit', q)">Edit</Button>
                <Button size="sm" variant="ghost" @click="emit('duplicate', q)">Duplicate</Button>
                <Button size="sm" variant="ghost" @click="emit('rename', q)">Rename</Button>
                <Button size="sm" variant="ghost" @click="emit('remove', q)">Delete</Button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </AsyncBoundary>
</template>
