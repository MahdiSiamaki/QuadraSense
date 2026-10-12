<script setup lang="ts">
/**
 * The dashboard: a saved dashboard, a template, or - at `/` with nothing chosen - Device Population,
 * the page this used to be (ADR-016 §7). Old links keep their meaning: the URL lens is read the way
 * the old page read it, and `/` never redirects.
 *
 * View and edit are two modes of one page. Edit works on a draft (useDashboardDraft); the saved
 * version stays in TanStack Query. Save sends the whole draft with the revision it started from, and
 * a 409 keeps the draft and asks what to do. A dirty draft is guarded on Cancel, on leaving the route
 * and on closing the tab.
 *
 * If the dashboards API itself is unavailable, `/` still shows Device Population: the analytics never
 * depended on it.
 */
import { computed, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter, type RouteLocationNormalized } from 'vue-router'
import { useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '@/api/client'
import {
  useCreateDashboard,
  useDashboard,
  useDashboardList,
  useDeleteDashboard,
  useDuplicateDashboard,
  useSetDefault,
  useSetFavorite,
  useUpdateDashboard,
  type DashboardList,
  type DashboardSummary,
} from '@/api/dashboards'
import { useDailyChanges } from '@/api/dashboard'
import { Permission, useAuth } from '@/features/auth/useAuth'
import Button from '@/design-system/Button.vue'
import { formatDate, formatTime } from '@/lib/format'
import DashboardGrid from './components/DashboardGrid.vue'
import WidgetHost from './components/WidgetHost.vue'
import LensBar from './components/LensBar.vue'
import DashboardSwitcher from './components/DashboardSwitcher.vue'
import ActionMenu, { type ActionMenuItem } from './components/ActionMenu.vue'
import NameDialog from './components/NameDialog.vue'
import DeleteDialog from './components/DeleteDialog.vue'
import ConfirmDialog from './components/ConfirmDialog.vue'
import ConflictDialog from './components/ConflictDialog.vue'
import DashboardSettingsDialog from './components/DashboardSettingsDialog.vue'
import AddWidgetDialog, { type WizardResult } from './components/AddWidgetDialog.vue'
import ShareDialog from './components/ShareDialog.vue'
import ExportDialog, { type ExportKind } from './components/ExportDialog.vue'
import { conflictOf, problemText } from './components/apiProblem'
import { useDashboardDraft } from './composables/useDashboardDraft'
import { useDashboardLens, type LensFilterKey } from './composables/useDashboardLens'
import { canMove, readingOrder, type Columns, type MoveTarget, type SizePreset } from './composables/layout'
import { migrateDefinition } from './registry/migrations'
import { validateDefinition } from './registry/validate'
import { DEFAULT_TEMPLATE_KEY, getTemplate, instantiateTemplate, templateReference, visibleTemplateDefinition } from './templates'
import { cloneDefinition, type CountBy, type DashboardDefinition, type DashboardLens, type WidgetInstance } from './types/definition'
import { DASHBOARD_CONTEXT, type WidgetMode } from './types/widget'

const route = useRoute()
const router = useRouter()
const queryClient = useQueryClient()
const { can } = useAuth()
const draft = useDashboardDraft()
const { urlLens, setFilter, clearFilters, setCountBy, setIncludeUnknown, setRange } = useDashboardLens()

// ---------------------------------------------------------------- which one

const list = useDashboardList()
const others = ref<DashboardSummary[] | null>(null)

const routeId = computed(() => (typeof route.params.id === 'string' ? Number(route.params.id) : null))
const routeTemplate = computed(() => (typeof route.params.key === 'string' ? route.params.key : null))

type Target = { kind: 'dashboard'; id: number } | { kind: 'template'; key: string }

const target = computed<Target | null>(() => {
  if (routeId.value !== null && Number.isFinite(routeId.value)) return { kind: 'dashboard', id: routeId.value }
  if (routeTemplate.value) return { kind: 'template', key: routeTemplate.value }
  // `/`: wait for the person's default rather than flash Device Population first - unless the list
  // cannot be had at all, when Device Population is what `/` always was.
  if (list.isPending.value) return null
  const preference = list.data.value?.default
  if (preference?.dashboardId) return { kind: 'dashboard', id: preference.dashboardId }
  if (preference?.templateKey && getTemplate(preference.templateKey)) return { kind: 'template', key: preference.templateKey }
  return { kind: 'template', key: DEFAULT_TEMPLATE_KEY }
})

const dashboardId = computed(() => (target.value?.kind === 'dashboard' ? target.value.id : null))
const detail = useDashboard(dashboardId)
const template = computed(() => (target.value?.kind === 'template' ? (getTemplate(target.value.key) ?? null) : null))

const migrated = computed(() => (detail.data.value ? migrateDefinition(detail.data.value.definition) : null))
const templateView = computed(() => (template.value ? visibleTemplateDefinition(template.value, can) : null))

const currentKey = computed(() =>
  dashboardId.value !== null ? String(dashboardId.value) : template.value ? `template:${template.value.key}` : null,
)

const editing = computed(() => draft.editing && draft.key === currentKey.value)
const savedDefinition = computed<DashboardDefinition | null>(() => migrated.value?.definition ?? templateView.value?.definition ?? null)
const definition = computed<DashboardDefinition | null>(() => (editing.value ? draft.definition : savedDefinition.value))
const widgets = computed<WidgetInstance[]>(() => definition.value?.widgets ?? [])
const mode = computed<WidgetMode>(() => (editing.value ? 'edit' : 'view'))

const name = computed(() =>
  editing.value ? draft.name : (detail.data.value?.name ?? template.value?.name ?? (target.value ? '' : 'Dashboard')),
)
const description = computed(() => detail.data.value?.description ?? template.value?.description ?? '')
const access = computed(() => detail.data.value?.access ?? null)
const notFound = computed(() => detail.isError.value && detail.error.value instanceof ApiError && detail.error.value.status === 404)

const unknownTemplate = computed(() => target.value?.kind === 'template' && !template.value)

/** What the switcher and the browser tab call this. */
watch(name, (value) => {
  if (value) document.title = `${value} · QuadraSense`
})

// --------------------------------------------------------------------- lens

const defaults = computed<DashboardLens>(() => definition.value?.defaults ?? {})
const view = computed<DashboardLens>(() => ({ ...defaults.value, ...urlLens.value }))

function onCountBy(value: CountBy) {
  setCountBy(value, defaults.value.countBy ?? 'bindings')
}
function onIncludeUnknown(value: boolean) {
  setIncludeUnknown(value, defaults.value.includeUnknownDevice ?? true)
}
function onRange(value: string) {
  setRange(value, defaults.value.range ?? 'all')
}
function onRemoveFilter(key: LensFilterKey) {
  setFilter(key, undefined)
}

// ------------------------------------------------------------------ context

const daily = useDailyChanges({ enabled: computed(() => can(Permission.DashboardView)) })
const dataThrough = computed(() => daily.data.value?.at(-1)?.date ?? null)
const refreshToken = ref(0)
const refreshedAt = ref(new Date())

provide(DASHBOARD_CONTEXT, {
  mode,
  setLensFilter: (key, value) => setFilter(key, value),
  dataThrough,
  refreshToken,
  snapshot: ref(null),
})

/** The aggregate queries a refresh may repeat. Audited lists and queries are mutations: never these. */
const REFRESHABLE = new Set([
  'kpi', 'top', 'distribution', 'device-class-mix', 'capabilities', 'daily-changes', 'daily-churn', 'vendor-movement',
  'feed-quality', 'quality-signals', 'model-arrivals', 'risk', 'imports', 'tac-versions', 'device',
])

function refresh() {
  refreshToken.value += 1
  refreshedAt.value = new Date()
  void queryClient.invalidateQueries({ predicate: (q) => REFRESHABLE.has(String(q.queryKey[0])) })
}

let timer: ReturnType<typeof setInterval> | null = null
watch(
  () => [definition.value?.refreshMinutes ?? 0, editing.value] as const,
  ([minutes, isEditing]) => {
    if (timer) clearInterval(timer)
    timer = null
    if (minutes > 0 && !isEditing) timer = setInterval(refresh, minutes * 60_000)
  },
  { immediate: true },
)

// -------------------------------------------------------------- grid & menu

const columns = ref<Columns>(12)
const order = computed(() => readingOrder(widgets.value))

function onCommit(positions: { id: string; x: number; y: number; w: number; h: number }[], label: string) {
  if (editing.value) draft.commitLayout(positions, label)
}

function onWidgetAction(widget: WidgetInstance, key: string) {
  if (key === 'configure') return openConfigure(widget)
  if (key === 'duplicate') return void draft.duplicateWidget(widget.id)
  if (key === 'remove') return draft.removeWidget(widget.id)
  if (key.startsWith('move:')) return draft.moveWidget(widget.id, key.slice(5) as MoveTarget)
  if (key.startsWith('size:')) return draft.resizeWidget(widget.id, key.slice(5) as SizePreset)
  if (key === 'export:csv') return openExport('csv', [widget.id])
  if (key === 'export:png') return openExport('png', [widget.id])
}

// --------------------------------------------------------------------- edit

const saveError = ref<unknown>(null)
const validationErrors = ref<string[]>([])
const update = useUpdateDashboard()
const create = useCreateDashboard()
const conflict = ref<{ updatedBy: string | null; updatedAt: string | null } | null>(null)

const canEdit = computed(() => !!access.value?.canEdit)
const canCreate = computed(() => can(Permission.DashboardCreate))

function startEditing() {
  if (!savedDefinition.value || !currentKey.value || !detail.data.value) return
  saveError.value = null
  validationErrors.value = []
  draft.begin(currentKey.value, cloneDefinition(savedDefinition.value), detail.data.value.name, detail.data.value.description)
}

async function save() {
  const d = detail.data.value
  if (!editing.value || !d || !draft.definition || update.isPending.value) return
  saveError.value = null
  const problems = validateDefinition(draft.definition, { maxWidgets: list.data.value?.limits.maxWidgets ?? 24 })
  validationErrors.value = problems.map((p) => `${p.field}: ${p.message}`)
  if (problems.length) return

  try {
    const saved = await update.mutateAsync({
      id: d.id,
      name: draft.name,
      description: draft.description,
      definition: draft.definition,
      expectedRevision: d.revision,
    })
    draft.markSaved(saved.definition, saved.name, saved.description)
    draft.end()
  } catch (error) {
    const winner = conflictOf(error)
    if (winner) conflict.value = { updatedBy: winner.updatedBy, updatedAt: winner.updatedAt }
    else saveError.value = error
  }
}

const confirmCancel = ref(false)
function cancelEditing() {
  if (draft.isDirty) confirmCancel.value = true
  else draft.end()
}

async function reloadSaved() {
  conflict.value = null
  draft.end()
  await detail.refetch()
}

/** Keeps the draft's changes as a new private dashboard. */
async function saveConflictAsCopy() {
  conflict.value = null
  if (!draft.definition) return
  try {
    const created = await create.mutateAsync({
      name: await freeName(`${draft.name} (my changes)`),
      description: draft.description,
      definition: draft.definition,
      sourceTemplate: detail.data.value?.sourceTemplate ?? null,
    })
    draft.end()
    await router.push({ name: 'dashboard-view', params: { id: created.id }, query: route.query })
  } catch (error) {
    saveError.value = error
  }
}

async function freeName(wanted: string) {
  const taken = new Set((list.data.value?.mine ?? []).map((d) => d.name.trim().toLowerCase()))
  let name = wanted.slice(0, 100)
  for (let n = 2; taken.has(name.trim().toLowerCase()) && n < 100; n++) name = `${wanted.slice(0, 94)} (${n})`
  return name
}

function onKeydown(event: KeyboardEvent) {
  if (!editing.value) return
  const target = event.target as HTMLElement | null
  if (target?.closest('input, textarea, select, [contenteditable="true"], dialog')) return
  const mod = event.ctrlKey || event.metaKey
  if (mod && event.key.toLowerCase() === 'z' && !event.shiftKey) {
    event.preventDefault()
    draft.undo()
  } else if (mod && (event.key.toLowerCase() === 'y' || (event.key.toLowerCase() === 'z' && event.shiftKey))) {
    event.preventDefault()
    draft.redo()
  }
}

// ---------------------------------------------------------- leaving, guarded

const pendingLeave = ref<RouteLocationNormalized | null>(null)

function guard(to: RouteLocationNormalized, from: RouteLocationNormalized) {
  // A lens change is the same dashboard: never guarded.
  if (to.path === from.path) return true
  if (editing.value && draft.isDirty) {
    pendingLeave.value = to
    return false
  }
  if (editing.value) draft.end()
  return true
}

onBeforeRouteLeave(guard)
onBeforeRouteUpdate(guard)

async function leaveAnyway() {
  const to = pendingLeave.value
  pendingLeave.value = null
  draft.end()
  if (to) await router.push(to.fullPath)
}

function onBeforeUnload(event: BeforeUnloadEvent) {
  if (editing.value && draft.isDirty) {
    event.preventDefault()
    event.returnValue = ''
  }
}

onMounted(() => {
  window.addEventListener('beforeunload', onBeforeUnload)
  window.addEventListener('keydown', onKeydown)
  document.addEventListener('fullscreenchange', onFullscreenChange)
})

onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  window.removeEventListener('keydown', onKeydown)
  document.removeEventListener('fullscreenchange', onFullscreenChange)
  if (timer) clearInterval(timer)
})

