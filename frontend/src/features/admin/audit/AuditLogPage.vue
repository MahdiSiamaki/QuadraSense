<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Pagination from '@/design-system/Pagination.vue'
import { useAuditActions, useAuditLog, type AuditRecord, type AuditFilters } from '@/api/admin'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * The audit log.
 *
 * One stream, not two. Identity events live in `audit.event` and the import platform keeps its
 * own table; a database view merges them, so this page is one query and a reader does not have to
 * interleave two lists by timestamp in their head.
 *
 * Failures and denials are the entries anyone actually comes here for, so they are filterable in
 * one click and marked in the row. A log where a refused attempt looks like every other line is
 * a log nobody reads twice.
 *
 * There is no export button and no delete. The application's database role holds INSERT and
 * SELECT on these tables and nothing else, so deletion is not merely absent from the UI - it is
 * refused by PostgreSQL.
 */
const search = ref('')
const action = ref('')
const outcome = ref('')
const category = ref('')
const page = ref(1)
const expanded = ref<string | null>(null)

const actions = useAuditActions()

const filters = computed<AuditFilters>(() => ({
  search: search.value.trim() || undefined,
  action: action.value || undefined,
  outcome: outcome.value || undefined,
  category: category.value || undefined,
  page: page.value,
  pageSize: 50,
}))

const log = useAuditLog(filters)

watch([search, action, outcome, category], () => {
  page.value = 1
})

const tone: Record<string, { fg: string; bg: string }> = {
  success: { fg: 'var(--c-text-secondary)', bg: 'transparent' },
  failure: { fg: 'var(--c-warning)', bg: 'var(--c-warning-subtle)' },
  denied: { fg: 'var(--c-danger)', bg: 'var(--c-danger-subtle)' },
}

/** Pretty-prints the JSON detail, or hands back the raw text if it will not parse. */
function detailText(record: AuditRecord): string {
  if (!record.detail) return ''
  try {
    return JSON.stringify(JSON.parse(record.detail), null, 2)
  } catch {
    return record.detail
  }
}

