<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { useAuth } from '@/features/auth/useAuth'
import { sectionsFor, type SettingsSection } from './sections'

/**
 * The frame every settings page sits in: a side menu of sections, the page beside it.
 *
 * A side menu here although the application itself uses a top bar. The top bar holds a handful of
 * destinations people move between all day; settings is a place someone goes to do one thing,
 * and a vertical list names every section at once, grouped, without a second row of tabs. Below
 * the large breakpoint the menu becomes a single scrolling row so the page keeps its width.
 */
const route = useRoute()
const { user } = useAuth()

const sections = computed(() => sectionsFor(user.value?.permissions ?? []))

const groups = computed(() => {
  const byGroup = new Map<string, SettingsSection[]>()
  for (const section of sections.value) {
    byGroup.set(section.group, [...(byGroup.get(section.group) ?? []), section])
  }
  return [...byGroup.entries()].map(([name, items]) => ({ name, items }))
})

/** /settings/roles stays current on /settings/roles/7 and the permission matrix. */
function isCurrent(to: string): boolean {
  return route.path === to || route.path.startsWith(`${to}/`)
}
</script>

<template>
  <div class="flex flex-col gap-6 lg:flex-row lg:gap-8">
    <aside class="shrink-0 lg:w-56">
      <h1 class="mb-3 text-xl font-semibold tracking-tight lg:mb-5">Settings</h1>

      <nav aria-label="Settings">
        <!-- Wide: grouped vertical list. -->
        <div class="hidden flex-col gap-5 lg:flex">
          <div v-for="group in groups" :key="group.name">
            <p
              class="mb-1.5 px-2.5 text-2xs font-semibold tracking-wider text-[var(--c-text-muted)] uppercase"
            >
              {{ group.name }}
            </p>
            <ul class="flex flex-col gap-0.5">
              <li v-for="item in group.items" :key="item.to">
                <RouterLink
                  :to="item.to"
                  :aria-current="isCurrent(item.to) ? 'page' : undefined"
                  class="block rounded-[var(--radius-md)] px-2.5 py-1.5 text-sm font-medium transition-colors"
                  :class="
                    isCurrent(item.to)
                      ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text)]'
                      : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]'
                  "
                >
                  {{ item.label }}
                </RouterLink>
              </li>
            </ul>
          </div>
        </div>

        <!-- Narrow: one scrolling row. -->
        <ul class="-mx-1 flex gap-1 overflow-x-auto border-b pb-2 lg:hidden">
          <li v-for="item in sections" :key="item.to" class="shrink-0">
            <RouterLink
              :to="item.to"
              :aria-current="isCurrent(item.to) ? 'page' : undefined"
              class="block rounded-[var(--radius-md)] px-2.5 py-1.5 text-sm font-medium whitespace-nowrap"
              :class="
                isCurrent(item.to)
                  ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text)]'
                  : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
              "
            >
              {{ item.label }}
            </RouterLink>
          </li>
        </ul>
      </nav>
    </aside>

    <section class="min-w-0 flex-1">
      <RouterView />
    </section>
  </div>
</template>
