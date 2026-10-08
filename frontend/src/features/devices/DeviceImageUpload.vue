<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from '@/design-system/Button.vue'
import { api } from '@/api/client'
import { ApiError } from '@/api/client'
import { formatBytes, formatDateTime } from '@/lib/format'

/**
 * Curating one device model's photograph.
 *
 * The GSMA TAC record carries no imagery and this deployment has no internet access, so the
 * catalogue is filled in by hand, one model at a time, by whoever holds `device.image.manage`.
 * This is that control: small, out of the way, and visible only to those who can use it.
 *
 * The server decides the content type from the file's own magic bytes rather than from what the
 * browser claims, so the check below is a courtesy that saves a round trip — not the one that
 * matters.
 */
const props = defineProps<{
  tac: string
  hasImage: boolean
  sourceNote: string | null
  updatedAt: string | null
}>()

const emit = defineEmits<{ changed: [] }>()

/** Matches the database's own CHECK constraint, so a rejection here is not a surprise later. */
const MAX_BYTES = 512 * 1024

const open = ref(false)
const file = ref<File | null>(null)
const note = ref('')
const busy = ref(false)
const problem = ref<string | null>(null)

const chosen = computed(() => file.value)

function pick(event: Event) {
  const input = event.target as HTMLInputElement
  const picked = input.files?.[0] ?? null
  problem.value = null

  if (picked && picked.size > MAX_BYTES) {
    problem.value = `That file is ${formatBytes(picked.size)}. The limit is ${formatBytes(MAX_BYTES)}.`
    file.value = null
    return
  }

  file.value = picked
}

async function upload() {
  if (!file.value) return

  busy.value = true
  problem.value = null

  try {
    const body = new FormData()
    body.append('file', file.value)
    body.append('sourceNote', note.value.trim())

    await api.upload(`/api/v1/devices/${props.tac}/image`, body)

    file.value = null
    note.value = ''
    open.value = false
    emit('changed')
  } catch (error) {
    problem.value =
      error instanceof ApiError
        ? (error.fieldErrors[0]?.message ?? error.problem?.title ?? `Server responded ${error.status}.`)
        : 'Could not reach the server.'
  } finally {
    busy.value = false
  }
}

async function remove() {
  busy.value = true
  problem.value = null

  try {
    await api.delete(`/api/v1/devices/${props.tac}/image`)
    open.value = false
    emit('changed')
  } catch (error) {
    problem.value =
      error instanceof ApiError
        ? (error.problem?.title ?? `Server responded ${error.status}.`)
        : 'Could not reach the server.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="shrink-0">
    <Button size="sm" @click="open = !open">{{ hasImage ? 'Replace image' : 'Add image' }}</Button>

    <div
      v-if="open"
      class="mt-2 w-72 rounded-[var(--radius-md)] border bg-[var(--c-surface)] p-3 shadow-sm"
    >
      <p class="text-xs text-[var(--c-text-secondary)]">
        PNG, JPEG or WebP, up to {{ formatBytes(MAX_BYTES) }}.
      </p>

      <input
        type="file"
        accept="image/png,image/jpeg,image/webp"
        class="mt-2 w-full text-xs"
        @change="pick"
      />

      <label class="mt-2 block">
        <span class="text-2xs text-[var(--c-text-muted)]">
          Where it came from (kept for provenance)
        </span>
        <input
          v-model="note"
          type="text"
          maxlength="200"
          placeholder="e.g. manufacturer press kit"
          class="mt-0.5 w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
        />
      </label>

      <p
        v-if="problem"
        class="mt-2 text-2xs text-[var(--c-danger-text)]"
      >
        {{ problem }}
      </p>

      <div class="mt-3 flex items-center gap-2">
        <Button variant="primary" size="sm" :disabled="!chosen" :pending="busy" @click="upload">
          {{ busy ? 'Saving…' : 'Save' }}
        </Button>
        <!-- danger: removing the live image is the destructive action the variant exists for. -->
        <Button v-if="hasImage" variant="danger" size="sm" :disabled="busy" @click="remove">Remove</Button>
        <Button variant="ghost" size="sm" class="ml-auto" @click="open = false">Cancel</Button>
      </div>

      <p
        v-if="hasImage && updatedAt"
        class="mt-2 border-t pt-2 text-2xs text-[var(--c-text-muted)]"
      >
        Current image saved {{ formatDateTime(updatedAt) }}<template v-if="sourceNote">
          · {{ sourceNote }}</template>.
      </p>
    </div>
  </div>
</template>
