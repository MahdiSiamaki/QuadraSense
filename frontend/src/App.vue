<script setup lang="ts">
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { computed, ref } from 'vue'
import { useFreshness } from '@/features/imports/useImportQueries'
import { formatDate } from '@/lib/format'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { useLogout } from '@/api/auth'
import UserMenu from '@/features/auth/UserMenu.vue'
import BrandMark from '@/design-system/BrandMark.vue'
import ThemeToggle from '@/design-system/ThemeToggle.vue'

const route = useRoute()
const router = useRouter()

const { user, isAuthenticated, can, canAny } = useAuth()
const logout = useLogout()

/**
 * Routes that render themselves whole, without the application shell.
 *
 * Sign-in and the forced password change are both states in which the navigation would be
 * actively misleading: one has no session behind it, and the other has a session the API refuses
 * every request from until the password is changed.
 */
const bare = computed(() => route.meta['chrome'] === false)

/**
 * The navigation, filtered to what this user can actually open.
 *
 * Hiding is a usability decision, not a security one - every one of these pages is checked again
 * server-side, and the route guard checks it a second time on the way in. What it buys is that a
 * Viewer does not see four links that all lead to an access-denied page, which is how people
 * learn to ignore what the interface tells them.
 */
const nav = computed(() =>
  [
    { to: '/', label: 'Dashboard', show: can(Permission.DashboardView) },
    { to: '/devices', label: 'Devices', show: can(Permission.DeviceView) },
    { to: '/imports', label: 'Imports', show: can(Permission.ImportView) },
    { to: '/lookup', label: 'Lookup', show: can(Permission.LookupSubscriber) },
    { to: '/lookup/imsi', label: 'IMSI', show: can(Permission.LookupImsi) },
    // Reachable by anybody who can look up any one of the three; the server decides per centre
    // which sections they actually get back.
    {
      to: '/relationships',
      label: 'Relationships',
      show: canAny(Permission.LookupSubscriber, Permission.LookupImsi, Permission.LookupImei),
    },
    // Users, roles, the audit log and device image review moved to Settings (the gear): they
    // configure the system, and this bar is for the work done in it.
  ].filter((item) => item.show),
)

function isCurrent(to: string): boolean {
  if (to === '/') return route.path === '/'
  // /lookup must not claim /lookup/imsi, which is its own destination.
  if (to === '/lookup') return route.path === '/lookup'
  return route.path.startsWith(to)
}

const inSettings = computed(() => route.path.startsWith('/settings'))

const freshness = useFreshness({ enabled: computed(() => can(Permission.ImportView)) })
const sqm = computed(() => freshness.data.value?.find((f) => f.sourceCode === 'SQM') ?? null)

const menuOpen = ref(false)

async function signOut() {
  menuOpen.value = false
  await logout.mutateAsync().catch(() => undefined)
  await router.replace('/login')
}
</script>

<template>
  <!-- Login and the forced password change own the whole viewport. -->
  <RouterView v-if="bare" />

  <div v-else class="flex min-h-screen flex-col">
    <!--
      A single top bar rather than a sidebar. With a handful of primary destinations a sidebar
      would spend 240px of horizontal space to hold five links, and horizontal space is exactly
      what dense tables and wide charts need.
    -->
    <header class="sticky top-0 z-10 border-b bg-[var(--c-surface)]/85 backdrop-blur-sm">
      <div class="mx-auto flex h-14 max-w-[1600px] items-center gap-6 px-5">
        <RouterLink to="/" class="flex shrink-0 items-center gap-2">
          <BrandMark :size="26" />
          <span class="hidden text-[var(--text-sm)] font-semibold tracking-tight sm:inline">
            QuadraSense
          </span>
        </RouterLink>

        <!-- Scrolls within the bar on a narrow screen rather than widening the whole page. -->
        <nav class="flex min-w-0 items-center gap-1 overflow-x-auto" aria-label="Main">
          <RouterLink
            v-for="item in nav"
            :key="item.to"
            :to="item.to"
            class="shrink-0 rounded-[var(--radius-md)] px-2.5 py-1.5 text-[var(--text-sm)] font-medium whitespace-nowrap transition-colors"
            :class="
              isCurrent(item.to)
                ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text)]'
                : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]'
            "
          >
            {{ item.label }}
          </RouterLink>
        </nav>

        <div class="ml-auto flex items-center gap-3">
          <RouterLink
            v-if="sqm?.latestBusinessDate"
            to="/imports"
            class="hidden items-center gap-1.5 text-[var(--text-2xs)] text-[var(--c-text-muted)] hover:text-[var(--c-text-secondary)] lg:inline-flex"
            :title="`Latest successfully imported day. ${sqm.missingBusinessDates.length} expected day(s) missing.`"
          >
            <span
              class="size-1.5 rounded-full"
              :style="{
                backgroundColor:
                  sqm.missingBusinessDates.length || (sqm.daysBehind ?? 0) > 45
                    ? 'var(--c-warning)'
                    : 'var(--c-success)',
              }"
              aria-hidden="true"
            />
            data through {{ formatDate(sqm.latestBusinessDate) }}
          </RouterLink>

          <div class="flex items-center gap-0.5">
            <ThemeToggle />

            <RouterLink
              to="/settings"
              class="settings-link grid size-9 place-items-center rounded-full text-[var(--c-text-secondary)] transition-colors hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
              :class="{ 'is-current text-[var(--c-text)]': inSettings }"
              aria-label="Settings"
              title="Settings"
              :aria-current="inSettings ? 'page' : undefined"
            >
              <svg viewBox="0 0 24 24" width="19" height="19" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z" />
                <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" />
              </svg>
            </RouterLink>
          </div>

          <UserMenu
            v-if="isAuthenticated && user"
            :user="user"
            :open="menuOpen"
            :signing-out="logout.isPending.value"
            @toggle="menuOpen = !menuOpen"
            @close="menuOpen = false"
            @sign-out="signOut"
          />
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

<style scoped>
/* The gear turns a little on hover and while Settings is open; the one flourish it gets. */
.settings-link svg {
  transition: transform 400ms cubic-bezier(0.22, 1, 0.36, 1);
}

.settings-link:hover svg,
.settings-link.is-current svg {
  transform: rotate(45deg);
}

@media (prefers-reduced-motion: reduce) {
  .settings-link svg {
    transition: none;
  }
}
</style>
