<script setup lang="ts">
/**
 * Who a dashboard is shared with (ADR-016 §9). Shown only to whoever may share it - its owner with
 * dashboard.share, or dashboard.manage - and every change is checked again by the server.
 *
 * What sharing is, said where it happens: a grant shares the arrangement, never data. Each person
 * sees each widget only with their own permissions; an editor grant edits only with dashboard.edit,
 * and nobody but the owner can widen access. A copied link is a link: it grants nothing.
 */
import { computed, ref, watch } from 'vue'
import {
  useAddGrant,
  useDashboardGrants,
  useRemoveGrant,
  useShareDirectory,
  useUpdateGrant,
  type DashboardDetail,
  type ShareSubject,
} from '@/api/dashboards'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { formatDate } from '@/lib/format'
import { problemText } from './apiProblem'

const props = defineProps<{ open: boolean; dashboard: DashboardDetail }>()
const emit = defineEmits<{ close: [] }>()

const grants = useDashboardGrants(() => props.dashboard.id, () => props.open)
const add = useAddGrant()
const update = useUpdateGrant()
const remove = useRemoveGrant()

const term = ref('')
const debounced = ref('')
let timer: ReturnType<typeof setTimeout> | null = null
watch(term, (value) => {
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => (debounced.value = value.trim()), 300)
})

const directory = useShareDirectory(debounced, () => props.open)
const level = ref<'viewer' | 'editor'>('viewer')
const error = ref<unknown>(null)
const copied = ref(false)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    term.value = ''
    debounced.value = ''
    level.value = 'viewer'
    error.value = null
    copied.value = false
  },
)

const granted = computed(() => new Set((grants.data.value?.grants ?? []).map((g) => `${g.subjectType}:${g.subjectId}`)))

const candidates = computed<ShareSubject[]>(() => {
  const d = directory.data.value
  if (!d || !term.value.trim()) return []
  return [...d.users, ...d.roles].filter((s) => !granted.value.has(`${s.type}:${s.id}`))
})

const atLimit = computed(() => (grants.data.value?.grants.length ?? 0) >= (grants.data.value?.maxGrants ?? Infinity))
const busy = computed(() => add.isPending.value || update.isPending.value || remove.isPending.value)

async function share(subject: ShareSubject) {
  error.value = null
  try {
    await add.mutateAsync({ dashboardId: props.dashboard.id, subjectType: subject.type, subjectId: subject.id, level: level.value })
    term.value = ''
  } catch (e) {
    error.value = e
  }
}

async function changeLevel(grantId: number, next: 'viewer' | 'editor') {
  error.value = null
  try {
    await update.mutateAsync({ dashboardId: props.dashboard.id, grantId, level: next })
  } catch (e) {
    error.value = e
  }
}

async function revoke(grantId: number) {
  error.value = null
  try {
    await remove.mutateAsync({ dashboardId: props.dashboard.id, grantId })
  } catch (e) {
    error.value = e
  }
}

async function copyLink() {
  const url = `${window.location.origin}/dashboards/${props.dashboard.id}`
  try {
    await navigator.clipboard.writeText(url)
    copied.value = true
  } catch {
    copied.value = false
  }
}

const LEVELS = [
  { value: 'viewer' as const, label: 'Can view' },
  { value: 'editor' as const, label: 'Can edit' },
]
</script>

