<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { formatTimeLeft } from '@/lib/format'
import { useUploadQueue } from './uploadQueue'

/**
 * Uploads still going, shown from any page.
 *
 * The upload queue outlives the Import Center (uploadQueue.ts), so somewhere else has to say it
 * is still running - otherwise the next thing the operator learns about the file is the browser
 * asking whether to leave the page. Not on the Import Center itself, which shows every file.
 */
const route = useRoute()
const { totals } = useUploadQueue()

const shown = computed(() => totals.value !== null && route.name !== 'imports')
const left = computed(() => (totals.value ? formatTimeLeft(totals.value.msLeft) : null))
</script>

<template>
  <RouterLink
    v-if="shown && totals"
    to="/imports"
    class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-2xs text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
    :title="`Uploading ${totals.files} file(s) - open the Import Center to follow or cancel them`"
    aria-live="polite"
  >
    <span class="size-1.5 animate-pulse rounded-full bg-[var(--c-accent)]" aria-hidden="true" />
    Uploading {{ totals.files === 1 ? '1 file' : `${totals.files} files` }} ·
    {{ Math.floor(totals.percent) }}%<template v-if="left"> · {{ left }}</template>
  </RouterLink>
</template>
