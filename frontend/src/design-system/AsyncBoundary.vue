<script setup lang="ts">
import { ApiError } from '@/api/client'

/**
 * The four async states, as one component.
 *
 * Every data surface in the app has to handle loading, empty, error and loaded.
 * Left to individual components, three of those get forgotten and the fourth is
 * a bare spinner. Making it a single contract means a widget cannot be written
 * without deciding what each state looks like.
 *
 * The skeleton deliberately occupies the same height as the loaded content, so
 * the page does not jump when data arrives - measured aggregates take seconds,
 * which is long enough for a layout shift to be genuinely disorienting.
 */
const props = withDefaults(
  defineProps<{
    isLoading: boolean
    isError: boolean
    error?: unknown
    /** True when the request succeeded but returned nothing. */
    isEmpty?: boolean
    emptyMessage?: string
    /** Height reserved for the skeleton, matching the loaded content. */
    minHeight?: string
  }>(),
  {
    isEmpty: false,
    emptyMessage: 'No data for the current filters.',
    minHeight: '8rem',
  },
)

defineEmits<{ retry: [] }>()

function errorTitle(error: unknown): string {
  if (error instanceof ApiError) return error.problem?.title ?? 'Request failed'
  return 'Something went wrong'
}

function errorDetail(error: unknown): string | null {
  if (error instanceof ApiError) {
    const fieldMessages = error.fieldErrors.map((e) => e.message)
    if (fieldMessages.length) return fieldMessages.join(' ')
    return error.problem?.detail ?? null
  }
  return null
}

/** Shown so a user can quote it to support. Never a stack trace. */
function correlationId(error: unknown): string | null {
  return error instanceof ApiError ? error.correlationId : null
}
</script>

<template>
  <div :style="{ minHeight: props.minHeight }" class="relative">
    <!-- Loading -->
    <div v-if="isLoading" class="animate-pulse space-y-2 p-1" aria-busy="true" aria-live="polite">
      <slot name="skeleton">
        <div class="h-3 w-1/3 rounded bg-[var(--c-surface-sunken)]" />
        <div class="h-3 w-2/3 rounded bg-[var(--c-surface-sunken)]" />
        <div class="h-3 w-1/2 rounded bg-[var(--c-surface-sunken)]" />
      </slot>
      <span class="sr-only">Loading</span>
    </div>

    <!-- Error -->
    <div
      v-else-if="isError"
      class="flex flex-col items-start gap-2 rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold text-[var(--c-text)]">
        {{ errorTitle(error) }}
      </p>
      <p v-if="errorDetail(error)" class="text-xs text-[var(--c-text-secondary)]">
        {{ errorDetail(error) }}
      </p>
      <p v-if="correlationId(error)" class="font-mono text-2xs text-[var(--c-text-muted)]">
        Reference: {{ correlationId(error) }}
      </p>
      <button
        type="button"
        class="mt-1 rounded-[var(--radius-sm)] border border-[var(--c-border-strong)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--c-surface-hover)]"
        @click="$emit('retry')"
      >
        Try again
      </button>
    </div>

    <!-- Empty -->
    <div
      v-else-if="isEmpty"
      class="flex h-full flex-col items-center justify-center gap-1 py-8 text-center"
    >
      <p class="text-sm text-[var(--c-text-secondary)]">{{ emptyMessage }}</p>
      <slot name="empty-action" />
    </div>

    <!-- Loaded -->
    <slot v-else />
  </div>
</template>
