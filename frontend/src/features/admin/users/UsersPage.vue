<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Pagination from '@/design-system/Pagination.vue'
import CreateUserDialog from './CreateUserDialog.vue'
import { useRoles, useUsers, type UserFilters } from '@/api/admin'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * The user list.
 *
 * Search, role filter, active filter, sort and paging all live in component state rather than the
 * URL. That is a deliberate scope choice: the import history page already establishes the pattern
 * for URL-held filters and this list is reached from the menu rather than shared as a link. If it
 * ever needs to be linkable, the filters object is already the shape a query string would take.
 *
 * Two facts get a column each because they answer different questions. "Active" is an
 * administrator's decision; "locked" is a consequence of failed sign-ins that clears itself. A
 * single status column would conflate a person who left the company with one who mistyped their
 * password five times.
 */
const { can } = useAuth()

const search = ref('')
const roleFilter = ref('')
const activeFilter = ref<'all' | 'active' | 'inactive'>('all')
const sort = ref<'displayName' | 'lastLogin' | 'created'>('displayName')
const page = ref(1)
const creating = ref(false)

const roles = useRoles()

const filters = computed<UserFilters>(() => ({
  search: search.value.trim() || undefined,
  role: roleFilter.value || undefined,
  active: activeFilter.value === 'all' ? undefined : activeFilter.value === 'active',
  sort: sort.value,
  page: page.value,
  pageSize: 25,
}))

const users = useUsers(filters)

// Any change to what is being filtered puts the reader back on page 1. Without this, narrowing a
// search while on page 4 shows an empty table and looks like "no results".
watch([search, roleFilter, activeFilter, sort], () => {
  page.value = 1
})

const selectClass =
  'rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-xs ' +
  'text-[var(--c-text)]'
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 class="text-lg font-semibold tracking-tight">Users</h2>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          Who can sign in, and what each of them may do.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <Button v-if="can(Permission.UserManage)" variant="primary" @click="creating = true">
          New user
        </Button>
      </div>
    </header>

    <Card flush>
      <template #actions>
        <span class="tabular text-xs text-[var(--c-text-muted)]">
          {{ users.data.value?.total ?? 0 }} total
        </span>
      </template>

      <div class="flex flex-wrap items-center gap-2 border-b px-4 py-2.5">
        <label class="sr-only" for="user-search">Search users</label>
        <input
          id="user-search"
          v-model="search"
          type="search"
          placeholder="Search name, username or email"
          class="min-w-0 flex-1 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm placeholder:text-[var(--c-text-muted)]"
        />

        <label class="sr-only" for="user-role">Role</label>
        <select id="user-role" v-model="roleFilter" :class="selectClass">
          <option value="">All roles</option>
          <option v-for="role in roles.data.value ?? []" :key="role.code" :value="role.code">
            {{ role.displayName }}
          </option>
        </select>

        <label class="sr-only" for="user-active">Status</label>
        <select id="user-active" v-model="activeFilter" :class="selectClass">
          <option value="all">Active and inactive</option>
          <option value="active">Active only</option>
          <option value="inactive">Deactivated only</option>
        </select>

        <label class="sr-only" for="user-sort">Sort</label>
        <select id="user-sort" v-model="sort" :class="selectClass">
          <option value="displayName">Name</option>
          <option value="lastLogin">Last sign-in</option>
          <option value="created">Newest</option>
        </select>
      </div>

      <AsyncBoundary
        :is-loading="users.isPending.value"
        :is-error="users.isError.value"
        :error="users.error.value"
        :is-empty="(users.data.value?.items.length ?? 0) === 0"
        empty-message="No users match these filters."
        min-height="20rem"
        @retry="users.refetch()"
      >
        <div class="overflow-x-auto">
          <table class="w-full text-left text-sm">
            <thead
              class="border-b text-2xs tracking-wide text-[var(--c-text-muted)] uppercase"
            >
              <tr>
                <th scope="col" class="px-4 py-2 font-medium">User</th>
                <th scope="col" class="px-4 py-2 font-medium">Roles</th>
                <th scope="col" class="px-4 py-2 font-medium">Status</th>
                <th scope="col" class="px-4 py-2 font-medium">Last sign-in</th>
              </tr>
            </thead>
            <tbody class="divide-y">
              <tr
                v-for="user in users.data.value?.items ?? []"
                :key="user.id"
                class="hover:bg-[var(--c-surface-hover)]"
              >
                <td class="px-4 py-2.5">
                  <RouterLink
                    :to="`/settings/users/${user.id}`"
                    class="font-medium hover:text-[var(--c-accent)] hover:underline"
                  >
                    {{ user.displayName }}
                  </RouterLink>
                  <p class="text-2xs text-[var(--c-text-muted)]">
                    {{ user.username
                    }}<template v-if="user.jobTitle"> · {{ user.jobTitle }}</template>
                  </p>
                </td>

                <td class="px-4 py-2.5">
                  <!--
                    Roles as chips, and "no role" spelled out. An empty cell reads as missing
                    data; an account with no role is a real and slightly alarming state, because
                    it can sign in and do nothing.
                  -->
                  <div v-if="user.roles.length" class="flex flex-wrap gap-1">
                    <span
                      v-for="role in user.roles"
                      :key="role.id"
                      class="rounded-full bg-[var(--c-surface-sunken)] px-1.5 py-0.5 text-2xs text-[var(--c-text-secondary)]"
                    >
                      {{ role.displayName }}
                    </span>
                  </div>
                  <span v-else class="text-2xs text-[var(--c-warning)]">
                    no role
                  </span>
                </td>

                <td class="px-4 py-2.5">
                  <span
                    class="inline-flex items-center gap-1.5 text-xs"
                    :style="{
                      color: user.isActive ? 'var(--c-text-secondary)' : 'var(--c-text-muted)',
                    }"
                  >
                    <span
                      class="size-1.5 rounded-full"
                      :style="{
                        backgroundColor: user.isActive
                          ? 'var(--c-success)'
                          : 'var(--c-text-muted)',
                      }"
                      aria-hidden="true"
                    />
                    {{ user.isActive ? 'Active' : 'Deactivated' }}
                  </span>
                  <span
                    v-if="user.isLocked"
                    class="ml-1.5 rounded-full px-1.5 py-0.5 text-2xs font-medium"
                    :style="{
                      backgroundColor: 'var(--c-warning-subtle)',
                      color: 'var(--c-warning)',
                    }"
                    title="Temporarily locked by failed sign-in attempts. Clears itself."
                  >
                    locked
                  </span>
                </td>

                <td class="tabular px-4 py-2.5 text-xs text-[var(--c-text-secondary)]">
                  <span v-if="user.lastLoginAt" :title="formatDateTime(user.lastLoginAt)">
                    {{ formatRelative(user.lastLoginAt) }}
                  </span>
                  <span v-else class="text-[var(--c-text-muted)]">never</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <Pagination
          :page="users.data.value?.page ?? 1"
          :page-size="users.data.value?.pageSize ?? 25"
          :total="users.data.value?.total ?? 0"
          :loading="users.isFetching.value"
          @update:page="(p) => (page = p)"
        />
      </AsyncBoundary>
    </Card>

    <CreateUserDialog :open="creating" @close="creating = false" />
  </div>
</template>
