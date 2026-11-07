<script setup lang="ts">
/**
 * One line of outcome inside a form, in the place the eye already is.
 *
 * `role="alert"` for what went wrong, so it is announced as it appears; `role="status"` for what
 * went right, which can wait its turn. Colour is never the only signal: the words say it.
 */
withDefaults(defineProps<{ tone?: 'danger' | 'warning' | 'success' | 'info' }>(), { tone: 'danger' })
</script>

<template>
  <p
    class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
    :style="{
      borderColor: `var(--c-${tone === 'info' ? 'border' : tone})`,
      backgroundColor: tone === 'info' ? 'var(--c-surface-sunken)' : `var(--c-${tone}-subtle)`,
      color: tone === 'info' ? 'var(--c-text-secondary)' : `var(--c-${tone}-text)`,
    }"
    :role="tone === 'danger' || tone === 'warning' ? 'alert' : 'status'"
  >
    <slot />
  </p>
</template>
