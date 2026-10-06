<script setup lang="ts">
/**
 * A main column and, when there is one, a detail panel beside it.
 *
 * The layout for "a list, and the thing you opened from it": the Explorer's results and entity
 * panel, the Risk page's lists and entity card. One component so the two cannot drift apart again.
 *
 * - **Everything the page shows about the selection goes in the main column**, under the list -
 *   the timeline included. Placed after the two columns instead, it landed full-width under the
 *   taller of them and left the space beside short results empty.
 * - **The aside is sticky and never taller than the window.** Stuck at the top while the main
 *   column scrolls, it scrolls inside itself, so its lower half is always reachable.
 * - Below xl the columns stack, main first.
 */
defineProps<{
  /** Whether the aside is shown; without it the main column takes the full width. */
  aside: boolean
}>()
</script>

<template>
  <div class="grid items-start gap-5" :class="aside ? 'xl:grid-cols-[minmax(0,1fr)_24rem]' : 'grid-cols-1'">
    <div class="flex min-w-0 flex-col gap-5">
      <slot />
    </div>
    <aside
      v-if="aside"
      class="min-w-0 xl:sticky xl:top-20 xl:max-h-[calc(100dvh-6rem)] xl:overflow-y-auto xl:overscroll-contain"
    >
      <slot name="aside" />
    </aside>
  </div>
</template>
