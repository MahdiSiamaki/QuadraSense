<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import { useChangePassword } from '@/api/auth'
import { ApiError } from '@/api/client'

/**
 * Change your own password.
 *
 * The rules shown here mirror the server's, which follows NIST SP 800-63B: length is what
 * matters, character-class rules are not required, and nothing expires on a schedule. They are
 * stated up front rather than revealed by rejection - a form that only tells you the rule after
 * you break it is a form people fight with.
 *
 * The confirmation field is checked in the browser only. The server has no opinion about it,
 * because it is not a security rule: it exists so a typo does not lock somebody out of an
 * account whose password they can no longer see.
 */
const props = withDefaults(defineProps<{ minimumLength?: number }>(), { minimumLength: 12 })
const emit = defineEmits<{ changed: [] }>()

const current = ref('')
const next = ref('')
const confirm = ref('')

const change = useChangePassword()

/** Field-level messages from the API, keyed by field name. */
const fieldErrors = computed(() => {
  const error = change.error.value
  if (!(error instanceof ApiError)) return {} as Record<string, string>
  return Object.fromEntries(error.fieldErrors.map((f) => [f.field, f.message]))
})

const mismatch = computed(
  () => confirm.value.length > 0 && next.value !== confirm.value,
)

const tooShort = computed(
  () => next.value.length > 0 && next.value.length < props.minimumLength,
)

const canSubmit = computed(
  () =>
    current.value.length > 0 &&
    next.value.length >= props.minimumLength &&
    next.value === confirm.value &&
    !change.isPending.value,
)

const unexpected = computed(() => {
  const error = change.error.value
  if (!error) return null
  if (error instanceof ApiError && error.status === 400) return null
  if (error instanceof ApiError) return error.problem?.title ?? `Server responded ${error.status}.`
  return 'Could not reach the server.'
})

async function submit() {
  if (!canSubmit.value) return

  try {
    await change.mutateAsync({ currentPassword: current.value, newPassword: next.value })
    current.value = ''
    next.value = ''
    confirm.value = ''
    emit('changed')
  } catch {
    // Shown from change.error.
  }
}
</script>

<template>
  <form class="flex flex-col gap-3.5" @submit.prevent="submit">
    <TextField
      v-model="current"
      label="Current password"
      type="password"
      autocomplete="current-password"
      required
      :error="fieldErrors['currentPassword'] ?? null"
      :disabled="change.isPending.value"
    />

    <TextField
      v-model="next"
      label="New password"
      type="password"
      autocomplete="new-password"
      required
      :hint="`At least ${minimumLength} characters. A passphrase of a few words is ideal.`"
      :error="
        fieldErrors['newPassword'] ??
        (tooShort ? `Use at least ${minimumLength} characters.` : null)
      "
      :disabled="change.isPending.value"
    />

    <TextField
      v-model="confirm"
      label="Confirm new password"
      type="password"
      autocomplete="new-password"
      required
      :error="mismatch ? 'The two passwords do not match.' : null"
      :disabled="change.isPending.value"
    />

    <!--
      Said before it happens, not discovered afterwards. Changing a password revokes every other
      session, which is the point of doing it when one is believed compromised - but someone who
      finds their other laptop signed out without warning reasonably reads it as a fault.
    -->
    <p class="text-xs text-[var(--c-text-muted)]">
      Changing your password signs you out everywhere except this browser.
    </p>

    <p
      v-if="unexpected"
      class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
      :style="{
        borderColor: 'var(--c-danger)',
        backgroundColor: 'var(--c-danger-subtle)',
        color: 'var(--c-danger)',
      }"
      role="alert"
    >
      {{ unexpected }}
    </p>

    <p
      v-else-if="change.isSuccess.value"
      class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
      :style="{
        borderColor: 'var(--c-success)',
        backgroundColor: 'var(--c-success-subtle)',
        color: 'var(--c-success)',
      }"
      role="status"
    >
      Password changed.
    </p>

    <div>
      <Button type="submit" variant="primary" :pending="change.isPending.value" :disabled="!canSubmit">
        Change password
      </Button>
    </div>
  </form>
</template>
