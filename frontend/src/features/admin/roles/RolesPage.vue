<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useCreateRole, usePermissionCatalogue, useRoles } from '@/api/admin'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { ApiError } from '@/api/client'

/**
 * The roles, and what each one can do.
 *
 * Built-in roles are listed first and marked. They cannot be deleted or renamed by code - too
 * much refers to them by that code - but their permissions remain editable, because deciding what
 * "Analyst" means in this organisation is exactly the decision Role Management exists for.
 *
 * The count of permissions is shown next to each role rather than the list, with the list a click
 * away. Seventeen codes on four rows is a wall of text; "8 of 17" is a number a reader can
 * compare at a glance, which is what a list page is for.
 */
const router = useRouter()
const { can } = useAuth()

const roles = useRoles()
const catalogue = usePermissionCatalogue()
const create = useCreateRole()

const creating = ref(false)
const code = ref('')
const displayName = ref('')
const description = ref('')
const selected = ref<string[]>([])

watch(creating, (open) => {
  if (!open) return
  code.value = ''
  displayName.value = ''
  description.value = ''
  selected.value = []
  create.reset()
})

// The code is derived from the name as it is typed, and stays derived until the code field is
// touched. Most people never need to think about it; the few who do can still override it.
const codeTouched = ref(false)
watch(displayName, (value) => {
  if (!codeTouched.value) {
    code.value = value.trim().toLowerCase().replace(/[\s-]+/g, '_').replace(/[^a-z0-9_]/g, '')
  }
})

const categories = computed(() => {
  const groups = new Map<string, typeof catalogue.data.value>()
  for (const permission of catalogue.data.value ?? []) {
    const list = groups.get(permission.category) ?? []
    list!.push(permission)
    groups.set(permission.category, list)
  }
  return [...groups.entries()]
})

const fieldErrors = computed(() => {
  const error = create.error.value
  if (!(error instanceof ApiError)) return {} as Record<string, string>
  return Object.fromEntries(error.fieldErrors.map((f) => [f.field, f.message]))
})

function toggle(permissionCode: string) {
  selected.value = selected.value.includes(permissionCode)
    ? selected.value.filter((c) => c !== permissionCode)
    : [...selected.value, permissionCode]
}

