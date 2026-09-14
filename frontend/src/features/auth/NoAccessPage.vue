<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import { useAuth } from './useAuth'

/**
 * Shown when a signed-in user opens something they cannot reach.
 *
 * It names the permission. That looks like leaking internal detail and is the opposite: without
 * it the user has to describe the screen to an administrator from memory, and the administrator
 * has to guess which of seventeen permissions they mean. With it, the request becomes "please
 * grant me tac.activate", which is one click on the user's detail page.
 *
 * A silent redirect to the dashboard was the alternative. It makes every link someone lacks
 * access to look broken.
 */
const route = useRoute()
const router = useRouter()
const { user } = useAuth()

const permission = computed(() =>
  typeof route.query['permission'] === 'string' ? route.query['permission'] : null,
)
</script>

<template>
  <div class="mx-auto flex max-w-md flex-col items-start gap-4 py-16">
    <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">You do not have access</h1>

    <p class="text-[var(--text-sm)] text-[var(--c-text-secondary)]">
      Your account
      <template v-if="user">({{ user.username }})</template>
      cannot open that page.
      <template v-if="permission">
        It requires the
        <code
          class="rounded-[var(--radius-sm)] bg-[var(--c-surface-sunken)] px-1 py-0.5 text-[var(--text-xs)]"
          >{{ permission }}</code
        >
        permission.
      </template>
    </p>

    <p class="text-[var(--text-sm)] text-[var(--c-text-secondary)]">
      Ask an administrator to grant it. Quoting the permission name above will save them guessing
      which one you need.
    </p>

    <div v-if="user" class="mt-2 rounded-[var(--radius-md)] border p-3">
      <p class="text-[var(--text-xs)] text-[var(--c-text-muted)]">You currently hold</p>
      <p class="mt-1 text-[var(--text-sm)]">
        {{ user.roles.length ? user.roles.join(', ') : 'no role' }}
        <span class="text-[var(--c-text-muted)]">
          · {{ user.permissions.length }} permission(s)
        </span>
      </p>
    </div>

    <div class="mt-2 flex gap-2">
      <Button variant="primary" @click="router.push('/')">Go to the dashboard</Button>
      <Button @click="router.back()">Back</Button>
    </div>
  </div>
</template>
