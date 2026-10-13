<script setup lang="ts">
/**
 * Exporting a dashboard (ADR-016 §11): an image (PNG), a printout the browser can save as PDF, or the
 * data as CSV.
 *
 * Nothing is exported from what the browser happens to hold. The server prepares every export: it
 * checks access, data.export and each widget's own permission, loads each permitted aggregate itself
 * and audits it. An image or printout is then drawn from that data, in a light report style, by the
 * same widget components; a CSV is written by the server, with formula neutralisation.
 *
 * What cannot be included is said before anything is produced - a widget the person may not use, a
 * source not ready, a risk list that runs only on request - and the person chooses to leave it out
 * or show it as a card that says why. Never a silently incomplete export.
 */
import { computed, nextTick, ref, watch } from 'vue'
import { toBlob } from 'html-to-image'
import { downloadWidgetCsv, prepareSnapshot, type DashboardSnapshot } from '@/api/dashboards'
import { saveFile } from '@/api/client'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { clearChartColorCache } from '@/lib/chart-colors'
import { getWidget } from '../registry'
import { readingOrder } from '../composables/layout'
import type { DashboardDefinition, DashboardLens } from '../types/definition'
import { problemText } from './apiProblem'
import ReportView from './ReportView.vue'

export type ExportKind = 'png' | 'print' | 'csv'

const props = defineProps<{
  open: boolean
  kind: ExportKind
  /** Preselected widgets; all when null. */
  widgetIds: string[] | null
  dashboardId: number | null
  name: string
  definition: DashboardDefinition | null
  lens: DashboardLens
}>()

const emit = defineEmits<{ close: [] }>()

type Phase = 'choose' | 'preparing' | 'review' | 'producing' | 'done'

const kind = ref<ExportKind>('png')
const phase = ref<Phase>('choose')
const chosen = ref<string[]>([])
const snapshot = ref<DashboardSnapshot | null>(null)
const showUnavailable = ref(false)
const error = ref<unknown>(null)
const message = ref<string | null>(null)
let abort: AbortController | null = null

const widgets = computed(() => (props.definition ? readingOrder(props.definition.widgets) : []))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    kind.value = props.kind
    phase.value = 'choose'
    chosen.value = props.widgetIds ?? widgets.value.map((w) => w.id)
    snapshot.value = null
    showUnavailable.value = false
    error.value = null
    message.value = null
  },
)

const KIND_OPTIONS = [
  { value: 'png' as const, label: 'Image (PNG)' },
  { value: 'print' as const, label: 'Print / Save as PDF' },
  { value: 'csv' as const, label: 'CSV' },
]

const csvable = computed(() => new Set(widgets.value.filter((w) => getWidget(w.type)?.exports.csv).map((w) => w.id)))
const selectable = computed(() => (kind.value === 'csv' ? widgets.value.filter((w) => csvable.value.has(w.id)) : widgets.value))

function toggle(id: string, on: boolean) {
  chosen.value = on ? [...new Set([...chosen.value, id])] : chosen.value.filter((x) => x !== id)
}

const selected = computed(() => chosen.value.filter((id) => selectable.value.some((w) => w.id === id)))

function title(id: string) {
  const w = widgets.value.find((x) => x.id === id)
  return w?.title ?? (w ? (getWidget(w.type)?.name ?? w.type) : id)
}

const unavailable = computed(() => (snapshot.value?.widgets ?? []).filter((w) => w.status !== 'ok' && selected.value.includes(w.id)))
const includable = computed(() => (snapshot.value?.widgets ?? []).filter((w) => w.status === 'ok' && selected.value.includes(w.id)))

async function prepare() {
  if (!props.definition || !selected.value.length) return
  error.value = null
  message.value = null
  abort?.abort()
  abort = new AbortController()

  if (kind.value === 'csv') {
    phase.value = 'producing'
    try {
      const file = await downloadWidgetCsv(
        { dashboardId: props.dashboardId, name: props.name, definition: props.definition, lens: props.lens, widgetIds: selected.value },
        abort.signal,
      )
      saveFile(file, selected.value.length === 1 ? `${slug(props.name)}-${slug(title(selected.value[0]!))}.csv` : `${slug(props.name)}.zip`)
      message.value = selected.value.length === 1 ? 'The CSV was downloaded.' : 'A ZIP with one CSV per widget was downloaded; about.txt says what each file is.'
      phase.value = 'done'
    } catch (e) {
      error.value = e
      phase.value = 'choose'
    }
    return
  }

  phase.value = 'preparing'
  try {
    snapshot.value = await prepareSnapshot(
      {
        dashboardId: props.dashboardId,
        name: props.name,
        definition: props.definition,
        lens: props.lens,
        widgetIds: selected.value,
        purpose: kind.value,
      },
      abort.signal,
    )
    phase.value = 'review'
  } catch (e) {
    error.value = e
    phase.value = 'choose'
  }
}

