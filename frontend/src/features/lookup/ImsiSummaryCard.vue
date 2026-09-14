<script setup lang="ts">
import { computed } from 'vue'
import Card from '@/design-system/Card.vue'
import type { ImsiSummary } from '@/api/imsi'
import { formatDate } from '@/lib/format'

/**
 * What is known about one SIM.
 *
 * Four figures, each of which means something specific about this dataset rather than being a
 * count for its own sake:
 *
 * - more than one **number** on a SIM is a number change, which is rare and worth seeing;
 * - more than one **handset** is a SIM swap, which is the product's core signal;
 * - **active bindings** above one usually means the initial dump listed a month's worth of
 *   handsets rather than an instant's, not that the SIM is in two phones;
 * - **never confirmed** is the honest caveat: roughly half the active population has never been
 *   named by a daily file, so its state rests on the dump alone.
 */
const props = defineProps<{ summary: ImsiSummary; imsi: string }>()

const swapped = computed(() => props.summary.distinctHandsets > 1)
const renumbered = computed(() => props.summary.distinctSubscribers > 1)
</script>

<template>
  <Card>
    <div class="flex flex-wrap items-start justify-between gap-4">
      <div>
        <p class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">SIM</p>
        <p class="tabular font-mono text-[var(--text-lg)] font-semibold tracking-tight">
          {{ imsi }}
        </p>
      </div>

      <dl class="tabular flex flex-wrap gap-x-8 gap-y-3">
        <div>
          <dt class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">Numbers</dt>
          <dd
            class="text-[var(--text-lg)] font-semibold"
            :style="renumbered ? { color: 'var(--c-warning)' } : undefined"
          >
            {{ summary.distinctSubscribers }}
          </dd>
        </div>

        <div>
          <dt class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">Handsets</dt>
          <dd
            class="text-[var(--text-lg)] font-semibold"
            :style="swapped ? { color: 'var(--c-accent)' } : undefined"
          >
            {{ summary.distinctHandsets }}
          </dd>
        </div>

        <div>
          <dt class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">Active now</dt>
          <dd class="text-[var(--text-lg)] font-semibold">{{ summary.activeBindings }}</dd>
        </div>

        <div>
          <dt class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">Seen</dt>
          <dd class="text-[var(--text-sm)]">
            <template v-if="summary.firstSeen">
              {{ formatDate(summary.firstSeen) }}
              <template v-if="summary.lastSeen !== summary.firstSeen">
                – {{ formatDate(summary.lastSeen) }}
              </template>
            </template>
            <span v-else class="text-[var(--c-text-muted)]">no dated event</span>
          </dd>
        </div>
      </dl>
    </div>

    <!--
      One sentence, chosen by what the figures actually say. A card that shows four numbers and
      leaves the reader to interpret them is a card that will be interpreted wrongly.
    -->
    <p class="mt-4 border-t pt-3 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
      <template v-if="!summary.everTouchedByDailyFile">
        <span class="font-medium text-[var(--c-warning)]">Never confirmed.</span>
        No daily file has mentioned this SIM since the initial dump, so everything above rests on
        that dump alone — it has been neither confirmed nor contradicted.
      </template>

      <template v-else-if="swapped && renumbered">
        This SIM has been in {{ summary.distinctHandsets }} handsets and on
        {{ summary.distinctSubscribers }} numbers. Both moving is unusual; the history below shows
        the order they happened in.
      </template>

      <template v-else-if="swapped">
        This SIM has been in {{ summary.distinctHandsets }} handsets — the SIM-swap signal. Note
        that the initial dump covers a 30-day window rather than an instant, so some of those
        handsets may have been in use during the same month rather than in sequence.
      </template>

      <template v-else-if="renumbered">
        This SIM has carried {{ summary.distinctSubscribers }} numbers. IMSI and MSISDN are 1:1 for
        99.97% of the population, so this is one of the 0.03%.
      </template>

      <template v-else>
        One number, one handset, confirmed by the daily feed. The ordinary case, and about 74% of
        the population.
      </template>
    </p>
  </Card>
</template>
