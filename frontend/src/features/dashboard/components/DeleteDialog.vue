<script setup lang="ts">
/**
 * Deletes a dashboard - only the arrangement. Typing its name is the confirmation: a delete cannot
 * be undone, and a dashboard shared with others disappears for them too. No data is touched.
 *
 * A destructive confirmation does not submit on Enter (16-ui, Dialogs): the button is the only way.
 */
import { computed, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import { problemText } from './apiProblem'

const props = defineProps<{ open: boolean; name: string; shared: boolean; busy: boolean; error: unknown }>()
const emit = defineEmits<{ close: []; confirm: [] }>()

const typed = ref('')
watch(
  () => props.open,
  (open) => {
    if (open) typed.value = ''
  },
)

const matches = computed(() => typed.value.trim() === props.name.trim())
const serverProblem = computed(() => problemText(props.error))
</script>

<template>
  <Modal :open="open" title="Delete dashboard" size="sm" :busy="busy" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <p>
        This deletes the dashboard <strong>{{ name }}</strong> - its widgets and layout. It cannot be undone.
        No data is deleted: every figure on it stays where it comes from.
      </p>
      <p v-if="shared" class="text-xs text-[var(--c-warning-text)]">It is shared: everyone it is shared with loses it too.</p>
      <TextField v-model="typed" :label="`Type ${name} to confirm`" autocomplete="off" />
      <p v-if="serverProblem" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ serverProblem }}</p>
    </div>
    <template #actions>
      <Button variant="ghost" :disabled="busy" @click="emit('close')">Cancel</Button>
      <Button variant="danger" :disabled="!matches" :pending="busy" @click="emit('confirm')">Delete dashboard</Button>
    </template>
  </Modal>
</template>
