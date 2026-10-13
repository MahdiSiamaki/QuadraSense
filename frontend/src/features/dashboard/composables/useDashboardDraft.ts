/**
 * The edit draft (ADR-016 §6): a copy of the saved definition, taken when editing starts.
 *
 * The saved version stays in TanStack Query and is never written here; Save sends this draft, Cancel
 * throws it away. Every change is a history entry - one per add, remove, duplicate, configure, and
 * one per finished drag or resize, never per frame - and undo/redo restore whole snapshots, so an
 * instance id never changes underneath anyone.
 */
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { cloneConfig, cloneDefinition, newWidgetId, type DashboardDefinition, type DashboardLens, type RefreshMinutes, type WidgetConfig, type WidgetInstance } from '../types/definition'
import { CONTRACT, typeSpec } from '../registry/contract'
import { findSpot, move, resolveCollisions, sizePreset, type MoveTarget, type SizePreset } from './layout'

export const MAX_HISTORY = 100

interface Snapshot {
  definition: DashboardDefinition
  name: string
  description: string
}

interface Entry {
  label: string
  snapshot: Snapshot
}

export interface WidgetPatch {
  config?: WidgetConfig
  title?: string | null
  subtitle?: string | null
  overrides?: DashboardLens
}

function clone(snapshot: Snapshot): Snapshot {
  return { definition: cloneDefinition(snapshot.definition), name: snapshot.name, description: snapshot.description }
}

function same(a: Snapshot, b: Snapshot): boolean {
  return a.name === b.name && a.description === b.description && JSON.stringify(a.definition) === JSON.stringify(b.definition)
}

function uniqueId(definition: DashboardDefinition): string {
  const taken = new Set(definition.widgets.map((w) => w.id))
  let id = newWidgetId()
  while (taken.has(id)) id = newWidgetId()
  return id
}

