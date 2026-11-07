<script setup lang="ts">
import { ref } from 'vue'
import Button from '@/design-system/Button.vue'

/**
 * Recovery codes, the one time they can be seen.
 *
 * Copy and download both, because a person's password manager and a person's filing cabinet are
 * both right answers. The download is made in the browser: these never touch a server again.
 */
const props = defineProps<{ codes: readonly string[] }>()

const copied = ref(false)

async function copy() {
  try {
    await navigator.clipboard.writeText(props.codes.join('\n'))
    copied.value = true
    setTimeout(() => (copied.value = false), 2000)
  } catch {
    // A browser that refuses the clipboard still shows the codes, and the download works.
  }
}

function download() {
  const text = `Quadrasense recovery codes\nEach works once.\n\n${props.codes.join('\n')}\n`
  const link = document.createElement('a')
  link.href = URL.createObjectURL(new Blob([text], { type: 'text/plain' }))
  link.download = 'quadrasense-recovery-codes.txt'
  link.click()
  URL.revokeObjectURL(link.href)
}
</script>

<template>
  <div class="flex flex-col gap-3">
    <ul
      class="tabular grid gap-x-6 gap-y-1 rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2.5 font-mono text-xs sm:grid-cols-2"
      aria-label="Recovery codes"
    >
      <li v-for="code in codes" :key="code" class="whitespace-nowrap">{{ code }}</li>
    </ul>

    <div class="flex flex-wrap items-center gap-2">
      <Button size="sm" @click="copy">Copy</Button>
      <Button size="sm" @click="download">Download</Button>
      <span v-if="copied" class="text-xs text-[var(--c-success-text)]" role="status">Copied.</span>
    </div>
  </div>
</template>
