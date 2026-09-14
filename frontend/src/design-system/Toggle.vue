<script setup lang="ts">
import { useId } from 'vue'

/**
 * A switch.
 *
 * A real `<button role="switch">` with `aria-checked`, not a styled checkbox: a screen reader
 * then announces "on"/"off" rather than "checked", which is what the control actually means when
 * it enables or disables an account.
 *
 * The state is always spelled out next to it as well. A toggle whose only signal is its position
 * is ambiguous at a glance - people genuinely disagree about which side means on - and this one
 * controls whether a person can sign in.
 */
withDefaults(
  defineProps<{
    modelValue: boolean
    label: string
    description?: string
    onLabel?: string
    offLabel?: string
    disabled?: boolean
    /** Paints the "on" state in the warning colour, for switches that grant something. */
    tone?: 'accent' | 'success'
  }>(),
  { onLabel: 'On', offLabel: 'Off', disabled: false, tone: 'accent' },
)

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

const id = useId()
</script>

<template>
  <div class="flex items-start justify-between gap-4">
    <div class="min-w-0">
      <label :id="`${id}-label`" class="text-[var(--text-sm)] font-medium">{{ label }}</label>
      <p v-if="description" class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
        {{ description }}
      </p>
    </div>

    <div class="flex shrink-0 items-center gap-2">
      <span
        class="tabular text-[var(--text-xs)] font-medium"
        :style="{
          color: modelValue
            ? tone === 'success'
              ? 'var(--c-success)'
              : 'var(--c-accent)'
            : 'var(--c-text-muted)',
        }"
      >
        {{ modelValue ? onLabel : offLabel }}
      </span>

      <button
        type="button"
        role="switch"
        :aria-checked="modelValue"
        :aria-labelledby="`${id}-label`"
        :disabled="disabled"
        class="relative h-5 w-9 shrink-0 rounded-full border transition-colors disabled:cursor-not-allowed disabled:opacity-50"
        :style="{
          backgroundColor: modelValue
            ? tone === 'success'
              ? 'var(--c-success)'
              : 'var(--c-accent)'
            : 'var(--c-surface-sunken)',
          borderColor: modelValue ? 'transparent' : 'var(--c-border-strong)',
        }"
        @click="emit('update:modelValue', !modelValue)"
      >
        <span
          class="absolute top-0.5 size-3.5 rounded-full bg-white shadow-[var(--shadow-xs)] transition-[left]"
          :class="modelValue ? 'left-[1.125rem]' : 'left-0.5'"
          aria-hidden="true"
        />
      </button>
    </div>
  </div>
</template>
