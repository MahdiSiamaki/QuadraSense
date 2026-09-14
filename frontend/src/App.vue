<script setup lang="ts">
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { computed, ref } from 'vue'
import { useTheme } from '@/lib/theme'
import { useFreshness } from '@/features/imports/useImportQueries'
import { formatDate } from '@/lib/format'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { useLogout } from '@/api/auth'
import UserMenu from '@/features/auth/UserMenu.vue'

const { isDark, toggle } = useTheme()
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
    { to: '/imports', label: 'Imports', show: can(Permission.ImportView) },
    { to: '/lookup', label: 'Lookup', show: can(Permission.LookupSubscriber) },
    { to: '/lookup/imsi', label: 'IMSI', show: can(Permission.LookupImsi) },
    {
      to: '/admin/users',
      label: 'Users',
      show: canAny(Permission.UserView, Permission.RoleView),
    },
    { to: '/admin/audit', label: 'Audit', show: can(Permission.AuditView) },
  ].filter((item) => item.show),
)

/** Highlights /admin/users while the reader is on /admin/users/17 or /admin/roles. */
function isCurrent(to: string): boolean {
  if (to === '/') return route.path === '/'
  if (to === '/admin/users') return route.path.startsWith('/admin/users') || route.path.startsWith('/admin/roles')
  // /lookup must not claim /lookup/imsi, which is its own destination.
  if (to === '/lookup') return route.path === '/lookup'
  return route.path.startsWith(to)
}

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
        <RouterLink to="/" class="flex shrink-0 items-center gap-2.5">
          <span
            class="grid size-7 place-items-center rounded-[var(--radius-md)] bg-[var(--c-accent)] text-[var(--text-xs)] font-bold text-[var(--c-accent-text)]"
            aria-hidden="true"
          >
            DI
          </span>
          <span class="hidden text-[var(--text-sm)] font-semibold tracking-tight sm:inline">
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

          <button
            type="button"
            class="rounded-[var(--radius-md)] border px-2 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
            :aria-label="isDark ? 'Switch to light theme' : 'Switch to dark theme'"
            @click="toggle"
          >
            {{ isDark ? 'Light' : 'Dark' }}
          </button>

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
