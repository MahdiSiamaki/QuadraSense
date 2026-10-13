<script setup lang="ts">
/**
 * One widget on a dashboard: whether it may be drawn at all, the lens it is computed with, what its
 * card says about that, and when its queries may run.
 *
 * Decided before anything mounts, so a widget the person may not use sends no request - a 403 is
 * audited, and a card that queries only to be refused teaches people to ignore errors:
 *  - a type this build does not know, or a config saved by a newer version, is a fallback card;
 *  - a permission the person lacks (dashboard access never grants data access) is a fallback card
 *    naming it.
 *
 * Queries wait until the card scrolls into view (`active`), and stay enabled once it has; exports
 * and previews are active at once.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useAuth } from '@/features/auth/useAuth'
import { getWidget } from '../registry'
import { requiredPermissions, typeSpec } from '../registry/contract'
import { effectiveLens, rangeLabel, supportsRange } from '../composables/lens'
import type { DashboardLens, WidgetInstance } from '../types/definition'
import type { WidgetMode, WidgetSnapshotData } from '../types/widget'
import type { ActionMenuItem } from './ActionMenu.vue'
import WidgetFrame, { type FrameBadge } from './WidgetFrame.vue'
import WidgetErrorBoundary from './WidgetErrorBoundary.vue'

const props = withDefaults(
  defineProps<{
    widget: WidgetInstance
    /** The dashboard's saved defaults. */
    defaults: DashboardLens
    /** What the URL names. */
    urlLens: DashboardLens
    mode: WidgetMode
    snapshot?: WidgetSnapshotData | null
    /** Edit-menu context: whether this widget can move up or down in reading order. */
    canMoveUp?: boolean
    canMoveDown?: boolean
    /** Whether the 12-column grid is in use (resize presets need it). */
    wide?: boolean
    /** The dashboard holds as many widgets as it may: Duplicate is not offered. */
    full?: boolean
  }>(),
  { snapshot: null, canMoveUp: false, canMoveDown: false, wide: true, full: false },
)

const emit = defineEmits<{ action: [key: string] }>()

const { can } = useAuth()

const definition = computed(() => getWidget(props.widget.type))
const spec = computed(() => typeSpec(props.widget.type))

const missing = computed(() => requiredPermissions(props.widget.type, props.widget.config).filter((p) => !can(p)))

/** Why the widget cannot be drawn, or null when it can. */
const blocked = computed<string | null>(() => {
  if (!definition.value || !spec.value) return 'This widget type is not available in this version of QuadraSense.'
  if (props.widget.configVersion > spec.value.configVersion) return 'These settings were saved by a newer version of QuadraSense.'
  if (props.widget.configVersion < spec.value.configVersion) return 'These settings are from an older version and could not be upgraded.'
  if (missing.value.length) return `Needs ${missing.value.join(', ')} - ask an administrator if you should have it.`
  return null
})

const lens = computed(() =>
  spec.value ? effectiveLens(spec.value, props.defaults, props.urlLens, props.widget.overrides ?? {}) : {},
)

/** The dashboard-level view: defaults with the URL on top. */
const view = computed<DashboardLens>(() => ({ ...props.defaults, ...props.urlLens }))

const badges = computed<FrameBadge[]>(() => {
  const s = spec.value
  if (!s) return []
  const list: FrameBadge[] = []
  const v = view.value
  const ignoresFilters =
    (['vendor', 'deviceType', 'operatingSystem', 'tac'] as const).some((k) => v[k] && !s.filters.includes(k)) ||
    (v.includeUnknownDevice === false && !s.filters.includes('includeUnknownDevice'))
  if (ignoresFilters) {
    list.push({ label: 'Network-wide', description: "The dashboard's filters do not apply to this widget: its source has no breakdown to filter." })
  }
  if (v.range && v.range !== 'all' && !supportsRange(s)) {
    list.push(
      s.time === 'fixedWindow'
        ? { label: 'Fixed rule window', description: `Each rule counts over its own window; the dashboard's range (${rangeLabel(v.range)}) does not apply.` }
        : { label: 'Current population', description: `A figure for the latest refreshed delivery; the dashboard's range (${rangeLabel(v.range)}) does not apply.` },
    )
  }
  const overridden = Object.keys(props.widget.overrides ?? {})
  if (overridden.length) {
    list.push({ label: 'Own view', description: `This widget sets its own ${overridden.join(', ')}, which wins over the dashboard's.`, tone: 'accent' })
  }
  return list
})

