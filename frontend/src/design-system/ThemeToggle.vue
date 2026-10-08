<script setup lang="ts">
import { useTheme } from '@/lib/theme'

/**
 * The theme switch: a sun that becomes a moon.
 *
 * One drawing, not two icons swapped. The disc grows and a second, masking disc slides across it
 * to cut the crescent, while the rays turn and draw in towards the centre - so the change reads as
 * the same object moving between two states rather than a picture being replaced. It shows the
 * theme in force now; the label says what pressing it does.
 *
 * Still a button to the browser - focusable, announced, operated by Enter and Space - only no
 * longer drawn as one. Motion is dropped under prefers-reduced-motion; the icon still changes.
 *
 * Colours come from the design tokens through currentColor, so it needs no chart-colors
 * resolution: this is CSS painting, not a canvas.
 */
const { isDark, toggle } = useTheme()

const rays = Array.from({ length: 8 }, (_, i) => i * 45)
</script>

<template>
  <button
    type="button"
    class="theme-toggle"
    :class="{ 'is-dark': isDark }"
    :aria-label="isDark ? 'Switch to light theme' : 'Switch to dark theme'"
    :title="isDark ? 'Switch to light theme' : 'Switch to dark theme'"
    @click="toggle"
  >
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
      <mask id="theme-toggle-crescent">
        <rect x="0" y="0" width="24" height="24" fill="white" />
        <circle class="bite" cx="17" cy="7" r="7.5" fill="black" />
      </mask>

      <!-- The mask sits on the group, so the bite moves in fixed coordinates while the disc
           inside it scales. -->
      <g mask="url(#theme-toggle-crescent)">
        <circle class="disc" cx="12" cy="12" r="8.5" fill="currentColor" />
      </g>

      <g class="rays" stroke="currentColor" stroke-width="2" stroke-linecap="round">
        <line
          v-for="angle in rays"
          :key="angle"
          x1="12"
          y1="2.5"
          x2="12"
          y2="4.5"
          :transform="`rotate(${angle} 12 12)`"
        />
      </g>
    </svg>
  </button>
</template>

<style scoped>
.theme-toggle {
  display: inline-grid;
  place-items: center;
  width: 2.25rem;
  height: 2.25rem;
  border-radius: 9999px;
  /* The words colour, not the dot colour: the light sun was 2.73:1, under the 3:1 a control's icon
     needs. Now 5.65:1. */
  color: var(--c-warning-text);
  background: transparent;
  cursor: pointer;
  transition:
    color 400ms ease,
    background-color 150ms ease;
}

.theme-toggle:hover {
  background: var(--c-surface-hover);
}

.theme-toggle.is-dark {
  color: var(--c-accent);
}

/*
  Transforms only. Animating r and cx directly would read more simply, but Safari does not treat
  them as animatable CSS properties, and the icon would jump there instead of move.
*/
.disc,
.bite,
.rays {
  transform-box: view-box;
  transform-origin: 12px 12px;
  transition:
    transform 500ms cubic-bezier(0.22, 1, 0.36, 1),
    opacity 300ms ease;
}

/* Sun: a small disc, the bite moved clear of it, rays out. */
.disc {
  transform: scale(0.59);
}

.bite {
  transform: translate(8px, -8px);
}

/* Moon: the full disc, bitten from the upper right; the rays turn and draw in to nothing. */
.is-dark .disc {
  transform: scale(1);
}

.is-dark .bite {
  transform: translate(0, 0);
}

.is-dark .rays {
  transform: rotate(90deg) scale(0.4);
  opacity: 0;
}

@media (prefers-reduced-motion: reduce) {
  .theme-toggle,
  .disc,
  .bite,
  .rays {
    transition: none;
  }
}
</style>
