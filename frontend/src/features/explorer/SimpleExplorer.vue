<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import SegmentedControl, { tabId, type SegmentOption } from '@/design-system/SegmentedControl.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import ImsiSearchPanel from '@/features/lookup/ImsiSearchPanel.vue'
import RelationshipGraph from '@/features/lookup/RelationshipGraph.vue'
import SubscriberBindings from '@/features/lookup/SubscriberBindings.vue'
import TimelineView from '@/features/timeline/TimelineView.vue'
import { formatImei, formatMsisdn } from '@/lib/format'
import { takePrefill } from '@/lib/prefill'
import EntityPanel from './EntityPanel.vue'
import { KIND_NAME, KIND_PERMISSION, readIdentifier, type KindChoice, type Resolved } from './identifier'

/**
 * The Explorer's Simple mode: one search box, and what is known about what it finds.
 *
 * Each view is the page it replaced, calling the same endpoint: Details is Subscriber lookup for a
 * number, IMSI search for a SIM and the entity summary for a handset; Relationships is the
 * Relationship explorer, dual-SIM pairs included; History is the timeline. A view sends its request
 * when it is first opened for an identifier, not before - each is an audited lookup, and the audit
 * log should say what somebody looked at, not what a page prefetched.
 *
 * Following a link - a neighbour, a SIM in a prefix list, a binding in the timeline - opens that
 * identifier here and keeps a Back trail. The trail is page state: identifiers never reach the
 * address bar, the history or a Referer.
 */
const route = useRoute()
const router = useRouter()
const { can } = useAuth()

type View = 'details' | 'relationships' | 'history'

const permitted = (kind: Resolved['kind']) => can(KIND_PERMISSION[kind])

/** The types offered: only those this user may look up, and ICCID, which nobody can yet. */
const kinds = computed<SegmentOption<KindChoice>[]>(() => [
  { value: 'auto', label: 'Auto', title: 'Tell the type from the length' },
  ...(permitted('msisdn') ? [{ value: 'msisdn' as const, label: 'MSISDN', title: 'Subscriber number' }] : []),
  ...(permitted('imsi') ? [{ value: 'imsi' as const, label: 'IMSI', title: 'SIM, complete or by prefix' }] : []),
  ...(permitted('imei') ? [{ value: 'imei' as const, label: 'IMEI', title: 'Handset' }] : []),
  { value: 'iccid', label: 'ICCID', disabled: "Not in the feed yet: the operator's files carry no ICCID." },
])

/** The type named in the address - from an old /lookup link, or this page's own last choice. */
function kindFromRoute(): KindChoice {
  const named = route.query['kind']
  return kinds.value.some((k) => k.value === named && !k.disabled) ? (named as KindChoice) : 'auto'
}

const choice = ref<KindChoice>(kindFromRoute())
const input = ref('')

// The type, not the identifier, is kept in the address, so Refresh and Back keep it.
watch(choice, (value) => {
  const kind = value === 'auto' ? undefined : value
  if (route.query['kind'] !== kind) void router.replace({ query: { ...route.query, kind } })
})
watch(
  () => route.query['kind'],
  () => {
    choice.value = kindFromRoute()
  },
)

const reading = computed(() => readIdentifier(input.value, choice.value))

/** Why the search cannot run, or null when it can. */
const problem = computed<string | null>(() => {
  const r = reading.value
  if (!r.ok) return r.problem
  return permitted(r.resolved.kind)
    ? null
    : `Looking up a ${KIND_NAME[r.resolved.kind]} needs the ${KIND_PERMISSION[r.resolved.kind]} permission.`
})

const tac = computed(() => {
  const r = reading.value
  return !r.ok && r.tac && can(Permission.DeviceView) ? r.tac : null
})

/** Identifiers opened, most recent last. Page state only - never the URL. */
const trail = ref<Resolved[]>([])
const current = computed(() => trail.value[trail.value.length - 1] ?? null)
const previous = computed(() => trail.value[trail.value.length - 2] ?? null)

const view = ref<View>('details')
/** The views opened for the current identifier: each loads once, when first shown. */
const opened = ref(new Set<View>(['details']))
/** The view a new identifier opens on: Details, unless it was handed over for another. */
let startOn: View = 'details'

watch(current, () => {
  view.value = complete.value ? startOn : 'details'
  opened.value = new Set([view.value])
  startOn = 'details'
})
watch(view, (v) => {
  if (!opened.value.has(v)) opened.value = new Set([...opened.value, v])
})

const same = (a: Resolved, b: Resolved) => a.kind === b.kind && a.value === b.value

function search() {
  const r = reading.value
  if (!r.ok || problem.value) return
  // The same search again does nothing: the views already show it, and each would be audited twice.
  if (current.value && same(current.value, r.resolved)) return
  trail.value = [r.resolved]
}

/** Opens an identifier reached from a view: a neighbour, a SIM of a prefix, a binding's member. */
function follow(identifier: string) {
  const r = readIdentifier(identifier, 'auto')
  if (!r.ok || !permitted(r.resolved.kind)) return
  if (current.value && same(current.value, r.resolved)) return
  trail.value = [...trail.value, r.resolved]
  show(r.resolved)
}

function back() {
  trail.value = trail.value.slice(0, -1)
  if (current.value) show(current.value)
}

/** Puts an identifier in the search box, as its own type, so the box always says what is shown. */
function show(resolved: Resolved) {
  input.value = resolved.value
  choice.value = permitted(resolved.kind) ? resolved.kind : 'auto'
}

