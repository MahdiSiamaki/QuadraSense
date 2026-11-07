<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import AuthShell from './AuthShell.vue'
import FormMessage from './FormMessage.vue'
import SecondStepForm from './SecondStepForm.vue'
import { isSecondStep, useLogin, useSignInOptions, type LoginFailure } from '@/api/auth'
import { ApiError } from '@/api/client'
import { formatRelative } from '@/lib/format'

/**
 * Sign in.
 *
 * "Remember me" asks for a longer session, and the server keeps the promise: the page takes the
 * number of days from it rather than stating one of its own. "Forgot password" appears only when
 * this deployment can send a code; where it cannot, the page says who can help, because a link
 * that goes nowhere is worse than none. See ADR-022.
 *
 * The second step replaces the form in place rather than being a route: there is nothing to link
 * to, and the ticket must not outlive this page.
 */
const router = useRouter()
const route = useRoute()

const username = ref('')
const password = ref('')
const remember = ref(false)
const ticket = ref<string | null>(null)

const login = useLogin()
const options = useSignInOptions()

const canRecover = computed(
  () => options.data.value?.recoverByEmail === true || options.data.value?.recoverBySms === true,
)

const rememberDays = computed(() => options.data.value?.rememberDays ?? 7)

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

const form = ref<HTMLFormElement | null>(null)

/** Back to wherever they were headed, so an expiry does not also lose the page someone had open. */
async function enter() {
  const next = typeof route.query['next'] === 'string' ? route.query['next'] : '/'
  await router.replace(next.startsWith('/') ? next : '/')
}

function focusPassword() {
  form.value?.querySelector<HTMLInputElement>('input[autocomplete="current-password"]')?.focus()
}

async function submit() {
  if (!username.value.trim() || !password.value) return

  try {
    const result = await login.mutateAsync({
      username: username.value.trim(),
      password: password.value,
      rememberMe: remember.value,
    })
    password.value = ''

    if (isSecondStep(result)) {
      ticket.value = result.ticket
      return
    }

    await enter()
  } catch {
    // Rendered from login.error; a rejected mutation is not an unhandled failure here.
    password.value = ''
    // Back into the field that was cleared. The fields were disabled during the request, and a
    // focused element that becomes disabled hands focus to the page body: the next keystroke went
    // nowhere. Once they are enabled again, the password field takes it.
    await nextTick()
    focusPassword()
  }
}

async function startOver(message: string | null) {
  ticket.value = null
  restarted.value = message
  login.reset()
  await nextTick()
  focusPassword()
}

/** Why the second step sent them back here. */
const restarted = ref<string | null>(null)

function recover() {
  // In history state rather than the address: a username does not belong in a URL or a proxy log.
  void router.push({ name: 'recover', state: { identifier: username.value.trim() } })
}
</script>

<template>
  <AuthShell>
    <SecondStepForm
      v-if="ticket"
      :ticket="ticket"
      @done="enter"
      @restart="startOver"
    />

    <template v-else>
      <header>
        <h1 class="text-2xl font-semibold tracking-tight">Welcome back</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
          Sign in to Quadrasense Primary Inventory.
        </p>
      </header>

      <form ref="form" class="flex flex-col gap-3.5" @submit.prevent="submit">
        <TextField
          v-model="username"
          label="Username"
          autocomplete="username"
          required
          autofocus
          size="lg"
          :disabled="login.isPending.value"
        />
        <TextField
          v-model="password"
          label="Password"
          type="password"
          revealable
          autocomplete="current-password"
          required
          size="lg"
          :disabled="login.isPending.value"
        />

        <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
          <label
            class="flex cursor-pointer items-center gap-2 text-xs text-[var(--c-text-secondary)]"
            :title="`Stay signed in on this browser for ${rememberDays} days`"
          >
            <input
              v-model="remember"
              type="checkbox"
              class="size-3.5 accent-[var(--c-accent)]"
              :disabled="login.isPending.value"
            />
            Remember me for {{ rememberDays }} days
          </label>

          <button
            v-if="canRecover"
            type="button"
            class="rounded-[var(--radius-sm)] text-xs font-medium text-[var(--c-accent)] hover:underline"
            @click="recover"
          >
            Forgot password?
          </button>
        </div>

        <!--
          One message, in the place the eye already is. Colour is not the only signal: the
          text says what happened, and a locked account says when it frees up.
        -->
        <FormMessage v-if="failure" tone="danger">
          {{ failure.message }}
          <template v-if="lockedUntil"> Unlocks {{ lockedUntil }}.</template>
        </FormMessage>

        <FormMessage v-else-if="unexpected" tone="warning">{{ unexpected }}</FormMessage>

        <FormMessage v-else-if="restarted" tone="warning">{{ restarted }}</FormMessage>

        <Button
          type="submit"
          variant="primary"
          size="lg"
          block
          :pending="login.isPending.value"
          :disabled="!username.trim() || !password"
        >
          Sign in
        </Button>
      </form>

      <p class="text-center text-xs text-[var(--c-text-muted)]">
        <template v-if="canRecover">
          Need an account? Contact your administrator.
        </template>
        <template v-else>
          Contact your administrator for access, or to reset a password.
        </template>
      </p>
    </template>
  </AuthShell>
</template>
