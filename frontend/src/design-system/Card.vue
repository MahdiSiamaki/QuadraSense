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
    class="flex min-w-0 flex-col rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-xs)]"
  >
    <!--
      The header wraps. Its actions used to be shrink-0 in a row that could not wrap, so on a
      narrow card they kept their full width, overflowed the card, and squeezed the title to
      nothing - the subtitle then broke one word per line. The title keeps at least 10rem; when the
      actions do not fit beside that, they move to their own line, still at the right. (16rem made
      the Explorer panel's Back and close buttons wrap in its 24rem aside.)
    -->
    <header
      v-if="title || $slots.actions"
      class="flex flex-wrap items-start justify-between gap-x-4 gap-y-2 border-b px-4 py-3"
    >
      <div class="min-w-0 flex-[1_1_10rem]">
        <h2 v-if="title" class="truncate text-sm font-semibold text-[var(--c-text)]">
          {{ title }}
        </h2>
        <p v-if="subtitle" class="mt-0.5 text-pretty break-words text-xs text-[var(--c-text-muted)]">
          {{ subtitle }}
        </p>
      </div>
      <div v-if="$slots.actions" class="ml-auto max-w-full min-w-0"><slot name="actions" /></div>
    </header>

    <div :class="['min-h-0 flex-1', flush ? '' : 'p-4']">
      <slot />
    </div>

    <footer v-if="$slots.footer" class="border-t px-4 py-2">
      <slot name="footer" />
    </footer>
  </section>
</template>
