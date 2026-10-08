<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { usePermissionCatalogue, useRoles } from '@/api/admin'

/**
 * Every role against every permission, in one grid.
 *
 * Read-only, and that is the point rather than a limitation. Editing happens on a role's own
 * page, where the change is scoped to one role and the screen can say how many people it affects.
 * A grid where any cell is clickable makes a seventeen-column mis-click indistinguishable from an
 * intention - and one of those cells is `user.manage`.
 *
 * What the grid is genuinely good at is the comparison: which roles overlap, which permission
 * nobody has, whether Viewer really is a subset of Analyst. None of that is visible from four
 * separate role pages.
 */
const roles = useRoles()
const catalogue = usePermissionCatalogue()

const categories = computed(() => {
  const groups = new Map<string, NonNullable<typeof catalogue.data.value>>()
  for (const permission of catalogue.data.value ?? []) {
    const list = groups.get(permission.category) ?? []
    list.push(permission)
    groups.set(permission.category, list)
  }
  return [...groups.entries()]
})

const roleList = computed(() => roles.data.value ?? [])

function has(roleIndex: number, code: string): boolean {
  return roleList.value[roleIndex]?.permissionCodes.includes(code) ?? false
}

/** Permissions no role carries. Worth surfacing: it usually means a capability nobody can use. */
const orphaned = computed(() =>
  (catalogue.data.value ?? []).filter(
    (permission) => !roleList.value.some((role) => role.permissionCodes.includes(permission.code)),
  ),
)
</script>

<template>
  <div class="flex flex-col gap-5">
    <nav class="text-xs text-[var(--c-text-muted)]">
      <RouterLink to="/settings/roles" class="hover:text-[var(--c-text)] hover:underline">
        Roles
      </RouterLink>
      <span aria-hidden="true"> / </span>
      <span>Permission matrix</span>
    </nav>

    <header>
      <h2 class="text-lg font-semibold tracking-tight">Permission matrix</h2>
      <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
        What every role can do, side by side. Edit a role from its own page.
      </p>
    </header>

    <AsyncBoundary
      :is-loading="roles.isPending.value || catalogue.isPending.value"
      :is-error="roles.isError.value || catalogue.isError.value"
      :error="roles.error.value ?? catalogue.error.value"
      min-height="24rem"
      @retry="
        () => {
          roles.refetch()
          catalogue.refetch()
        }
      "
    >
      <p
        v-if="orphaned.length"
        class="rounded-[var(--radius-md)] border px-3 py-2 text-xs"
        :style="{
          borderColor: 'var(--c-warning)',
          backgroundColor: 'var(--c-warning-subtle)',
          color: 'var(--c-warning-text)',
        }"
      >
        No role carries {{ orphaned.map((p) => p.displayName).join(', ') }}. Only a direct
        permission on an individual user can grant that.
      </p>

      <Card flush>
        <!--
          The table scrolls inside the card rather than the page. With a sticky first column the
          permission name stays readable while the roles scroll, which is the only way a wide
          grid works on a laptop.
        -->
        <!--
          isolate: the sticky first column ranks only within the table, never against the top bar.
          contain-paint: at 390px the role headers widened the whole page by 80px although this
          wrapper scrolls; overflow-x alone did not bound them, paint containment does.
        -->
        <div class="isolate overflow-x-auto contain-paint">
          <table class="w-full border-collapse text-left text-sm">
            <thead>
              <tr class="border-b">
                <th
                  scope="col"
                  class="sticky left-0 z-10 bg-[var(--c-surface)] px-4 py-2 text-2xs font-medium tracking-wide text-[var(--c-text-muted)] uppercase"
                >
                  Permission
                </th>
                <th
                  v-for="role in roleList"
                  :key="role.id"
                  scope="col"
                  class="px-3 py-2 text-center"
                >
                  <RouterLink
                    :to="`/settings/roles/${role.id}`"
                    class="text-xs font-semibold hover:text-[var(--c-accent)] hover:underline"
                  >
                    {{ role.displayName }}
                  </RouterLink>
                  <span class="tabular block text-2xs font-normal text-[var(--c-text-muted)]">
                    {{ role.memberCount }} member(s)
                  </span>
                </th>
              </tr>
            </thead>

            <tbody>
              <template v-for="[category, permissions] in categories" :key="category">
                <tr>
                  <th
                    :colspan="roleList.length + 1"
                    scope="colgroup"
                    class="sticky left-0 bg-[var(--c-surface-sunken)] px-4 py-1.5 text-left text-2xs font-medium tracking-wide text-[var(--c-text-muted)] uppercase"
                  >
                    {{ category }}
                  </th>
                </tr>

                <tr
                  v-for="permission in permissions"
                  :key="permission.code"
                  class="border-b hover:bg-[var(--c-surface-hover)]"
                >
                  <th
                    scope="row"
                    class="sticky left-0 z-10 max-w-[18rem] bg-[var(--c-surface)] px-4 py-2 text-left font-normal"
                  >
                    <span class="flex items-center gap-1.5">
                      <span class="text-sm">{{ permission.displayName }}</span>
                      <span
                        v-if="permission.isDangerous"
                        class="rounded-full px-1.5 text-2xs font-medium"
                        :style="{
                          backgroundColor: 'var(--c-warning-subtle)',
                          color: 'var(--c-warning-text)',
                        }"
                        title="Sensitive"
                      >
                        !
                      </span>
                    </span>
                    <code class="block text-2xs text-[var(--c-text-muted)]">
                      {{ permission.code }}
                    </code>
                  </th>

                  <td
                    v-for="(role, index) in roleList"
                    :key="role.id"
                    class="px-3 py-2 text-center"
                  >
                    <!--
                      A filled dot and an empty ring, not a tick and a blank. Colour alone would
                      exclude anyone who cannot distinguish it; shape carries the meaning, and
                      the cell is labelled for a screen reader either way.
                    -->
                    <span
                      class="inline-block size-2.5 rounded-full"
                      :style="
                        has(index, permission.code)
                          ? { backgroundColor: 'var(--c-accent)' }
                          : {
                              border: '1.5px solid var(--c-border-strong)',
                              backgroundColor: 'transparent',
                            }
                      "
                      aria-hidden="true"
                    />
                    <span class="sr-only">
                      {{ role.displayName }}
                      {{ has(index, permission.code) ? 'has' : 'does not have' }}
                      {{ permission.displayName }}
                    </span>
                  </td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>
      </Card>
    </AsyncBoundary>
  </div>
</template>
