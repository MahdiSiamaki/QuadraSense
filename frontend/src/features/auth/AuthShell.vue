<script setup lang="ts">
import BrandMark from '@/design-system/BrandMark.vue'

/**
 * The frame of every page a person sees before they are in: signing in, getting back in, and the
 * two steps that can stand between a good password and the application.
 *
 * Two panels on a desktop. The brand panel is night-blue in both themes, the way the mark and the
 * README's banner are, and its colours are tokens of their own (`--brand-night-*`). It says what
 * this is and nothing else, and it carries no decoration that moves: this page is seen daily.
 *
 * Below `md` the panel folds to a band above the form, so the form is on the first screen of a
 * phone. The form comes after the panel in the document, which is also the reading order.
 *
 * The page is `main` and the heading inside the slot is its h1; the name in the panel is not a
 * heading, or a screen reader would meet the product's name as the title of every step.
 */
withDefaults(defineProps<{ subtitle?: string }>(), { subtitle: 'Primary Inventory' })
</script>

<template>
  <div class="auth-canvas grid min-h-dvh place-items-center px-4 py-6 sm:py-10">
    <main
      class="grid w-full max-w-[52rem] overflow-hidden rounded-[var(--radius-xl)] border bg-[var(--c-surface)] shadow-[var(--shadow-md)] md:min-h-[33rem] md:grid-cols-[minmax(0,5fr)_minmax(0,6fr)]"
    >
      <aside class="auth-panel relative flex min-h-36 items-center justify-center md:min-h-0">
        <div class="relative z-10 flex flex-col items-center gap-3 px-6 py-7 pb-12 text-center md:pb-24">
          <span class="auth-mark"><BrandMark :size="64" /></span>
          <div>
            <p class="text-2xl font-semibold tracking-tight">Quadrasense</p>
            <p class="auth-panel-note mt-0.5 text-sm">{{ subtitle }}</p>
          </div>
        </div>

        <!-- Two bands, not one: the depth is what keeps the panel from reading as a flat poster. -->
        <svg
          class="absolute inset-x-0 bottom-0 h-16 w-full md:h-36"
          viewBox="0 0 400 140"
          preserveAspectRatio="none"
          aria-hidden="true"
        >
          <defs>
            <linearGradient id="auth-wave" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" style="stop-color: var(--brand-wave-from)" />
              <stop offset="1" style="stop-color: var(--brand-wave-to)" />
            </linearGradient>
          </defs>
          <path
            d="M0 66 C 70 28, 150 34, 226 66 S 352 104, 400 58 V140 H0 Z"
            style="fill: var(--brand-wave-from); opacity: 0.28"
          />
          <path d="M0 104 C 92 58, 196 74, 262 98 S 366 118, 400 84 V140 H0 Z" fill="url(#auth-wave)" />
        </svg>
      </aside>

      <section class="flex min-w-0 flex-col justify-center gap-5 px-6 py-8 sm:px-10 sm:py-10">
        <slot />
      </section>
    </main>
  </div>
</template>

<style scoped>
.auth-canvas {
  background-color: var(--c-canvas);
}

.auth-panel {
  color: var(--brand-on-night);
  background-image: linear-gradient(
    150deg,
    var(--brand-night-1),
    var(--brand-night-2) 55%,
    var(--brand-night-3)
  );
}

.auth-panel-note {
  color: var(--brand-on-night-muted);
}

/* The mark's ring is navy on a light surface, which would vanish here. */
.auth-mark {
  --brand-deep: var(--brand-on-night);
}
</style>
