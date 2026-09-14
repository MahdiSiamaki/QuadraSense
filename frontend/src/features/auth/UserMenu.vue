<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import type { CurrentUser } from '@/api/auth'

/**
 * The account menu in the top bar.
 *
 * Shows the roles rather than only the name. On a system where what you can see depends entirely
 * on your role, "signed in as Ada Analyst · Analyst" answers a question people otherwise ask
 * support: why can I not see the Imports tab.
 *
 * Built from a button and a positioned panel rather than a library popover. It needs Escape,
 * outside-click and `aria-expanded`, which is all of it, and a dependency for that would be a
 * dependency for thirty lines.
 */
const props = defineProps<{
  user: CurrentUser
  open: boolean
  signingOut?: boolean
}>()

const emit = defineEmits<{ toggle: []; close: []; signOut: [] }>()

const root = ref<HTMLElement | null>(null)

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
  if (event.key === 'Escape' && props.open) emit('close')
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
  <div ref="root" class="relative">
    <button
      type="button"
      class="flex items-center gap-2 rounded-[var(--radius-md)] py-1 pr-1.5 pl-1 hover:bg-[var(--c-surface-hover)]"
      :aria-expanded="open"
      aria-haspopup="menu"
      @click="emit('toggle')"
    >
      <span
        class="grid size-7 shrink-0 place-items-center rounded-full bg-[var(--c-surface-sunken)] text-[var(--text-2xs)] font-semibold text-[var(--c-text-secondary)]"
        aria-hidden="true"
      >
        {{ initials }}
      </span>
      <span class="hidden text-[var(--text-xs)] font-medium md:inline">
        {{ user.displayName }}
      </span>
    </button>

    <div
      v-if="open"
      role="menu"
      class="absolute right-0 z-20 mt-1.5 w-60 overflow-hidden rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-md)]"
    >
      <div class="border-b px-3 py-2.5">
        <p class="truncate text-[var(--text-sm)] font-medium">{{ user.displayName }}</p>
        <p class="truncate text-[var(--text-xs)] text-[var(--c-text-muted)]">
          {{ user.username }}
        </p>
        <p class="mt-1 text-[var(--text-2xs)] text-[var(--c-text-secondary)]">
          {{ user.roles.length ? user.roles.join(', ') : 'no role assigned' }}
          <span class="text-[var(--c-text-muted)]">
            · {{ user.permissions.length }} permission(s)
          </span>
        </p>
      </div>

      <RouterLink
        to="/profile"
        role="menuitem"
        class="block px-3 py-2 text-[var(--text-sm)] hover:bg-[var(--c-surface-hover)]"
        @click="emit('close')"
      >
        Your profile
      </RouterLink>

      <button
        type="button"
        role="menuitem"
        class="w-full px-3 py-2 text-left text-[var(--text-sm)] text-[var(--c-danger)] hover:bg-[var(--c-surface-hover)] disabled:opacity-50"
        :disabled="signingOut"
        @click="emit('signOut')"
      >
        {{ signingOut ? 'Signing out…' : 'Sign out' }}
      </button>
    </div>
  </div>
</template>
