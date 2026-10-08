<script setup lang="ts" generic="T extends string | boolean">
import { ref } from 'vue'

/**
 * A two-to-five-way choice drawn as joined buttons: a radio group, or a row of tabs.
 *
 * One component because the product had three designs of it and none kept the promise its roles
 * made. Most declared role="radio" or role="tab" (one used aria-pressed), which tells a screen
 * reader to expect the arrow keys, and answered none of them: every option was its own Tab stop,
 * and where the frame was overflow-hidden it clipped the focus ring drawn outside the button.
 *
 * The keyboard contract (WAI-ARIA radio group and tabs patterns):
 * - the group is one Tab stop, on the chosen option;
 * - Arrow keys move between enabled options and wrap; Home and End go to the ends;
 * - in a radio group the option the arrows reach is chosen at once;
 * - in a tab row the arrows only move focus, and Enter or Space opens the tab - several of the
 *   panels behind these tabs run a query, which should not fire for every tab passed on the way.
 *
 * A disabled option says why in its title. It stays out of arrow navigation and ignores clicks;
 * aria-disabled rather than the disabled attribute, so the reason still shows on hover.
 *
 * Choosing the option already chosen does nothing. A few hand-written groups used to re-run
 * their handler on a repeat click (resetting a page number, remounting a list); none relied on it.
 */
export interface SegmentOption<V> {
  value: V
  label: string
  /** Why it cannot be chosen; null or absent when it can. */
  disabled?: string | null
  /** Hover text for an enabled option. */
  title?: string
}

const props = withDefaults(
  defineProps<{
    modelValue: T
    options: SegmentOption<T>[]
    /** Names the group for assistive technology; every group has one. */
    label: string
    /** A row of tabs rather than a radio group. */
    tabs?: boolean
    /**
     * Tabs only: the id the panel uses. Each tab then gets `${panelId}-tab-${index}`, and the panel
     * is labelled by the chosen one - see tabId().
     */
    panelId?: string
  }>(),
  { tabs: false, panelId: undefined },
)

const emit = defineEmits<{ 'update:modelValue': [value: T] }>()

const group = ref<HTMLElement | null>(null)

/**
 * The button for an option, read from the DOM when needed. Not a ref array: Vue does not promise
 * its order, and refs kept by index go stale when a leading option is removed.
 */
const button = (index: number): HTMLButtonElement | undefined =>
  group.value?.querySelectorAll<HTMLButtonElement>(':scope > button')[index]

const isOn = (option: SegmentOption<T>): boolean => option.value === props.modelValue
const isDisabled = (option: SegmentOption<T>): boolean => !!option.disabled

/** The one option in the Tab order: the chosen one, or the first enabled if none is chosen. */
function tabStop(index: number): boolean {
  const chosen = props.options.findIndex((o) => isOn(o) && !isDisabled(o))
  return index === (chosen >= 0 ? chosen : props.options.findIndex((o) => !isDisabled(o)))
}

function choose(option: SegmentOption<T>) {
  if (isDisabled(option) || isOn(option)) return
  emit('update:modelValue', option.value)
}

function onKeydown(event: KeyboardEvent, index: number) {
  // Alt+Arrow is the browser's Back and Forward; Ctrl and Meta combinations are not ours either.
  if (event.altKey || event.ctrlKey || event.metaKey) return
  const enabled = props.options.map((o, i) => (isDisabled(o) ? -1 : i)).filter((i) => i >= 0)
  if (enabled.length === 0) return
  // From a disabled option (a mouse click can focus one), the nearest enabled one that way.
  const after = enabled.find((i) => i > index) ?? enabled[0]
  const before = [...enabled].reverse().find((i) => i < index) ?? enabled[enabled.length - 1]
  let next: number | undefined
  switch (event.key) {
    case 'ArrowRight':
    case 'ArrowDown':
      next = after
      break
    case 'ArrowLeft':
    case 'ArrowUp':
      next = before
      break
    case 'Home':
      next = enabled[0]
      break
    case 'End':
      next = enabled[enabled.length - 1]
      break
    default:
      return
  }
  event.preventDefault()
  if (next === undefined) return
  button(next)?.focus()
  const option = props.options[next]
  if (!props.tabs && option) choose(option)
}

const key = (value: T): string => String(value)
</script>

<script lang="ts">
/** The id of a tab, for the panel's aria-labelledby. */
export function tabId(panelId: string, index: number): string {
  return `${panelId}-tab-${index}`
}
</script>

<template>
  <!--
    The frame is rounded and clips, so the buttons need no radius of their own (rounded-none also
    stops the base focus rule rounding single corners into notches), and the focus ring is drawn
    inside the button (-outline-offset-2), where the clip cannot hide it.
  -->
  <div
    ref="group"
    class="inline-flex max-w-full flex-wrap overflow-hidden rounded-[var(--radius-md)] border"
    :role="tabs ? 'tablist' : 'radiogroup'"
    :aria-label="label"
  >
    <button
      v-for="(option, index) in options"
      :id="tabs && panelId ? tabId(panelId, index) : undefined"
      :key="key(option.value)"
      type="button"
      :role="tabs ? 'tab' : 'radio'"
      :aria-checked="tabs ? undefined : isOn(option)"
      :aria-selected="tabs ? isOn(option) : undefined"
      :aria-controls="tabs && panelId && isOn(option) ? panelId : undefined"
      :aria-disabled="isDisabled(option) || undefined"
      :tabindex="tabStop(index) ? 0 : -1"
      :title="option.disabled || option.title || undefined"
      class="rounded-none px-2.5 py-1 text-xs font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-[var(--c-accent)] aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
      :class="
        isOn(option)
          ? 'bg-[var(--c-accent-subtle)] text-[var(--c-accent)]'
          : 'bg-[var(--c-surface)] text-[var(--c-text-secondary)] not-aria-disabled:hover:bg-[var(--c-surface-hover)]'
      "
      @click="choose(option)"
      @keydown="onKeydown($event, index)"
    >
      {{ option.label }}
    </button>
  </div>
</template>