export const useDashboardDraft = defineStore('dashboardDraft', () => {
  /** What is being edited: a dashboard id, or `template:<key>` for an unsaved copy of a template. */
  const key = ref<string | null>(null)
  /**
   * The server revision the draft was taken from, and the one Save must name. Not the cached detail's:
   * a refetch during an edit (a reconnect, a reload of the other person's version) moved that forward,
   * and a save naming it overwrote the other person's changes with no conflict.
   */
  const revision = ref<number | null>(null)
  const base = ref<Snapshot | null>(null)
  const current = ref<Snapshot | null>(null)
  const past = ref<Entry[]>([])
  const future = ref<Entry[]>([])

  const editing = computed(() => current.value !== null)
  const definition = computed(() => current.value?.definition ?? null)
  const name = computed(() => current.value?.name ?? '')
  const description = computed(() => current.value?.description ?? '')
  const canUndo = computed(() => past.value.length > 0)
  const canRedo = computed(() => future.value.length > 0)
  const isDirty = computed(() => !!base.value && !!current.value && !same(base.value, current.value))
  const undoLabel = computed(() => past.value.at(-1)?.label ?? null)
  const redoLabel = computed(() => future.value.at(-1)?.label ?? null)

  function begin(dashboardKey: string, saved: DashboardDefinition, savedName: string, savedDescription: string, savedRevision: number | null = null) {
    key.value = dashboardKey
    revision.value = savedRevision
    base.value = clone({ definition: saved, name: savedName, description: savedDescription })
    current.value = clone(base.value)
    past.value = []
    future.value = []
  }

  /** Applies one change as one history entry. A change that changes nothing records nothing. */
  function apply(label: string, mutate: (draft: Snapshot) => void): boolean {
    if (!current.value) return false
    const before = clone(current.value)
    const next = clone(current.value)
    mutate(next)
    if (same(before, next)) return false
    past.value = [...past.value, { label, snapshot: before }].slice(-MAX_HISTORY)
    future.value = []
    current.value = next
    return true
  }

  function addWidget(
    type: string,
    config: WidgetConfig,
    title: string | null = null,
    overrides: DashboardLens = {},
    subtitle: string | null = null,
  ): string | null {
    const spec = typeSpec(type)
    if (!spec || !current.value) return null
    const id = uniqueId(current.value.definition)
    apply(`Add ${title ?? type}`, (draft) => {
      const layout = findSpot(draft.definition.widgets, spec.size.w, spec.size.h)
      draft.definition.widgets.push({
        id,
        type,
        configVersion: spec.configVersion,
        title,
        subtitle,
        config: cloneConfig(config),
        overrides: { ...overrides },
        layout,
      })
    })
    return id
  }

  function removeWidget(id: string) {
    apply('Remove widget', (draft) => {
      draft.definition.widgets = draft.definition.widgets.filter((w) => w.id !== id)
    })
  }

  /** A copy with a new id and its own config, placed in the first free spot. */
  function duplicateWidget(id: string): string | null {
    const source = current.value?.definition.widgets.find((w) => w.id === id)
    if (!source || !current.value) return null
    const copyId = uniqueId(current.value.definition)
    apply('Duplicate widget', (draft) => {
      const copy: WidgetInstance = JSON.parse(JSON.stringify(source))
      copy.id = copyId
      copy.layout = findSpot(draft.definition.widgets, source.layout.w, source.layout.h)
      draft.definition.widgets.push(copy)
    })
    return copyId
  }

  function updateWidget(id: string, patch: WidgetPatch, label = 'Change widget') {
    apply(label, (draft) => {
      const widget = draft.definition.widgets.find((w) => w.id === id)
      if (!widget) return
      if (patch.config !== undefined) widget.config = cloneConfig(patch.config)
      if (patch.title !== undefined) widget.title = patch.title?.trim() ? patch.title.trim() : null
      if (patch.subtitle !== undefined) widget.subtitle = patch.subtitle?.trim() ? patch.subtitle.trim() : null
      if (patch.overrides !== undefined) widget.overrides = { ...patch.overrides }
    })
  }

  /** The end of one drag or resize: every position the grid reports, as one history entry. */
  function commitLayout(positions: { id: string; x: number; y: number; w: number; h: number }[], label = 'Move widgets') {
    apply(label, (draft) => {
      for (const p of positions) {
        const widget = draft.definition.widgets.find((w) => w.id === p.id)
        // Never a height Save would refuse: the grid caps a resize, and this caps anything else.
        if (widget) widget.layout = { x: p.x, y: p.y, w: p.w, h: Math.min(p.h, CONTRACT.limits.maxWidgetHeight) }
      }
    })
  }

  function moveWidget(id: string, target: MoveTarget) {
    apply('Move widget', (draft) => {
      draft.definition.widgets = move(draft.definition.widgets, id, target)
    })
  }

  function resizeWidget(id: string, preset: SizePreset) {
    apply('Resize widget', (draft) => {
      const widget = draft.definition.widgets.find((w) => w.id === id)
      const spec = widget && typeSpec(widget.type)
      if (!widget || !spec) return
      widget.layout = sizePreset(widget.layout, spec, preset)
      draft.definition.widgets = resolveCollisions(draft.definition.widgets, id)
    })
  }

  /** The settings dialog's three values as one change: one Apply, one undo. */
  function setSettings(next: { description: string; refreshMinutes: RefreshMinutes; defaults: DashboardLens }) {
    apply('Change settings', (draft) => {
      draft.description = next.description
      draft.definition.refreshMinutes = next.refreshMinutes
      draft.definition.defaults = { ...next.defaults }
    })
  }

  /** The rename dialog's name, and its description when it shows one: one Rename, one undo. */
  function rename(next: string, description?: string) {
    apply('Rename', (draft) => {
      draft.name = next
      if (description !== undefined) draft.description = description
    })
  }

  function undo() {
    const entry = past.value.at(-1)
    if (!entry || !current.value) return
    past.value = past.value.slice(0, -1)
    future.value = [...future.value, { label: entry.label, snapshot: clone(current.value) }]
    current.value = clone(entry.snapshot)
  }

  function redo() {
    const entry = future.value.at(-1)
    if (!entry || !current.value) return
    future.value = future.value.slice(0, -1)
    past.value = [...past.value, { label: entry.label, snapshot: clone(current.value) }].slice(-MAX_HISTORY)
    current.value = clone(entry.snapshot)
  }

  /** Back to the saved version; history cleared. */
  function discard() {
    if (!base.value) return
    current.value = clone(base.value)
    past.value = []
    future.value = []
  }

  /** The server accepted the save: what was saved is the new base. */
  function markSaved(saved: DashboardDefinition, savedName: string, savedDescription: string, newKey?: string, savedRevision?: number) {
    if (newKey) key.value = newKey
    if (savedRevision !== undefined) revision.value = savedRevision
    base.value = clone({ definition: saved, name: savedName, description: savedDescription })
    current.value = clone(base.value)
    past.value = []
    future.value = []
  }

  /** Leaves edit mode. */
  function end() {
    key.value = null
    revision.value = null
    base.value = null
    current.value = null
    past.value = []
    future.value = []
  }

  function widget(id: string): WidgetInstance | undefined {
    return current.value?.definition.widgets.find((w) => w.id === id)
  }

  return {
    key,
    revision,
    editing,
    definition,
    name,
    description,
    canUndo,
    canRedo,
    undoLabel,
    redoLabel,
    isDirty,
    begin,
    apply,
    addWidget,
    removeWidget,
    duplicateWidget,
    updateWidget,
    commitLayout,
    moveWidget,
    resizeWidget,
    setSettings,
    rename,
    undo,
    redo,
    discard,
    markSaved,
    end,
    widget,
  }
})
