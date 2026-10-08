import { ref, watch, readonly } from 'vue'

export type ThemeMode = 'light' | 'dark' | 'system'

// public/theme-init.js repeats this key and the rule in apply() before the bundle loads, so the
// first paint is in the reader's theme: change one, change the other.
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
  const root = document.documentElement
  const next = dark ? 'dark' : 'light'
  // public/theme-init.js has usually set it before the first paint already.
  if (root.dataset['theme'] === next) return
  // The new colours apply with every transition off (tokens.css), in one style pass; a second
  // pass turns transitions back on, and since nothing changes in it, none starts. Both passes are
  // synchronous: a timer could let a frame render with the theme icon's own morph switched off.
  root.dataset['themeSwitching'] = ''
  root.dataset['theme'] = next
  void document.body?.offsetWidth
  delete root.dataset['themeSwitching']
  void document.body?.offsetWidth
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
