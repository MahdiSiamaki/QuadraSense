<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'

/**
 * Names a query for My Queries.
 *
 * Saving keeps the definition, never the rows: a saved query is run again each time it is
 * opened, against the data as it is then, and is checked again against what the person may see.
 * My Queries are private to their owner.
 */
const props = defineProps<{
  open: boolean
  /** The saved query being changed, if the builder holds one. */
  editing: { id: number; name: string; description: string } | null
  /** A suggested name, when saving something new. */
  suggestion: string
  busy: boolean
  error: unknown
}>()

const emit = defineEmits<{
  close: []
  save: [value: { name: string; description: string; asNew: boolean }]
}>()

const name = ref('')
const description = ref('')

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = props.editing?.name ?? props.suggestion
    description.value = props.editing?.description ?? ''
  },
  { immediate: true },
)

const nameProblem = computed(() => {
  const trimmed = name.value.trim()
  if (trimmed.length === 0) return 'A name is needed.'
  if (trimmed.length > 100) return `${trimmed.length} characters; at most 100.`
  return null
})

const serverProblem = computed(() => {
  const error = props.error
  if (!error) return null
  if (error instanceof ApiError) {
    if (error.status === 409) return error.problem?.detail ?? 'You already have a query by that name.'
    return error.fieldErrors[0]?.message ?? error.problem?.detail ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return 'Could not reach the server.'
})

function save(asNew: boolean) {
  if (nameProblem.value) return
  emit('save', { name: name.value.trim(), description: description.value.trim(), asNew })
}
</script>

<template>
  <Modal
    :open="open"
    :title="editing ? 'Save query' : 'Save to My Queries'"
    description="Saves the query, not its results: it runs again, on the data as it is then, each time you open it. Only you can see it."
    :busy="busy"
    @close="emit('close')"
  >
    <div class="flex flex-col gap-3">
      <TextField
        v-model="name"
        label="Name"
        required
        :error="name.length > 0 ? nameProblem : null"
        placeholder="e.g. Weekly SIM check - Galaxy A01"
      />

      <div class="flex flex-col gap-1">
        <label for="saved-query-notes" class="text-xs font-medium text-[var(--c-text-secondary)]">
          Notes
        </label>
        <textarea
          id="saved-query-notes"
          v-model="description"
          rows="3"
          maxlength="1000"
          class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm"
          placeholder="What it is for - optional"
        />
      </div>

      <p v-if="serverProblem" class="text-xs text-[var(--c-danger)]" role="alert">{{ serverProblem }}</p>
    </div>

    <template #actions>
      <Button variant="ghost" :disabled="busy" @click="emit('close')">Cancel</Button>
      <Button v-if="editing" :disabled="!!nameProblem" :pending="busy" @click="save(true)">Save as new</Button>
      <Button variant="primary" :disabled="!!nameProblem" :pending="busy" @click="save(false)">
        {{ editing ? 'Save changes' : 'Save' }}
      </Button>
    </template>
  </Modal>
</template>
