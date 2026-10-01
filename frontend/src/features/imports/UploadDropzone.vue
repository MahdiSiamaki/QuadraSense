<script setup lang="ts">
import { computed, ref } from 'vue'
import ProgressBar from '@/design-system/ProgressBar.vue'
import { formatBytes } from '@/lib/format'
import { uploadImportFile, type UploadResult } from '@/api/imports'
import { ApiError } from '@/api/client'

/**
 * Upload area for daily and TAC files.
 *
 * Two things make this more than a styled file input.
 *
 * First, it uploads several files as a queue rather than in parallel. A month of daily files is
 * thirty gigabytes; starting thirty simultaneous uploads divides one connection thirty ways,
 * makes every progress bar crawl, and finishes the whole set no sooner. One at a time gives a
 * bar that moves and an obvious place to stop.
 *
 * Second, a duplicate is shown as an outcome, not an error. Sending the same file twice is a
 * normal thing for an operator to do — a retry after a dropped connection, or simply not
 * remembering — and the platform already knows the answer from the content hash.
 */
const props = defineProps<{ sourceCode: string; accept?: string; hint?: string }>()
const emit = defineEmits<{ uploaded: [] }>()

interface QueueItem {
  file: File
  status: 'waiting' | 'uploading' | 'queued' | 'duplicate' | 'failed'
  fraction: number
  message?: string
  jobId?: number
}

const queue = ref<QueueItem[]>([])
const dragging = ref(false)
const input = ref<HTMLInputElement | null>(null)
const busy = ref(false)

const active = computed(() => queue.value.filter((i) => i.status === 'uploading').length > 0)

function onDrop(event: DragEvent) {
  dragging.value = false
  const files = event.dataTransfer?.files
  if (files?.length) enqueue(Array.from(files))
}

function onPick(event: Event) {
  const target = event.target as HTMLInputElement
  if (target.files?.length) enqueue(Array.from(target.files))
  // Reset so picking the same file again still fires a change event.
  target.value = ''
}

function enqueue(files: File[]) {
  queue.value = [
    ...queue.value,
    ...files.map((file): QueueItem => ({ file, status: 'waiting', fraction: 0 })),
  ]
  void drain()
}

async function drain() {
  if (busy.value) return
  busy.value = true

  try {
    for (const item of queue.value) {
      if (item.status !== 'waiting') continue

      item.status = 'uploading'
      try {
        const result = await uploadImportFile(props.sourceCode, item.file, (fraction) => {
          item.fraction = fraction
        })
        apply(item, result)
      } catch (error) {
        item.status = 'failed'
        item.message =
          error instanceof ApiError
            ? (error.problem?.detail ?? error.message)
            : 'The upload did not complete.'
      }
    }
  } finally {
    busy.value = false
    emit('uploaded')
  }
}

function apply(item: QueueItem, result: UploadResult) {
  if (result.kind === 'queued') {
    item.status = 'queued'
    item.jobId = result.jobId
    item.fraction = 1
    item.message = `Queued as import ${result.jobId}`
    return
  }

  item.status = 'duplicate'
  item.fraction = 1
  item.message = result.existingJobId
    ? `Already imported — see import ${result.existingJobId}`
    : 'This exact file has already been uploaded'
}

function clearFinished() {
  queue.value = queue.value.filter((i) => i.status === 'uploading' || i.status === 'waiting')
}

const tone: Record<QueueItem['status'], string> = {
  waiting: 'var(--c-text-muted)',
  uploading: 'var(--c-accent)',
  queued: 'var(--c-success)',
  duplicate: 'var(--c-warning)',
  failed: 'var(--c-danger)',
}
</script>

<template>
  <div class="space-y-3">
    <!--
      The whole panel is the drop target, and it is also a button. Drag and drop alone would be
      unusable by keyboard; a file input alone would ignore the gesture most people reach for
      when they already have the file in a folder.
    -->
    <button
      type="button"
      class="flex w-full flex-col items-center justify-center gap-1 rounded-[var(--radius-lg)] border-2 border-dashed px-4 py-8 transition-colors"
      :class="
        dragging
          ? 'border-[var(--c-accent)] bg-[var(--c-accent-subtle)]'
          : 'border-[var(--c-border)] hover:border-[var(--c-border-strong)] hover:bg-[var(--c-surface-hover)]'
      "
      @click="input?.click()"
      @dragover.prevent="dragging = true"
      @dragleave.prevent="dragging = false"
      @drop.prevent="onDrop"
    >
      <span class="text-sm font-medium text-[var(--c-text)]">
        Drop {{ sourceCode }} files here, or choose files
      </span>
      <span class="text-xs text-[var(--c-text-muted)]">
        {{ hint ?? 'Several files can be queued at once; they upload one after another.' }}
      </span>
    </button>

    <input
      ref="input"
      type="file"
      multiple
      class="sr-only"
      :accept="accept ?? '.csv,text/csv'"
      @change="onPick"
    />

    <ul v-if="queue.length" class="space-y-2">
      <li
        v-for="(item, index) in queue"
        :key="`${item.file.name}-${index}`"
        class="rounded-[var(--radius-md)] border px-3 py-2"
      >
        <div class="flex items-baseline justify-between gap-3">
          <span class="truncate text-xs font-medium" :title="item.file.name">
            {{ item.file.name }}
          </span>
          <span class="tabular shrink-0 text-2xs text-[var(--c-text-muted)]">
            {{ formatBytes(item.file.size) }}
          </span>
        </div>

        <ProgressBar
          v-if="item.status === 'uploading'"
          class="mt-1.5"
          :percent="item.fraction * 100"
          :detail="formatBytes(item.file.size * item.fraction)"
        />

        <p
          v-else-if="item.message"
          class="mt-1 text-2xs"
          :style="{ color: tone[item.status] }"
        >
          {{ item.message }}
        </p>

        <p v-else class="mt-1 text-2xs text-[var(--c-text-muted)]">Waiting…</p>
      </li>
    </ul>

    <div v-if="queue.length && !active" class="flex justify-end">
      <button
        type="button"
        class="text-xs font-medium text-[var(--c-text-secondary)] hover:text-[var(--c-text)] hover:underline"
        @click="clearFinished"
      >
        Clear list
      </button>
    </div>
  </div>
</template>
