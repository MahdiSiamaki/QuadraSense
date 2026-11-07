<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import FormMessage from '@/features/auth/FormMessage.vue'
import { useConfirmContact, useSendContactCode, type ContactRouteName } from '@/api/auth'
import { ApiError } from '@/api/client'

/**
 * Proving an e-mail address or a phone number: the password, then the code sent to it.
 *
 * The password comes first so that an open session cannot point recovery at someone else's phone.
 * See ADR-022, Decision 2.
 */
const props = defineProps<{
  channel: ContactRouteName | null
  /** The contact as it stands on the profile. */
  destination: string
}>()

const emit = defineEmits<{ close: [] }>()

const send = useSendContactCode()
const confirm = useConfirmContact()

const stage = ref<'password' | 'code'>('password')
const password = ref('')
const code = ref('')

watch(
  () => props.channel,
  () => {
    stage.value = 'password'
    password.value = ''
    code.value = ''
    send.reset()
    confirm.reset()
  },
)

const noun = computed(() => (props.channel === 'email' ? 'email address' : 'phone number'))
const busy = computed(() => send.isPending.value || confirm.isPending.value)

function field(error: unknown, name: string): string | null {
  if (!(error instanceof ApiError)) return null
  return error.fieldErrors.find((f) => f.field === name)?.message ?? null
}

const passwordError = computed(() => field(send.error.value, 'currentPassword'))
const codeError = computed(() => field(confirm.error.value, 'code'))

const failure = computed(() => {
  const error = stage.value === 'password' ? send.error.value : confirm.error.value
  if (!error) return null
  if (error instanceof ApiError && error.fieldErrors.length > 0) return null
  if (error instanceof ApiError && error.status === 429) {
    return 'Too many attempts. Wait a minute and try again.'
  }
  if (error instanceof ApiError) return error.problem?.detail ?? 'Something went wrong.'
  return 'Could not reach the server. Check your connection and try again.'
})

async function next() {
  if (!props.channel) return

  if (stage.value === 'password') {
    if (!password.value) return

    try {
      await send.mutateAsync({ channel: props.channel, currentPassword: password.value })
      password.value = ''
      stage.value = 'code'
    } catch {
      password.value = ''
    }

    return
  }

  if (code.value.replace(/\s/g, '').length !== 6) return

  try {
    await confirm.mutateAsync({ channel: props.channel, code: code.value })
    emit('close')
  } catch {
    code.value = ''
  }
}
</script>

<template>
  <Modal
    :open="channel !== null"
    :title="`Verify your ${noun}`"
    :description="
      stage === 'password'
        ? `A verified ${noun} can be used to reset your password, so we ask for your password first.`
        : `A code is on its way to ${destination}.`
    "
    :busy="busy"
    @close="emit('close')"
    @submit="next"
  >
    <div class="flex flex-col gap-3.5">
      <TextField
        v-if="stage === 'password'"
        key="password"
        v-model="password"
        label="Your password"
        type="password"
        revealable
        autocomplete="current-password"
        autofocus
        :error="passwordError"
        :disabled="busy"
      />
      <TextField
        v-else
        key="code"
        v-model="code"
        label="Code"
        autocomplete="one-time-code"
        inputmode="numeric"
        :maxlength="7"
        code
        autofocus
        :error="codeError"
        :disabled="busy"
      />
      <FormMessage v-if="failure" tone="warning">{{ failure }}</FormMessage>
    </div>

    <template #actions>
      <Button :disabled="busy" @click="emit('close')">Cancel</Button>
      <Button
        type="submit"
        variant="primary"
        :pending="busy"
        :disabled="stage === 'password' ? !password : code.replace(/\s/g, '').length !== 6"
      >
        {{ stage === 'password' ? 'Send code' : 'Verify' }}
      </Button>
    </template>
  </Modal>
</template>
