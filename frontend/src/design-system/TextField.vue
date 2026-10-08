<script setup lang="ts">
import { computed, useId } from 'vue'

/**
 * A labelled input.
 *
 * Three things it does that a bare `<input>` does not, and each of them is an accessibility
 * requirement rather than a nicety: the label is a real `<label for>`, the hint and the error are
 * wired to the input through `aria-describedby`, and an invalid field carries `aria-invalid` so
 * it is announced as invalid rather than merely drawn in red.
 *
 * The error replaces the hint rather than stacking below it. Two lines of guidance under one
 * field is where people stop reading either.
 */
const props = withDefaults(
  defineProps<{
    label: string
    modelValue: string
    type?: string
    hint?: string
    error?: string | null
    placeholder?: string
    autocomplete?: string
    required?: boolean
    disabled?: boolean
    /** Rendered small and muted to the right of the label. */
    optionalNote?: string
    /** Where a dialog's focus starts: the first field, not the close button. */
    autofocus?: boolean
  }>(),
  { type: 'text', required: false, disabled: false, autofocus: false },
)

defineEmits<{ 'update:modelValue': [value: string] }>()

const id = useId()
const describedBy = computed(() =>
  props.error ? `${id}-error` : props.hint ? `${id}-hint` : undefined,
)
</script>

<template>
  <div class="flex flex-col gap-1">
    <div class="flex items-baseline justify-between gap-2">
      <label :for="id" class="text-xs font-medium text-[var(--c-text-secondary)]">
        {{ label }}
        <span v-if="required" class="text-[var(--c-danger-text)]" aria-hidden="true">*</span>
      </label>
      <span v-if="optionalNote" class="text-2xs text-[var(--c-text-muted)]">
        {{ optionalNote }}
      </span>
    </div>

    <input
      :id="id"
      :type="type"
      :value="modelValue"
      :placeholder="placeholder"
      :autocomplete="autocomplete"
      :required="required"
      :disabled="disabled"
      :autofocus="autofocus || undefined"
      :aria-invalid="error ? 'true' : undefined"
      :aria-describedby="describedBy"
      class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm text-[var(--c-text)] transition-colors placeholder:text-[var(--c-text-muted)] disabled:opacity-60"
      :style="error ? { borderColor: 'var(--c-danger)' } : undefined"
      @input="$emit('update:modelValue', ($event.target as HTMLInputElement).value)"
    />

    <p
      v-if="error"
      :id="`${id}-error`"
      class="text-xs text-[var(--c-danger-text)]"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="hint"
      :id="`${id}-hint`"
      class="text-xs text-[var(--c-text-muted)]"
    >
      {{ hint }}
    </p>
  </div>
</template>
