<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import TextField from '@/design-system/TextField.vue'
import { useSetUserPermissions, type PermissionResolution, type UserDetail } from '@/api/admin'
import { ApiError } from '@/api/client'

/**
 * Every permission this user could have, and why they do or do not have it.
 *
 * This panel is the reason the API returns provenance instead of a boolean. Two questions are
 * asked of every access-control system ever built - "why can she export?" and "I removed her
 * lookup access, why does she still have it?" - and a list of granted permissions answers
 * neither. So each row states the outcome AND its cause: which roles grant it, whether a direct
 * grant or deny applies, and the reason recorded with that override.
 *
 * Permissions the user does NOT have are listed too, greyed rather than hidden. An access review
 * asks "should she be able to activate a TAC version?", and a screen that only shows what
 * somebody has cannot answer a question about what they should not.
 */
const props = defineProps<{ user: UserDetail; editable: boolean }>()

const save = useSetUserPermissions()

const editing = ref(false)
/** Working copy while the dialog is open: code -> 'grant' | 'deny'. */
const draft = ref(new Map<string, 'grant' | 'deny'>())
const reasons = ref(new Map<string, string>())

const categories = computed(() => {
  const groups = new Map<string, PermissionResolution[]>()
  for (const permission of props.user.permissions) {
    const list = groups.get(permission.category) ?? []
    list.push(permission)
    groups.set(permission.category, list)
  }
  return [...groups.entries()]
})

const grantedCount = computed(() => props.user.permissions.filter((p) => p.isGranted).length)
const overrideCount = computed(
  () => props.user.permissions.filter((p) => p.grantedDirectly || p.deniedDirectly).length,
)

watch(editing, (open) => {
  if (!open) return
  draft.value = new Map(
    props.user.permissions
      .filter((p) => p.grantedDirectly || p.deniedDirectly)
      .map((p) => [p.code, p.grantedDirectly ? ('grant' as const) : ('deny' as const)]),
  )
  reasons.value = new Map(
    props.user.permissions
      .filter((p) => p.overrideReason)
      .map((p) => [p.code, p.overrideReason ?? '']),
  )
  save.reset()
})

/** Cycles a permission through no override -> grant -> deny -> no override. */
function cycle(permission: PermissionResolution) {
  const current = draft.value.get(permission.code)
  const next = new Map(draft.value)

  if (current === undefined) next.set(permission.code, 'grant')
  else if (current === 'grant') next.set(permission.code, 'deny')
  else next.delete(permission.code)

  draft.value = next
}

/** What the effective answer would be if the draft were saved. */
function draftOutcome(permission: PermissionResolution): boolean {
  const override = draft.value.get(permission.code)
  if (override === 'deny') return false
  if (override === 'grant') return true
  return permission.grantedByRoles.length > 0
}

