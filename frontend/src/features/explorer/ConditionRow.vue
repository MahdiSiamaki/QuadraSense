<script setup lang="ts">
import { computed, useId } from 'vue'
import type { ExplorerField, ExplorerOperator } from '@/api/explorer'
import { arity, blankValues, isList, operatorLabel, type ConditionDraft } from './model'
import { control, DIGIT_TYPES, iconButton, mono } from './ui'

/**
 * One condition: NOT, a field, an operator, and the values that operator takes.
 *
 * Edits the node in place. The node belongs to the page's reactive draft, and a tree editor that
 * re-emitted every keystroke up through six levels of groups would be all plumbing.
 *
 * The inputs follow the field: a date picker for a date, a list for Label fields, digits in
 * monospace for identifiers. What an identifier may look like is the server's decision - it
 * normalises "0912 345 6789" to the stored number and says precisely what is wrong otherwise,
 * and the message comes back to this row.
 */
const props = defineProps<{
  node: ConditionDraft
  /** Fields a condition here may use: the dataset's, or the query's measures in Having. */
  fields: ExplorerField[]
  problems?: string[]
  /** Whether the user holds a field's permission; a field they lack is shown but not offered. */
  allowed: (field: ExplorerField) => boolean
}>()

defineEmits<{ remove: [] }>()

const id = useId()
const field = computed(() => props.fields.find((f) => f.name === props.node.field))
const operators = computed(() => field.value?.operators ?? [])
const bounds = computed(() => arity(props.node.operator))

function setField(name: string) {
  const next = props.fields.find((f) => f.name === name)
  props.node.field = name
  if (!next) return
  if (!next.operators.includes(props.node.operator)) {
    props.node.operator = next.operators.includes('Equals') ? 'Equals' : (next.operators[0] ?? 'Equals')
  }
  props.node.values = blankValues(props.node.operator, next.type)
}

function setOperator(op: ExplorerOperator) {
  const wasList = isList(props.node.operator)
  props.node.operator = op
  if (isList(op) !== wasList) {
    props.node.values = isList(op) ? [props.node.values.filter((v) => v).join(', ')] : blankValues(op, field.value?.type)
    return
  }
  const { max } = arity(op)
  const kept = props.node.values.slice(0, max)
  while (kept.length < arity(op).min) kept.push('')
  props.node.values = kept
}

const inputType = computed(() => (field.value?.type === 'Date' ? 'date' : field.value?.type === 'Number' ? 'number' : 'text'))
const digits = computed(() => DIGIT_TYPES.has(field.value?.type ?? ''))
</script>

<template>
  <div
    class="flex flex-col gap-1 rounded-[var(--radius-md)] py-1"
    :class="problems?.length ? 'bg-[var(--c-danger-subtle)] px-2' : ''"
  >
    <div class="flex flex-wrap items-center gap-1.5">
      <label
        class="inline-flex items-center gap-1 text-2xs font-medium text-[var(--c-text-muted)]"
        :title="'Negates this condition'"
      >
        <input v-model="node.not" type="checkbox" />
        NOT
      </label>

      <select
        :id="`${id}-field`"
        :value="node.field"
        :class="control"
        class="min-w-36"
        aria-label="Field"
        @change="setField(($event.target as HTMLSelectElement).value)"
      >
        <option value="" disabled>Choose a field…</option>
        <option v-for="f in fields" :key="f.name" :value="f.name" :disabled="!allowed(f)">
          {{ f.label }}{{ allowed(f) ? '' : ` — needs ${f.permission}` }}
        </option>
      </select>

      <select
        v-if="field"
        :value="node.operator"
        :class="control"
        aria-label="Comparison"
        @change="setOperator(($event.target as HTMLSelectElement).value as ExplorerOperator)"
      >
        <option v-for="op in operators" :key="op" :value="op">{{ operatorLabel(op, field.type) }}</option>
      </select>

      <template v-if="field && bounds.max > 0">
        <!-- A list: typed or pasted, one per line or comma-separated. -->
        <textarea
          v-if="isList(node.operator)"
          v-model="node.values[0]"
          rows="1"
          :class="digits ? mono : control"
          class="min-w-56 flex-1 resize-y"
          aria-label="Values, separated by commas or new lines"
          placeholder="Values, separated by commas or new lines"
        />

        <template v-else>
          <template v-for="(_, i) in bounds.min" :key="i">
            <span v-if="i > 0" class="text-2xs text-[var(--c-text-muted)]">and</span>

            <select
              v-if="field.type === 'Boolean'"
              v-model="node.values[i]"
              :class="control"
              :aria-label="`${field.label} value`"
            >
              <option value="true">{{ field.name === 'active' ? 'active' : 'yes' }}</option>
              <option value="false">{{ field.name === 'active' ? 'ended' : 'no' }}</option>
            </select>

            <select
              v-else-if="field.type === 'Label' && field.values"
              v-model="node.values[i]"
              :class="control"
              :aria-label="`${field.label} value`"
            >
              <option value="" disabled>Choose…</option>
              <option v-for="v in field.values" :key="v" :value="v">{{ v }}</option>
            </select>

            <input
              v-else
              v-model="node.values[i]"
              :type="inputType"
              :inputmode="digits || field.type === 'Number' ? 'numeric' : undefined"
              :min="field.type === 'Number' ? 0 : undefined"
              autocomplete="off"
              spellcheck="false"
              :class="digits ? mono : control"
              class="w-40"
              :aria-label="bounds.min > 1 ? `${field.label}, ${i === 0 ? 'from' : 'to'}` : `${field.label} value`"
            />
          </template>
        </template>
      </template>

      <button type="button" :class="iconButton" class="ml-auto" aria-label="Remove condition" @click="$emit('remove')">
        <span aria-hidden="true" class="text-lg leading-none">&times;</span>
      </button>
    </div>

    <p v-if="field?.description" class="text-2xs text-pretty text-[var(--c-text-muted)]">
      {{ field.description }}
    </p>
    <p v-for="(message, i) in problems" :key="i" class="text-2xs text-[var(--c-danger)]" role="alert">
      {{ message }}
    </p>
  </div>
</template>
