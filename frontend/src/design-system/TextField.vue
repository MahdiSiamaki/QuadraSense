<script setup lang="ts">
import { computed, onMounted, ref, useId, watch } from 'vue'

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
 *
 * `revealable` puts a real button inside a password field. It stays in the tab order, says what
 * it will do rather than what is showing, and goes back to hidden once the field is cleared,
 * which is what a failed sign-in does: a password must not stay on screen after the moment it
 * was asked to be seen.
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
    /** A show/hide button, for a password field. */
    revealable?: boolean
    /** The keyboard a phone offers. */
    inputmode?: 'text' | 'numeric' | 'tel' | 'email'
    maxlength?: number
    /** Set out for a short code: centred, in figures of one width. */
    code?: boolean
    /** `lg` on the pages before sign-in, where the form is all there is. */
    size?: 'md' | 'lg'
  }>(),
  {
    type: 'text',
    required: false,
    disabled: false,
    autofocus: false,
    revealable: false,
    code: false,
    size: 'md',
  },
)

defineEmits<{ 'update:modelValue': [value: string] }>()

const id = useId()

const revealed = ref(false)

watch(
  () => props.modelValue,
  (value) => {
    if (!value) revealed.value = false
  },
)

const inputType = computed(() => (props.revealable && revealed.value ? 'text' : props.type))

const input = ref<HTMLInputElement | null>(null)

/*
  The attribute alone only works for the first thing a page or a dialog shows: a browser honours
  `autofocus` once per document. A field that arrives with the next step of a form would otherwise
  leave focus on the body, and a person on a keyboard would have to find their place again. A field
  inside a closed dialog cannot take focus, so this does nothing there, and the dialog's own
  focusing steps read the attribute when it opens.
*/
onMounted(() => {
  if (props.autofocus) input.value?.focus()
})
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

    <div class="relative">
    <input
      :id="id"
      ref="input"
      :type="inputType"
      :value="modelValue"
      :placeholder="placeholder"
      :autocomplete="autocomplete"
      :required="required"
      :disabled="disabled"
      :autofocus="autofocus || undefined"
      :inputmode="inputmode"
      :maxlength="maxlength"
      :aria-invalid="error ? 'true' : undefined"
      :aria-describedby="describedBy"
      :class="[
        size === 'lg' ? 'py-2' : 'py-1.5',
        revealable ? 'pr-9' : '',
        code ? 'tabular text-center font-mono text-lg tracking-[0.3em]' : '',
      ]"
      class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 text-sm text-[var(--c-text)] transition-colors placeholder:text-[var(--c-text-muted)] disabled:opacity-60"
      :style="error ? { borderColor: 'var(--c-danger)' } : undefined"
      @input="$emit('update:modelValue', ($event.target as HTMLInputElement).value)"
    />

    <button
      v-if="revealable"
      type="button"
      class="absolute inset-y-0 right-0 flex w-9 items-center justify-center rounded-r-[var(--radius-md)] text-[var(--c-text-muted)] hover:text-[var(--c-text)] disabled:cursor-not-allowed disabled:opacity-50"
      :aria-label="revealed ? 'Hide password' : 'Show password'"
      :aria-pressed="revealed"
      :disabled="disabled"
      @click="revealed = !revealed"
    >
      <svg
        width="16"
        height="16"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z" />
        <circle cx="12" cy="12" r="3" />
        <path v-if="revealed" d="M4 4l16 16" />
      </svg>
    </button>
    </div>

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
