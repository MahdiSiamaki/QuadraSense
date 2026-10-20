<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import AdvancedExplorer from './AdvancedExplorer.vue'
import SimpleExplorer from './SimpleExplorer.vue'

/**
 * The Explorer, in two modes.
 *
 * Simple, the default, is one search box for a number, a SIM or a handset, and what is known about
 * it: its details, what it is bound to, and its history. It replaced three pages - Subscriber
 * lookup, IMSI search and the Relationship explorer - and calls their endpoints, so every lookup is
 * checked and audited exactly as it was. Anyone who could open one of those pages can open it, and
 * it offers only the searches the user may run.
 *
 * Advanced is the query builder that was the whole Explorer, unchanged, behind explorer.query.
 *
 * The mode is in the address (`?mode=advanced`), so a link, Refresh and Back keep it. No identifier
 * ever is: lookups are POSTed, and what was looked up lives in the page.
 */
type Mode = 'simple' | 'advanced'

const route = useRoute()
const router = useRouter()
const { can, canAny } = useAuth()

const canSimple = computed(() => canAny(Permission.LookupSubscriber, Permission.LookupImsi, Permission.LookupImei))
const canAdvanced = computed(() => can(Permission.ExplorerQuery))

const mode = computed<Mode>({
  get: () => {
    if (!canSimple.value) return 'advanced'
    return route.query['mode'] === 'advanced' && canAdvanced.value ? 'advanced' : 'simple'
  },
  set: (value) => {
    // Replace, not push: switching mode is a choice within the page, and Back leaves the page.
    void router.replace({ query: { ...route.query, mode: value === 'advanced' ? 'advanced' : undefined } })
  },
})

const modes = [
  { value: 'simple' as const, label: 'Simple', title: 'Search for one number, SIM or handset' },
  { value: 'advanced' as const, label: 'Advanced', title: 'Build a query over all bindings and events' },
]
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Explorer</h1>
        <p v-if="mode === 'simple'" class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          Find one subscriber number, SIM or handset, and see its details, what it is bound to and its history.
        </p>
        <p v-else class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          Ask questions of current bindings and the dated event log. Every query is costed by the server before it
          runs, kept within a budget, and recorded in the audit log - which fields, never which values.
        </p>
      </div>

      <SegmentedControl v-if="canSimple && canAdvanced" v-model="mode" :options="modes" label="Explorer mode" />
    </header>

    <SimpleExplorer v-if="mode === 'simple'" />
    <AdvancedExplorer v-else />
  </div>
</template>
