<script setup lang="ts">
import { computed } from 'vue'

/**
 * The one button.
 *
 * Four variants, and the restraint is the point: on a data product a page with three competing
 * primary buttons has no primary action at all. `danger` exists because destructive actions
 * genuinely need a different signal, and it is used only where the action cannot be undone.
 *
 * A press gives a little under the pointer (a 3% scale, 120ms): feedback that the click registered
 * before the result can arrive. Not with reduced motion, and not while disabled. With reduced
 * motion the pending spinner pulses instead of turning: a still ring with a gap reads as stalled.
 *
 * A pending button keeps its label and its width. Swapping the text for a spinner makes the
 * layout jump and hides what the user just asked for, which is exactly the moment they most
 * want to see it.
 */
const props = withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'danger' | 'ghost'
    /** sm in dense rows, md by default, lg beside a full-size field (38px, the field's height). */
    size?: 'sm' | 'md' | 'lg'
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
    'font-medium whitespace-nowrap transition-[color,background-color,border-color,scale] ' +
    'duration-(--duration-press) ease-out motion-safe:enabled:active:scale-[0.97] disabled:cursor-not-allowed ' +
    'disabled:opacity-50'

  const size =
    props.size === 'sm'
      ? 'px-2 py-1 text-xs'
      : props.size === 'lg'
        ? 'px-4 py-2 text-sm'
        : 'px-3 py-1.5 text-sm'

  // Every variant has a 1px border - transparent where none is drawn - so a primary button and a
  // secondary one beside it in a dialog footer are the same height, not 1px inset top and bottom.
  const variants: Record<string, string> = {
    primary:
      'border border-transparent bg-[var(--c-accent)] text-[var(--c-accent-text)] ' +
      'enabled:hover:bg-[var(--c-accent-hover)] ' +
      'shadow-[var(--shadow-xs)]',
    secondary:
      'border bg-[var(--c-surface)] text-[var(--c-text)] enabled:hover:bg-[var(--c-surface-hover)]',
    danger:
      'border border-[var(--c-danger)] bg-[var(--c-surface)] text-[var(--c-danger-text)] ' +
      'enabled:hover:bg-[var(--c-danger-subtle)]',
    ghost:
      'border border-transparent text-[var(--c-text-secondary)] ' +
      'enabled:hover:bg-[var(--c-surface-hover)] enabled:hover:text-[var(--c-text)]',
  }

  return [base, size, variants[props.variant], props.block ? 'w-full' : ''].join(' ')
})
</script>

<template>
  <button :type="type" :class="classes" :disabled="disabled || pending" :aria-busy="pending">
    <span
      v-if="pending"
      class="size-3 shrink-0 animate-spin rounded-full border-2 border-current border-t-transparent motion-reduce:animate-pulse"
      aria-hidden="true"
    />
    <slot />
  </button>
</template>
