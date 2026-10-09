<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import Button from '@/design-system/Button.vue'
import { control } from '@/features/explorer/ui'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { prefillState } from '@/lib/prefill'
import type { QuickLookupConfig } from './config'

/**
 * A lookup box on the dashboard that opens the real lookup page with the value already in it.
 *
 * What is typed never leaves this card except to that page, and never through a URL: numbers, SIMs
 * and handsets travel in history state (`lib/prefill.ts`), are not stored in the dashboard and are
 * not shared with it. The lookup itself is the page's own audited request. A TAC is a model code, so
 * it opens the device page by its path like everywhere else.
 */
const props = defineProps<WidgetViewProps<QuickLookupConfig>>()
const router = useRouter()

const TARGETS = {
  msisdn: { label: 'Subscriber number', placeholder: 'e.g. 9121234567', route: 'lookup', pattern: /^\d{10,12}$/, hint: 'A 10-digit number.' },
  imsi: { label: 'IMSI or prefix', placeholder: '15 digits, or the first few', route: 'imsi-search', pattern: /^\d{5,15}$/, hint: 'Five to fifteen digits.' },
  relationships: {
    label: 'Number, SIM or handset',
    placeholder: '10, 14 or 15 digits',
    route: 'relationships',
    pattern: /^(\d{10}|\d{14}|\d{15})$/,
    hint: 'A number (10 digits), a handset (14) or a SIM (15).',
  },
  tac: { label: 'TAC', placeholder: '8 digits', route: 'device-detail', pattern: /^\d{8}$/, hint: 'The first 8 digits of an IMEI.' },
} as const

const target = computed(() => TARGETS[props.instance.config.target])
const value = ref('')
const digits = computed(() => value.value.replace(/[\s-]/g, ''))
const valid = computed(() => target.value.pattern.test(digits.value))
const live = computed(() => props.mode === 'view')
const id = computed(() => `quick-lookup-${props.instance.id}`)

function open() {
  if (!valid.value || !live.value) return
  if (props.instance.config.target === 'tac') void router.push({ name: 'device-detail', params: { tac: digits.value } })
  else void router.push({ name: target.value.route, state: prefillState(digits.value) })
}
</script>

<template>
  <form class="flex h-full flex-col justify-center gap-1.5" @submit.prevent="open">
    <label :for="id" class="text-xs font-medium text-[var(--c-text-secondary)]">{{ target.label }}</label>
    <div class="flex gap-2">
      <input
        :id="id"
        v-model="value"
        type="text"
        inputmode="numeric"
        autocomplete="off"
        spellcheck="false"
        :placeholder="target.placeholder"
        :disabled="!live"
        :aria-invalid="value !== '' && !valid"
        :class="[control, 'tabular min-w-0 flex-1 font-mono']"
      />
      <Button type="submit" size="sm" :disabled="!live || !valid">Open</Button>
    </div>
    <p class="text-2xs text-[var(--c-text-muted)]">
      <template v-if="!live">Works on the dashboard; not in a preview.</template>
      <template v-else-if="value !== '' && !valid">{{ target.hint }}</template>
      <template v-else-if="instance.config.target === 'tac'">Opens the device page.</template>
      <template v-else>Opens the lookup with this filled in. Not saved with the dashboard.</template>
    </p>
  </form>
</template>
