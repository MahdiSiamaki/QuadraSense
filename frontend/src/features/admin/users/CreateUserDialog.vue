<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import Modal from '@/design-system/Modal.vue'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import Toggle from '@/design-system/Toggle.vue'
import { useCreateUser, useRoles } from '@/api/admin'
import { ApiError } from '@/api/client'

/**
 * Create a local account.
 *
 * The initial password is typed by the administrator rather than generated, and "must change at
 * first sign-in" defaults on. That pairing is the point: a generated password has to be
 * transmitted somehow - and it is usually pasted into a chat message that outlives the account -
 * whereas one the administrator says out loud and the user immediately replaces has a lifetime
 * measured in minutes.
 *
 * Roles are chosen here rather than after creation, because an account created with no role can
 * sign in and see nothing, which reads to its owner as a broken system.
 */
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const router = useRouter()
const roles = useRoles()
const create = useCreateUser()

const username = ref('')
const displayName = ref('')
const password = ref('')
const email = ref('')
const jobTitle = ref('')
const selectedRoles = ref<string[]>([])
const mustChange = ref(true)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    username.value = ''
    displayName.value = ''
    password.value = ''
    email.value = ''
    jobTitle.value = ''
    selectedRoles.value = []
    mustChange.value = true
    create.reset()
  },
)

const fieldErrors = computed(() => {
  const error = create.error.value
  if (!(error instanceof ApiError)) return {} as Record<string, string>
  return Object.fromEntries(error.fieldErrors.map((f) => [f.field, f.message]))
})

const unexpected = computed(() => {
  const error = create.error.value
  if (!error) return null
  if (error instanceof ApiError && error.status === 400) return null
  if (error instanceof ApiError) return error.problem?.detail ?? error.problem?.title ?? null
  return 'Could not reach the server.'
})

const canSubmit = computed(
  () =>
    username.value.trim().length > 0 &&
    displayName.value.trim().length > 0 &&
    password.value.length >= 12 &&
    !create.isPending.value,
)

function toggleRole(code: string) {
  selectedRoles.value = selectedRoles.value.includes(code)
    ? selectedRoles.value.filter((c) => c !== code)
    : [...selectedRoles.value, code]
}

async function submit() {
  if (!canSubmit.value) return

  try {
    const created = await create.mutateAsync({
      username: username.value.trim(),
      displayName: displayName.value.trim(),
      password: password.value,
      email: email.value.trim() || null,
      jobTitle: jobTitle.value.trim() || null,
      phone: null,
      roleCodes: selectedRoles.value,
      mustChangePassword: mustChange.value,
    })

    emit('close')
    // Straight to the detail page: the next thing an administrator does is check that the
    // permissions came out the way they expected.
    await router.push(`/settings/users/${created.id}`)
  } catch {
    // Rendered from create.error.
  }
}
</script>

<template>
  <Modal
    :open="open"
    title="New user"
    description="Creates a local account. Directory accounts are not created here."
    :busy="create.isPending.value"
    @close="emit('close')"
  >
    <div class="flex flex-col gap-4">
      <div class="grid gap-3.5 sm:grid-cols-2">
        <TextField
          v-model="username"
          label="Username"
          required
          :error="fieldErrors['username'] ?? null"
          hint="Used to sign in. Cannot be changed later."
        />
        <TextField
          v-model="displayName"
          label="Display name"
          required
          :error="fieldErrors['displayName'] ?? null"
        />
        <TextField v-model="email" label="Email" type="email" optional-note="optional" />
        <TextField v-model="jobTitle" label="Job title" optional-note="optional" />
      </div>

      <TextField
        v-model="password"
        label="Initial password"
        type="password"
        autocomplete="new-password"
        required
        :error="fieldErrors['password'] ?? null"
        hint="At least 12 characters. Tell it to them directly, not by message."
      />

      <Toggle
        v-model="mustChange"
        label="Require a password change at first sign-in"
        description="Leave on unless you have a reason not to. Until they change it, the account can reach nothing else."
      />

      <fieldset>
        <legend class="text-xs font-medium text-[var(--c-text-secondary)]">
          Roles
        </legend>
        <p
          v-if="fieldErrors['roleCodes']"
          class="mt-1 text-xs text-[var(--c-danger-text)]"
          role="alert"
        >
          {{ fieldErrors['roleCodes'] }}
        </p>

        <div class="mt-2 flex flex-col gap-1.5">
          <label
            v-for="role in roles.data.value ?? []"
            :key="role.code"
            class="flex cursor-pointer items-start gap-2.5 rounded-[var(--radius-md)] border px-2.5 py-2 hover:bg-[var(--c-surface-hover)]"
            :style="
              selectedRoles.includes(role.code)
                ? { borderColor: 'var(--c-accent)', backgroundColor: 'var(--c-accent-subtle)' }
                : undefined
            "
          >
            <input
              type="checkbox"
              class="mt-0.5"
              :checked="selectedRoles.includes(role.code)"
              @change="toggleRole(role.code)"
            />
            <span class="min-w-0">
              <span class="block text-sm font-medium">{{ role.displayName }}</span>
              <span class="block text-xs text-[var(--c-text-muted)]">
                {{ role.description }}
              </span>
            </span>
          </label>
        </div>

        <p
          v-if="selectedRoles.length === 0"
          class="mt-2 text-xs text-[var(--c-warning-text)]"
        >
          With no role, this account can sign in but see nothing.
        </p>
      </fieldset>

      <p
        v-if="unexpected"
        class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
        :style="{
          borderColor: 'var(--c-danger)',
          backgroundColor: 'var(--c-danger-subtle)',
          color: 'var(--c-danger-text)',
        }"
        role="alert"
      >
        {{ unexpected }}
      </p>
    </div>

    <template #actions>
      <Button :disabled="create.isPending.value" @click="emit('close')">Cancel</Button>
      <Button
        variant="primary"
        :pending="create.isPending.value"
        :disabled="!canSubmit"
        @click="submit"
      >
        Create user
      </Button>
    </template>
  </Modal>
</template>
