<script setup lang="ts">
/**
 * A yes/no question with consequences - leaving with unsaved changes, cancelling an edit. The safe
 * answer is the first button and the default focus; the destructive one says exactly what it does.
 */
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'

defineProps<{
  open: boolean
  title: string
  message: string
  confirmLabel: string
  cancelLabel?: string
  danger?: boolean
}>()

const emit = defineEmits<{ close: []; confirm: [] }>()
</script>

<template>
  <Modal :open="open" :title="title" size="sm" @close="emit('close')">
    <p class="text-sm">{{ message }}</p>
    <template #actions>
      <Button autofocus @click="emit('close')">{{ cancelLabel ?? 'Keep editing' }}</Button>
      <Button :variant="danger ? 'danger' : 'primary'" @click="emit('confirm')">{{ confirmLabel }}</Button>
    </template>
  </Modal>
</template>
