<script setup lang="ts">
/**
 * Settings that belong to the dashboard rather than to one widget: its description, how often its
 * aggregate widgets refresh, and its default view.
 *
 * The default view is saved only on purpose (ADR-016 §8): "Use the current view" takes what the URL
 * shows now - count unit, unknown devices, range, filters - and makes it what the dashboard opens
 * with. Looking at a filtered view never changes the saved dashboard by itself.
 */
import { computed, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import type { DashboardLens, RefreshMinutes } from '../types/definition'
import { rangeLabel } from '../composables/lens'
import { LENS_FILTER_KEYS, LENS_FILTER_LABELS } from '../composables/useDashboardLens'

const props = defineProps<{
  open: boolean
  description: string
  refreshMinutes: RefreshMinutes
  defaults: DashboardLens
  /** What is on screen now: the saved defaults with the URL on top. */
  current: DashboardLens
}>()

const emit = defineEmits<{
  close: []
  apply: [value: { description: string; refreshMinutes: RefreshMinutes; defaults: DashboardLens }]
}>()

const text = ref('')
const refresh = ref<RefreshMinutes>(0)
const defaults = ref<DashboardLens>({})

watch(
  () => props.open,
  (open) => {
    if (!open) return
    text.value = props.description
    refresh.value = props.refreshMinutes
    defaults.value = { ...props.defaults }
  },
  { immediate: true },
)

// SegmentedControl takes strings; the interval is a number in the definition.
const REFRESH_OPTIONS = [
  { value: '0', label: 'Off' },
  { value: '5', label: '5 min' },
  { value: '15', label: '15 min' },
  { value: '30', label: '30 min' },
]

const refreshChoice = computed({
  get: () => String(refresh.value),
  set: (value: string) => (refresh.value = Number(value) as RefreshMinutes),
})

function describe(lens: DashboardLens): string[] {
  const parts: string[] = []
  parts.push(`Counted by ${lens.countBy ?? 'bindings'}`)
  parts.push(lens.includeUnknownDevice === false ? 'unknown devices hidden' : 'unknown devices shown')
  parts.push(rangeLabel(lens.range))
  for (const key of LENS_FILTER_KEYS) if (lens[key]) parts.push(`${LENS_FILTER_LABELS[key]}: ${lens[key]}`)
  return parts
}

const differs = computed(() => JSON.stringify(normalised(props.current)) !== JSON.stringify(normalised(defaults.value)))

function normalised(lens: DashboardLens) {
  return {
    countBy: lens.countBy ?? 'bindings',
    includeUnknownDevice: lens.includeUnknownDevice ?? true,
    range: lens.range ?? 'all',
    vendor: lens.vendor ?? null,
    deviceType: lens.deviceType ?? null,
    operatingSystem: lens.operatingSystem ?? null,
    tac: lens.tac ?? null,
  }
}

function useCurrent() {
  const next: DashboardLens = {}
  for (const [key, value] of Object.entries(props.current)) if (value !== undefined) (next as Record<string, unknown>)[key] = value
  defaults.value = next
}

function apply() {
  emit('apply', { description: text.value.trim(), refreshMinutes: refresh.value, defaults: defaults.value })
}
</script>

<template>
  <Modal :open="open" title="Dashboard settings" @close="emit('close')" @submit="apply">
    <div class="flex flex-col gap-4 text-sm">
      <div class="flex flex-col gap-1">
        <label for="dashboard-settings-description" class="text-xs font-medium text-[var(--c-text-secondary)]">Description</label>
        <textarea
          id="dashboard-settings-description"
          v-model="text"
          rows="2"
          maxlength="1000"
          class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm"
        />
      </div>

      <div class="flex flex-col gap-1.5">
        <span class="text-xs font-medium text-[var(--c-text-secondary)]">Refresh aggregate widgets</span>
        <SegmentedControl v-model="refreshChoice" :options="REFRESH_OPTIONS" label="Refresh interval" />
        <p class="text-2xs text-[var(--c-text-muted)]">
          The data changes once a day. Risk lists and Explorer queries never refresh by themselves: each run is audited.
        </p>
      </div>

      <div class="flex flex-col gap-1.5">
        <span class="text-xs font-medium text-[var(--c-text-secondary)]">Default view</span>
        <p class="text-xs">{{ describe(defaults).join(' · ') }}</p>
        <div class="flex flex-wrap items-center gap-2">
          <Button size="sm" :disabled="!differs" @click="useCurrent">Use the current view</Button>
          <Button size="sm" variant="ghost" @click="defaults = {}">Reset to the plain defaults</Button>
        </div>
        <p v-if="differs" class="text-2xs text-[var(--c-text-muted)]">On screen now: {{ describe(current).join(' · ') }}</p>
      </div>
    </div>
    <template #actions>
      <Button variant="ghost" @click="emit('close')">Cancel</Button>
      <Button type="submit" variant="primary">Apply</Button>
    </template>
  </Modal>
</template>
