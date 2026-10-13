<script setup lang="ts">
/**
 * Somebody saved this dashboard after the edit began (ADR-016 §2). Nothing is overwritten: the draft
 * is kept, and the person chooses - take the saved version (their changes are discarded), keep their
 * changes as a copy of their own, or go back to editing.
 */
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import { formatDateTime } from '@/lib/format'

defineProps<{
  open: boolean
  updatedBy: string | null
  updatedAt: string | null
  canCopy: boolean
}>()

const emit = defineEmits<{ close: []; reload: []; copy: [] }>()
</script>

<template>
  <Modal :open="open" title="This dashboard changed while you were editing" @close="emit('close')">
    <div class="flex flex-col gap-2 text-sm">
      <p>
        <template v-if="updatedBy">{{ updatedBy }} saved it</template>
        <template v-else>It was saved</template>
        <template v-if="updatedAt"> at {{ formatDateTime(updatedAt) }}</template>
        - after you started. Your changes have not been saved, and nothing was overwritten.
      </p>
      <ul class="list-disc space-y-1 pl-5 text-xs text-[var(--c-text-secondary)]">
        <li><strong>Load their version</strong> discards your changes and shows what is saved now.</li>
        <li v-if="canCopy"><strong>Save mine as a copy</strong> keeps your changes as a new private dashboard.</li>
        <li><strong>Keep editing</strong> leaves both as they are.</li>
      </ul>
    </div>
    <template #actions>
      <Button variant="ghost" autofocus @click="emit('close')">Keep editing</Button>
      <Button v-if="canCopy" @click="emit('copy')">Save mine as a copy</Button>
      <Button variant="danger" @click="emit('reload')">Load their version</Button>
    </template>
  </Modal>
</template>
