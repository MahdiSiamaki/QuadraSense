<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import FormMessage from './FormMessage.vue'
import { useSecondStep, type LoginFailure } from '@/api/auth'
import { ApiError } from '@/api/client'
import { formatRelative } from '@/lib/format'

/**
 * The code that follows a good password.
 *
 * A recovery code is a different shape of thing, so it gets its own field rather than a hint that
 * one box takes both: six digits want a numeric keyboard on a phone and a sixteen-character code
 * does not, and the field's label says plainly which is being asked for. The server tells them
 * apart either way.
 */
const props = defineProps<{ ticket: string }>()

const emit = defineEmits<{
  done: []
  /** The ticket has gone. Back to the password, with why. */
  restart: [message: string | null]
}>()

const code = ref('')
const usingRecoveryCode = ref(false)
const form = ref<HTMLFormElement | null>(null)

const step = useSecondStep()

const failure = computed<LoginFailure | null>(() => {
  const error = step.error.value
  if (!(error instanceof ApiError) || error.status !== 401) return null
  return error.problem as unknown as LoginFailure | null
})

const unexpected = computed(() => {
  const error = step.error.value
  if (!error) return null
  if (error instanceof ApiError && error.status === 401) return null
  if (error instanceof ApiError && error.status === 429) {
    return 'Too many attempts from this address. Wait a minute and try again.'
  }
  return 'Could not reach the server. Check your connection and try again.'
})

const complete = computed(() =>
  usingRecoveryCode.value
    ? code.value.replace(/[\s-]/g, '').length === 16
    : code.value.replace(/\s/g, '').length === 6,
)

async function focusCode() {
  await nextTick()
  form.value?.querySelector<HTMLInputElement>('input')?.focus()
}

async function submit() {
  if (!complete.value) return

  try {
    await step.mutateAsync({ ticket: props.ticket, code: code.value })
    emit('done')
  } catch (error) {
    const refused = error instanceof ApiError ? (error.problem as unknown as LoginFailure) : null

    if (refused?.status === 'ChallengeExpired') {
      emit('restart', refused.message)
      return
    }

    if (refused?.status === 'AccountLocked') {
      const until = refused.lockedUntil ? ` Unlocks ${formatRelative(refused.lockedUntil)}.` : ''
      emit('restart', `${refused.message}${until}`)
      return
    }

    code.value = ''
    await focusCode()
  }
}

function switchKind() {
  usingRecoveryCode.value = !usingRecoveryCode.value
  code.value = ''
  step.reset()
  void focusCode()
}
</script>

<template>
  <header>
    <h1 class="text-2xl font-semibold tracking-tight">Two-step sign-in</h1>
    <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
      <template v-if="usingRecoveryCode">
        Enter one of the recovery codes you saved. Each works once.
      </template>
      <template v-else>Enter the six-digit code from your authenticator app.</template>
    </p>
  </header>

  <form ref="form" class="flex flex-col gap-3.5" @submit.prevent="submit">
    <TextField
      v-if="usingRecoveryCode"
      key="recovery"
      v-model="code"
      label="Recovery code"
      autocomplete="off"
      placeholder="XXXX-XXXX-XXXX-XXXX"
      autofocus
      size="lg"
      :disabled="step.isPending.value"
    />
    <TextField
      v-else
      key="code"
      v-model="code"
      label="Authentication code"
      autocomplete="one-time-code"
      inputmode="numeric"
      :maxlength="7"
      code
      autofocus
      size="lg"
      :disabled="step.isPending.value"
    />

    <FormMessage v-if="failure" tone="danger">{{ failure.message }}</FormMessage>
    <FormMessage v-else-if="unexpected" tone="warning">{{ unexpected }}</FormMessage>

    <Button
      type="submit"
      variant="primary"
      size="lg"
      block
      :pending="step.isPending.value"
      :disabled="!complete"
    >
      Verify
    </Button>
  </form>

  <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 text-xs">
    <button
      type="button"
      class="rounded-[var(--radius-sm)] font-medium text-[var(--c-accent)] hover:underline"
      @click="switchKind"
    >
      {{ usingRecoveryCode ? 'Use my authenticator app' : 'Use a recovery code' }}
    </button>
    <button
      type="button"
      class="rounded-[var(--radius-sm)] text-[var(--c-text-muted)] hover:underline"
      @click="emit('restart', null)"
    >
      Start over
    </button>
  </div>
</template>
