<script setup lang="ts">
import { useTheme, type ThemeMode } from '@/lib/theme'

/**
 * The theme, with its third state.
 *
 * The icon in the top bar flips between light and dark, and in doing so makes an explicit choice.
 * "Follow the system" - the default for anyone who never touched it - had no way back once
 * someone had, short of clearing site data. It lives here.
 */
const { mode } = useTheme()

const options: Array<{ value: ThemeMode; label: string; hint: string }> = [
  { value: 'system', label: 'System', hint: 'Follows your operating system, and changes with it.' },
  { value: 'light', label: 'Light', hint: 'Always light.' },
  { value: 'dark', label: 'Dark', hint: 'Always dark.' },
]
</script>

<template>
  <div class="flex flex-col gap-5">
    <header>
      <h2 class="text-lg font-semibold tracking-tight">Appearance</h2>
      <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
        Kept in this browser only. It changes nothing for anyone else.
      </p>
    </header>

    <fieldset class="grid gap-3 sm:grid-cols-3">
      <legend class="sr-only">Theme</legend>
      <label
        v-for="option in options"
        :key="option.value"
        class="flex cursor-pointer flex-col gap-3 rounded-[var(--radius-lg)] border p-3 transition-colors hover:bg-[var(--c-surface-hover)] has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-[var(--c-accent)]"
        :class="mode === option.value ? 'border-[var(--c-accent)] bg-[var(--c-surface-sunken)]' : ''"
      >
        <!-- A miniature of the page in that theme: bar, card, a line of text. -->
        <span
          class="preview"
          :class="`preview--${option.value}`"
          aria-hidden="true"
        >
          <span class="half half-light"><i /><b /><b /></span>
          <span class="half half-dark"><i /><b /><b /></span>
        </span>

        <span class="flex items-start gap-2">
          <input
            v-model="mode"
            type="radio"
            name="theme"
            :value="option.value"
            class="mt-0.5 accent-[var(--c-accent)] focus-visible:outline-none"
          />
          <span>
            <span class="block text-sm font-medium">{{ option.label }}</span>
            <span class="block text-xs text-[var(--c-text-muted)]">{{ option.hint }}</span>
          </span>
        </span>
      </label>
    </fieldset>
  </div>
</template>

<style scoped>
/*
  Each preview shows one theme whichever theme is active, so it cannot read the tokens: they are
  bound to :root and change with the theme. The values are copied from tokens.css - canvas,
  surface, border, muted text - so the miniature is the real palette, not an impression of it.
*/
.preview {
  display: flex;
  height: 4.5rem;
  overflow: hidden;
  border-radius: var(--radius-md);
  border: 1px solid var(--c-border);
}

.half {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 0.3rem;
  padding: 0.5rem;
}

.half i {
  display: block;
  height: 0.5rem;
  border-radius: 2px;
}

.half b {
  display: block;
  height: 0.35rem;
  width: 70%;
  border-radius: 2px;
}

.half b + b {
  width: 45%;
}

.half-light {
  background: oklch(98.4% 0.002 250);
}

.half-light i {
  background: oklch(100% 0 0);
  box-shadow: 0 0 0 1px oklch(91.5% 0.005 250);
}

.half-light b {
  background: oklch(53% 0.011 250 / 0.45);
}

.half-dark {
  background: oklch(17.5% 0.008 260);
}

.half-dark i {
  background: oklch(21% 0.009 260);
  box-shadow: 0 0 0 1px oklch(29% 0.011 260);
}

.half-dark b {
  background: oklch(65% 0.011 260 / 0.45);
}

/* System shows both halves; light and dark only their own. */
.preview--light .half-dark,
.preview--dark .half-light {
  display: none;
}
</style>
