<script setup lang="ts">
import { useRouter } from 'vue-router'
import ChangePasswordForm from './ChangePasswordForm.vue'
import Button from '@/design-system/Button.vue'
import { useAuth } from './useAuth'
import { useLogout } from '@/api/auth'

/**
 * The screen a user sees when their password must be changed before anything else.
 *
 * Full-screen with no navigation, because the API refuses every other endpoint until the change
 * is made: showing the app shell around this would produce a menu of links that all fail. The
 * only way out other than changing the password is signing out, and that is offered plainly
 * rather than left to be discovered.
 */
const router = useRouter()
const { user } = useAuth()
const logout = useLogout()

async function onChanged() {
  await router.replace('/')
}

async function signOut() {
  await logout.mutateAsync().catch(() => undefined)
  await router.replace('/login')
}
</script>

<template>
  <div class="flex min-h-screen flex-col items-center justify-center px-5 py-12">
    <div class="w-full max-w-sm">
      <div class="mb-7">
        <h1 class="text-lg font-semibold tracking-tight">Choose a new password</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
          <template v-if="user">Signed in as {{ user.displayName }}.</template>
          Your account was set up with a temporary password, or an administrator has reset it.
        </p>
      </div>

      <section
        class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-5 shadow-[var(--shadow-sm)]"
      >
        <ChangePasswordForm @changed="onChanged" />
      </section>

      <div class="mt-4 text-center">
        <Button variant="ghost" size="sm" :pending="logout.isPending.value" @click="signOut">
          Sign out instead
        </Button>
      </div>
    </div>
  </div>
</template>
