<script setup lang="ts">
import BrandMark from '@/design-system/BrandMark.vue'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import { useLogin, type LoginFailure } from '@/api/auth'
import { ApiError } from '@/api/client'
import { formatRelative } from '@/lib/format'

/**
 * Sign in.
 *
 * Two decisions worth stating, because both look like omissions.
 *
 * There is no "remember me". The session cookie has no Expires attribute, so it dies with the
 * browser and the server enforces an 8-hour idle and 24-hour absolute limit regardless. A
 * checkbox offering to extend that would be offering something the server will not honour.
 *
 * There is no "forgot password" link. Local accounts have no email delivery behind them, so a
 * reset link would go nowhere; an administrator resets a password from the Users page and the
 * message below says so. A link that silently does nothing is worse than its absence.
 */
const router = useRouter()
const route = useRoute()

const username = ref('')
const password = ref('')
const login = useLogin()

const failure = computed<LoginFailure | null>(() => {
  const error = login.error.value
  if (!(error instanceof ApiError) || error.status !== 401) return null
  return error.problem as unknown as LoginFailure | null
})

/** A failure that is not a refused sign-in: the API is down, or the network is. */
const unexpected = computed(() => {
  const error = login.error.value
  if (!error) return null
  if (error instanceof ApiError && error.status === 401) return null
  if (error instanceof ApiError && error.status === 429) {
    return 'Too many attempts from this address. Wait a minute and try again.'
  }
  if (error instanceof ApiError) {
    return error.problem?.title ?? `The server responded with ${error.status}.`
  }
  return 'Could not reach the server. Check your connection and try again.'
})

const lockedUntil = computed(() =>
  failure.value?.status === 'AccountLocked' && failure.value.lockedUntil
    ? formatRelative(failure.value.lockedUntil)
    : null,
)

async function submit() {
  if (!username.value.trim() || !password.value) return

  try {
    await login.mutateAsync({ username: username.value.trim(), password: password.value })
    password.value = ''

    // Back to wherever they were headed before the session ran out, so an expiry does not also
    // lose the page someone had open.
    const next = typeof route.query['next'] === 'string' ? route.query['next'] : '/'
    await router.replace(next.startsWith('/') ? next : '/')
  } catch {
    // Rendered from login.error; a rejected mutation is not an unhandled failure here.
    password.value = ''
  }
}
</script>

<template>
  <!--
    A single centred column on the canvas rather than a marketing split-screen. This is an
    internal tool: the person signing in has already decided to be here, and a hero image would
    only push the form below the fold on a laptop.
  -->
  <div class="flex min-h-screen flex-col items-center justify-center px-5 py-12">
    <div class="w-full max-w-sm">
      <div class="mb-7 flex items-center gap-2.5">
        <BrandMark :size="34" />
        <div>
          <h1 class="text-base font-semibold tracking-tight">QuadraSense</h1>
          <p class="text-xs text-[var(--c-text-muted)]">
            Primary SIM &amp; Device Inventory
          </p>
        </div>
      </div>

      <section
        class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-5 shadow-[var(--shadow-sm)]"
      >
        <h2 class="text-sm font-semibold">Sign in</h2>
        <p class="mt-0.5 mb-4 text-xs text-[var(--c-text-muted)]">
          Use the account your administrator created for you.
        </p>

        <form class="flex flex-col gap-3.5" @submit.prevent="submit">
          <TextField
            v-model="username"
            label="Username"
            autocomplete="username"
            required
            :disabled="login.isPending.value"
          />
          <TextField
            v-model="password"
            label="Password"
            type="password"
            autocomplete="current-password"
            required
            :disabled="login.isPending.value"
          />

          <!--
            One message, in the place the eye already is. Colour is not the only signal: the
            text says what happened, and a locked account says when it frees up.
          -->
          <p
            v-if="failure"
            class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
            :style="{
              borderColor: 'var(--c-danger)',
              backgroundColor: 'var(--c-danger-subtle)',
              color: 'var(--c-danger)',
            }"
            role="alert"
          >
            {{ failure.message }}
            <template v-if="lockedUntil"> Unlocks {{ lockedUntil }}.</template>
          </p>

          <p
            v-else-if="unexpected"
            class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
            :style="{
              borderColor: 'var(--c-warning)',
              backgroundColor: 'var(--c-warning-subtle)',
              color: 'var(--c-warning)',
            }"
            role="alert"
          >
            {{ unexpected }}
          </p>

          <Button
            type="submit"
            variant="primary"
            block
            :pending="login.isPending.value"
            :disabled="!username.trim() || !password"
          >
            Sign in
          </Button>
        </form>
      </section>

      <p class="mt-4 text-center text-xs text-[var(--c-text-muted)]">
        Forgotten your password? An administrator can reset it for you.
      </p>
    </div>
  </div>
</template>
