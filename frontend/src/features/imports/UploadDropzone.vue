<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { RouterLink } from 'vue-router'
import Button from '@/design-system/Button.vue'
import ProgressBar from '@/design-system/ProgressBar.vue'
import { formatBytes, formatRate, formatTimeLeft } from '@/lib/format'
import { timeLeftMs } from '@/lib/transfer-rate'
import {
  cancel,
  cancelAll,
  clearFinished,
  enqueue,
  onUploadFinished,
  useUploadQueue,
  type UploadItem,
  type UploadStatus,
} from './uploadQueue'

/**
 * Upload area for daily and TAC files.
 *
 * Three things make this more than a styled file input.
 *
 * First, it uploads several files as a queue rather than in parallel. A month of daily files is
 * thirty gigabytes; starting thirty simultaneous uploads divides one connection thirty ways,
 * makes every progress bar crawl, and finishes the whole set no sooner. One at a time gives a
 * bar that moves and an obvious place to stop.
 *
 * Second, a duplicate is shown as an outcome, not an error. Sending the same file twice is a
 * normal thing for an operator to do — a retry after a dropped connection, or simply not
 * remembering — and the platform already knows the answer from the content hash.
 *
 * Third, the queue is not this component's (uploadQueue.ts): it keeps going when the page is
 * left, says how fast it is going and how long is left, and every file in it can be stopped.
 */
const props = defineProps<{ sourceCode: string; accept?: string; hint?: string }>()
const emit = defineEmits<{ uploaded: [] }>()

const { items, totals, active } = useUploadQueue(props.sourceCode)
const dragging = ref(false)
const input = ref<HTMLInputElement | null>(null)

const stopListening = onUploadFinished((source) => {
  if (source === props.sourceCode) emit('uploaded')
})
onBeforeUnmount(stopListening)

function onDrop(event: DragEvent) {
  dragging.value = false
  const files = event.dataTransfer?.files
  if (files?.length) enqueue(props.sourceCode, Array.from(files))
}

function onPick(event: Event) {
  const target = event.target as HTMLInputElement
  if (target.files?.length) enqueue(props.sourceCode, Array.from(target.files))
  // Reset so picking the same file again still fires a change event.
  target.value = ''
}

/** "4.2 MB/s · about 3 min left", or what is known so far. */
function pace(rate: number | null, bytesLeft: number): string {
  if (rate === null) return 'Measuring speed…'
  if (rate === 0) return 'Stalled - waiting for the connection'
  const left = formatTimeLeft(timeLeftMs(bytesLeft, rate))
  return left ? `${formatRate(rate)} · ${left}` : formatRate(rate)
}

const tone: Record<UploadStatus, string> = {
  waiting: 'var(--c-text-muted)',
  uploading: 'var(--c-accent)',
  queued: 'var(--c-success-text)',
  duplicate: 'var(--c-warning-text)',
  failed: 'var(--c-danger-text)',
  cancelled: 'var(--c-text-muted)',
}

const stoppable = (item: UploadItem) => item.status === 'waiting' || item.status === 'uploading'
</script>

<template>
  <div class="space-y-3">
    <!--
      Its text takes no pointer events: crossing a line of it fired dragleave on the panel, the
      highlight went off and came back on, and the drop zone flickered under the file.

      The whole panel is the drop target, and it is also a button. Drag and drop alone would be
      unusable by keyboard; a file input alone would ignore the gesture most people reach for
      when they already have the file in a folder.
    -->
    <button
      type="button"
      class="flex w-full flex-col items-center justify-center gap-1 rounded-[var(--radius-lg)] border-2 border-dashed px-4 py-8 transition-colors [&_*]:pointer-events-none"
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

    <!-- Out of the Tab order: the drop zone above opens the same picker, and a stop on an input
         whose focus ring the sr-only clip hides was a stop nobody could see. -->
    <input
      ref="input"
      type="file"
      multiple
      tabindex="-1"
      class="sr-only"
      :accept="accept ?? '.csv,text/csv'"
      @change="onPick"
    />

    <!-- The whole line: what is left of everything queued, and one way to stop all of it. -->
    <div
      v-if="totals"
      class="flex flex-wrap items-center justify-between gap-x-3 gap-y-1 text-2xs text-[var(--c-text-muted)]"
      aria-live="polite"
    >
      <span class="tabular">
        {{ totals.files }} {{ totals.files === 1 ? 'file' : 'files' }} ·
        {{ formatBytes(totals.bytesLeft) }} left · {{ pace(totals.rate, totals.bytesLeft) }}
      </span>
      <Button v-if="totals.files > 1" size="sm" variant="ghost" @click="cancelAll(sourceCode)">
        Cancel all
      </Button>
    </div>

    <ul v-if="items.length" class="space-y-2">
      <li v-for="item in items" :key="item.id" class="rounded-[var(--radius-md)] border px-3 py-2">
        <div class="flex items-center justify-between gap-3">
          <span class="min-w-0 truncate text-xs font-medium" :title="item.file.name">
            {{ item.file.name }}
          </span>
          <span class="flex shrink-0 items-center gap-2">
            <span class="tabular text-2xs text-[var(--c-text-muted)]">
              {{ formatBytes(item.file.size) }}
            </span>
            <Button
              v-if="stoppable(item)"
              size="sm"
              variant="ghost"
              :aria-label="`Cancel the upload of ${item.file.name}`"
              @click="cancel(item.id)"
            >
              Cancel
            </Button>
          </span>
        </div>

        <template v-if="item.status === 'uploading'">
          <ProgressBar
            class="mt-1.5"
            :percent="item.file.size ? (item.sent / item.file.size) * 100 : null"
            :detail="`${formatBytes(item.sent)} of ${formatBytes(item.file.size)}`"
          />
          <p class="tabular mt-1 text-2xs text-[var(--c-text-muted)]">
            {{ pace(item.rate, item.file.size - item.sent) }}
          </p>
        </template>

        <p v-else-if="item.message" class="mt-1 text-2xs" :style="{ color: tone[item.status] }">
          {{ item.message }}
          <RouterLink
            v-if="item.status === 'queued' && item.jobId"
            :to="`/imports/${item.jobId}`"
            class="ml-1 underline hover:text-[var(--c-text)]"
          >
            Follow it
          </RouterLink>
        </p>

        <p v-else class="mt-1 text-2xs text-[var(--c-text-muted)]">Waiting…</p>
      </li>
    </ul>

    <div v-if="items.length && !active" class="flex justify-end">
      <button
        type="button"
        class="text-xs font-medium text-[var(--c-text-secondary)] hover:text-[var(--c-text)] hover:underline"
        @click="clearFinished(sourceCode)"
      >
        Clear list
      </button>
    </div>
  </div>
</template>
