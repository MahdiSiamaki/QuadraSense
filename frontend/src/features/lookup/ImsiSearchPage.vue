<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import Pagination from '@/design-system/Pagination.vue'
import ImsiSummaryCard from './ImsiSummaryCard.vue'
import ImsiHistoryPanel from './ImsiHistoryPanel.vue'
import {
  describeTerm,
  prefixBreadth,
  useImsiSearch,
  IMSI_CONSTANT_PREFIX,
  IMSI_LENGTH,
  MIN_IMSI_PREFIX,
  type ImsiMatch,
} from '@/api/imsi'
import { useTopDimension, useDistribution } from '@/api/dashboard'
import { ApiError } from '@/api/client'
import { formatDate, formatFull } from '@/lib/format'

/**
 * Search by IMSI.
 *
 * The page is built around one fact the data forced on it: **every IMSI in this feed begins
 * 43211** - MCC 432, MNC 11 - so the first five digits carry no information. Measured over the
 * 295-million-row current-state table, a five-digit prefix matches every row, eight digits still
 * average 3.4 million, and ten digits average 78,183 with a worst bucket of 557,158.
 *
 * So the input explains the minimum rather than merely enforcing it, and it shows how wide the
 * term is *before* the search runs. A field that says "invalid" teaches nothing; one that says
 * "10 digits leaves 100,000 possible SIMs" teaches the shape of the data.
 *
 * Every search is audited server-side. The IMSI is not recorded - only who searched, how many
 * digits they gave, and how many rows came back. An audit log full of SIM identities would be a
 * second copy of the data it exists to protect.
 */
const raw = ref('')
const activeOnly = ref(false)
const deviceType = ref('')
const manufacturer = ref('')
const from = ref('')
const to = ref('')
const page = ref(1)

const search = useImsiSearch()
const result = computed(() => search.data.value ?? null)

/** Filter values come from the API, never free text, so a filter cannot name anything unknown. */
const deviceTypes = useDistribution('deviceType', () => ({}), 'bindings')
const vendors = useTopDimension('vendorCanonical', () => ({}), 12, 'bindings')

const term = computed(() => describeTerm(raw.value))
const breadth = computed(() => prefixBreadth(term.value.digits.length))

