<script setup lang="ts">
import { computed } from 'vue'

/**
 * The one button.
 *
 * Four variants, and the restraint is the point: on a data product a page with three competing
 * primary buttons has no primary action at all. `danger` exists because destructive actions
 * genuinely need a different signal, and it is used only where the action cannot be undone.
 *
 * A pending button keeps its label and its width. Swapping the text for a spinner makes the
 * layout jump and hides what the user just asked for, which is exactly the moment they most
 * want to see it.
 */
const props = withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'danger' | 'ghost'
    size?: 'sm' | 'md'
    type?: 'button' | 'submit'
    disabled?: boolean
    pending?: boolean
    /** Renders the pending state without disabling, for optimistic flows. */
    block?: boolean
  }>(),
  { variant: 'secondary', size: 'md', type: 'button', disabled: false, pending: false, block: false },
)

const classes = computed(() => {
  const base =
    'relative inline-flex items-center justify-center gap-1.5 rounded-[var(--radius-md)] ' +
    'font-medium whitespace-nowrap transition-colors disabled:cursor-not-allowed ' +
    'disabled:opacity-50'

  const size =
    props.size === 'sm'
      ? 'px-2 py-1 text-xs'
      : 'px-3 py-1.5 text-sm'

  const variants: Record<string, string> = {
    primary:
      'bg-[var(--c-accent)] text-[var(--c-accent-text)] enabled:hover:bg-[var(--c-accent-hover)] ' +
      'shadow-[var(--shadow-xs)]',
    secondary:
      'border bg-[var(--c-surface)] text-[var(--c-text)] enabled:hover:bg-[var(--c-surface-hover)]',
    danger:
      'border border-[var(--c-danger)] bg-[var(--c-surface)] text-[var(--c-danger)] ' +
      'enabled:hover:bg-[var(--c-danger-subtle)]',
    ghost:
      'text-[var(--c-text-secondary)] enabled:hover:bg-[var(--c-surface-hover)] enabled:hover:text-[var(--c-text)]',
  }

  return [base, size, variants[props.variant], props.block ? 'w-full' : ''].join(' ')
})
</script>

<template>
  <button :type="type" :class="classes" :disabled="disabled || pending" :aria-busy="pending">
    <span
      v-if="pending"
      class="size-3 shrink-0 animate-spin rounded-full border-2 border-current border-t-transparent"
      aria-hidden="true"
    />
    <slot />
  </button>
</template>
