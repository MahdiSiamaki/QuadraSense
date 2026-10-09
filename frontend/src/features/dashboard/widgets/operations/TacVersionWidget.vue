<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { useTacVersions } from '@/features/imports/useTacVersions'
import { formatDate, formatDateTime, formatFull, formatRelative } from '@/lib/format'
import type { TacVersionConfig } from './config'

/**
 * Which GSMA device database the names and models on every dashboard come from, and whether a newer
 * one is waiting to be activated - activation changes the manufacturer and model behind every figure.
 */
const props = defineProps<WidgetViewProps<TacVersionConfig>>()

const active = useQueryActive(props)
const query = useTacVersions({ enabled: active })
const versions = useWidgetData(props, query, (raw) => (raw as SnapshotData<'ops.tacVersion'>).tacVersions)

const current = computed(() => versions.data.value?.find((v) => v.status === 'Active') ?? null)
const pending = computed(() => versions.data.value?.filter((v) => v.status === 'Ready') ?? [])
</script>

<template>
  <AsyncBoundary
    class="h-full"
    :is-loading="versions.isPending.value"
    :is-error="versions.isError.value"
    :error="versions.error.value"
    min-height="3rem"
    gap="none"
    @retry="versions.refetch()"
  >
    <div class="flex h-full flex-col gap-1 overflow-auto text-xs">
      <p class="text-lg font-semibold tracking-tight">{{ current?.versionLabel ?? 'None active' }}</p>
      <p v-if="current" class="tabular text-[var(--c-text-secondary)]">
        <template v-if="current.rowCount !== null">{{ formatFull(current.rowCount) }} TACs</template>
        <template v-if="current.datasetDate"> · dataset of {{ formatDate(current.datasetDate) }}</template>
        <template v-if="current.activatedAt">
          · activated <span :title="formatDateTime(current.activatedAt)">{{ formatRelative(current.activatedAt) }}</span></template>
      </p>
      <p v-else class="text-[var(--c-text-secondary)]">Without an active version, manufacturer and model are unknown everywhere.</p>
      <p v-if="instance.config.showPending && pending.length" class="text-[var(--c-warning-text)]">
        <RouterLink v-if="mode === 'view'" to="/imports" class="font-medium hover:underline">
          {{ pending.length }} {{ pending.length === 1 ? 'version' : 'versions' }} waiting for activation
        </RouterLink>
        <template v-else>{{ pending.length }} {{ pending.length === 1 ? 'version' : 'versions' }} waiting for activation</template>
        ({{ pending.map((p) => p.versionLabel).join(', ') }}).
      </p>
    </div>
  </AsyncBoundary>
</template>
