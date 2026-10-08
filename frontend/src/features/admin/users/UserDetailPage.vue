<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import SessionList from '@/features/profile/SessionList.vue'
import UserPermissionsPanel from './UserPermissionsPanel.vue'
import {
  useResetPassword,
  useRevokeUserSessions,
  useRoles,
  useSetUserActive,
  useSetUserRoles,
  useUnlockUser,
  useUser,
  useUserSessions,
} from '@/api/admin'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { ApiError } from '@/api/client'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * One user: who they are, what they can do, and what has been done to their account.
 *
 * The page is arranged by question rather than by table. "What can this person do" is the large
 * left column, because it is what an access review is here to answer; "what state is the account
 * in" and "where are they signed in from" are the narrow right-hand column, because they are
 * checked rather than read.
 */
const route = useRoute()
const { user: me, can } = useAuth()

const userId = computed(() => {
  const raw = route.params['id']
  const parsed = Number(Array.isArray(raw) ? raw[0] : raw)
  return Number.isFinite(parsed) ? parsed : null
})

const user = useUser(userId)
const sessions = useUserSessions(userId)
const roles = useRoles()

const setActive = useSetUserActive()
const setRoles = useSetUserRoles()
const resetPassword = useResetPassword()
const unlock = useUnlockUser()
const revokeSessions = useRevokeUserSessions()

const manage = computed(() => can(Permission.UserManage))
const isSelf = computed(() => me.value?.id === user.data.value?.id)

const locked = computed(() => {
  const until = user.data.value?.lockedUntil
  return until !== null && until !== undefined && new Date(until) > new Date()
})

// ------------------------------------------------------------------ roles
const editingRoles = ref(false)
const draftRoles = ref<string[]>([])

watch(editingRoles, (open) => {
  if (!open) return
  draftRoles.value = (user.data.value?.roles ?? []).map((r) => r.code)
  setRoles.reset()
})

function toggleRole(code: string) {
  draftRoles.value = draftRoles.value.includes(code)
    ? draftRoles.value.filter((c) => c !== code)
    : [...draftRoles.value, code]
}

async function saveRoles() {
  if (userId.value === null) return
  try {
    await setRoles.mutateAsync({ id: userId.value, roleCodes: draftRoles.value })
    editingRoles.value = false
  } catch {
    // Rendered below.
  }
}

// -------------------------------------------------------------- password
const resetting = ref(false)
const newPassword = ref('')

watch(resetting, (open) => {
  if (open) {
    newPassword.value = ''
    resetPassword.reset()
  }
})

async function submitReset() {
  if (userId.value === null || newPassword.value.length < 12) return
  try {
    await resetPassword.mutateAsync({ id: userId.value, newPassword: newPassword.value })
    resetting.value = false
    newPassword.value = ''
  } catch {
    // Rendered in the dialog.
  }
}

// ------------------------------------------------------------ activation
const deactivating = ref(false)
const reason = ref('')

watch(deactivating, (open) => {
  if (open) {
    reason.value = ''
    setActive.reset()
  }
})

async function submitDeactivate() {
  if (userId.value === null) return
  try {
    await setActive.mutateAsync({
      id: userId.value,
      isActive: false,
      reason: reason.value.trim() || null,
    })
    deactivating.value = false
  } catch {
    // Rendered in the dialog.
  }
}

function reactivate() {
  if (userId.value === null) return
  setActive.mutate({ id: userId.value, isActive: true, reason: null })
}

