<script setup lang="ts">
/**
 * The catalogue's and card headers' icons, drawn inline the way the rest of the app draws its few
 * icons (App.vue's gear, ThemeToggle): stroke in currentColor, 24-unit box, round caps. Decorative -
 * every icon sits beside the words it stands for, so it is hidden from assistive technology.
 */
import type { WidgetIconName } from '../types/widget'

defineProps<{ name: WidgetIconName; size?: number }>()

const PATHS: Record<WidgetIconName, string[]> = {
  kpi: ['M4 19V5', 'M4 19h16', 'M8 15l3-4 3 2 4-6'],
  trend: ['M3 17l6-6 4 4 8-8', 'M15 7h6v6'],
  bar: ['M4 20h16', 'M7 16V9', 'M12 16V5', 'M17 16v-4'],
  table: ['M4 5h16v14H4z', 'M4 10h16', 'M4 15h16', 'M10 5v14'],
  composition: ['M4 8h16v8H4z', 'M10 8v8', 'M15 8v8'],
  capability: ['M5 12.5l4 4 10-10', 'M5 7h6'],
  calendar: ['M5 6h14v14H5z', 'M5 10h14', 'M9 4v4', 'M15 4v4'],
  movement: ['M7 7h11l-3-3', 'M17 17H6l3 3'],
  compare: ['M8 4v16', 'M16 4v16', 'M4 9h8', 'M12 15h8'],
  shield: ['M12 3l7 3v5c0 5-3 8-7 10-4-2-7-5-7-10V6z'],
  list: ['M9 6h11', 'M9 12h11', 'M9 18h11', 'M4.5 6h.01', 'M4.5 12h.01', 'M4.5 18h.01'],
  quality: ['M12 3v4', 'M12 17v4', 'M3 12h4', 'M17 12h4', 'M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6'],
  clock: ['M12 7v5l3 2', 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18'],
  upload: ['M12 16V4', 'M7 9l5-5 5 5', 'M5 20h14'],
  server: ['M4 5h16v6H4z', 'M4 13h16v6H4z', 'M8 8h.01', 'M8 16h.01'],
  tag: ['M4 4h8l8 8-8 8-8-8z', 'M8 8h.01'],
  search: ['M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14', 'M20 20l-4-4'],
  query: ['M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3', 'M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6', 'M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3'],
  device: ['M8 3h8a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1', 'M11 18h2'],
}
</script>

<template>
  <svg
    :width="size ?? 16"
    :height="size ?? 16"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    class="shrink-0"
  >
    <path v-for="d in PATHS[name]" :key="d" :d="d" />
  </svg>
</template>
