import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import App from './App.vue'
import { router } from './router'
import './design-system/tokens.css'

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(VueQueryPlugin, {
  queryClientConfig: {
    defaultOptions: {
      queries: {
        // Dashboard aggregates take seconds against 126M rows. Refetching them
        // every time the window regains focus would make the app feel slower
        // without making it fresher - the source data changes once a day.
        refetchOnWindowFocus: false,
        retry: 1,
        staleTime: 60_000,
      },
    },
  },
})

app.mount('#app')
