<script setup lang="ts">
import type { WidgetDefinition } from '../types/widget'

/**
 * What a widget shows, how it counts and what it needs - the Add Widget dialog's details for the
 * selected entry. Drawn in the side panel on a wide screen and in a disclosure below the catalogue on a
 * narrow one, so a phone sees the same unit, time scope and caveats before adding.
 */
defineProps<{
  definition: WidgetDefinition<any>
  filters: string
  needs: string[]
  missing: string[]
}>()
</script>

<template>
  <div>
    <dl class="space-y-2 text-xs">
      <div><dt class="font-medium text-[var(--c-text-secondary)]">Counts</dt><dd>{{ definition.unit }}</dd></div>
      <div><dt class="font-medium text-[var(--c-text-secondary)]">Time</dt><dd>{{ definition.timeScope }}</dd></div>
      <div><dt class="font-medium text-[var(--c-text-secondary)]">Filters</dt><dd>{{ filters }}</dd></div>
      <div><dt class="font-medium text-[var(--c-text-secondary)]">Source</dt><dd class="font-mono text-2xs [overflow-wrap:anywhere]">{{ definition.source }}</dd></div>
      <div>
        <dt class="font-medium text-[var(--c-text-secondary)]">Needs</dt>
        <dd>{{ needs.join(', ') || 'Nothing beyond the dashboard' }}</dd>
      </div>
    </dl>
    <ul v-if="definition.caveats.length" class="mt-3 list-disc space-y-1 pl-4 text-2xs text-[var(--c-text-muted)]">
      <li v-for="c in definition.caveats" :key="c">{{ c }}</li>
    </ul>
    <p v-if="missing.length" class="mt-3 text-xs text-[var(--c-warning-text)]" role="note">
      You cannot add this: it needs {{ missing.join(', ') }}. A dashboard never grants access to data.
    </p>
  </div>
</template>
