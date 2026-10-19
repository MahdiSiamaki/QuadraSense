<script setup lang="ts">
import { ref, watch } from 'vue'
import { lookupMsisdn, type LookupResult } from '@/api/dashboard'
import { ApiError } from '@/api/client'
import Card from '@/design-system/Card.vue'
import { formatDate, formatImei, formatMsisdn } from '@/lib/format'

/**
 * Every device-SIM binding for one subscriber number, active and historical.
 *
 * Was the Subscriber lookup page; it is the Explorer's details for a number now, and the number
 * comes from the Explorer's search box. The request is the same audited lookup, sent in the body,
 * once each time the number changes.
 */
const props = defineProps<{ msisdn: string }>()

const result = ref<LookupResult | null>(null)
const error = ref<unknown>(null)
const isLoading = ref(false)

/** Cancels an in-flight lookup when a new one starts. */
let controller: AbortController | null = null

async function search(value: string) {
  controller?.abort()
  controller = new AbortController()

  isLoading.value = true
  error.value = null

  try {
    result.value = await lookupMsisdn(value, controller.signal)
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') return
    error.value = e
    result.value = null
  } finally {
    isLoading.value = false
  }
}

watch(() => props.msisdn, (value) => void search(value), { immediate: true })

function errorMessage(e: unknown): string {
  if (e instanceof ApiError) {
    const fields = e.fieldErrors.map((f) => f.message)
    if (fields.length) return fields.join(' ')
    return e.problem?.title ?? 'Lookup failed'
  }
  return 'Lookup failed'
}

function correlationId(e: unknown): string | null {
  return e instanceof ApiError ? e.correlationId : null
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <p v-if="isLoading && !result" class="text-sm text-[var(--c-text-muted)]" role="status">Looking up…</p>

    <div
      v-if="error"
      class="rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">{{ errorMessage(error) }}</p>
      <p v-if="correlationId(error)" class="mt-1 font-mono text-2xs text-[var(--c-text-muted)]">
        Reference: {{ correlationId(error) }}
      </p>
    </div>

    <Card
      v-if="result"
      :title="`${result.count} binding${result.count === 1 ? '' : 's'}`"
      :subtitle="result.wellFormed ? formatMsisdn(result.msisdn) : `${result.msisdn} — unusual length, shown for review`"
      flush
    >
      <!-- Masking is a server decision; the page reports it rather than performing it. -->
      <p
        v-if="result.identifiers === 'Masked'"
        class="mx-4 my-3 rounded-[var(--radius-md)] bg-[var(--c-warning-subtle)] px-3 py-2 text-xs"
      >
        SIM and handset identifiers are shown masked. Your account does not hold
        <code class="font-mono">identifier.reveal</code>, so the server redacted them before sending.
      </p>

      <div v-if="result.count === 0" class="px-4 py-8 text-center text-sm text-[var(--c-text-secondary)]">
        No bindings found for this number.
      </div>

      <div v-else class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b text-left text-xs text-[var(--c-text-muted)]">
              <th class="px-4 py-2 font-medium">Status</th>
              <th class="px-4 py-2 font-medium">IMEI</th>
              <th class="px-4 py-2 font-medium">Device</th>
              <th class="px-4 py-2 font-medium">Type</th>
              <th class="px-4 py-2 font-medium">IMSI</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="b in result.bindings"
              :key="`${b.imsi}-${b.imei}`"
              class="border-b last:border-0 hover:bg-[var(--c-surface-hover)]"
            >
              <td class="px-4 py-2.5">
                <span
                  class="inline-flex items-center rounded-full px-2 py-0.5 text-2xs font-medium"
                  :class="
                    b.isActive
                      ? 'bg-[var(--c-success-subtle)] text-[var(--c-success-text)]'
                      : 'bg-[var(--c-surface-sunken)] text-[var(--c-text-muted)]'
                  "
                >
                  {{ b.isActive ? 'Active' : 'Inactive' }}
                </span>
                <!--
                  A binding no daily file has ever mentioned is active only because the initial
                  dump listed it and nothing has removed it. The dump covers a 30-day window, not
                  an instant, so it can list several handsets one subscriber used that month -
                  which is why one SIM can show more than one Active row. Saying so turns a row
                  that looks like broken data into one that is merely unconfirmed.
                -->
                <span
                  v-if="b.isActive && !b.lastChangeDate"
                  class="mt-0.5 block text-2xs text-[var(--c-text-muted)]"
                  title="Listed in the initial dump (2025-12-27 to 2026-01-25) and never mentioned by a daily file since. Not confirmed, and not contradicted."
                >
                  from initial dump, unconfirmed
                </span>
                <span
                  v-else-if="b.lastChangeDate"
                  class="mt-0.5 block text-2xs text-[var(--c-text-muted)]"
                >
                  {{ b.isActive ? 'confirmed' : 'removed' }} {{ formatDate(b.lastChangeDate) }}
                </span>
              </td>
              <td class="px-4 py-2.5 font-mono text-xs tabular">
                {{ formatImei(b.imei) }}
              </td>
              <td class="px-4 py-2.5">
                <span v-if="b.marketingName">{{ b.marketingName }}</span>
                <span v-else class="text-[var(--c-text-muted)] italic">unknown device</span>
                <span v-if="b.manufacturer" class="ml-1.5 text-xs text-[var(--c-text-muted)]">
                  {{ b.manufacturer }}
                </span>
              </td>
              <td class="px-4 py-2.5 text-[var(--c-text-secondary)]">{{ b.deviceType ?? '—' }}</td>
              <td class="px-4 py-2.5 font-mono text-xs tabular text-[var(--c-text-secondary)]">
                {{ b.imsi }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </Card>
  </div>
</template>
