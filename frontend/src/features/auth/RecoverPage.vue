<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { RouterLink } from 'vue-router'
import Button from '@/design-system/Button.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import TextField from '@/design-system/TextField.vue'
import AuthShell from './AuthShell.vue'
import FormMessage from './FormMessage.vue'
import {
  useCompleteRecovery,
  useSignInOptions,
  useStartRecovery,
  type ContactChannelName,
} from '@/api/auth'
import { ApiError } from '@/api/client'

/**
 * Getting back into an account.
 *
 * The wording after "Send code" is careful, and the reason is the whole design (ADR-022): the
 * server answers the same whether or not the account exists, so this page cannot know whether a
 * code went anywhere. It says what is true for everyone, and offers no hint such as a masked
 * address, which would say the account exists.
 *
 * The rules shown for a new password are those of the change-password form.
 */
const MINIMUM_LENGTH = 12
const RESEND_SECONDS = 60

const options = useSignInOptions()
const start = useStartRecovery()
const complete = useCompleteRecovery()

const stage = ref<'identify' | 'code' | 'done'>('identify')

/** From the sign-in page, through history state: a username does not belong in the address. */
const remembered = (history.state as { identifier?: unknown } | null)?.identifier
const identifier = ref(typeof remembered === 'string' ? remembered : '')

const code = ref('')
const password = ref('')
const confirm = ref('')

const channels = computed(() => {
  const available: Array<{ value: ContactChannelName; label: string }> = []
  if (options.data.value?.recoverByEmail) available.push({ value: 'Email', label: 'Email' })
  if (options.data.value?.recoverBySms) available.push({ value: 'Sms', label: 'Text message' })
  return available
})

const chosen = ref<ContactChannelName | null>(null)
const channel = computed(() => chosen.value ?? channels.value[0]?.value ?? 'Email')

const minutes = computed(() => options.data.value?.codeMinutes ?? 10)

const sendFailure = computed(() => describe(start.error.value))

const fieldErrors = computed(() => {
  const error = complete.error.value
  if (!(error instanceof ApiError)) return {} as Record<string, string>
  return Object.fromEntries(error.fieldErrors.map((f) => [f.field, f.message]))
})

const completeFailure = computed(() => {
  const error = complete.error.value
  return error instanceof ApiError && error.status === 400 ? null : describe(error)
})

function describe(error: unknown): string | null {
  if (!error) return null
  if (error instanceof ApiError && error.status === 429) {
    return 'Too many attempts from this address. Wait a minute and try again.'
  }
  if (error instanceof ApiError) return error.problem?.title ?? 'Something went wrong.'
  return 'Could not reach the server. Check your connection and try again.'
}

const mismatch = computed(() => confirm.value.length > 0 && password.value !== confirm.value)
const tooShort = computed(() => password.value.length > 0 && password.value.length < MINIMUM_LENGTH)

const canComplete = computed(
  () =>
    code.value.replace(/\s/g, '').length === 6 &&
    password.value.length >= MINIMUM_LENGTH &&
    password.value === confirm.value,
)

// ------------------------------------------------------------------ asking again

const wait = ref(0)
let timer: ReturnType<typeof setInterval> | null = null

function startWaiting() {
  wait.value = RESEND_SECONDS
  if (timer) clearInterval(timer)
  timer = setInterval(() => {
    wait.value -= 1
    if (wait.value <= 0 && timer) {
      clearInterval(timer)
      timer = null
    }
  }, 1000)
}

onBeforeUnmount(() => {
  if (timer) clearInterval(timer)
})

async function send() {
  if (!identifier.value.trim()) return

  try {
    await start.mutateAsync({ identifier: identifier.value.trim(), channel: channel.value })
  } catch {
    return
  }

  startWaiting()

  if (stage.value === 'identify') stage.value = 'code'
}

async function submit() {
  if (!canComplete.value) return

  try {
    await complete.mutateAsync({
      identifier: identifier.value.trim(),
      code: code.value,
      newPassword: password.value,
    })
  } catch {
    return
  }

  code.value = ''
  password.value = ''
  confirm.value = ''
  stage.value = 'done'
}
</script>

