<script setup lang="ts">
import { computed } from 'vue'
import { useCapabilities } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import CapabilityBars from '@/design-system/CapabilityBars.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import { useTacVersions } from '@/features/imports/useTacVersions'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatFull } from '@/lib/format'
import type { CapabilitiesConfig } from './config'
import { lensCountBy } from '../overview/shared/filters'
import { useKept } from '../overview/shared/useKept'

/**
 * Network and SIM capability: LTE, 5G, eSIM, IMS emergency calling - independent bars, because the
 * categories overlap. Network-wide, from a pre-computed table.
 *
 * The size of the GSMA database in the VoLTE note is read from the active version, never written
 * down: it was the literal 270,166 until activating the September 16 export took it to 270,885, and
 * the sentence kept asserting the old figure. A number in prose is a number nobody updates.
 */
const props = defineProps<WidgetViewProps<CapabilitiesConfig>>()
type Snapshot = SnapshotData<'device.capabilities'>

const { can } = useAuth()
const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const countBy = computed(() => lensCountBy(props.lens))
const query = useCapabilities(countBy, { enabled: active })
const data = useWidgetData(props, query, (raw) => (raw as Snapshot).capabilities)
const { value: rows, loading, updating } = useKept(data)

const tacEnabled = computed(() => active.value && can(Permission.ImportView) && config.value.showVolteNote !== false)
const tacQuery = useTacVersions({ enabled: tacEnabled })
const tac = useWidgetData(props, tacQuery, (raw) => (raw as Snapshot).tacVersions ?? undefined)
const activeTacRows = computed(() => tac.data.value?.find((v) => v.status === 'Active')?.rowCount ?? null)

const shown = computed(() => (rows.value ?? []).filter((r) => (config.value.capabilities as string[]).includes(r.capability)))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Counted by {{ countBy }}<template v-if="countBy !== 'bindings'"> (estimated)</template>, from the GSMA band list
      and eUICC records.
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="data.isError.value"
      :error="data.error.value"
      :is-empty="!loading && shown.length === 0"
      min-height="8rem"
      gap="none"
      @retry="data.refetch()"
    >
      <div class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <CapabilityBars :data="shown" />
      </div>
    </AsyncBoundary>
    <p v-if="config.showVolteNote" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      <strong>VoLTE is not shown</strong> because the GSMA dataset does not contain it: the band list mentions VoLTE in 2
      records<template v-if="activeTacRows"> of {{ formatFull(activeTacRows) }}</template>, and the IMS fields describe
      emergency calling rather than VoLTE. A proxy would look like an answer without being one.
    </p>
  </div>
</template>
