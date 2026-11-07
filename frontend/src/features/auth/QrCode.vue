<script setup lang="ts">
import { computed } from 'vue'
import { encode } from 'uqr'

/**
 * A QR code, drawn as one SVG path.
 *
 * Drawn here rather than fetched: what it carries is an authenticator secret, which must not go to
 * any service, and the security model allows no image from elsewhere anyway.
 *
 * Black on white in every theme, and those two are the only literal colours in the application's
 * components: a scanner needs dark on light with a quiet border, and a code that followed the dark
 * theme would stop being a code.
 */
const props = withDefaults(defineProps<{ value: string; size?: number }>(), { size: 176 })

const matrix = computed(() => encode(props.value, { ecc: 'M', border: 2 }))

const path = computed(() => {
  let d = ''
  matrix.value.data.forEach((row, y) => {
    row.forEach((dark, x) => {
      if (dark) d += `M${x} ${y}h1v1h-1z`
    })
  })
  return d
})
</script>

<template>
  <svg
    :width="size"
    :height="size"
    :viewBox="`0 0 ${matrix.size} ${matrix.size}`"
    shape-rendering="crispEdges"
    role="img"
    aria-label="QR code for your authenticator app"
    class="block shrink-0 rounded-[var(--radius-md)]"
  >
    <rect :width="matrix.size" :height="matrix.size" fill="#fff" />
    <path :d="path" fill="#000" />
  </svg>
</template>
