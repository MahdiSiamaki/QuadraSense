<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import ProgressBar from '@/design-system/ProgressBar.vue'
import type { ImportJobSummary, ImportProgress, StageDuration } from '@/api/imports'
import { estimateImport, stageLabel } from '@/lib/import-estimate'
import { formatBytes, formatDuration, formatTimeLeft } from '@/lib/format'

/**
 * Where a running import is, and roughly how long it has left.
 *
 * The stages in order, the current one's own progress - its steps for AGGREGATING, bytes while it
 * reads the file - and the time left, estimated from the source's recent imports
 * (lib/import-estimate.ts). AGGREGATING is most of a daily import and reads no file: before
 * 2026-10-26 the page showed an indeterminate sweep for all of it, and the previous stage's
 * percentage under its name.
 *
 * The clock ticks here, once a second, so the time so far and the time left move between the
 * page's polls without asking the server anything.
 */
const props = defineProps<{
  summary: ImportJobSummary
  progress: ImportProgress | null
  typicals: StageDuration[] | undefined
}>()

const now = ref(Date.now())
let timer: ReturnType<typeof setInterval> | undefined
onMounted(() => (timer = setInterval(() => (now.value = Date.now()), 1000)))
onBeforeUnmount(() => clearInterval(timer))

const estimate = computed(() => estimateImport(props.summary, props.progress, props.typicals, now.value))

/** The bar for the stage now running: its steps, its bytes, or a sweep when it counts neither. */
const bar = computed(() => {
  const p = props.progress
  const stage = props.summary.currentStage
  if (!stage) return null
  if (!p || p.stage !== stage) return { percent: null, label: stageLabel(stage), detail: undefined }
  if (p.step && p.stepIndex && p.stepCount) {
    return { percent: p.percent, label: `Step ${p.stepIndex} of ${p.stepCount}: ${p.step}`, detail: undefined }
  }
  return {
    percent: p.percent,
    label: stageLabel(stage),
    detail: `${formatBytes(p.bytesProcessed)} of ${p.bytesExpected ? formatBytes(p.bytesExpected) : 'unknown'} read`,
  }
})

const line = computed(() => {
  const e = estimate.value
  if (!e) return null
  const basis = `from the last ${e.basedOn} ${e.basedOn === 1 ? 'import' : 'imports'}`
  if (e.overdue) {
    const usual = e.stages.find((s) => s.state === 'current')?.typicalMs ?? 0
    const after = formatTimeLeft(e.msLeft)
    return `Taking longer than usual: this stage usually takes ${formatDuration(usual)} (${basis}).${
      after && (e.msLeft ?? 0) >= 60_000 ? ` After it, ${after.replace(' left', '')} more.` : ''
    }`
  }
  const left = formatTimeLeft(e.msLeft)
  if (!left) return null
  return e.waiting ? `Once it starts: ${left.replace(' left', '')} (${basis})` : `${left[0]!.toUpperCase()}${left.slice(1)}, estimated ${basis}`
})
</script>

<template>
  <div class="space-y-4">
    <ol v-if="estimate" class="flex flex-wrap gap-x-5 gap-y-2" aria-label="Stages">
      <li
        v-for="s in estimate.stages"
        :key="s.stage"
        class="flex items-center gap-1.5 text-xs"
        :aria-current="s.state === 'current' ? 'step' : undefined"
      >
        <span
          class="grid size-4 shrink-0 place-items-center rounded-full border text-2xs"
          :class="{
            'border-[var(--c-success)] bg-[var(--c-success-subtle)] text-[var(--c-success-text)]': s.state === 'done',
            'animate-pulse border-[var(--c-accent)] bg-[var(--c-accent-subtle)]': s.state === 'current',
            'border-[var(--c-border)]': s.state === 'upcoming',
          }"
          aria-hidden="true"
        >
          <template v-if="s.state === 'done'">✓</template>
        </span>
        <span
          :class="s.state === 'upcoming' ? 'text-[var(--c-text-muted)]' : 'font-medium text-[var(--c-text)]'"
        >
          {{ stageLabel(s.stage) }}
        </span>
        <span v-if="s.state === 'current' && s.elapsedMs !== null" class="tabular text-[var(--c-text-muted)]">
          {{ formatDuration(s.elapsedMs) }}<template v-if="s.typicalMs"> of usually {{ formatDuration(s.typicalMs) }}</template>
        </span>
        <span v-else-if="s.state === 'upcoming' && s.typicalMs" class="tabular text-[var(--c-text-muted)]">
          ~{{ formatDuration(s.typicalMs) }}
        </span>
      </li>
    </ol>

    <ProgressBar v-if="bar" :percent="bar.percent" :label="bar.label" :detail="bar.detail" />

    <p v-if="line" class="text-xs text-[var(--c-text-secondary)]" aria-live="polite">{{ line }}</p>
    <p v-else-if="!typicals?.length" class="text-xs text-[var(--c-text-muted)]">
      No time estimate yet: it needs this source's earlier imports to go by.
    </p>
  </div>
</template>
