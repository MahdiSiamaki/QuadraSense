<script setup lang="ts">
/**
 * The only file that talks to GridStack (ADR-016 §5). Vue owns every widget's content and state;
 * GridStack owns positions while a person drags, and nothing else.
 *
 * What the evaluation on this toolchain found, and what this adapter therefore does:
 *  - Declarative <GridStackItem> children render nothing in GridStack 14 (they mount before the grid
 *    exists), so items are GridStack-owned - added and removed here from the `widgets` prop - and the
 *    wrapper teleports a tiny host into each, which renders the page's `item` slot for that id.
 *  - GridStack's own responsive mode lost a saved position after a 12 -> 1 -> 12 round trip without
 *    telling anyone, so this adapter picks the column count from its own width and lays the derived
 *    layout out explicitly (`deriveLayout`). Programmatic updates emit nothing it acts on.
 *  - A layout is committed once per finished drag or resize, read from the engine after the gesture
 *    ends - never per frame, and never from a change GridStack made on our behalf.
 *  - Drag and resize happen only in edit mode on the 12-column grid. Everywhere else, and for
 *    keyboard and touch users, the card's menu moves and resizes (WCAG 2.2, 2.5.7).
 */
import { computed, defineComponent, h, inject, markRaw, onBeforeUnmount, onMounted, provide, ref, watch, type InjectionKey, type VNode } from 'vue'
import { GridStack as GridStackView, type GridStackOptions, type GridStackWidget } from 'gridstack/dist/vue'
import type { GridItemHTMLElement, GridStack } from 'gridstack'
import 'gridstack/dist/gridstack.css'
import type { WidgetInstance } from '../types/definition'
import { CONTRACT, typeSpec } from '../registry/contract'
import { deriveLayout, type Columns } from '../composables/layout'

const props = defineProps<{
  widgets: WidgetInstance[]
  /** Edit mode: drag and resize are on, at 12 columns. */
  editable: boolean
}>()

const emit = defineEmits<{
  /** Every position at the end of one gesture. */
  commit: [positions: { id: string; x: number; y: number; w: number; h: number }[], label: string]
  /** The column count in use, for the page to say why dragging is off on a narrow screen. */
  columns: [columns: Columns]
}>()

const slots = defineSlots<{
  item(props: { widget: WidgetInstance; columns: Columns }): VNode[]
}>()

/** One grid row, in pixels, and the gap between cards: the page's 20px block gap (16-ui). */
const CELL_HEIGHT = 40
const GAP = 20

const root = ref<HTMLElement | null>(null)
const view = ref<{ getGrid: () => GridStack | null } | null>(null)
const columns = ref<Columns>(12)
const byId = computed(() => new Map(props.widgets.map((w) => [w.id, w])))

const RENDER: InjectionKey<(id: string) => VNode[] | null> = Symbol('dashboard-grid-render')
provide(RENDER, (id) => {
  const widget = byId.value.get(id)
  return widget ? slots.item({ widget, columns: columns.value }) : null
})

/** What GridStack teleports into each item: the page's slot for that widget id. */
const Host = markRaw(
  defineComponent({
    name: 'DashboardGridHost',
    props: { id: { type: String, required: true } },
    setup(hostProps) {
      const render = inject(RENDER)!
      return () => h('div', { class: 'h-full' }, render(hostProps.id) ?? undefined)
    },
  }),
)

const components = { Host }

const options: GridStackOptions = {
  column: 12,
  cellHeight: CELL_HEIGHT,
  margin: GAP / 2,
  mode: 'top',
  staticGrid: true,
  // Positions snap: a dashboard is rearranged tens of times a day at most, and gliding cards read
  // as the layout still moving (16-ui, Motion).
  animate: false,
  handle: '.dashboard-drag-handle',
  alwaysShowResizeHandle: true,
  resizable: { handles: 'se' },
}

let applying = false

function grid(): GridStack | null {
  return view.value?.getGrid() ?? null
}