const title = computed(() => props.widget.title ?? definition.value?.name ?? props.widget.type)

const menu = computed<ActionMenuItem[]>(() => {
  if (props.mode === 'edit') {
    const items: ActionMenuItem[] = []
    if (!blocked.value || definition.value) items.push({ key: 'configure', label: 'Configure…', disabled: !!blocked.value })
    items.push({
      key: 'duplicate',
      label: 'Duplicate',
      disabled: !!blocked.value || props.full,
      hint: props.full ? 'This dashboard holds as many widgets as it may' : undefined,
    })
    items.push({ key: 'move:up', label: 'Move up', disabled: !props.canMoveUp, separated: true })
    items.push({ key: 'move:down', label: 'Move down', disabled: !props.canMoveDown })
    items.push({ key: 'move:start', label: 'Move to the start', disabled: !props.canMoveUp })
    items.push({ key: 'move:end', label: 'Move to the end', disabled: !props.canMoveDown })
    const sizeHint = props.wide ? undefined : 'On a wide screen'
    items.push({ key: 'size:small', label: 'Size: small', disabled: !props.wide, hint: sizeHint, separated: true })
    items.push({ key: 'size:medium', label: 'Size: medium', disabled: !props.wide })
    items.push({ key: 'size:large', label: 'Size: large', disabled: !props.wide })
    items.push({ key: 'size:full', label: 'Size: full width', disabled: !props.wide })
    items.push({ key: 'remove', label: 'Remove', danger: true, separated: true })
    return items
  }
  if (props.mode === 'view' && !blocked.value && definition.value) {
    const items: ActionMenuItem[] = []
    const exportsOk = can('data.export')
    if (definition.value.exports.csv)
      items.push({ key: 'export:csv', label: 'Download CSV', disabled: !exportsOk, hint: exportsOk ? undefined : 'Needs data.export' })
    if (definition.value.exports.image)
      items.push({ key: 'export:png', label: 'Save as image (PNG)', disabled: !exportsOk, hint: exportsOk ? undefined : 'Needs data.export' })
    return items
  }
  return []
})

/** Lazy mount: queries wait until the card is near the viewport, then stay enabled. */
const root = ref<HTMLElement | null>(null)
const seen = ref(props.mode === 'export' || props.mode === 'preview')
let observer: IntersectionObserver | null = null

onMounted(() => {
  if (seen.value || !root.value || typeof IntersectionObserver === 'undefined') {
    seen.value = true
    return
  }
  observer = new IntersectionObserver(
    (entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        seen.value = true
        observer?.disconnect()
        observer = null
      }
    },
    { rootMargin: '200px 0px' },
  )
  observer.observe(root.value)
})

onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <div ref="root" class="h-full" :data-widget-id="widget.id" :data-widget-type="widget.type">
    <WidgetFrame
      :title="title"
      :subtitle="widget.subtitle"
      :badges="blocked ? [] : badges"
      :menu="menu"
      :editing="mode === 'edit'"
      :chrome="blocked ? 'card' : (definition?.chrome ?? 'card')"
      :fit="definition?.height === 'content'"
      @action="emit('action', $event)"
    >
      <div v-if="blocked" class="flex h-full flex-col justify-center gap-1 text-sm" role="note">
        <p class="font-medium text-[var(--c-text-secondary)]">{{ blocked }}</p>
        <p v-if="mode === 'edit'" class="text-xs text-[var(--c-text-muted)]">Remove it from the card's menu, or keep it for those who can see it.</p>
        <p v-else class="text-xs text-[var(--c-text-muted)]">Nothing was requested for it.</p>
      </div>
      <WidgetErrorBoundary v-else>
        <component
          :is="definition!.component"
          :instance="widget"
          :lens="lens"
          :mode="mode"
          :snapshot="snapshot"
          :active="seen"
        />
      </WidgetErrorBoundary>
    </WidgetFrame>
  </div>
</template>