// --------------------------------------------------------- dashboard actions

type NameMode = 'new' | 'template' | 'duplicate' | 'rename'
const nameDialog = ref<NameMode | null>(null)
const nameError = ref<unknown>(null)
const duplicate = useDuplicateDashboard()
const remove = useDeleteDashboard()
const setDefault = useSetDefault()
const setFavorite = useSetFavorite()

const templateCopy = computed(() => (template.value ? instantiateTemplate(template.value, can) : null))

const nameDialogProps = computed(() => {
  switch (nameDialog.value) {
    case 'new':
      return { title: 'New dashboard', description: 'An empty dashboard of your own; only you can see it until you share it.', initialName: 'New dashboard', submit: 'Create', notes: [] as string[] }
    case 'template':
      return {
        title: `Use “${template.value?.name}”`,
        description: 'A copy of the template, private to you, that you can change. The template itself stays as it is.',
        initialName: template.value?.name ?? '',
        submit: 'Create my copy',
        notes: (templateCopy.value?.omitted ?? []).map((o) => `Left out: ${o.title} - needs ${o.missing.join(', ')}.`),
      }
    case 'duplicate':
      return { title: 'Duplicate dashboard', description: 'A private copy; who it is shared with is not copied.', initialName: `Copy of ${name.value}`, submit: 'Duplicate', notes: [] }
    case 'rename':
      return { title: 'Rename dashboard', description: undefined, initialName: name.value, submit: 'Rename', notes: [] }
    default:
      return { title: '', description: undefined, initialName: '', submit: '', notes: [] }
  }
})

