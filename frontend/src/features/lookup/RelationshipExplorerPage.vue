<script setup lang="ts">
import { computed, ref } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import { useRelationshipExplorer, type RelatedNode } from '@/api/relationships'
import { ApiError } from '@/api/client'
import { formatDate, formatImei, formatMsisdn } from '@/lib/format'

/**
 * Explore what one identifier is connected to, and step from any of them to the next.
 *
 * The three identifiers are the grain of this whole dataset - a binding is
 * `(msisdn, imsi, imei)` - so every relationship shown here is a fact already in the data rather
 * than something computed. What the page does is reach it from whichever one the operator happens
 * to hold, and let them keep going.
 *
 * The one exception is the paired-handset section, which is derived and says so.
 */
const input = ref('')
const explorer = useRelationshipExplorer()
const graph = computed(() => explorer.data.value ?? null)

/** Without identifier.reveal the neighbours arrive redacted, and a redacted value cannot be explored. */
const masked = computed(() => graph.value?.identifiers === 'Masked')

/** Length alone decides, matching the server; the three lengths do not overlap in this feed. */
const KINDS: Record<number, string> = { 10: 'phone number', 14: 'handset', 15: 'SIM' }

const digits = computed(() => input.value.replace(/\D/g, ''))
const detected = computed(() => KINDS[digits.value.length] ?? null)

function explore(value?: string) {
  if (value) input.value = value
  const term = (value ?? input.value).replace(/\D/g, '')
  if (term) explorer.mutate(term)
}

function labelFor(node: RelatedNode): string {
  if (node.kind === 'Msisdn') return formatMsisdn(node.value)
  if (node.kind === 'Imei') return formatImei(node.value)
  return node.value
}

const centreLabel = computed(() => {
  const g = graph.value
  if (!g) return ''
  if (g.kind === 'Msisdn') return formatMsisdn(g.centre)
  if (g.kind === 'Imei') return formatImei(g.centre)
  return g.centre
})

const centreKindLabel = computed(() => {
  const g = graph.value
  return g?.kind === 'Msisdn' ? 'Phone number' : g?.kind === 'Imei' ? 'Handset' : 'SIM'
})

const errorMessage = computed(() => {
  const e = explorer.error.value
  if (!e) return null
  if (e instanceof ApiError) return e.problem?.detail ?? e.message
  return e instanceof Error ? e.message : String(e)
})

const sections = computed(() => {
  const g = graph.value
  if (!g) return []
  return [
    { key: 'subscribers', title: 'Phone numbers', unit: 'MSISDN', nodes: g.subscribers },
    { key: 'sims', title: 'SIMs', unit: 'IMSI', nodes: g.sims },
    { key: 'handsets', title: 'Handsets', unit: 'IMEI', nodes: g.handsets },
  ].filter((s) => s.nodes.length > 0 || g.withheld.includes(s.key))
})

function isWithheld(key: string): boolean {
  return graph.value?.withheld.includes(key) ?? false
}
</script>

