<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import ChangePasswordForm from '@/features/auth/ChangePasswordForm.vue'
import SessionList from './SessionList.vue'
import { useAuth } from '@/features/auth/useAuth'
import { useMySessions, useRevokeMySession, useUpdateProfile } from '@/api/auth'
import { ApiError } from '@/api/client'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * A user's own account.
 *
 * Three panels in the order they matter: who you are and what you may change, the security facts
 * you can act on, and your password.
 *
 * What is NOT editable here is as deliberate as what is. Username, roles and permissions are
 * absent because a person changing their own access is the definition of privilege escalation -
 * and the API enforces that independently: the profile endpoint accepts four fields and has no
 * path to any other.
 */
const { user, query } = useAuth()

const sessions = useMySessions()
const revoke = useRevokeMySession()
const save = useUpdateProfile()

const displayName = ref('')
const email = ref('')
const jobTitle = ref('')
const phone = ref('')

/**
 * Seeded from the server, and re-seeded whenever it changes.
 *
 * `immediate` matters: the user is usually already in the cache when this page mounts, so
 * waiting for a change would leave the form blank over data that is right there.
 */
watch(
  user,
  (value) => {
    if (!value) return
    displayName.value = value.displayName
    email.value = value.email ?? ''
    jobTitle.value = value.jobTitle ?? ''
    phone.value = value.phone ?? ''
  },
  { immediate: true },
)

const dirty = computed(
  () =>
    user.value !== null &&
    (displayName.value !== user.value.displayName ||
      email.value !== (user.value.email ?? '') ||
      jobTitle.value !== (user.value.jobTitle ?? '') ||
      phone.value !== (user.value.phone ?? '')),
)

const fieldErrors = computed(() => {
  const error = save.error.value
  if (!(error instanceof ApiError)) return {} as Record<string, string>
  return Object.fromEntries(error.fieldErrors.map((f) => [f.field, f.message]))
})

function submit() {
  if (!dirty.value) return
  save.mutate({
    displayName: displayName.value.trim(),
    email: email.value.trim() || null,
    jobTitle: jobTitle.value.trim() || null,
    phone: phone.value.trim() || null,
  })
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <header>
      <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Your profile</h1>
      <p class="mt-0.5 text-[var(--text-sm)] text-[var(--c-text-secondary)]">
        {{ user?.username }} · {{ user?.roles.join(', ') || 'no role assigned' }}
      </p>
    </header>

    <AsyncBoundary
      :is-loading="query.isPending.value"
      :is-error="query.isError.value"
      :error="query.error.value"
      min-height="12rem"
      @retry="query.refetch()"
    >
      <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <div class="flex flex-col gap-5">
          <Card title="Details" subtitle="Everything here is visible to administrators.">
            <form class="grid gap-3.5 sm:grid-cols-2" @submit.prevent="submit">
              <TextField
                v-model="displayName"
                label="Display name"
                required
                :error="fieldErrors['displayName'] ?? null"
                hint="How your name appears in the audit log."
              />
              <TextField
                v-model="jobTitle"
                label="Job title"
                optional-note="optional"
                :error="fieldErrors['jobTitle'] ?? null"
              />
              <TextField
                v-model="email"
                label="Email"
                type="email"
                optional-note="optional"
                :error="fieldErrors['email'] ?? null"
                hint="Not used for sign-in or password reset."
              />
              <TextField
                v-model="phone"
                label="Phone"
                optional-note="optional"
                :error="fieldErrors['phone'] ?? null"
              />

              <div class="flex items-center gap-3 sm:col-span-2">
                <Button type="submit" variant="primary" :disabled="!dirty" :pending="save.isPending.value">
                  Save changes
                </Button>
                <span
                  v-if="save.isSuccess.value && !dirty"
                  class="text-[var(--text-xs)] text-[var(--c-success)]"
                  role="status"
                >
                  Saved.
                </span>
              </div>
            </form>

            <!--
              Named explicitly rather than simply omitted. Someone looking for "change my role"
              should find out here that it is not theirs to change, not conclude the page is
              incomplete.
            -->
            <p class="mt-4 border-t pt-3 text-[var(--text-xs)] text-[var(--c-text-muted)]">
              Your username, roles and permissions can only be changed by an administrator.
            </p>
          </Card>

          <Card title="Password">
            <ChangePasswordForm />
          </Card>
        </div>

        <div class="flex flex-col gap-5">
          <Card title="Security">
            <!--
              "Last sign-in" is the PREVIOUS one, not the current session. Showing someone the
              session they are looking at tells them nothing; showing the one before it is how a
              person notices a sign-in that was not theirs.
            -->
            <dl class="flex flex-col gap-3">
              <div>
                <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">
                  Previous sign-in
                </dt>
                <dd class="tabular mt-0.5 text-[var(--text-sm)]">
                  <template v-if="user?.previousLoginAt">
                    <span :title="formatDateTime(user.previousLoginAt)">
                      {{ formatRelative(user.previousLoginAt) }}
                    </span>
                  </template>
                  <span v-else class="text-[var(--c-text-muted)]">
                    this is your first sign-in
                  </span>
                </dd>
              </div>

              <div>
                <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">This sign-in</dt>
                <dd class="tabular mt-0.5 text-[var(--text-sm)]">
                  {{ user?.lastLoginAt ? formatDateTime(user.lastLoginAt) : '—' }}
                  <span v-if="user?.lastLoginIp" class="text-[var(--c-text-muted)]">
                    from {{ user.lastLoginIp }}
                  </span>
                </dd>
              </div>

              <div>
                <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Password set</dt>
                <dd class="tabular mt-0.5 text-[var(--text-sm)]">
                  <span v-if="user?.passwordUpdatedAt" :title="formatDateTime(user.passwordUpdatedAt)">
                    {{ formatRelative(user.passwordUpdatedAt) }}
                  </span>
                  <span v-else class="text-[var(--c-text-muted)]">—</span>
                </dd>
              </div>

              <div>
                <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Session expires</dt>
                <dd class="tabular mt-0.5 text-[var(--text-sm)]">
                  <span v-if="user" :title="formatDateTime(user.sessionIdleExpiresAt)">
                    {{ formatRelative(user.sessionIdleExpiresAt) }} if idle
                  </span>
                </dd>
              </div>
            </dl>
          </Card>

          <Card
            title="Signed-in devices"
            :subtitle="`${sessions.data.value?.filter((s) => !s.revokedAt).length ?? 0} active`"
            flush
          >
            <SessionList
              :query="sessions"
              :revoking="revoke.isPending.value"
              @revoke="(id) => revoke.mutate(id)"
            />
          </Card>
        </div>
      </div>
    </AsyncBoundary>
  </div>
</template>
