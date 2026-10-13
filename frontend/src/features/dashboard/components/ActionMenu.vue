<script setup lang="ts">
/**
 * A list of actions behind a button - a widget card's menu, the dashboard's More menu.
 *
 * The disclosure pattern UserMenu settled on, not role="menu": a button with aria-expanded and
 * aria-controls, a panel of ordinary buttons, Escape and outside-click to close, focus back on the
 * trigger when Escape closes it, and closing when focus moves past the last item. Tab moves through
 * the items like any other buttons; nothing promises arrow keys it does not keep.
 *
 * It opens out of its own corner (150ms, opacity and a 3% scale, scale only with motion allowed) and
 * closes at once.
 *
 * The panel is positioned against the viewport (`position: fixed`), measured from the trigger when it
 * opens, so no scrolling or clipping ancestor can cut it off - a dashboard card scrolls its own body,
 * and a twelve-item edit menu in a three-row KPI card was clipped inside it. It opens below the trigger,
 * above it when only the space above fits, and scrolls itself when neither does. A scroll outside it or
 * a resize closes it rather than leave it floating away from its trigger.
 */
import { nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'

export interface ActionMenuItem {
  key: string
  label: string
  disabled?: boolean
  /** Why it is disabled, said beside it rather than only in a tooltip. */
  hint?: string
  danger?: boolean
  /** A rule above it, to group the list. */
  separated?: boolean
}

const props = withDefaults(
  defineProps<{
    /** The trigger's accessible name, e.g. "Actions for Top vendors". */
    label: string
    items: ActionMenuItem[]
    /** Visible text on the trigger; an ellipsis glyph when omitted. */
    text?: string
    align?: 'left' | 'right'
    size?: 'sm' | 'md'
  }>(),
  { align: 'right', size: 'sm', text: undefined },
)

const emit = defineEmits<{ select: [key: string] }>()

const open = ref(false)
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const panelId = useId()

const GAP = 4
const EDGE = 8
const place = ref<{ style: Record<string, string>; above: boolean }>({ style: {}, above: false })

/** Where the panel goes, from the trigger's place on screen and the panel's own height. */
function position() {
  const t = trigger.value?.getBoundingClientRect()
  if (!t) return
  const height = panel.value?.scrollHeight ?? 0
  const below = window.innerHeight - t.bottom - GAP - EDGE
  const aboveSpace = t.top - GAP - EDGE
  const above = height > below && aboveSpace > below
  const room = Math.max(120, above ? aboveSpace : below)
  const style: Record<string, string> = { maxHeight: `${room}px` }
  if (above) style.bottom = `${window.innerHeight - t.top + GAP}px`
  else style.top = `${t.bottom + GAP}px`
  if (props.align === 'right') style.right = `${Math.max(EDGE, window.innerWidth - t.right)}px`
  else style.left = `${Math.max(EDGE, t.left)}px`
  place.value = { style, above }
}

watch(open, async (isOpen) => {
  if (!isOpen) return
  position()
  await nextTick()
  position()
})

function onWindowScroll(event: Event) {
  if (open.value && !(panel.value && event.target instanceof Node && panel.value.contains(event.target))) open.value = false
}

function onResize() {
  open.value = false
}

function choose(item: ActionMenuItem) {
  if (item.disabled) return
  open.value = false
  trigger.value?.focus()
  emit('select', item.key)
}

function onDocumentPointerDown(event: PointerEvent) {
  if (open.value && root.value && !root.value.contains(event.target as Node)) open.value = false
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || !open.value) return
  if (root.value?.contains(document.activeElement)) trigger.value?.focus()
  open.value = false
}

function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (open.value && next && root.value && !root.value.contains(next)) open.value = false
}

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown)
  document.addEventListener('keydown', onKeydown)
  window.addEventListener('scroll', onWindowScroll, true)
  window.addEventListener('resize', onResize)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onDocumentPointerDown)
  document.removeEventListener('keydown', onKeydown)
  window.removeEventListener('scroll', onWindowScroll, true)
  window.removeEventListener('resize', onResize)
})

defineExpose({ close: () => (open.value = false) })
</script>

<template>
  <div ref="root" class="relative" @focusout="onFocusOut">
    <button
      ref="trigger"
      type="button"
      class="inline-flex items-center justify-center gap-1 rounded-[var(--radius-md)] border border-transparent font-medium text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)] aria-expanded:bg-[var(--c-surface-hover)]"
      :class="size === 'sm' ? (text ? 'h-7 px-2 text-xs' : 'size-7') : 'h-8 px-3 text-sm'"
      :aria-expanded="open"
      :aria-controls="panelId"
      :aria-label="text ? undefined : label"
      :title="text ? undefined : label"
      @click="open = !open"
    >
      <template v-if="text">{{ text }}<span aria-hidden="true" class="text-[var(--c-text-muted)]">▾</span></template>
      <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <circle cx="5" cy="12" r="1.8" /><circle cx="12" cy="12" r="1.8" /><circle cx="19" cy="12" r="1.8" />
      </svg>
    </button>

    <div
      v-if="open"
      :id="panelId"
      ref="panel"
      class="fixed z-30 min-w-48 overflow-y-auto overscroll-contain rounded-[var(--radius-lg)] border bg-[var(--c-surface)] py-1 shadow-[var(--shadow-md)] transition-[opacity,scale] duration-(--duration-popover) ease-out starting:opacity-0 motion-safe:starting:scale-[0.97]"
      :class="place.above ? (align === 'right' ? 'origin-bottom-right' : 'origin-bottom-left') : align === 'right' ? 'origin-top-right' : 'origin-top-left'"
      :style="place.style"
    >
      <template v-for="item in items" :key="item.key">
        <div v-if="item.separated" class="my-1 border-t" role="separator" />
        <button
          type="button"
          class="flex w-full flex-col items-start gap-0.5 rounded-none px-3 py-1.5 text-left text-sm focus-visible:-outline-offset-2 enabled:hover:bg-[var(--c-surface-hover)] disabled:cursor-not-allowed"
          :class="[
            item.danger ? 'text-[var(--c-danger-text)]' : '',
            item.disabled ? 'text-[var(--c-text-muted)]' : '',
          ]"
          :disabled="item.disabled"
          @click="choose(item)"
        >
          <span>{{ item.label }}</span>
          <span v-if="item.hint" class="text-2xs text-[var(--c-text-muted)]">{{ item.hint }}</span>
        </button>
      </template>
    </div>
  </div>
</template>