<template>
  <AuthShell subtitle="Account recovery">
    <!-- ================================================================== nothing to send with -->
    <template v-if="options.isSuccess.value && channels.length === 0">
      <header>
        <h1 class="text-2xl font-semibold tracking-tight">Forgot your password?</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
          This system cannot send recovery codes. An administrator can reset your password from the
          Users page.
        </p>
      </header>
      <RouterLink
        :to="{ name: 'login' }"
        class="text-sm font-medium text-[var(--c-accent)] hover:underline"
      >
        Back to sign in
      </RouterLink>
    </template>

    <!-- ================================================================== who -->
    <template v-else-if="stage === 'identify'">
      <header>
        <h1 class="text-2xl font-semibold tracking-tight">Forgot your password?</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
          We will send a code to the email address or phone number you verified on your profile.
        </p>
      </header>

      <form class="flex flex-col gap-3.5" @submit.prevent="send">
        <TextField
          v-model="identifier"
          label="Username, email or phone"
          autocomplete="username"
          required
          autofocus
          size="lg"
          :disabled="start.isPending.value"
        />

        <div v-if="channels.length > 1" class="flex flex-col gap-1">
          <span class="text-xs font-medium text-[var(--c-text-secondary)]">Send the code by</span>
          <SegmentedControl
            :model-value="channel"
            :options="channels"
            label="Send the code by"
            @update:model-value="chosen = $event"
          />
        </div>

        <FormMessage v-if="sendFailure" tone="warning">{{ sendFailure }}</FormMessage>

        <Button
          type="submit"
          variant="primary"
          size="lg"
          block
          :pending="start.isPending.value"
          :disabled="!identifier.trim() || !options.isSuccess.value"
        >
          Send code
        </Button>
      </form>

      <RouterLink
        :to="{ name: 'login' }"
        class="self-center text-xs text-[var(--c-text-muted)] hover:underline"
      >
        Back to sign in
      </RouterLink>
    </template>

    <!-- ================================================================== the code -->
    <template v-else-if="stage === 'code'">
      <header>
        <h1 class="text-2xl font-semibold tracking-tight">Enter your code</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]" role="status">
          If that account has a verified {{ channel === 'Email' ? 'email address' : 'phone number' }},
          a code is on its way. It works for {{ minutes }} minutes.
        </p>
      </header>

      <form class="flex flex-col gap-3.5" @submit.prevent="submit">
        <TextField
          v-model="code"
          label="Code"
          autocomplete="one-time-code"
          inputmode="numeric"
          :maxlength="7"
          code
          autofocus
          size="lg"
          required
          :error="fieldErrors['code'] ?? null"
          :disabled="complete.isPending.value"
        />
        <TextField
          v-model="password"
          label="New password"
          type="password"
          revealable
          autocomplete="new-password"
          required
          size="lg"
          :error="fieldErrors['newPassword'] ?? (tooShort ? `At least ${MINIMUM_LENGTH} characters.` : null)"
          :hint="`At least ${MINIMUM_LENGTH} characters. A few unrelated words work well.`"
          :disabled="complete.isPending.value"
        />
        <TextField
          v-model="confirm"
          label="Repeat the new password"
          type="password"
          revealable
          autocomplete="new-password"
          required
          size="lg"
          :error="mismatch ? 'The two passwords differ.' : null"
          :disabled="complete.isPending.value"
        />

        <FormMessage v-if="completeFailure" tone="warning">{{ completeFailure }}</FormMessage>

        <Button
          type="submit"
          variant="primary"
          size="lg"
          block
          :pending="complete.isPending.value"
          :disabled="!canComplete"
        >
          Set new password
        </Button>
      </form>

      <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 text-xs">
        <button
          type="button"
          class="rounded-[var(--radius-sm)] font-medium text-[var(--c-accent)] hover:underline disabled:cursor-not-allowed disabled:text-[var(--c-text-muted)] disabled:no-underline"
          :disabled="wait > 0 || start.isPending.value"
          @click="send"
        >
          {{ wait > 0 ? `Send another code in ${wait}s` : 'Send another code' }}
        </button>
        <RouterLink :to="{ name: 'login' }" class="text-[var(--c-text-muted)] hover:underline">
          Back to sign in
        </RouterLink>
      </div>
    </template>

    <!-- ================================================================== done -->
    <template v-else>
      <header>
        <h1 class="text-2xl font-semibold tracking-tight">Password changed</h1>
        <p class="mt-1 text-sm text-[var(--c-text-secondary)]" role="status">
          Every device has been signed out. Sign in with your new password.
        </p>
      </header>
      <RouterLink
        :to="{ name: 'login' }"
        class="inline-flex items-center justify-center rounded-[var(--radius-md)] bg-[var(--c-accent)] px-4 py-2 text-sm font-medium text-[var(--c-accent-text)] hover:bg-[var(--c-accent-hover)]"
      >
        Go to sign in
      </RouterLink>
    </template>
  </AuthShell>
</template>