<template>
  <Modal :open="open" size="lg" :title="`Share “${dashboard.name}”`" :busy="false" @close="emit('close')">
    <div class="flex flex-col gap-4 text-sm">
      <p class="text-xs text-[var(--c-text-secondary)]">
        Sharing shares the arrangement, never data: each person sees each widget only with their own permissions, and a
        widget they may not see shows them what it needs instead. <strong>Can edit</strong> works only for people who also
        hold dashboard.edit; nobody but you can share it further.
      </p>

      <div class="flex flex-col gap-2">
        <label for="share-search" class="text-xs font-medium text-[var(--c-text-secondary)]">Add people or roles</label>
        <div class="flex flex-wrap items-center gap-2">
          <input
            id="share-search"
            v-model="term"
            type="search"
            autocomplete="off"
            placeholder="Name, username or role"
            class="min-w-[min(18rem,100%)] flex-1 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm"
            :disabled="atLimit"
            @keydown.enter.prevent
          />
          <SegmentedControl v-model="level" :options="LEVELS" label="Level for new grants" />
        </div>
        <p v-if="atLimit" class="text-xs text-[var(--c-warning-text)]">
          This dashboard has the most grants it can have ({{ grants.data.value?.maxGrants }}). Share with a role instead.
        </p>
        <p v-else-if="term.trim().length === 1" class="text-2xs text-[var(--c-text-muted)]">Type two characters to search people; roles match at once.</p>

        <ul v-if="candidates.length" class="max-h-48 overflow-y-auto overscroll-contain rounded-[var(--radius-md)] border" aria-label="Matches">
          <li v-for="s in candidates" :key="`${s.type}:${s.id}`" class="flex items-center gap-2 border-b px-3 py-1.5 last:border-b-0">
            <span class="min-w-0 flex-1">
              <span class="block truncate">{{ s.name }}</span>
              <span class="block text-2xs text-[var(--c-text-muted)]">{{ s.type === 'role' ? 'Role - everyone who holds it' : s.detail }}</span>
            </span>
            <Button size="sm" :pending="add.isPending.value" :disabled="busy" @click="share(s)">Share</Button>
          </li>
        </ul>
        <p v-else-if="debounced && directory.isFetched.value && !directory.isFetching.value" class="text-xs text-[var(--c-text-muted)]">
          No one else matches “{{ debounced }}”.
        </p>
      </div>

      <div class="flex flex-col gap-2">
        <h3 class="text-xs font-medium text-[var(--c-text-secondary)]">Shared with</h3>
        <p v-if="grants.isPending.value" class="text-xs text-[var(--c-text-muted)]">Loading…</p>
        <p v-else-if="!grants.data.value?.grants.length" class="text-xs text-[var(--c-text-muted)]">Nobody yet: only you can open it.</p>
        <ul v-else class="rounded-[var(--radius-md)] border" aria-label="Grants">
          <li v-for="g in grants.data.value.grants" :key="g.id" class="flex flex-wrap items-center gap-2 border-b px-3 py-2 last:border-b-0">
            <span class="min-w-0 flex-1">
              <span class="block truncate">{{ g.subjectName }}</span>
              <span class="block text-2xs text-[var(--c-text-muted)]">
                {{ g.subjectType === 'role' ? 'Role' : g.subjectDetail }} · since {{ formatDate(g.grantedAt.slice(0, 10)) }}<template v-if="g.grantedBy"> by {{ g.grantedBy }}</template>
              </span>
            </span>
            <label class="sr-only" :for="`grant-level-${g.id}`">Level for {{ g.subjectName }}</label>
            <select
              :id="`grant-level-${g.id}`"
              class="h-7 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-xs"
              :value="g.level"
              :disabled="busy"
              @change="changeLevel(g.id, ($event.target as HTMLSelectElement).value as 'viewer' | 'editor')"
            >
              <option value="viewer">Can view</option>
              <option value="editor">Can edit</option>
            </select>
            <Button size="sm" variant="ghost" :disabled="busy" @click="revoke(g.id)">Remove</Button>
          </li>
        </ul>
      </div>

      <div class="flex flex-wrap items-center gap-2 rounded-[var(--radius-md)] bg-[var(--c-surface-sunken)] px-3 py-2">
        <span class="min-w-0 flex-1 text-xs text-[var(--c-text-secondary)]">
          A link opens it only for someone it is shared with, after signing in. It grants nothing by itself.
        </span>
        <Button size="sm" @click="copyLink">{{ copied ? 'Link copied' : 'Copy link' }}</Button>
      </div>

      <p v-if="error" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ problemText(error) }}</p>
    </div>
    <template #actions>
      <Button variant="primary" @click="emit('close')">Done</Button>
    </template>
  </Modal>
</template>