/** Turns any mutation failure into one line a person can act on. */
function message(error: unknown): string | null {
  if (!error) return null
  if (error instanceof ApiError) {
    return error.problem?.detail ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return 'Could not reach the server.'
}

const activationError = computed(() => message(setActive.error.value))
const roleError = computed(() => message(setRoles.error.value))
const resetError = computed(() => message(resetPassword.error.value))
</script>

<template>
  <div class="flex flex-col gap-5">
    <nav class="text-xs text-[var(--c-text-muted)]">
      <RouterLink to="/settings/users" class="hover:text-[var(--c-text)] hover:underline">
        Users
      </RouterLink>
      <span aria-hidden="true"> / </span>
      <span>{{ user.data.value?.displayName ?? '…' }}</span>
    </nav>

    <AsyncBoundary
      :is-loading="user.isPending.value"
      :is-error="user.isError.value"
      :error="user.error.value"
      min-height="20rem"
      @retry="user.refetch()"
    >
      <template v-if="user.data.value">
        <header class="flex flex-wrap items-start justify-between gap-4">
          <div class="min-w-0">
            <h2 class="flex flex-wrap items-center gap-2 text-lg font-semibold tracking-tight">
              {{ user.data.value.displayName }}
              <span
                v-if="!user.data.value.isActive"
                class="rounded-full px-2 py-0.5 text-xs font-medium"
                :style="{ backgroundColor: 'var(--c-surface-sunken)', color: 'var(--c-text-muted)' }"
              >
                Deactivated
              </span>
              <span
                v-else-if="locked"
                class="rounded-full px-2 py-0.5 text-xs font-medium"
                :style="{ backgroundColor: 'var(--c-warning-subtle)', color: 'var(--c-warning-text)' }"
              >
                Locked
              </span>
            </h2>
            <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
              {{ user.data.value.username }}
              <template v-if="user.data.value.jobTitle"> · {{ user.data.value.jobTitle }}</template>
              <template v-if="user.data.value.email"> · {{ user.data.value.email }}</template>
              <span class="text-[var(--c-text-muted)]">
                · {{ user.data.value.provider }} account
              </span>
            </p>
          </div>

          <div v-if="manage" class="flex flex-wrap items-center gap-2">
            <Button
              v-if="locked"
              size="sm"
              :pending="unlock.isPending.value"
              @click="unlock.mutate(user.data.value.id)"
            >
              Unlock
            </Button>
            <Button size="sm" @click="resetting = true">Reset password</Button>
            <Button
              v-if="user.data.value.activeSessions > 0"
              size="sm"
              :pending="revokeSessions.isPending.value"
              @click="revokeSessions.mutate(user.data.value.id)"
            >
              Sign out everywhere
            </Button>

            <!--
              Self-deactivation is refused by the API with a 409, and hidden here for the same
              reason: it signs you out in the same transaction, and if you were the last
              administrator nobody can undo it.
            -->
            <Button
              v-if="user.data.value.isActive && !isSelf"
              size="sm"
              variant="danger"
              @click="deactivating = true"
            >
              Deactivate
            </Button>
            <Button
              v-else-if="!user.data.value.isActive"
              size="sm"
              variant="primary"
              :pending="setActive.isPending.value"
              @click="reactivate"
            >
              Reactivate
            </Button>
          </div>
        </header>

        <p
          v-if="activationError"
          class="rounded-[var(--radius-md)] border px-3 py-2 text-sm"
          :style="{
            borderColor: 'var(--c-danger)',
            backgroundColor: 'var(--c-danger-subtle)',
            color: 'var(--c-danger-text)',
          }"
          role="alert"
        >
          {{ activationError }}
        </p>

        <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
          <div class="flex flex-col gap-5">
            <Card title="Roles" :subtitle="`${user.data.value.roles.length} assigned`">
              <template v-if="manage" #actions>
                <Button size="sm" @click="editingRoles = true">Edit roles</Button>
              </template>

              <div v-if="user.data.value.roles.length" class="flex flex-wrap gap-2">
                <RouterLink
                  v-for="role in user.data.value.roles"
                  :key="role.id"
                  :to="`/settings/roles/${role.id}`"
                  class="rounded-full border px-2.5 py-1 text-xs font-medium hover:bg-[var(--c-surface-hover)]"
                >
                  {{ role.displayName }}
                </RouterLink>
              </div>
              <p v-else class="text-sm text-[var(--c-warning-text)]">
                No role assigned. This account can sign in but will see nothing.
              </p>
            </Card>

            <UserPermissionsPanel :user="user.data.value" :editable="manage" />
          </div>

          <div class="flex flex-col gap-5">
            <Card title="Account">
              <dl class="flex flex-col gap-2.5 text-sm">
                <div class="flex justify-between gap-3">
                  <dt class="text-[var(--c-text-muted)]">Last sign-in</dt>
                  <dd class="tabular text-right">
                    <span
                      v-if="user.data.value.lastLoginAt"
                      :title="formatDateTime(user.data.value.lastLoginAt)"
                    >
                      {{ formatRelative(user.data.value.lastLoginAt) }}
                    </span>
                    <span v-else class="text-[var(--c-text-muted)]">never</span>
                  </dd>
                </div>
                <div class="flex justify-between gap-3">
                  <dt class="text-[var(--c-text-muted)]">From</dt>
                  <dd class="tabular text-right">{{ user.data.value.lastLoginIp ?? '—' }}</dd>
                </div>
                <div class="flex justify-between gap-3">
                  <dt class="text-[var(--c-text-muted)]">Failed attempts</dt>
                  <dd class="tabular text-right">
                    {{ user.data.value.failedLoginCount }}
                    <span v-if="locked" class="text-[var(--c-warning-text)]">
                      · until {{ formatRelative(user.data.value.lockedUntil) }}
                    </span>
                  </dd>
                </div>
                <div class="flex justify-between gap-3">
                  <dt class="text-[var(--c-text-muted)]">Password set</dt>
                  <dd class="tabular text-right">
                    <span
                      v-if="user.data.value.passwordUpdatedAt"
                      :title="formatDateTime(user.data.value.passwordUpdatedAt)"
                    >
                      {{ formatRelative(user.data.value.passwordUpdatedAt) }}
                    </span>
                    <span v-else class="text-[var(--c-text-muted)]">—</span>
                  </dd>
                </div>
                <div v-if="user.data.value.mustChangePassword" class="flex justify-between gap-3">
                  <dt class="text-[var(--c-text-muted)]">Pending</dt>
                  <dd class="text-right text-[var(--c-warning-text)]">must change password</dd>
                </div>

                <div class="mt-1 border-t pt-2.5 text-xs text-[var(--c-text-muted)]">
                  <p>
                    Created {{ formatDateTime(user.data.value.createdAt) }}
                    <template v-if="user.data.value.createdBy">
                      by {{ user.data.value.createdBy }}
                    </template>
                  </p>
                  <p class="mt-0.5">
                    Updated {{ formatDateTime(user.data.value.updatedAt) }}
                    <template v-if="user.data.value.updatedBy">
                      by {{ user.data.value.updatedBy }}
                    </template>
                  </p>
                  <!--
                    Kept on screen rather than only in the audit log. "Why is this account off?"
                    is asked far more often than anyone opens the log to find out.
                  -->
                  <p v-if="user.data.value.deactivatedAt" class="mt-1.5 text-[var(--c-text-secondary)]">
                    Deactivated {{ formatDateTime(user.data.value.deactivatedAt) }}
                    <template v-if="user.data.value.deactivatedBy">
                      by {{ user.data.value.deactivatedBy }}
                    </template>
                    <template v-if="user.data.value.deactivationReason">
                      — &ldquo;{{ user.data.value.deactivationReason }}&rdquo;
                    </template>
                  </p>
                </div>
              </dl>
            </Card>

            <Card
              title="Signed-in devices"
              :subtitle="`${user.data.value.activeSessions} active`"
              flush
            >
              <SessionList :query="sessions" readonly />
            </Card>
          </div>
        </div>
      </template>
    </AsyncBoundary>

    <!-- --------------------------------------------------------- dialogs -->
    <Modal
      :open="editingRoles"
      title="Roles"
      description="A user receives the union of every role's permissions."
      :busy="setRoles.isPending.value"
      @close="editingRoles = false"
    >
      <div class="flex flex-col gap-1.5">
        <label
          v-for="(role, index) in roles.data.value ?? []"
          :key="role.code"
          class="flex cursor-pointer items-start gap-2.5 rounded-[var(--radius-md)] border px-2.5 py-2 hover:bg-[var(--c-surface-hover)]"
          :style="
            draftRoles.includes(role.code)
              ? { borderColor: 'var(--c-accent)', backgroundColor: 'var(--c-accent-subtle)' }
              : undefined
          "
        >
          <input
            type="checkbox"
            class="mt-0.5"
            :autofocus="index === 0 || undefined"
            :checked="draftRoles.includes(role.code)"
            @change="toggleRole(role.code)"
          />
          <span class="min-w-0">
            <span class="block text-sm font-medium">{{ role.displayName }}</span>
            <span class="block text-xs text-[var(--c-text-muted)]">
              {{ role.description }}
            </span>
          </span>
        </label>
      </div>

      <p
        v-if="roleError"
        class="mt-3 rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
        :style="{
          borderColor: 'var(--c-danger)',
          backgroundColor: 'var(--c-danger-subtle)',
          color: 'var(--c-danger-text)',
        }"
        role="alert"
      >
        {{ roleError }}
      </p>

      <template #actions>
        <Button :disabled="setRoles.isPending.value" @click="editingRoles = false">Cancel</Button>
        <Button variant="primary" :pending="setRoles.isPending.value" @click="saveRoles">
          Save roles
        </Button>
      </template>
    </Modal>

    <Modal
      :open="resetting"
      title="Reset password"
      description="Signs this user out everywhere and requires a change at their next sign-in."
      size="sm"
      :busy="resetPassword.isPending.value"
      @close="resetting = false"
      @submit="submitReset"
    >
      <TextField
        v-model="newPassword"
        label="New password"
        type="password"
        autofocus
        autocomplete="new-password"
        required
        hint="At least 12 characters. Give it to them directly, not by message."
        :error="resetError"
      />

      <template #actions>
        <Button :disabled="resetPassword.isPending.value" @click="resetting = false">Cancel</Button>
        <Button
          type="submit"
          variant="primary"
          :pending="resetPassword.isPending.value"
          :disabled="newPassword.length < 12"
        >
          Reset password
        </Button>
      </template>
    </Modal>

    <Modal
      :open="deactivating"
      :title="`Deactivate ${user.data.value?.displayName ?? ''}?`"
      description="They are signed out immediately and cannot sign in again until reactivated."
      size="sm"
      :busy="setActive.isPending.value"
      @close="deactivating = false"
    >
      <!-- Focus starts on the reason. Enter deliberately does not deactivate: the dialog has no
           submit listener, so a destructive action needs its button. -->
      <TextField
        v-model="reason"
        label="Reason"
        optional-note="shown on this page and in the audit log"
        placeholder="Left the company"
        autofocus
      />

      <template #actions>
        <Button :disabled="setActive.isPending.value" @click="deactivating = false">Cancel</Button>
        <Button variant="danger" :pending="setActive.isPending.value" @click="submitDeactivate">
          Deactivate
        </Button>
      </template>
    </Modal>
  </div>
</template>
