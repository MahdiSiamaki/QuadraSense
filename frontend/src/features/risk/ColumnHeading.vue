<script lang="ts">
/**
 * A column heading as the eye reads it: what is counted, then - smaller, on its own line - over
 * what window and before what. "Days with a SIM change, 7 days" becomes "Days with a SIM change"
 * over "7 days"; "Number (MSISDN)" becomes "Number" over "MSISDN".
 *
 * The server's labels keep that order on purpose ("Headings name their unit", RiskEndpoints), and
 * the CSV export uses them whole. Shown whole in one cell, they wrapped to one, two or three lines
 * at random column widths and the header row read as a jumble (2026-10-20).
 */
export function splitHeading(label: string): { main: string; detail: string | null } {
  const paren = /^(.*\S)\s*\(([^()]+)\)$/.exec(label)
  if (paren) return { main: paren[1] ?? label, detail: paren[2] ?? null }
  const comma = label.indexOf(', ')
  return comma < 0 ? { main: label, detail: null } : { main: label.slice(0, comma), detail: label.slice(comma + 2) }
}
</script>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ label: string }>()
const parts = computed(() => splitHeading(props.label))
</script>

<template>
  <span :title="label">
    <span class="block whitespace-nowrap">{{ parts.main }}</span>
    <span v-if="parts.detail" class="block whitespace-nowrap font-normal">{{ parts.detail }}</span>
  </span>
</template>
