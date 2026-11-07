<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import FormMessage from '@/features/auth/FormMessage.vue'
import RecoveryCodesList from '@/features/auth/RecoveryCodesList.vue'
import TwoStepSetup from '@/features/auth/TwoStepSetup.vue'
import { useDisableTwoStep, useNewRecoveryCodes, useTwoStepStatus } from '@/api/auth'
import { ApiError } from '@/api/client'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * Two-step sign-in, on the person's own profile.
 *
 * Absent where it cannot be offered, rather than a disabled button nobody can act on: without a key
 * in the server's configuration there is nothing for a person to do about it.
 */
const status = useTwoStepStatus()
const newCodes = useNewRecoveryCodes()
const disable = useDisableTwoStep()

const dialog = ref<'setup' | 'codes' | 'disable' | null>(null)

const password = ref('')
const code = ref('')
const shownCodes = ref<string[]>([])

const enabled = computed(() => status.data.value?.enabled === true)
const required = computed(() => status.data.value?.required === true)
const left = computed(() => status.data.value?.recoveryCodesLeft ?? 0)

function open(which: 'setup' | 'codes' | 'disable') {
  password.value = ''
  code.value = ''
  shownCodes.value = []
  newCodes.reset()
  disable.reset()
  dialog.value = which
}

function close() {
  dialog.value = null
  void status.refetch()
}

const passwordError = computed(() => field(newCodes.error.value, 'currentPassword'))
const disableError = computed(() => field(disable.error.value, 'code'))

const failure = computed(() => {
  const error = dialog.value === 'codes' ? newCodes.error.value : disable.error.value
  if (!error) return null
  if (error instanceof ApiError && error.fieldErrors.length > 0) return null
  if (error instanceof ApiError) return error.problem?.detail ?? 'Something went wrong.'
  return 'Could not reach the server. Check your connection and try again.'
})

function field(error: unknown, name: string): string | null {
  if (!(error instanceof ApiError)) return null
  return error.fieldErrors.find((f) => f.field === name)?.message ?? null
}

async function regenerate() {
  if (!password.value) return

  try {
    const result = await newCodes.mutateAsync({ currentPassword: password.value })
    shownCodes.value = result.codes
    password.value = ''
  } catch {
    password.value = ''
  }
}

async function turnOff() {
  if (!password.value || !code.value.trim()) return

  try {
    await disable.mutateAsync({ currentPassword: password.value, code: code.value })
    close()
  } catch {
    code.value = ''
  }
}
</script>

<template>
  <Card
    v-if="status.data.value?.available || enabled"
    title="Two-step sign-in"
    :subtitle="enabled ? 'On' : 'Off'"
  >
    <template v-if="enabled">
      <p class="text-xs text-[var(--c-text-secondary)]">
        <template v-if="status.data.value?.enabledAt">
          On since
          <span :title="formatDateTime(status.data.value.enabledAt)">
            {{ formatRelative(status.data.value.enabledAt) }}</span>.
        </template>
        {{ left }} recovery {{ left === 1 ? 'code' : 'codes' }} left.
      </p>

      <FormMessage v-if="left <= 2" tone="warning" class="mt-3">
        You are running out of recovery codes. Make new ones before you need them.
      </FormMessage>

      <div class="mt-3 flex flex-wrap gap-2">
        <Button size="sm" @click="open('codes')">New recovery codes</Button>
        <Button v-if="!required" size="sm" variant="danger" @click="open('disable')">Turn off</Button>
      </div>

      <p v-if="required" class="mt-3 text-xs text-[var(--c-text-muted)]">
        A role you hold requires it, so it cannot be turned off here.
      </p>
    </template>

    <template v-else>
      <p class="text-xs text-[var(--c-text-secondary)]">
        Ask for a code from an authenticator app as well as your password, so that a stolen password
        is not enough.
      </p>
      <div class="mt-3">
        <Button size="sm" variant="primary" @click="open('setup')">Turn on</Button>
      </div>
    </template>
  </Card>

  <Modal :open="dialog === 'setup'" title="Set up two-step sign-in" @close="close">
    <TwoStepSetup v-if="dialog === 'setup'" @finished="close" />
  </Modal>

  <Modal
    :open="dialog === 'codes'"
    title="New recovery codes"
    description="These replace every code you have now."
    :busy="newCodes.isPending.value"
    @close="close"
    @submit="regenerate"
  >
    <RecoveryCodesList v-if="shownCodes.length > 0" :codes="shownCodes" />

    <div v-else class="flex flex-col gap-3.5">
      <TextField
        v-model="password"
        label="Your password"
        type="password"
        revealable
        autocomplete="current-password"
        autofocus
        :error="passwordError"
        :disabled="newCodes.isPending.value"
      />
      <FormMessage v-if="failure" tone="warning">{{ failure }}</FormMessage>
    </div>

    <template #actions>
      <Button v-if="shownCodes.length > 0" variant="primary" @click="close">Done</Button>
      <template v-else>
        <Button :disabled="newCodes.isPending.value" @click="close">Cancel</Button>
        <Button
          type="submit"
          variant="primary"
          :pending="newCodes.isPending.value"
          :disabled="!password"
        >
          Make new codes
        </Button>
      </template>
    </template>
  </Modal>

  <Modal
    :open="dialog === 'disable'"
    title="Turn off two-step sign-in"
    description="A password alone will then sign you in. Your recovery codes stop working."
    :busy="disable.isPending.value"
    @close="close"
    @submit="turnOff"
  >
    <div class="flex flex-col gap-3.5">
      <TextField
        v-model="password"
        label="Your password"
        type="password"
        revealable
        autocomplete="current-password"
        autofocus
        :disabled="disable.isPending.value"
      />
      <TextField
        v-model="code"
        label="Code from your authenticator, or a recovery code"
        autocomplete="one-time-code"
        :error="disableError"
        :disabled="disable.isPending.value"
      />
      <FormMessage v-if="failure" tone="warning">{{ failure }}</FormMessage>
    </div>

    <template #actions>
      <Button :disabled="disable.isPending.value" @click="close">Cancel</Button>
      <Button
        type="submit"
        variant="danger"
        :pending="disable.isPending.value"
        :disabled="!password || !code.trim()"
      >
        Turn off
      </Button>
    </template>
  </Modal>
</template>
