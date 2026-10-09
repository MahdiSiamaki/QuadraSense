<script setup lang="ts">
/**
 * A dashboard as a report: what an image or a printout shows (ADR-016 §11).
 *
 * Drawn from the server's snapshot - data the server loaded and audited for this person - by the same
 * widget components the dashboard uses, in export mode, so nothing queries and nothing moves while it
 * is captured. No drag handles, menus or buttons: a report is read, not used.
 *
 * Every card says what its own data is as of. The header says when the export was prepared, never
 * that the whole dashboard is one atomic moment: its sources are refreshed at different times.
 */
import { computed } from 'vue'
import type { DashboardSnapshot } from '@/api/dashboards'
import { formatDateTime } from '@/lib/format'
import { getWidget } from '../registry'
import { effectiveLens, rangeLabel } from '../composables/lens'
import { readingOrder } from '../composables/layout'
import { typeSpec } from '../registry/contract'
import { LENS_FILTER_KEYS, LENS_FILTER_LABELS } from '../composables/useDashboardLens'
import type { DashboardDefinition, DashboardLens, WidgetInstance } from '../types/definition'
import type { WidgetSnapshotData } from '../types/widget'
import WidgetErrorBoundary from './WidgetErrorBoundary.vue'

const props = defineProps<{
  name: string
  snapshot: DashboardSnapshot
  definition: DashboardDefinition
  urlLens: DashboardLens
  /** The widgets on this page of the report, in reading order. */
  widgetIds: string[]
  /** Show a widget that could not be prepared as a card saying why, rather than leave it out. */
  showUnavailable: boolean
  page?: { index: number; count: number }
}>()

const byId = computed(() => new Map(props.snapshot.widgets.map((w) => [w.id, w])))

const items = computed(() =>
  readingOrder(props.definition.widgets.filter((w) => props.widgetIds.includes(w.id)))
    .map((widget) => ({ widget, data: byId.value.get(widget.id) ?? null }))
    .filter((i) => i.data && (i.data.status === 'ok' || props.showUnavailable)),
)

function lensOf(widget: WidgetInstance): DashboardLens {
  const spec = typeSpec(widget.type)
  return spec ? effectiveLens(spec, props.definition.defaults, props.urlLens, widget.overrides ?? {}) : {}
}

const view = computed(() => ({ ...props.definition.defaults, ...props.urlLens }))

const lensWords = computed(() => {
  const v = view.value
  const parts = [`Counted by ${v.countBy ?? 'bindings'}`, v.includeUnknownDevice === false ? 'unknown devices hidden' : 'unknown devices shown', rangeLabel(v.range)]
  for (const key of LENS_FILTER_KEYS) if (v[key]) parts.push(`${LENS_FILTER_LABELS[key]}: ${v[key]}`)
  return parts.join(' · ')
})

const STATUS_WORDS: Record<WidgetSnapshotData['status'], string> = {
  ok: '',
  forbidden: 'Not included: you do not have the permission this widget needs.',
  notReady: 'Not included: its source is not ready.',
  error: 'Not included: the server could not prepare it.',
  interactive: 'Not included: it runs only on request. Run it on the dashboard and export it from its own menu.',
  noData: 'Nothing to show: a launcher has no data.',
}

/** Rows of the 40px dashboard grid, drawn at a comfortable report height. */
function height(widget: WidgetInstance) {
  return `${Math.max(3, widget.layout.h) * 40}px`
}
</script>

<template>
  <article class="report-view flex w-[1200px] flex-col gap-4 bg-[var(--c-surface)] p-8 text-[var(--c-text)]">
    <header class="flex flex-col gap-1 border-b pb-3">
      <h1 class="text-xl font-semibold tracking-tight">{{ name }}</h1>
      <p class="text-xs text-[var(--c-text-secondary)]">
        Prepared {{ formatDateTime(snapshot.preparedAt) }} by QuadraSense<template v-if="page && page.count > 1"> · page {{ page.index }} of {{ page.count }}</template>.
        Each card says what its own data is as of: the sources are refreshed at different times.
      </p>
      <p class="text-xs text-[var(--c-text-secondary)]">{{ lensWords }}</p>
      <p class="text-2xs text-[var(--c-text-muted)]">
        A binding is one number + SIM + handset combination. Subscriber, SIM and handset counts are estimates (about 0.5%).
      </p>
    </header>

    <div class="grid grid-cols-12 gap-4">
      <section
        v-for="{ widget, data } in items"
        :key="widget.id"
        class="report-card flex min-w-0 flex-col overflow-hidden rounded-[var(--radius-lg)] border"
        :style="{ gridColumn: `span ${widget.layout.w} / span ${widget.layout.w}` }"
      >
        <header class="border-b px-4 py-2">
          <h2 class="text-sm font-semibold">{{ widget.title ?? getWidget(widget.type)?.name ?? widget.type }}</h2>
          <p v-if="widget.subtitle" class="text-xs text-[var(--c-text-muted)]">{{ widget.subtitle }}</p>
          <p v-if="data!.asOf || data!.notes.length" class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
            <template v-if="data!.asOf">As of {{ data!.asOf }}.</template>
            {{ data!.notes.join(' ') }}
          </p>
        </header>
        <div class="min-h-0 flex-1 px-4 py-3" :style="{ height: height(widget) }">
          <p v-if="data!.status !== 'ok'" class="text-sm text-[var(--c-text-secondary)]">{{ data!.reason ?? STATUS_WORDS[data!.status] }}</p>
          <WidgetErrorBoundary v-else-if="getWidget(widget.type)">
            <component
              :is="getWidget(widget.type)!.component"
              :instance="widget"
              :lens="lensOf(widget)"
              mode="export"
              :snapshot="data"
              :active="true"
            />
          </WidgetErrorBoundary>
        </div>
      </section>
    </div>
  </article>
</template>

<style>
/*
  Printing: only the report, on white, each card whole on one page where it fits. Unscoped on
  purpose - it has to reach <body>'s other children. Scoped to the print medium, so the screen is
  untouched.
*/
@media print {
  body > *:not(.print-host) {
    display: none !important;
  }
  .print-host {
    position: static !important;
    inset: auto !important;
    opacity: 1 !important;
  }
  .report-view {
    width: 100% !important;
    padding: 0 !important;
  }
  .report-card {
    break-inside: avoid;
  }
  @page {
    size: A4 landscape;
    margin: 12mm;
  }
}
</style>