const nameBusy = computed(() => create.isPending.value || duplicate.isPending.value || update.isPending.value)

async function onName(value: { name: string; description: string }) {
  nameError.value = null
  try {
    if (nameDialog.value === 'new') {
      const created = await create.mutateAsync({ name: value.name, description: value.description, definition: { schemaVersion: 1, defaults: {}, refreshMinutes: 0, widgets: [] } })
      nameDialog.value = null
      await router.push({ name: 'dashboard-view', params: { id: created.id } })
      draft.begin(String(created.id), created.definition, created.name, created.description)
    } else if (nameDialog.value === 'template' && template.value && templateCopy.value) {
      const created = await create.mutateAsync({
        name: value.name,
        description: value.description,
        definition: templateCopy.value.definition,
        sourceTemplate: templateReference(template.value),
      })
      nameDialog.value = null
      await router.push({ name: 'dashboard-view', params: { id: created.id }, query: route.query })
    } else if (nameDialog.value === 'duplicate' && detail.data.value) {
      const created = await duplicate.mutateAsync({ id: detail.data.value.id, name: value.name })
      nameDialog.value = null
      await router.push({ name: 'dashboard-view', params: { id: created.id }, query: route.query })
    } else if (nameDialog.value === 'rename') {
      if (editing.value) {
        draft.rename(value.name)
        nameDialog.value = null
      } else if (detail.data.value && savedDefinition.value) {
        await update.mutateAsync({
          id: detail.data.value.id,
          name: value.name,
          description: detail.data.value.description,
          definition: savedDefinition.value,
          expectedRevision: detail.data.value.revision,
        })
        nameDialog.value = null
      }
    }
  } catch (error) {
    nameError.value = conflictOf(error) ? new Error('Somebody changed this dashboard; reload it and try again.') : error
  }
}

