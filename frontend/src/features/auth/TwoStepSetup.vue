<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import FormMessage from './FormMessage.vue'
import QrCode from './QrCode.vue'
import RecoveryCodesList from './RecoveryCodesList.vue'
import { useBeginTwoStep, useConfirmTwoStep, type TwoStepSetupResponse } from '@/api/auth'
import { ApiError } from '@/api/client'

/**
 * Setting up an authenticator: the password, then a code to prove the app has the secret, then the
 * recovery codes.
 *
 * Used in a dialog on the profile and as a whole page when a role demands it, so it owns no chrome.
 * Nothing changes on the server until the first good code, so walking away at any point leaves the
 * account as it was.
 */
const emit = defineEmits<{ finished: [] }>()

const stage = ref<'password' | 'scan' | 'codes'>('password')
const password = ref('')
const code = ref('')
const setup = ref<TwoStepSetupResponse | null>(null)
const recoveryCodes = ref<string[]>([])
const saved = ref(false)

const begin = useBeginTwoStep()
const confirm = useConfirmTwoStep()

const beginError = computed(() => fieldError(begin.error.value, 'currentPassword'))
const beginFailure = computed(() => problem(begin.error.value))
const confirmError = computed(() => fieldError(confirm.error.value, 'code'))
const confirmFailure = computed(() => problem(confirm.error.value))

function fieldError(error: unknown, field: string): string | null {
  if (!(error instanceof ApiError)) return null
  return error.fieldErrors.find((f) => f.field === field)?.message ?? null
}

function problem(error: unknown): string | null {
  if (!error) return null
  if (error instanceof ApiError && error.fieldErrors.length > 0) return null
  if (error instanceof ApiError && error.status === 429) {
    return 'Too many attempts. Wait a minute and try again.'
  }
  if (error instanceof ApiError) return error.problem?.detail ?? 'Something went wrong.'
  return 'Could not reach the server. Check your connection and try again.'
}

async function submitPassword() {
  if (!password.value) return

  try {
    setup.value = await begin.mutateAsync({ currentPassword: password.value })
    password.value = ''
    stage.value = 'scan'
  } catch {
    password.value = ''
  }
}

async function submitCode() {
  if (code.value.replace(/\s/g, '').length !== 6) return

  try {
    const result = await confirm.mutateAsync({ code: code.value })
    recoveryCodes.value = result.codes
    stage.value = 'codes'
  } catch {
    code.value = ''
  }
}
</script>

<template>
  <form v-if="stage === 'password'" class="flex flex-col gap-3.5" @submit.prevent="submitPassword">
    <p class="text-sm text-[var(--c-text-secondary)]">
      Two-step sign-in asks for a code from an app on your phone or computer each time you sign in,
      so a stolen password is not enough. Any authenticator app works.
    </p>
    <TextField
      v-model="password"
      label="Your password"
      type="password"
      revealable
      autocomplete="current-password"
      autofocus
      :error="beginError"
      :disabled="begin.isPending.value"
    />
    <FormMessage v-if="beginFailure" tone="warning">{{ beginFailure }}</FormMessage>
    <div class="flex justify-end">
      <Button type="submit" variant="primary" :pending="begin.isPending.value" :disabled="!password">
        Continue
      </Button>
    </div>
  </form>

  <form v-else-if="stage === 'scan'" class="flex flex-col gap-3.5" @submit.prevent="submitCode">
    <div class="flex flex-col items-center gap-4 sm:flex-row sm:items-start">
      <QrCode v-if="setup" :value="setup.uri" />
      <div class="flex min-w-0 flex-col gap-2 text-sm text-[var(--c-text-secondary)]">
        <p>Scan this with your authenticator app.</p>
        <p class="text-xs">Cannot scan it? Add an account by hand with this key:</p>
        <code
          class="rounded-[var(--radius-sm)] border bg-[var(--c-surface-sunken)] px-2 py-1 font-mono text-xs break-all text-[var(--c-text)]"
        >{{ setup?.secret }}</code>
      </div>
    </div>

    <TextField
      v-model="code"
      label="Then enter the code it shows"
      autocomplete="one-time-code"
      inputmode="numeric"
      :maxlength="7"
      code
      autofocus
      :error="confirmError"
      :disabled="confirm.isPending.value"
    />
    <FormMessage v-if="confirmFailure" tone="warning">{{ confirmFailure }}</FormMessage>
    <div class="flex justify-end">
      <Button
        type="submit"
        variant="primary"
        :pending="confirm.isPending.value"
        :disabled="code.replace(/\s/g, '').length !== 6"
      >
        Turn on
      </Button>
    </div>
  </form>

  <div v-else class="flex flex-col gap-3.5">
    <FormMessage tone="success">
      Two-step sign-in is on. Every other device has been signed out.
    </FormMessage>
    <p class="text-sm text-[var(--c-text-secondary)]">
      Save these recovery codes somewhere safe. If you lose your phone, each one signs you in once.
      They will not be shown again.
    </p>
    <RecoveryCodesList :codes="recoveryCodes" />
    <label class="flex cursor-pointer items-center gap-2 text-xs text-[var(--c-text-secondary)]">
      <input v-model="saved" type="checkbox" class="size-3.5 accent-[var(--c-accent)]" />
      I have saved my recovery codes
    </label>
    <div class="flex justify-end">
      <Button variant="primary" :disabled="!saved" @click="emit('finished')">Done</Button>
    </div>
  </div>
</template>
