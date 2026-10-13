<script setup lang="ts">
/**
 * Choosing a dashboard: the person's own, those shared with them, and the templates. For someone
 * with dashboard.manage, everybody else's on request - an administrator needs to find a dashboard to
 * manage it, not to have the whole system in their switcher.
 *
 * A disclosure (the UserMenu pattern), with the search field first: on open, focus goes there, and
 * typing filters by name, description and owner without asking the server.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId } from 'vue'
import type { DashboardList, DashboardSummary } from '@/api/dashboards'
import { TEMPLATES } from '../templates'

const props = defineProps<{
  list: DashboardList | undefined
  /** Others, when loaded on request (dashboard.manage). */
  others: DashboardSummary[] | null
  currentId: number | null
  currentTemplate: string | null
  label: string
  canManage: boolean
}>()

const emit = defineEmits<{
  openDashboard: [id: number]
  openTemplate: [key: string]
  loadOthers: []
}>()

const open = ref(false)
const query = ref('')
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const search = ref<HTMLInputElement | null>(null)
const panelId = useId()

function matches(name: string, description: string, owner = '') {
  const q = query.value.trim().toLowerCase()
  return !q || name.toLowerCase().includes(q) || description.toLowerCase().includes(q) || owner.toLowerCase().includes(q)
}

const mine = computed(() => (props.list?.mine ?? []).filter((d) => matches(d.name, d.description)))
const shared = computed(() => (props.list?.sharedWithMe ?? []).filter((d) => matches(d.name, d.description, d.owner.displayName)))
// Named apart from the `others` prop: in the template the prop decides whether the list is loaded at
// all (null), this decides what of it matches the search.
const othersShown = computed(() => (props.others ?? []).filter((d) => matches(d.name, d.description, d.owner.displayName)))
const templates = computed(() => TEMPLATES.filter((t) => matches(t.name, t.description)))
const nothing = computed(() => !mine.value.length && !shared.value.length && !templates.value.length && !othersShown.value.length)

async function toggle() {
  open.value = !open.value
  if (open.value) {
    query.value = ''
    await nextTick()
    search.value?.focus()
  }
}

function pickDashboard(id: number) {
  open.value = false
  emit('openDashboard', id)
}

function pickTemplate(key: string) {
  open.value = false
  emit('openTemplate', key)
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
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onDocumentPointerDown)
  document.removeEventListener('keydown', onKeydown)
})

function level(d: DashboardSummary) {
  return d.access.level === 'editor' ? 'can edit' : d.access.level === 'viewer' ? 'can view' : d.access.level === 'manager' ? 'as administrator' : ''
}
</script>

