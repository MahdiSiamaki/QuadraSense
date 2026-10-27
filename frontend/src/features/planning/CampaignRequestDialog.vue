<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useCampaignExports, useRequestCampaignExport, type CampaignExport } from '@/api/campaigns'
import type { PlanningFilter } from '@/api/planning'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import { formatFull, formatSequence } from '@/lib/format'

/**
 * Asks for a campaign list: the numbers of one class, for a reason that is recorded, shown to the
 * approver and audited. Above the threshold a second person approves; the server decides, counts the
 * list again, and says which it is.
 */
const props = defineProps<{
  open: boolean
  view: 'sunset' | 'esim'
  planningClass: string
  classLabel: string
  reason: string | null
  run: { runId: number; asOfSequence: number; asOfDate: string | null }
  filter: PlanningFilter
}>()

const emit = defineEmits<{ close: [] }>()

const purpose = ref('')
const done = ref<CampaignExport | null>(null)
const settings = useCampaignExports(true)
const ask = useRequestCampaignExport()

watch(
  () => props.open,
  (open) => {
    if (open) {
      purpose.value = ''
      done.value = null
      ask.reset()
    }
  },
)

const threshold = computed(() => settings.data.value?.approvalThreshold ?? 0)
const filters = computed(() =>
  [
    props.filter.vendor && `vendor ${props.filter.vendor}`,
    props.filter.model && `model ${props.filter.model}`,
    props.filter.deviceType && `type ${props.filter.deviceType}`,
  ]
    .filter(Boolean)
    .join(', '),
)

async function submit() {
  if (done.value) {
    emit('close')
    return
  }

  done.value = await ask.mutateAsync({
    view: props.view,
    class: props.planningClass,
    reason: props.reason,
    runId: props.run.runId,
    purpose: purpose.value.trim(),
    ...props.filter,
  })
}
</script>

<template>
  <Modal
    :open="open"
    title="Request a campaign list"
    description="The numbers leave the system as a file. Your reason is recorded, shown to whoever approves, and kept in the audit log."
    :busy="ask.isPending.value"
    @close="emit('close')"
    @submit="submit"
  >
    <div v-if="!done" class="flex flex-col gap-3 text-sm">
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt class="text-[var(--c-text-muted)]">Class</dt>
        <dd>{{ classLabel }}</dd>
        <dt class="text-[var(--c-text-muted)]">Narrowed to</dt>
        <dd>{{ filters || 'every model' }}</dd>
        <dt class="text-[var(--c-text-muted)]">Delivery</dt>
        <dd class="tabular">{{ formatSequence(run.asOfSequence, run.asOfDate) }}</dd>
      </dl>

      <label class="flex flex-col gap-1 text-xs text-[var(--c-text-secondary)]">
        Why is this list needed?
        <textarea
          v-model="purpose"
          rows="3"
          maxlength="1000"
          required
          class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-3 py-2 text-sm text-[var(--c-text)]"
          placeholder="For example: the 2G/3G migration campaign, first wave"
        />
        <span class="text-2xs text-[var(--c-text-muted)]">At least 10 characters.</span>
      </label>

      <p class="text-2xs text-[var(--c-text-muted)]">
        <template v-if="threshold === 0">Every campaign list is approved by a second person before it can be downloaded.</template>
        <template v-else>
          A list of more than {{ formatFull(threshold) }} numbers is approved by a second person before it can be downloaded.
        </template>
        Nobody approves their own.
      </p>

      <p v-if="ask.isError.value" class="text-xs text-[var(--c-danger-text)]" role="alert">
        {{ (ask.error.value as Error | null)?.message ?? 'Not accepted.' }}
      </p>
    </div>

    <div v-else class="flex flex-col gap-2 text-sm" role="status">
      <p class="font-medium">
        {{ done.status === 'approved' ? 'Approved' : 'Waiting for a second person' }} -
        {{ formatFull(done.rowCount) }} numbers.
      </p>
      <p class="text-xs text-[var(--c-text-secondary)]">
        <template v-if="done.status === 'approved'">It can be downloaded until {{ done.expiresAt }}.</template>
        <template v-else>Someone holding the approval permission decides; you will find it under Campaign lists.</template>
      </p>
      <RouterLink to="/settings/campaign-lists" class="w-fit text-xs text-[var(--c-accent)] hover:underline">
        Open Campaign lists
      </RouterLink>
    </div>

    <template #actions>
      <Button v-if="!done" :disabled="ask.isPending.value" @click="emit('close')">Cancel</Button>
      <Button type="submit" variant="primary" :pending="ask.isPending.value" :disabled="!done && purpose.trim().length < 10">
        {{ done ? 'Close' : 'Request' }}
      </Button>
    </template>
  </Modal>
</template>
