<script setup lang="ts">
/**
 * The card a widget sits in, and its edit controls.
 *
 * The same border-defined panel as design-system/Card.vue, so a dashboard built here reads as the
 * rest of the app. In edit mode the header carries a drag handle - the only place a drag starts, so
 * scrolling a table or hovering a chart never moves the card - and the card's menu is always shown,
 * never only on hover: a control that appears under the pointer does not exist for a keyboard or a
 * touch screen.
 *
 * A widget that is a card itself (a KPI) asks for `bare` chrome: then only a strip is drawn above it,
 * carrying the edit handle, the badges and the card's menu - drawn whenever any of them has something
 * to show, so a KPI's Download CSV and Save as image are there in view mode too.
 */
import { useId } from 'vue'
import ActionMenu, { type ActionMenuItem } from './ActionMenu.vue'

export interface FrameBadge {
  label: string
  /** What it means, read on hover and by a screen reader. */
  description: string
  tone?: 'neutral' | 'accent' | 'warning'
}

withDefaults(
  defineProps<{
    title: string
    subtitle?: string | null
    badges?: FrameBadge[]
    menu?: ActionMenuItem[]
    editing?: boolean
    chrome?: 'card' | 'bare'
    /** Refreshing with the previous data still shown. */
    updating?: boolean
  }>(),
  { subtitle: null, badges: () => [], menu: () => [], editing: false, chrome: 'card', updating: false },
)

const emit = defineEmits<{ action: [key: string] }>()

const titleId = useId()
</script>

<template>
  <section
    v-if="chrome === 'card'"
    :aria-labelledby="titleId"
    class="flex h-full min-w-0 flex-col rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-xs)]"
    :class="editing ? 'ring-1 ring-[var(--c-border-strong)]' : ''"
  >
    <header class="flex items-start gap-2 border-b px-4 py-2.5">
      <span
        v-if="editing"
        class="dashboard-drag-handle -ml-1.5 mt-0.5 grid size-6 shrink-0 cursor-grab place-items-center rounded-[var(--radius-sm)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] active:cursor-grabbing"
        title="Drag to move. Keyboard: use the card's menu."
        aria-hidden="true"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="6" r="1.6" /><circle cx="15" cy="6" r="1.6" /><circle cx="9" cy="12" r="1.6" /><circle cx="15" cy="12" r="1.6" /><circle cx="9" cy="18" r="1.6" /><circle cx="15" cy="18" r="1.6" /></svg>
      </span>

      <div class="min-w-0 flex-1">
        <h2 :id="titleId" class="truncate text-sm font-semibold text-[var(--c-text)]" :title="title">{{ title }}</h2>
        <p v-if="subtitle" class="mt-0.5 text-pretty break-words text-xs text-[var(--c-text-muted)]">{{ subtitle }}</p>
      </div>

      <ul v-if="badges.length || updating" class="flex shrink-0 flex-wrap items-center justify-end gap-1" aria-label="About this widget">
        <li v-if="updating" class="text-2xs text-[var(--c-text-muted)]" aria-live="polite">Updating…</li>
        <li
          v-for="badge in badges"
          :key="badge.label"
          class="rounded-full border px-1.5 py-px text-2xs font-medium"
          :class="{
            'border-[var(--c-accent)] text-[var(--c-accent)]': badge.tone === 'accent',
            'border-[var(--c-warning)] text-[var(--c-warning-text)]': badge.tone === 'warning',
            'text-[var(--c-text-secondary)]': !badge.tone || badge.tone === 'neutral',
          }"
          :title="badge.description"
        >
          {{ badge.label }}<span class="sr-only">: {{ badge.description }}</span>
        </li>
      </ul>

      <ActionMenu v-if="menu.length" :label="`Actions for ${title}`" :items="menu" class="-mr-1.5 shrink-0" @select="emit('action', $event)" />
    </header>

    <div class="min-h-0 flex-1 overflow-auto overscroll-contain px-4 py-3">
      <slot />
    </div>
  </section>

  <div v-else class="flex h-full min-w-0 flex-col" :class="editing ? 'gap-1' : ''">
    <div v-if="editing || badges.length || menu.length" class="flex items-center gap-1.5">
      <span
        v-if="editing"
        class="dashboard-drag-handle grid size-6 shrink-0 cursor-grab place-items-center rounded-[var(--radius-sm)] text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] active:cursor-grabbing"
        title="Drag to move. Keyboard: use the card's menu."
        aria-hidden="true"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="6" r="1.6" /><circle cx="15" cy="6" r="1.6" /><circle cx="9" cy="12" r="1.6" /><circle cx="15" cy="12" r="1.6" /><circle cx="9" cy="18" r="1.6" /><circle cx="15" cy="18" r="1.6" /></svg>
      </span>
      <span class="min-w-0 flex-1 truncate text-2xs text-[var(--c-text-muted)]">
        <template v-for="badge in badges" :key="badge.label"><span :title="badge.description">{{ badge.label }}</span> </template>
      </span>
      <ActionMenu v-if="menu.length" :label="`Actions for ${title}`" :items="menu" class="shrink-0" @select="emit('action', $event)" />
    </div>
    <div class="min-h-0 flex-1">
      <slot />
    </div>
  </div>
</template>