function label(resolved: Resolved): string {
  if (resolved.kind === 'msisdn') return formatMsisdn(resolved.value)
  if (resolved.kind === 'imei') return formatImei(resolved.value)
  return resolved.prefix ? `${resolved.value}…` : resolved.value
}

const TITLE: Record<Resolved['kind'], string> = { msisdn: 'Subscriber number', imsi: 'SIM (IMSI)', imei: 'Handset (IMEI)' }

/** A 16- or 17-digit IMSI is shown for review, and is not an identifier the other views accept. */
const complete = computed(() => {
  const c = current.value
  return c !== null && !c.prefix && (c.kind !== 'imsi' || c.value.length === 15)
})

const views = computed<SegmentOption<View>[]>(() => {
  const unavailable = complete.value ? null : 'Only for a complete, 15-digit IMSI.'
  return [
    { value: 'details', label: 'Details' },
    { value: 'relationships', label: 'Relationships', disabled: unavailable },
    { value: 'history', label: 'History', disabled: unavailable },
  ]
})

// An identifier handed over by a dashboard's quick lookup: in memory, never the URL or history.
// Its relationships target lands on Relationships, as it did when that was a page of its own.
onMounted(() => {
  const forRelationships = takePrefill('explorer:relationships')
  const prefill = forRelationships ?? takePrefill('explorer')
  if (prefill === null) return
  if (forRelationships !== null) startOn = 'relationships'
  input.value = prefill
  search()
})
</script>

<template>
  <div class="flex flex-col gap-5">
    <Card>
      <form class="flex flex-col gap-3" @submit.prevent="search">
        <SegmentedControl v-model="choice" class="w-fit max-w-full" :options="kinds" label="Identifier type" />

        <div class="flex flex-wrap items-end gap-3">
          <div class="min-w-[min(18rem,100%)] flex-1">
            <label for="simple-identifier" class="block text-xs font-medium text-[var(--c-text-secondary)]">
              Subscriber number, IMSI or IMEI
            </label>
            <input
              id="simple-identifier"
              v-model="input"
              inputmode="tel"
              autocomplete="off"
              spellcheck="false"
              placeholder="0913 123 4567 · 43211… · 35004012…"
              aria-describedby="simple-identifier-help"
              :aria-invalid="input.trim() && problem ? 'true' : undefined"
              class="tabular mt-1.5 w-full rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 font-mono text-sm placeholder:font-sans placeholder:text-[var(--c-text-muted)]"
            />
          </div>
          <Button type="submit" variant="primary" size="lg" :disabled="!reading.ok || !!problem">Search</Button>
        </div>

        <p id="simple-identifier-help" class="text-2xs" role="status">
          <span v-if="problem" class="text-[var(--c-danger-text)]">
            {{ problem }}
            <RouterLink v-if="tac" :to="`/devices/${tac}`" class="text-[var(--c-accent)] hover:underline">
              Open the model
            </RouterLink>
          </span>
          <span v-else-if="reading.ok" class="text-[var(--c-text-secondary)]">{{ reading.note }}</span>
          <span v-else class="text-[var(--c-text-muted)]">
            Auto reads the type from the length: 10 digits is a number (also written 0…, +98… or 0098…), 14 a
            handset, 15 a SIM. To search SIMs by the start of their IMSI, choose IMSI. Lookups are audited, and the
            value is sent in the request body, never in the URL.
          </span>
        </p>
      </form>
    </Card>

    <template v-if="current">
      <div class="flex flex-wrap items-end justify-between gap-3">
        <div class="min-w-0">
          <p class="text-xs text-[var(--c-text-muted)]">
            {{ current.prefix ? 'SIMs whose IMSI begins' : TITLE[current.kind] }}
          </p>
          <p class="tabular mt-0.5 font-mono text-lg font-semibold break-all">{{ label(current) }}</p>
        </div>
        <Button v-if="previous" size="sm" variant="ghost" @click="back">← Back to {{ label(previous) }}</Button>
      </div>

      <!-- A prefix is many SIMs: its list is the whole answer, and choosing a SIM opens it. -->
      <ImsiSearchPanel v-if="current.prefix" :key="`prefix:${current.value}`" :term="current.value" @select="follow" />

      <template v-else>
        <SegmentedControl
          v-model="view"
          class="w-fit max-w-full"
          tabs
          panel-id="simple-explorer-panel"
          :options="views"
          label="Show"
        />

        <div
          id="simple-explorer-panel"
          :key="`${current.kind}:${current.value}`"
          role="tabpanel"
          :aria-labelledby="tabId('simple-explorer-panel', views.findIndex((v) => v.value === view))"
        >
          <div v-if="opened.has('details')" v-show="view === 'details'">
            <SubscriberBindings v-if="current.kind === 'msisdn'" :msisdn="current.value" />
            <ImsiSearchPanel v-else-if="current.kind === 'imsi'" :term="current.value" @select="follow" />
            <EntityPanel
              v-else
              :identifier="current.value"
              :depth="1"
              :data-through="null"
              :can-open-device="can(Permission.DeviceView)"
              :can-explore="false"
              :closable="false"
              @timeline="view = 'history'"
            />
          </div>

          <div v-if="complete && opened.has('relationships')" v-show="view === 'relationships'">
            <RelationshipGraph :identifier="current.value" @explore="follow" />
          </div>

          <div v-if="complete && opened.has('history')" v-show="view === 'history'">
            <TimelineView :identifier="current.value" :closable="false" @drill="follow" />
          </div>
        </div>
      </template>
    </template>
  </div>
</template>
