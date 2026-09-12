<script setup lang="ts">
import { computed } from 'vue'
import { WIDGET_LIST, type WidgetDefinition } from './widgets/registry'

defineProps<{ open: boolean }>()
const emit = defineEmits<{ add: [type: string]; close: [] }>()

/** Grouped by category so the gallery reads as a menu rather than a list. */
const grouped = computed(() => {
  const map = new Map<string, WidgetDefinition[]>()
  for (const def of WIDGET_LIST) {
    const list = map.get(def.category) ?? []
    list.push(def)
    map.set(def.category, list)
  }
  return [...map.entries()]
})
</script>

<template>
  <div
    v-if="open"
    class="rounded-[var(--radius-lg)] border border-dashed bg-[var(--c-surface-sunken)] p-4"
  >
    <div class="mb-3 flex items-center justify-between">
      <div>
        <h2 class="text-[var(--text-sm)] font-semibold">Add a widget</h2>
        <p class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
          Each one is configurable after you add it, and can be added more than once.
        </p>
      </div>
      <button
        type="button"
        class="rounded-[var(--radius-sm)] border bg-[var(--c-surface)] px-2.5 py-1 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
        @click="emit('close')"
      >
        Done
      </button>
    </div>

    <div v-for="[category, items] in grouped" :key="category" class="mb-3 last:mb-0">
      <p class="mb-1.5 text-[var(--text-2xs)] font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">
        {{ category }}
      </p>
      <div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
        <button
          v-for="def in items"
          :key="def.type"
          type="button"
          class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] p-3 text-left transition-colors hover:border-[var(--c-accent)] hover:bg-[var(--c-surface-hover)]"
          @click="emit('add', def.type)"
        >
          <span class="block text-[var(--text-xs)] font-semibold">{{ def.name }}</span>
          <span class="mt-1 block text-[var(--text-2xs)] leading-relaxed text-[var(--c-text-muted)]">
            {{ def.description }}
          </span>
        </button>
      </div>
    </div>
  </div>
</template>
