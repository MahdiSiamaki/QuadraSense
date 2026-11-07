<script setup lang="ts">
import { useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import TwoStepSetup from './TwoStepSetup.vue'
import { useAuth } from './useAuth'
import { useLogout } from '@/api/auth'

/**
 * When a role demands two-step sign-in and it is not set up yet.
 *
 * The same shape as the forced password change, and for the same reason: the API refuses everything
 * else until this is done, so the application around it would be a menu of links that all fail.
 */
const router = useRouter()
const { user } = useAuth()
const logout = useLogout()

async function finished() {
  await router.replace('/')
}

async function signOut() {
  await logout.mutateAsync().catch(() => undefined)
  await router.replace('/login')
}
</script>

<template>
  <div class="flex min-h-dvh flex-col items-center justify-center px-5 py-12">
    <div class="w-full max-w-lg">
      <div class="mb-7">
        <h1 class="text-lg font-semibold tracking-tight">Set up two-step sign-in</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
          <template v-if="user">Signed in as {{ user.displayName }}.</template>
          A role you hold requires it before you can continue.
        </p>
      </div>

      <section
        class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-5 shadow-[var(--shadow-sm)]"
      >
        <TwoStepSetup @finished="finished" />
      </section>

      <div class="mt-4 text-center">
        <Button variant="ghost" size="sm" :pending="logout.isPending.value" @click="signOut">
          Sign out instead
        </Button>
      </div>
    </div>
  </div>
</template>
