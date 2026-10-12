<script setup lang="ts">
/**
 * Names a dashboard: new, from a template, a copy, a rename. One dialog for all of them, so the
 * rules a name follows - 1 to 100 characters, one per owner whatever the case - are said in one
 * place, the same way every time.
 */
import { computed, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import { problemText } from './apiProblem'

const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    description?: string
    initialName: string
    initialDescription?: string
    submitLabel: string
    busy: boolean
    error: unknown
    maxName?: number
    maxDescription?: number
    /** Things the person should know before confirming, e.g. widgets left out of a template copy. */
    notes?: string[]
    /** Whether the description is asked for: only where it is saved (a duplicate keeps the original's). */
    withDescription?: boolean
  }>(),
  { description: undefined, initialDescription: '', maxName: 100, maxDescription: 1000, notes: () => [], withDescription: true },
)

const emit = defineEmits<{ close: []; submit: [value: { name: string; description: string }] }>()

const name = ref('')
const text = ref('')

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = props.initialName
    text.value = props.initialDescription
  },
  { immediate: true },
)

const nameProblem = computed(() => {
  const trimmed = name.value.trim()
  if (trimmed.length === 0) return 'A name is needed.'
  if (trimmed.length > props.maxName) return `${trimmed.length} characters; at most ${props.maxName}.`
  return null
})

const textProblem = computed(() =>
  text.value.length > props.maxDescription ? `${text.value.length} characters; at most ${props.maxDescription}.` : null,
)

const serverProblem = computed(() => problemText(props.error))

function submit() {
  if (nameProblem.value || textProblem.value || props.busy) return
  emit('submit', { name: name.value.trim(), description: text.value.trim() })
}
</script>

<template>
  <Modal :open="open" :title="title" :description="description" :busy="busy" @close="emit('close')" @submit="submit">
    <div class="flex flex-col gap-3">
      <TextField v-model="name" label="Name" required autofocus :error="name.length > 0 ? nameProblem : null" />

      <div v-if="withDescription" class="flex flex-col gap-1">
        <label for="dashboard-description" class="text-xs font-medium text-[var(--c-text-secondary)]">Description</label>
        <textarea
          id="dashboard-description"
          v-model="text"
          rows="3"
          :maxlength="maxDescription"
          class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm"
          placeholder="What it is for - optional"
        />
        <p v-if="textProblem" class="text-xs text-[var(--c-danger-text)]">{{ textProblem }}</p>
      </div>

      <ul v-if="notes.length" class="space-y-1 rounded-[var(--radius-md)] bg-[var(--c-surface-sunken)] px-3 py-2 text-xs text-[var(--c-text-secondary)]">
        <li v-for="note in notes" :key="note">{{ note }}</li>
      </ul>

      <p v-if="serverProblem" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ serverProblem }}</p>
    </div>

    <template #actions>
      <Button variant="ghost" :disabled="busy" @click="emit('close')">Cancel</Button>
      <Button type="submit" variant="primary" :disabled="!!nameProblem || !!textProblem" :pending="busy">{{ submitLabel }}</Button>
    </template>
  </Modal>
</template>
