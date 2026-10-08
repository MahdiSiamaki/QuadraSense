<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useId } from 'vue'
import { RouterLink } from 'vue-router'
import type { CurrentUser } from '@/api/auth'

/**
 * The account menu in the top bar.
 *
 * Shows the roles rather than only the name. On a system where what you can see depends entirely
 * on your role, "signed in as Ada Analyst · Analyst" answers a question people otherwise ask
 * support: why can I not see the Imports tab.
 *
 * Built from a button and a positioned panel rather than a library popover: a disclosure, not a
 * role="menu". Two links and a button need no arrow keys, and a menu role promises them - it did,
 * and kept none of the promise. What a disclosure owes is all here: `aria-expanded` and
 * `aria-controls`, Escape and outside-click to close, focus back on the button when Escape closes
 * it (the panel's items are removed with it, and focus fell to the page), and closing when focus
 * moves on past the last item.
 */
const props = defineProps<{
  user: CurrentUser
  open: boolean
  signingOut?: boolean
}>()

const emit = defineEmits<{ toggle: []; close: []; signOut: [] }>()

const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const panelId = useId()

const initials = computed(() =>
  props.user.displayName
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('') || props.user.username.slice(0, 2).toUpperCase(),
)

function onDocumentPointerDown(event: PointerEvent) {
  if (!props.open) return
  if (root.value && !root.value.contains(event.target as Node)) emit('close')
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || !props.open) return
  // Focus goes back to the button only if it was in the menu; Escape pressed elsewhere still
  // closes the menu but leaves focus where it is.
  if (root.value?.contains(document.activeElement)) trigger.value?.focus()
  emit('close')
}

/** Tabbing past the last item leaves the menu; an open panel behind the focus is a trap for the eye. */
function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (props.open && next && root.value && !root.value.contains(next)) emit('close')
}

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown)
  document.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onDocumentPointerDown)
  document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <div ref="root" class="relative" @focusout="onFocusOut">
    <button
      ref="trigger"
      type="button"
      class="flex items-center gap-2 rounded-[var(--radius-md)] py-1 pr-1.5 pl-1 hover:bg-[var(--c-surface-hover)]"
      :aria-expanded="open"
      :aria-controls="panelId"
      @click="emit('toggle')"
    >
      <span
        class="grid size-7 shrink-0 place-items-center rounded-full bg-[var(--c-surface-sunken)] text-2xs font-semibold text-[var(--c-text-secondary)]"
        aria-hidden="true"
      >
        {{ initials }}
      </span>
      <span class="sr-only text-xs font-medium md:not-sr-only">
        {{ user.displayName }}
      </span>
    </button>

    <div
      v-if="open"
      :id="panelId"
      class="absolute right-0 z-20 mt-1.5 w-60 overflow-hidden rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-md)]"
    >
      <div class="border-b px-3 py-2.5">
        <p class="truncate text-sm font-medium">{{ user.displayName }}</p>
        <p class="truncate text-xs text-[var(--c-text-muted)]">
          {{ user.username }}
        </p>
        <p class="mt-1 text-2xs text-[var(--c-text-secondary)]">
          {{ user.roles.length ? user.roles.join(', ') : 'no role assigned' }}
          <span class="text-[var(--c-text-muted)]">
            · {{ user.permissions.length }} {{ user.permissions.length === 1 ? 'permission' : 'permissions' }}
          </span>
        </p>
      </div>

      <RouterLink
        to="/profile"
        class="block rounded-none px-3 py-2 text-sm hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2"
        @click="emit('close')"
      >
        Your profile
      </RouterLink>

      <button
        type="button"
        class="w-full rounded-none px-3 py-2 text-left text-sm text-[var(--c-danger-text)] enabled:hover:bg-[var(--c-surface-hover)] focus-visible:-outline-offset-2 disabled:opacity-50"
        :disabled="signingOut"
        @click="emit('signOut')"
      >
        {{ signingOut ? 'Signing out…' : 'Sign out' }}
      </button>
    </div>
  </div>
</template>
