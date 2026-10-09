<script setup lang="ts">
import type { RiskStatus } from '@/api/risk'
import { formatDate } from '@/lib/format'

/**
 * The two states that change how a risk figure may be read: not built yet, and built but stale.
 * Said on every risk widget, because each may be the only one on a dashboard.
 */
defineProps<{ status: RiskStatus | undefined }>()
</script>

<template>
  <p
    v-if="status && !status.ready"
    class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 text-xs text-[var(--c-text-secondary)]"
    role="status"
  >
    {{ status.notReady ?? 'The risk measures are not built yet.' }}
  </p>
  <p
    v-else-if="status?.run?.stale"
    class="rounded-[var(--radius-md)] border border-[var(--c-warning)] bg-[var(--c-warning-subtle)] px-3 py-2 text-xs text-[var(--c-text)]"
    role="status"
  >
    {{ status.run.stale }} Until then, this is as of {{ formatDate(status.run.asOf) }}.
  </p>
</template>
