import { ref, watch, readonly } from 'vue'

export type ThemeMode = 'light' | 'dark' | 'system'

const STORAGE_KEY = 'sqm.theme'

const mode = ref<ThemeMode>(readStoredMode())
const isDark = ref(false)

function readStoredMode(): ThemeMode {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored === 'light' || stored === 'dark' || stored === 'system') return stored
  } catch {
    // Private windows and locked-down browsers throw on storage access. A theme
    // preference is not worth breaking the app over.
  }
  return 'system'
}

const systemPrefersDark =
  typeof window !== 'undefined' ? window.matchMedia('(prefers-color-scheme: dark)') : null

function apply() {
  const dark = mode.value === 'dark' || (mode.value === 'system' && (systemPrefersDark?.matches ?? false))
  isDark.value = dark
  document.documentElement.dataset['theme'] = dark ? 'dark' : 'light'
}

watch(mode, (next) => {
  try {
    localStorage.setItem(STORAGE_KEY, next)
  } catch {
    // Same reasoning as above: best-effort persistence.
  }
  apply()
})

systemPrefersDark?.addEventListener('change', () => {
  // Only relevant while following the system; an explicit choice wins.
  if (mode.value === 'system') apply()
})

apply()

/**
 * Theme state.
 *
 * Three states, not two: "system" is the default and must stay distinct from an
 * explicit light choice, otherwise a user who has never touched the control gets
 * frozen into whatever their OS happened to be on first load.
 */
export function useTheme() {
  return {
    mode,
    isDark: readonly(isDark),
    setMode: (next: ThemeMode) => {
      mode.value = next
    },
    toggle: () => {
      mode.value = isDark.value ? 'light' : 'dark'
    },
  }
}