const deleting = ref(false)
const deleteError = ref<unknown>(null)
async function onDelete() {
  if (!detail.data.value) return
  deleteError.value = null
  try {
    await remove.mutateAsync(detail.data.value.id)
    deleting.value = false
    await router.push({ name: 'dashboard' })
  } catch (error) {
    deleteError.value = error
  }
}

const isDefault = computed(() =>
  dashboardId.value !== null ? !!detail.data.value?.isDefault : (list.data.value?.default.templateKey ?? null) === template.value?.key,
)

async function toggleDefault() {
  if (isDefault.value) await setDefault.mutateAsync({ dashboardId: null, templateKey: null })
  else if (dashboardId.value !== null) await setDefault.mutateAsync({ dashboardId: dashboardId.value, templateKey: null })
  else if (template.value) await setDefault.mutateAsync({ dashboardId: null, templateKey: template.value.key })
}

async function toggleFavorite() {
  if (!detail.data.value) return
  await setFavorite.mutateAsync({ id: detail.data.value.id, favorite: !detail.data.value.isFavorite })
}

async function loadOthers() {
  const all = await api.get<DashboardList>('/api/v1/dashboards', { scope: 'all' })
  others.value = all.others ?? []
}

// ------------------------------------------------------------ fullscreen

const page = ref<HTMLElement | null>(null)
const fullscreen = ref(false)
function onFullscreenChange() {
  fullscreen.value = document.fullscreenElement === page.value
}
async function toggleFullscreen() {
  if (document.fullscreenElement) await document.exitFullscreen()
  else await page.value?.requestFullscreen()
}

