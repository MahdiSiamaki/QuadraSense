<script setup lang="ts">
import { computed } from 'vue'
import { ICCID_NOTE, iccidFromImsi } from '@/api/iccid'

/**
 * A SIM's ICCID, under its IMSI. Derived, and titled so: the feed carries no ICCID.
 *
 * Shows nothing for an IMSI that has none - a masked one, or one that is not 15 digits.
 */
const props = defineProps<{ imsi: string | null | undefined }>()

const iccid = computed(() => iccidFromImsi(props.imsi))
</script>

<template>
  <span
    v-if="iccid"
    class="tabular block font-mono text-2xs font-normal text-[var(--c-text-muted)]"
    :title="ICCID_NOTE"
  >
    ICCID {{ iccid }}
  </span>
</template>
