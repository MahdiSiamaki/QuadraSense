<script setup lang="ts">
import { computed } from 'vue'
import Button from './Button.vue'
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
    /**
     * Space between the blocks of the loaded content.
     *
     * The loaded slot is laid out here, not by the page: content rendered through one
     * `<template v-if>` arrives as several sibling blocks, and the page's own `gap` stops at this
     * component. That is how the Risk page's warning banner, tab bar and card grid came to touch -
     * and the user and role pages, and every other page with more than one block inside.
     */
    gap?: 'none' | 'sm' | 'md' | 'lg'
  }>(),
  {
    isEmpty: false,
    emptyMessage: 'No data for the current filters.',
    minHeight: '8rem',
    gap: 'lg',
  },
)

/** Literal class names, so Tailwind finds them. lg is the pages' 20px rhythm. */
const GAPS = { none: '', sm: 'gap-2', md: 'gap-3', lg: 'gap-5' } as const

/**
 * The skeleton's height is reserved only while there is no content: once loaded, the content
 * decides. Reserving it always left a blank band under short content - under a card's pagination
 * bar, for one.
 */
const loaded = computed(() => !props.isLoading && !props.isError && !props.isEmpty)

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
  <div
    :style="{ minHeight: loaded ? undefined : props.minHeight }"
    :class="['relative flex flex-col', GAPS[props.gap]]"
  >
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
      <!-- The design-system button: same radius, height and states as every other one. -->
      <Button size="sm" class="mt-1" @click="$emit('retry')">Try again</Button>
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
