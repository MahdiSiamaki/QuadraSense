<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import DeviceImage from './DeviceImage.vue'
import DeviceTimelineChart from './DeviceTimelineChart.vue'
import DeviceIdentifiersPanel from './DeviceIdentifiersPanel.vue'
import DeviceImageUpload from './DeviceImageUpload.vue'
import { useDevice, useDeviceTimeline } from '@/api/devices'
import { networkAgeText, useModelNetworkAge } from '@/api/modelArrivals'
import { useAuth, Permission } from '@/features/auth/useAuth'
import { formatDate, formatFull, formatSignedDecimal } from '@/lib/format'

/**
 * One device model.
 *
 * The page is laid out to answer, in this order: **what is it**, **how many are there**, **what
 * can it do**, **what happened to it**, and only then **which identifiers are on it**. That last
 * one is the sensitive one and it is last on the page as well as behind its own permission —
 * a reader who came here to identify a handset has to scroll past the population figures to get
 * there, which is the right amount of friction for bulk exposure of personal identifiers.
 */

const route = useRoute()
const tac = computed(() => String(route.params['tac'] ?? ''))

const { can } = useAuth()

const device = useDevice(tac)
const detail = computed(() => device.data.value ?? null)
const age = useModelNetworkAge(tac)

/** The timeline's own range, defaulting to everything the change log covers. */
const from = ref('')
const to = ref('')
const range = computed(() => ({ from: from.value || null, to: to.value || null }))
const timeline = useDeviceTimeline(tac, range)

const title = computed(() => {
  const d = detail.value
  if (!d) return 'Device'
  return d.marketingName || d.model || d.brand || `TAC ${d.tac}`
})

/** The GSMA fields worth a row each, with the blanks dropped rather than rendered as dashes. */
const specification = computed(() => {
  const d = detail.value
  if (!d) return []

  return [
    { label: 'Manufacturer', value: d.manufacturer },
    { label: 'Vendor (curated)', value: d.vendor !== d.manufacturer ? d.vendor : null },
    { label: 'Brand', value: d.brand },
    { label: 'Model', value: d.model },
    { label: 'Marketing name', value: d.marketingName },
    { label: 'Device type', value: d.deviceType },
    { label: 'Operating system', value: d.operatingSystem },
    { label: 'OEM', value: d.oem },
    { label: 'Organisation ID', value: d.organisationId },
    { label: 'Allocated', value: d.allocationDate },
    { label: 'GSMA record updated', value: d.lastUpdatedDate },
    { label: 'SIM slots', value: d.simSlots },
    { label: 'IMEIs per device', value: d.imeiQuantity },
    { label: 'Bluetooth', value: d.bluetooth },
    { label: 'NFC', value: d.nfc },
    { label: 'WLAN', value: d.wlan },
  ].filter((row) => row.value !== null && row.value !== '')
})

/**
 * Capability, as three states.
 *
 * Null is rendered as "not stated" and not as "no", because the GSMA record leaving a field blank
 * is not the same as it saying the device lacks the feature — and for IMS emergency calling that
 * blank covers 94% of TACs. A page whose job is to report what the source says must not quietly
 * decide on its behalf.
 */
const capabilities = computed(() => {
  const c = detail.value?.capabilities
  if (!c) return []
  return [
    { label: 'LTE', state: c.lte },
    { label: '5G', state: c.fiveG },
    { label: 'eSIM', state: c.esim },
    { label: 'IMS emergency', state: c.imsEmergency },
  ]
})

const bandsOpen = ref(false)
</script>

