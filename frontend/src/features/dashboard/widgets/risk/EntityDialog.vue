<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Modal from '@/design-system/Modal.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import EntityPanel from '@/features/explorer/EntityPanel.vue'
import TimelineView from '@/features/timeline/TimelineView.vue'

/**
 * One listed entity, opened from a dashboard - the risk page's panel and timeline in a dialog.
 *
 * The identifier stays in memory: never a route, a query string or the dashboard's definition, so it
 * cannot leak through a shared link or the browser history. Each opening is the entity endpoints' own
 * audited request, exactly as on the risk page.
 */
const props = defineProps<{ identifier: string | null; dataThrough: string | null }>()
const emit = defineEmits<{ close: [] }>()

const { can } = useAuth()
const stack = ref<string[]>([])
const timeline = ref(false)
const current = computed(() => stack.value[stack.value.length - 1] ?? null)

watch(
  () => props.identifier,
  (identifier) => {
    stack.value = identifier ? [identifier] : []
    timeline.value = false
  },
  { immediate: true },
)

function drill(identifier: string) {
  timeline.value = false
  if (current.value !== identifier) stack.value.push(identifier)
}

function back() {
  if (timeline.value) timeline.value = false
  else if (stack.value.length > 1) stack.value.pop()
  else emit('close')
}
</script>

<template>
  <Modal :open="current !== null" :title="timeline ? 'Timeline' : 'Entity'" size="lg" @close="emit('close')">
    <template v-if="current">
      <TimelineView v-if="timeline" :identifier="current" @drill="drill" @close="timeline = false" />
      <EntityPanel
        v-else
        :key="current"
        :identifier="current"
        :depth="stack.length"
        :data-through="dataThrough"
        :can-open-device="can(Permission.DeviceView)"
        :can-explore="false"
        @back="back"
        @close="emit('close')"
        @timeline="timeline = true"
      />
    </template>
  </Modal>
</template>