const selectClass =
  'rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1.5 text-xs'
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 class="text-lg font-semibold tracking-tight">Audit log</h2>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          Append-only. Sign-ins, administrative changes, imports and every refused request.
        </p>
      </div>
      <p class="tabular text-xs text-[var(--c-text-muted)]">
        {{ (log.data.value?.total ?? 0).toLocaleString() }} entries
      </p>
    </header>

    <Card flush>
      <div class="flex flex-wrap items-center gap-2 border-b px-4 py-2.5">
        <label class="sr-only" for="audit-search">Search</label>
        <input
          id="audit-search"
          v-model="search"
          type="search"
          placeholder="Search actor, action or target"
          class="min-w-0 flex-1 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm placeholder:text-[var(--c-text-muted)]"
        />

        <label class="sr-only" for="audit-outcome">Outcome</label>
        <select id="audit-outcome" v-model="outcome" :class="selectClass">
          <option value="">Every outcome</option>
          <option value="denied">Denied only</option>
          <option value="failure">Failures only</option>
          <option value="success">Successes only</option>
        </select>

        <label class="sr-only" for="audit-category">Category</label>
        <select id="audit-category" v-model="category" :class="selectClass">
          <option value="">All areas</option>
          <option value="authentication">Authentication</option>
          <option value="user">Users</option>
          <option value="role">Roles</option>
          <option value="data">Data access</option>
          <option value="import">Imports</option>
          <option value="system">System</option>
        </select>

        <label class="sr-only" for="audit-action">Action</label>
        <select id="audit-action" v-model="action" :class="selectClass">
          <option value="">All actions</option>
          <option v-for="code in actions.data.value ?? []" :key="code" :value="code">
            {{ code }}
          </option>
        </select>
      </div>

      <AsyncBoundary
        :is-loading="log.isPending.value"
        :is-error="log.isError.value"
        :error="log.error.value"
        :is-empty="(log.data.value?.items.length ?? 0) === 0"
        empty-message="Nothing matches these filters."
        min-height="24rem"
        gap="none"
        @retry="log.refetch()"
      >
        <div class="overflow-x-auto">
          <table class="w-full text-left text-sm">
            <thead
              class="border-b text-2xs tracking-wide text-[var(--c-text-muted)] uppercase"
            >
              <tr>
                <th scope="col" class="px-4 py-2 font-medium">When</th>
                <th scope="col" class="px-4 py-2 font-medium">Who</th>
                <th scope="col" class="px-4 py-2 font-medium">Action</th>
                <th scope="col" class="px-4 py-2 font-medium">Target</th>
                <th scope="col" class="px-4 py-2 font-medium">Source</th>
              </tr>
            </thead>

            <tbody class="divide-y">
              <template v-for="entry in log.data.value?.items ?? []" :key="entry.entryId">
                <tr
                  class="cursor-pointer hover:bg-[var(--c-surface-hover)]"
                  :style="{ backgroundColor: tone[entry.outcome]?.bg }"
                  @click="expanded = expanded === entry.entryId ? null : entry.entryId"
                >
                  <td class="tabular px-4 py-2 whitespace-nowrap">
                    <span :title="formatDateTime(entry.occurredAt)">
                      {{ formatRelative(entry.occurredAt) }}
                    </span>
                  </td>

                  <td class="px-4 py-2">
                    <RouterLink
                      v-if="entry.actorUserId"
                      :to="`/settings/users/${entry.actorUserId}`"
                      class="hover:text-[var(--c-accent)] hover:underline"
                      @click.stop
                    >
                      {{ entry.actorName }}
                    </RouterLink>
                    <!--
                      An actor with no account id is a failed sign-in against a username that does
                      not exist. Keeping those is what makes a password spray visible at all.
                    -->
                    <span v-else class="text-[var(--c-text-muted)]">{{ entry.actorName }}</span>
                  </td>

                  <td class="px-4 py-2">
                    <span
                      class="font-medium"
                      :style="{ color: tone[entry.outcome]?.fg }"
                    >
                      {{ entry.action }}
                    </span>
                    <span
                      v-if="entry.outcome !== 'success'"
                      class="ml-1.5 rounded-full px-1.5 py-0.5 text-2xs font-medium"
                      :style="{
                        backgroundColor: 'var(--c-surface)',
                        color: tone[entry.outcome]?.fg,
                      }"
                    >
                      {{ entry.outcome }}
                    </span>
                  </td>

                  <td class="px-4 py-2 text-xs text-[var(--c-text-secondary)]">
                    <template v-if="entry.targetName || entry.targetId">
                      {{ entry.targetName ?? entry.targetId }}
                      <span v-if="entry.targetType" class="text-[var(--c-text-muted)]">
                        ({{ entry.targetType }})
                      </span>
                    </template>
                    <span v-else class="text-[var(--c-text-muted)]">—</span>
                  </td>

                  <td class="tabular px-4 py-2 text-xs text-[var(--c-text-muted)]">
                    {{ entry.sourceIp ?? '—' }}
                  </td>
                </tr>

                <tr v-if="expanded === entry.entryId">
                  <td colspan="5" class="bg-[var(--c-surface-sunken)] px-4 py-3">
                    <dl class="mb-2 flex flex-wrap gap-x-6 gap-y-1 text-xs">
                      <div>
                        <dt class="inline text-[var(--c-text-muted)]">Exact time:</dt>
                        <dd class="tabular inline"> {{ formatDateTime(entry.occurredAt) }}</dd>
                      </div>
                      <div v-if="entry.correlationId">
                        <dt class="inline text-[var(--c-text-muted)]">Correlation:</dt>
                        <dd class="inline font-mono"> {{ entry.correlationId }}</dd>
                      </div>
                      <div>
                        <dt class="inline text-[var(--c-text-muted)]">Entry:</dt>
                        <dd class="inline font-mono"> {{ entry.entryId }}</dd>
                      </div>
                    </dl>

                    <pre
                      v-if="entry.detail"
                      class="overflow-x-auto rounded-[var(--radius-md)] border bg-[var(--c-surface)] p-2.5 font-mono text-2xs text-[var(--c-text-secondary)]"
                      >{{ detailText(entry) }}</pre
                    >
                    <p v-else class="text-xs text-[var(--c-text-muted)]">
                      No additional detail recorded.
                    </p>
                  </td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>

        <Pagination
          :page="log.data.value?.page ?? 1"
          :page-size="log.data.value?.pageSize ?? 50"
          :total="log.data.value?.total ?? 0"
          :loading="log.isFetching.value"
          @update:page="(p) => (page = p)"
        />
      </AsyncBoundary>
    </Card>

    <p class="text-xs text-[var(--c-text-muted)]">
      Subscriber numbers are never recorded here. A lookup is audited as who, when and how many
      results — an audit log full of MSISDNs would be a second copy of the data it exists to
      protect.
    </p>
  </div>
</template>