// ----------------------------------------------------- wizard, share, export

const wizard = ref<{ open: boolean; widget: WidgetInstance | null }>({ open: false, widget: null })

function openAdd() {
  wizard.value = { open: true, widget: null }
}
function openConfigure(widget: WidgetInstance) {
  wizard.value = { open: true, widget }
}
function onWizard(result: WizardResult) {
  if (result.id) {
    draft.updateWidget(result.id, { config: result.config, title: result.title, subtitle: result.subtitle, overrides: result.overrides }, 'Configure widget')
  } else {
    const id = draft.addWidget(result.type, result.config, result.title, result.overrides)
    if (id && result.subtitle) draft.updateWidget(id, { subtitle: result.subtitle }, 'Add widget')
  }
  wizard.value = { open: false, widget: null }
}

const sharing = ref(false)
const exporting = ref<{ kind: ExportKind; widgetIds: string[] | null } | null>(null)
function openExport(kind: ExportKind, widgetIds: string[] | null = null) {
  exporting.value = { kind, widgetIds }
}

const settings = ref(false)
function onSettings(value: { description: string; refreshMinutes: 0 | 5 | 15 | 30; defaults: DashboardLens }) {
  if (value.description !== draft.description) draft.setDescription(value.description)
  if (value.refreshMinutes !== draft.definition?.refreshMinutes) draft.setRefresh(value.refreshMinutes)
  if (JSON.stringify(value.defaults) !== JSON.stringify(draft.definition?.defaults ?? {})) draft.setDefaults(value.defaults)
  settings.value = false
}

// --------------------------------------------------------------- toolbar

const moreItems = computed<ActionMenuItem[]>(() => {
  const items: ActionMenuItem[] = []
  if (canCreate.value) items.push({ key: 'new', label: 'New dashboard…' })
  if (canCreate.value && (access.value?.canDuplicate || template.value)) items.push({ key: 'duplicate', label: template.value ? 'Make my own copy…' : 'Duplicate…' })
  if (canCreate.value) items.push({ key: 'import', label: 'Import from a file…' })
  if (access.value?.canEdit) items.push({ key: 'rename', label: 'Rename…', separated: true })
  items.push({ key: 'default', label: isDefault.value ? 'Stop opening this at /' : 'Open this at /', separated: !access.value?.canEdit })
  if (detail.data.value) items.push({ key: 'config', label: 'Download its structure (JSON)' })
  if (access.value?.canDelete) items.push({ key: 'delete', label: 'Delete…', danger: true, separated: true })
  return items
})

const exportItems = computed<ActionMenuItem[]>(() => {
  const exportsOk = can(Permission.DataExport)
  const hint = exportsOk ? undefined : 'Needs data.export'
  return [
    { key: 'png', label: 'Image (PNG)', disabled: !exportsOk, hint },
    { key: 'print', label: 'Print / Save as PDF', disabled: !exportsOk, hint },
    { key: 'csv', label: 'Data as CSV', disabled: !exportsOk, hint },
  ]
})

const importInput = ref<HTMLInputElement | null>(null)
const importError = ref<string | null>(null)
const importing = ref(false)

async function onImportFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  ;(event.target as HTMLInputElement).value = ''
  if (!file) return
  importError.value = null
  if (file.size > 512 * 1024) {
    importError.value = 'That file is larger than a dashboard can be.'
    return
  }
  importing.value = true
  try {
    const document = JSON.parse(await file.text())
    const migratedDoc = migrateDefinition(document?.definition)
    const created = await api.post<{ id: number }>('/api/v1/dashboards/import', {
      document: { ...document, definition: migratedDoc.definition },
      name: null,
    })
    void queryClient.invalidateQueries({ queryKey: ['dashboards', 'list'] })
    await router.push({ name: 'dashboard-view', params: { id: created.id } })
  } catch (error) {
    importError.value = error instanceof SyntaxError ? 'That file is not a dashboard file (it is not JSON).' : problemText(error)
  } finally {
    importing.value = false
  }
}

