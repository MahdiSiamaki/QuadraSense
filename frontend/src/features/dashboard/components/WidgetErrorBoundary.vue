<script setup lang="ts">
/**
 * One broken widget never blanks the dashboard (ADR-016 §6). An error thrown while drawing a widget
 * stops here: the card says so and offers to try again, and every other card keeps working.
 */
import { onErrorCaptured, ref } from 'vue'
import Button from '@/design-system/Button.vue'

const failed = ref<string | null>(null)
const attempt = ref(0)

onErrorCaptured((error) => {
  failed.value = error instanceof Error ? error.message : String(error)
  console.error('A dashboard widget failed to draw:', error)
  return false
})

function retry() {
  failed.value = null
  attempt.value += 1
}
</script>

<template>
  <div v-if="failed" role="alert" class="flex h-full flex-col items-start justify-center gap-2 text-sm">
    <p class="font-medium">This widget could not be drawn.</p>
    <p class="text-xs text-[var(--c-text-muted)]">The rest of the dashboard is unaffected.</p>
    <Button size="sm" @click="retry">Try again</Button>
  </div>
  <div v-else :key="attempt" class="h-full min-h-0">
    <slot />
  </div>
</template>
