<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'

/**
 * A dialog, built on the native `<dialog>` element.
 *
 * `showModal()` gives focus trapping, inertness of the page behind, Escape to close and the
 * top layer for free - all of which a div-based modal has to reimplement, and almost always
 * reimplements incompletely. The parts that remain ours are closing on a backdrop click and
 * keeping the open state in sync with the prop.
 *
 * Deliberately not dismissible while `busy`: a dialog that vanishes mid-request leaves the user
 * unable to tell whether what they asked for happened.
 */
const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    description?: string
    /** sm for confirmations, md for forms, lg for the permission editor. */
    size?: 'sm' | 'md' | 'lg'
    busy?: boolean
  }>(),
  { size: 'md', busy: false },
)

const emit = defineEmits<{ close: [] }>()

const dialog = ref<HTMLDialogElement | null>(null)

watch(
  () => props.open,
  (open) => {
    const element = dialog.value
    if (!element) return
    if (open && !element.open) element.showModal()
    if (!open && element.open) element.close()
  },
  { flush: 'post' },
)

function requestClose(event?: Event) {
  event?.preventDefault()
  if (!props.busy) emit('close')
}

/** A click that lands on the dialog element itself is a click on the backdrop. */
function onBackdropClick(event: MouseEvent) {
  if (event.target === dialog.value) requestClose()
}

onBeforeUnmount(() => dialog.value?.close())

const widths: Record<string, string> = {
  sm: 'max-w-sm',
  md: 'max-w-lg',
  lg: 'max-w-3xl',
}
</script>

<template>
  <!--
    m-auto is what centres this, and it is load-bearing rather than cosmetic.

    A modal <dialog> is laid out in the top layer against `inset: 0`, so the user agent centres it
    with `margin: auto`. Tailwind's Preflight resets `margin: 0` on every element, `dialog`
    included, which silently removes that - measured in this app's own stylesheet: computed margin
    `0px`, and the dialog rendered at left 0, top 0, pinned to the corner of the screen. Restoring
    the margin puts it back in the middle.
  -->
  <dialog
    ref="dialog"
    class="m-auto w-[calc(100vw-2rem)] rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-0 text-[var(--c-text)] shadow-[var(--shadow-md)] backdrop:bg-black/40 backdrop:backdrop-blur-[1px]"
    :class="widths[size]"
    @cancel="requestClose"
    @click="onBackdropClick"
  >
    <form method="dialog" class="flex max-h-[85vh] flex-col" @submit.prevent>
      <header class="flex items-start justify-between gap-4 border-b px-4 py-3">
        <div class="min-w-0">
          <h2 class="text-sm font-semibold">{{ title }}</h2>
          <p v-if="description" class="mt-0.5 text-xs text-[var(--c-text-muted)]">
            {{ description }}
          </p>
        </div>
        <button
          type="button"
          class="-mt-0.5 -mr-1 rounded-[var(--radius-md)] px-1.5 py-0.5 text-lg leading-none text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)] disabled:opacity-40"
          :disabled="busy"
          aria-label="Close"
          @click="requestClose()"
        >
          &times;
        </button>
      </header>

      <div class="min-h-0 flex-1 overflow-y-auto px-4 py-4">
        <slot />
      </div>

      <footer
        v-if="$slots.actions"
        class="flex items-center justify-end gap-2 border-t bg-[var(--c-surface-sunken)] px-4 py-3"
      >
        <slot name="actions" />
      </footer>
    </form>
  </dialog>
</template>
