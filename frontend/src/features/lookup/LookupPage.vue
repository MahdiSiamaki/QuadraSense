<script setup lang="ts">
import { ref } from 'vue'
import { lookupMsisdn, type LookupResult } from '@/api/dashboard'
import { ApiError } from '@/api/client'
import Card from '@/design-system/Card.vue'
import { formatImei, formatMsisdn } from '@/lib/format'

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
      <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Subscriber lookup</h1>
      <p class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
        Every device–SIM binding for one subscriber number, active and historical.
      </p>
    </header>

    <Card>
      <form class="flex flex-wrap items-end gap-3" @submit.prevent="search">
        <div class="min-w-[16rem] flex-1">
          <label for="msisdn" class="block text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">
            Subscriber number
          </label>
          <input
            id="msisdn"
            v-model="input"
            inputmode="numeric"
            autocomplete="off"
            placeholder="9140257910"
            class="mt-1.5 w-full rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 font-[var(--font-mono)] text-[var(--text-sm)] tabular placeholder:text-[var(--c-text-muted)]"
          />
        </div>
        <button
          type="submit"
          :disabled="isLoading || !input.trim()"
          class="rounded-[var(--radius-md)] bg-[var(--c-accent)] px-4 py-2 text-[var(--text-sm)] font-medium text-[var(--c-accent-text)] hover:bg-[var(--c-accent-hover)] disabled:opacity-50"
        >
          {{ isLoading ? 'Searching…' : 'Search' }}
        </button>
      </form>

      <p class="mt-3 text-[var(--text-2xs)] text-[var(--c-text-muted)]">
        Lookups are audited. The number is sent in the request body, never in the URL, so it does not
        reach server access logs or browser history.
      </p>
    </Card>

    <div
      v-if="error"
      class="rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] p-4"
      role="alert"
    >
      <p class="text-[var(--text-sm)] font-semibold">{{ errorMessage(error) }}</p>
      <p v-if="correlationId(error)" class="mt-1 font-[var(--font-mono)] text-[var(--text-2xs)] text-[var(--c-text-muted)]">
        Reference: {{ correlationId(error) }}
      </p>
    </div>

    <Card
      v-if="result"
      :title="`${result.count} binding${result.count === 1 ? '' : 's'}`"
      :subtitle="result.wellFormed ? formatMsisdn(result.msisdn) : `${result.msisdn} — unusual length, shown for review`"
      flush
    >
      <div v-if="result.count === 0" class="px-4 py-8 text-center text-[var(--text-sm)] text-[var(--c-text-secondary)]">
        No bindings found for this number.
      </div>

      <div v-else class="overflow-x-auto">
        <table class="w-full text-[var(--text-sm)]">
          <thead>
            <tr class="border-b text-left text-[var(--text-xs)] text-[var(--c-text-muted)]">
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
                  class="inline-flex items-center rounded-full px-2 py-0.5 text-[var(--text-2xs)] font-medium"
                  :class="
                    b.isActive
                      ? 'bg-[var(--c-success-subtle)] text-[var(--c-success)]'
                      : 'bg-[var(--c-surface-sunken)] text-[var(--c-text-muted)]'
                  "
                >
                  {{ b.isActive ? 'Active' : 'Inactive' }}
                </span>
              </td>
              <td class="px-4 py-2.5 font-[var(--font-mono)] text-[var(--text-xs)] tabular">
                {{ formatImei(b.imei) }}
              </td>
              <td class="px-4 py-2.5">
                <span v-if="b.marketingName">{{ b.marketingName }}</span>
                <span v-else class="text-[var(--c-text-muted)] italic">unknown device</span>
                <span v-if="b.manufacturer" class="ml-1.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
                  {{ b.manufacturer }}
                </span>
              </td>
              <td class="px-4 py-2.5 text-[var(--c-text-secondary)]">{{ b.deviceType ?? '—' }}</td>
              <td class="px-4 py-2.5 font-[var(--font-mono)] text-[var(--text-xs)] tabular text-[var(--c-text-secondary)]">
                {{ b.imsi }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </Card>
  </div>
</template>
