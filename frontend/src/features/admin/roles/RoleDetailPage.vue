<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import {
  useDeleteRole,
  usePermissionCatalogue,
  useRole,
  useRoleMembers,
  useSetRolePermissions,
  useUpdateRole,
} from '@/api/admin'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { ApiError } from '@/api/client'
import { formatDateTime } from '@/lib/format'

/**
 * One role: its permissions, and the people who hold it.
 *
 * Permissions are edited in place with a save button rather than saving on each click. A role
 * change affects everyone holding it at once, and an interface that commits on every checkbox
 * would apply five half-finished states to a dozen people while an administrator makes up their
 * mind.
 *
 * The count of affected members is stated next to the save button for the same reason: "Save"
 * means something different when eleven people are about to gain an ability.
 */
const route = useRoute()
const router = useRouter()
const { can } = useAuth()

const roleId = computed(() => {
  const raw = route.params['id']
  const parsed = Number(Array.isArray(raw) ? raw[0] : raw)
  return Number.isFinite(parsed) ? parsed : null
})

const role = useRole(roleId)
const members = useRoleMembers(roleId)
const catalogue = usePermissionCatalogue()

const savePermissions = useSetRolePermissions()
const saveDetails = useUpdateRole()
const remove = useDeleteRole()

const manage = computed(() => can(Permission.RoleManage))

// --------------------------------------------------------- permissions
const draft = ref<string[]>([])

watch(
  () => role.data.value,
  (value) => {
    if (value) draft.value = [...value.permissionCodes]
  },
  { immediate: true },
)

const dirty = computed(() => {
  const current = role.data.value?.permissionCodes ?? []
  if (current.length !== draft.value.length) return true
  const set = new Set(draft.value)
  return current.some((code) => !set.has(code))
})

const added = computed(() => {
  const current = new Set(role.data.value?.permissionCodes ?? [])
  return draft.value.filter((code) => !current.has(code))
})

const removed = computed(() => {
  const next = new Set(draft.value)
  return (role.data.value?.permissionCodes ?? []).filter((code) => !next.has(code))
})

const categories = computed(() => {
  const groups = new Map<string, NonNullable<typeof catalogue.data.value>>()
  for (const permission of catalogue.data.value ?? []) {
    const list = groups.get(permission.category) ?? []
    list.push(permission)
    groups.set(permission.category, list)
  }
  return [...groups.entries()]
})

function toggle(code: string) {
  draft.value = draft.value.includes(code)
    ? draft.value.filter((c) => c !== code)
    : [...draft.value, code]
}

function reset() {
  draft.value = [...(role.data.value?.permissionCodes ?? [])]
  savePermissions.reset()
}

async function submitPermissions() {
  if (roleId.value === null || !dirty.value) return
  try {
    await savePermissions.mutateAsync({ id: roleId.value, permissionCodes: draft.value })
  } catch {
    // Rendered below. The draft is deliberately kept so the edit is not lost.
  }
}

// ------------------------------------------------------------- details
const editingDetails = ref(false)
const displayName = ref('')
const description = ref('')

watch(editingDetails, (open) => {
  if (!open) return
  displayName.value = role.data.value?.displayName ?? ''
  description.value = role.data.value?.description ?? ''
  saveDetails.reset()
})

async function submitDetails() {
  if (roleId.value === null) return
  try {
    await saveDetails.mutateAsync({
      id: roleId.value,
      displayName: displayName.value.trim(),
      description: description.value.trim(),
    })
    editingDetails.value = false
  } catch {
    // Rendered in the dialog.
  }
}

// -------------------------------------------------------------- delete
const deleting = ref(false)

async function submitDelete() {
  if (roleId.value === null) return
  try {
    await remove.mutateAsync(roleId.value)
    deleting.value = false
    await router.push('/settings/roles')
  } catch {
    // Rendered in the dialog.
  }
}

