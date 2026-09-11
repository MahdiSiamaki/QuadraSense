<script setup lang="ts">
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { useTheme } from '@/lib/theme'

const { isDark, toggle } = useTheme()
const route = useRoute()

const nav = [
  { to: '/', label: 'Dashboard' },
  { to: '/lookup', label: 'Lookup' },
]
</script>

<template>
  <div class="flex min-h-screen flex-col">
    <!--
      A single top bar rather than a sidebar. With two primary destinations a
      sidebar would spend 240px of horizontal space to hold two links, and
      horizontal space is exactly what dense tables and wide charts need.
    -->
    <header
      class="sticky top-0 z-10 border-b bg-[var(--c-surface)]/85 backdrop-blur-sm"
    >
      <div class="mx-auto flex h-14 max-w-[1600px] items-center gap-6 px-5">
        <RouterLink to="/" class="flex items-center gap-2.5 shrink-0">
          <span
            class="grid size-7 place-items-center rounded-[var(--radius-md)] bg-[var(--c-accent)] text-[var(--text-xs)] font-bold text-[var(--c-accent-text)]"
            aria-hidden="true"
          >
            DI
          </span>
          <span class="text-[var(--text-sm)] font-semibold tracking-tight">
            Device Intelligence
          </span>
        </RouterLink>

        <nav class="flex items-center gap-1" aria-label="Main">
          <RouterLink
            v-for="item in nav"
            :key="item.to"
            :to="item.to"
            class="rounded-[var(--radius-md)] px-2.5 py-1.5 text-[var(--text-sm)] font-medium transition-colors"
            :class="
              route.path === item.to
                ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text)]'
                : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]'
            "
          >
            {{ item.label }}
          </RouterLink>
        </nav>

        <div class="ml-auto flex items-center gap-3">
          <!--
            The data has no dates, so the product says so rather than showing a
            fabricated "as of" timestamp. Honest beats reassuring.
          -->
          <span class="hidden text-[var(--text-2xs)] text-[var(--c-text-muted)] sm:inline">
            delivery sequence · no source dates
          </span>

          <button
            type="button"
            class="rounded-[var(--radius-md)] border px-2 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
            :aria-label="isDark ? 'Switch to light theme' : 'Switch to dark theme'"
            @click="toggle"
          >
            {{ isDark ? 'Light' : 'Dark' }}
          </button>
        </div>
      </div>
    </header>

    <main class="mx-auto w-full max-w-[1600px] flex-1 px-5 py-6">
      <RouterView v-slot="{ Component }">
        <Suspense>
          <component :is="Component" />
        </Suspense>
      </RouterView>
    </main>
  </div>
</template>