async function submit() {
  if (!code.value || !displayName.value.trim()) return

  try {
    const created = await create.mutateAsync({
      code: code.value,
      displayName: displayName.value.trim(),
      description: description.value.trim(),
      permissionCodes: selected.value,
    })
    creating.value = false
    await router.push(`/admin/roles/${created.id}`)
  } catch {
    // Rendered from create.error.
  }
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Roles</h1>
        <p class="mt-0.5 text-[var(--text-sm)] text-[var(--c-text-secondary)]">
          A role is a named set of permissions. Users receive the union of the roles they hold.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <RouterLink
          to="/admin/roles/matrix"
          class="rounded-[var(--radius-md)] border px-3 py-1.5 text-[var(--text-sm)] font-medium hover:bg-[var(--c-surface-hover)]"
        >
          Permission matrix
        </RouterLink>
        <Button v-if="can(Permission.RoleManage)" variant="primary" @click="creating = true">
          New role
        </Button>
      </div>
    </header>

    <AsyncBoundary
      :is-loading="roles.isPending.value"
      :is-error="roles.isError.value"
      :error="roles.error.value"
      min-height="16rem"
      @retry="roles.refetch()"
    >
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <RouterLink
          v-for="role in roles.data.value ?? []"
          :key="role.id"
          :to="`/admin/roles/${role.id}`"
          class="group flex flex-col rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4 shadow-[var(--shadow-xs)] transition-colors hover:border-[var(--c-border-strong)]"
        >
          <div class="flex items-start justify-between gap-3">
            <h2
              class="text-[var(--text-sm)] font-semibold group-hover:text-[var(--c-accent)]"
            >
              {{ role.displayName }}
            </h2>
            <span
              v-if="role.isSystem"
              class="shrink-0 rounded-full bg-[var(--c-surface-sunken)] px-1.5 py-0.5 text-[var(--text-2xs)] text-[var(--c-text-muted)]"
              title="Built in. Cannot be deleted or renamed; its permissions can still be changed."
            >
              built-in
            </span>
          </div>

          <code class="mt-0.5 text-[var(--text-2xs)] text-[var(--c-text-muted)]">
            {{ role.code }}
          </code>

          <p class="mt-2 flex-1 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            {{ role.description || 'No description.' }}
          </p>

          <dl class="tabular mt-3 flex gap-5 border-t pt-2.5 text-[var(--text-xs)]">
            <div>
              <dt class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">Permissions</dt>
              <dd class="font-semibold">
                {{ role.permissionCodes.length }}
                <span class="font-normal text-[var(--c-text-muted)]">
                  / {{ catalogue.data.value?.length ?? '—' }}
                </span>
              </dd>
            </div>
            <div>
              <dt class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">Members</dt>
              <dd
                class="font-semibold"
                :style="role.memberCount === 0 ? { color: 'var(--c-text-muted)' } : undefined"
              >
                {{ role.memberCount }}
              </dd>
            </div>
          </dl>
        </RouterLink>
      </div>
    </AsyncBoundary>

    <Modal
      :open="creating"
      title="New role"
      description="Create one when several people need the same set. For a single exception, use a direct permission on that user instead."
      size="lg"
      :busy="create.isPending.value"
      @close="creating = false"
    >
      <div class="flex flex-col gap-4">
        <div class="grid gap-3.5 sm:grid-cols-2">
          <TextField
            v-model="displayName"
            label="Name"
            required
            :error="fieldErrors['displayName'] ?? null"
          />
          <TextField
            v-model="code"
            label="Code"
            required
            :error="fieldErrors['code'] ?? null"
            hint="Permanent. Used in the audit log and in configuration."
            @update:model-value="codeTouched = true"
          />
        </div>

        <TextField
          v-model="description"
          label="Description"
          optional-note="shown wherever the role is offered"
          placeholder="What this role is for"
        />

        <fieldset>
          <legend class="text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">
            Permissions ({{ selected.length }} selected)
          </legend>

          <div class="mt-2 flex flex-col gap-3">
            <div v-for="[category, permissions] in categories" :key="category">
              <h4
                class="mb-1 text-[var(--text-2xs)] font-medium tracking-wide text-[var(--c-text-muted)] uppercase"
              >
                {{ category }}
              </h4>
              <div class="flex flex-col gap-1">
                <label
                  v-for="permission in permissions ?? []"
                  :key="permission.code"
                  class="flex cursor-pointer items-start gap-2.5 rounded-[var(--radius-md)] border px-2.5 py-1.5 hover:bg-[var(--c-surface-hover)]"
                  :style="
                    selected.includes(permission.code)
                      ? { borderColor: 'var(--c-accent)', backgroundColor: 'var(--c-accent-subtle)' }
                      : undefined
                  "
                >
                  <input
                    type="checkbox"
                    class="mt-0.5"
                    :checked="selected.includes(permission.code)"
                    @change="toggle(permission.code)"
                  />
                  <span class="min-w-0">
                    <span class="flex items-center gap-1.5 text-[var(--text-sm)]">
                      {{ permission.displayName }}
                      <span
                        v-if="permission.isDangerous"
                        class="rounded-full px-1.5 text-[var(--text-2xs)] font-medium"
                        :style="{
                          backgroundColor: 'var(--c-warning-subtle)',
                          color: 'var(--c-warning)',
                        }"
                      >
                        sensitive
                      </span>
                    </span>
                    <span class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
                      {{ permission.description }}
                    </span>
                  </span>
                </label>
              </div>
            </div>
          </div>
        </fieldset>
      </div>

      <template #actions>
        <Button :disabled="create.isPending.value" @click="creating = false">Cancel</Button>
        <Button
          variant="primary"
          :pending="create.isPending.value"
          :disabled="!code || !displayName.trim()"
          @click="submit"
        >
          Create role
        </Button>
      </template>
    </Modal>
  </div>
</template>
