<script setup lang="ts">
import { ref } from 'vue'
import { lookupMsisdn, type LookupResult } from '@/api/dashboard'
import { ApiError } from '@/api/client'
import Card from '@/design-system/Card.vue'
import { formatDate, formatImei, formatMsisdn } from '@/lib/format'

const input = ref('')
const result = ref<LookupResult | null>(null)
const error = ref<unknown>(null)
const isLoading = ref(false)

/** Cancels an in-flight lookup when a new one starts. */
let controller: AbortController | null = null

async function search() {
  const value = input.value.trim()
  if (!value) return

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
  <div class="mx-auto max-w-4xl space-y-5">
    <header>
      <h1 class="text-xl font-semibold tracking-tight">Subscriber lookup</h1>
      <p class="mt-0.5 text-xs text-[var(--c-text-muted)]">
        Every device–SIM binding for one subscriber number, active and historical.
      </p>
    </header>

    <Card>
      <form @submit.prevent="search">
        <!--
          The help text is a sibling of this row, not a child of the field.

          It used to sit inside the flex item, and `items-end` then aligned the button with
          the bottom of the whole column - which is the bottom of the help paragraph, not of
          the input. Measured against this stylesheet, the button sat 26px low. A row that
          contains only the things that should line up is what keeps them lined up.
        -->
        <div class="flex flex-wrap items-end gap-3">
          <div class="min-w-[16rem] flex-1">
            <label for="msisdn" class="block text-xs font-medium text-[var(--c-text-secondary)]">
              Subscriber number
            </label>
            <input
              id="msisdn"
              v-model="input"
              inputmode="tel"
              autocomplete="off"
              placeholder="0913 123 4567"
              aria-describedby="msisdn-formats"
              class="mt-1.5 w-full rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 font-mono text-sm tabular placeholder:text-[var(--c-text-muted)]"
            />
          </div>
          <button
            type="submit"
            :disabled="isLoading || !input.trim()"
            class="rounded-[var(--radius-md)] bg-[var(--c-accent)] px-4 py-2 text-sm font-medium text-[var(--c-accent-text)] hover:bg-[var(--c-accent-hover)] disabled:opacity-50"
          >
            {{ isLoading ? 'Searching…' : 'Search' }}
          </button>
        </div>

        <!--
          The accepted forms are stated rather than left to be discovered. An operator pastes
          whatever they were given - a spreadsheet cell, a ticket, a chat message - and the one
          thing they should not have to do is convert it by hand for a number they are about to
          investigate.
        -->
        <p id="msisdn-formats" class="mt-1.5 text-2xs text-[var(--c-text-muted)]">
          National or international, with or without separators —
          <code class="font-mono">0913…</code>,
          <code class="font-mono">913…</code>,
          <code class="font-mono">+98 913…</code>,
          <code class="font-mono">0098913…</code>
          all find the same subscriber.
        </p>
      </form>

      <p class="mt-3 text-2xs text-[var(--c-text-muted)]">
        Lookups are audited. The number is sent in the request body, never in the URL, so it does not
        reach server access logs or browser history.
      </p>
    </Card>

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
        class="mx-4 mt-3 rounded-[var(--radius-md)] bg-[var(--c-warning-subtle)] px-3 py-2 text-xs"
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
                      ? 'bg-[var(--c-success-subtle)] text-[var(--c-success)]'
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