<template>
  <div ref="root" class="relative" @focusout="onFocusOut">
    <!--
      The page's heading holds the switcher's button, not the other way round: a button's children are
      presentational, so an h1 inside it left the page without a level-1 heading.
    -->
    <!--
      The hover box reaches 6px left of the text so the text lines up with the description below. That
      offset is the heading's margin, not the button's: on the button, the heading measured 6px narrower
      than the name, the button's max-width took the 6px from the text, and every name lost its end to
      an ellipsis ("Device populati…"). A name too long for the line wraps; it is never cut.
    -->
    <h1 class="-ml-1.5 min-w-0 text-xl font-semibold tracking-tight">
      <button
        ref="trigger"
        type="button"
        class="flex max-w-full items-center gap-2 rounded-[var(--radius-md)] px-1.5 py-0.5 text-left hover:bg-[var(--c-surface-hover)]"
        :aria-expanded="open"
        :aria-controls="panelId"
        @click="toggle"
      >
        <span class="min-w-0 break-words">{{ label }}</span>
        <span aria-hidden="true" class="text-sm font-normal text-[var(--c-text-muted)]">▾</span>
        <span class="sr-only">Switch dashboard</span>
      </button>
    </h1>

    <div
      v-if="open"
      :id="panelId"
      class="absolute left-0 top-full z-40 mt-1.5 flex max-h-[min(70vh,36rem)] w-[min(26rem,calc(100vw-2.5rem))] flex-col overflow-hidden rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-md)] origin-top-left transition-[opacity,scale] duration-(--duration-popover) ease-out starting:opacity-0 motion-safe:starting:scale-[0.97]"
    >
      <div class="border-b p-2">
        <label :for="`${panelId}-search`" class="sr-only">Find a dashboard</label>
        <input
          :id="`${panelId}-search`"
          ref="search"
          v-model="query"
          type="search"
          autocomplete="off"
          placeholder="Find a dashboard"
          class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm"
          @keydown.enter.prevent
        />
      </div>

      <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain py-1">
        <section v-if="mine.length" aria-label="My dashboards">
          <h2 class="px-3 pt-2 pb-1 text-2xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">My dashboards</h2>
          <button
            v-for="d in mine"
            :key="d.id"
            type="button"
            class="flex w-full items-center gap-2 rounded-none px-3 py-1.5 text-left text-sm hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2"
            :aria-current="d.id === currentId ? 'page' : undefined"
            :class="d.id === currentId ? 'bg-[var(--c-accent-subtle)]' : ''"
            @click="pickDashboard(d.id)"
          >
            <span class="min-w-0 flex-1 truncate">{{ d.name }}</span>
            <span v-if="d.isFavorite" class="text-xs text-[var(--c-warning-text)]" title="Favourite">★<span class="sr-only"> favourite</span></span>
            <span v-if="d.isDefault" class="text-2xs text-[var(--c-text-muted)]">default</span>
            <span v-if="d.isShared" class="text-2xs text-[var(--c-text-muted)]">shared</span>
          </button>
        </section>

        <section v-if="shared.length" aria-label="Shared with me">
          <h2 class="px-3 pt-3 pb-1 text-2xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Shared with me</h2>
          <button
            v-for="d in shared"
            :key="d.id"
            type="button"
            class="flex w-full items-center gap-2 rounded-none px-3 py-1.5 text-left text-sm hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2"
            :aria-current="d.id === currentId ? 'page' : undefined"
            :class="d.id === currentId ? 'bg-[var(--c-accent-subtle)]' : ''"
            @click="pickDashboard(d.id)"
          >
            <span class="min-w-0 flex-1">
              <span class="block truncate">{{ d.name }}</span>
              <span class="block truncate text-2xs text-[var(--c-text-muted)]">by {{ d.owner.displayName }} · {{ level(d) }}</span>
            </span>
            <span v-if="d.isFavorite" class="text-xs text-[var(--c-warning-text)]" title="Favourite">★<span class="sr-only"> favourite</span></span>
            <span v-if="d.isDefault" class="text-2xs text-[var(--c-text-muted)]">default</span>
          </button>
        </section>

        <section v-if="templates.length" aria-label="Templates">
          <h2 class="px-3 pt-3 pb-1 text-2xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Templates</h2>
          <button
            v-for="t in templates"
            :key="t.key"
            type="button"
            class="flex w-full flex-col items-start rounded-none px-3 py-1.5 text-left text-sm hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2"
            :aria-current="t.key === currentTemplate ? 'page' : undefined"
            :class="t.key === currentTemplate ? 'bg-[var(--c-accent-subtle)]' : ''"
            @click="pickTemplate(t.key)"
          >
            <span class="truncate">{{ t.name }}</span>
            <span class="text-2xs text-[var(--c-text-muted)]">{{ t.description }}</span>
          </button>
        </section>

        <section v-if="canManage" aria-label="Everybody else's dashboards">
          <h2 class="px-3 pt-3 pb-1 text-2xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Everybody else's</h2>
          <button
            v-if="props.others === null"
            type="button"
            class="w-full rounded-none px-3 py-1.5 text-left text-xs text-[var(--c-accent)] hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2"
            @click="emit('loadOthers')"
          >
            Show them (administrator)
          </button>
          <button
            v-for="d in othersShown"
            :key="d.id"
            type="button"
            class="flex w-full items-center gap-2 rounded-none px-3 py-1.5 text-left text-sm hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2"
            @click="pickDashboard(d.id)"
          >
            <span class="min-w-0 flex-1">
              <span class="block truncate">{{ d.name }}</span>
              <span class="block truncate text-2xs text-[var(--c-text-muted)]">by {{ d.owner.displayName }}</span>
            </span>
          </button>
        </section>

        <p v-if="nothing" class="px-3 py-4 text-center text-xs text-[var(--c-text-muted)]">
          Nothing matches "{{ query }}".
          <button type="button" class="font-medium text-[var(--c-accent)] underline-offset-2 hover:underline" @click="query = ''">Clear search</button>
        </p>
      </div>
    </div>
  </div>
</template>