// ---------------------------------------------------------------- rendering

const rendering = ref(false)
const pages = ref<string[][]>([])
const host = ref<HTMLElement | null>(null)

/** Pages of widgets short enough for one PNG each: a canvas taller than ~16k px fails in browsers. */
function paginate(ids: string[]): string[][] {
  const MAX_PAGE_PX = 6500
  const out: string[][] = []
  let page: string[] = []
  let height = 0
  for (const w of readingOrder(widgets.value.filter((x) => ids.includes(x.id)))) {
    const h = Math.max(3, w.layout.h) * 40 + 80
    if (page.length && height + h > MAX_PAGE_PX) {
      out.push(page)
      page = []
      height = 0
    }
    page.push(w.id)
    // Cards side by side share a row; counting each full height errs towards smaller pages.
    height += w.layout.w >= 12 ? h : h / 2
  }
  if (page.length) out.push(page)
  return out
}

/** The report is drawn light, whatever the app's theme, then the theme is put back. */
async function withLightTheme<T>(work: () => Promise<T>): Promise<T> {
  const root = document.documentElement
  const before = root.dataset['theme']
  root.dataset['themeSwitching'] = ''
  root.dataset['theme'] = 'light'
  clearChartColorCache()
  try {
    return await work()
  } finally {
    if (before) root.dataset['theme'] = before
    else delete root.dataset['theme']
    clearChartColorCache()
    void document.body?.offsetWidth
    delete root.dataset['themeSwitching']
  }
}

/** Charts draw on mount and on their container's first size; give them two frames and a moment. */
async function settled() {
  await nextTick()
  await new Promise((r) => setTimeout(r, 400))
}

function slug(text: string) {
  return text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'dashboard'
}

async function produce() {
  if (!snapshot.value) return
  const ids = (showUnavailable.value ? selected.value : includable.value.map((w) => w.id))
  if (!ids.length) {
    message.value = 'Nothing left to export.'
    return
  }
  phase.value = 'producing'
  error.value = null
  try {
    await withLightTheme(async () => {
      if (kind.value === 'print') {
        pages.value = [ids]
        rendering.value = true
        await settled()
        await new Promise<void>((resolve) => {
          window.addEventListener('afterprint', () => resolve(), { once: true })
          window.print()
          // Some browsers do not fire afterprint for a cancelled dialog; do not wait for ever.
          setTimeout(resolve, 60_000)
        })
      } else {
        pages.value = paginate(ids)
        rendering.value = true
        await settled()
        const nodes = [...(host.value?.querySelectorAll<HTMLElement>('.report-view') ?? [])]
        for (const [index, node] of nodes.entries()) {
          const blob = await toBlob(node, { pixelRatio: 2, backgroundColor: '#ffffff', cacheBust: true })
          if (!blob) throw new Error('The browser could not draw the image.')
          const suffix = nodes.length > 1 ? `-part${index + 1}` : ''
          saveFile({ blob, filename: null }, `${slug(props.name)}${suffix}.png`)
        }
      }
    })
    message.value =
      kind.value === 'print'
        ? 'Sent to the print dialog. Choose “Save as PDF” there to keep a file.'
        : pages.value.length > 1
          ? `Saved as ${pages.value.length} images: the dashboard is taller than one image can be.`
          : 'The image was saved.'
    phase.value = 'done'
  } catch (e) {
    error.value = e
    phase.value = 'review'
  } finally {
    rendering.value = false
  }
}

function close() {
  abort?.abort()
  emit('close')
}
</script>

