<script setup lang="ts">
/**
 * The panel every widget sits in.
 *
 * Border-defined rather than shadow-defined: on a dense dashboard, a dozen
 * floating cards reads as clutter, while hairline borders let the grid itself
 * do the organising work.
 */
withDefaults(
  defineProps<{
    title?: string
    subtitle?: string
    /** Removes body padding, for widgets that manage their own (tables, charts). */
    flush?: boolean
  }>(),
  { flush: false },
)
</script>

<template>
  <section
    class="flex flex-col rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-xs)]"
  >
    <header
      v-if="title || $slots.actions"
      class="flex items-start justify-between gap-4 border-b px-4 py-3"
    >
      <div class="min-w-0">
        <h2 v-if="title" class="truncate text-[var(--text-sm)] font-semibold text-[var(--c-text)]">
          {{ title }}
        </h2>
        <p v-if="subtitle" class="mt-0.5 text-pretty break-words text-[var(--text-xs)] text-[var(--c-text-muted)]">
          {{ subtitle }}
        </p>
      </div>
      <div v-if="$slots.actions" class="shrink-0"><slot name="actions" /></div>
    </header>

    <div :class="['min-h-0 flex-1', flush ? '' : 'p-4']">
      <slot />
    </div>

    <footer v-if="$slots.footer" class="border-t px-4 py-2">
      <slot name="footer" />
    </footer>
  </section>
</template>
