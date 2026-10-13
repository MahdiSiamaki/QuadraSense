import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

/*
  Unit tests for logic that decides behaviour: the dashboard draft, layout, lens, validation,
  migrations and templates, and the components that carry rules (a widget that must not query, a
  dialog that must not add an invalid widget).

  Tests live in tests/unit/, mirroring src/, so the production type-check (tsconfig.app.json, src
  only) and bundle never see them. happy-dom rather than jsdom: lighter, and nothing here needs
  layout - layout is proven in the browser by measurement (docs/architecture/16-ui-design-system.md).
*/
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  test: {
    environment: 'happy-dom',
    include: ['tests/unit/**/*.spec.ts'],
    restoreMocks: true,
  },
})