/** Makes the grid show `widgets` at the current column count, adding and removing items as needed. */
function sync() {
  const g = grid()
  if (!g) return

  const layouts = new Map(deriveLayout(props.widgets, columns.value).map((w) => [w.id, w.layout]))
  const existing = new Map(g.getGridItems().map((el) => [String(el.gridstackNode?.id ?? ''), el] as const))

  applying = true
  g.batchUpdate()
  try {
    for (const [id, el] of existing) {
      if (!layouts.has(id)) g.removeWidget(el, true, false)
    }
    for (const widget of props.widgets) {
      const layout = layouts.get(widget.id)!
      const spec = typeSpec(widget.type)
      const minW = columns.value === 12 ? (spec?.size.minW ?? 1) : 1
      const minH = spec?.size.minH ?? 1
      // The contract's tallest card: a resize stops there instead of making a height Save refuses.
      const maxH = CONTRACT.limits.maxWidgetHeight
      const el = existing.get(widget.id)
      if (!el) {
        g.addWidget({ id: widget.id, ...layout, minW, minH, maxH, component: 'Host', props: { id: widget.id } } as GridStackWidget as never)
        continue
      }
      const node = el.gridstackNode!
      if (node.x !== layout.x || node.y !== layout.y || node.w !== layout.w || node.h !== layout.h || node.minW !== minW || node.maxH !== maxH) {
        g.update(el as GridItemHTMLElement, { ...layout, minW, minH, maxH })
      }
    }
  } finally {
    g.batchUpdate(false)
    applying = false
  }
  g.setStatic(!(props.editable && columns.value === 12))
}

/** After a drag or resize ends: every position, as one commit. */
function endGesture(label: string) {
  // The engine settles pushed neighbours after the stop event; read it on the next task.
  setTimeout(() => {
    const g = grid()
    if (!g || applying || columns.value !== 12) return
    const positions = g.engine.nodes.map((n) => ({ id: String(n.id), x: n.x ?? 0, y: n.y ?? 0, w: n.w ?? 1, h: n.h ?? 1 }))
    emit('commit', positions, label)
  }, 0)
}

function columnsFor(width: number): Columns {
  return width >= 1024 ? 12 : width >= 640 ? 6 : 1
}

// Ids, types and positions only: a config or title change redraws the widget without touching the grid.
watch(
  () => props.widgets.map((w) => `${w.id}:${w.type}:${w.layout.x},${w.layout.y},${w.layout.w},${w.layout.h}`).join('|'),
  () => sync(),
)
watch(() => props.editable, () => sync())

let observer: ResizeObserver | null = null

onMounted(() => {
  if (root.value) columns.value = columnsFor(root.value.clientWidth)
  const g = grid()
  if (g && columns.value !== 12) g.column(columns.value, 'none')
  sync()
  emit('columns', columns.value)

  observer = new ResizeObserver(() => {
    if (!root.value) return
    const next = columnsFor(root.value.clientWidth)
    if (next === columns.value) return
    columns.value = next
    grid()?.column(next, 'none')
    sync()
    emit('columns', next)
  })
  if (root.value) observer.observe(root.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

defineExpose({ sync, columns })
</script>

<template>
  <div ref="root" class="dashboard-grid" :class="{ 'is-editing': editable && columns === 12 }">
    <GridStackView
      ref="view"
      :options="options"
      :components="components"
      @dragstop="endGesture('Move widget')"
      @resizestop="endGesture('Resize widget')"
    />
  </div>
</template>

<style scoped>
/*
  GridStack positions items with its own CSS; the cards inside are ours. Its placeholder (where a
  dragged card will land) takes the accent tint instead of the library's grey, so it reads as this
  app's - drawn with tokens, never literal colours.
*/
.dashboard-grid :deep(.grid-stack-placeholder > .placeholder-content) {
  background: var(--c-accent-subtle);
  border: 1px dashed var(--c-accent);
  border-radius: var(--radius-lg);
}

/* The library's resize glyph is a dark SVG; in edit mode a token-drawn corner is visible in both themes. */
.dashboard-grid :deep(.grid-stack-item > .ui-resizable-se) {
  background: none;
  width: 16px;
  height: 16px;
  right: 6px;
  bottom: 6px;
  border-right: 2px solid var(--c-border-strong);
  border-bottom: 2px solid var(--c-border-strong);
  border-bottom-right-radius: 3px;
  transform: none;
  cursor: se-resize;
}
</style>
