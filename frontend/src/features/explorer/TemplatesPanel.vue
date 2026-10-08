<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import type { ExplorerField, ExplorerQueryRequest } from '@/api/explorer'
import Button from '@/design-system/Button.vue'
import { permissionsNeeded } from './model'
import { TEMPLATES, type ExplorerTemplate, type TemplateCategory } from './templates'
import { control, miniLabel, mono } from './ui'

/**
 * The template gallery. Choosing one asks only for its parameters; running it fills the
 * builder with the query it stands for and runs that, so nothing a template does is hidden.
 *
 * A template needing a lookup permission the user lacks is shown, greyed, with the permission
 * named - the same rule as a field in the builder. Hiding it would leave people wondering whether
 * the question can be asked at all.
 */
const props = defineProps<{
  fieldsByDataset: Record<string, ExplorerField[]>
  dataThrough: string | null
  can: (permission: string) => boolean
}>()

const emit = defineEmits<{ use: [request: ExplorerQueryRequest, template: ExplorerTemplate] }>()

const CATEGORIES: TemplateCategory[] = ['Look up', 'History', 'Observations', 'Data quality']

const ctx = computed(() => ({ dataThrough: props.dataThrough ?? new Date().toISOString().slice(0, 10) }))

const open = ref<string | null>(null)

/** The template whose parameters are showing, if any. */
const openTemplate = computed(() => TEMPLATES.find((x) => x.id === open.value) ?? null)
const values = reactive<Record<string, Record<string, string>>>({})

function missing(t: ExplorerTemplate): string[] {
  const request = t.build(t.defaults(ctx.value), ctx.value)
  return permissionsNeeded(request, props.fieldsByDataset[request.dataset] ?? []).filter((p) => !props.can(p))
}

function choose(t: ExplorerTemplate) {
  open.value = open.value === t.id ? null : t.id
  values[t.id] ??= t.defaults(ctx.value)
}

function ready(t: ExplorerTemplate): boolean {
  const v = values[t.id]
  return !!v && t.params.every((p) => (v[p.key] ?? '').trim().length > 0)
}

function run(t: ExplorerTemplate) {
  const v = values[t.id]
  if (!v || !ready(t)) return
  emit('use', t.build(v, ctx.value), t)
}

const inputType = (kind: string) => (kind === 'date' ? 'date' : kind === 'count' ? 'number' : 'text')
const numeric = (kind: string) => ['msisdn', 'imsi', 'imei', 'prefix', 'count'].includes(kind)
</script>

<template>
  <div class="flex flex-col gap-6">
    <section v-for="category in CATEGORIES" :key="category" class="flex flex-col gap-2">
      <h3 class="text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">{{ category }}</h3>

      <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
        <article
          v-for="t in TEMPLATES.filter((x) => x.category === category)"
          :key="t.id"
          class="flex flex-col gap-2 rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-3 transition-colors"
          :class="open === t.id ? 'border-[var(--c-accent)]' : ''"
        >
          <button
            type="button"
            class="flex flex-col gap-1 text-left disabled:cursor-not-allowed"
            :aria-expanded="open === t.id"
            :disabled="missing(t).length > 0"
            @click="choose(t)"
          >
            <span class="text-sm font-semibold" :class="missing(t).length ? 'text-[var(--c-text-muted)]' : ''">
              {{ t.title }}
            </span>
            <span class="text-xs text-[var(--c-text-secondary)]">{{ t.description }}</span>
            <span v-if="missing(t).length" class="text-2xs text-[var(--c-warning-text)]">
              Needs {{ missing(t).join(', ') }}
            </span>
          </button>

        </article>
      </div>

      <!--
        The parameters, once, under the category's cards. Inside the chosen card they made it
        tall, and the grid stretched its row neighbours into tall, mostly empty boxes.
      -->
      <form
        v-if="openTemplate?.category === category && values[openTemplate.id]"
        class="flex flex-col gap-2 rounded-[var(--radius-lg)] border border-[var(--c-accent)] bg-[var(--c-surface)] p-3"
        @submit.prevent="run(openTemplate)"
      >
        <p class="text-sm font-semibold">{{ openTemplate.title }}</p>
        <div class="flex flex-wrap gap-2">
          <label v-for="p in openTemplate.params" :key="p.key" class="flex min-w-32 flex-1 flex-col gap-0.5 sm:max-w-xs">
            <span :class="miniLabel">{{ p.label }}</span>
            <input
              v-model="values[openTemplate.id]![p.key]"
              :type="inputType(p.kind)"
              :inputmode="numeric(p.kind) ? 'numeric' : undefined"
              :min="p.kind === 'count' ? 0 : undefined"
              :placeholder="p.placeholder"
              autocomplete="off"
              spellcheck="false"
              :class="numeric(p.kind) && p.kind !== 'count' ? mono : control"
            />
          </label>
        </div>

        <p v-if="openTemplate.caution" class="text-2xs text-pretty text-[var(--c-text-muted)]">{{ openTemplate.caution }}</p>

        <Button type="submit" size="sm" variant="primary" class="w-fit" :disabled="!ready(openTemplate)">Run</Button>
      </form>
    </section>
  </div>
</template>