const unexpected = computed(() => {
  const error = save.error.value
  if (!error) return null
  if (error instanceof ApiError) {
    return error.problem?.detail ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return 'Could not reach the server.'
})

async function submit() {
  try {
    await save.mutateAsync({
      id: props.user.id,
      overrides: [...draft.value.entries()].map(([permissionCode, effect]) => ({
        permissionCode,
        effect,
        reason: reasons.value.get(permissionCode)?.trim() || null,
      })),
    })
    editing.value = false
  } catch {
    // Rendered from save.error.
  }
}
</script>

<template>
  <Card
    title="Permissions"
    :subtitle="`${grantedCount} of ${user.permissions.length} granted · ${overrideCount} direct override(s)`"
    flush
  >
    <template v-if="editable" #actions>
      <Button size="sm" @click="editing = true">Edit overrides</Button>
    </template>

    <div v-for="[category, permissions] in categories" :key="category" class="border-b last:border-b-0">
      <h3
        class="bg-[var(--c-surface-sunken)] px-4 py-1.5 text-[var(--text-2xs)] font-medium tracking-wide text-[var(--c-text-muted)] uppercase"
      >
        {{ category }}
      </h3>

      <ul class="divide-y">
        <li
          v-for="permission in permissions"
          :key="permission.code"
          class="flex items-start gap-3 px-4 py-2.5"
          :class="permission.isGranted ? '' : 'opacity-55'"
        >
          <span
            class="mt-1 size-1.5 shrink-0 rounded-full"
            :style="{
              backgroundColor: permission.deniedDirectly
                ? 'var(--c-danger)'
                : permission.isGranted
                  ? 'var(--c-success)'
                  : 'var(--c-text-muted)',
            }"
            aria-hidden="true"
          />

          <div class="min-w-0 flex-1">
            <p class="flex flex-wrap items-center gap-1.5 text-[var(--text-sm)]">
              <span class="font-medium">{{ permission.displayName }}</span>
              <code class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
                {{ permission.code }}
              </code>
              <span
                v-if="permission.isDangerous"
                class="rounded-full px-1.5 py-0.5 text-[var(--text-2xs)] font-medium"
                :style="{
                  backgroundColor: 'var(--c-warning-subtle)',
                  color: 'var(--c-warning)',
                }"
                title="Sensitive: grants access to raw identifiers, irreversible actions, or administration."
              >
                sensitive
              </span>
            </p>

            <p class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
              {{ permission.description }}
            </p>

            <!--
              The provenance line. This is the whole point of the panel: it reads as a sentence
              rather than as a set of flags, so the answer to "why" is the same text as the
              answer to "whether".
            -->
            <p class="mt-1 text-[var(--text-xs)]">
              <template v-if="permission.deniedDirectly">
                <span class="font-medium text-[var(--c-danger)]">Denied directly.</span>
                <template v-if="permission.grantedByRoles.length">
                  Overrides {{ permission.grantedByRoles.join(', ') }}.
                </template>
                <template v-if="permission.overrideReason">
                  &ldquo;{{ permission.overrideReason }}&rdquo;
                </template>
              </template>

              <template v-else-if="permission.grantedDirectly">
                <span class="font-medium text-[var(--c-accent)]">Granted directly</span>
                <template v-if="permission.grantedByRoles.length">
                  , and by {{ permission.grantedByRoles.join(', ') }}.
                </template>
                <template v-else> — no role carries it.</template>
                <template v-if="permission.overrideReason">
                  &ldquo;{{ permission.overrideReason }}&rdquo;
                </template>
              </template>

              <template v-else-if="permission.grantedByRoles.length">
                <span class="text-[var(--c-text-secondary)]">
                  From {{ permission.grantedByRoles.join(', ') }}.
                </span>
              </template>

              <template v-else>
                <span class="text-[var(--c-text-muted)]">Not granted by any role.</span>
              </template>
            </p>
          </div>
        </li>
      </ul>
    </div>

    <!-- ------------------------------------------------------------- editor -->
    <Modal
      :open="editing"
      title="Direct permission overrides"
      description="Layered over this user's roles. A deny always wins."
      size="lg"
      :busy="save.isPending.value"
      @close="editing = false"
    >
      <p class="mb-3 text-[var(--text-xs)] text-[var(--c-text-muted)]">
        Click a permission to cycle it: no override &rarr; grant &rarr; deny &rarr; no override.
        Use overrides for exceptions; if several people need the same set, make a role instead.
      </p>

      <div class="flex flex-col gap-3">
        <div v-for="[category, permissions] in categories" :key="category">
          <h4 class="mb-1 text-[var(--text-2xs)] font-medium tracking-wide text-[var(--c-text-muted)] uppercase">
            {{ category }}
          </h4>

          <ul class="flex flex-col gap-1">
            <li v-for="permission in permissions" :key="permission.code">
              <button
                type="button"
                class="flex w-full items-center gap-2.5 rounded-[var(--radius-md)] border px-2.5 py-1.5 text-left hover:bg-[var(--c-surface-hover)]"
                :style="
                  draft.get(permission.code) === 'deny'
                    ? { borderColor: 'var(--c-danger)', backgroundColor: 'var(--c-danger-subtle)' }
                    : draft.get(permission.code) === 'grant'
                      ? { borderColor: 'var(--c-accent)', backgroundColor: 'var(--c-accent-subtle)' }
                      : undefined
                "
                @click="cycle(permission)"
              >
                <span
                  class="w-14 shrink-0 text-center text-[var(--text-2xs)] font-medium"
                  :style="{
                    color:
                      draft.get(permission.code) === 'deny'
                        ? 'var(--c-danger)'
                        : draft.get(permission.code) === 'grant'
                          ? 'var(--c-accent)'
                          : 'var(--c-text-muted)',
                  }"
                >
                  {{ draft.get(permission.code) ?? 'role' }}
                </span>

                <span class="min-w-0 flex-1">
                  <span class="block truncate text-[var(--text-sm)]">
                    {{ permission.displayName }}
                  </span>
                  <span class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
                    {{
                      permission.grantedByRoles.length
                        ? `roles grant this: ${permission.grantedByRoles.join(', ')}`
                        : 'no role grants this'
                    }}
                  </span>
                </span>

                <!-- The resulting answer, so nobody has to work out what a deny does. -->
                <span
                  class="shrink-0 text-[var(--text-2xs)] font-medium"
                  :style="{
                    color: draftOutcome(permission) ? 'var(--c-success)' : 'var(--c-text-muted)',
                  }"
                >
                  {{ draftOutcome(permission) ? 'allowed' : 'blocked' }}
                </span>
              </button>

              <TextField
                v-if="draft.get(permission.code)"
                :model-value="reasons.get(permission.code) ?? ''"
                label="Reason"
                optional-note="recorded in the audit log"
                class="mt-1 ml-16"
                @update:model-value="
                  (v: string) => (reasons = new Map(reasons).set(permission.code, v))
                "
              />
            </li>
          </ul>
        </div>
      </div>

      <p
        v-if="unexpected"
        class="mt-3 rounded-[var(--radius-md)] border px-2.5 py-2 text-[var(--text-xs)]"
        :style="{
          borderColor: 'var(--c-danger)',
          backgroundColor: 'var(--c-danger-subtle)',
          color: 'var(--c-danger)',
        }"
        role="alert"
      >
        {{ unexpected }}
      </p>

      <template #actions>
        <Button :disabled="save.isPending.value" @click="editing = false">Cancel</Button>
        <Button variant="primary" :pending="save.isPending.value" @click="submit">
          Save overrides
        </Button>
      </template>
    </Modal>
  </Card>
</template>
