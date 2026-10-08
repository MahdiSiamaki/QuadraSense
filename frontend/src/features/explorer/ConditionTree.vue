<script setup lang="ts">
import { computed } from 'vue'
import type { ExplorerField } from '@/api/explorer'
import Button from '@/design-system/Button.vue'
import ConditionRow from './ConditionRow.vue'
import { emptyGroup, newCondition, type GroupDraft } from './model'
import { iconButton, segment } from './ui'

/**
 * A group of conditions - AND or OR, optionally negated - and the groups inside it.
 *
 * The connective is drawn between the children, not only chosen once at the top: a reader
 * checking a nested query reads "A AND B AND (C OR D)" down the page, and a single toggle above
 * a list of six rows makes them hold the logic in their head.
 */
const props = withDefaults(
  defineProps<{
    node: GroupDraft
    fields: ExplorerField[]
    depth?: number
    maxDepth: number
    problems: Map<string, string[]>
    allowed: (field: ExplorerField) => boolean
    /** Words for the add button: "condition", or "measure condition" in Having. */
    noun?: string
  }>(),
  { depth: 0, noun: 'condition' },
)

defineEmits<{ remove: [] }>()

const isRoot = computed(() => props.depth === 0)
const firstAllowed = computed(() => props.fields.find((f) => props.allowed(f)))

function addCondition() {
  props.node.children.push(newCondition(firstAllowed.value))
}

function addGroup() {
  const group = emptyGroup(props.node.logic === 'And' ? 'Or' : 'And')
  group.children.push(newCondition(firstAllowed.value))
  props.node.children.push(group)
}

function removeChild(index: number) {
  props.node.children.splice(index, 1)
}
</script>

<template>
  <div
    class="flex flex-col gap-2"
    :class="
      isRoot
        ? ''
        : 'rounded-[var(--radius-md)] border border-dashed bg-[var(--c-surface-sunken)]/50 p-2.5'
    "
  >
    <div class="flex flex-wrap items-center gap-2">
      <div class="inline-flex overflow-hidden rounded-[var(--radius-md)] border" role="radiogroup" :aria-label="isRoot ? 'Rows must match' : 'This group must match'">
        <button
          type="button"
          role="radio"
          :aria-checked="node.logic === 'And'"
          :class="segment(node.logic === 'And')"
          @click="node.logic = 'And'"
        >
          All (AND)
        </button>
        <button
          type="button"
          role="radio"
          :aria-checked="node.logic === 'Or'"
          :class="segment(node.logic === 'Or')"
          @click="node.logic = 'Or'"
        >
          Any (OR)
        </button>
      </div>

      <label class="inline-flex items-center gap-1 text-2xs font-medium text-[var(--c-text-muted)]">
        <input v-model="node.not" type="checkbox" />
        NOT this {{ isRoot ? 'whole set' : 'group' }}
      </label>

      <span v-if="node.children.length === 0" class="text-2xs text-[var(--c-text-muted)]">
        {{ isRoot ? 'No conditions: every row.' : 'Empty group.' }}
      </span>

      <button
        v-if="!isRoot"
        type="button"
        :class="iconButton"
        class="ml-auto"
        aria-label="Remove group"
        @click="$emit('remove')"
      >
        <span aria-hidden="true" class="text-lg leading-none">&times;</span>
      </button>
    </div>

    <p v-for="(message, i) in problems.get(node.id) ?? []" :key="i" class="text-2xs text-[var(--c-danger-text)]" role="alert">
      {{ message }}
    </p>

    <ul class="flex flex-col gap-1">
      <li v-for="(child, i) in node.children" :key="child.id" class="flex flex-col gap-1">
        <span
          v-if="i > 0"
          class="w-fit rounded-[var(--radius-sm)] bg-[var(--c-surface-sunken)] px-1.5 text-2xs font-semibold tracking-wide text-[var(--c-text-muted)]"
          aria-hidden="true"
        >
          {{ node.logic.toUpperCase() }}
        </span>

        <ConditionTree
          v-if="child.kind === 'group'"
          :node="child"
          :fields="fields"
          :depth="depth + 1"
          :max-depth="maxDepth"
          :problems="problems"
          :allowed="allowed"
          :noun="noun"
          @remove="removeChild(i)"
        />
        <ConditionRow
          v-else
          :node="child"
          :fields="fields"
          :problems="problems.get(child.id)"
          :allowed="allowed"
          @remove="removeChild(i)"
        />
      </li>
    </ul>

    <div class="flex flex-wrap gap-1.5">
      <Button size="sm" variant="ghost" :disabled="!firstAllowed" @click="addCondition">+ Add {{ noun }}</Button>
      <Button v-if="depth + 1 < maxDepth" size="sm" variant="ghost" :disabled="!firstAllowed" @click="addGroup">
        + Add group
      </Button>
    </div>
  </div>
</template>
