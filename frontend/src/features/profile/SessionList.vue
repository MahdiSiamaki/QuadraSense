<script setup lang="ts">
import { computed } from 'vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import type { SessionInfo } from '@/api/auth'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * The devices an account has signed in from.
 *
 * Ended sessions are shown too, for thirty days, and that is the useful half: a live session
 * from an unfamiliar address is alarming, but so is one that ended at 3am from somewhere the
 * person has never been. A list of only current sessions cannot show that.
 *
 * The user agent is summarised rather than printed raw. A 140-character string is not something
 * anyone reads, and "Chrome on Windows" is the part that tells you whether it was you.
 */
const props = defineProps<{
  query: {
    data: { value: SessionInfo[] | undefined }
    isPending: { value: boolean }
    isError: { value: boolean }
    error: { value: unknown }
    refetch: () => unknown
  }
  /** Hides the revoke control when the viewer may not use it. */
  readonly?: boolean
  revoking?: boolean
}>()

defineEmits<{ revoke: [id: string] }>()

const sessions = computed(() => props.query.data.value ?? [])

/** Whether the session can still be used right now. */
function isLive(session: SessionInfo): boolean {
  return session.revokedAt === null && new Date(session.idleExpiresAt) > new Date()
}

/**
 * Turns a user-agent string into something a person can recognise.
 *
 * Deliberately crude: it names a browser and a platform and gives up otherwise. Anything more
 * would be user-agent parsing, which is a library, a dependency and a source of wrong answers.
 */
function describe(agent: string | null): string {
  if (!agent) return 'Unknown device'

  const browser =
    /Edg\//.test(agent) ? 'Edge'
    : /OPR\//.test(agent) ? 'Opera'
    : /Chrome\//.test(agent) ? 'Chrome'
    : /Firefox\//.test(agent) ? 'Firefox'
    : /Safari\//.test(agent) ? 'Safari'
    : /curl/i.test(agent) ? 'curl'
    : null

  const platform =
    /Windows/.test(agent) ? 'Windows'
    : /Android/.test(agent) ? 'Android'
    : /iPhone|iPad/.test(agent) ? 'iOS'
    : /Mac OS X/.test(agent) ? 'macOS'
    : /Linux/.test(agent) ? 'Linux'
    : null

  if (browser && platform) return `${browser} on ${platform}`
  if (browser) return browser
  if (platform) return platform
  return agent.slice(0, 40)
}
</script>

<template>
  <AsyncBoundary
    :is-loading="query.isPending.value"
    :is-error="query.isError.value"
    :error="query.error.value"
    min-height="6rem"
    @retry="query.refetch()"
  >
    <p
      v-if="sessions.length === 0"
      class="px-4 py-6 text-center text-xs text-[var(--c-text-muted)]"
    >
      No sessions recorded.
    </p>

    <ul v-else class="divide-y">
      <li
        v-for="session in sessions"
        :key="session.id"
        class="flex items-start justify-between gap-3 px-4 py-2.5"
      >
        <div class="min-w-0">
          <p class="flex items-center gap-1.5 text-sm">
            <span
              class="size-1.5 shrink-0 rounded-full"
              :style="{
                backgroundColor: isLive(session)
                  ? 'var(--c-success)'
                  : 'var(--c-text-muted)',
              }"
              aria-hidden="true"
            />
            <span class="truncate">{{ describe(session.userAgent) }}</span>
            <span
              v-if="session.isCurrent"
              class="shrink-0 rounded-full px-1.5 py-0.5 text-2xs font-medium"
              :style="{
                backgroundColor: 'var(--c-accent-subtle)',
                color: 'var(--c-accent)',
              }"
            >
              this browser
            </span>
          </p>

          <p class="tabular mt-0.5 text-2xs text-[var(--c-text-muted)]">
            <span v-if="session.ip">{{ session.ip }} · </span>
            <span :title="formatDateTime(session.lastSeenAt)">
              last used {{ formatRelative(session.lastSeenAt) }}
            </span>
            <template v-if="session.revokedAt">
              · ended {{ formatRelative(session.revokedAt) }}
              <template v-if="session.revokedReason">({{ session.revokedReason }})</template>
            </template>
            <template v-else-if="!isLive(session)"> · expired</template>
          </p>
        </div>

        <button
          v-if="!readonly && isLive(session) && !session.isCurrent"
          type="button"
          class="shrink-0 rounded-[var(--radius-md)] border px-2 py-1 text-2xs font-medium text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)] disabled:opacity-40"
          :disabled="revoking"
          @click="$emit('revoke', session.id)"
        >
          Sign out
        </button>
      </li>
    </ul>
  </AsyncBoundary>
</template>