async function downloadConfig() {
  if (!detail.data.value) return
  const document = await api.get<unknown>(`/api/v1/dashboards/${detail.data.value.id}/config`)
  const blob = new Blob([JSON.stringify(document, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const link = window.document.createElement('a')
  link.href = url
  link.download = `${detail.data.value.name.replace(/[^\w.-]+/g, '-').toLowerCase()}.dashboard.json`
  window.document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 0)
}

function onMore(key: string) {
  if (key === 'new') nameDialog.value = 'new'
  else if (key === 'duplicate') nameDialog.value = template.value ? 'template' : 'duplicate'
  else if (key === 'import') importInput.value?.click()
  else if (key === 'rename') nameDialog.value = 'rename'
  else if (key === 'default') void toggleDefault()
  else if (key === 'config') void downloadConfig()
  else if (key === 'delete') {
    deleteError.value = null
    deleting.value = true
  }
}

const ownership = computed(() => {
  if (template.value) return { label: 'Template', description: 'Read-only. Make your own copy to change it.' }
  const d = detail.data.value
  if (!d) return null
  if (d.access.level === 'owner') return d.grantCount > 0 ? { label: 'Shared', description: `Yours, shared ${d.grantCount} ${d.grantCount === 1 ? 'time' : 'times'}.` } : { label: 'Private', description: 'Only you can see it.' }
  if (d.access.level === 'manager') return { label: `By ${d.owner.displayName}`, description: 'Opened as an administrator.' }
  return { label: `Shared by ${d.owner.displayName}`, description: d.access.level === 'editor' ? 'You can change it.' : 'You can view it.' }
})

const omitted = computed(() => templateView.value?.omitted ?? [])
const problems = computed(() => migrated.value?.problems ?? [])
</script>

<template>
  <div ref="page" class="flex flex-col gap-5" :class="fullscreen ? 'h-full overflow-auto bg-[var(--c-canvas)] p-5' : ''">
    <!-- Title, switcher and actions -->
    <header class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0 flex-[1_1_20rem]">
        <div class="flex flex-wrap items-center gap-2">
          <DashboardSwitcher
            :list="list.data.value"
            :others="others"
            :current-id="dashboardId"
            :current-template="template?.key ?? null"
            :label="name || 'Dashboard'"
            :can-manage="can(Permission.DashboardManage)"
            @open-dashboard="(id) => router.push({ name: 'dashboard-view', params: { id }, query: route.query })"
            @open-template="(key) => router.push({ name: 'dashboard-template', params: { key }, query: route.query })"
            @load-others="loadOthers"
          />
          <span
            v-if="ownership && !editing"
            class="rounded-full border px-2 py-0.5 text-2xs font-medium text-[var(--c-text-secondary)]"
            :title="ownership.description"
          >{{ ownership.label }}<span class="sr-only">: {{ ownership.description }}</span></span>
          <span v-if="editing" class="rounded-full bg-[var(--c-accent-subtle)] px-2 py-0.5 text-2xs font-semibold text-[var(--c-accent)]">Editing</span>
          <button
            v-if="detail.data.value && !editing"
            type="button"
            class="grid size-7 place-items-center rounded-[var(--radius-md)] text-base hover:bg-[var(--c-surface-hover)]"
            :class="detail.data.value.isFavorite ? 'text-[var(--c-warning-text)]' : 'text-[var(--c-text-muted)]'"
            :aria-pressed="detail.data.value.isFavorite"
            :title="detail.data.value.isFavorite ? 'Remove from favourites' : 'Add to favourites'"
            @click="toggleFavorite"
          >
            <span aria-hidden="true">{{ detail.data.value.isFavorite ? '★' : '☆' }}</span>
            <span class="sr-only">Favourite</span>
          </button>
        </div>
        <p v-if="description" class="mt-0.5 text-sm text-[var(--c-text-secondary)]">{{ description }}</p>
        <p class="mt-1 text-2xs text-[var(--c-text-muted)]">
          A <strong>binding</strong> is one number + SIM + handset combination
          (<code class="font-mono">MSISDN</code> + <code class="font-mono">IMSI</code> + <code class="font-mono">IMEI</code>). One subscriber can hold several.
          <template v-if="dataThrough"> Data through {{ formatDate(dataThrough) }}.</template>
          Refreshed {{ formatTime(refreshedAt.toISOString()) }}.
        </p>
      </div>

      <!-- View mode -->
      <div v-if="!editing" class="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="ghost" title="Fetch the aggregates again" @click="refresh">Refresh</Button>
        <Button v-if="canEdit" size="sm" @click="startEditing">Edit</Button>
        <Button v-else-if="template && canCreate" size="sm" @click="nameDialog = 'template'">Make my own copy</Button>
        <Button v-if="access?.canShare" size="sm" @click="sharing = true">Share</Button>
        <ActionMenu label="Export" text="Export" size="md" :items="exportItems" @select="(k) => openExport(k as ExportKind)" />
        <Button size="sm" variant="ghost" :title="fullscreen ? 'Leave full screen (Esc)' : 'Present full screen'" @click="toggleFullscreen">
          {{ fullscreen ? 'Exit full screen' : 'Full screen' }}
        </Button>
        <ActionMenu label="More dashboard actions" :items="moreItems" @select="onMore" />
      </div>

      <!-- Edit mode -->
      <div v-else class="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="primary" @click="openAdd">Add widget</Button>
        <Button size="sm" variant="ghost" :disabled="!draft.canUndo" :title="draft.undoLabel ? `Undo ${draft.undoLabel} (Ctrl+Z)` : 'Nothing to undo'" @click="draft.undo()">Undo</Button>
        <Button size="sm" variant="ghost" :disabled="!draft.canRedo" :title="draft.redoLabel ? `Redo ${draft.redoLabel} (Ctrl+Y)` : 'Nothing to redo'" @click="draft.redo()">Redo</Button>
        <Button size="sm" variant="ghost" @click="nameDialog = 'rename'">Rename</Button>
        <Button size="sm" variant="ghost" @click="settings = true">Settings</Button>
        <Button size="sm" variant="ghost" :disabled="update.isPending.value" @click="cancelEditing">Cancel</Button>
        <Button size="sm" variant="primary" :pending="update.isPending.value" :disabled="!draft.isDirty" @click="save">Save</Button>
      </div>
    </header>

    <input ref="importInput" type="file" accept="application/json,.json" class="hidden" @change="onImportFile" />

    <p v-if="importing" class="text-xs text-[var(--c-text-muted)]" aria-live="polite">Importing…</p>
    <p v-if="importError" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ importError }}</p>

    <div v-if="editing && (saveError || validationErrors.length)" class="rounded-[var(--radius-md)] border border-[var(--c-danger)] px-3 py-2 text-xs" role="alert">
      <p class="font-medium text-[var(--c-danger-text)]">Not saved. Your changes are still here.</p>
      <p v-if="saveError">{{ problemText(saveError) }}</p>
      <ul v-if="validationErrors.length" class="mt-1 list-disc pl-4">
        <li v-for="e in validationErrors.slice(0, 8)" :key="e">{{ e }}</li>
      </ul>
    </div>

    <p v-if="editing && columns !== 12" class="text-xs text-[var(--c-text-muted)]">
      Dragging and resizing need a wide screen. Each card's menu moves and resizes it here.
    </p>

    <p v-if="omitted.length" class="text-xs text-[var(--c-text-muted)]">
      {{ omitted.length }} {{ omitted.length === 1 ? 'widget of this template is' : 'widgets of this template are' }} left out:
      {{ omitted.map((o) => `${o.title} (needs ${o.missing.join(', ')})`).join('; ') }}.
    </p>
    <p v-if="problems.length" class="text-xs text-[var(--c-warning-text)]">
      {{ problems.join(' ') }}
    </p>

    <LensBar
      v-if="definition"
      :widgets="widgets"
      :view="view"
      :defaults="defaults"
      @count-by="onCountBy"
      @include-unknown="onIncludeUnknown"
      @range="onRange"
      @remove-filter="onRemoveFilter"
      @clear-filters="clearFilters"
    />

    <!-- States -->
    <div v-if="notFound" class="rounded-[var(--radius-lg)] border px-4 py-8 text-center text-sm">
      <p class="font-medium">This dashboard does not exist, or it is not shared with you.</p>
      <RouterLink :to="{ name: 'dashboard' }" class="mt-2 inline-block text-[var(--c-accent)] underline-offset-2 hover:underline">Open your default dashboard</RouterLink>
    </div>
    <div v-else-if="unknownTemplate" class="rounded-[var(--radius-lg)] border px-4 py-8 text-center text-sm">
      <p class="font-medium">There is no such template.</p>
      <RouterLink :to="{ name: 'dashboard' }" class="mt-2 inline-block text-[var(--c-accent)] underline-offset-2 hover:underline">Open your default dashboard</RouterLink>
    </div>
    <div v-else-if="detail.isError.value" class="rounded-[var(--radius-lg)] border px-4 py-8 text-center text-sm" role="alert">
      <p class="font-medium">The dashboard could not be loaded.</p>
      <p class="mt-1 text-xs text-[var(--c-text-muted)]">{{ problemText(detail.error.value) }}</p>
      <Button class="mt-3" size="sm" @click="detail.refetch()">Try again</Button>
    </div>
    <div v-else-if="!definition" class="grid grid-cols-2 gap-3 lg:grid-cols-6" aria-busy="true">
      <div v-for="i in 6" :key="i" class="h-[5.5rem] rounded-[var(--radius-lg)] bg-[var(--c-surface-sunken)]" />
    </div>
    <div v-else-if="!widgets.length" class="rounded-[var(--radius-lg)] border border-dashed px-4 py-10 text-center text-sm">
      <p class="font-medium">This dashboard has no widgets yet.</p>
      <p class="mt-1 text-xs text-[var(--c-text-muted)]">
        <template v-if="editing">Add the first one: every widget reads the data you are allowed to see.</template>
        <template v-else-if="canEdit">Edit it to add widgets.</template>
        <template v-else>Its owner has not added any.</template>
      </p>
      <Button v-if="editing" class="mt-3" variant="primary" size="sm" @click="openAdd">Add widget</Button>
    </div>

    <DashboardGrid
      v-if="definition && widgets.length && !notFound"
      :key="currentKey ?? 'none'"
      :widgets="widgets"
      :editable="editing"
      class="-mx-2.5"
      @commit="onCommit"
      @columns="(c) => (columns = c)"
    >
      <template #item="{ widget }">
        <WidgetHost
          :widget="widget"
          :defaults="defaults"
          :url-lens="urlLens"
          :mode="mode"
          :can-move-up="canMove(order, widget.id, 'up')"
          :can-move-down="canMove(order, widget.id, 'down')"
          :wide="columns === 12"
          @action="(key) => onWidgetAction(widget, key)"
        />
      </template>
    </DashboardGrid>

    <!-- Dialogs: mounted always and toggled, the pattern Modal needs (it opens on a change of `open`). -->
    <NameDialog
      :open="nameDialog !== null"
      :title="nameDialogProps.title"
      :description="nameDialogProps.description"
      :initial-name="nameDialogProps.initialName"
      :initial-description="nameDialog === 'rename' ? description : ''"
      :submit-label="nameDialogProps.submit"
      :notes="nameDialogProps.notes"
      :busy="nameBusy"
      :error="nameError"
      @close="nameDialog = null"
      @submit="onName"
    />
    <DeleteDialog
      :open="deleting"
      :name="detail.data.value?.name ?? ''"
      :shared="(detail.data.value?.grantCount ?? 0) > 0"
      :busy="remove.isPending.value"
      :error="deleteError"
      @close="deleting = false"
      @confirm="onDelete"
    />
    <ConfirmDialog
      :open="confirmCancel"
      title="Discard your changes?"
      message="Everything you changed since you started editing will be lost. The saved dashboard stays as it is."
      confirm-label="Discard changes"
      danger
      @close="confirmCancel = false"
      @confirm="() => { confirmCancel = false; draft.end() }"
    />
    <ConfirmDialog
      :open="pendingLeave !== null"
      title="Leave without saving?"
      message="You have unsaved changes to this dashboard. Leaving discards them; the saved dashboard stays as it is."
      confirm-label="Leave and discard"
      danger
      @close="pendingLeave = null"
      @confirm="leaveAnyway"
    />
    <ConflictDialog
      :open="conflict !== null"
      :updated-by="conflict?.updatedBy ?? null"
      :updated-at="conflict?.updatedAt ?? null"
      :can-copy="canCreate"
      @close="conflict = null"
      @reload="reloadSaved"
      @copy="saveConflictAsCopy"
    />
    <DashboardSettingsDialog
      :open="settings"
      :description="draft.description"
      :refresh-minutes="draft.definition?.refreshMinutes ?? 0"
      :defaults="draft.definition?.defaults ?? {}"
      :current="view"
      @close="settings = false"
      @apply="onSettings"
    />
    <AddWidgetDialog
      :open="wizard.open"
      :widget="wizard.widget"
      :defaults="defaults"
      :url-lens="urlLens"
      :widget-count="widgets.length"
      :max-widgets="list.data.value?.limits.maxWidgets ?? 24"
      @close="wizard = { open: false, widget: null }"
      @done="onWizard"
    />
    <ShareDialog
      v-if="detail.data.value"
      :open="sharing"
      :dashboard="detail.data.value"
      @close="sharing = false"
    />
    <ExportDialog
      :open="exporting !== null"
      :kind="exporting?.kind ?? 'png'"
      :widget-ids="exporting?.widgetIds ?? null"
      :dashboard-id="detail.data.value?.id ?? null"
      :name="name"
      :definition="definition"
      :lens="urlLens"
      @close="exporting = null"
    />
  </div>
</template>