<template>
  <div class="flex flex-col gap-5">
    <nav class="text-xs text-[var(--c-text-muted)]">
      <RouterLink to="/devices" class="hover:text-[var(--c-text)]">Devices</RouterLink>
      <span aria-hidden="true"> / </span>
      <span class="tabular">{{ tac }}</span>
    </nav>

    <AsyncBoundary
      :is-loading="device.isPending.value"
      :is-error="device.isError.value"
      :error="device.error.value"
      min-height="24rem"
      @retry="device.refetch()"
    >
      <template v-if="detail">
        <div class="flex flex-col gap-5">
          <!-- Identity. Picture, name, and the three things that name the model. -->
          <Card>
            <div class="flex flex-wrap items-start gap-5">
              <div class="size-28 shrink-0">
                <DeviceImage
                  :tac="detail.tac"
                  :has-image="detail.hasImage"
                  :name="detail.vendor ?? detail.brand"
                  :device-type="detail.deviceType"
                  size="lg"
                />
              </div>

              <div class="min-w-0 flex-1">
                <h1 class="text-xl font-semibold tracking-tight">{{ title }}</h1>
                <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
                  {{ detail.vendor ?? detail.manufacturer ?? 'Unknown manufacturer' }}
                  <template v-if="detail.deviceType"> · {{ detail.deviceType }}</template>
                  <template v-if="detail.operatingSystem"> · {{ detail.operatingSystem }}</template>
                </p>

                <div class="mt-2 flex flex-wrap items-center gap-2">
                  <span
                    class="tabular rounded-[var(--radius-sm)] bg-[var(--c-surface-sunken)] px-2 py-0.5 text-xs"
                  >
                    TAC {{ detail.tac }}
                  </span>
                  <span
                    class="rounded-[var(--radius-sm)] bg-[var(--c-surface-sunken)] px-2 py-0.5 text-2xs text-[var(--c-text-muted)]"
                  >
                    GSMA snapshot v{{ detail.tacVersionId }}
                  </span>
                </div>

                <!--
                  The honest explanation for a page full of blanks. A TAC can be on the network
                  and absent from the GSMA snapshot — measured at 0.20% of bindings — and without
                  this the reader concludes the system is broken rather than that the source is
                  silent.
                -->
                <p
                  v-if="!detail.knownToGsma"
                  class="mt-2 rounded-[var(--radius-md)] border border-[var(--c-warning)] px-2.5 py-1.5 text-xs"
                >
                  This code appears in subscriber data but is <strong>not in the active GSMA
                  snapshot</strong>, so there is no manufacturer or capability information for it.
                  The population figures below are still exact.
                </p>
              </div>

              <DeviceImageUpload
                v-if="can(Permission.DeviceImageManage)"
                :tac="detail.tac"
                :has-image="detail.hasImage"
                :source-note="detail.imageSourceNote"
                :updated-at="detail.imageUpdatedAt"
                @changed="device.refetch()"
              />
            </div>
          </Card>

          <!-- Population. -->
          <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <Card>
              <p class="text-2xs text-[var(--c-text-muted)]">Active bindings</p>
              <p class="tabular mt-1 text-xl font-semibold">
                {{ formatFull(detail.population.bindings) }}
              </p>
              <p class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
                number + SIM + handset
              </p>
            </Card>
            <Card>
              <p class="text-2xs text-[var(--c-text-muted)]">Handsets</p>
              <p class="tabular mt-1 text-xl font-semibold">
                {{ formatFull(detail.population.handsets) }}
              </p>
              <p class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
                distinct IMEIs, estimated
              </p>
            </Card>
            <Card>
              <p class="text-2xs text-[var(--c-text-muted)]">SIMs</p>
              <p class="tabular mt-1 text-xl font-semibold">
                {{ formatFull(detail.population.sims) }}
              </p>
              <p class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
                distinct IMSIs, estimated
              </p>
            </Card>
            <Card>
              <p class="text-2xs text-[var(--c-text-muted)]">Numbers</p>
              <p class="tabular mt-1 text-xl font-semibold">
                {{ formatFull(detail.population.subscribers) }}
              </p>
              <p class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
                distinct MSISDNs, estimated
              </p>
            </Card>
          </div>

          <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
            <div class="flex flex-col gap-5">
              <!-- Movement. -->
              <Card title="Daily movement" subtitle="Add and remove events, not devices.">
                <template #actions>
                  <div class="flex flex-wrap items-end gap-2">
                    <input
                      v-model="from"
                      type="date"
                      aria-label="From"
                      :min="timeline.data.value?.earliestAvailable ?? undefined"
                      :max="timeline.data.value?.latestAvailable ?? undefined"
                      class="tabular rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
                    />
                    <input
                      v-model="to"
                      type="date"
                      aria-label="To"
                      :min="timeline.data.value?.earliestAvailable ?? undefined"
                      :max="timeline.data.value?.latestAvailable ?? undefined"
                      class="tabular rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
                    />
                    <button
                      v-if="from || to"
                      type="button"
                      class="rounded-[var(--radius-md)] px-1.5 py-1 text-2xs text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)]"
                      @click="from = ''; to = ''"
                    >
                      clear
                    </button>
                  </div>
                </template>

                <AsyncBoundary
                  :is-loading="timeline.isPending.value"
                  :is-error="timeline.isError.value"
                  :error="timeline.error.value"
                  min-height="12rem"
                  @retry="timeline.refetch()"
                >
                  <DeviceTimelineChart
                    v-if="timeline.data.value"
                    :points="timeline.data.value.points"
                  />
                </AsyncBoundary>

                <p class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
                  These are <em>events</em>. One SIM moving between two handsets of this model
                  contributes one add and one remove and changes the population by nothing. The
                  population figures are above.
                  <span v-if="timeline.data.value">
                    Query cost {{ formatFull(timeline.data.value.timing.elapsedMs) }} ms.
                  </span>
                </p>
              </Card>

              <DeviceIdentifiersPanel
                v-if="can(Permission.DeviceIdentifiers)"
                :tac="detail.tac"
              />

              <Card v-else title="Identifiers">
                <p class="text-sm text-[var(--c-text-secondary)]">
                  Listing the IMEIs, SIMs and numbers bound to this model needs the
                  <code class="text-xs">device.identifiers</code> permission. It is
                  separate from the per-handset lookup because it is bulk: this model covers
                  {{ formatFull(detail.population.handsets) }} handsets.
                </p>
              </Card>
            </div>

            <div class="flex flex-col gap-5">
              <!-- Growth, relative to the network. -->
              <Card title="Population change" subtitle="Against the first delivery.">
                <dl class="flex flex-col gap-2 text-sm">
                  <div class="flex items-baseline justify-between gap-3">
                    <dt class="text-[var(--c-text-secondary)]">At first delivery</dt>
                    <dd class="tabular font-medium">
                      {{ formatFull(detail.population.bindingsAtStart) }}
                    </dd>
                  </div>
                  <div class="flex items-baseline justify-between gap-3">
                    <dt class="text-[var(--c-text-secondary)]">Now</dt>
                    <dd class="tabular font-medium">
                      {{ formatFull(detail.population.bindings) }}
                    </dd>
                  </div>
                  <div class="flex items-baseline justify-between gap-3 border-t pt-2">
                    <dt class="text-[var(--c-text-secondary)]">Change</dt>
                    <!-- No bindings at the start: there is no percentage of zero to show or compare. -->
                    <dd
                      v-if="detail.population.changePercent === null"
                      class="text-right text-xs text-[var(--c-text-muted)]"
                    >
                      New since the first delivery
                    </dd>
                    <dd
                      v-else
                      class="tabular font-medium"
                      :style="{
                        color:
                          Math.round(detail.population.changePercent * 100) >= 0
                            ? 'var(--viz-3)'
                            : 'var(--viz-6)',
                      }"
                    >
                      {{ formatSignedDecimal(detail.population.changePercent, 2) }}%
                    </dd>
                  </div>
                  <div
                    v-if="detail.population.vsNetworkPoints !== null"
                    class="flex items-baseline justify-between gap-3"
                  >
                    <dt class="text-[var(--c-text-secondary)]">Against the network</dt>
                    <dd
                      class="tabular font-medium"
                      :style="{
                        color:
                          Math.round(detail.population.vsNetworkPoints * 10) >= 0
                            ? 'var(--viz-3)'
                            : 'var(--viz-6)',
                      }"
                    >
                      {{ formatSignedDecimal(detail.population.vsNetworkPoints, 1) }} pts
                    </dd>
                  </div>
                </dl>

                <p
                  v-if="detail.population.vsNetworkPoints !== null"
                  class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]"
                >
                  The whole active population fell over this span, so almost every model's absolute
                  change is negative. The second figure is the one that says whether this model
                  gained or lost <em>share</em>.
                </p>
                <p v-else class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
                  This model had no bindings at the first delivery, so there is no change to
                  measure or to compare with the network's.
                </p>
              </Card>

              <!-- Capability. -->
              <Card title="Capability" subtitle="As the GSMA record states it.">
                <ul class="flex flex-col gap-1.5 text-sm">
                  <li
                    v-for="c in capabilities"
                    :key="c.label"
                    class="flex items-center justify-between gap-3"
                  >
                    <span class="text-[var(--c-text-secondary)]">{{ c.label }}</span>
                    <span
                      class="rounded-[var(--radius-sm)] px-1.5 py-0.5 text-2xs font-medium"
                      :class="
                        c.state === true
                          ? 'bg-[var(--c-success-subtle)] text-[var(--c-success-text)]'
                          : c.state === false
                            ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text-secondary)]'
                            : 'bg-[var(--c-surface-sunken)] text-[var(--c-text-muted)]'
                      "
                    >
                      {{ c.state === true ? 'Yes' : c.state === false ? 'No' : 'Not stated' }}
                    </span>
                  </li>
                </ul>
                <p class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
                  “Not stated” is the GSMA record saying nothing, not the device saying no — it
                  covers 94% of TACs for IMS emergency. VoLTE is absent because the dataset does
                  not contain it.
                </p>
              </Card>

              <!-- Everything else the record says. -->
              <Card title="GSMA record">
                <dl class="flex flex-col gap-1.5 text-sm">
                  <div
                    v-for="row in specification"
                    :key="row.label"
                    class="flex items-baseline justify-between gap-3"
                  >
                    <dt class="shrink-0 text-xs text-[var(--c-text-secondary)]">
                      {{ row.label }}
                    </dt>
                    <dd class="min-w-0 truncate text-right" :title="row.value ?? undefined">
                      {{ row.value }}
                    </dd>
                  </div>
                </dl>

                <div v-if="detail.bands" class="mt-3 border-t pt-2">
                  <button
                    type="button"
                    class="text-xs text-[var(--c-accent)] hover:underline"
                    @click="bandsOpen = !bandsOpen"
                  >
                    {{ bandsOpen ? 'Hide' : 'Show' }} radio bands
                  </button>
                  <!-- Behind a disclosure because this field runs to several hundred characters. -->
                  <p
                    v-if="bandsOpen"
                    class="mt-2 max-h-48 overflow-y-auto break-words text-2xs leading-relaxed text-[var(--c-text-secondary)]"
                  >
                    {{ detail.bands }}
                  </p>
                </div>
              </Card>

              <!-- Where to go next. -->
              <Card title="Related">
                <ul class="flex flex-col gap-1.5 text-sm">
                  <li v-if="can(Permission.LookupImsi)">
                    <RouterLink to="/lookup/imsi" class="text-[var(--c-accent)] hover:underline">
                      Search by IMSI
                    </RouterLink>
                    <span class="text-2xs text-[var(--c-text-muted)]">
                      — one SIM's handsets and history
                    </span>
                  </li>
                  <li v-if="can(Permission.LookupSubscriber)">
                    <RouterLink to="/lookup" class="text-[var(--c-accent)] hover:underline">
                      Subscriber lookup
                    </RouterLink>
                    <span class="text-2xs text-[var(--c-text-muted)]">
                      — one number's SIMs and handsets
                    </span>
                  </li>
                  <li v-if="can(Permission.ImportView)">
                    <RouterLink to="/imports" class="text-[var(--c-accent)] hover:underline">
                      Import Center
                    </RouterLink>
                    <span class="text-2xs text-[var(--c-text-muted)]">
                      — where GSMA snapshot v{{ detail.tacVersionId }} came from
                    </span>
                  </li>
                </ul>

                <!-- First seen is the first daily file that named any of its handsets (analytics
                     migration 025): not the earliest last change among its current bindings, which
                     makes a model whose early bindings were replaced look newer than it is. -->
                <p v-if="age.data.value" class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
                  <template v-if="age.data.value.inDump">
                    Seen in the initial dump, before the first daily file: network age
                    {{ networkAgeText(age.data.value.networkAgeDays, true) }}.
                  </template>
                  <template v-else>
                    First seen in the daily file of {{ formatDate(age.data.value.firstSeen) }}: network age
                    {{ networkAgeText(age.data.value.networkAgeDays, false) }}.
                  </template>
                  Network age is time in this data, not the age of any handset.
                  <template v-if="detail.population.lastSeen"> Last named {{ formatDate(detail.population.lastSeen) }}.</template>
                </p>
                <p
                  v-else-if="detail.population.firstSeen || detail.population.lastSeen"
                  class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]"
                >
                  Daily files last named this model on {{ formatDate(detail.population.lastSeen) }}.
                </p>
                <p v-else class="mt-3 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
                  No daily file has ever named a binding of this model. It is present because the
                  initial dump listed it.
                </p>
              </Card>
            </div>
          </div>
        </div>
      </template>
    </AsyncBoundary>
  </div>
</template>