const problem = computed(() => {
  if (term.value.problem) return term.value.problem
  const error = search.error.value
  if (!error) return null
  if (error instanceof ApiError) {
    return error.fieldErrors[0]?.message ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return 'Could not reach the server.'
})

/** The IMSI whose history is open, if any. Only ever a complete one. */
const opened = ref<string | null>(null)

/**
 * Whether a search has been run, separately from whether its result is on screen. The filters
 * keyed off the result, which a failed search clears: the filter that caused the failure then
 * vanished with it, and every retry re-sent it.
 */
const searched = ref(false)

function run(toPage = 1) {
  if (!term.value.ok) return
  searched.value = true
  page.value = toPage
  opened.value = null

  search.mutate({
    imsi: term.value.digits,
    deviceType: deviceType.value || null,
    manufacturer: manufacturer.value || null,
    activeOnly: activeOnly.value,
    from: from.value || null,
    to: to.value || null,
    page: toPage,
    pageSize: 50,
  })
}

// Re-running on a filter change rather than making the user press the button again: they have
// already asked for this term, and the filters only narrow it.
watch([activeOnly, deviceType, manufacturer, from, to], () => {
  if (searched.value) run(1)
})

// An exact search with exactly one SIM in it opens itself. The user typed a complete IMSI; making
// them click the single row it returned is a step that carries no decision.
watch(result, (value) => {
  if (value?.isExact && value.items.length > 0) {
    opened.value = value.items[0]?.imsi ?? null
  }
})

function openHistory(match: ImsiMatch) {
  // Only possible on a complete, unmasked IMSI: a masked one cannot be sent back to the server.
  opened.value = match.imsi.length === IMSI_LENGTH && /^\d+$/.test(match.imsi) ? match.imsi : null
}

const masked = computed(() => result.value?.identifiers === 'Masked')
</script>

<template>
  <div class="flex flex-col gap-5">
    <header>
      <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">IMSI search</h1>
      <p class="mt-0.5 text-[var(--text-sm)] text-[var(--c-text-secondary)]">
        Find the numbers and handsets bound to a SIM, by full IMSI or by prefix.
      </p>
    </header>

    <Card>
      <form class="flex flex-col gap-3" @submit.prevent="run(1)">
        <div class="flex flex-wrap items-start gap-3">
          <div class="min-w-0 flex-1">
            <label
              for="imsi-input"
              class="mb-1 block text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]"
            >
              IMSI or prefix
            </label>
            <input
              id="imsi-input"
              v-model="raw"
              inputmode="numeric"
              autocomplete="off"
              spellcheck="false"
              placeholder="432 11 3991761332"
              class="tabular w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-3 py-2 font-mono text-[var(--text-base)] tracking-wide placeholder:font-sans placeholder:tracking-normal placeholder:text-[var(--c-text-muted)]"
              :style="problem ? { borderColor: 'var(--c-danger)' } : undefined"
              :aria-invalid="problem ? 'true' : undefined"
              aria-describedby="imsi-help"
            />
          </div>

          <div class="pt-[1.55rem]">
            <Button type="submit" variant="primary" :disabled="!term.ok" :pending="search.isPending.value">
              Search
            </Button>
          </div>
        </div>

        <!--
          The one line that teaches the data. It counts digits as they are typed and names what
          the term would match, so the minimum reads as a consequence of the network's numbering
          rather than as an arbitrary rule somebody chose.
        -->
        <p id="imsi-help" class="text-[var(--text-xs)]" role="status">
          <span v-if="problem" class="text-[var(--c-danger)]">{{ problem }}</span>

          <span v-else-if="term.digits.length === 0" class="text-[var(--c-text-muted)]">
            Every IMSI here begins <code class="font-mono">{{ IMSI_CONSTANT_PREFIX }}</code> — MCC 432,
            MNC 11 — so the first five digits narrow nothing. Enter at least
            {{ MIN_IMSI_PREFIX }}.
          </span>

          <span v-else-if="term.isExact" class="text-[var(--c-success)]">
            {{ term.digits.length }} digits — a complete IMSI.
          </span>

          <span v-else class="text-[var(--c-text-secondary)]">
            {{ term.digits.length }} digits — a prefix covering up to
            <span class="tabular font-medium">{{ formatFull(breadth) }}</span> possible SIMs.
          </span>
        </p>

        <!-- Filters appear once a search has been run, and stay if it fails. -->
        <div v-if="searched" class="flex flex-wrap items-end gap-3 border-t pt-3">
          <label class="flex items-center gap-2 text-[var(--text-xs)]">
            <input v-model="activeOnly" type="checkbox" />
            Active bindings only
          </label>

          <div>
            <label for="f-type" class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              Device type
            </label>
            <select
              id="f-type"
              v-model="deviceType"
              class="mt-0.5 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
            >
              <option value="">Any</option>
              <option v-for="d in deviceTypes.data.value ?? []" :key="d.key" :value="d.key">
                {{ d.key }}
              </option>
            </select>
          </div>

          <div>
            <label for="f-vendor" class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              Manufacturer
            </label>
            <select
              id="f-vendor"
              v-model="manufacturer"
              class="mt-0.5 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
            >
              <option value="">Any</option>
              <option v-for="v in vendors.data.value ?? []" :key="v.key" :value="v.key">
                {{ v.key }}
              </option>
            </select>
          </div>

          <div>
            <label for="f-from" class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              Changed from
            </label>
            <input
              id="f-from"
              v-model="from"
              type="date"
              class="tabular mt-0.5 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
            />
          </div>

          <div>
            <label for="f-to" class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              to
            </label>
            <input
              id="f-to"
              v-model="to"
              type="date"
              class="tabular mt-0.5 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-[var(--text-xs)]"
            />
          </div>

          <!--
            Stated beside the control, not discovered afterwards. Roughly half the active
            population has never been named by a daily file, so its last-change date is null and a
            date filter silently removes it.
          -->
          <p
            v-if="from || to"
            class="basis-full text-[var(--text-2xs)] text-[var(--c-warning)]"
          >
            A date filter excludes bindings no daily file has ever mentioned — they have no change
            date at all, not a date outside the range.
          </p>
        </div>
      </form>
    </Card>

    <template v-if="result">
      <!-- Masking is a server decision; the page reports it rather than performing it. -->
      <p
        v-if="masked"
        class="rounded-[var(--radius-md)] border px-3 py-2 text-[var(--text-xs)]"
        :style="{
          borderColor: 'var(--c-warning)',
          backgroundColor: 'var(--c-warning-subtle)',
          color: 'var(--c-warning)',
        }"
      >
        Identifiers are shown masked. Your account does not hold
        <code class="font-mono">identifier.reveal</code>, so the server redacted them before
        sending — the complete values are not in this page.
      </p>

      <p
        v-if="!result.wellFormed"
        class="rounded-[var(--radius-md)] border px-3 py-2 text-[var(--text-xs)]"
        :style="{
          borderColor: 'var(--c-warning)',
          backgroundColor: 'var(--c-warning-subtle)',
          color: 'var(--c-warning)',
        }"
      >
        {{ result.term.length }} digits — an IMSI is {{ IMSI_LENGTH }}. Shown for review: 28 rows in
        the dataset are 16 or 17 digits.
      </p>

      <ImsiSummaryCard v-if="result.summary" :summary="result.summary" :imsi="result.term" />

      <Card flush>
        <template #actions>
          <!--
            Rows examined, not only elapsed time. On this dataset an indexed lookup reads a few
            thousand rows and a scan reads 295 million; elapsed time alone moves with cache warmth
            and hides the difference.
          -->
          <span class="tabular text-[var(--text-2xs)] text-[var(--c-text-muted)]">
            {{ result.timing.elapsedMs }} ms ·
            {{ formatFull(result.timing.rowsExamined) }} rows examined
          </span>
        </template>

        <div v-if="result.items.length === 0" class="px-4 py-10 text-center">
          <p class="text-[var(--text-sm)] text-[var(--c-text-secondary)]">
            No binding matches
            <code class="font-mono">{{ result.term }}</code
            ><template v-if="!result.isExact">…</template>.
          </p>
          <p class="mt-1 text-[var(--text-xs)] text-[var(--c-text-muted)]">
            The SIM may not be on this network, or the filters may exclude it.
          </p>
        </div>

        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-[var(--text-sm)]">
            <thead
              class="border-b text-[var(--text-2xs)] tracking-wide text-[var(--c-text-muted)] uppercase"
            >
              <tr>
                <th scope="col" class="px-4 py-2 font-medium">IMSI</th>
                <th scope="col" class="px-4 py-2 font-medium">Number</th>
                <th scope="col" class="px-4 py-2 font-medium">Handset</th>
                <th scope="col" class="px-4 py-2 font-medium">Device</th>
                <th scope="col" class="px-4 py-2 font-medium">State</th>
                <th scope="col" class="px-4 py-2 font-medium">Last change</th>
              </tr>
            </thead>

            <tbody class="divide-y">
              <tr
                v-for="match in result.items"
                :key="`${match.imsi}-${match.msisdn}-${match.imei}`"
                class="hover:bg-[var(--c-surface-hover)]"
                :class="opened === match.imsi ? 'bg-[var(--c-surface-sunken)]' : ''"
              >
                <td class="tabular px-4 py-2 font-mono text-[var(--text-xs)]">
                  <button
                    v-if="!masked"
                    type="button"
                    class="hover:text-[var(--c-accent)] hover:underline"
                    @click="openHistory(match)"
                  >
                    {{ match.imsi }}
                  </button>
                  <span v-else>{{ match.imsi }}</span>
                </td>

                <td class="tabular px-4 py-2 font-mono text-[var(--text-xs)]">
                  {{ match.msisdn }}
                </td>

                <td class="tabular px-4 py-2 font-mono text-[var(--text-xs)]">
                  {{ match.imei }}
                </td>

                <td class="px-4 py-2">
                  <template v-if="match.marketingName || match.manufacturer">
                    <span class="block">{{ match.marketingName ?? match.manufacturer }}</span>
                    <span class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
                      {{ match.manufacturer }}
                      <template v-if="match.deviceType"> · {{ match.deviceType }}</template>
                      <template v-if="match.operatingSystem"> · {{ match.operatingSystem }}</template>
                    </span>
                  </template>
                  <!--
                    Absence stated rather than left blank. 7.4% of bindings have no TAC match and
                    an empty cell reads as a rendering fault.
                  -->
                  <span v-else class="text-[var(--text-xs)] text-[var(--c-text-muted)]">
                    <template v-if="match.imei === '000000'">device not reported</template>
                    <template v-else-if="match.tac">TAC {{ match.tac }} not in the database</template>
                    <template v-else>no usable IMEI</template>
                  </span>
                </td>

                <td class="px-4 py-2">
                  <span
                    class="inline-flex items-center gap-1.5 text-[var(--text-xs)]"
                    :style="{
                      color: match.isActive ? 'var(--c-text-secondary)' : 'var(--c-text-muted)',
                    }"
                  >
                    <span
                      class="size-1.5 rounded-full"
                      :style="{
                        backgroundColor: match.isActive
                          ? 'var(--c-success)'
                          : 'var(--c-text-muted)',
                      }"
                      aria-hidden="true"
                    />
                    {{ match.isActive ? 'Active' : 'Ended' }}
                  </span>
                </td>

                <td class="tabular px-4 py-2 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                  <template v-if="match.lastChangeDate">
                    {{ formatDate(match.lastChangeDate) }}
                  </template>
                  <span
                    v-else
                    class="text-[var(--c-text-muted)]"
                    title="No daily file has ever mentioned this binding. It exists because the initial dump listed it."
                  >
                    dump only
                  </span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <Pagination
          v-if="result.total > result.pageSize"
          :page="result.page"
          :page-size="result.pageSize"
          :total="result.total"
          :loading="search.isPending.value"
          @update:page="(p) => run(p)"
        />
      </Card>

      <ImsiHistoryPanel v-if="opened" :imsi="opened" />
    </template>

    <!-- The empty state carries the explanation, so nobody has to fail first to learn the rule. -->
    <Card v-else-if="!search.isPending.value">
      <div class="py-8 text-center">
        <p class="text-[var(--text-sm)] text-[var(--c-text-secondary)]">
          Enter an IMSI to see the numbers and handsets bound to that SIM.
        </p>
        <dl
          class="tabular mx-auto mt-5 grid max-w-md grid-cols-3 gap-4 text-[var(--text-xs)]"
        >
          <div>
            <dt class="text-[var(--c-text-muted)]">5 digits</dt>
            <dd class="font-medium">every SIM</dd>
          </div>
          <div>
            <dt class="text-[var(--c-text-muted)]">10 digits</dt>
            <dd class="font-medium">~78,000 rows</dd>
          </div>
          <div>
            <dt class="text-[var(--c-text-muted)]">15 digits</dt>
            <dd class="font-medium">one SIM</dd>
          </div>
        </dl>
        <p class="mx-auto mt-3 max-w-md text-[var(--text-2xs)] text-[var(--c-text-muted)]">
          Measured over 295,013,916 bindings. Searches are recorded in the audit log — who and how
          many results, never the IMSI itself.
        </p>
      </div>
    </Card>
  </div>
</template>