<template>
  <Modal
    :open="open"
    size="lg"
    title="Export"
    :description="kind === 'csv' ? 'The data behind the chosen widgets, written by the server.' : 'Prepared by the server from the data you may see, then drawn as a report.'"
    :busy="phase === 'preparing' || phase === 'producing'"
    @close="close"
  >
    <div class="flex flex-col gap-4 text-sm">
      <SegmentedControl
        v-if="phase === 'choose'"
        v-model="kind"
        :options="KIND_OPTIONS"
        label="Export as"
      />

      <template v-if="phase === 'choose'">
        <fieldset class="flex flex-col gap-1">
          <legend class="mb-1 text-xs font-medium text-[var(--c-text-secondary)]">Widgets</legend>
          <label v-for="w in selectable" :key="w.id" class="inline-flex items-center gap-2">
            <input
              type="checkbox"
              class="size-4 accent-[var(--c-accent)]"
              :checked="chosen.includes(w.id)"
              @change="toggle(w.id, ($event.target as HTMLInputElement).checked)"
            />
            <span>{{ title(w.id) }}</span>
          </label>
          <p v-if="kind === 'csv' && selectable.length < widgets.length" class="mt-1 text-2xs text-[var(--c-text-muted)]">
            Launchers have no data, and risk lists and Explorer queries export from their own menus after a run.
          </p>
        </fieldset>
        <p class="text-2xs text-[var(--c-text-muted)]">
          <template v-if="kind === 'csv'">Each file says what it holds - the rows on the card, its top N, or the full result - in its name and in about.txt. A capped file is never called full.</template>
          <template v-else>The report is light whatever your theme, and every card says what its data is as of.</template>
        </p>
      </template>

      <p v-else-if="phase === 'preparing'" aria-live="polite">The server is preparing the data…</p>

      <template v-else-if="phase === 'review' && snapshot">
        <p>{{ includable.length }} of {{ selected.length }} {{ selected.length === 1 ? 'widget is' : 'widgets are' }} ready.</p>
        <div v-if="unavailable.length" class="flex flex-col gap-2 rounded-[var(--radius-md)] border px-3 py-2">
          <p class="text-xs font-medium">These cannot be included as they are:</p>
          <ul class="space-y-1 text-xs">
            <li v-for="w in unavailable" :key="w.id">
              <strong>{{ title(w.id) }}</strong>
              - {{ w.reason ?? (w.status === 'forbidden' ? 'needs a permission you do not have' : w.status === 'interactive' ? 'runs only on request' : 'not available') }}
            </li>
          </ul>
          <label class="inline-flex items-center gap-2 text-xs">
            <input v-model="showUnavailable" type="checkbox" class="size-4 accent-[var(--c-accent)]" />
            Show them as cards that say why, instead of leaving them out
          </label>
        </div>
      </template>

      <p v-else-if="phase === 'producing'" aria-live="polite">{{ kind === 'csv' ? 'Writing the file…' : 'Drawing the report…' }}</p>

      <p v-if="message" class="text-xs text-[var(--c-success-text)]" role="status">{{ message }}</p>
      <p v-if="error" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ problemText(error) }}</p>
    </div>

    <template #actions>
      <Button variant="ghost" :disabled="phase === 'preparing' || phase === 'producing'" @click="close">{{ phase === 'done' ? 'Close' : 'Cancel' }}</Button>
      <Button v-if="phase === 'choose'" variant="primary" :disabled="!selected.length || !definition" @click="prepare">
        {{ kind === 'csv' ? 'Download' : 'Prepare' }}
      </Button>
      <Button v-else-if="phase === 'review'" variant="primary" :disabled="!includable.length && !showUnavailable" @click="produce">
        {{ kind === 'print' ? 'Print' : 'Save image' }}
      </Button>
    </template>
  </Modal>

  <!-- Off screen while drawing; for print, the only thing @media print shows. -->
  <Teleport to="body">
    <div
      v-if="rendering && snapshot && definition"
      ref="host"
      class="print-host pointer-events-none fixed top-0 left-[-20000px] flex flex-col gap-8 bg-white"
      aria-hidden="true"
      data-theme="light"
    >
      <ReportView
        v-for="(page, index) in pages"
        :key="index"
        :name="name"
        :snapshot="snapshot"
        :definition="definition"
        :url-lens="lens"
        :widget-ids="page"
        :show-unavailable="showUnavailable"
        :page="{ index: index + 1, count: pages.length }"
      />
    </div>
  </Teleport>
</template>
