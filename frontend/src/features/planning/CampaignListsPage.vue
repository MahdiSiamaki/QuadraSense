<script setup lang="ts">
import { ref } from 'vue'
import { saveFile } from '@/api/client'
import {
  useCampaignExports,
  useDecideCampaignExport,
  useDownloadCampaignExport,
  useWithdrawCampaignExport,
  type CampaignExport,
} from '@/api/campaigns'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import Modal from '@/design-system/Modal.vue'
import { formatDateTime, formatFull, formatSequence } from '@/lib/format'

/**
 * Campaign lists: one's own requests to download, and - for an approver - everybody's to decide.
 *
 * What a list is, how many numbers it holds and why it was asked for are shown before anyone decides;
 * whether a person may download or decide is the server's answer, carried with each request.
 */
const list = useCampaignExports(true)
const decide = useDecideCampaignExport()
const withdraw = useWithdrawCampaignExport()
const download = useDownloadCampaignExport()

const deciding = ref<{ request: CampaignExport; approve: boolean } | null>(null)
const note = ref('')

function openDecision(request: CampaignExport, approve: boolean) {
  note.value = ''
  deciding.value = { request, approve }
}

async function submitDecision() {
  if (!deciding.value) return
  await decide.mutateAsync({ id: deciding.value.request.id, approve: deciding.value.approve, note: note.value.trim() || undefined })
  deciding.value = null
}

async function take(request: CampaignExport) {
  saveFile(await download.mutateAsync(request.id), `campaign-${request.id}.csv`)
}

const STATUS: Record<CampaignExport['status'], string> = {
  pending: 'Waiting for approval',
  approved: 'Approved',
  rejected: 'Rejected',
  withdrawn: 'Withdrawn',
}

function what(r: CampaignExport) {
  const narrowed = [r.filter.vendor, r.filter.model, r.filter.deviceType].filter(Boolean).join(', ')
  return `${r.view === 'esim' ? 'eSIM' : '2G/3G sunset'}: ${r.class}${r.reason ? ` (${r.reason})` : ''}${narrowed ? ` - ${narrowed}` : ''}`
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <header>
      <h1 class="text-lg font-semibold tracking-tight">Campaign lists</h1>
      <p class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
        Lists of numbers taken out of the system for a campaign. Each records why it was asked for; above the threshold a
        second person approves it, and nobody approves their own. Every request, decision and download is in the audit log.
      </p>
    </header>

    <AsyncBoundary
      :is-loading="list.isPending.value"
      :is-error="list.isError.value"
      :error="list.error.value"
      min-height="8rem"
      @retry="list.refetch()"
    >
      <template v-if="list.data.value">
        <p class="text-xs text-[var(--c-text-secondary)]">
          <template v-if="list.data.value.approvalThreshold === 0">Every list needs a second person's approval.</template>
          <template v-else>Lists of more than {{ formatFull(list.data.value.approvalThreshold) }} numbers need a second person's approval.</template>
          An approved list can be downloaded for {{ list.data.value.validHours }} hours.
        </p>

        <Card flush>
          <p v-if="list.data.value.requests.length === 0" class="px-4 py-8 text-center text-xs text-[var(--c-text-muted)]">
            No campaign lists yet. Ask for one from a class on the Planning page.
          </p>
          <div v-else class="overflow-x-auto">
            <table class="w-full text-xs">
              <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
                <tr>
                  <th scope="col" class="px-3 py-2 font-medium">List</th>
                  <th scope="col" class="px-3 py-2 text-right font-medium">Numbers</th>
                  <th scope="col" class="px-3 py-2 font-medium">Why</th>
                  <th scope="col" class="px-3 py-2 font-medium">Asked by</th>
                  <th scope="col" class="px-3 py-2 font-medium">Status</th>
                  <th scope="col" class="px-3 py-2"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="r in list.data.value.requests" :key="r.id" class="border-t align-top">
                  <td class="px-3 py-2">
                    <p>{{ what(r) }}</p>
                    <p class="tabular text-2xs text-[var(--c-text-muted)]">Delivery {{ formatSequence(r.asOfSequence, r.asOfDate) }}</p>
                  </td>
                  <td class="tabular px-3 py-2 text-right">{{ formatFull(r.rowCount) }}</td>
                  <td class="max-w-72 px-3 py-2 text-[var(--c-text-secondary)]">{{ r.purpose }}</td>
                  <td class="px-3 py-2 whitespace-nowrap">
                    {{ r.requestedBy }}
                    <p class="text-2xs text-[var(--c-text-muted)]">{{ formatDateTime(r.requestedAt) }}</p>
                  </td>
                  <td class="px-3 py-2">
                    <p>{{ STATUS[r.status] }}</p>
                    <p v-if="r.decidedBy" class="text-2xs text-[var(--c-text-muted)]">
                      by {{ r.decidedBy }}<template v-if="r.decisionNote">: “{{ r.decisionNote }}”</template>
                    </p>
                    <p v-else-if="r.status === 'approved' && !r.needsApproval" class="text-2xs text-[var(--c-text-muted)]">within the threshold</p>
                    <p v-if="r.status === 'approved' && r.expiresAt" class="text-2xs text-[var(--c-text-muted)]">
                      until {{ formatDateTime(r.expiresAt) }} · {{ r.downloads }} download{{ r.downloads === 1 ? '' : 's' }}
                    </p>
                  </td>
                  <td class="px-3 py-2">
                    <div class="flex flex-wrap justify-end gap-1.5">
                      <Button v-if="r.canDownload" size="sm" variant="primary" :pending="download.isPending.value" @click="take(r)">
                        Download
                      </Button>
                      <template v-if="r.canDecide">
                        <Button size="sm" variant="primary" @click="openDecision(r, true)">Approve</Button>
                        <Button size="sm" variant="secondary" @click="openDecision(r, false)">Reject</Button>
                      </template>
                      <Button
                        v-if="r.canDownload || (r.status === 'pending' && !r.canDecide)"
                        size="sm"
                        variant="ghost"
                        @click="withdraw.mutate(r.id)"
                      >
                        Withdraw
                      </Button>
                    </div>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </Card>
        <p v-if="download.isError.value" class="text-xs text-[var(--c-danger-text)]" role="alert">
          {{ (download.error.value as Error | null)?.message }}
        </p>
      </template>
    </AsyncBoundary>

    <Modal
      :open="!!deciding"
      :title="deciding?.approve ? 'Approve this campaign list?' : 'Reject this campaign list?'"
      :description="deciding ? `${formatFull(deciding.request.rowCount)} numbers, asked for by ${deciding.request.requestedBy}: “${deciding.request.purpose}”` : ''"
      size="sm"
      :busy="decide.isPending.value"
      @close="deciding = null"
      @submit="submitDecision"
    >
      <label class="flex flex-col gap-1 text-xs text-[var(--c-text-secondary)]">
        A note for the record (optional)
        <textarea v-model="note" rows="2" maxlength="1000" class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-3 py-2 text-sm text-[var(--c-text)]" />
      </label>
      <p v-if="decide.isError.value" class="mt-2 text-xs text-[var(--c-danger-text)]" role="alert">
        {{ (decide.error.value as Error | null)?.message }}
      </p>
      <template #actions>
        <Button :disabled="decide.isPending.value" @click="deciding = null">Cancel</Button>
        <Button type="submit" :variant="deciding?.approve ? 'primary' : 'danger'" :pending="decide.isPending.value">
          {{ deciding?.approve ? 'Approve' : 'Reject' }}
        </Button>
      </template>
    </Modal>
  </div>
</template>