function message(error: unknown): string | null {
  if (!error) return null
  if (error instanceof ApiError) {
    return error.problem?.detail ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return 'Could not reach the server.'
}

const permissionError = computed(() => message(savePermissions.error.value))
const deleteError = computed(() => message(remove.error.value))
</script>

<template>
  <div class="flex flex-col gap-5">
    <nav class="text-xs text-[var(--c-text-muted)]">
      <RouterLink to="/settings/roles" class="hover:text-[var(--c-text)] hover:underline">
        Roles
      </RouterLink>
      <span aria-hidden="true"> / </span>
      <span>{{ role.data.value?.displayName ?? '…' }}</span>
    </nav>

    <AsyncBoundary
      :is-loading="role.isPending.value"
      :is-error="role.isError.value"
      :error="role.error.value"
      min-height="20rem"
      @retry="role.refetch()"
    >
      <template v-if="role.data.value">
        <header class="flex flex-wrap items-start justify-between gap-4">
          <div class="min-w-0">
            <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
              {{ role.data.value.displayName }}
              <span
                v-if="role.data.value.isSystem"
                class="rounded-full bg-[var(--c-surface-sunken)] px-2 py-0.5 text-xs text-[var(--c-text-muted)]"
              >
                built-in
              </span>
            </h2>
            <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
              <code class="text-xs">{{ role.data.value.code }}</code>
              · {{ role.data.value.description || 'No description.' }}
            </p>
          </div>

          <div v-if="manage" class="flex items-center gap-2">
            <Button size="sm" @click="editingDetails = true">Rename</Button>
            <Button
              v-if="!role.data.value.isSystem"
              size="sm"
              variant="danger"
              @click="deleting = true"
            >
              Delete
            </Button>
          </div>
        </header>

        <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_20rem]">
          <Card
            title="Permissions"
            :subtitle="`${draft.length} of ${catalogue.data.value?.length ?? 0} selected`"
            flush
          >
            <template v-if="manage" #actions>
              <div class="flex items-center gap-2">
                <Button v-if="dirty" size="sm" @click="reset">Discard</Button>
                <Button
                  size="sm"
                  variant="primary"
                  :disabled="!dirty"
                  :pending="savePermissions.isPending.value"
                  @click="submitPermissions"
                >
                  Save
                </Button>
              </div>
            </template>

            <!--
              What is about to change, and to how many people, before it happens. A permission
              grid gives no sense of scale on its own, and "Save" on a role held by eleven people
              is a different act from "Save" on an empty one.
            -->
            <p
              v-if="dirty"
              class="border-b px-4 py-2 text-xs"
              :style="{ backgroundColor: 'var(--c-warning-subtle)', color: 'var(--c-warning-text)' }"
            >
              <template v-if="added.length">+{{ added.length }} added</template>
              <template v-if="added.length && removed.length"> · </template>
              <template v-if="removed.length">−{{ removed.length }} removed</template>
              ·
              {{
                role.data.value.memberCount === 0
                  ? 'nobody holds this role yet'
                  : `affects ${role.data.value.memberCount} user(s) immediately`
              }}
            </p>

            <p
              v-if="permissionError"
              class="border-b px-4 py-2 text-xs"
              :style="{ backgroundColor: 'var(--c-danger-subtle)', color: 'var(--c-danger-text)' }"
              role="alert"
            >
              {{ permissionError }}
            </p>

            <div
              v-for="[category, permissions] in categories"
              :key="category"
              class="border-b last:border-b-0"
            >
              <h3
                class="bg-[var(--c-surface-sunken)] px-4 py-1.5 text-2xs font-medium tracking-wide text-[var(--c-text-muted)] uppercase"
              >
                {{ category }}
              </h3>

              <ul class="divide-y">
                <li v-for="permission in permissions" :key="permission.code">
                  <label
                    class="flex items-start gap-3 px-4 py-2.5"
                    :class="manage ? 'cursor-pointer hover:bg-[var(--c-surface-hover)]' : ''"
                  >
                    <input
                      type="checkbox"
                      class="mt-1"
                      :checked="draft.includes(permission.code)"
                      :disabled="!manage"
                      @change="toggle(permission.code)"
                    />
                    <span class="min-w-0 flex-1">
                      <span class="flex flex-wrap items-center gap-1.5 text-sm">
                        <span class="font-medium">{{ permission.displayName }}</span>
                        <code class="text-2xs text-[var(--c-text-muted)]">
                          {{ permission.code }}
                        </code>
                        <span
                          v-if="permission.isDangerous"
                          class="rounded-full px-1.5 py-0.5 text-2xs font-medium"
                          :style="{
                            backgroundColor: 'var(--c-warning-subtle)',
                            color: 'var(--c-warning-text)',
                          }"
                        >
                          sensitive
                        </span>
                      </span>
                      <span class="mt-0.5 block text-xs text-[var(--c-text-muted)]">
                        {{ permission.description }}
                      </span>
                    </span>
                  </label>
                </li>
              </ul>
            </div>
          </Card>

          <div class="flex flex-col gap-5">
            <Card :title="`Members (${role.data.value.memberCount})`" flush>
              <AsyncBoundary
                :is-loading="members.isPending.value"
                :is-error="members.isError.value"
                :error="members.error.value"
                :is-empty="(members.data.value?.length ?? 0) === 0"
                empty-message="Nobody holds this role."
                min-height="6rem"
                @retry="members.refetch()"
              >
                <ul class="divide-y">
                  <li v-for="member in members.data.value ?? []" :key="member.id">
                    <RouterLink
                      :to="`/settings/users/${member.id}`"
                      class="flex items-center justify-between gap-3 px-4 py-2 hover:bg-[var(--c-surface-hover)]"
                    >
                      <span class="min-w-0">
                        <span class="block truncate text-sm">
                          {{ member.displayName }}
                        </span>
                        <span class="block text-2xs text-[var(--c-text-muted)]">
                          {{ member.username }}
                        </span>
                      </span>
                      <span
                        v-if="!member.isActive"
                        class="shrink-0 text-2xs text-[var(--c-text-muted)]"
                      >
                        deactivated
                      </span>
                    </RouterLink>
                  </li>
                </ul>
              </AsyncBoundary>
            </Card>

            <Card title="History">
              <dl class="flex flex-col gap-2 text-xs">
                <div>
                  <dt class="text-[var(--c-text-muted)]">Created</dt>
                  <dd class="tabular">
                    {{ formatDateTime(role.data.value.createdAt) }}
                    <template v-if="role.data.value.createdBy">
                      by {{ role.data.value.createdBy }}
                    </template>
                  </dd>
                </div>
                <div>
                  <dt class="text-[var(--c-text-muted)]">Last changed</dt>
                  <dd class="tabular">
                    {{ formatDateTime(role.data.value.updatedAt) }}
                    <template v-if="role.data.value.updatedBy">
                      by {{ role.data.value.updatedBy }}
                    </template>
                  </dd>
                </div>
              </dl>
              <p class="mt-3 border-t pt-2.5 text-xs text-[var(--c-text-muted)]">
                Every change to this role's permissions is in the audit log, with what was added
                and what was removed.
              </p>
            </Card>
          </div>
        </div>
      </template>
    </AsyncBoundary>

    <Modal
      :open="editingDetails"
      title="Rename role"
      description="The code cannot change: audit entries and configuration refer to it."
      :busy="saveDetails.isPending.value"
      @close="editingDetails = false"
    >
      <div class="flex flex-col gap-3.5">
        <TextField v-model="displayName" label="Name" required />
        <TextField v-model="description" label="Description" />
      </div>

      <template #actions>
        <Button :disabled="saveDetails.isPending.value" @click="editingDetails = false">
          Cancel
        </Button>
        <Button
          variant="primary"
          :pending="saveDetails.isPending.value"
          :disabled="!displayName.trim()"
          @click="submitDetails"
        >
          Save
        </Button>
      </template>
    </Modal>

    <Modal
      :open="deleting"
      :title="`Delete ${role.data.value?.displayName ?? 'role'}?`"
      description="This cannot be undone. A role with members cannot be deleted."
      size="sm"
      :busy="remove.isPending.value"
      @close="deleting = false"
    >
      <p class="text-sm">
        <template v-if="(role.data.value?.memberCount ?? 0) > 0">
          {{ role.data.value?.memberCount }} user(s) still hold this role. Remove it from them
          first.
        </template>
        <template v-else> Nobody holds this role, so nobody loses access. </template>
      </p>

      <p
        v-if="deleteError"
        class="mt-3 rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
        :style="{
          borderColor: 'var(--c-danger)',
          backgroundColor: 'var(--c-danger-subtle)',
          color: 'var(--c-danger-text)',
        }"
        role="alert"
      >
        {{ deleteError }}
      </p>

      <template #actions>
        <Button :disabled="remove.isPending.value" @click="deleting = false">Cancel</Button>
        <Button
          variant="danger"
          :pending="remove.isPending.value"
          :disabled="(role.data.value?.memberCount ?? 0) > 0"
          @click="submitDelete"
        >
          Delete role
        </Button>
      </template>
    </Modal>
  </div>
</template>