<template>
  <div class="space-y-5">
    <header>
      <h1 class="text-xl font-semibold tracking-tight">Relationship explorer</h1>
      <p class="mt-0.5 text-xs text-[var(--c-text-muted)]">
        Start from a phone number, a SIM or a handset and follow the links between them.
      </p>
      <p class="mt-1 text-2xs text-[var(--c-text-muted)]">
        A <strong>binding</strong> is one number + SIM + handset together, so these connections are
        recorded facts, not inferences &mdash; with one labelled exception below.
      </p>
    </header>

    <Card>
      <form @submit.prevent="explore()">
        <div class="flex flex-wrap items-end gap-3">
          <div class="min-w-[min(18rem,100%)] flex-1">
            <label
              for="identifier"
              class="block text-xs font-medium text-[var(--c-text-secondary)]"
            >
              Phone number, SIM or handset
            </label>
            <input
              id="identifier"
              v-model="input"
              inputmode="numeric"
              autocomplete="off"
              spellcheck="false"
              placeholder="0913 123 4567 · 43211… · 35004012…"
              aria-describedby="identifier-help"
              class="tabular mt-1.5 w-full rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 font-mono text-sm placeholder:font-sans placeholder:text-[var(--c-text-muted)]"
            />
          </div>
          <Button type="submit" variant="primary" :disabled="!digits" :pending="explorer.isPending.value">
            Explore
          </Button>
        </div>

        <p id="identifier-help" class="mt-1.5 text-2xs text-[var(--c-text-muted)]">
          <template v-if="detected">
            {{ digits.length }} digits &mdash; read as a <strong>{{ detected }}</strong>.
          </template>
          <template v-else-if="digits.length">
            {{ digits.length }} digits. Expecting 10 (number), 14 (handset) or 15 (SIM).
          </template>
          <template v-else>
            10 digits is a number, 14 a handset, 15 a SIM. Length alone decides, because the three
            do not overlap in this feed. Lookups are audited and the value is sent in the request
            body, never in the URL.
          </template>
        </p>
      </form>
    </Card>

    <div
      v-if="errorMessage"
      class="rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">{{ errorMessage }}</p>
    </div>

    <template v-if="graph">
      <Card>
        <div class="flex flex-wrap items-baseline justify-between gap-3">
          <div>
            <p class="text-xs text-[var(--c-text-muted)]">{{ centreKindLabel }}</p>
            <p class="tabular mt-1 font-mono text-lg font-semibold">
              {{ centreLabel }}
            </p>
          </div>
          <p class="text-2xs text-[var(--c-text-muted)]">
            {{ graph.elapsedMs }} ms · {{ graph.rowsExamined.toLocaleString() }} rows read
          </p>
        </div>

        <p v-if="!graph.found" class="mt-3 text-sm text-[var(--c-text-secondary)]">
          Well-formed, but it appears nowhere in the data.
        </p>

        <p
          v-if="graph.truncated"
          class="mt-3 rounded-[var(--radius-md)] bg-[var(--c-warning-subtle)] px-3 py-2 text-xs"
        >
          This identifier has more bindings than the server will return in one go, so the lists
          below are the first 500 and not the whole set.
        </p>

        <!-- Masking is a server decision; the page reports it rather than performing it. -->
        <p
          v-if="masked"
          class="mt-3 rounded-[var(--radius-md)] bg-[var(--c-warning-subtle)] px-3 py-2 text-xs"
        >
          Identifiers are shown masked. Your account does not hold
          <code class="font-mono">identifier.reveal</code>, so the server redacted them before
          sending, and a masked identifier cannot be explored further.
        </p>
      </Card>

      <!-- At most two sections can show (the centre is not its own neighbour): two columns, not three. -->
      <div v-if="sections.length" class="grid gap-5" :class="sections.length > 1 ? 'lg:grid-cols-2' : ''">
        <Card v-for="section in sections" :key="section.key" :title="section.title" flush>
          <template #actions>
            <span class="text-2xs text-[var(--c-text-muted)]">
              {{ section.nodes.length }} · {{ section.unit }}
            </span>
          </template>

          <p
            v-if="isWithheld(section.key)"
            class="px-4 py-3 text-xs text-[var(--c-warning)]"
          >
            Withheld: you do not have permission to see {{ section.title.toLowerCase() }}. This is
            not an empty result.
          </p>

          <ul v-else class="max-h-[28rem] divide-y overflow-y-auto">
            <!-- Indexed: two different identifiers can mask to the same string. -->
            <li v-for="(node, i) in section.nodes" :key="`${i}:${node.value}`">
              <button
                type="button"
                class="w-full px-4 py-2.5 text-left enabled:hover:bg-[var(--c-surface-hover)] disabled:cursor-default"
                :disabled="masked"
                @click="explore(node.value)"
              >
                <span class="tabular block font-mono text-sm">
                  {{ labelFor(node) }}
                </span>
                <span class="mt-0.5 block text-2xs text-[var(--c-text-muted)]">
                  <template v-if="node.marketingName">
                    {{ node.brand }} {{ node.marketingName }} ·
                  </template>
                  <span :class="node.activeBindings > 0 ? 'text-[var(--c-success)]' : ''">
                    {{ node.activeBindings > 0 ? 'active' : 'ended' }}
                  </span>
                  ·
                  <template v-if="node.lastConfirmed">
                    last confirmed {{ formatDate(node.lastConfirmed) }}
                  </template>
                  <template v-else>never in a daily file</template>
                </span>
              </button>
            </li>
          </ul>
        </Card>
      </div>

      <!--
        The one derived section on the page, and the only one that can be incomplete.
      -->
      <Card
        title="Same physical handset"
        subtitle="Two IMEIs shown to be the two radios of one dual-SIM phone."
      >
        <ul v-if="graph.paired.length" class="divide-y">
          <li v-for="(pair, i) in graph.paired" :key="`${i}:${pair.imei}`" class="py-2.5 first:pt-0">
            <button
              type="button"
              class="w-full text-left disabled:cursor-default"
              :disabled="masked"
              @click="explore(pair.imei)"
            >
              <span class="tabular block font-mono text-sm">
                {{ formatImei(pair.imei) }}
              </span>
              <span class="mt-0.5 block text-2xs text-[var(--c-text-muted)]">
                <template v-if="pair.marketingName">{{ pair.marketingName }} · </template>
                same model at a paired IMEI position, and
                <strong>{{ pair.sharedSubscribers }}</strong>
                phone number{{ pair.sharedSubscribers === 1 ? '' : 's' }} seen on both
              </span>
            </button>
          </li>
        </ul>

        <p v-else class="text-sm text-[var(--c-text-secondary)]">
          No pair could be shown for this identifier.
        </p>

        <template #footer>
          <!--
            The caveat the product owner asked for, with its reason.

            It matters because absence here is not evidence of absence in the world: most dual-SIM
            handsets on this network will never appear as a pair, and a reader who does not know
            why would take "no pair" to mean "single-SIM phone".
          -->
          <div class="space-y-2 text-2xs text-[var(--c-text-muted)]">
            <p>
              <strong>This will not show every real pair, and that is expected.</strong> The feed
              records a binding &mdash; number, SIM, handset &mdash; and has no field saying which
              two IMEIs are one device. A pair is shown only where the data makes it provable: the
              two IMEIs sit at a position manufacturers use for a second radio, they are the same
              model, <em>and</em> the same phone number has been seen on both.
            </p>
            <p>
              Manufacturers do not allocate alike, and there are two schemes here.
              <strong>Adjacent serial in one TAC</strong> &mdash; Xiaomi and Redmi: measured on one
              Redmi Note 12S TAC, 4,760 pairs at distance 1 and <strong>zero</strong> at distances
              2, 3 and 1000. <strong>Identical serial in a twin TAC</strong> &mdash; Samsung, where
              the GSMA database lists both TACs under one model name: measured on Galaxy A51,
              3,290 pairs at that offset and zero at the others.
            </p>
            <p>
              The limit that remains is the feed itself. A handset's second radio appears only if a
              SIM of <em>this</em> network has used it, so a phone whose other slot holds another
              operator's SIM cannot be paired here at all. A handset with no pair shown is therefore
              <em>not</em> shown to be single-SIM. Coverage will improve as probe data from other
              network nodes is added; until then the rule stays strict, because a wrong pairing is
              worse than a missing one &mdash; nobody re-checks a link that looks right.
            </p>
          </div>
        </template>
      </Card>
    </template>
  </div>
</template>
