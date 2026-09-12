<script setup lang="ts">
import { computed, ref } from 'vue'
import { GridLayout, GridItem } from 'grid-layout-plus'
import type { Dimension } from '@/api/dashboard'
import { useFilterState, FILTER_LABELS } from './useFilterState'
import { useDashboardLayout } from './useDashboardLayout'
import { WIDGETS } from './widgets/registry'
import WidgetFrame from './WidgetFrame.vue'
import AddWidgetPanel from './AddWidgetPanel.vue'

const { filters, activeFilters, setFilter, toggleUnknownDevice, clearAll } = useFilterState()
const layout = useDashboardLayout()

const showAddPanel = ref(false)

/**
 * Drill-down from a widget.
 *
 * Only dimensions that exist as filters can be drilled into. Clicking a model name,
 * for instance, has nothing to narrow to — there is no model filter — so it is
 * ignored rather than appearing to work and doing nothing.
 */
const DRILLABLE: Partial<Record<Dimension, keyof typeof FILTER_LABELS>> = {
  vendorCanonical: 'vendor',
  deviceType: 'deviceType',
  operatingSystem: 'operatingSystem',
  tac: 'tac',
}

function onDrill(dimension: Dimension, value: string) {
  const key = DRILLABLE[dimension]
  if (key) setFilter(key, value)
}

const isFiltered = computed(() => activeFilters.value.length > 0)

function toggleEdit() {
  layout.isEditing = !layout.isEditing
  if (!layout.isEditing) showAddPanel.value = false
}
</script>

<template>
  <div class="space-y-4">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Device population</h1>
        <p class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
          Active device–SIM bindings across the network, enriched with GSMA device data.
        </p>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <button
          type="button"
          class="rounded-[var(--radius-md)] border px-2.5 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          :class="filters.includeUnknownDevice ? '' : 'bg-[var(--c-surface-sunken)]'"
          @click="toggleUnknownDevice"
        >
          {{ filters.includeUnknownDevice ? 'Hide unknown devices' : 'Show unknown devices' }}
        </button>

        <button
          v-if="layout.isEditing"
          type="button"
          class="rounded-[var(--radius-md)] border px-2.5 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          :class="showAddPanel ? 'bg-[var(--c-surface-sunken)]' : ''"
          @click="showAddPanel = !showAddPanel"
        >
          Add widget
        </button>

        <button
          v-if="layout.isEditing && !layout.isDefault"
          type="button"
          class="rounded-[var(--radius-md)] border px-2.5 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          @click="layout.reset()"
        >
          Reset layout
        </button>

        <button
          type="button"
          class="rounded-[var(--radius-md)] px-3 py-1.5 text-[var(--text-xs)] font-medium"
          :class="
            layout.isEditing
              ? 'bg-[var(--c-accent)] text-[var(--c-accent-text)] hover:bg-[var(--c-accent-hover)]'
              : 'border hover:bg-[var(--c-surface-hover)]'
          "
          @click="toggleEdit"
        >
          {{ layout.isEditing ? 'Done editing' : 'Edit dashboard' }}
        </button>
      </div>
    </header>

    <!-- Says where the layout lives. "Per browser" is a real limitation a user
         should be told about, not discover by losing their board on another machine. -->
    <p
      v-if="layout.isEditing"
      class="rounded-[var(--radius-md)] border border-dashed bg-[var(--c-surface-sunken)] px-3 py-2 text-[var(--text-xs)] text-[var(--c-text-secondary)]"
    >
      Drag the <span aria-hidden="true">⠿</span> handle to move a widget, drag its bottom-right
      corner to resize, and use Settings to change what it shows. Changes save automatically —
      to this browser only, until user accounts exist.
    </p>

    <AddWidgetPanel
      :open="layout.isEditing && showAddPanel"
      @add="layout.add($event)"
      @close="showAddPanel = false"
    />

    <!-- Active filters -->
    <div v-if="isFiltered" class="flex flex-wrap items-center gap-2">
      <span class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Filtered by</span>
      <button
        v-for="f in activeFilters"
        :key="f.key"
        type="button"
        class="group inline-flex items-center gap-1.5 rounded-full bg-[var(--c-accent-subtle)] py-1 pr-2 pl-2.5 text-[var(--text-xs)] font-medium"
        :title="`Remove ${FILTER_LABELS[f.key]} filter`"
        @click="setFilter(f.key, undefined)"
      >
        <span class="text-[var(--c-text-secondary)]">{{ FILTER_LABELS[f.key] }}:</span>
        <span>{{ f.value }}</span>
        <span aria-hidden="true" class="text-[var(--c-text-muted)] group-hover:text-[var(--c-text)]">×</span>
      </button>
      <button
        type="button"
        class="rounded-[var(--radius-sm)] px-2 py-1 text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)] underline-offset-2 hover:underline"
        @click="clearAll"
      >
        Clear all
      </button>
      <span class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
        · filtered views query raw data and take a few seconds
      </span>
    </div>

    <!--
      Row height is deliberately small (24px) so heights are expressed in fine
      increments and resizing feels continuous rather than snapping in large jumps.
      Vertical compacting pulls widgets up into the gap left by a removal.
    -->
    <GridLayout
      v-model:layout="layout.widgets"
      :col-num="12"
      :row-height="24"
      :margin="[16, 16]"
      :is-draggable="layout.isEditing"
      :is-resizable="layout.isEditing"
      :vertical-compact="true"
      :use-css-transforms="true"
      drag-allow-from=".widget-drag-handle"
    >
      <GridItem
        v-for="item in layout.widgets"
        :key="item.i"
        :i="item.i"
        :x="item.x"
        :y="item.y"
        :w="item.w"
        :h="item.h"
        :min-w="3"
        :min-h="3"
      >
        <WidgetFrame
          :type="item.type"
          :config="item.config"
          :is-editing="layout.isEditing"
          @remove="layout.remove(item.i)"
          @duplicate="layout.duplicate(item.i)"
          @update="layout.updateConfig(item.i, $event)"
        >
          <component
            :is="WIDGETS[item.type]?.component"
            :config="item.config"
            :filters="filters"
            @drill="onDrill"
          />
        </WidgetFrame>
      </GridItem>
    </GridLayout>
  </div>
</template>

<style>
/* The grid library ships unstyled placeholders and handles. Binding them to our
   design tokens keeps edit mode looking like part of the product rather than a
   third-party widget dropped into it. */
.vgl-item--placeholder {
  background: var(--c-accent);
  opacity: 0.12;
  border-radius: var(--radius-lg);
}

.vgl-item__resizer {
  width: 14px;
  height: 14px;
  right: 3px;
  bottom: 3px;
  opacity: 0.4;
}

.vgl-item__resizer::after {
  border-right: 2px solid var(--c-text-muted);
  border-bottom: 2px solid var(--c-text-muted);
  width: 7px;
  height: 7px;
}

.vgl-item--dragging {
  z-index: 20;
  cursor: grabbing;
}
</style>
